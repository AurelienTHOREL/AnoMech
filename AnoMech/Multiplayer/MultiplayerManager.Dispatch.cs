using System;
using AnoMech.Network;
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

public sealed partial class MultiplayerManager
{
    // ---- Message pump -------------------------------------------------------

    // Queued and drained in Tick rather than one Framework.Run per message: Dalamud's task
    // scheduler doesn't run pending tasks in insertion order, and wire order matters.
    private readonly SessionInbox<(MpMessage Message, bool IsFromHost, uint SenderId, Guid PeerId)> pendingMessages =
        new(NetGuard.MaxQueuedMessages, RelayWire.MaxQueuedBytes);

    private long shedMessages;

    private void DrainPendingMessages()
    {
        var budget = NetGuard.MaxMessagesPerDrain;
        while (budget-- > 0 && pendingMessages.TryDequeue(out var entry))
            Dispatch(entry.Message, entry.IsFromHost, entry.SenderId, entry.PeerId);
    }

    // Receive thread. Snapshots and poses are superseded by the next one, so they're shed first
    // when the frame loop falls behind. On overflow a peer reconnects to resync; the host has
    // nothing to resync from and drops peer messages instead of ending the room.
    private void OnMessageReceivedOffThread(RelayClient client, MpMessage message, bool fromHost, uint connection, Guid peerId, int bytes)
    {
        if (!ReferenceEquals(relay, client)) return;
        if (message is WorldSnapshotMessage or RolesSnapshotMessage or SelfPoseMessage && pendingMessages.IsBacklogged)
        {
            if (Interlocked.Increment(ref shedMessages) % 500 == 1)
                DiagnosticLog.Warn($"[Multiplayer] Falling behind -- shedding superseded snapshots and poses ({Interlocked.Read(ref shedMessages)} so far).");
            return;
        }
        if (pendingMessages.TryEnqueue(client, (message, fromHost, connection, peerId), bytes) != InboxResult.Full) return;
        if (!fromHost)
        {
            if (Interlocked.Increment(ref shedMessages) % 500 == 1)
                DiagnosticLog.Warn($"[Multiplayer] Inbound queue full -- dropping peer messages ({Interlocked.Read(ref shedMessages)} shed so far).");
            return;
        }
        DiagnosticLog.Warn($"[Multiplayer] Inbound queue full of messages that can't be skipped -- dropping the connection to resync.");
        client.Dispose();
    }

    // ---- Abuse handling (host) ----------------------------------------------

    // The relay forwards bodies unread, so the host is where a peer sending messages that
    // don't decode or validate gets caught. A few is a build mismatch; this many is not.
    private const int StrikesBeforeKick = 10;
    private const long StrikeWindowMs = 10_000;
    private readonly object strikeGate = new();
    private readonly Dictionary<Guid, (int Count, long WindowStartMs)> strikes = new();

    private void OnMessageRejectedOffThread(RelayClient client, Guid sender, bool fromHost, string reason)
    {
        if (!ReferenceEquals(relay, client)) return;
        int count;
        lock (strikeGate)
        {
            var now = Environment.TickCount64;
            if (strikes.Count > 256) strikes.Clear();
            var entry = strikes.GetValueOrDefault(sender);
            if (now - entry.WindowStartMs > StrikeWindowMs) entry = (0, now);
            count = ++entry.Count;
            strikes[sender] = entry;
        }
        if (count == 1 || count == StrikesBeforeKick)
            DiagnosticLog.Warn($"[Multiplayer] Dropped an invalid message from {(fromHost ? "the host" : sender.ToString())}: {reason}");
        if (count != StrikesBeforeKick || fromHost || !IsHost) return;
        _ = Plugin.Framework.Run(() =>
        {
            if (!ReferenceEquals(relay, client)) return;
            DiagnosticLog.Warn($"[Multiplayer] Kicking {Session.NameOf(sender)} ({sender}) -- {StrikesBeforeKick} invalid messages in {StrikeWindowMs / 1000}s.");
            _ = relay?.ModerateAsync("kick", sender);
            RemovePeer(sender);
        });
    }

