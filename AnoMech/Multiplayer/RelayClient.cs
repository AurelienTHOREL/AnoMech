using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnoMech.Core;

namespace AnoMech.Multiplayer;

// The relay rejected the connection with a close frame right after the upgrade (see
// ReadGreetingAsync), almost always "session not found" after a relay restart. Retrying the
// same code can never succeed, so the reconnect loop gives up instead of backing off forever.
internal sealed class RelaySessionRejectedException(string reason) : Exception(reason);

// ClientWebSocket wrapper for AnoMech.Relay (Relay/README.md), which only forwards opaque
// frames. The WebSocket message type doubles as the compression flag: Text = raw JSON,
// Binary = Brotli (from CompressionThresholdBytes up; below that Brotli's framing costs more
// than it saves).
public sealed class RelayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int CompressionThresholdBytes = 256;

    // Replaced per fallback attempt: a ClientWebSocket can only ConnectAsync once.
    private ClientWebSocket socket = new();
    private readonly CancellationTokenSource cts = new();
    // Two queues so a large WorldSnapshot can't hog the single send slot ahead of small urgent
    // messages; bulk is drained only while the priority queue is empty.
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource Completion)> sendQueue =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, TaskCompletionSource)>();
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource Completion)> bulkSendQueue =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, TaskCompletionSource)>();
    private bool disposed;

    // The relay is untrusted; 2 MB is ~100x the largest real message.
    private const int MaxIncomingMessageBytes = 2 * 1024 * 1024;
    private const int MaxIncomingFragments = 4096;

    // (message, isFromHost, relay-assigned sender connection id or 0) -- see IHostOnlyMessage.
    public event Action<MpMessage, bool, uint>? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => socket.State == WebSocketState.Open;

    // The scheme actually dialed; the relay can't say (wss:// terminates in a reverse proxy).
    public bool IsEncrypted { get; private set; }

    // Distinct from a ws:// the user typed on purpose.
    public bool FellBackToUnencrypted { get; private set; }

    // From the relay's greeting; a missing entry just means "not supported".
    public IReadOnlySet<string> RelayCapabilities { get; private set; } = new HashSet<string>();
    public bool HasRelayCapability(string name) => RelayCapabilities.Contains(name);
    public bool SupportsCompression => HasRelayCapability("binaryCompression");
    public bool SupportsSenderIdentity => HasRelayCapability("senderIdentity");

    // Each connection's usage is counted from zero: the caps it is measured against are
    // per-connection too.
    public Task ConnectAsync(string relayUrl, string sessionCode, string? accessToken = null)
    {
        RelayStats.Reset();
        return ConnectCoreAsync(relayUrl, $"session/{Uri.EscapeDataString(sessionCode)}", accessToken);
    }

    // Only the relay can guarantee a collision-free session code.
    public Task<string?> ConnectAndHostAsync(string relayUrl, string? accessToken = null)
    {
        RelayStats.Reset();
        return ConnectCoreAsync(relayUrl, "host", accessToken);
    }

    // A bare host tries wss:// then falls back to ws://; the only way to learn whether a server
    // speaks TLS is to try. Default ports match Relay/README.md (443 behind Caddy, 7890
    // direct). An explicit scheme is tried once, literally.
    private static IReadOnlyList<Uri> ResolveCandidateUris(string relayUrl, string path)
    {
        var trimmed = relayUrl.Trim();
        var explicitScheme = trimmed.IndexOf("://", StringComparison.Ordinal) is var idx && idx > 0
            ? trimmed[..idx].ToLowerInvariant()
            : null;
        if (explicitScheme is not null)
        {
            var mapped = explicitScheme switch { "https" => "wss", "http" => "ws", _ => explicitScheme };
            return [BuildUri($"{mapped}://{trimmed[(idx + 3)..]}", path)];
        }

        var (host, explicitPort) = SplitHostPort(trimmed);
        return
        [
            BuildUri($"wss://{host}:{explicitPort ?? 443}", path),
            BuildUri($"ws://{host}:{explicitPort ?? 7890}", path),
        ];
    }

    // A probe scheme with no default port: "ws://host" would resolve an unspecified port to 80.
    private static (string Host, int? Port) SplitHostPort(string hostAndOptionalPort)
    {
        if (!Uri.TryCreate($"anomech-probe://{hostAndOptionalPort}", UriKind.Absolute, out var probe))
            return (hostAndOptionalPort, null);
        return (probe.Host, probe.Port < 0 ? null : probe.Port);
    }

    private static Uri BuildUri(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");

    // Same host/port resolution mapped to https/http; /info lives on the same host:port as the
    // WS endpoint.
    private static IReadOnlyList<Uri> ResolveInfoCandidateUris(string relayUrl)
    {
        var trimmed = relayUrl.Trim();
        var explicitScheme = trimmed.IndexOf("://", StringComparison.Ordinal) is var idx && idx > 0
            ? trimmed[..idx].ToLowerInvariant()
            : null;
        if (explicitScheme is not null)
        {
            var mapped = explicitScheme switch { "wss" => "https", "ws" => "http", _ => explicitScheme };
            return [BuildUri($"{mapped}://{trimmed[(idx + 3)..]}", "info")];
        }
        var (host, explicitPort) = SplitHostPort(trimmed);
        return
        [
            BuildUri($"https://{host}:{explicitPort ?? 443}", "info"),
            BuildUri($"http://{host}:{explicitPort ?? 7890}", "info"),
        ];
    }

    private sealed record RelayInfo(int RelayVersion, string[]? Capabilities, bool RequiresToken);

    private static readonly HttpClient InfoHttpClient = new() { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 64 * 1024 };

    // Plain HTTP, so the UI can learn whether a relay needs a token before the user has one.
    // Null = unreachable or an old relay without /info; callers assume no token needed.
    public static async Task<(int RelayVersion, bool RequiresToken)?> FetchInfoAsync(string relayUrl, CancellationToken ct = default)
    {
        foreach (var uri in ResolveInfoCandidateUris(relayUrl))
        {
            try
            {
                var json = await InfoHttpClient.GetStringAsync(uri, ct).ConfigureAwait(false);
                var info = JsonSerializer.Deserialize<RelayInfo>(json, JsonOptions);
                if (info != null) return (info.RelayVersion, info.RequiresToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                DiagnosticLog.Info($"[RelayClient] /info fetch from {uri} failed: {e.Message}");
            }
        }
        return null;
    }

    private async Task<string?> ConnectCoreAsync(string relayUrl, string path, string? accessToken)
    {
        var candidates = ResolveCandidateUris(relayUrl, path);
        // A password must never go over ws://, whether from the fallback or typed explicitly
        // (the relay enforces the same; see Program.cs IsRequestEncrypted).
        if (!string.IsNullOrEmpty(accessToken))
        {
            var encryptedOnly = candidates.Where(u => u.Scheme == "wss").ToList();
            if (encryptedOnly.Count == 0)
            {
                var reason = "A relay password is set, but this connection isn't encrypted (wss://) -- refusing to send it in plaintext.";
                DiagnosticLog.Warn($"[RelayClient] {reason}");
                Disconnected?.Invoke(new InvalidOperationException(reason));
                return null;
            }
            candidates = encryptedOnly;
        }
        Exception? lastFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i > 0)
            {
                socket.Dispose();
                socket = new ClientWebSocket();
            }
            // Options only apply before ConnectAsync, so they are set per instance.
            if (!string.IsNullOrEmpty(accessToken))
                socket.Options.SetRequestHeader("X-AnoMech-Relay-Token", accessToken);
            var uri = candidates[i];
            IsEncrypted = uri.Scheme == "wss";
            try
            {
                await socket.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
                DiagnosticLog.Info($"[RelayClient] Connected to {uri}.");
                if (i > 0)
                {
                    FellBackToUnencrypted = true;
                    DiagnosticLog.Warn($"[RelayClient] {candidates[0]} wasn't reachable -- fell back to {uri}, unencrypted.");
                }
                var assignedCode = await ReadGreetingAsync().ConfigureAwait(false);
                _ = Task.Run(ReceiveLoopAsync);
                _ = Task.Run(SendLoopAsync);
                return assignedCode;
            }
            catch (Exception e)
            {
                lastFailure = e;
                DiagnosticLog.Info($"[RelayClient] {uri} failed: {e.Message}"
                    + (i < candidates.Count - 1 ? " -- trying the next candidate." : ""));
            }
        }
        // Callers are fire-and-forget; without Disconnected a failed handshake would only be
        // an unobserved Task exception.
        DiagnosticLog.Warn($"[RelayClient] Connect failed: {lastFailure?.Message}");
        Disconnected?.Invoke(lastFailure);
        return null;
    }

    private sealed record RelayGreeting(int RelayVersion, string[]? Capabilities, string? SessionCode);

    private const int GreetingTimeoutMs = 5000;

    // The greeting is not an MpMessage (the relay stays protocol-agnostic) and is consumed once
    // before ReceiveLoopAsync starts. An old relay that sends none times out to "no capabilities".
    private async Task<string?> ReadGreetingAsync()
    {
        var buffer = new byte[1024];
        WebSocketReceiveResult result;
        try
        {
            var receiveTask = socket.ReceiveAsync(buffer, cts.Token);
            if (await Task.WhenAny(receiveTask, Task.Delay(GreetingTimeoutMs, cts.Token)).ConfigureAwait(false) != receiveTask)
            {
                DiagnosticLog.Warn("[RelayClient] No greeting from the relay within timeout -- assuming an old relay with no advertised capabilities.");
                return null;
            }
            result = await receiveTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Failed to read the relay's greeting: {e.Message} -- assuming an old relay with no advertised capabilities.");
            return null;
        }

        // The relay accepts the WS upgrade before validating the session code, so a rejected
        // /session/<code> or /host arrives here as a close frame. Surfaced distinctly so the
        // reconnect loop can tell "give up" from "keep retrying".
        if (result.MessageType == WebSocketMessageType.Close)
            throw new RelaySessionRejectedException(socket.CloseStatusDescription ?? "the relay closed the connection");

        try
        {
            var greeting = JsonSerializer.Deserialize<RelayGreeting>(buffer.AsSpan(0, result.Count), JsonOptions);
            if (greeting is null) return null;
            RelayCapabilities = greeting.Capabilities is { } caps ? new HashSet<string>(caps) : new HashSet<string>();
            DiagnosticLog.Info($"[RelayClient] Relay version {greeting.RelayVersion}, capabilities: [{string.Join(", ", RelayCapabilities)}].");
            return greeting.SessionCode;
        }
        catch (JsonException e)
        {
            DiagnosticLog.Warn($"[RelayClient] Failed to read the relay's greeting: {e.Message} -- assuming an old relay with no advertised capabilities.");
            return null;
        }
    }

    public async Task SendAsync(MpMessage message)
    {
        if (!IsConnected) return;
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        var (bytes, type) = SupportsCompression && jsonBytes.Length >= CompressionThresholdBytes
            ? (Compress(jsonBytes), WebSocketMessageType.Binary)
            : (jsonBytes, WebSocketMessageType.Text);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = message is WorldSnapshotMessage ? bulkSendQueue : sendQueue;
        if (!queue.Writer.TryWrite((bytes, type, completion))) return;
        await completion.Task.ConfigureAwait(false);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
            brotli.Write(data, 0, data.Length);
        return output.ToArray();
    }

    // CopyTo has no output limit, so a compression bomb could exhaust the game's own memory.
    // ~100x the largest real snapshot.
    private const int MaxDecompressedBytes = 8 * 1024 * 1024;

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = brotli.Read(chunk, 0, chunk.Length)) > 0)
        {
            output.Write(chunk, 0, read);
            if (output.Length > MaxDecompressedBytes)
                throw new InvalidDataException($"Decompressed message exceeded {MaxDecompressedBytes} bytes.");
        }
        return output.ToArray();
    }

    // ClientWebSocket allows one outstanding send with no timeout; aborting also faults the
    // receive loop's pending read, which triggers Disconnected/reconnect.
    private const int SendTimeoutMs = 10_000;

    private async Task SendLoopAsync()
    {
        try
        {
            while (true)
            {
                if (sendQueue.Reader.TryRead(out var entry) || bulkSendQueue.Reader.TryRead(out entry))
                {
                    await SendOneAsync(entry.Bytes, entry.Type, entry.Completion).ConfigureAwait(false);
                    continue;
                }
                var prioritySignal = sendQueue.Reader.WaitToReadAsync(cts.Token).AsTask();
                var bulkSignal = bulkSendQueue.Reader.WaitToReadAsync(cts.Token).AsTask();
                await Task.WhenAny(prioritySignal, bulkSignal).ConfigureAwait(false);
                if (sendQueue.Reader.Completion.IsCompleted && bulkSendQueue.Reader.Completion.IsCompleted) return;
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by Dispose()
        }
    }

    private async Task SendOneAsync(byte[] bytes, WebSocketMessageType type, TaskCompletionSource completion)
    {
        try
        {
            var sendTask = socket.SendAsync(bytes, type, true, cts.Token);
            if (await Task.WhenAny(sendTask, Task.Delay(SendTimeoutMs, cts.Token)).ConfigureAwait(false) != sendTask)
            {
                DiagnosticLog.Warn($"[RelayClient] Send stuck for over {SendTimeoutMs / 1000}s -- treating the connection as dead.");
                try { socket.Abort(); } catch { /* best-effort */ }
                completion.SetException(new TimeoutException($"Send timed out after {SendTimeoutMs}ms."));
                return;
            }
            await sendTask.ConfigureAwait(false);
            RelayStats.RecordSent(bytes.Length);
            completion.SetResult();
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Send failed: {e.Message}");
            completion.SetException(e);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        Exception? failure = null;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                var fragments = 0;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DiagnosticLog.Info($"[RelayClient] Received close frame: status={result.CloseStatus}, description=\"{result.CloseStatusDescription}\".");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > MaxIncomingMessageBytes || ++fragments > MaxIncomingFragments)
                    {
                        failure = new InvalidDataException(
                            $"Relay sent a message over {MaxIncomingMessageBytes / (1024 * 1024)} MB or {MaxIncomingFragments} fragments -- dropping the connection.");
                        DiagnosticLog.Warn($"[RelayClient] {failure.Message}");
                        try { socket.Abort(); } catch { /* best-effort */ }
                        return;
                    }
                } while (!result.EndOfMessage);
                RelayStats.RecordReceived(ms.Length);

                MpMessage? message;
                var isFromHost = true;
                uint senderConnectionId = 0;
                try
                {
                    var raw = ms.ToArray();
                    // Relay-written prefix (Relay/Program.cs BroadcastAsync): host-tag byte,
                    // then the sender's connection id.
                    if (SupportsSenderIdentity)
                    {
                        if (raw.Length < 5) throw new InvalidDataException("frame shorter than the senderIdentity prefix.");
                        isFromHost = raw[0] == 1;
                        senderConnectionId = BitConverter.ToUInt32(raw, 1);
                        raw = raw[5..];
                    }
                    if (result.MessageType == WebSocketMessageType.Binary) raw = Decompress(raw);
                    message = JsonSerializer.Deserialize<MpMessage>(raw, JsonOptions);
                }
                // One bad message shouldn't take the connection down.
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    DiagnosticLog.Warn($"[RelayClient] Malformed message dropped: {e.Message}");
                    continue;
                }
                if (message != null) MessageReceived?.Invoke(message, isFromHost, senderConnectionId);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by Dispose()
        }
        catch (Exception e)
        {
            failure = e;
            var code = e is WebSocketException wse ? $" (WebSocketErrorCode={wse.WebSocketErrorCode})" : "";
            DiagnosticLog.Warn($"[RelayClient] Receive loop faulted: {e}{code}");
        }
        finally
        {
            DiagnosticLog.Debug($"[RelayClient] Receive loop exiting -- final socket state {socket.State}.");
            Disconnected?.Invoke(failure);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DiagnosticLog.Debug($"[RelayClient] Dispose() -- socket state was {socket.State}.");
        // Complete before Cancel, so an enqueued SendAsync caller sees a failed send instead
        // of hanging.
        sendQueue.Writer.TryComplete();
        bulkSendQueue.Writer.TryComplete();
        while (sendQueue.Reader.TryRead(out var pending))
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(RelayClient)));
        while (bulkSendQueue.Reader.TryRead(out var pending))
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(RelayClient)));
        cts.Cancel();
        try { socket.Abort(); } catch { /* best-effort */ }
        socket.Dispose();
        cts.Dispose();
    }
}
