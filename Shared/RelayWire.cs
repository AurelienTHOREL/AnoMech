using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AnoMech.Network;

internal static class RelayWire
{
    public const int Version = 1;
    public const int PrefixBytes = 22;

    // Decoded size caps, applied by the receiving client. The relay never decodes: it forwards
    // bodies untouched, so every limit on message content is the receiver's to enforce.
    public const int MaxMessageBytes = 8 * 1024 * 1024;       // from the host
    public const int MaxPeerMessageBytes = 64 * 1024;         // from a peer; none is near this
    public const int MaxQueuedBytes = 32 * 1024 * 1024;

    // The relay's per-connection defaults, counted on the wire.
    public const int MessagesPerSecond = 20_000;
    public const int BytesPerSecond = 40 * 1024 * 1024;

    // What the host accepts from any one peer, decoded. A peer sends a pose per frame and little
    // else, so these sit far above real use while keeping one sender from swamping the host.
    public const int PeerMessagesPerSecond = 3_000;
    public const int PeerBytesPerSecond = 1024 * 1024;

    // Each VFX list one character carries in one snapshot. The host logs anything past it.
    public const int MaxVfxPerEntity = 1600;

    // relayControl is the only body the relay reads, so it has to arrive uncompressed and small.
    public const int MaxControlBytes = 1024;
    public const string ControlType = "relayControl";
    public const string NoticeType = "relayNotice";

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static Guid PeerId(string secret)
    {
        if (secret.Length != 64) throw new InvalidDataException("Invalid peer credential.");
        return new Guid(SHA256.HashData(Convert.FromHexString(secret)).AsSpan(0, 16));
    }

    public static Uri Endpoint(string address, string path)
    {
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "wss://" + text;
        var uri = new Uri(text, UriKind.Absolute);
        var scheme = uri.Scheme switch
        {
            "https" or "wss" => "wss",
            "http" or "ws" => "ws",
            _ => throw new ArgumentException("Use a wss:// or ws:// relay address."),
        };
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Relay addresses cannot contain credentials, a query, or a fragment.");
        return new UriBuilder(uri) { Scheme = scheme, Path = uri.AbsolutePath.TrimEnd('/') + "/" + path }.Uri;
    }

    public static string Origin(string address) => Endpoint(address, "").GetLeftPart(UriPartial.Authority);

    // The relay writes this prefix on every frame it forwards, so a client can trust it. A
    // connection id of 0 with an empty sender marks a notice from the relay itself.
    public static byte[] Envelope(bool fromHost, uint connectionId, Guid sender, bool compressed, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[PrefixBytes + payload.Length];
        frame[0] = (byte)(fromHost ? 1 : 0);
        BitConverter.TryWriteBytes(frame.AsSpan(1, 4), connectionId);
        sender.TryWriteBytes(frame.AsSpan(5, 16));
        frame[21] = (byte)(compressed ? 1 : 0);
        payload.CopyTo(frame.AsSpan(PrefixBytes));
        return frame;
    }

