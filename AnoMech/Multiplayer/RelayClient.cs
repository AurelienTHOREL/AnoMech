using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Core;
using AnoMech.Network;

namespace AnoMech.Multiplayer;

// The relay refused this client or ended the room. Retrying the same code can't help.
internal sealed class RelaySessionRejectedException(string reason) : Exception(reason);

public sealed class RelayClient(string peerSecret) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private static readonly HttpMessageInvoker WebSocketHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    private const int MaxInboundFrameBytes = 2 * 1024 * 1024;
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource cts = new();
    private readonly object sendLock = new();
    private readonly SemaphoreSlim sendSignal = new(0);
    private readonly Queue<PendingSend> priority = new();
    private readonly Queue<PendingSend> bulk = new();
    private int queuedBytes;
    private volatile bool disposed;
    private bool host;
    private volatile bool ready;
    private sealed record PendingSend(byte[] Bytes, int RawBytes, WebSocketMessageType Type, TaskCompletionSource Completion);
    private sealed record RelayNotice(string? T, Guid[]? Removed);

    public event Action<MpMessage, bool, uint, Guid, int>? MessageReceived;
    // A message that failed to decode, validate or deserialize, from this relay-authenticated
    // sender. Dropped rather than fatal: one bad message must not take the connection down.
    public event Action<Guid, bool, string>? MessageRejected;
    // Relay-side removals the host didn't ask for by id, e.g. others on a banned address.
    public event Action<IReadOnlyList<Guid>>? PeersRemoved;
    public event Action<Exception?>? Disconnected;
    public bool IsConnected => ready && socket.State == WebSocketState.Open;
    // Stays true after the connection drops, so a lost session can be told from one that
    // never reached the relay at all.
    public bool HasConnected { get; private set; }
    public bool IsEncrypted { get; private set; }
    public IReadOnlySet<string> RelayCapabilities { get; private set; } = new HashSet<string>();
    public bool SupportsCompression => RelayCapabilities.Contains("binaryCompression");
    public bool SupportsSenderIdentity => RelayCapabilities.Contains("authenticatedIdentity");

    public Task ConnectAsync(string relayUrl, string sessionCode, string? accessToken = null)
        => ConnectCoreAsync(relayUrl, $"session/{Uri.EscapeDataString(sessionCode)}", accessToken);

    public Task<string?> ConnectAndHostAsync(string relayUrl, string? accessToken = null)
        => ConnectCoreAsync(relayUrl, "host", accessToken);

    private sealed record RelayInfo(int RelayVersion, string[]? Capabilities, bool RequiresToken);
    private sealed record RelayGreeting(int RelayVersion, string[]? Capabilities, string? SessionCode, Guid PeerId);
    private static readonly HttpClient InfoHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 64 * 1024 };

    public static async Task<(int RelayVersion, bool RequiresToken)?> FetchInfoAsync(string relayUrl, CancellationToken ct = default)
    {
        try
        {
            var endpoint = RelayWire.Endpoint(relayUrl, "info");
            var uri = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" }.Uri;
            var json = await InfoHttpClient.GetStringAsync(uri, ct).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<RelayInfo>(json, JsonOptions);
            return info is null ? null : (info.RelayVersion, info.RequiresToken);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private async Task<string?> ConnectCoreAsync(string relayUrl, string path, string? accessToken)
    {
        RelayStats.Reset();
        try
        {
            var uri = RelayWire.Endpoint(relayUrl, path);
            IsEncrypted = uri.Scheme == "wss";
            host = path == "host";
            if (!IsEncrypted && !string.IsNullOrEmpty(accessToken))
                throw new RelaySessionRejectedException("Use wss:// to send a relay password.");
            if (!string.IsNullOrEmpty(accessToken)) socket.Options.SetRequestHeader("X-AnoMech-Relay-Token", accessToken);
            socket.Options.SetRequestHeader("X-AnoMech-Peer-Secret", peerSecret);
            socket.Options.SetRequestHeader("X-AnoMech-Protocol", RelayWire.Version.ToString());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, WebSocketHttp, timeout.Token).ConfigureAwait(false);
            // Only a refusal in the greeting itself is terminal. A slow or dropped greeting is an
            // ordinary network failure, and a reconnect has to be free to try again.
            var code = await ReadGreetingAsync().ConfigureAwait(false);
            ready = true;
            HasConnected = true;
            DiagnosticLog.Info($"[RelayClient] Connected to {uri}{(code is null ? "" : $" and assigned session {code}")}.");
            _ = Task.Run(ReceiveLoopAsync);
            _ = Task.Run(SendLoopAsync);
            return code;
        }
        catch (Exception e)
        {
            if (!cts.IsCancellationRequested)
                DiagnosticLog.Warn($"[RelayClient] Connect to {relayUrl} failed: {e.GetType().Name}: {e.Message}");
            socket.Abort();
            Disconnected?.Invoke(e);
            return null;
        }
    }

    private async Task<string?> ReadGreetingAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var message = new MemoryStream();
        var buffer = new byte[1024];
        WebSocketReceiveResult result;
        var fragments = 0;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new RelaySessionRejectedException(result.CloseStatusDescription ?? "Room closed.");
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 4096 || ++fragments > 16)
                throw new RelaySessionRejectedException("Invalid relay greeting.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        RelayGreeting? greeting;
        try { greeting = JsonSerializer.Deserialize<RelayGreeting>(message.ToArray(), JsonOptions); }
        catch (JsonException) { throw new RelaySessionRejectedException("Invalid relay greeting."); }
        if (greeting is null) throw new RelaySessionRejectedException("Missing relay greeting.");
        RelayCapabilities = new HashSet<string>(greeting.Capabilities ?? []);
        if (greeting.RelayVersion != RelayWire.Version || !SupportsCompression || !SupportsSenderIdentity
            || !RelayCapabilities.Contains("roomModeration") || greeting.PeerId != RelayWire.PeerId(peerSecret)
            || (host && string.IsNullOrEmpty(greeting.SessionCode)))
            throw new RelaySessionRejectedException("This relay doesn't match your AnoMech version -- update the relay.");
        return greeting.SessionCode;
    }

    public Task SendAsync(MpMessage message)
    {
        byte[] raw;
        try { raw = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions); }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Couldn't serialize {message.GetType().Name} -- not sent: {e.Message}");
            return Task.CompletedTask;
        }
        return QueueSend(raw, message is WorldSnapshotMessage, compress: true);
    }

    // Always uncompressed: the relay reads this one body, and it never decompresses.
    internal Task ModerateAsync(string operation, Guid peerId)
        => QueueSend(JsonSerializer.SerializeToUtf8Bytes(new { t = RelayWire.ControlType, Operation = operation, PeerId = peerId }), false, compress: false);

    private Task QueueSend(byte[] raw, bool isBulk, bool compress)
    {
        if (!IsConnected) return Task.CompletedTask;
        if (raw.Length > RelayWire.MaxMessageBytes)
        {
            DiagnosticLog.Warn($"[RelayClient] Dropped an outgoing message of {raw.Length} bytes, over the {RelayWire.MaxMessageBytes}-byte limit.");
            return Task.CompletedTask;
        }
        var bytes = raw;
        var type = WebSocketMessageType.Text;
        if (compress && raw.Length >= 256)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, true)) brotli.Write(raw);
            bytes = output.ToArray();
            type = WebSocketMessageType.Binary;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sendLock)
        {
            if (disposed) return Task.CompletedTask;
            if (priority.Count + bulk.Count >= 1024 || queuedBytes + bytes.Length > RelayWire.MaxQueuedBytes)
            {
                DiagnosticLog.Warn($"[RelayClient] Send queue full ({priority.Count + bulk.Count} messages, {queuedBytes} bytes) -- the connection has stalled; dropping it.");
                socket.Abort();
                return Task.CompletedTask;
            }
            queuedBytes += bytes.Length;
            (isBulk ? bulk : priority).Enqueue(new PendingSend(bytes, raw.Length, type, completion));
            sendSignal.Release();
        }
        return completion.Task;
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await sendSignal.WaitAsync(cts.Token).ConfigureAwait(false);
                PendingSend entry;
                lock (sendLock)
                {
                    if (!priority.TryDequeue(out entry!) && !bulk.TryDequeue(out entry!)) continue;
                    queuedBytes -= entry.Bytes.Length;
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.SendAsync(entry.Bytes, entry.Type, true, timeout.Token).ConfigureAwait(false);
                    RelayStats.RecordSent(entry.RawBytes, entry.Bytes.Length);
                }
                finally { entry.Completion.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Send failed ({e.GetType().Name}: {e.Message}) -- dropping the connection.");
            socket.Abort();
        }
        finally { CompletePending(); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        // Per sender: the host hears from every peer at once, and one budget shared between
        // them let a few peers inside their own relay limits exhaust it together.
        var budgets = new Dictionary<Guid, TrafficBudget>();
        long overBudget = 0;
        Exception? failure = null;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                WebSocketReceiveResult result;
                var fragments = 0;
                do
                {
                    result = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (fragments == 0) timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new RelaySessionRejectedException(result.CloseStatusDescription ?? "Room closed.");
                    if (message.Length + result.Count > MaxInboundFrameBytes || ++fragments > 4096)
                        throw new InvalidDataException("Relay message too large.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                var frame = message.ToArray();
                // The relay writes every prefix, so a bad one means the relay itself is broken.
                if (result.MessageType != WebSocketMessageType.Binary || frame.Length < RelayWire.PrefixBytes || frame[0] > 1 || frame[21] > 1)
                    throw new InvalidDataException("Missing relay identity.");
                var fromHost = frame[0] == 1;
                var connectionId = BitConverter.ToUInt32(frame, 1);
                var sender = new Guid(frame.AsSpan(5, 16));
                if (connectionId == 0 && sender == Guid.Empty)
                {
                    ReadNotice(frame);
                    continue;
                }
                if (connectionId == 0 || sender == Guid.Empty) throw new InvalidDataException("Invalid relay sender.");
                if (!host && !fromHost) continue;
                if (!budgets.TryGetValue(sender, out var budget))
                {
                    if (budgets.Count >= 64) budgets.Clear();
                    // The host is trusted with the whole simulation already; a peer's budget is
                    // what keeps one sender from flooding the host.
                    budget = fromHost
                        ? new TrafficBudget(int.MaxValue, long.MaxValue)
                        : new TrafficBudget(RelayWire.PeerMessagesPerSecond, RelayWire.PeerBytesPerSecond);
                    budgets[sender] = budget;
                }
                byte[] raw;
                MpMessage? parsed;
                try
                {
                    raw = RelayWire.Decode(frame[RelayWire.PrefixBytes..], frame[21] == 1, budget,
                                           fromHost ? RelayWire.MaxMessageBytes : RelayWire.MaxPeerMessageBytes);
                    RelayWire.Validate(raw, fromHost, sender);
                    parsed = JsonSerializer.Deserialize<MpMessage>(raw, JsonOptions);
                }
                catch (TrafficLimitException)
                {
                    if (overBudget++ % 1000 == 0)
                        DiagnosticLog.Warn($"[RelayClient] Dropping traffic over the per-sender budget from {sender} ({overBudget} so far).");
                    continue;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    MessageRejected?.Invoke(sender, fromHost, e.Message);
                    continue;
                }
                RelayStats.RecordReceived(raw.Length);
                if (parsed != null) MessageReceived?.Invoke(parsed, fromHost, connectionId, sender, raw.Length);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (RelaySessionRejectedException e)
        {
            failure = e;
            DiagnosticLog.Info($"[RelayClient] The relay closed the connection: {e.Message}");
            socket.Abort();
        }
        catch (Exception e)
        {
            failure = e;
            DiagnosticLog.Warn($"[RelayClient] Receive failed ({e.GetType().Name}: {e.Message}) -- dropping the connection.");
            socket.Abort();
        }
        finally { ready = false; Disconnected?.Invoke(failure); }
    }

    private void ReadNotice(byte[] frame)
    {
        try
        {
            var notice = JsonSerializer.Deserialize<RelayNotice>(frame.AsSpan(RelayWire.PrefixBytes), JsonOptions);
            if (notice?.T == RelayWire.NoticeType && notice.Removed is { Length: > 0 and <= 64 } removed)
                PeersRemoved?.Invoke(removed);
        }
        catch (JsonException e)
        {
            DiagnosticLog.Warn($"[RelayClient] Ignored an unreadable relay notice: {e.Message}");
        }
    }

    private void CompletePending()
    {
        lock (sendLock)
        {
            while (priority.TryDequeue(out var entry)) entry.Completion.TrySetResult();
            while (bulk.TryDequeue(out var entry)) entry.Completion.TrySetResult();
            queuedBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (sendLock)
        {
            if (disposed) return;
            disposed = true;
            ready = false;
            cts.Cancel();
            socket.Abort();
            socket.Dispose();
            CompletePending();
        }
    }
}