    // Others the relay removed alongside a ban (same address). They leave without a
    // SessionEnded, so without this the host would keep them seated.
    private void OnPeersRemovedOffThread(RelayClient client, IReadOnlyList<Guid> removed)
        => Plugin.Framework.Run(() =>
        {
            if (!ReferenceEquals(relay, client) || !IsHost) return;
            foreach (var id in removed) RemovePeer(id);
        });

    private long lastIdleEndSentMs;

    // The ReferenceEquals guard drops events from superseded clients, and a manual Leave
    // (which nulls `relay` first) never gets past it.
    private void OnDisconnectedOffThread(RelayClient source, Exception? failure)
        => Plugin.Framework.Run(() =>
        {
            if (!ReferenceEquals(relay, source)) return;
            pendingMessages.SetSource(null);
            // A joiner that never reached the relay has nothing to resume. Retrying a wrong
            // address or a TLS mismatch would otherwise loop forever behind "Waiting for the host".
            if (IsHost || failure is RelaySessionRejectedException || !source.HasConnected)
            {
                var reason = failure?.Message ?? "Connection lost -- room ended.";
                DiagnosticLog.Warn($"[Multiplayer] Session over ({(IsHost ? "host" : "peer")}): {reason}");
                if (Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
                LeaveSessionInternal(notifyOthers: false);
                SessionEndReason = reason;
                ConnectionError = failure?.Message;
                LobbyChanged?.Invoke();
                return;
            }
            source.Dispose();
            relay = null;
            disconnectedSinceMs ??= Environment.TickCount64;
            if (failure != null)
            {
                DiagnosticLog.Warn($"[Multiplayer] Disconnected: {failure.Message}");
                ConnectionError = failure.Message;
            }
            else
            {
                DiagnosticLog.Info("[Multiplayer] Disconnected (no failure reported -- socket just closed).");
            }
            LobbyChanged?.Invoke();
            BeginReconnect();
        });

    private void Dispatch(MpMessage message, bool isFromHost, uint senderId, Guid authenticatedPeerId)
    {
        // One bad message must not take down the tick.
        try
        {
            DispatchCore(message, isFromHost, senderId, authenticatedPeerId);
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[Multiplayer] Error handling {message.GetType().Name}: {e}");
        }
    }

    private readonly Dictionary<Guid, uint> peerConnectionIds = new();

    private static readonly Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Weather> WeatherSheet =
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Weather>();

    // Host-driven world state is applied only inside a live run; outside one it would act on
    // the real inn.
    private bool PeerInRun => !IsHost && running && peerEnteredInstance && Plugin.GameInstance.World.Map.IsInInstance;

    private static Guid? ClaimedPeerId(MpMessage message) => message switch
    {
        HelloMessage m => m.PeerId,
        ClaimRoleMessage m => m.PeerId,
        ReleaseRoleMessage m => m.PeerId,
        SelfPoseMessage m => m.PeerId,
        PongMessage m => m.PeerId,
        StartCheckResponseMessage m => m.PeerId,
        StartAbortMessage m => m.PeerId,
        SessionEndedMessage m => m.PeerId,
        ResetRequestMessage m => m.PeerId,
        LeaveRequestMessage m => m.PeerId,
        SelfMitigationMessage m => m.PeerId,
        PeerAppliedEnemyStatusMessage m => m.PeerId,
        PeerAppliedRoleStatusMessage m => m.PeerId,
        _ => null,
    };

    private void DispatchCore(MpMessage message, bool isFromHost, uint senderId, Guid authenticatedPeerId)
    {
        if (message is IHostOnlyMessage && !isFromHost)
        {
            DiagnosticLog.Warn($"[Multiplayer] Dropped {message.GetType().Name} -- relay says it wasn't from the host.");
            return;
        }
        if (IsHost)
        {
            if (isFromHost || authenticatedPeerId == MyPeerId || ClaimedPeerId(message) != authenticatedPeerId) return;
            if (bannedPeers.ContainsKey(authenticatedPeerId))
            {
                // A ban issued while they were disconnected never reached the relay's live list;
                // repeating it now closes the connection instead of leaving a silent listener.
                if (message is HelloMessage) _ = relay?.ModerateAsync("ban", authenticatedPeerId);
                return;
            }
            if (message is not HelloMessage && (!Session.Names.ContainsKey(authenticatedPeerId)
                || peerConnectionIds.GetValueOrDefault(authenticatedPeerId) != senderId)) return;
        }
        else if (!isFromHost || (message is LobbyStateMessage lobby && lobby.HostId != authenticatedPeerId)) return;
        // Host liveness: only types the host itself broadcasts (SessionEnded excluded, any peer
        // can send it). Keep in sync with the `when !IsHost` cases below.
        if (!IsHost && message is LobbyStateMessage or StartMessage or WorldSnapshotMessage or RolesSnapshotMessage
            or RoleKilledMessage or KnockbackMessage or TeleportMessage or PushMessage or FollowMessage
            or SpawnOmenMessage or EndMessage or PingMessage or PeerStatusMessage
            or SetFogHoldMessage or AnnouncementMessage
            or IScenarioReplayStateMessage or IScenarioMidRunUpdateMessage or KickMessage)
        {
            lastHostMessageMs = Environment.TickCount64;
            everHeardFromHost = true;
        }

        switch (message)
        {
            case HelloMessage hello when IsHost:
            {
                if (!Session.Names.ContainsKey(hello.PeerId) && Session.Names.Count >= NetGuard.MaxSessionPeers)
                {
                    DiagnosticLog.Warn($"[Multiplayer] Ignoring Hello from {hello.PeerId} -- roster already holds {NetGuard.MaxSessionPeers} peers.");
                    break;
                }
                peerConnectionIds[hello.PeerId] = senderId;
                peerLastSeenMs[hello.PeerId] = Environment.TickCount64;
                var build = new PeerBuildInfo(NetGuard.Clean(hello.Version), NetGuard.Clean(hello.Checksum));
                Session.Names[hello.PeerId] = NetGuard.Clean(hello.DisplayName);
                Session.Builds[hello.PeerId] = build;
                DiagnosticLog.Info($"[Multiplayer] Hello from {hello.PeerId} ({Session.NameOf(hello.PeerId)}), build {build.Version} ({build.ShortChecksum}), mismatch={IsVersionMismatched(hello.PeerId)}.");
                BroadcastLobbyState();
                break;
            }
            case ClaimRoleMessage claim when IsHost:
                peerLastSeenMs[claim.PeerId] = Environment.TickCount64;
                if (!Enum.IsDefined(claim.Role))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Dropped a claim for role {(int)claim.Role} from {Session.NameOf(claim.PeerId)} -- no such role.");
                    break;
                }
                ApplyClaim(claim.PeerId, claim.Role);
                break;
            case ReleaseRoleMessage release when IsHost:
                peerLastSeenMs[release.PeerId] = Environment.TickCount64;
                ApplyRelease(release.PeerId);
                break;
            case SelfPoseMessage pose when IsHost:
                peerLastSeenMs[pose.PeerId] = Environment.TickCount64;
                OnSelfPoseReceived(pose);
                break;
            case PongMessage pong when IsHost:
                peerLastSeenMs[pong.PeerId] = Environment.TickCount64;
                peerLatencyMs[pong.PeerId] = Environment.TickCount64 - pong.SentAtMs;
                break;
            // Mitigation reports put statuses on the host's own characters: seated peers only,
            // chart ids only, durations and shields clamped.
            case SelfMitigationMessage mit when IsHost:
            {
                if (Session.RoleOf(mit.PeerId) is not { } selfRole) break;
                var previous = peerMitigationStatusIds.GetValueOrDefault(mit.PeerId, []);
                var current = NetGuard.Cap(mit.ActiveMitigationStatusIds, NetGuard.MaxStatusesPerEntity)
                    .Where(TankMitigation.IsKnownTargetSideStatus).ToHashSet();
                var who = Session.NameOf(mit.PeerId);
                foreach (var gained in current.Except(previous))
                    DiagnosticLog.Info($"[Multiplayer] Host: {who} ({selfRole}) reported mitigation status {gained} gained.");
                foreach (var lost in previous.Except(current))
                    DiagnosticLog.Info($"[Multiplayer] Host: {who} ({selfRole}) reported mitigation status {lost} lost.");
                peerMitigationStatusIds[mit.PeerId] = current;
                TankShieldTracker.SetFromPeerReport(selfRole, NetGuard.Clamp(mit.SelfShieldFraction, 0f, 1f));
                break;
            }
            case PeerAppliedEnemyStatusMessage applied when IsHost:
            {
                var who = Session.NameOf(applied.PeerId);
                if (Session.RoleOf(applied.PeerId) is null) break;
                if (!TankMitigation.IsKnownSourceSideStatus(applied.StatusId))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Host: {who} reported enemy status {applied.StatusId}, which is no known mitigation -- dropping.");
                    break;
                }
                var duration = NetGuard.Clamp(applied.Duration, 0f, NetGuard.MaxMitigationSeconds);
                foreach (var netId in NetGuard.Cap(applied.EnemyNetIds, NetGuard.MaxEnemiesPerSnapshot))
                {
                    var enemy = hostEnemyNetIds.FirstOrDefault(kv => kv.Value == netId).Key;
                    if (enemy == null)
                    {
                        DiagnosticLog.Warn($"[Multiplayer] Host: {who} reported status {applied.StatusId} on unknown enemy NetId {netId} -- dropping.");
                        continue;
                    }
                    enemy.AddStatus(applied.StatusId, duration);
                    DiagnosticLog.Info($"[Multiplayer] Host: applied {who}'s reported status {applied.StatusId} (duration={duration:F1}) to enemy NetId {netId}.");
                }
                break;
            }
            case PeerAppliedRoleStatusMessage applied when IsHost:
            {
                var who = Session.NameOf(applied.PeerId);
                if (Session.RoleOf(applied.PeerId) is null) break;
                if (!TankMitigation.IsKnownTargetSideStatus(applied.StatusId))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Host: {who} reported role status {applied.StatusId}, which is no known mitigation -- dropping.");
                    break;
                }
                var duration = NetGuard.Clamp(applied.Duration, 0f, NetGuard.MaxMitigationSeconds);
                var shieldFraction = NetGuard.Clamp(applied.ShieldFraction, 0f, 1f);
                foreach (var role in NetGuard.Cap(applied.Roles, 8).Where(Enum.IsDefined))
                {
                    if (Plugin.GameInstance.World.Party.Get(role) is not { } member)
                    {
                        DiagnosticLog.Warn($"[Multiplayer] Host: {who} reported status {applied.StatusId} on role {role}, but that slot is empty -- dropping.");
                        continue;
                    }
                    member.AddStatus(applied.StatusId, duration);
                    DiagnosticLog.Info($"[Multiplayer] Host: applied {who}'s reported status {applied.StatusId} (duration={duration:F1}) to role {role}.");
                    if (shieldFraction > 0f)
                        TankShieldTracker.Grant(role, shieldFraction, duration);
                }
                break;
            }

