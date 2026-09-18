using System;
using AnoMech.Network;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Geometry;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using AnoMech.Scenarios;

namespace AnoMech.Multiplayer;

// Session lifecycle and the host<->peer replication loop. The host runs the real scenario
// with joined peers' roles spawned as SimNetworkPuppet; peers run no scenario logic, apply
// what the host broadcasts, and report their own position back (SelfPose).
//
// Everything here runs on the framework thread: Tick() via OnFrameworkUpdate, and relay
// messages are queued and drained there (see DrainPendingMessages).
public sealed partial class MultiplayerManager : IDisposable
{
    private RelayClient? relay;
    private bool running;

    private readonly Dictionary<SimEnemy, int> hostEnemyNetIds = new();
    private int nextEnemyNetId;
    private readonly Dictionary<SimTether, int> hostTetherNetIds = new();
    private int nextTetherNetId;
    // Host-only, for edge-triggered logging; every snapshot still carries the full state.
    private readonly Dictionary<SimEnemy, byte> hostEnemyLastLoggedModelState = new();
    private readonly Dictionary<SimEnemy, Dictionary<ushort, ushort>> hostEnemyLastLoggedStatuses = new();
    private readonly Dictionary<SimEnemy, int> hostEnemyLastLoggedAnimationTimeline = new();
    private readonly Dictionary<SimEnemy, int> hostEnemyLastLoggedAnimationState = new();
    private readonly Dictionary<PartyRole, Dictionary<ushort, ushort>> hostRoleLastLoggedStatuses = new();
    private readonly Dictionary<PartyRole, int> hostRoleLastLoggedAnimationTimeline = new();

    private readonly Dictionary<SimEventObject, int> hostEventObjectNetIds = new();
    private int nextEventObjectNetId;

    private readonly Dictionary<int, SimEnemy> peerEnemies = new();
    private readonly Dictionary<int, SimTether> peerTethers = new();
    private readonly Dictionary<int, SimEventObject> peerEventObjects = new();
    // Peer-only: last applied value per NetId. Re-issuing an unchanged ModelState rebuilds the
    // model (visible flicker) and re-playing an animation restarts it.
    private readonly Dictionary<int, byte> peerEnemyModelState = new();
    private readonly Dictionary<int, Dictionary<ushort, ushort>> peerEnemyLastLoggedStatuses = new();
    private readonly Dictionary<int, int> peerEnemyAnimationTimeline = new();
    private readonly Dictionary<int, int> peerEnemyAnimationState = new();
    private readonly Dictionary<int, int> peerEnemyLastInstantCastSeq = new();
    private readonly Dictionary<int, int> peerEnemyLastCastSeq = new();
    // NetIds whose real-packet spawn the engine dropped locally; recreated as plain doppels.
    private readonly HashSet<int> peerEnemyTemplateFailed = new();
    private readonly Dictionary<int, ushort> peerEventObjectState = new();
    private readonly Dictionary<int, int> peerEventObjectAnimationSeq = new();
    private readonly Dictionary<int, int> peerEventObjectFadeSeq = new();
    // Engine-state seqs applied per NetId (see ActorEngineState): re-issuing an unchanged mode
    // or hold restarts it.
    private readonly Dictionary<int, (int Mode, int Hold, int Direct, int ForceLoad)> peerEnemyEngineSeqs = new();
    private readonly Dictionary<int, bool> peerEnemyModelHidden = new();
    private readonly Dictionary<PartyRole, int> peerRoleAnimationTimelineSeq = new();
    private readonly Dictionary<PartyRole, int> peerRolePlayedActionSeq = new();
    private readonly Dictionary<PartyRole, Dictionary<ushort, ushort>> peerRoleLastLoggedStatuses = new();
    // Statuses this client put on a role by reconciliation; removal must only undo those, never
    // a status the local client manages itself (Sprint via LocalPlayerInputHooks).
    private readonly Dictionary<PartyRole, HashSet<ushort>> peerRoleReconciledStatusIds = new();
    // The peer's zone load is deferred a frame past running=true, so IsInInstance==false only
    // means "left" once it has been true.
    private bool peerEnteredInstance;

    // ---- Connection-quality tracking (runs in the lobby too) ---------------
    private const float PingIntervalSeconds = 2f;
    private const long PeerStaleTimeoutMs = 8000;
    // Catches a mistyped/nonexistent code; the relay has no "session not found" at the
    // transport level.
    private const long NoHostFoundTimeoutMs = 4000;
    private float pingTimer;
    private readonly Dictionary<Guid, long> peerLastSeenMs = new();

    private readonly Dictionary<Guid, float> peerLatencyMs = new();
    private readonly HashSet<Guid> warnedStalePeers = new();
    // Host-only: each peer's self-reported mitigation statuses (SelfMitigationMessage); read by
    // TankMitigation.ComputeMitigation.
    private readonly Dictionary<Guid, HashSet<ushort>> peerMitigationStatusIds = new();
    // Rebuilt by the host each ping cycle and broadcast (PeerStatusMessage).
    private readonly Dictionary<Guid, PeerStatusEntry> peerStatuses = new();
    // Peer-only: the host never pings itself, so its liveness is the time since any host broadcast.
    private long lastHostMessageMs;
    // lastHostMessageMs is seeded to "now" on join, so it alone can't tell "never heard" from
    // "went silent".
    private bool everHeardFromHost;

