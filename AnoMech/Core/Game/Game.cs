using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Top.P2PartySynergy;
using AnoMech.Scenarios.Top.P5Delta;
using AnoMech.Scenarios.Top.P5Omega;
using AnoMech.Scenarios.Top.P5Sigma;
using AnoMech.Scenarios.Top.P6WaveCannon2;
using AnoMech.Scenarios.Umad;
using AnoMech.Scenarios.Umad.P1TeleTrouncing;
using AnoMech.Scenarios.Umad.P2Forsaken;
using AnoMech.Scenarios.Umad.P3BlackHole;
using AnoMech.Scenarios.Umad.P3LimitCut;
using AnoMech.Scenarios.Umad.P4KefkaSays;
using AnoMech.Scenarios.Umad.P5Exaflares;
using AnoMech.Scenarios.Umad.P5Flood;
using AnoMech.Scenarios.Uwu.UltimatePredation;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AnoMech.Core.Game;

// High-level orchestrator: owns the World, holds the scenario catalog, drives
// the active scenario's lifecycle, and is the single entry point UI talks to.
public sealed class Game : IDisposable
{
    // EventObj for the duty Exit portal — hidden on every scenario start so the
    // teleport-out interactable doesn't sit inside the simulated arena.
    private const uint ExitObjectBaseId = 2000139;

    public EventScheduler Events { get; } = new();
    public SimWorld World { get; }
    public SimPlayer? Player => World.Party.Player;
    // Null once Reset/Leave clears it -- MultiplayerManager's host-side tick reads
    // this to stop broadcasting once the multiplayer run has ended locally.
    public IScenario? ActiveScenario => activeScenario;
    // Flat registry; the zone -> phase -> scenario tree is derived from it in
    // first-appearance order.
    public IReadOnlyList<IScenario> Scenarios { get; }
    public IReadOnlyList<IZone> Zones { get; }
    private readonly Dictionary<IZone, List<IPhase>> phasesByZone = new();
    private readonly Dictionary<IPhase, List<IScenario>> scenariosByPhase = new();
    public Bgm Bgm { get; } = new();

    // Fixed scenario-local player spawn (16y south of centre).
    public static readonly Vector3 PlayerSpawnLocal = new(0f, 0f, 16f);

    // Multiplier applied only to the EventScheduler's delta. Intentionally does not
    // scale enemy/party/tether/status ticks so cast bars, animations, and movement
    // run at real time — only the timeline of scheduled events stretches/compresses.
    public float EventTimeScale { get; set; } = 1f;

    // Set by Game.Kill once the post-first-death freeze timer fires. While true,
    // Tick is a no-op so scenario events, scheduler, and world all stop.
    public bool Paused { get; set; }

    // When true, Game.Kill still posts the chat line for learning but skips every
    // gameplay side effect (HP=0, KO timeline, stun hooks, freeze timer).
    public bool GodMode { get; set; }

    private IScenario? activeScenario;
    private float scenarioElapsed;
    // The phase of the last run in the loaded zone, host and peer alike (activeScenario is
    // host-only and cleared by a Reset).
    private IPhase? lastPhase;
    private bool firstDeathScheduled;
    private bool firstFreezeScheduled;
    private readonly OpcodeUpdater opcodeUpdater;

#if DEBUG
    // A run where nobody dies but something went wrong needs the same trace as the auto-freeze.
    private const float PeriodicDumpInterval = 3f;
    private float periodicDumpTimer;
#endif

    public Game()
    {
        World = new SimWorld(Events);
        opcodeUpdater = new OpcodeUpdater();
        Scenarios = new IScenario[]
        {
            new UmadP1TeleTrouncingScenario(),
            new UmadP2ForsakenScenario(),
            new UmadP3BlackHoleScenario(),
            new UmadP3LimitCutScenario(),
            new UmadP4KefkaSaysScenario(),
            new UmadP5ExaflaresScenario(),
            new UmadP5FloodScenario(),
            new UmadP5ForsakenNull(),
            new TopP2PartySynergyScenario(),
            new TopP5DeltaScenario(),
            new TopP5SigmaScenario(),
            new TopP5OmegaScenario(),
            new TopP6WaveCannon2Scenario(),
            new UltimatePredationScenario()
        };

        // Derive the zone tree from the flat registry (first-appearance order).
        var zoneOrder = new List<IZone>();
        foreach (var scenario in Scenarios)
        {
            var phase = scenario.Phase;
            var zone = phase.Zone;
            if (!phasesByZone.TryGetValue(zone, out var phases))
            {
                phases = new List<IPhase>();
                phasesByZone[zone] = phases;
                zoneOrder.Add(zone);
            }
            if (!phases.Contains(phase)) phases.Add(phase);
            if (!scenariosByPhase.TryGetValue(phase, out var phaseScenarios))
            {
                phaseScenarios = new List<IScenario>();
                scenariosByPhase[phase] = phaseScenarios;
            }
            phaseScenarios.Add(scenario);
        }
        Zones = zoneOrder;
    }

