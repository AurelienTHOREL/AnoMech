using System;
using System.Collections.Generic;
using AnoMech.Core.Native;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace AnoMech.Core.Map;

// Hook on ProcessMapEffect (sig from Hyperborea/ECommons).
// Logs every call at Debug level: [MapEffect] index=0x?? state=0x???? flags=0x??
// Apply() replays a known effect by calling the native function directly.
//
// The "module" arg (EventFramework+0x158) resolves to DirectorModule.ActiveContentDirector.
// ProcessMapEffectEx (the network-packet batch variant) routes through this same function
// internally, so there is no separate commit step — this is the correct and only endpoint.
//
// Encoding: packetFlags high16 = State, low8 = Flags
// State: selects the SGB animation mode on the FIRST call to a slot; ignored on subsequent calls.
// Flags: triggers a specific animation action (0x01 show, 0x02 spawn, 0x04 hide, 0x08 despawn,
//        0x10 eyelid-close-instant, 0x20 eyelid-close-anim, 0x40/0x80 charge anim).
internal sealed unsafe class MapEffects : IDisposable
{
    public bool Loaded { get; set; } = false;

    // void(ContentDirector*, uint, ushort, ushort): Dalamud's hook verification rejects any
    // other shape at load.
    private delegate void ProcessMapEffectDelegate(ContentDirector* module, uint index, ushort state, ushort flags);
    private readonly Hook<ProcessMapEffectDelegate> hook;

    internal MapEffects()
    {
        var addr = Plugin.SigScanner.ScanText(
            "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B FA 41 0F B7 E8");
        hook = Plugin.GameInterop.HookFromAddress<ProcessMapEffectDelegate>(addr, Detour);
        hook.Enable();
    }

    private void Detour(ContentDirector* module, uint index, ushort state, ushort flags)
    {
        Plugin.LogManager.LogMapEffect(index, state, flags);
        // Only the real native/packet path reaches this detour (Apply calls hook.Original
        // directly); a host's SGB has been seen carrying flag bits nothing here sends.
        var before = ReadMapEffectItem(module, index);
        hook.Original(module, index, state, flags);
        var after = ReadMapEffectItem(module, index);
        AnoMech.Core.DiagnosticLog.Info(
            $"[MapEffect] REAL native call: index=0x{index:X} state=0x{state:X} flags=0x{flags:X} module=0x{(nint)module:X} "
            + $"item before=({Format(before)}) after=({Format(after)}).");
    }

    // packetFlags: high16=State, low8=Flags (ACT type-257 raw value).
    // Returns false when the zone/director isn't ready yet (async load still in
    // flight) so MapController can retry instead of silently losing the call.
    internal bool Apply(uint packetFlags, byte index)
    {
        if (!Loaded) return false;
        var modulePtr = *(nint*)((nint)EventFramework.Instance() + 344);
        if (modulePtr == 0) return false;
        var module = (ContentDirector*)modulePtr;
        // ProcessMapEffect writes into MapEffects->Items[index], and index can come off the
        // network. Not ready reads like any other not-ready case, so the caller retries.
        if (!IsIndexInRange(module, index)) return false;
        var state = (ushort)(packetFlags >> 16);
        var flags = (ushort)(packetFlags & 0xFF);
        // Before/after per index: whether LayoutId is populated at all and whether State/Flags took.
        var before = ReadMapEffectItem(module, index);
        hook.Original(module, index, state, flags);
        var after = ReadMapEffectItem(module, index);
        AnoMech.Core.DiagnosticLog.Info(
            $"[MapEffect] native call: index=0x{index:X} state=0x{state:X} flags=0x{flags:X} module=0x{modulePtr:X} "
            + $"item before=({Format(before)}) after=({Format(after)}).");
        // The SharedGroupLayoutInstance behind LayoutId carries its own load state, which
        // ContentDirector's table can't see.
        var sgState = ReadSharedGroupInstanceState(after.LayoutId);
        AnoMech.Core.DiagnosticLog.Info($"[MapEffect] SharedGroupLayoutInstance for index=0x{index:X} LayoutId=0x{after.LayoutId:X}: {sgState}.");
        return true;
    }