    // ---- Pre-start readiness check (see StartScenario/FinishStartCheck) -----
    private const float StartCheckTimeoutSeconds = 5f;
    private HashSet<Guid>? pendingStartResponses;
    private readonly Dictionary<Guid, string> startCheckFailures = new();
    private float startCheckTimer;
    public bool IsStartCheckPending => pendingStartResponses != null;
    public string? StartCheckFailureReason { get; private set; }

    // ---- Debug: bot-controlled host or peer ---------------------------------
    // Testing aid: the user's own role is driven by the bot AI, so one developer can fill a
    // session alone. The host's scenario.Run already schedules that choreography (the flag just
    // lets PlayerMovement.MoveTo act on the player); a peer rebuilds it from the host's
    // replay-state message. Sticky across Start/Reset; lobby-only to toggle.
    private bool debugBotControlled;
    public bool DebugBotControlled => debugBotControlled;

    private bool aiReplayStateSent;

    // EndMessage is resent a few times: a lost one would leave peers waiting out PeerStaleTimeoutMs.
    private const int EndMessageResendCount = 4;
    private const float EndMessageResendIntervalSeconds = 1f;
    private bool? pendingEndResendReturnedToInn;
    private string? pendingEndResendReason;
    private int endResendsRemaining;
    private float endResendTimer;

    // RunScenarioAsHost sets ActiveScenario a frame late; without this Tick() would read the
    // null as "run ended".
    private bool hostScenarioStarted;

    // Peer-only: the host's replay-state message, buffered until the zone is entered (arrival
    // order isn't guaranteed), and the opaque shadow state the owning scenario built from it.
    private MpMessage? pendingGenericReplayState;
    private object? debugShadowStateGeneric;
    private bool debugBotReplayStarted;

    public bool SetDebugBotControlled(bool value)
    {
        if (running) return false;
        debugBotControlled = value;
        return true;
    }

    // ---- Reconnection --------------------------------------------------------
    // A room-scoped credential survives automatic reconnection.
    private static readonly TimeSpan[] ReconnectBackoff =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? reconnectCts;
    private bool reconnecting;
    public bool IsReconnecting => reconnecting;
    // Host-only; lets Tick() end a run whose peers have all given up (IsHostStale) while the
    // host would otherwise keep retrying.
    private long? disconnectedSinceMs;
    public int ReconnectAttempt { get; private set; }

