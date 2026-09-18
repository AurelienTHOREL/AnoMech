using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnoMech.Multiplayer;
using AnoMech.Network;
using AnoMech.Relay;

internal static class SecurityTests
{
    private static readonly Type Relay = typeof(AnoMech.Relay.Program);
    private static int passed;
    private static object? Call(string name, params object?[] args)
        => Relay.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine($"PASS {name}");
        passed++;
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException or FormatException or TrafficLimitException)
        { Check(true, name); return; }
        throw new Exception($"Accepted invalid input: {name}");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Zip(byte[] bytes)
    {
        using var stream = new MemoryStream();
        using (var compressor = new BrotliStream(stream, CompressionLevel.Fastest, true)) compressor.Write(bytes);
        return stream.ToArray();
    }

    public static async Task<int> Main(string[] args)
    {
        try { await Run(args); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static async Task Run(string[] args)
    {
        if (args.FirstOrDefault() == "relay") { await (Task)Call("Main", (object)args[1..])!; return; }
        if (args.FirstOrDefault() == "relay-fixture") { await RelayFixture(int.Parse(args[1])); return; }
        var secret = RelayWire.NewSecret();
        var id = RelayWire.PeerId(secret);
        Check(id == RelayWire.PeerId(secret) && id != RelayWire.PeerId(RelayWire.NewSecret()), "private credential determines identity");
        Reject(() => RelayWire.PeerId("bad"), "malformed credential");
        var hello = Bytes($$"""{"t":"hello","PeerId":"{{id}}","DisplayName":"Test","Version":"1","Checksum":"test"}""");
        Check(RelayWire.Validate(hello, false, id) == "hello", "valid peer message");
        Reject(() => RelayWire.Validate(hello, false, Guid.NewGuid()), "forged peer identity");
        Reject(() => RelayWire.Validate(Bytes("{\"t\":\"snapshot\",\"Enemies\":[]}"), false, id), "peer host-only message");
        Reject(() => RelayWire.Validate(Bytes("{\"t\":\"ping\",\"T\":\"snapshot\"}"), true, id), "duplicate discriminator");
        Reject(() => RelayWire.Validate(Bytes("{\"t\":\"snapshot\",\"Enemies\":[null]}"), true, id), "null native collection entry");
        Reject(() => RelayWire.Validate(Bytes("{\"t\":\"snapshot\",\"Enemies\":[" + string.Join(',', Enumerable.Repeat("{}", 257)) + "]}"), true, id), "oversized native collection");
        Reject(() => RelayWire.Validate(Bytes("{\"t\":\"ping\",\"SentAtMs\":1e999}"), true, id), "nonfinite number");
        Reject(() => RelayWire.Validate(Bytes("{}"), true, id), "empty object");
        var accented = Bytes($$"""{"t":"hello","PeerId":"{{id}}","DisplayName":"{{string.Concat(Enumerable.Repeat("\\u00e9", 400))}}","Version":"1","Checksum":"test"}""");
        Check(RelayWire.Validate(accented, false, id) == "hello", "escaped text measured by its real length");
        Reject(() => RelayWire.Validate(Bytes("{\"t\":\"lobby\",\"A\":[{\"ScenarioSettingsJson\":\"\"},\"" + new string('x', 20_000) + "\"]}"), true, id),
            "array strings keep their own key's limit");
        Check(RelayWire.IsControl(Bytes("{\"t\":\"relayControl\",\"Operation\":\"kick\"}")) && !RelayWire.IsControl(Bytes("{\"t\":\"ping\"}"))
            && !RelayWire.IsControl(Bytes("not json")) && !RelayWire.IsControl(Bytes("{\"t\":\"relayControl\",\"X\":\"" + new string('x', 2000) + "\"}")),
            "relay reads only small control frames");
        var envelope = RelayWire.Envelope(true, 7, id, true, Bytes("x"));
        Check(envelope.Length == RelayWire.PrefixBytes + 1 && envelope[0] == 1 && BitConverter.ToUInt32(envelope, 1) == 7
            && new Guid(envelope.AsSpan(5, 16)) == id && envelope[21] == 1 && envelope[^1] == (byte)'x', "envelope layout");
        Reject(() => RelayWire.Decode(Zip(new byte[RelayWire.MaxPeerMessageBytes + 1]), true, new TrafficBudget(), RelayWire.MaxPeerMessageBytes),
            "peer decompression ceiling");
        var bomb = Zip(Bytes($$"""{"t":"hello","PeerId":"{{id}}","DisplayName":"{{new string('x', 1_000_000)}}"}"""));
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        Reject(() => RelayWire.Validate(RelayWire.Decode(bomb, true, new TrafficBudget()), false, id), "compressed oversized string before typed deserialization");
        Check(GC.GetAllocatedBytesForCurrentThread() - allocationStart < 8 * 1024 * 1024, "bounded allocation for compressed oversized string");
        Reject(() => RelayWire.Decode(Zip(new byte[RelayWire.MaxMessageBytes + 1]), true, new TrafficBudget()), "decompression ceiling");
        Reject(() => RelayWire.Decode(bomb, true, new TrafficBudget(bytes: 512)), "budget charges decompressed bytes");
        var traffic = new TrafficBudget(messages: 1);
        RelayWire.Decode(hello, false, traffic);
        Reject(() => RelayWire.Decode(hello, false, traffic), "message rate ceiling");
        Check(RelayWire.MessagesPerSecond == 20_000 && RelayWire.BytesPerSecond == 40 * 1024 * 1024, "quadrupled rate limits");
        var role = new RoleState(default, true, false, 0, 0, 0, 0, [], [], 10000, 10000);
        var roleMessage = new RolesSnapshotMessage(Enumerable.Repeat(role, 8).ToList());
        Check(RelayWire.Validate(JsonSerializer.SerializeToUtf8Bytes<MpMessage>(roleMessage), true, id) == "rolesSnapshot", "full eight-player snapshot accepted");
        var forms = typeof(MpMessage).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(MpMessage)) && !t.IsAbstract).ToArray();
        foreach (var form in forms)
        {
            var constructor = form.GetConstructors().Single();
            var message = (MpMessage)constructor.Invoke(constructor.GetParameters().Select(p => SampleValue(p.ParameterType, id)).ToArray());
            RelayWire.Validate(JsonSerializer.SerializeToUtf8Bytes(message), true, id);
        }
        Check(forms.Length > 40, $"all {forms.Length} protocol message forms accepted");
        var enemyCtor = typeof(EnemyState).GetConstructors().Single();
        var enemy = (EnemyState)enemyCtor.Invoke(enemyCtor.GetParameters().Select(p => SampleValue(p.ParameterType, id)).ToArray());
        enemy = enemy with { Statuses = Enumerable.Repeat(new EnemyStatusState(1, 1, 30), 64).ToArray() };
        var worldMessage = new WorldSnapshotMessage(Enumerable.Repeat(enemy, 256).ToList(), [], []);
        Check(RelayWire.Validate(JsonSerializer.SerializeToUtf8Bytes<MpMessage>(worldMessage), true, id) == "snapshot", "maximum enemy and status counts accepted");
        AttachedVfxState[] VfxList(int count) => Enumerable.Repeat(new AttachedVfxState("vfx/x.avfx", 1f), count).ToArray();
        Check(RelayWire.Validate(JsonSerializer.SerializeToUtf8Bytes<MpMessage>(new WorldSnapshotMessage([enemy with { NewVfx = VfxList(RelayWire.MaxVfxPerEntity) }], [], [])), true, id) == "snapshot",
            "full VFX list accepted");
        Reject(() => RelayWire.Validate(JsonSerializer.SerializeToUtf8Bytes<MpMessage>(new WorldSnapshotMessage([enemy with { NewVfx = VfxList(RelayWire.MaxVfxPerEntity + 1) }], [], [])), true, id),
            "over-long VFX list");

        Check(RelayWire.Endpoint("example.com:7890", "host").Scheme == "wss", "bare relay remains TLS");
        Check(RelayWire.Endpoint("ws://localhost:7890", "host").Scheme == "ws", "explicit local plaintext allowed");
        Check(RelayWire.Origin("https://EXAMPLE.com/a") == RelayWire.Origin("wss://example.com:443/b"), "canonical credential origin");
        Reject(() => RelayWire.Endpoint("wss://user:password@example.com", "host"), "URI embedded credentials");
        var config = new AnoMech.Configuration { RelayAccessToken = "secret", RelayTokenOrigin = RelayWire.Origin("wss://one.example") };
        Check(config.TokenForRelay("wss://one.example/path") == "secret"
            && config.TokenForRelay("wss://two.example") == ""
            && config.TokenForRelay("ws://one.example") == ""
            && config.TokenForRelay("wss://one.example:8443") == "", "password scoped to scheme host and port");
        config.RelayTokenOrigin = "";
        Check(config.TokenForRelay("wss://one.example") == "", "unscoped saved password discarded");
        var roomSecret = config.RoomSecret("wss://one.example", "ABCDEF");
        Check(roomSecret == config.RoomSecret("wss://one.example/path", "ABCDEF")
            && roomSecret != config.RoomSecret("wss://one.example", "ZZZZZZ")
            && roomSecret != config.RoomSecret("wss://two.example", "ABCDEF"), "room credential stable per room");
        for (var i = 0; i < 40; i++) config.RoomSecret("wss://one.example", $"ROOM{i:00}");
        Check(config.RoomCredentials.Count == 16, "room credentials bounded");

        var inbox = new SessionInbox<int>(2, 10);
        var oldSource = new object(); var newSource = new object();
        inbox.SetSource(oldSource);
        Check(inbox.TryEnqueue(oldSource, 1, 6) == InboxResult.Queued && inbox.TryEnqueue(oldSource, 2, 5) == InboxResult.Full, "inbox byte bound");
        inbox.SetSource(newSource);
        Check(!inbox.TryDequeue(out _) && inbox.TryEnqueue(oldSource, 2, 1) == InboxResult.StaleSource, "new session clears queue and rejects stale callbacks");
        Check(inbox.TryEnqueue(newSource, 3, 1) == InboxResult.Queued && inbox.TryEnqueue(newSource, 4, 1) == InboxResult.Queued
            && inbox.TryEnqueue(newSource, 5, 1) == InboxResult.Full, "inbox count bound");
        Check(inbox.IsBacklogged, "full inbox reports backlog");
        Check(inbox.TryDequeue(out var next) && next == 3, "inbox preserves wire order");
        inbox.SetSource(null);
        Check(inbox.TryEnqueue(newSource, 3, 1) == InboxResult.StaleSource, "ended session rejects callbacks");

        Check(AdminConsole.IsSafeAdminUri("http://localhost:7890") && AdminConsole.IsSafeAdminUri("http://[::1]:7890")
            && AdminConsole.IsSafeAdminUri("https://relay.example") && !AdminConsole.IsSafeAdminUri("http://relay.example")
            && !AdminConsole.IsSafeAdminUri("https://user:secret@relay.example"), "admin transport policy");
        var mappedA = (IPAddress)Call("AbuseKey", IPAddress.Parse("::ffff:192.0.2.1"))!;
        var mappedB = (IPAddress)Call("AbuseKey", IPAddress.Parse("::ffff:192.0.2.2"))!;
        Check(mappedA.Equals(IPAddress.Parse("192.0.2.1")) && !mappedA.Equals(mappedB), "mapped IPv4 abuse buckets stay independent");
        object?[] networkArgs = ["::ffff:192.0.2.0/120", null];
        Check((bool)Call("TryParseNetwork", networkArgs)! && ((IPNetwork)networkArgs[1]!).Contains(IPAddress.Parse("192.0.2.8")), "mapped proxy CIDR normalized");

        await SendSerialization();
        await LiveRelay();
        await GreetingTests();
        await RedirectTest();
        Console.WriteLine($"{passed} security checks passed on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}.");
    }

    private static object? SampleValue(Type type, Guid id)
    {
        if (type == typeof(Guid)) return id;
        if (type == typeof(string)) return "";
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsGenericType)
        {
            var concrete = type.IsInterface ? typeof(List<>).MakeGenericType(type.GenericTypeArguments) : type;
            return Activator.CreateInstance(concrete);
        }
        return null;
    }

    private static async Task SendSerialization()
    {
        var socket = new ProbeSocket();
        var peerType = Relay.GetNestedType("PeerConn", BindingFlags.NonPublic)!;
        var peer = Activator.CreateInstance(peerType, socket, 1u, IPAddress.Loopback, Guid.NewGuid())!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sends = Enumerable.Range(0, 20).Select(_ => (Task)Call("SendOneAsync", peer, new byte[] { 1 }, WebSocketMessageType.Text, timeout.Token)!);
        await Task.WhenAll(sends);
        Check(socket.PeakSends == 1 && socket.Sends == 20, "relay serializes concurrent writers");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static async Task LiveRelay()
    {
        var port = FreePort();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "relay-fixture", port.ToString() }) start.ArgumentList.Add(arg);
        start.Environment.Remove("ANOMECH_RELAY_TOKEN"); start.Environment.Remove("ANOMECH_RELAY_ADMIN_TOKEN");
        using var process = Process.Start(start)!;
        var firstLine = process.StandardOutput.ReadLineAsync();
        Task<string>? output = null;
        var errors = process.StandardError.ReadToEndAsync();
        var completed = false;
        try
        {
            Check((await firstLine.WaitAsync(TimeSpan.FromSeconds(5))) == "Listening on managed loopback", "relay transport listens on loopback");
            output = process.StandardOutput.ReadToEndAsync();
            var url = $"ws://localhost:{port}";
            var hostSecret = RelayWire.NewSecret();
            using var host = new RelayClient(hostSecret);
            host.Disconnected += e => { if (e is not null and not RelaySessionRejectedException) Console.WriteLine($"Host transport: {e}"); };
            var code = await host.ConnectAndHostAsync(url);
            Check(host.IsConnected && code != null, "modern client hosts room");
            var helloReceived = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.MessageReceived += (message, fromHost, connection, peerId, bytes) =>
            { if (message is HelloMessage) helloReceived.TrySetResult(peerId); };
            var peerSecret = RelayWire.NewSecret();
            using var peer = new RelayClient(peerSecret);
            peer.Disconnected += e => { if (e is not null and not RelaySessionRejectedException) Console.WriteLine($"Peer transport: {e}"); };
            await peer.ConnectAsync(url, code!);
            Check(peer.IsConnected, "modern client joins room");
            await peer.SendAsync(new HelloMessage(RelayWire.PeerId(peerSecret), "Test", "1", "test"));
            Check(await helloReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)) == RelayWire.PeerId(peerSecret), "relay authenticates hello sender");
            var pingReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var compressedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.MessageReceived += (message, fromHost, connection, peerId, bytes) =>
            {
                if (message is PingMessage) pingReceived.TrySetResult(fromHost && peerId == RelayWire.PeerId(hostSecret));
                if (message is AnnouncementMessage announcement) compressedReceived.TrySetResult(announcement.Text == new string('x', 512));
            };
            await host.SendAsync(new PingMessage(123));
            Check(await pingReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)), "host tag reaches client");
            await host.SendAsync(new AnnouncementMessage(new string('x', 512)));
            Check(await compressedReceived.Task.WaitAsync(TimeSpan.FromSeconds(5)), "compressed payload survives binary relay envelope");

            var rejections = new System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<string>>();
            host.MessageRejected += (sender, fromHost, reason) => { if (!fromHost && rejections.TryGetValue(sender, out var waiter)) waiter.TrySetResult(reason); };
            using var observer = await ConnectRaw(url, code!, RelayWire.NewSecret());
            async Task<bool> DroppedByHost(byte[] body, WebSocketMessageType type)
            {
                var secret = RelayWire.NewSecret();
                var waiter = rejections.GetOrAdd(RelayWire.PeerId(secret), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                using var attacker = await ConnectRaw(url, code!, secret);
                await SendRaw(attacker, body, type);
                await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return host.IsConnected && attacker.State == WebSocketState.Open;
            }
            Check(await DroppedByHost(Bytes($$"""{"t":"hello","PeerId":"{{RelayWire.PeerId(hostSecret)}}","DisplayName":"Fake"}"""), WebSocketMessageType.Text),
                "host identity spoof dropped by the host");
            Check(await DroppedByHost(Bytes("{\"t\":\"snapshot\",\"Enemies\":[],\"EventObjects\":[],\"Tethers\":[]}"), WebSocketMessageType.Text),
                "forged host snapshot dropped by the host");
            Check(await DroppedByHost(Bytes("{\"t\":\"pose\","), WebSocketMessageType.Text), "malformed peer message leaves the room up");
            Check(await DroppedByHost(Zip(Bytes("{\"t\":\"pose\",\"X\":\"" + new string('x', 1_000_000) + "\"}")), WebSocketMessageType.Binary),
                "relay forwards a peer's compression bomb unread and the host stops it");
            Check(await TryReadFrame(observer, TimeSpan.FromMilliseconds(500)) is null, "peer traffic reaches only the host");
            var replacementSecret = RelayWire.NewSecret();
            using var oldPeer = await ConnectRaw(url, code!, replacementSecret);
            using var replacement = await ConnectRaw(url, code!, replacementSecret);
            var replacementId = RelayWire.PeerId(replacementSecret);
            var resumedHello = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.MessageReceived += (message, fromHost, connection, sender, bytes) =>
            { if (message is HelloMessage && sender == replacementId) resumedHello.TrySetResult(true); };
            await SendRaw(replacement, $$"""{"t":"hello","PeerId":"{{replacementId}}","DisplayName":"Reconnected","Version":"1","Checksum":"test"}""");
            Check(await resumedHello.Task.WaitAsync(TimeSpan.FromSeconds(5)), "credential resumes on a new connection without waiting for silence");
            var kicked = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Disconnected += e => kicked.TrySetResult(e);
            var removedNotice = new TaskCompletionSource<IReadOnlyList<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.PeersRemoved += ids => removedNotice.TrySetResult(ids);
            await host.ModerateAsync("ban", RelayWire.PeerId(peerSecret));
            Check(await kicked.Task.WaitAsync(TimeSpan.FromSeconds(5)) is RelaySessionRejectedException, "ban forcibly disconnects peer");
            Check((await ReadFrame(replacement)).Type == WebSocketMessageType.Close, "ban closes existing alternate identities on the same address");
            var removedIds = await removedNotice.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(removedIds.Contains(replacementId) && !removedIds.Contains(RelayWire.PeerId(peerSecret)), "host told who else the ban removed");
            Check(host.IsConnected, "ban leaves the host connected");
            using (var rotated = await ConnectRaw(url, code!, RelayWire.NewSecret(), expectGreeting: false))
                Check((await ReadFrame(rotated)).Type == WebSocketMessageType.Close, "room ban survives identity rotation on same address");
            await host.ModerateAsync("unban", RelayWire.PeerId(peerSecret));
            await Task.Delay(100);
            using var resumed = new RelayClient(peerSecret);
            await resumed.ConnectAsync(url, code!);
            Check(resumed.IsConnected, "unban permits reconnect with same private credential");
            var absentSecret = RelayWire.NewSecret();
            await host.ModerateAsync("ban", RelayWire.PeerId(absentSecret));
            await Task.Delay(100);
            using (var absent = await ConnectRaw(url, code!, absentSecret, expectGreeting: false))
                Check((await ReadFrame(absent)).Type == WebSocketMessageType.Close, "ban issued while the target is away blocks its return");
            Check(resumed.IsConnected, "banning an absent identity leaves others connected");
            await host.ModerateAsync("unban", RelayWire.PeerId(absentSecret));
            await Task.Delay(100);
            var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            resumed.Disconnected += e => ended.TrySetResult(e);
            host.Dispose();
            Check(await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)) is RelaySessionRejectedException, "host disconnect ends room");
            using var late = await ConnectRaw(url, code!, RelayWire.NewSecret(), expectGreeting: false);
            Check((await ReadFrame(late)).Type == WebSocketMessageType.Close, "ended room cannot be rejoined");
            completed = true;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var log = output is null ? "" : await output; var error = await errors;
            if (!completed) Console.WriteLine(log + error);
        }
    }

    private static async Task<(WebSocket Socket, string Path, Dictionary<string, string> Headers)> AcceptSocket(TcpListener listener)
    {
        var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var request = (await reader.ReadLineAsync())!.Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            var colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Bytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Bytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
        return (WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30)), request[1], headers);
    }

    // The Windows sandbox cannot use HTTP.sys; the actual relay room and receive/send code runs unchanged.
    private static async Task RelayFixture(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine("Listening on managed loopback");
        uint nextId = 0;
        while (true)
        {
            var accepted = await AcceptSocket(listener);
            var peerId = RelayWire.PeerId(accepted.Headers["X-AnoMech-Peer-Secret"]);
            var peer = Activator.CreateInstance(Relay.GetNestedType("PeerConn", BindingFlags.NonPublic)!, accepted.Socket, ++nextId, IPAddress.Loopback, peerId)!;
            _ = RunFixturePeer(accepted.Socket, accepted.Path, peer);
        }
    }

    private static async Task RunFixturePeer(WebSocket socket, string path, object peer)
    {
        string? code = null;
        try
        {
            var isHost = path == "/host";
            if (isHost)
            {
                object?[] create = [peer, null];
                if (!(bool)Call("TryCreateSession", create)!) throw new Exception("fixture full");
                code = (string)create[1]!;
            }
            else
            {
                code = path.Split('/').Last();
                object?[] join = [code, peer, null];
                if (!(bool)Call("TryJoin", join)!)
                {
                    await (Task)Call("CloseQuietlyAsync", socket, WebSocketCloseStatus.PolicyViolation, join[2])!;
                    return;
                }
            }
            await (Task)Call("RunPeerAsync", peer, code, isHost)!;
        }
        catch (Exception e) { Console.Error.WriteLine(e); }
        finally { if (code != null) Call("Leave", code, peer); socket.Dispose(); }
    }

    private static async Task<ClientWebSocket> ConnectRaw(string url, string code, string secret, bool expectGreeting = true)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-AnoMech-Protocol", RelayWire.Version.ToString());
        socket.Options.SetRequestHeader("X-AnoMech-Peer-Secret", secret);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri($"{url}/session/{code}"), timeout.Token);
        if (expectGreeting)
        {
            var frame = await ReadFrame(socket);
            Check(frame.Type == WebSocketMessageType.Text && JsonDocument.Parse(frame.Bytes).RootElement.GetProperty("peerId").GetGuid() == RelayWire.PeerId(secret), "greeting precedes room traffic");
        }
        return socket;
    }

    private static Task SendRaw(ClientWebSocket socket, string text) => SendRaw(socket, Bytes(text), WebSocketMessageType.Text);

    private static async Task SendRaw(ClientWebSocket socket, byte[] body, WebSocketMessageType type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(body, type, true, timeout.Token);
    }

    private static async Task<(byte[] Bytes, WebSocketMessageType Type)> ReadFrame(ClientWebSocket socket, TimeSpan? wait = null)
    {
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(5));
        using var output = new MemoryStream(); var buffer = new byte[65536];
        WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer, timeout.Token); output.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        return (output.ToArray(), result.MessageType);
    }

    private static async Task<(byte[] Bytes, WebSocketMessageType Type)?> TryReadFrame(ClientWebSocket socket, TimeSpan wait)
    {
        try { return await ReadFrame(socket, wait); }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task GreetingTests()
    {
        foreach (var mode in new[] { "modern", "wrong version", "missing capabilities", "older relay build", "timeout" })
        {
            var valid = mode == "modern";
            var port = FreePort();
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            var secret = RelayWire.NewSecret();
            using var client = new RelayClient(secret);
            Exception? failure = null;
            client.Disconnected += e => failure = e;
            var connect = client.ConnectAndHostAsync($"ws://localhost:{port}");
            var accepted = await AcceptSocket(listener).WaitAsync(TimeSpan.FromSeconds(5));
            using var socket = accepted.Socket;
            listener.Stop();
            if (mode != "timeout")
            {
                // Same version number, the capabilities it advertised and no peer id: the relay this
                // protocol replaced must still be refused.
                var data = mode == "older relay build"
                    ? JsonSerializer.SerializeToUtf8Bytes(new { relayVersion = RelayWire.Version, capabilities = new[] { "binaryCompression", "senderIdentity" }, sessionCode = "ABCDEF" })
                    : JsonSerializer.SerializeToUtf8Bytes(new { relayVersion = mode == "wrong version" ? RelayWire.Version + 1 : RelayWire.Version,
                        capabilities = mode == "missing capabilities" ? [] : new[] { "binaryCompression", "authenticatedIdentity", "roomModeration" }, sessionCode = "ABCDEF", peerId = RelayWire.PeerId(secret) });
                await socket.SendAsync(data.AsMemory(0, 7), WebSocketMessageType.Text, false, CancellationToken.None);
                await socket.SendAsync(data.AsMemory(7), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            await connect.WaitAsync(TimeSpan.FromSeconds(8));
            Check(client.IsConnected == valid, valid ? "fragmented modern greeting accepted" : $"{mode} greeting rejected");
            if (mode == "timeout") Check(failure is not null and not RelaySessionRejectedException, "slow greeting stays retryable");
            else if (!valid) Check(failure is RelaySessionRejectedException, $"{mode} greeting is a terminal refusal");
        }
    }

    private static async Task RedirectTest()
    {
        var source = new TcpListener(IPAddress.Loopback, 0);
        var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); target.Start();
        try
        {
            var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            using var client = new RelayClient(RelayWire.NewSecret());
            var connect = client.ConnectAndHostAsync($"ws://localhost:{sourcePort}");
            using var accepted = await source.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var stream = accepted.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await stream.WriteAsync(Bytes($"HTTP/1.1 302 Found\r\nLocation: ws://localhost:{targetPort}/host\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await connect.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!client.IsConnected && !target.Pending(), "WebSocket redirects cannot forward peer credentials");
        }
        finally { source.Stop(); target.Stop(); }
    }

    private sealed class ProbeSocket : WebSocket
    {
        private int concurrent;
        public int PeakSends;
        public int Sends;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            var count = Interlocked.Increment(ref concurrent);
            PeakSends = Math.Max(count, PeakSends);
            await Task.Delay(5, token);
            Interlocked.Decrement(ref concurrent);
            Interlocked.Increment(ref Sends);
        }
    }
}
