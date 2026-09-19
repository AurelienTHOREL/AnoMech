using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AnoMech.Core.Map;

// The firewall cuts both directions for the whole stay, so anything the server does to the
// character meanwhile (a Teleport cast before it went up, a duty pop) leaves client and server
// disagreeing about where the character is, and the inn reload would then put the client in a
// zone the server doesn't have it in. This part refuses a start the server could still act on,
// watches the stay for the client-side signs of such a move, and kills the game rather than lift
// the firewall on one: a dead client sends nothing, and the next login starts from the server's
// truth.
public sealed unsafe partial class ZoneSession
{
    // ---- Start gate ---------------------------------------------------------------------

    // The one instance, for the events Plugin forwards.
    internal static ZoneSession? Current { get; private set; }

    private const uint TeleportActionId = 5;
    private const uint ReturnActionId = 6;
    // A Teleport/Return press blocks Start until its cast is seen interrupted, the zone changes,
    // or this passes (the server refused it). A press the client refused shows no cast at all.
    private const double ZoneChangeHoldSeconds = 20;
    private const double NoCastGraceSeconds = 1.5;
    private const float InterruptedBelowProgress = 0.9f;
    // The server may still be acting on whatever the player was just doing (an event's zone
    // change lands after the event ends), and a zone just entered is still being synced.
    private const double SettleSeconds = 3;

    private static long? zoneChangePressedAt;
    private static uint zoneChangeActionId;
    private static bool zoneChangeCastSeen;
    private static float zoneChangeCastProgress;
    private static long? lastTerritoryChangeAt;
    private static long? lastBusyAt;

    public static void NoteActionPressed(ActionType type, uint actionId)
    {
        if (type != ActionType.Action || actionId is not (TeleportActionId or ReturnActionId)) return;
        zoneChangePressedAt = Stopwatch.GetTimestamp();
        zoneChangeActionId = actionId;
        zoneChangeCastSeen = false;
        zoneChangeCastProgress = 0f;
        DiagnosticLog.Info($"[ZoneGuard] {ActionLookup.Name(actionId)} pressed -- Start is blocked until it resolves.");
    }

    public static void NoteTerritoryChanged(uint territory)
    {
        lastTerritoryChangeAt = Stopwatch.GetTimestamp();
        if (zoneChangePressedAt != null)
            DiagnosticLog.Info($"[ZoneGuard] Zone changed to {territory} -- the pending {ActionLookup.Name(zoneChangeActionId)} resolved.");
        zoneChangePressedAt = null;
        Current?.GuardTerritoryChanged(territory);
    }

    public static void NoteLogout(int type, int code) => Current?.GuardLogout(type, code);

    // Every frame, from Plugin: the gate's timers run outside a session too.
    public static void TickGuard()
    {
        if (IsServerActingSoon()) lastBusyAt = Stopwatch.GetTimestamp();
        TickZoneChangeLatch();
        Current?.TickSessionGuard();
    }