    public MultiplayerSession Session { get; private set; } = new();
    public Guid MyPeerId { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsConnected => relay?.IsConnected ?? false;
    public bool IsEncrypted => relay?.IsEncrypted ?? false;
    public bool SupportsCompression => relay?.SupportsCompression ?? false;
    public bool RelayAttestsSender => relay?.SupportsSenderIdentity ?? false;
    public bool IsRunning => running;
    public string? SessionCode { get; private set; }
    public string? RelayUrl { get; private set; }
    // Captured at Host/Join time so a mid-session config edit doesn't change what the
    // reconnect loop sends.
    private string? relayAccessToken;
    private string peerSecret = "";
    private int runGeneration;
    public string DisplayName { get; set; } = "Player";
    // Failed-connect reason for the UI; cleared on the next Host/Join.
    public string? ConnectionError { get; private set; }
    // Why the session ended, when it wasn't this client's own Leave.
    public string? SessionEndReason { get; private set; }

    public PartyRole? MyClaimedRole => Session.RoleOf(MyPeerId);

    // ScenarioIndex comes off the wire; every read goes through here rather than indexing
    // Scenarios directly.
    internal IScenario? TryResolveScenario()
    {
        var scenarios = Plugin.GameInstance.Scenarios;
        if (Session.ScenarioIndex < 0) return null; // nothing chosen yet
        if (NetGuard.InRange(Session.ScenarioIndex, scenarios.Count)) return scenarios[Session.ScenarioIndex];
        if (warnedBadScenarioIndex == Session.ScenarioIndex) return null;
        warnedBadScenarioIndex = Session.ScenarioIndex;
        DiagnosticLog.Warn($"[Multiplayer] Host sent scenario index {Session.ScenarioIndex}, but only {scenarios.Count} exist -- ignoring.");
        return null;
    }

    private int? warnedBadScenarioIndex;

    public event Action? LobbyChanged;

    public PeerStatusEntry? GetPeerStatus(Guid peerId) => peerStatuses.GetValueOrDefault(peerId);

    // Host-only (a peer has no view of other peers' statuses); empty if unclaimed or unreported.
    public IReadOnlyCollection<ushort> PeerMitigationStatusIds(PartyRole role)
    {
        if (!IsHost || !Session.ClaimedBy.TryGetValue(role, out var peerId)) return [];
        return peerMitigationStatusIds.TryGetValue(peerId, out var ids) ? ids : [];
    }

    public float SecondsSinceHostMessage => (Environment.TickCount64 - lastHostMessageMs) / 1000f;
    // SessionCode is set synchronously on Join; this is what confirms a host is actually there.
    public bool EverHeardFromHost => everHeardFromHost;
    public bool IsHostStale => !IsHost && everHeardFromHost && SecondsSinceHostMessage * 1000f > PeerStaleTimeoutMs;
    public bool IsSessionNotFound => !IsHost && !everHeardFromHost && SecondsSinceHostMessage * 1000f > NoHostFoundTimeoutMs;

    // ---- Session lifecycle ----------------------------------------------

    public void HostSession(string relayUrl)
    {
        LeaveSession();
        ConnectionError = null;
        peerSecret = RelayWire.NewSecret();
        MyPeerId = RelayWire.PeerId(peerSecret);
        IsHost = true;
        RelayUrl = relayUrl;
        relayAccessToken = Plugin.Config.TokenForRelay(relayUrl);
        Session = new MultiplayerSession { HostId = MyPeerId };
        Session.Names[MyPeerId] = DisplayName;
        Session.Builds[MyPeerId] = new PeerBuildInfo(PluginBuildInfo.Version, PluginBuildInfo.Checksum);

        DiagnosticLog.Info($"[Multiplayer] Hosting a new session at {relayUrl} as {MyPeerId} ({DisplayName}), build {PluginBuildInfo.ShortChecksum}.");
        var client = new RelayClient(peerSecret);
        WireRelay(client);
        relay = client;
        _ = FinishHostConnectAsync(client);
        LobbyChanged?.Invoke();
    }

    // The ReferenceEquals check keeps a late-arriving code from resurrecting a session that was
    // left mid-connect.
    private async Task FinishHostConnectAsync(RelayClient client)
    {
        var code = await client.ConnectAndHostAsync(RelayUrl!, relayAccessToken);
        if (!ReferenceEquals(relay, client)) return;
        if (code is null) return; // Disconnected already fired
        await Plugin.Framework.Run(() =>
        {
            if (!ReferenceEquals(relay, client)) return;
            SessionCode = code;
            LobbyChanged?.Invoke();
        });
    }

    public void JoinSession(string relayUrl, string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        // Rejoining the room already held must not announce a leave first: it races the new
        // Hello, and a host that reads it second drops the seat this client is coming back to.
        LeaveSessionInternal(notifyOthers: SessionCode != normalized);
        ConnectionError = null;
        // Kept per room, so coming back after a crash or a manual rejoin is the same player to
        // the host and the relay, and resumes the same seat.
        peerSecret = Plugin.Config.RoomSecret(relayUrl, normalized);
        MyPeerId = RelayWire.PeerId(peerSecret);
        IsHost = false;
        RelayUrl = relayUrl;
        relayAccessToken = Plugin.Config.TokenForRelay(relayUrl);
        SessionCode = normalized;
        Session = new MultiplayerSession();
        // Seeded to now, or the host reads as silent since 1970 until its first broadcast.
        lastHostMessageMs = Environment.TickCount64;
        everHeardFromHost = false;

        DiagnosticLog.Info($"[Multiplayer] Joining session {SessionCode} at {relayUrl} as {MyPeerId} ({DisplayName}), build {PluginBuildInfo.ShortChecksum}.");
        relay = new RelayClient(peerSecret);
        WireRelay(relay);
        _ = ConnectAndHelloAsync(relay, relayUrl, SessionCode);
    }

    private async Task ConnectAndHelloAsync(RelayClient client, string relayUrl, string code)
    {
        var hello = new HelloMessage(MyPeerId, DisplayName, PluginBuildInfo.Version, PluginBuildInfo.Checksum);
        await client.ConnectAsync(relayUrl, code, relayAccessToken);
        if (!ReferenceEquals(relay, client) || !client.IsConnected) return;
        DiagnosticLog.Info($"[Multiplayer] Connected to relay, socket ready -- sending Hello.");
        await client.SendAsync(hello);
    }

    // Disconnected captures the instance so an event from a replaced client can be told apart.
    private void WireRelay(RelayClient client, bool activateInbox = true)
    {
        if (activateInbox) pendingMessages.SetSource(client);
        client.MessageReceived += (message, fromHost, connection, peerId, bytes)
            => OnMessageReceivedOffThread(client, message, fromHost, connection, peerId, bytes);
        client.MessageRejected += (sender, fromHost, reason) => OnMessageRejectedOffThread(client, sender, fromHost, reason);
        client.PeersRemoved += removed => OnPeersRemovedOffThread(client, removed);
        client.Disconnected += failure => OnDisconnectedOffThread(client, failure);
    }

    public void LeaveSession() => LeaveSessionInternal(notifyOthers: true);

    private void LeaveSessionInternal(bool notifyOthers)
    {
        if (SessionCode != null)
            DiagnosticLog.Info($"[Multiplayer] Leaving session {SessionCode} (was {(IsHost ? "host" : "peer")}, notifyOthers={notifyOthers}).");
        pendingMessages.SetSource(null);
        runGeneration++;
        lock (strikeGate) strikes.Clear();
        reconnectCts?.Cancel();
        reconnectCts?.Dispose();
        reconnectCts = null;
        reconnecting = false;
        ReconnectAttempt = 0;
        disconnectedSinceMs = null;
        if (IsHost) Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
        if (IsHost) Plugin.GameInstance.World.OmenSpawned -= OnOmenSpawnedHost;

        // Dispose only after the SessionEnded send completes, or it usually aborts before
        // reaching the wire. notifyOthers is false when reacting to someone else's
        // SessionEnded, to avoid a cascade.
        if (notifyOthers && relay is { IsConnected: true } activeRelay)
            _ = activeRelay.SendAsync(new SessionEndedMessage(MyPeerId)).ContinueWith(_ => activeRelay.Dispose());
        else
            relay?.Dispose();
        relay = null;

        running = false;
        IsHost = false;
        SessionCode = null;
        RelayUrl = null;
        ConnectionError = null;
        SessionEndReason = null;
        RunEndReason = null;
        warnedBadScenarioIndex = null;
        peerConnectionIds.Clear();
        peerEnteredInstance = false;
        bannedPeers.Clear();
        Session = new MultiplayerSession();
        hostEnemyNetIds.Clear();
        hostEnemyLastLoggedModelState.Clear();
        hostEnemyLastLoggedStatuses.Clear();
        hostEnemyLastLoggedAnimationTimeline.Clear();
        hostEnemyLastLoggedAnimationState.Clear();
        hostRoleLastLoggedStatuses.Clear();
        hostRoleLastLoggedAnimationTimeline.Clear();
        hostTetherNetIds.Clear();
        hostEventObjectNetIds.Clear();
        peerEnemies.Clear();
        peerEnemyModelState.Clear();
        peerEnemyLastLoggedStatuses.Clear();
        peerEnemyAnimationTimeline.Clear();
        peerEnemyAnimationState.Clear();
        peerEnemyLastInstantCastSeq.Clear();
        peerEnemyLastCastSeq.Clear();
        peerEnemyTemplateFailed.Clear();
        peerRoleLastLoggedStatuses.Clear();
        peerRoleAnimationTimelineSeq.Clear();
        peerRolePlayedActionSeq.Clear();
        peerRoleReconciledStatusIds.Clear();
        peerTethers.Clear();
        peerEventObjects.Clear();
        peerEventObjectState.Clear();
        peerEventObjectAnimationSeq.Clear();
        peerEventObjectFadeSeq.Clear();
        peerEnemyEngineSeqs.Clear();
        peerEnemyModelHidden.Clear();
        peerLastSeenMs.Clear();
        peerLatencyMs.Clear();
        peerStatuses.Clear();
        peerMitigationStatusIds.Clear();
        lastSentMitigationStatusIds.Clear();
        lastSentShieldFraction = 0f;
        TankShieldTracker.Reset();
        warnedStalePeers.Clear();
        pingTimer = 0f;
        pendingStartResponses = null;
        startCheckFailures.Clear();
        StartCheckFailureReason = null;
        startCheckTimer = 0f;
        debugBotControlled = false;
        aiReplayStateSent = false;
        pendingEndResendReturnedToInn = null;
        RestoreOwnScenarioSettings();
        StopDebugBotReplay();
    }

    public void Dispose() => LeaveSession();

    // ---- Reconnection ---------------------------------------------------

    // No attempt limit: "Leave session" is always the user's way out.
    private void BeginReconnect()
    {
        if (IsHost || SessionCode == null || RelayUrl == null || reconnecting) return;
        DiagnosticLog.Info($"[Multiplayer] Connection to {RelayUrl} lost -- beginning reconnect loop for session {SessionCode}.");
        reconnecting = true;
        ReconnectAttempt = 0;
        reconnectCts = new CancellationTokenSource();
        _ = ReconnectLoopAsync(RelayUrl, SessionCode, peerSecret, relayAccessToken, reconnectCts.Token);
    }

    private async Task ReconnectLoopAsync(string relayUrl, string sessionCode, string secret, string? accessToken, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = ReconnectBackoff[Math.Min(ReconnectAttempt, ReconnectBackoff.Length - 1)];
            DiagnosticLog.Info($"[Multiplayer] Reconnect attempt {ReconnectAttempt + 1} in {delay.TotalSeconds}s.");
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;

            var client = new RelayClient(secret);
            WireRelay(client, activateInbox: false);
            // OnDisconnectedOffThread ignores a client not yet installed as `relay`, so the
            // failure is captured here.
            Exception? failure = null;
            client.Disconnected += e => failure = e;
            await client.ConnectAsync(relayUrl, sessionCode, accessToken).ConfigureAwait(false);
            if (token.IsCancellationRequested) { client.Dispose(); return; }
            var connected = client.IsConnected;
            DiagnosticLog.Info($"[Multiplayer] Reconnect attempt {ReconnectAttempt + 1}: {(connected ? "succeeded" : "failed")}.");

            if (!connected && failure is RelaySessionRejectedException rejected)
            {
                // The relay says the session doesn't exist (it most likely restarted); retrying
                // the same code can never succeed.
                client.Dispose();
                DiagnosticLog.Warn($"[Multiplayer] Giving up on session {sessionCode} -- relay says: {rejected.Message}.");
                var wasHost = IsHost;
                _ = Plugin.Framework.Run(() =>
                {
                    if (token.IsCancellationRequested) return;
                    LeaveSessionInternal(notifyOthers: false);
                    SessionEndReason = wasHost
                        ? $"The relay lost this session ({rejected.Message}) -- start a new one."
                        : $"The relay lost this session ({rejected.Message}) -- ask the host to start a new one.";
                    LobbyChanged?.Invoke();
                });
                return;
            }

            if (await Plugin.Framework.Run(() => FinishReconnectAttempt(client, token))) return;
            if (token.IsCancellationRequested) return;
            ReconnectAttempt++;
        }
    }

