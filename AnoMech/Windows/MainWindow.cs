using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using AnoMech.Core.Map;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using AnoMech.Scenarios;
using static AnoMech.Core.Game.Game;

namespace AnoMech.Windows;

public unsafe class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool _leftPanelOpen = true;
    internal IScenario? SelectedScenario => _selectedScenario;
    private IScenario? _selectedScenario;

    internal PartyRole? SelectedRoleOverride => _roleOverride;
    private PartyRole? _roleOverride;

    // Index into the selected scenario's AiStrats; reset to the first strat whenever the
    // selected scenario changes. Passed to RunScenario as selectedAi on a (non-solo) Start.
    // -1 when a grouped scenario's selected region has no strats (Start is then gated off).
    internal int SelectedStrat => _selectedStrat;
    private int _selectedStrat;

    // Index into the selected scenario's WaymarkPresets; reset to the first preset when the
    // selected scenario changes. Passed to RunScenario as selectedWaymark on Start. Ignored
    // by scenarios that declare no presets.
    internal int SelectedWaymark => _selectedWaymark;
    private int _selectedWaymark;

    // The region/group label currently selected in the strat picker, for scenarios that
    // declare StratGroups. Null until a grouped scenario is drawn (then it snaps to the
    // first group); stays null for ungrouped scenarios. Filters AiStrats under the buttons.
    private string? _selectedStratGroup;

    // The last region picked per grouped scenario, restored on a switch back to it.
    private readonly Dictionary<IScenario, string> _stratGroupMemory = new();

    // Index 0 = Auto (null override); indices 1..8 map to (PartyRole)(idx - 1).
    // Labels are the canonical raid role abbreviations: MT/OT tanks, H1/H2 healers
    // (H1 = regen), M1/M2 melee DPS, R1/R2 ranged DPS (R1 = phys).
    private static readonly string[] RoleLabels =
        ["Auto", "MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

#if DEBUG
    private readonly DebugMenu debugMenu;
#endif

    // Version plus the build checksum the multiplayer handshake compares; the ### id keeps the
    // window identity stable across versions.
    private static string TitleWithVersion()
        => $"AnoMech v{PluginBuildInfo.Version} ({PluginBuildInfo.ShortChecksum})###MainWindow";

    public MainWindow(Plugin plugin)
        : base(TitleWithVersion())
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(220, 80),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Flags |= ImGuiWindowFlags.AlwaysAutoResize;

        this.plugin = plugin;
        IsOpen = false;

        // Small gear in the title bar opens the settings window (same toggle as /anomech config).
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2f, 1f),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
#if DEBUG
        debugMenu = new DebugMenu(plugin);
