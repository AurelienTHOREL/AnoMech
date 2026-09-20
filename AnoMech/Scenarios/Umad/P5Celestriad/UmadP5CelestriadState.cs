using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Umad.P5Celestriad.UmadP5CelestriadConstants;

namespace AnoMech.Scenarios.Umad.P5Celestriad;

// Declared in the confirmed real clockwise ring order (Fire block, then Lightning block, then
// Ice block): ElementForSet's cyclic shift relies on this order to mean "next clockwise".
public enum CelestriadElement { Fire, Lightning, Ice }

// One of the 9 fixed towers, spawned once for the whole mechanic; position never changes.
public sealed record CelestriadTower(CelestriadElement Element, int SubIndex, Vector3 Position);

// Per-run randomization: which element (or "free") each party role is permanently debuffed
// with, which element doubles up on each of the 3 sets, which of its 3 ring towers are active,
// and (sets 0 and 2 only, the 1st and 3rd soaks) whether that set's single Catastrophic Choice
// is Aero (green, safe toward centre) or Earth (brown, safe away from centre). ElementForSet
// derives each role's actual per-set soak target from its debuff.
//
// Deliberately has no notion of which specific player goes to which specific active tower
// within a doubled element's pair, or who's "responsible" for a tower failing: that's a
// strategy decision (the AI's job, see UmadP5CelestriadAi) and a scenario-ruleset resolution
// decision (the scenario's job, see UmadP5CelestriadScenario.ResolveSet), not a fact of the
// randomization itself. State only ever exposes what's actually random.
public sealed class UmadP5CelestriadState
{
    private readonly Rng rng = new();

    // Cyclic shift applied to a debuffed player's own element index to get their set-s soak
    // target: set 0 -> next element, set 1 -> element after that, set 2 -> own element again.
    // Since this is a fixed shift of a 3-element cycle, it's automatically a bijection each set
    // (exactly one debuff group per element) and never repeats an element across the 3 sets.
    private static readonly int[] SetOffset = { 1, 2, 0 };

    private static readonly CelestriadElement[] Elements =
        { CelestriadElement.Fire, CelestriadElement.Lightning, CelestriadElement.Ice };

    public IReadOnlyDictionary<PartyRole, CelestriadElement?> PlayerDebuffElement { get; }
    public IReadOnlyList<CelestriadElement> DoubleElement { get; }
    public IReadOnlyList<CelestriadTower> AllTowers { get; }
    public IReadOnlyList<IReadOnlyList<CelestriadTower>> SetActiveTowers { get; }
    public IReadOnlyList<bool?> AeroVariant { get; }

    // The element this role should physically soak at this set. NOT the same as their permanent
    // debuff except in set 2. Free (undebuffed) players always fill in for the doubled element.
    public CelestriadElement ElementForSet(PartyRole role, int set) =>
        PlayerDebuffElement[role] is { } own ? (CelestriadElement)(((int)own + SetOffset[set]) % 3) : DoubleElement[set];

    public UmadP5CelestriadState(SimParty party, UmadP5CelestriadStateOverrides overrides)
    {
        // Each element doubles exactly once across the 3 sets: a shuffled permutation
        // guarantees that instead of leaving it to independent per-set coin flips.
        DoubleElement = rng.Shuffle(CelestriadElement.Fire, CelestriadElement.Ice, CelestriadElement.Lightning);

        var roles = RoleList.Random(party).List;
        var buckets = new CelestriadElement?[]
        {
            CelestriadElement.Fire, CelestriadElement.Fire,
            CelestriadElement.Ice, CelestriadElement.Ice,
            CelestriadElement.Lightning, CelestriadElement.Lightning,
            null, null,
        };
        if (overrides.PlayerElement != CelestriadElementOverride.Random)
        {
            CelestriadElement? wanted = overrides.PlayerElement switch
            {
                CelestriadElementOverride.Fire => CelestriadElement.Fire,
                CelestriadElementOverride.Ice => CelestriadElement.Ice,
                CelestriadElementOverride.Lightning => CelestriadElement.Lightning,
                _ => null, // Free
            };
            var playerIdx = Array.IndexOf(roles, party.PlayerRole);
            var wantedIdx = Array.FindIndex(buckets, b => b == wanted);
            (roles[playerIdx], roles[wantedIdx]) = (roles[wantedIdx], roles[playerIdx]);
        }
        PlayerDebuffElement = Enumerable.Range(0, 8).ToDictionary(i => roles[i], i => buckets[i]);

        var allTowers = new List<CelestriadTower>(9);
        foreach (var element in Elements)
            for (var sub = 0; sub < 3; sub++)
                allTowers.Add(new CelestriadTower(element, sub, TowerPosition(element, sub)));
        AllTowers = allTowers;

        var setActive = new List<IReadOnlyList<CelestriadTower>>(3);
        var aero = new List<bool?>(3);
        for (var set = 0; set < 3; set++)
        {
            var active = new List<CelestriadTower>(4);
            foreach (var element in Elements)
            {
                var elementTowers = allTowers.Where(t => t.Element == element).ToArray();
                var isDouble = element == DoubleElement[set];
                // Which sub-towers light up is random; sorted ascending so a doubled element's
                // pair always lists in a stable, deterministic clockwise order for whoever reads
                // "first" vs "second" out of it (the AI, when deciding who goes where).
                var subs = rng.Shuffle(0, 1, 2).Take(isDouble ? 2 : 1).OrderBy(s => s).ToArray();
                active.AddRange(subs.Select(s => elementTowers[s]));
            }
            setActive.Add(active);
            aero.Add(ResolveAero(set, overrides));
        }
        SetActiveTowers = setActive;
        AeroVariant = aero;
    }

    private bool? ResolveAero(int set, UmadP5CelestriadStateOverrides overrides) => set switch
    {
        0 => overrides.Set1 switch
        {
            CatastrophicVariantOverride.Aero => true,
            CatastrophicVariantOverride.Earth => false,
            _ => rng.NextBool(),
        },
        2 => overrides.Set3 switch
        {
            CatastrophicVariantOverride.Aero => true,
            CatastrophicVariantOverride.Earth => false,
            _ => rng.NextBool(),
        },
        _ => null, // set 1 (index 1, the "second" soak) has no Catastrophic Choice
    };

    // 9 towers 40 degrees apart clockwise from north, grouped as 3 contiguous per-element blocks
    // (not interleaved) starting 20 degrees off north, confirmed against the real EObj spawn
    // positions (see UmadP5CelestriadConstants). subIndex selects which of an element's 3 ring spots.
    public static Vector3 TowerPosition(CelestriadElement element, int subIndex)
    {
        var ringIndex = (int)element * 3 + subIndex;
        var angle = MathF.PI / 9f + ringIndex * (MathF.PI * 2f / 9f);
        return new Vector3(MathF.Sin(angle) * CelestriadGeometry.RingRadius, 0f, -MathF.Cos(angle) * CelestriadGeometry.RingRadius);
    }
}
