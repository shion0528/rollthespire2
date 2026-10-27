using System.Numerics;

namespace RolltheSpire2.Search.FamilyExecution;

// N-private necessary identity checks. Remaining local replay uses the unchanged
// operators, including real pickup/continuation and the existing lossy boundary.
internal sealed class NeowCpuPreGate
{
    private readonly NeowReplayPlan _plan;
    private readonly NeowIdentityCpuPlan? _pair;
    private readonly ulong _allowedCurses;
    internal bool IdentityOnly { get; }
    internal bool FixedPair => _pair is not null;
    internal CpuNeowBatchGate? CreateBatchGate(bool? requested = null) =>
        CpuNeowBatchGate.TryCreate(_plan, _allowedCurses, requested);
    internal double GateSurvival { get; }
    // Donor e89849e: the fixed direct two-draw operator only. Other replay
    // bodies retain their own configuration boundary.
    internal bool BoundedLeafyWorkers => !IdentityOnly && !FixedPair && !_plan.Bones && !_plan.RequireBones &&
        _plan.Selected == Beta110FastRelicCatalog.LeafyPoultice &&
        _plan.StructuredConditions is [{ Kind: Beta110FastStructuredConditionKind.LeafyPoulticeTransforms, TargetCount: 2 } condition] &&
        condition.SourceRelicId == _plan.Selected && _plan.ExactOnly.Length == 0 && !_plan.HasFinalCurseFastProjection &&
        _plan.EnabledDomains == Beta110FastDomain.LeafyPoulticeTransforms &&
        _plan.Authority.PlayersCount == 1 && _plan.Authority.AllCharacterCardPoolsUnlocked &&
        _plan.Authority.ScrollBoxesAllowed && _plan.Authority.EligibleCurseRelicIds.Length == 10 &&
        _plan.Authority.EffectCatalog.LeafyTransformAuthorityExact &&
        (_plan.AuthoredUpgrades?.Advance(_plan.Selected, 255) ?? 0) == 0;
    private NeowCpuPreGate(NeowReplayPlan plan, NeowIdentityCpuPlan? pair, ulong allowed, bool identityOnly)
    {
        _plan = plan; _pair = pair; _allowedCurses = allowed; IdentityOnly = identityOnly;
        GateSurvival = (double)BitOperations.PopCount(allowed) / plan.Authority.EligibleCurseRelicIds.Length;
        if (pair is not null) GateSurvival *= 2d / (plan.Authority.BonesEligibleRelicIds.Length * (plan.Authority.BonesEligibleRelicIds.Length - 1));
    }

    internal static NeowCpuPreGate? TryCompile(NeowReplayPlan p)
    {
        if (!p.DirectNestedVanilla111 || !p.Authority.IdentityAuthorityExact || p.Authority.EligibleCurseRelicIds.Length == 0) return null;
        ulong curses = 0;
        foreach (byte id in p.Authority.EligibleCurseRelicIds)
        {
            // Prove curse observations cannot also be supplied by a positive slot.
            if (NeowFamilyReplay.Positives.Contains(id) || id is Beta110FastRelicCatalog.LavaRock or
                Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.NutritiousOyster or
                Beta110FastRelicCatalog.StoneHumidifier or Beta110FastRelicCatalog.NeowsTalisman or Beta110FastRelicCatalog.Pomander) return null;
            curses |= Beta110FastRelicCatalog.Bit(id);
        }
        ulong allowed = curses;
        ulong selected = p.Selected == Beta110FastRelicCatalog.InvalidId ? 0 : Beta110FastRelicCatalog.Bit(p.Selected);
        if ((selected & curses) != 0) allowed &= selected;
        if (p.Bones || p.RequireBones) allowed &= Beta110FastRelicCatalog.Bit(Beta110FastRelicCatalog.NeowsBones);
        ulong all = p.TopAll & curses;
        if (all != 0) allowed &= BitOperations.PopCount(all) == 1 ? all : 0;
        if (p.TopAny != 0 && (p.TopAny & ~curses) == 0) allowed &= p.TopAny;
        allowed &= ~p.TopBan;

        var pair = NeowIdentityCpuPlan.TryCompile(p with { StructuredConditions = [],
            EnabledDomains = Beta110FastDomain.None, ExactOnly = [] });
        if (pair is null && allowed == curses) return null;
        bool noInnerRequirement = (p.BonesAny | p.BonesAll | p.BonesBan) == 0 &&
            p.First == Beta110FastRelicCatalog.InvalidId && p.Second == Beta110FastRelicCatalog.InvalidId;
        bool identityOnly = noInnerRequirement && p.StructuredConditions.Length == 0 &&
            p.ExactOnly.Length == 0 && p.EnabledDomains == Beta110FastDomain.None &&
            ((p.TopAny | p.TopAll | p.TopBan | selected) & ~curses) == 0;
        if (!identityOnly && pair is null && BitOperations.PopCount(allowed) * 2 > p.Authority.EligibleCurseRelicIds.Length)
            return null; // loose necessary checks do not justify replaying the prefix
        return new(p, pair, allowed, identityOnly);
    }

    internal bool PassesIdentity(ulong root) => _pair is not null ? _pair.Matches(root) :
            (_allowedCurses & Beta110FastRelicCatalog.Bit(NeowFamilyReplay.ObserveCurse(root, _plan, out _))) != 0;

    internal bool Matches(ulong root)
    {
        // Pure curse predicates are completely decided by the first observation.
        // For locals, tiny surviving populations use the established replay;
        // no hypothetical route result replaces actual N filtering.
        return PassesIdentity(root) && (IdentityOnly || NeowFamilyReplay.Matches(root, _plan));
    }
}