#endif
    }

    public void Dispose()
    {
#if DEBUG
        debugMenu.Dispose();
#endif
    }

    // Hidden while the instance is loaded (RunningSimWindow covers Start/Reset/Leave) and
    // reopened afterwards only if we were the one who closed it.
    private bool hiddenByUs;

    public override void PreOpenCheck()
    {
        if (plugin.Game.World.Map.IsInInstance)
        {
            if (IsOpen) hiddenByUs = true;
            IsOpen = false;
        }
        else if (hiddenByUs)
        {
            hiddenByUs = false;
            IsOpen = true;
        }
    }

    public override void Draw()
    {
        var leftWidth = _leftPanelOpen ? ScenarioPanelWidth() : 30f * ImGuiHelpers.GlobalScale;

        if (ImGui.BeginTable("##layout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##left", ImGuiTableColumnFlags.WidthFixed, leftWidth);
            ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawScenariosPanel();
            ImGui.TableSetColumnIndex(1);
            DrawMainContent();
            ImGui.EndTable();
        }
    }

    // Size the left panel to the widest scenario label so names never clip as scenarios are added.
    private float ScenarioPanelWidth()
    {
        var style = ImGui.GetStyle();
        var widest = 0f;
        foreach (var zone in plugin.Game.Zones)
        {
            widest = Math.Max(widest, ImGui.CalcTextSize(zone.Name).X);
            foreach (var phase in plugin.Game.PhasesOf(zone))
                foreach (var scenario in plugin.Game.ScenariosOf(phase))
                    widest = Math.Max(widest, ImGui.CalcTextSize(DisplayName(scenario)).X);
        }
        var measured = widest + style.FramePadding.X * 2 + style.CellPadding.X * 2;
        return Math.Max(180f * ImGuiHelpers.GlobalScale, measured);
    }

    private void DrawScenariosPanel()
    {
        if (_leftPanelOpen)
        {
            ImGui.TextUnformatted("Scenarios");
            ImGui.SameLine();
            if (ImGui.SmallButton("<##collapse")) _leftPanelOpen = false;
            ImGui.Separator();

            foreach (var zone in plugin.Game.Zones)
            {
                if (!ImGui.CollapsingHeader(zone.Name, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                ImGui.Indent();
                var mpWindowOpen = plugin.MultiplayerWindow.IsOpen;
                var mpConnected = plugin.Multiplayer.IsConnected;
                foreach (var phase in plugin.Game.PhasesOf(zone))
                    foreach (var scenario in plugin.Game.ScenariosOf(phase))
                    {
                        var selected = _selectedScenario == scenario;
                        var mpUnsupported = (mpWindowOpen || mpConnected)
                            && !scenario.SupportsMultiplayer;
                        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
                        // Zone-qualified: two zones can hold same-named scenarios (UMAD and UCOB
                        // both have a P5 "Exaflares"), and a shared ImGui id makes the second
                        // button unclickable.
                        ImGui.PushID(FullName(scenario));
                        ImGui.BeginDisabled(mpUnsupported);
                        if (ImGui.Button(DisplayName(scenario), new Vector2(-1, 0)))
                            SelectScenario(scenario);
                        ImGui.EndDisabled();
                        if (mpUnsupported && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip($"This scenario doesn't support multiplayer. {MpDisabledReason(mpWindowOpen, mpConnected)}");
                        ImGui.PopID();
                        if (selected) ImGui.PopStyleColor();
                    }
                ImGui.Unindent();
            }
        }
        else
        {
            if (ImGui.Button(">##expand")) _leftPanelOpen = true;
        }
    }

    // Select a scenario and reset its per-scenario UI state (strat, waymark, remembered region).
    private void SelectScenario(IScenario scenario)
    {
        _selectedScenario = scenario;
        _selectedStrat = 0;
        _selectedWaymark = 0;
        // Restore the last region picked for this scenario; null self-heals to its first region when drawn.
        _selectedStratGroup = _stratGroupMemory.GetValueOrDefault(scenario);
    }

    // Shared wording for every control disabled by the Multiplayer window or a live session.
    private static string MpDisabledReason(bool windowOpen, bool connected) => (windowOpen, connected) switch
    {
        (true, true) => "Disabled: the Multiplayer window is open and you're connected to a multiplayer session.",
        (true, false) => "Disabled while the Multiplayer window is open.",
        (false, true) => "Disabled while connected to a multiplayer session.",
        _ => "",
    };

    // Distinct, ordered region labels from the strats' IScenarioAi.Group; empty = ungrouped.
    private static IReadOnlyList<string> StratGroups(IScenario scenario)
    {
        var groups = new List<string>();
        foreach (var ai in scenario.AiStrats)
            if (ai.Group is { } g && !groups.Contains(g)) groups.Add(g);
        return groups;
    }

    private void DrawMainContent()
    {
        if (_selectedScenario == null)
        {
            ImGui.TextDisabled("Select a scenario");
            return;
        }

        var game = plugin.Game;

        ImGui.TextUnformatted(FullName(_selectedScenario));
        if (_selectedScenario.SupportsMultiplayer)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Multiplayer...")) plugin.MultiplayerWindow.Toggle();
        }
        ImGui.Separator();
        DrawLocationHint();

        // Once connected the role comes from the Multiplayer claim; forced back to Auto, not
        // just disabled, so an earlier pick can't apply underneath it.
        var mpConnectedForRole = plugin.Multiplayer.IsConnected;
        if (mpConnectedForRole) _roleOverride = null;
        ImGui.BeginDisabled(mpConnectedForRole);
        ImGui.BeginGroup();
        DrawRoleSelector();
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpConnectedForRole && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Role is claimed via the Multiplayer window instead. " + MpDisabledReason(false, true));

        // Only the host's region/strat is broadcast and run; a guest's is reset, not just
        // disabled, so it doesn't sit frozen on a stale pick.
        var mpGuest = plugin.Multiplayer.IsConnected && !plugin.Multiplayer.IsHost;
        if (mpGuest)
        {
            _selectedStrat = 0;
            _selectedStratGroup = null;
        }
        ImGui.BeginDisabled(mpGuest);
        ImGui.BeginGroup();
        DrawStratSelector();
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpGuest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Only the host's selection is used in multiplayer. " + MpDisabledReason(false, true));
        DrawWaymarkSelector();

        DrawSoloStartButton();
        ImGui.SameLine();
        DrawResetLeaveButtons();

        var blocked = ZoneSession.StartBlockedReason();
        var envReady = blocked == null;
        var mpBlocked = _selectedScenario.SupportsMultiplayer && plugin.Multiplayer.IsConnected;
        if (_selectedScenario.SupportsSolo)
        {
            ImGui.BeginDisabled(!envReady || mpBlocked);
            if (ImGui.Button("Start Solo")) game.RunScenario(_selectedScenario, _roleOverride, selectedAi: null, _selectedWaymark);
            ImGui.EndDisabled();
            if ((!envReady || mpBlocked) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(mpBlocked
                    ? "Connected to a multiplayer session -- use Start in the Multiplayer window instead."
                    : $"Cannot start: {blocked}.");
            }
        }

        // God mode, speed and the solo scenario config are disabled while a session is being set
        // up or is live; forced to defaults, not just disabled, so a stale value can't apply.
        var mpWindowOpen = plugin.MultiplayerWindow.IsOpen;
        var mpConnected = plugin.Multiplayer.IsConnected;
        var mpActive = mpWindowOpen || mpConnected;
        if (mpActive) game.GodMode = false;
        ImGui.BeginDisabled(mpActive);
        var god = game.GodMode;
        if (ImGui.Checkbox("God mode", ref god)) game.GodMode = god;
        ImGui.EndDisabled();
        if (mpActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(MpDisabledReason(mpWindowOpen, mpConnected));
        ImGui.SameLine();
        // A host rerunning on its own would desync the session, so this is solo-only.
        if (mpActive) game.AutoRestart = false;
        ImGui.BeginDisabled(mpActive);
        var autoRestart = game.AutoRestart;
        if (ImGui.Checkbox("Auto-restart", ref autoRestart)) game.AutoRestart = autoRestart;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(mpActive
                ? MpDisabledReason(mpWindowOpen, mpConnected)
                : "Restart the same scenario immediately after a successful run. A death turns this back off.");
        ImGui.SameLine();
        ImGui.TextDisabled($"Streak: {game.MechanicStreak}");

#if DEBUG
        if (mpActive) game.EventTimeScale = 1f;
        ImGui.BeginDisabled(mpActive);
        ImGui.BeginGroup();
        debugMenu.DrawSpeedControl();
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(MpDisabledReason(mpWindowOpen, mpConnected));
#endif

        if (game.Paused) ImGui.TextDisabled("(scenario paused — press Reset to clear)");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Scenario config", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            if (mpConnected)
            {
                ImGui.TextDisabled(plugin.Multiplayer.IsHost
                    ? "Configured in the Multiplayer window while hosting."
                    : "The host configures the scenario -- see the Multiplayer window.");
            }
            else
            {
                // DrawMultiplayerSettings only matters in multiplayer, so it stays outside the
                // disabled block.
                ImGui.BeginGroup();
                ImGui.BeginDisabled(mpActive);
                _selectedScenario.DrawSettings();
                ImGui.EndDisabled();
                ImGui.EndGroup();
                if (mpActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(MpDisabledReason(mpWindowOpen, mpConnected));
                // Solo keeps its own copy: these are the player's own mechanics, not only a
                // host's assignment.
                MultiplayerWindow.DrawAssignMechanicsButton(_selectedScenario, mpActive,
                                                            MpDisabledReason(mpWindowOpen, mpConnected));
                _selectedScenario.DrawMultiplayerSettings();
            }
            ImGui.Unindent();
        }

#if DEBUG
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Debug"))
        {
            debugMenu.DrawDebugContent();
        }
#endif
    }

    // Self-contained so RunningSimWindow can draw it too.
    internal void DrawSoloStartButton()
    {
        if (_selectedScenario == null) return;
        var blocked = ZoneSession.StartBlockedReason();
        var hasStrat = HasStartableStrat();
        // The solo path bypasses MultiplayerManager: a host would run the fight without a
        // StartMessage, a peer would start a second independent simulation.
        var mpBlocked = _selectedScenario.SupportsMultiplayer && plugin.Multiplayer.IsConnected;
        var canStart = blocked == null && hasStrat && !mpBlocked;
        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Start")) plugin.Game.RunScenario(_selectedScenario, _roleOverride, _selectedStrat, _selectedWaymark);
        ImGui.EndDisabled();
        if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(mpBlocked
                ? "Connected to a multiplayer session -- use Start in the Multiplayer window instead."
                : blocked != null
                    ? $"Cannot start: {blocked}."
                    : "No strat available for this region yet.");
        }
    }

    // Reset, plus Leave while in-instance; a connected peer's clicks route through the host.
    internal void DrawResetLeaveButtons()
    {
        var game = plugin.Game;
        // A peer's own Game.Reset() would only clear their local view.
        if (ImGui.Button("Reset"))
        {
            if (plugin.Multiplayer.IsConnected && !plugin.Multiplayer.IsHost)
                plugin.Multiplayer.RequestReset();
            else
                game.Reset();
        }
        if (game.World.Map.IsInInstance)
        {
            ImGui.SameLine();
            // A peer's own Game.Leave() would leave the host simulating for a torn-down world.
            if (ImGui.Button("Leave"))
            {
                if (plugin.Multiplayer.IsConnected && !plugin.Multiplayer.IsHost)
                    plugin.Multiplayer.RequestLeaveInstance();
                else
                {
                    game.Leave();
                    // A prior Reset consumed Tick()'s one-shot end trigger (see NotifyLeftInstance).
                    plugin.Multiplayer.NotifyLeftInstance();
                }
            }
        }
    }

    // Changing the preset while a scenario is loaded re-places the markers immediately.
    private void DrawWaymarkSelector()
    {
        if (_selectedScenario is null) return;
        var presets = _selectedScenario.Phase.Zone.WaymarkPresets;
        if (presets.Count == 0) return;
        if (_selectedWaymark < 0 || _selectedWaymark >= presets.Count) _selectedWaymark = 0;

        var labels = new string[presets.Count];
        for (var i = 0; i < presets.Count; i++) labels[i] = presets[i].Name;

        ImGui.TextUnformatted("Waymarks:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##waymarks", ref _selectedWaymark, labels, labels.Length)
            && plugin.Game.World.Map.IsInInstance)
            plugin.Game.World.PlaceWaymarks(presets[_selectedWaymark].Markers);
    }

    private void DrawRoleSelector()
    {
        var idx = _roleOverride is { } role ? (int)role + 1 : 0;
        ImGui.TextUnformatted("Select your Role:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##role", ref idx, RoleLabels, RoleLabels.Length))
            _roleOverride = idx == 0 ? null : (PartyRole)(idx - 1);
    }

    // Only meaningful when a scenario offers more than one strat; hidden otherwise.
    // When the scenario declares StratGroups, a region-button row is drawn above the
    // dropdown and the dropdown is filtered to the selected region.
    private void DrawStratSelector()
    {
        if (_selectedScenario is null) return;
        var strats = _selectedScenario.AiStrats;
        var groups = StratGroups(_selectedScenario);
        if (groups.Count > 0)
        {
            DrawGroupedStratSelector(strats, groups);
            return;
        }

        if (strats.Count <= 1) return;
        _selectedStrat = Math.Clamp(_selectedStrat, 0, strats.Count - 1);
        var labels = new string[strats.Count];
        for (var i = 0; i < strats.Count; i++) labels[i] = strats[i].Name;
        ImGui.TextUnformatted("Select Strat:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##strat", ref _selectedStrat, labels, labels.Length);
    }

    // Region buttons + a region-filtered strat dropdown. _selectedStrat stays an
    // absolute index into AiStrats (what RunScenario consumes); it is reconciled here
    // each frame to the selected region, or set to -1 when that region has no strats.
    private void DrawGroupedStratSelector(IReadOnlyList<IScenarioAi> strats, IReadOnlyList<string> groups)
    {
        if (!GroupsContain(groups, _selectedStratGroup))
            _selectedStratGroup = groups[0];

        ImGui.TextUnformatted("Region:");
        for (var i = 0; i < groups.Count; i++)
        {
            ImGui.SameLine();
            var group = groups[i];
            var selected = _selectedStratGroup == group;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            ImGui.PushID($"region{i}");
            if (ImGui.Button(group))
            {
                _selectedStratGroup = group;
                _stratGroupMemory[_selectedScenario!] = group; // remember across scenario switches
            }
            ImGui.PopID();
            if (selected) ImGui.PopStyleColor();
        }

        var filtered = new List<int>();
        for (var i = 0; i < strats.Count; i++)
            if (strats[i].Group == _selectedStratGroup) filtered.Add(i);

        ImGui.TextUnformatted("Select Strat:");
        ImGui.SameLine();
        if (filtered.Count == 0)
        {
            _selectedStrat = -1;
            ImGui.TextDisabled("(no strats for this region yet)");
            return;
        }

        if (!filtered.Contains(_selectedStrat)) _selectedStrat = filtered[0];
        var localIdx = filtered.IndexOf(_selectedStrat);
        var labels = new string[filtered.Count];
        for (var i = 0; i < filtered.Count; i++) labels[i] = strats[filtered[i]].Name;
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##strat", ref localIdx, labels, labels.Length))
            _selectedStrat = filtered[localIdx];
    }

    // Grouped scenarios need a real strat in the active region. Also the host's pre-broadcast
    // check in MultiplayerManager.StartScenario.
    internal bool HasStartableStrat()
    {
        if (_selectedScenario is not { } scenario) return false;
        if (StratGroups(scenario).Count == 0) return true;
        var strats = scenario.AiStrats;
        return _selectedStrat >= 0 && _selectedStrat < strats.Count
            && strats[_selectedStrat].Group == _selectedStratGroup;
    }

    private static bool GroupsContain(IReadOnlyList<string> groups, string? group)
    {
        if (group is null) return false;
        for (var i = 0; i < groups.Count; i++)
            if (groups[i] == group) return true;
        return false;
    }

    private void DrawLocationHint()
    {
        if (ZoneSession.IsInInn()) return;
        ImGui.TextDisabled("Scenarios only run in an inn");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Scenarios can only be started from an inn — return to one to run a scenario.");
    }
}