    private bool FinishReconnectAttempt(RelayClient client, CancellationToken token)
    {
        if (token.IsCancellationRequested || !client.IsConnected)
        {
            client.Dispose();
            return false;
        }
        relay = client;
        pendingMessages.SetSource(client);
        reconnecting = false;
        ReconnectAttempt = 0;
        disconnectedSinceMs = null;
        DiagnosticLog.Info($"[Multiplayer] Reconnected to session {SessionCode}.");
        lastHostMessageMs = Environment.TickCount64;
        ConnectionError = null;
        // Re-registering with the host resumes a running scenario the same way a late join does.
        if (!IsHost) _ = relay.SendAsync(new HelloMessage(MyPeerId, DisplayName, PluginBuildInfo.Version, PluginBuildInfo.Checksum));
        LobbyChanged?.Invoke();
        return true;
    }

    // ---- Role claiming ----------------------------------------------------

    public void ClaimRole(PartyRole role)
    {
        if (relay == null) return;
        if (IsHost) ApplyClaim(MyPeerId, role);
        else _ = relay.SendAsync(new ClaimRoleMessage(MyPeerId, role));
    }

    public void ReleaseRole()
    {
        if (relay == null) return;
        if (IsHost) ApplyRelease(MyPeerId);
        else _ = relay.SendAsync(new ReleaseRoleMessage(MyPeerId));
    }