    // Derived zone-tree accessors, in registry order.
    public IReadOnlyList<IPhase> PhasesOf(IZone zone) => phasesByZone[zone];
    public IReadOnlyList<IScenario> ScenariosOf(IPhase phase) => scenariosByPhase[phase];

    // selectedAi: index into the scenario's AiStrats of the strat to run, or null for
    // solo (no doppels, no AI). Defaults to 0 = run the first strat with a full party.
    // selectedWaymark: index into the scenario's WaymarkPresets; ignored when it has none.
    public void RunScenario(IScenario scenario, PartyRole? roleOverride = null, int? selectedAi = 0, int selectedWaymark = 0)
    {
        Plugin.Framework.Run(() => { RunScenarioInternal(scenario, roleOverride, selectedAi, selectedWaymark, null, null, isPeer: false); });
    }

    // Multiplayer host: RunScenario with `networkRoles` spawned as SimNetworkPuppet, named
    // after their players (`networkNames`).
    public void RunScenarioAsHost(IScenario scenario, PartyRole roleOverride, int selectedAi, int selectedWaymark, IReadOnlySet<PartyRole> networkRoles, IReadOnlyDictionary<PartyRole, string> networkNames, Action<string?> resolved)
        => RunResolved(() => RunScenarioInternal(scenario, roleOverride, selectedAi, selectedWaymark, networkRoles, networkNames, isPeer: false), resolved);

    // Multiplayer peer: same zone/party/waymarks, but never zone/phase/scenario.Run; every
    // other slot is a puppet driven by the host's snapshots.
    public void RunScenarioAsPeer(IScenario scenario, PartyRole roleOverride, int selectedWaymark, IReadOnlySet<PartyRole> networkRoles, IReadOnlyDictionary<PartyRole, string> networkNames, Action<string?> resolved)
        => RunResolved(() => RunScenarioInternal(scenario, roleOverride, null, selectedWaymark, networkRoles, networkNames, isPeer: true), resolved);

    // `resolved` runs in the same deferred callback as the start, with why it was refused, or
    // null once the run is up; an exception counts as a refusal.
    private static void RunResolved(Func<string?> start, Action<string?> resolved)
    {
        Plugin.Framework.Run(() =>
        {
            string? refusal;
            try
            {
                refusal = start();
            }
            catch (Exception e)
            {
                resolved($"the scenario threw {e.GetType().Name} while loading");
                throw;
            }
            resolved(refusal);
        });
    }

    // Raised when Kill actually takes a slot down; the host broadcasts RoleKilled from it. A
    // peer's own Kill calls are reactions to a received RoleKilled, so nothing echoes.
    public event Action<PartyRole, string>? PartyMemberKilled;

    // The selected preset, or [0] as the default.
    private static IReadOnlyList<Waymark> ResolveWaymarks(IZone zone, int selectedWaymark)
    {
        var presets = zone.WaymarkPresets;
        if (selectedWaymark >= 0 && selectedWaymark < presets.Count)
            return presets[selectedWaymark].Markers;
        return presets[0].Markers;
    }