    // Hard-deactivate one arena scenery slot's SharedGroup and all its children (geometry, VFX
    // AND sound), bypassing ProcessMapEffect's flag state machine: flag 0x04 ("hide") blanks the
    // BgParts but leaves the SGB's Sound children playing. False until the slot's SGB resolves.
    internal bool SuppressSlot(byte index)
    {
        if (!Loaded) return false;
        var modulePtr = *(nint*)((nint)EventFramework.Instance() + 344);
        if (modulePtr == 0) return false;
        if (!IsIndexInRange((ContentDirector*)modulePtr, index)) return false;
        var item = ReadMapEffectItem((ContentDirector*)modulePtr, index);
        if (item.LayoutId == 0) return false;
        if (!preSuppressionStates.ContainsKey(index)
            && LayoutInstanceDiagnostics.CaptureActiveStates(item.LayoutId) is { } states)
            preSuppressionStates[index] = states;
        var ok = LayoutInstanceDiagnostics.SetSharedGroupActive(item.LayoutId, active: false, recurseChildren: true);
        if (ok)
            AnoMech.Core.DiagnosticLog.Info($"[MapEffect] SuppressSlot index=0x{index:X} LayoutId=0x{item.LayoutId:X} -- SG + children set inactive.");
        return ok;
    }

    // Each suppressed slot's tree as it was before its first SuppressSlot, for RestoreSlot.
    private readonly Dictionary<byte, bool[]> preSuppressionStates = new();

    internal IReadOnlyCollection<byte> SuppressedSlots => preSuppressionStates.Keys;

    // Undoes SuppressSlot. Nothing to do for a slot never suppressed since the territory loaded.
    internal void RestoreSlot(byte index)
    {
        if (!preSuppressionStates.Remove(index, out var states) || !Loaded) return;
        var modulePtr = *(nint*)((nint)EventFramework.Instance() + 344);
        if (modulePtr == 0 || !IsIndexInRange((ContentDirector*)modulePtr, index)) return;
        var item = ReadMapEffectItem((ContentDirector*)modulePtr, index);
        if (item.LayoutId == 0) return;
        if (LayoutInstanceDiagnostics.RestoreActiveStates(item.LayoutId, states))
            AnoMech.Core.DiagnosticLog.Info($"[MapEffect] RestoreSlot index=0x{index:X} LayoutId=0x{item.LayoutId:X} -- SG + children back as before suppression.");
        else
            AnoMech.Core.DiagnosticLog.Warn($"[MapEffect] RestoreSlot index=0x{index:X} LayoutId=0x{item.LayoutId:X} -- tree changed shape; only the SG re-enabled.");
    }

    // The territory reverted: its SharedGroups are gone.
    internal void ForgetSuppressions() => preSuppressionStates.Clear();

    // Per-frame follow-up to SuppressSlot: the SGB's own update re-arms its Sound children, so
    // the ambient/voice loops creep back. Skips the native call unless one is active again.
    internal void SilenceSlotSounds(byte index)
    {
        if (!Loaded) return;
        var modulePtr = *(nint*)((nint)EventFramework.Instance() + 344);
        if (modulePtr == 0) return;
        var item = ReadMapEffectItem((ContentDirector*)modulePtr, index);
        if (item.LayoutId != 0)
            LayoutInstanceDiagnostics.SilenceSlotSounds(item.LayoutId);
    }

    private static bool IsIndexInRange(ContentDirector* director, uint index)
    {
        var list = director->MapEffects;
        return list != null && index < list->ItemCount;
    }

    private static ContentDirector.MapEffectItem ReadMapEffectItem(ContentDirector* director, uint index)
    {
        if (!IsIndexInRange(director, index)) return default;
        return director->MapEffects->Items[(int)index];
    }

    private static string Format(ContentDirector.MapEffectItem item) => $"LayoutId=0x{item.LayoutId:X} State=0x{item.State:X} Flags=0x{item.Flags:X}";

    private static string ReadSharedGroupInstanceState(uint layoutId) => LayoutInstanceDiagnostics.Describe(layoutId);

    public void Dispose()
    {
        hook.Disable();
        hook.Dispose();
    }
}