    public void RequestReset()
    {
        if (IsHost || relay is not { IsConnected: true }) return;
        _ = relay.SendAsync(new ResetRequestMessage(MyPeerId));
    }

    // Peer-only: the host ends the run for the whole group (LeaveRequestMessage); LeaveSession
    // disconnects just this client.
    public void RequestLeaveInstance()
    {
        if (IsHost || relay is not { IsConnected: true }) return;
        _ = relay.SendAsync(new LeaveRequestMessage(MyPeerId));
    }

    private void ApplyClaim(Guid peerId, PartyRole role)
    {
        if (Session.Started || !Session.Names.ContainsKey(peerId) || !Enum.IsDefined(role)) return;
        if (Session.ClaimedBy.TryGetValue(role, out var holder) && holder != peerId)
        {
            DiagnosticLog.Info($"[Multiplayer] Rejected role claim: {Session.NameOf(peerId)} wanted {role}, already held by {Session.NameOf(holder)}.");
            return;
        }
        // Rejected rather than kicked, so it self-resolves once the peer updates.
        if (IsVersionMismatched(peerId))
        {
            DiagnosticLog.Warn($"[Multiplayer] Rejected role claim from {Session.NameOf(peerId)} -- plugin build mismatch.");
            return;
        }
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        Session.ClaimedBy[role] = peerId;
        DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} claimed {role}.");
        BroadcastLobbyState();
    }

    // An "unknown" checksum (local checksumming failed) fails open.
    public bool IsVersionMismatched(Guid peerId)
    {
        if (!Session.Builds.TryGetValue(peerId, out var build)) return false;
        if (build.Checksum == "unknown" || PluginBuildInfo.Checksum == "unknown") return false;
        return build.Checksum != PluginBuildInfo.Checksum;
    }

    private void ApplyRelease(Guid peerId)
    {
        if (Session.Started || !Session.Names.ContainsKey(peerId)) return;
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} released their role.");
        BroadcastLobbyState();
    }

    // ---- Host-side roster management ----------------------------------------

    // Lobby only. The previous holder is unseated, not swapped.
    public void AssignRole(Guid peerId, PartyRole role)
    {
        if (!IsHost || Session.Started || !Session.Names.ContainsKey(peerId)) return;
        if (IsVersionMismatched(peerId))
        {
            DiagnosticLog.Warn($"[Multiplayer] Not assigning {role} to {Session.NameOf(peerId)} -- plugin build mismatch.");
            return;
        }
        if (Session.ClaimedBy.TryGetValue(role, out var holder) && holder != peerId)
        {
            Session.ClaimedBy.Remove(role);
            DiagnosticLog.Info($"[Multiplayer] Host unseated {Session.NameOf(holder)} from {role} to assign it to {Session.NameOf(peerId)}.");
        }
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
            Session.ClaimedBy.Remove(r);
        Session.ClaimedBy[role] = peerId;
        DiagnosticLog.Info($"[Multiplayer] Host assigned {role} to {Session.NameOf(peerId)}.");
        BroadcastLobbyState();
    }

    public void UnassignRole(Guid peerId)
    {
        if (!IsHost || Session.Started) return;
        ApplyRelease(peerId);
    }

    // Names retained for the room ban list.
    private readonly Dictionary<Guid, string> bannedPeers = new();
    public IReadOnlyDictionary<Guid, string> BannedPeers => bannedPeers;

    public void KickPeer(Guid peerId) => RemoveByHost(peerId, ban: false);
    public void BanPeer(Guid peerId) => RemoveByHost(peerId, ban: true);

    private void RemoveByHost(Guid peerId, bool ban)
    {
        if (!IsHost || peerId == MyPeerId || relay == null || !Session.Names.ContainsKey(peerId)
            || (ban && bannedPeers.Count >= 1024)) return;
        var who = Session.NameOf(peerId);
        DiagnosticLog.Info($"[Multiplayer] Host {(ban ? "banned" : "kicked")} {who} ({peerId}).");
        if (ban) bannedPeers[peerId] = who;
        _ = relay.ModerateAsync(ban ? "ban" : "kick", peerId);
        RemovePeer(peerId);
    }

    public void UnbanPeer(Guid peerId)
    {
        if (!IsHost || !bannedPeers.Remove(peerId, out var who)) return;
        _ = relay?.ModerateAsync("unban", peerId);
        DiagnosticLog.Info($"[Multiplayer] Host unbanned {who} ({peerId}).");
        LobbyChanged?.Invoke();
    }

    // Both the display summary and the overrides themselves: a peer's own code reads them too
    // (IScenario.RunInstanceEvents), so the host's choices have to reach it.
    public void PublishScenarioSettings(IScenario? scenario)
    {
        if (!IsHost) return;
        var overrides = scenario is { SupportsMultiplayer: true } ? scenario.SettingsOverrides : null;
        var lines = ScenarioSettingsSummary.Describe(overrides);
        var json = ScenarioSettingsSync.Serialize(overrides);
        if (lines.SequenceEqual(Session.ScenarioSettings) && json == Session.ScenarioSettingsJson) return;
        Session.ScenarioSettings = lines;
        Session.ScenarioSettingsJson = json;
        BroadcastLobbyState();
    }

    // Peer-only: the host's overrides for the selected scenario, applied to our own instance of
    // them. Ours are put back when the session ends, so a lobby can't leave its debug knobs
    // behind on someone's solo settings.
    private string? appliedScenarioSettingsJson;
    private (object Target, string Json)? scenarioSettingsBackup;

    private void ApplyHostScenarioSettings()
    {
        if (IsHost) return;
        var json = Session.ScenarioSettingsJson;
        if (json == appliedScenarioSettingsJson) return;
        if (string.IsNullOrEmpty(json) || TryResolveScenario()?.SettingsOverrides is not { } overrides) return;
        if (scenarioSettingsBackup is not { } backup || !ReferenceEquals(backup.Target, overrides))
        {
            RestoreOwnScenarioSettings();
            if (ScenarioSettingsSync.Serialize(overrides) is { } ownJson) scenarioSettingsBackup = (overrides, ownJson);
        }
        appliedScenarioSettingsJson = json;
        ScenarioSettingsSync.Apply(overrides, json);
    }

    private void RestoreOwnScenarioSettings()
    {
        appliedScenarioSettingsJson = null;
        if (scenarioSettingsBackup is not { } backup) return;
        scenarioSettingsBackup = null;
        DiagnosticLog.Info($"[Multiplayer] Restoring our own {backup.Target.GetType().Name} settings.");
        ScenarioSettingsSync.Apply(backup.Target, backup.Json);
    }

    // Mirrors the main-window selection pre-start so the lobby can name it.
    public void PublishSelectedScenario(IScenario? scenario)
    {
        if (!IsHost || Session.Started || scenario == null) return;
        var index = Plugin.GameInstance.Scenarios.ToList().IndexOf(scenario);
        if (index < 0 || index == Session.ScenarioIndex) return;
        Session.ScenarioIndex = index;
        BroadcastLobbyState();
    }

    // Drops the peer from the roster entirely, unlike ApplyRelease.
    private void RemovePeer(Guid peerId)
    {
        if (!Session.Names.ContainsKey(peerId) || peerId == MyPeerId) return;
        var occupiedRole = Session.RoleOf(peerId) != null;
        var who = Session.NameOf(peerId);
        // Captured first: settling a start check below can start the run, which must not then
        // read as this peer leaving mid-fight.
        var wasRunning = running;
        DiagnosticLog.Info($"[Multiplayer] Removing {who} ({peerId}) from the session (running={running}).");
        // A seated player leaving mid-check fails it rather than the run starting without them.
        // Settled before the roster changes so the failure can still name them.
        startCheckFailures.Remove(peerId);
        if (pendingStartResponses?.Remove(peerId) == true)
        {
            if (occupiedRole) startCheckFailures[peerId] = "left the session";
            if (pendingStartResponses.Count == 0) FinishStartCheck();
        }
        foreach (var r in Session.ClaimedBy.Where(kv => kv.Value == peerId).Select(kv => kv.Key).ToList())
        {
            Session.ClaimedBy.Remove(r);
            // Their banked shield must not linger onto the role's next claimant.
            TankShieldTracker.SetFromPeerReport(r, 0f);
        }
        Session.Names.Remove(peerId);
        Session.Builds.Remove(peerId);
        peerConnectionIds.Remove(peerId);
        peerLastSeenMs.Remove(peerId);
        peerLatencyMs.Remove(peerId);
        peerStatuses.Remove(peerId);
        peerMitigationStatusIds.Remove(peerId);
        warnedStalePeers.Remove(peerId);
        // A missing party member usually dooms the mechanic; Tick() broadcasts the end once
        // ActiveScenario clears.
        if (occupiedRole && wasRunning)
        {
            DiagnosticLog.Info($"[Multiplayer] Ending the run because {who} left mid-fight.");
            if (Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
            BroadcastRunEnded(returnedToInn: true);
        }
        BroadcastLobbyState();
    }

    private void BroadcastLobbyState()
    {
        LobbyChanged?.Invoke();
        _ = relay?.SendAsync(Session.ToMessage());
    }

    // ---- Starting the scenario ---------------------------------------------

    // Also printed to chat: by the time it matters everyone is back in the inn and the window
    // may be closed.
    public string? RunEndReason { get; private set; }

    internal void AnnounceRunEnded(string reason)
    {
        RunEndReason = reason;
        DiagnosticLog.Warn($"[Multiplayer] Run ended: {reason}");
        Plugin.ChatGui.PrintError($"[AnoMech] Run ended -- {reason}");
        LobbyChanged?.Invoke();
    }

    private void AbortStart(string reason)
    {
        DiagnosticLog.Warn($"[Multiplayer] Refusing the host's start: {reason}.");
        _ = relay?.SendAsync(new StartAbortMessage(MyPeerId, reason));
        AnnounceRunEnded($"you couldn't start: {reason}");
    }

    // The preconditions RunScenarioInternal enforces, checked up front so a failure is reported
    // instead of a silent no-op. A claimed tank role must be on a tank job: bot mitigation picks
    // ability ids off the seat's job.
    private string? CheckOwnStartReadiness()
    {
        if (!ZoneSession.IsInInn()) return "not in an inn";
        if (ZoneSession.IsPlayerBusy()) return "busy";
        if (MyClaimedRole is { } role && role.IsTank())
        {
            var jobId = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
            if (!PartyPresets.SkipRoleForJob(jobId).IsTank())
                return "queued as a tank role but not on a tank job";
        }
        return null;
    }

    // Doesn't start immediately: every claimed peer first confirms readiness
    // (StartCheckMessage), so a peer who isn't in an inn fails loudly here rather than
    // silently seconds later.
    public void StartScenario()
    {
        if (!IsHost || relay == null) return;
        if (!IsConnected)
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: not connected to the relay.");
            return;
        }
        if (MyClaimedRole == null)
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: host has not claimed a role.");
            return;
        }
        if (Plugin.MainWindow.SelectedScenario is not { } selectedScenario
            || !selectedScenario.SupportsMultiplayer)
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: no multiplayer-supported scenario is selected in the main window.");
            return;
        }
        // A region with no strats leaves SelectedStrat at -1, which a debug-bot peer would index with.
        if (!Plugin.MainWindow.HasStartableStrat())
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: no strat available for the selected scenario/region.");
            return;
        }
        // Starting anyway would drop whatever the slot layout can't seat, giving the host a run
        // they didn't set up.
        if (selectedScenario.SettingsConflicts is { Count: > 0 } conflicts)
        {
            foreach (var conflict in conflicts)
                DiagnosticLog.Warn($"[Multiplayer] Cannot start: {conflict}");
            return;
        }
        var scenarioIndex = Plugin.GameInstance.Scenarios.ToList().IndexOf(selectedScenario);
        Session.ScenarioIndex = scenarioIndex;
        Session.SelectedAi = Plugin.MainWindow.SelectedStrat;
        Session.SelectedWaymark = Plugin.MainWindow.SelectedWaymark;
        // ApplyClaim already rejects these; this closes the race.
        if (Session.ClaimedBy.Values.Any(IsVersionMismatched))
        {
            DiagnosticLog.Warn("[Multiplayer] Cannot start: one or more claimed players are on a different plugin build.");
            return;
        }
        if (IsStartCheckPending) return;

        if (CheckOwnStartReadiness() is { } ownReason)
        {
            StartCheckFailureReason = $"You cannot start: {ownReason}.";
            DiagnosticLog.Info($"[Multiplayer] Cannot start: {ownReason}.");
            LobbyChanged?.Invoke();
            return;
        }

        StartCheckFailureReason = null;
        RunEndReason = null;
        startCheckFailures.Clear();
        startCheckTimer = 0f;
        pendingStartResponses = Session.ClaimedBy.Values.Where(id => id != MyPeerId).ToHashSet();
        // Don't wait out the full timeout for someone already known gone.
        foreach (var peerId in pendingStartResponses.ToList())
        {
            if (!IsPeerStale(peerId)) continue;
            startCheckFailures[peerId] = "disconnected";
            pendingStartResponses.Remove(peerId);
        }
        DiagnosticLog.Info($"[Multiplayer] Start requested -- waiting on readiness from: {string.Join(", ", pendingStartResponses.Select(Session.NameOf))}.");
        _ = relay.SendAsync(new StartCheckMessage());
        LobbyChanged?.Invoke();

        if (pendingStartResponses.Count == 0) FinishStartCheck();
    }

    private void FinishStartCheck()
    {
        pendingStartResponses = null;
        if (startCheckFailures.Count > 0)
        {
            var summary = string.Join(", ", startCheckFailures.Select(kv => $"{Session.NameOf(kv.Key)} ({kv.Value})"));
            StartCheckFailureReason = $"{startCheckFailures.Count} player(s) cannot start: {summary}.";
            DiagnosticLog.Info($"[Multiplayer] Start check failed: {StartCheckFailureReason}");
            LobbyChanged?.Invoke();
            return;
        }
        DiagnosticLog.Info("[Multiplayer] Start check passed -- starting the scenario.");
        ActuallyStartScenario();
    }

    private void ActuallyStartScenario()
    {
        if (MyClaimedRole is not { } myRole) return;

        if (TryResolveScenario() is not { } scenario) return;
        var networkRoles = Session.ClaimedBy.Where(kv => kv.Value != MyPeerId).Select(kv => kv.Key).ToHashSet();
        DiagnosticLog.Info($"[Multiplayer] Host starting '{scenario.Name}' as {myRole}. Network roles: {string.Join(", ", networkRoles.Select(r => $"{r}={Session.NameOf(Session.ClaimedBy[r])}"))}.");

        Session.Started = true;
        _ = relay!.SendAsync(Session.ToMessage());
        _ = relay.SendAsync(new StartMessage());

        hostEnemyNetIds.Clear();
        hostEnemyLastLoggedModelState.Clear();
        hostEnemyLastLoggedStatuses.Clear();
        hostEnemyLastLoggedAnimationTimeline.Clear();
        hostEnemyLastLoggedAnimationState.Clear();
        hostRoleLastLoggedStatuses.Clear();
        hostRoleLastLoggedAnimationTimeline.Clear();
        hostTetherNetIds.Clear();
        hostEventObjectNetIds.Clear();
        nextEnemyNetId = 0;
        nextTetherNetId = 0;
        nextEventObjectNetId = 0;
        warnedStalePeers.Clear();
        aiReplayStateSent = false;
        pendingEndResendReturnedToInn = null;
        hostScenarioStarted = false;
        var nowMs = Environment.TickCount64;
        foreach (var peerId in Session.ClaimedBy.Values)
            if (peerId != MyPeerId)
                peerLastSeenMs[peerId] = nowMs;
        Plugin.GameInstance.PartyMemberKilled += OnPartyMemberKilledHost;
        Plugin.GameInstance.World.OmenSpawned += OnOmenSpawnedHost;
        var generation = ++runGeneration;
        Plugin.GameInstance.RunScenarioAsHost(scenario, myRole, Session.SelectedAi, Session.SelectedWaymark, networkRoles, ClaimedRoleNames(), () => running && generation == runGeneration);
        // scenario.Run already scheduled the Ai against every role; this lets it move the
        // host's own character. The party exists only after the deferred RunScenarioInternal,
        // so the obstacle field is wired one callback later.
        if (debugBotControlled)
        {
            DiagnosticLog.Info("[Multiplayer] Host: debug-bot mode active for own character this run.");
            DebugBotControl.Enabled = true;
            _ = Plugin.Framework.Run(() => { if (running && generation == runGeneration) GiveLocalPlayerObstacles(); });
        }
        running = true;
        LobbyChanged?.Invoke();
    }

    private void OnStartReceived()
    {
        // Idempotent: a fresh start delivers both LobbyState(Started) and StartMessage, and a
        // late join replays it from LobbyState.
        if (IsHost) return;
        if (running)
        {
            DiagnosticLog.Debug("[Multiplayer] OnStartReceived: already running -- ignoring (idempotency guard).");
            return;
        }
        if (MyClaimedRole is not { } myRole)
        {
            DiagnosticLog.Warn("[Multiplayer] Host started the scenario, but I never claimed a role -- ignoring.");
            return;
        }

        if (TryResolveScenario() is not { } scenario)
        {
            AbortStart("the host chose a scenario this build doesn't have");
            return;
        }
        // The start check is advisory (a late join skips it, and state changes in between);
        // RunScenarioInternal would silently no-op, so refuse and say why.
        if (CheckOwnStartReadiness() is { } notReady)
        {
            AbortStart(notReady);
            return;
        }

        RunEndReason = null;
        var networkRoles = Enum.GetValues<PartyRole>().Where(r => r != myRole).ToHashSet();
        DiagnosticLog.Info($"[Multiplayer] Peer entering '{scenario.Name}' as {myRole}.");

        peerEnemies.Clear();
        peerEnemyModelState.Clear();
        peerEnemyLastLoggedStatuses.Clear();
        peerEnemyAnimationTimeline.Clear();
        peerEnemyAnimationState.Clear();
        peerEnemyLastInstantCastSeq.Clear();
        peerEnemyLastCastSeq.Clear();
        peerEnemyTemplateFailed.Clear();
        peerRoleLastLoggedStatuses.Clear();
        peerRoleAnimationTimelineSeq.Clear();
        peerRolePlayedActionSeq.Clear();
        peerRoleReconciledStatusIds.Clear();
        peerTethers.Clear();
        peerEventObjects.Clear();
        peerEventObjectState.Clear();
        peerEventObjectAnimationSeq.Clear();
        peerEventObjectFadeSeq.Clear();
        peerEnemyEngineSeqs.Clear();
        peerEnemyModelHidden.Clear();
        peerEnteredInstance = false;
        StopDebugBotReplay();
        var generation = ++runGeneration;
        Plugin.GameInstance.RunScenarioAsPeer(scenario, myRole, Session.SelectedWaymark, networkRoles, ClaimedRoleNames(), () => running && generation == runGeneration);
        running = true;
    }

    // Names for the puppets: every role claimed by someone else, the host's included from a
    // peer's side.
    private Dictionary<PartyRole, string> ClaimedRoleNames() =>
        Session.ClaimedBy.Where(kv => kv.Value != MyPeerId).ToDictionary(kv => kv.Key, kv => Session.NameOf(kv.Value));

    private string DescribeRoleOwner(PartyRole role, SimCharacter? member)
    {
        if (member == null) return "empty";
        if (member is SimNetworkPuppet puppet) return $"{puppet.DisplayName}, job {puppet.ClassJob}";
        if (ReferenceEquals(member, Plugin.GameInstance.World.Party.Player))
            return $"{DisplayName} (me), job {Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId.ToString() ?? "?"}";
        return "bot";
    }

    // One line per status gained/lost/restacked; lastSeen is mutated in place.
    private static void LogStatusChanges(string who, IReadOnlyList<(ushort StatusId, ushort Stacks, float RemainingTime)> current, Dictionary<ushort, ushort> lastSeen)
    {
        var currentIds = new HashSet<ushort>();
        foreach (var (id, stacks, remaining) in current)
        {
            currentIds.Add(id);
            if (!lastSeen.TryGetValue(id, out var lastStacks))
                DiagnosticLog.Info($"[Multiplayer] {who}: status {id} gained (stacks={stacks}, duration={remaining:F1}).");
            else if (lastStacks != stacks)
                DiagnosticLog.Info($"[Multiplayer] {who}: status {id} stacks {lastStacks}->{stacks}.");
            lastSeen[id] = stacks;
        }
        foreach (var id in lastSeen.Keys.Where(id => !currentIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] {who}: status {id} lost.");
            lastSeen.Remove(id);
        }
    }

}