            case LobbyStateMessage lobby when !IsHost:
                Session.ApplyLobbyState(lobby);
                ApplyHostScenarioSettings();
                LobbyChanged?.Invoke();
                // A late join or mid-fight rejoin never gets a StartMessage; OnStartReceived is
                // idempotent, so this is safe on a fresh start too.
                if (lobby.Started && MyClaimedRole != null)
                    OnStartReceived();
                break;
            case StartMessage when !IsHost:
                OnStartReceived();
                break;
            case StartCheckMessage when !IsHost:
            {
                var reason = CheckOwnStartReadiness();
                _ = relay?.SendAsync(new StartCheckResponseMessage(MyPeerId, reason == null, reason));
                break;
            }
            case StartCheckResponseMessage resp when IsHost:
                DiagnosticLog.Info($"[Multiplayer] StartCheck reply from {Session.NameOf(resp.PeerId)}: ready={resp.Ready}{(resp.Reason is { } r ? $" ({NetGuard.Clean(r)})" : "")}.");
                // A duplicate/stale reply, or the timeout already gave up on this peer.
                if (pendingStartResponses == null || !pendingStartResponses.Remove(resp.PeerId)) break;
                if (!resp.Ready) startCheckFailures[resp.PeerId] = NetGuard.Clean(resp.Reason) is { Length: > 0 } cleaned ? cleaned : "not ready";
                if (pendingStartResponses.Count == 0) FinishStartCheck();
                break;
            // Ending the run beats simulating around a player who never entered.
            case StartAbortMessage abort when IsHost && running && Session.RoleOf(abort.PeerId) != null:
            {
                var who = Session.NameOf(abort.PeerId);
                // Cleaned as a whole: two peer strings side by side can exceed what a receiver's
                // validator accepts, and then no peer would see the run end.
                var reason = NetGuard.Clean($"{who} couldn't start: {abort.Reason}");
                DiagnosticLog.Warn($"[Multiplayer] {reason} -- ending the run for everyone.");
                if (Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
                BroadcastRunEnded(returnedToInn: true, reason);
                break;
            }
            case WorldSnapshotMessage snap when !IsHost:
                OnWorldSnapshotReceived(snap);
                break;
            case RolesSnapshotMessage rolesSnap when !IsHost:
                OnRolesSnapshotReceived(rolesSnap);
                break;
            case RoleKilledMessage killed when !IsHost:
                OnRoleKilledReceived(killed);
                break;
            case KnockbackMessage kb when !IsHost:
                OnKnockbackReceived(kb);
                break;
            case TeleportMessage teleport when !IsHost:
                OnTeleportReceived(teleport);
                break;
            case PushMessage push when !IsHost:
                OnPushReceived(push);
                break;
            case FollowMessage follow when !IsHost:
                OnFollowReceived(follow);
                break;
            case SpawnOmenMessage omen when !IsHost:
                OnSpawnOmenReceived(omen);
                break;
            case EndMessage end when !IsHost:
                OnEndReceived(end);
                break;
            case PingMessage ping when !IsHost:
                _ = relay?.SendAsync(new PongMessage(MyPeerId, ping.SentAtMs));
                break;
            case PeerStatusMessage status when !IsHost:
                peerStatuses.Clear();
                foreach (var (id, entry) in status.Statuses)
                    peerStatuses[id] = entry;
                break;
            // The host leaving ends the session for everyone; a departing peer only shrinks
            // the roster.
            case SessionEndedMessage ended when ended.PeerId == Session.HostId && isFromHost:
            {
                var who = Session.NameOf(ended.PeerId); // before LeaveSessionInternal wipes Session
                DiagnosticLog.Info($"[Multiplayer] Host {who} left -- session ending for the whole group.");
                // IsInInstance, not running: a Reset clears running while staying in-instance.
                if (Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
                LeaveSessionInternal(notifyOthers: false);
                SessionEndReason = $"{who} left -- session ended.";
                LobbyChanged?.Invoke();
                break;
            }
            case SessionEndedMessage ended when IsHost:
            {
                RemovePeer(ended.PeerId);
                break;
            }
            // Everyone else learns of it from the lobby state broadcast alongside.
            case KickMessage kick when !IsHost && kick.PeerId == MyPeerId:
                DiagnosticLog.Info($"[Multiplayer] {(kick.Banned ? "Banned" : "Removed")} from the session by the host.");
                if (Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
                LeaveSessionInternal(notifyOthers: false);
                SessionEndReason = kick.Banned
                    ? "You were banned from the session by the host."
                    : "You were removed from the session by the host.";
                LobbyChanged?.Invoke();
                break;
            // Only a seated peer can end the run for everyone.
            case ResetRequestMessage req when IsHost && Session.RoleOf(req.PeerId) != null && Plugin.GameInstance.World.Map.IsInInstance:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested a reset.");
                Plugin.GameInstance.Reset();
                break;
            // IsInInstance, not running: Leave must still work after a Reset. Answered even with
            // nothing to end, or a peer that missed the end of a run waits on its Leave forever.
            case LeaveRequestMessage req when IsHost && Session.RoleOf(req.PeerId) != null:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested to leave the instance.");
                if (running || Plugin.GameInstance.World.Map.IsInInstance)
                {
                    if (Plugin.GameInstance.World.Map.IsInInstance)
                        Plugin.GameInstance.Leave();
                    BroadcastRunEnded(returnedToInn: true);
                }
                // A bare EndMessage, at most once a second, so repeated requests can't make the
                // host rebroadcast its whole end-of-run sequence.
                else if (Environment.TickCount64 - lastIdleEndSentMs >= 1000)
                {
                    lastIdleEndSentMs = Environment.TickCount64;
                    _ = relay?.SendAsync(new EndMessage(ReturnedToInn: true, Reason: null));
                }
                break;
            // Can't loop into a re-broadcast: the map-event handlers gate on IsHost.
            case MapEffectMessage effect when PeerInRun:
                DiagnosticLog.Info($"[Multiplayer] Peer: applying MapEffect packetFlags=0x{effect.PacketFlags:X8} index=0x{effect.Index:X}.");
                Plugin.GameInstance.World.Map.AddEffect(effect.PacketFlags, effect.Index);
                break;
            case MapDirectorUpdateMessage directorUpdate when PeerInRun:
                if (!MapController.IsReplayableDirectorCategory(directorUpdate.Category))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Dropped MapDirectorUpdate category=0x{directorUpdate.Category:X8} -- no scenario uses it.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: applying MapDirectorUpdate category=0x{directorUpdate.Category:X8}.");
                Plugin.GameInstance.World.Map.DirectorUpdate(
                    directorUpdate.Category, directorUpdate.Arg1, directorUpdate.Arg2,
                    directorUpdate.Arg3, directorUpdate.Arg4, directorUpdate.Arg5, directorUpdate.Arg6);
                break;
            case SetWeatherMessage weather when PeerInRun:
                if (!WeatherSheet.HasRow(weather.WeatherId))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Dropped SetWeather weatherId={weather.WeatherId} -- no such Weather row.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: applying SetWeather weatherId={weather.WeatherId} transition={weather.Transition}.");
                Plugin.GameInstance.World.Map.SetWeather(weather.WeatherId, NetGuard.Clamp(weather.Transition, 0f, 60f, 0.5f));
                break;
            case SetFogHoldMessage fog when PeerInRun:
                DiagnosticLog.Info($"[Multiplayer] Peer: applying SetFogHold {(fog.FogHold is { } value ? value.ToString("F0") : "off")}.");
                Plugin.GameInstance.World.Map.SetFogHold(fog.FogHold is { } hold ? NetGuard.Clamp(hold, 0f, 100_000f) : null);
                break;
            case AnnouncementMessage announcement when PeerInRun:
                Plugin.GameInstance.World.Announce(NetGuard.Clean(announcement.Text));
                break;
            // Every IMultiplayerReplayable scenario routes through these two cases.
            case MpMessage genericMsg when !IsHost && genericMsg is IScenarioReplayStateMessage:
                pendingGenericReplayState = genericMsg;
                TryStartDebugBotReplay();
                break;
            // Without a shadow state yet, nothing needs the update.
            case MpMessage midRunUpdate when !IsHost && midRunUpdate is IScenarioMidRunUpdateMessage:
                if (debugShadowStateGeneric != null && TryResolveScenario() is IMultiplayerReplayable replayable)
                    replayable.ApplyMidRunUpdate(debugShadowStateGeneric, midRunUpdate);
                break;
        }
    }
}
