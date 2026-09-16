using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Umad;

public sealed class UmadZone : IZone
{
    public static readonly UmadZone Instance = new();
    // 77 = the weather ARR's InitZone packet carries for P1. P1Haze is the zone's fog value at
    // load (EnvState+0xD8 / EnvSimulator+0x2F8); the real P1 keeps its red haze for the whole
    // phase, which the engine's own transition to 1800 cleared.
    public const float P1Haze = 1000f;
    public static readonly Phase P1 = new(Instance, "P1", 77, 20292, InitP1Arena, fogHold: P1Haze);
    public static readonly Phase P2 = new(Instance, "P2", 79, 20292);
    public static readonly Phase P3 = new(Instance, "P3", 174, 20293);
    public static readonly Phase P4 = new(Instance, "P4", 174, 20293);
    public static readonly Phase P5 = new(Instance, "P5", 175, 20294, InitP5Arena);
    // Same weather/bgm as P5, but Flood happens before P5's own arena transforms: real MapEffect
    // logs show slots 0x14-0x21 untransformed before FloodCast.
    public static readonly Phase P5Flood = new(Instance, "P5", 175, 20294, InitP5FloodArena);

    public string Name => "Dancing Mad";
    public uint TerritoryId => 1363;
    public Vector3 Origin => new(100f, 0f, 100f);
    public byte Level => 100;

    public IReadOnlyList<WaymarkLayout> WaymarkPresets => Waymarks;
    public IReadOnlyList<Vector3> ColliderRemovalPoints => [new(0f, 0f, -10f)];

    public void Run(SimWorld world) { }

    // Replay-derived RSV/RSF data the server would deliver in a real duty; peers need it too, or
    // their BgParts point at unseeded paths and render black. Idempotent.
    public void RunClientSetup(SimWorld world) => UmadReplayData.Seed();

    // Slots 1-0x23 are the P4/P5 destroyed-world scenery (broken floor, rubble, dais), which a
    // client-side zone load shows by default. AddEffect's hide is the replicated geometry hide;
    // SuppressArenaSlot also kills the SGBs' Sound children (the out-of-place voice lines).
    // Three passes because a slot's SGB streams in asynchronously. Slot 0 (the platform) is
    // driven by the scenario's own catch-up sequence.
    private static void InitP1Arena(SimWorld world)
    {
        foreach (var t in (float[])[1f, 2.5f, 5f])
            world.Events.Add(t, () =>
            {
                for (byte slot = 1; slot < 0x24; slot++)
                {
                    world.Map.AddEffect((0x0004U << 16) | 0x04, slot);
                    world.Map.SuppressArenaSlot(slot);
                }
            });
    }

    private static void InitP5Arena(SimWorld world) => world.Events.Add(1f, () =>
    {
        var s = new ushort[0x24];
        for (var i = 0; i < s.Length; i++) s[i] = 0x4; // base default (hide/empty)
        s[0x11] = 0x1; s[0x12] = 0x1;                  // base lit holes
        s[0x00] = 0x40;                                // P5
        s[0x14] = 0x200;                               // P5 centerpiece ("nine holes")
        for (var i = 0x15; i <= 0x1C; i++) s[i] = 0x1; // P5 nine holes
        for (var i = 0x1D; i <= 0x21; i++) s[i] = 0x2; // P5 towers of rubble

        for (byte slot = 0; slot < s.Length; slot++)
        {
            var state = s[slot];
            var flags = (byte)(state & 0xFF);
            if (flags == 0) flags = 0x01;              // 0x200: no action bit -> "show"
            world.Map.AddEffect(((uint)state << 16) | flags, slot);
        }
    });

    // Slots 0x14-0x21 sit at plain 0x00010001 before FloodCast. Slot 0's real value doesn't fit
    // this state/flags packing and is left alone.
    private static void InitP5FloodArena(SimWorld world) => world.Events.Add(1f, () =>
    {
        for (byte slot = 0x14; slot <= 0x21; slot++)
            world.Map.AddEffect((0x0001U << 16) | 0x01, slot);
    });

    private static readonly IReadOnlyList<WaymarkLayout> Waymarks =
    [
        new WaymarkLayout("Diamond Waymarks", (IReadOnlyList<Waymark>)[
            new(WaymarkSlot.A,     new Vector3(  0, 0, -12)),
            new(WaymarkSlot.B,     new Vector3( 12, 0,   0)),
            new(WaymarkSlot.C,     new Vector3(  0, 0,  12)),
            new(WaymarkSlot.D,     new Vector3(-12, 0,   0)),
            new(WaymarkSlot.One,   new Vector3( -6, 0,  -6)),
            new(WaymarkSlot.Two,   new Vector3(  6, 0,  -6)),
            new(WaymarkSlot.Three, new Vector3(  6, 0,   6)),
            new(WaymarkSlot.Four,  new Vector3( -6, 0,   6)),
        ]),
        new WaymarkLayout("DN Zenith Waymarks",
        [
            new(WaymarkSlot.A,     new Vector3(    0, 0,   -12)),
            new(WaymarkSlot.B,     new Vector3(   12, 0,     0)),
            new(WaymarkSlot.C,     new Vector3(    0, 0,    12)),
            new(WaymarkSlot.D,     new Vector3(  -12, 0,     0)),
            new(WaymarkSlot.One,   new Vector3(-8.765f, 0, -8.765f)),
            new(WaymarkSlot.Two,   new Vector3( 8.628f, 0, -8.765f)),
            new(WaymarkSlot.Three, new Vector3( 8.628f, 0,  8.628f)),
            new(WaymarkSlot.Four,  new Vector3(-8.765f, 0,  8.628f)),
        ]),
        // A/B/C/D on cardinals 12y out, 1-4 on the corners (±12, ±12).
        new WaymarkLayout("[LPDU] Big Box",
        [
            new(WaymarkSlot.A,     new Vector3(  0, 0, -12)),
            new(WaymarkSlot.B,     new Vector3( 12, 0,   0)),
            new(WaymarkSlot.C,     new Vector3(  0, 0,  12)),
            new(WaymarkSlot.D,     new Vector3(-12, 0,   0)),
            new(WaymarkSlot.One,   new Vector3(-12, 0, -12)),
            new(WaymarkSlot.Two,   new Vector3( 12, 0, -12)),
            new(WaymarkSlot.Three, new Vector3( 12, 0,  12)),
            new(WaymarkSlot.Four,  new Vector3(-12, 0,  12)),
        ]),
    ];
}