    // Reads only the first property, so the relay's cost is the same however large the frame.
    public static bool IsControl(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxControlBytes) return false;
        try
        {
            var reader = new Utf8JsonReader(json);
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject
                && reader.Read() && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("t")
                && reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ControlType);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    public static byte[] Decode(byte[] payload, bool compressed, TrafficBudget budget, int maxBytes = MaxMessageBytes)
    {
        budget.Message();
        if (!compressed)
        {
            if (payload.Length > maxBytes) throw new InvalidDataException("Message too large.");
            budget.Bytes(payload.Length);
            return payload;
        }
        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = brotli.Read(chunk, 0, chunk.Length)) > 0)
        {
            budget.Bytes(read);
            if (output.Length + read > maxBytes) throw new InvalidDataException("Message too large.");
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    // Name is the property the container is the value of: for an array element that is the
    // array's own key, not whichever key was last read inside a sibling object.
    private sealed class Container(bool array, int limit, string name)
    {
        public readonly bool Array = array;
        public readonly int Limit = limit;
        public readonly string Name = name;
        public int Count;
        public readonly HashSet<string> Properties = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string Validate(ReadOnlySpan<byte> json, bool fromHost, Guid sender)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject
            || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("t")
            || !reader.Read() || reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 64)
            throw new InvalidDataException("Missing message type.");
        var type = reader.GetString()!;
        var peerMessage = type is "hello" or "claim" or "release" or "pose" or "pong" or "startCheckResponse"
            or "startAbort" or "sessionEnded" or "resetRequest" or "leaveRequest" or "selfMitigation"
            or "peerAppliedEnemyStatus" or "peerAppliedRoleStatus";
        if (!fromHost && !peerMessage) throw new InvalidDataException("Host message sent by a peer.");
        var stack = new Stack<Container>();
        var root = new Container(false, 256, "");
        root.Properties.Add("t");
        stack.Push(root);
        string property = "";
        Guid? claimed = null;
        var tokens = 0;
        while (reader.Read())
        {
            if (++tokens > 250_000 || stack.Count == 0) throw new InvalidDataException("Message structure too large.");
            var parent = stack.Peek();
            var token = reader.TokenType;
            if (token == JsonTokenType.PropertyName)
            {
                if (reader.ValueSpan.Length > 256) throw new InvalidDataException("Property name too long.");
                property = reader.GetString()!;
                if (!parent.Properties.Add(property) || ++parent.Count > parent.Limit)
                    throw new InvalidDataException("Too many or duplicate properties.");
                continue;
            }
            if (token is JsonTokenType.EndArray or JsonTokenType.EndObject) { stack.Pop(); continue; }
            if (parent.Array && (++parent.Count > parent.Limit || token == JsonTokenType.Null))
                throw new InvalidDataException("Invalid collection.");
            var key = parent.Array ? parent.Name : property;
            if (token is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                var limit = parent.Array ? 256 : key.ToLowerInvariant() switch
                {
                    "roles" or "claimedby" => 8,
                    "names" or "builds" or "statuses" => 64,
                    "newvfx" or "persistentvfx" or "newlockonvfxids" => MaxVfxPerEntity,
                    "tethers" => 128,
                    _ => 256,
                };
                stack.Push(new Container(token == JsonTokenType.StartArray, limit, key));
            }
            else if (token == JsonTokenType.String)
            {
                var limit = key.Equals("ScenarioSettingsJson", StringComparison.OrdinalIgnoreCase) ? 24_576 : 2048;
                // The limit is on the text, not its JSON spelling: the serializer escapes every
                // non-ASCII character as \uXXXX, which a byte count of the raw span would charge 6x.
                if (reader.ValueSpan.Length > limit
                    && (!reader.ValueIsEscaped || Encoding.UTF8.GetByteCount(reader.GetString()!) > limit))
                    throw new InvalidDataException("String too long.");
                if (stack.Count == 1 && property.Equals("PeerId", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.TryGetGuid(out var id)) throw new InvalidDataException("Invalid identity.");
                    claimed = id;
                }
            }
            else if (token == JsonTokenType.Number && (!reader.TryGetDouble(out var number) || !double.IsFinite(number)))
                throw new InvalidDataException("Invalid number.");
        }
        if (stack.Count != 0 || (peerMessage && claimed != sender)) throw new InvalidDataException("Invalid sender identity.");
        return type;
    }
}

// Distinct from a malformed message: going over a rate says nothing about the message itself.
internal sealed class TrafficLimitException(string message) : Exception(message);

internal sealed class TrafficBudget(int messages = RelayWire.MessagesPerSecond, long bytes = RelayWire.BytesPerSecond)
{
    public int MessageLimit { get; set; } = messages;
    public long ByteLimit { get; set; } = bytes;
    private long window = Stopwatch.GetTimestamp();
    private int messageCount;
    private long byteCount;

    private void Advance()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - window < Stopwatch.Frequency) return;
        window = now;
        messageCount = 0;
        byteCount = 0;
    }

    public void Message()
    {
        Advance();
        if (++messageCount > MessageLimit) throw new TrafficLimitException("Message rate exceeded.");
    }

    public void Bytes(int count)
    {
        Advance();
        if ((byteCount += count) > ByteLimit) throw new TrafficLimitException("Uncompressed byte rate exceeded.");
    }
}