    // The busy states whose outcome the server delivers after they end (an event's zone change,
    // a queue pop, a cast's effect), as opposed to ones that only occupy the player.
    private static bool IsServerActingSoon()
    {
        var c = Plugin.Condition;
        return c[ConditionFlag.Casting]
            || c[ConditionFlag.Casting87]
            || c[ConditionFlag.BetweenAreas]
            || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.Occupied]
            || c[ConditionFlag.Occupied30]
            || c[ConditionFlag.Occupied33]
            || c[ConditionFlag.Occupied38]
            || c[ConditionFlag.Occupied39]
            || c[ConditionFlag.OccupiedInEvent]
            || c[ConditionFlag.OccupiedInQuestEvent]
            || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.OccupiedSummoningBell]
            || c[ConditionFlag.WatchingCutscene]
            || c[ConditionFlag.WatchingCutscene78]
            || c[ConditionFlag.TradeOpen]
            || c[ConditionFlag.LoggingOut]
            || c[ConditionFlag.SystemError]
            || c[ConditionFlag.WaitingForDuty]
            || c[ConditionFlag.WaitingForDutyFinder]
            || c[ConditionFlag.InDutyQueue]
            || c[ConditionFlag.ReadyingVisitOtherWorld]
            || c[ConditionFlag.WaitingToVisitOtherWorld];
    }

    private static void TickZoneChangeLatch()
    {
        if (zoneChangePressedAt is not { } pressedAt) return;
        var elapsed = Stopwatch.GetElapsedTime(pressedAt).TotalSeconds;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is { IsCasting: true } && player.CastActionId == zoneChangeActionId)
        {
            zoneChangeCastSeen = true;
            zoneChangeCastProgress = player.TotalCastTime > 0f ? player.CurrentCastTime / player.TotalCastTime : 0f;
            return;
        }
        string? release = null;
        if (zoneChangeCastSeen && zoneChangeCastProgress < InterruptedBelowProgress)
            release = $"its cast was interrupted at {zoneChangeCastProgress:P0}";
        else if (!zoneChangeCastSeen && elapsed > NoCastGraceSeconds)
            release = "no cast followed the press";
        else if (elapsed > ZoneChangeHoldSeconds)
            release = $"{ZoneChangeHoldSeconds:F0}s passed with no zone change";
        if (release == null) return;
        DiagnosticLog.Info($"[ZoneGuard] {ActionLookup.Name(zoneChangeActionId)} hold released: {release}.");
        zoneChangePressedAt = null;
    }

    private static double SecondsSince(long? stamp) => stamp is { } s ? Stopwatch.GetElapsedTime(s).TotalSeconds : double.PositiveInfinity;

    // Null when a scenario may start now, otherwise why not. Every start path ends in Enter,
    // which asks again, so a change between the click and the deferred start is caught too. A
    // restart inside a loaded zone changes nothing the server can see, so only a tripped guard
    // refuses it.
    public static string? StartBlockedReason()
    {
        if (Current is { IsActive: true } active)
            return active.tripReason is { } tripped ? $"the session guard tripped ({tripped})" : null;
        // The previous stay's delayed lift is still pending; a new stay under it would lose its
        // firewall a second in.
        if (Current is { guardArmed: true }) return "the previous run is still settling -- wait a moment";
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer is not { } player) return "not logged in";
        if (!IsInInn()) return "not in an inn";
        var c = Plugin.Condition;
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]) return "zoning";
        if (SecondsSince(lastTerritoryChangeAt) < SettleSeconds) return "the zone just loaded -- wait a moment";
        if (player.IsCasting) return $"casting {ActionLookup.Name(player.CastActionId)}";
        if (zoneChangePressedAt is { } pressed)
            return $"{ActionLookup.Name(zoneChangeActionId)} was used {Stopwatch.GetElapsedTime(pressed).TotalSeconds:F0}s ago and has not resolved";
        if (IsPlayerBusy()) return "busy (cutscene, NPC event, crafting, trading, zoning, combat, mounted, queued, etc.)";
        if (SecondsSince(lastBusyAt) < SettleSeconds) return "the last action is still settling -- wait a moment";
        return null;
    }

    // ---- Session guard ------------------------------------------------------------------

    private bool guardArmed;
    private long guardArmedAt;
    private int stayId;
    private string? tripReason;
    // Dalamud's territory follows real zone-ins only (the sim's own loads leave it on the inn),
    // so anything else is one. GameMain's reads 0 after the sim's load, so it is only logged.
    private uint innClientTerritory;
    private uint loadedTerritory;
    private uint lastNativeTerritory;
    // What the firewall held, for the stay summary.
    private long heldInbound;
    private readonly Dictionary<ushort, long> heldOutbound = new();

    private static uint NativeTerritory()
    {
        var gm = GameMain.Instance();
        return gm == null ? 0 : gm->CurrentTerritoryTypeId;
    }

    private string? TerritoryDrift()
    {
        var client = Plugin.ClientState.TerritoryType;
        if (client != innClientTerritory && client != loadedTerritory)
            return $"the client's territory reads {client}, neither the inn ({innClientTerritory}) nor the loaded zone ({loadedTerritory})";
        return null;
    }

    private void LogNativeTerritoryChange()
    {
        var native = NativeTerritory();
        if (native == lastNativeTerritory) return;
        DiagnosticLog.Info($"[ZoneGuard] GameMain territory {lastNativeTerritory} -> {native}, {Stopwatch.GetElapsedTime(guardArmedAt).TotalSeconds:F2}s into the stay (logged only).");
        lastNativeTerritory = native;
    }

    // Enter refuses a start whose firewall is not actually up; a hook that failed to install
    // after a patch would otherwise arm nothing and load the zone anyway.
    private bool FirewallArmed(out string why)
    {
        if (!sendPacketHook.IsEnabled || sendPacketHook.IsDisposed) { why = "the send filter did not arm"; return false; }
        if (!receivePacketHook.IsEnabled || receivePacketHook.IsDisposed) { why = "the receive filter did not arm"; return false; }
        if (heartbeatOpcode == 0) { why = "no heartbeat opcode"; return false; }
        why = "";
        return true;
    }

    private void CaptureInnState()
    {
        innClientTerritory = Plugin.ClientState.TerritoryType;
        lastNativeTerritory = NativeTerritory();
    }

    private void ArmGuard(uint territoryId)
    {
        loadedTerritory = territoryId;
        tripReason = null;
        heldInbound = 0;
        heldOutbound.Clear();
        guardArmedAt = Stopwatch.GetTimestamp();
        stayId++;
        guardArmed = true;
        DiagnosticLog.Info($"[ZoneGuard] Armed for territory {territoryId}: inn territory {innClientTerritory}, Dalamud now reads {Plugin.ClientState.TerritoryType}, GameMain {lastNativeTerritory} -> {NativeTerritory()}.");
        lastNativeTerritory = NativeTerritory();
    }

    private void TickSessionGuard()
    {
        if (!guardArmed || tripReason != null) return;
        LogNativeTerritoryChange();
        var c = Plugin.Condition;
        if (!sendPacketHook.IsEnabled || !receivePacketHook.IsEnabled)
            Trip("a firewall hook was found disabled while armed");
        else if (!Plugin.ClientState.IsLoggedIn)
            Trip("the client logged out while the firewall was up");
        else if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51])
            Trip("the client began a zone transition while the firewall was up");
        else if (c[ConditionFlag.LoggingOut])
            Trip("a logout began while the firewall was up");
        else if (TerritoryDrift() is { } drift)
            Trip($"{drift} while the firewall was up");
        else if (Plugin.ObjectTable.LocalPlayer is { IsCasting: true } player
                 && player.CastActionId is TeleportActionId or ReturnActionId
                 && player.CurrentCastTime > Stopwatch.GetElapsedTime(guardArmedAt).TotalSeconds + 0.05)
            Trip($"a {ActionLookup.Name(player.CastActionId)} cast begun before the firewall went up is completing on the server");
    }

    private void GuardTerritoryChanged(uint territory)
    {
        if (!guardArmed || tripReason != null || territory == innClientTerritory || territory == loadedTerritory) return;
        Trip($"the client processed a zone change to {territory} while the firewall was up");
    }

    private void GuardLogout(int type, int code)
    {
        if (!guardArmed || tripReason != null) return;
        Trip($"logout (type {type}, code {code}) while the firewall was up");
    }

    // Sticky, and fatal at once: none of the causes clears on its own, and the stay would only
    // drift further from the server.
    private void Trip(string reason)
    {
        tripReason = reason;
        Die(reason);
    }

    // Dalamud's territory must read the inn again here: the reload has run, and only a real
    // zone-in moves that reading.
    private string? LiftBlockedReason()
    {
        if (tripReason is { } tripped) return tripped;
        if (!sendPacketHook.IsEnabled || !receivePacketHook.IsEnabled) return "a firewall hook was found disabled";
        if (!Plugin.ClientState.IsLoggedIn) return "not logged in";
        var c = Plugin.Condition;
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]) return "a zone transition is in progress";
        if (c[ConditionFlag.LoggingOut]) return "logging out";
        if (Plugin.ClientState.TerritoryType != innClientTerritory)
            return $"the client reports territory {Plugin.ClientState.TerritoryType}, not the inn ({innClientTerritory}) the firewall was armed in";
        return TerritoryDrift();
    }

    // Once per stay: Dispose lifts an early one, and the delayed lift then finds nothing to do.
    private void LiftFirewallOrDie(string when)
    {
        if (!guardArmed) return;
        if (LiftBlockedReason() is { } reason) Die($"{reason} ({when})");
        guardArmed = false;
        DisableFirewall();
        DiagnosticLog.Info($"[ZoneGuard] Firewall lifted {when}: back in territory {innClientTerritory} as the server left it (GameMain reads {NativeTerritory()}).");
    }

    private void LogStaySummary()
    {
        var top = string.Join(", ", heldOutbound.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"0x{kv.Key:X4}x{kv.Value}"));
        DiagnosticLog.Info($"[ZoneGuard] Stay held {heldInbound} inbound and {heldOutbound.Values.Sum()} outbound packets (outbound top: {top}).");
    }

    // ---- The stop -------------------------------------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private const uint MessageBoxErrorOnTop = 0x10 | 0x1000 | 0x10000 | 0x40000; // ICONERROR, SYSTEMMODAL, SETFOREGROUND, TOPMOST

    // Environment.FailFast runs no finalizers, so nothing on the way out (Plugin.Dispose
    // included) can lift the firewall.
    private static void Die(string reason)
    {
        var note = DiagnosticLog.Fatal($"[ZoneGuard] FATAL: {reason} -- stopping the game with the firewall up.");
        var text = "AnoMech stopped the game on purpose.\n\n"
                   + $"Reason: {reason}.\n\n"
                   + "The packet filter was still up, so nothing was sent to the server. Log in again; your character will be wherever the server has it.\n\n"
                   + (note != null ? $"Details: {note}" : "See dalamud.log.");
        try { MessageBoxW(0, text, "AnoMech safety stop", MessageBoxErrorOnTop); }
        catch { /* the stop must not depend on the dialog */ }
        Environment.FailFast($"AnoMech safety stop: {reason}");
    }
}