    // Null once the run is up, else why it was refused.
    private string? RunScenarioInternal(IScenario scenario, PartyRole? roleOverride, int? selectedAi, int selectedWaymark, IReadOnlySet<PartyRole>? networkRoles, IReadOnlyDictionary<PartyRole, string>? networkNames, bool isPeer)
    {
        var solo = selectedAi is null;
        var phase = scenario.Phase;
        var zone = phase.Zone;
        // Hard gate: scenarios are only ever run from an inn. Everything
        // downstream (CharacterManager registration, zone load, doppel spawn)
        // assumes that invariant.
        if (!ZoneSession.IsInInn())
        {
            Plugin.Log.Warning("Game: scenarios can only run from an inn; aborting.");
            return "not in an inn";
        }

        // Per-player settings the fight can't produce together. Empty for a peer and for solo,
        // so only a host's own setup is refused here; this is the funnel every entry point
        // (both windows, /ano start) passes through.
        if (scenario.SettingsConflicts is { Count: > 0 } conflicts)
        {
            foreach (var conflict in conflicts)
                Plugin.Log.Warning($"Game: refusing to start {scenario.Name} -- {conflict}");
            return "impossible scenario settings";
        }

        ResetInternal();

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            Plugin.Log.Warning("Game: no local player; aborting scenario start");
            return "no local player";
        }

#if DEBUG
        // Here rather than in scenario.Run (which peers skip), and before TryLoad so its
        // freshLoad line survives into the dump.
        AnoMech.Core.DiagnosticLog.Clear();
#endif

        // Captured before TryLoad: false only on the first start from the inn (a true
        // zone entry), true for any restart/switch within the already-loaded zone.
        var freshLoad = !World.Map.IsZoneLoaded;
        if (!freshLoad && lastPhase != phase) World.Map.RestoreSuppressedArenaSlots();

        World.HideObject(ExitObjectBaseId);
        World.Map.TryLoad(
            new TargetInstance(zone.TerritoryId, zone.Origin, zone.Origin + PlayerSpawnLocal, phase.Weather, phase.FogHold),
            zone.Level, zone.ItemLevel);
        lastPhase = phase;
        World.ScenarioOrigin = zone.Origin;
        World.Map.ArmColliderDrops(zone.ColliderRemovalPoints.Select(World.Coordinates.ToGlobal));
        World.PlaceWaymarks(ResolveWaymarks(zone, selectedWaymark));
        World.CreateParty(player.ClassJob.RowId, scenario.TankMaxHealth, roleOverride, solo, networkRoles, networkNames);
        // Client-asset setup a peer needs too (see IZone.RunClientSetup).
        zone.RunClientSetup(World);
        phase.RunClientSetup(World);
        // A peer runs no scenario logic; zone.Run also creates the arena boundary the
        // out-of-arena check below reads, so that check no-ops for a peer too.
        if (!isPeer)
        {
            // Log-and-rethrow, so the exception reaches our own log too.
            try
            {
                zone.Run(World);
                phase.Run(World);
                scenario.Run(World, selectedAi);
            }
            catch (Exception e)
            {
                AnoMech.Core.DiagnosticLog.Warn($"[Game.RunScenarioInternal] zone/phase/scenario.Run threw -- scenario load aborted here: {e}");
                throw;
            }
        }
        // Both host and peer: RunInstanceEvents carries no RNG/AI/DamageSolver dependency.
        scenario.RunInstanceEvents(World);
        // Entering the zone always starts at spawn; a restart only recenters the player
        // if they're standing outside the arena ring (otherwise they keep their position).
        if (freshLoad)
            TeleportPlayerToSpawn();
        else
            TeleportPlayerToSpawnIfOutsideArena();
        ResetSprintCooldown();
        if (!isPeer)
        {
            activeScenario = scenario;
            scenarioElapsed = 0f;
        }

        // Reconcile BGM to the new scenario. Bgm.Play is idempotent, so switching
        // between same-track scenarios (e.g. the P5 phases) keeps playing without
        // restarting the song; a different track swaps; suppressed/no-track reverts.
        if (Plugin.Config.SuppressBgm || phase.Bgm == 0)
            Bgm.Reset();
        else
            Bgm.Play(phase.Bgm);

        Plugin.ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.SystemMessage,
            // networkRoles null, not solo: a peer passes selectedAi null too.
            Message = new SeStringBuilder().AddText($"[AnoMech] Starting: {FullName(scenario)}{(networkRoles is null ? " (Solo)" : "")}").Build(),
        });
        return null;
    }

    // Sprint goes on cooldown when the player presses it inside a scenario
    // (LocalPlayerInputHooks lets Original run so the recast starts). Clear it
    // here so each scenario starts with Sprint ready, regardless of whether
    // the player pressed it just before clicking Start.
    private static unsafe void ResetSprintCooldown()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        var group = am->GetRecastGroup((int)ActionType.Action, LocalPlayerInputHooks.SprintActionId);
        if (group < 0) return;
        var detail = am->GetRecastGroupDetail(group);
        if (detail == null) return;
        detail->IsActive = false;
        detail->Elapsed = 0f;
    }

    // Undoes LocalPlayerInputHooks.ForceRecastSweep's fake cooldown display on every
    // intercepted mitigation -- the real recast group never actually started, so this just
    // clears our own fake sweep (same as ResetSprintCooldown above).
    private static unsafe void ClearFakedTankMitigationCooldowns()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        foreach (var ability in TankMitigation.ByActionId.Values)
        {
            var group = am->GetRecastGroup((int)ActionType.Action, ability.ActionId);
            if (group < 0) continue;
            var detail = am->GetRecastGroupDetail(group);
            if (detail == null) continue;
            detail->IsActive = false;
            detail->Elapsed = 0f;
        }
    }

    public void Tick(float deltaSeconds)
    {
        if (Paused) return;
        Events.Tick(deltaSeconds * EventTimeScale);
        World.Tick(deltaSeconds);
        // A peer's own tick would fight OnRolesSnapshotReceived's HP writes.
        if (Plugin.MultiplayerInstance is not { IsHost: false })
            TankHpRegen.Tick(World.Party, deltaSeconds);
        if (activeScenario != null)
        {
            scenarioElapsed += deltaSeconds;
            activeScenario.Tick(deltaSeconds, scenarioElapsed);
        }
#if DEBUG
        // Gated so an idle client doesn't spam empty snapshots into the size-capped log.
        if (activeScenario != null || (Plugin.MultiplayerInstance?.IsRunning ?? false))
        {
            periodicDumpTimer += deltaSeconds;
            if (periodicDumpTimer >= PeriodicDumpInterval)
            {
                periodicDumpTimer = 0f;
                AnoMech.Windows.DamageDebugWindow.Instance?.DumpToFile();
            }
        }
        else
        {
            periodicDumpTimer = 0f;
        }
#endif
    }

    // Godmode preview: how long a swallowed-death HP-bar drop stays down before healing back.
    private const float GodmodeHealSeconds = 1.2f;

    // Single entry point for "this character died". Always posts the cause
    // to chat and, on the first call of a run, fires the on-screen overlay
    // — both happen even in godmode so the user can learn what would have
    // killed them. Gameplay side effects (OnKilled, which flips Dead, plus
    // the 5s freeze) only run outside godmode; the freeze fires once per run
    // on the first non-godmode death.
    //
    // Returns true only when the member actually went down (OnKilled ran):
    // false when it was already dead, invulnerable (GiveInvuln), or godmode
    // swallowed it. Callers that run extra on-death logic should gate on this
    // so an invuln'd/godmode'd "death" doesn't trigger gameplay consequences.
    public bool Kill(ISimPartyMember target, string cause)
    {
        if (target == null) return false;
        if (target.Dead) return false;
        // ActiveStatusSnapshot, not the native StatusManager: AddStatus writes through our list.
        if (target is SimCharacter sc && sc.ActiveStatusSnapshot.Any(s => TankMitigation.IsInvuln(s.StatusId)))
        {
            Plugin.Log.Info($"[Invuln] {DescribeName(target)} survived: {cause}");
            AnoMech.Core.DiagnosticLog.Info($"[Game] Kill: {target.Role} survived via Invuln -- {cause}");
            return false;
        }

        AnoMech.Core.DiagnosticLog.Warn(
            $"[Game] Kill: {target.Role} died at ({(target as IPositioned)?.Position.X:F1},{(target as IPositioned)?.Position.Z:F1}) -- {cause}");
        PrintDeath(target, cause);
        if (!firstDeathScheduled)
        {
            firstDeathScheduled = true;
            ShowFirstDeathOverlay(target, cause);
        }

        if (GodMode)
        {
            // Godmode still previews the death: drop the player's bar and heal it back a beat later.
            if (target is SimPlayer player)
            {
                player.DropHpBar();
                Events.Add(GodmodeHealSeconds, player.RestoreHpBar);
            }
            return false;
        }
        target.OnKilled();
        PartyMemberKilled?.Invoke(target.Role, cause);
        if (!firstFreezeScheduled)
        {
            firstFreezeScheduled = true;
#if DEBUG
            AnoMech.Windows.DamageDebugWindow.Instance?.Freeze();
#endif
            Events.Add(5f, () => Paused = true);
        }
        return true;
    }

    private static void PrintDeath(ISimPartyMember target, string cause)
    {
        Plugin.ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.SystemMessage,
            Message = new SeStringBuilder().AddText($"[AnoMech] {DescribeName(target)} died: {cause}").Build(),
        });
    }

    private static string DescribeName(ISimPartyMember target) => target switch
    {
        SimPlayer => "You",
        SimPartyNpc pm => pm.DisplayName,
        SimNetworkPuppet pm => pm.DisplayName,
        _ => "Character",
    };

    private static unsafe void ShowFirstDeathOverlay(ISimPartyMember target, string cause)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        ui->ShowErrorText($"{DescribeName(target)} died: {cause}", true);
    }

    public void Reset() => Plugin.Framework.Run(() =>
    {
        if (activeScenario is not null)
            TeleportPlayerToSpawnIfOutsideArena();
        ResetInternal();
        Bgm.Reset();
    });

    // Pull the player back to the scenario's spawn point only if they're standing
    // outside the arena ring (e.g. knocked out of bounds, or wandered off). No-op
    // when the scenario enforces no boundary. Reads the live game-object position:
    // at scenario start the SimPlayer was just created and hasn't ticked, so its
    // cached Position is still zero. At reset this must run before ResetInternal
    // clears Party / ScenarioOrigin.
    private void TeleportPlayerToSpawnIfOutsideArena()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        if (!World.IsOutsideArena(World.Coordinates.ToLocal(lp.Position))) return;
        TeleportPlayerToSpawn();
    }

    // ScenarioOrigin must already be set (SetPosition resolves local -> world through it).
    private void TeleportPlayerToSpawn() => Player?.SetPosition(PlayerSpawnLocal);

    // Menu label, e.g. "P5 Delta".
    public static string DisplayName(IScenario scenario)
    {
        var phase = scenario.Phase;
        return string.IsNullOrEmpty(phase.Name) ? scenario.Name : $"{phase.Name} {scenario.Name}";
    }

    public static string FullName(IScenario scenario)
        => $"{scenario.Phase.Zone.Name} — {DisplayName(scenario)}";

    // Leave returns to the inn. Only meaningful when IsInInstance is true.
    // Resets the encounter first, then reverts the zone — Reset stays in-zone.
    public void Leave()
    {
        // Leaving always finalizes its own log segment.
        AnoMech.Core.DiagnosticLog.RotateNow();
        Plugin.Framework.Run(() =>
        {
            ResetInternal();
            Bgm.Reset();
            World.Map.Unload();
        });
    }

    private void ResetInternal()
    {
        activeScenario = null;
        scenarioElapsed = 0f;
        Events.Clear();
        World.Despawn();
        // A wipe or Leave never reaches the scenario's own cleanup.
        Core.Native.VfxSpawnLog.Disable();
        // Sim-only bookkeeping; ClearAllVisuals also undoes the native ShieldValue byte.
        TankMitigationTracker.Reset();
        ClearFakedTankMitigationCooldowns();
        Plugin.PlayerInputHooks.RestoreGaugeIllusion();
        TankShieldTracker.Reset();
        TankShieldTracker.ClearAllVisuals(World.Party);
        TankHpRegen.Reset();
        // BGM is the callers': resetting here would restart a same-track scenario switch.

        Paused = false;
        firstDeathScheduled = false;
        firstFreezeScheduled = false;
#if DEBUG
        periodicDumpTimer = 0f;
        AnoMech.Windows.DamageDebugWindow.Instance?.ResetFreeze();
#endif
    }

    // Synchronous: Plugin.Dispose runs on the framework thread during unload, and a
    // Framework.Run wrapper would never fire.
    public void Dispose()
    {
        activeScenario = null;
        Events.Clear();
        Bgm.Dispose();
        World.Dispose();
        opcodeUpdater.Dispose();
    }
}
