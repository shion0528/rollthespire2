using System.Numerics;
using System.Text;

namespace RolltheSpire2.Search.FamilyExecution;

// Independent same-DLL trials. Ordinary releases contain no environment reads
// for these experiments and default to the unchanged resource.14 bodies.
internal readonly record struct NeowStructuralTrialOptions(
    bool KaleidoscopePoolGate, bool FinalCurseIntervals, bool BonesIdentityOnly)
{
    internal static NeowStructuralTrialOptions Capture()
    {
#if RT2_GPU_PROFILING
        return new NeowStructuralTrialOptions(
            System.Environment.GetEnvironmentVariable("RT2_N_POOL_REACHABILITY") == "1",
            System.Environment.GetEnvironmentVariable("RT2_N_FINAL_CURSE_INTERVALS") == "1",
            System.Environment.GetEnvironmentVariable("RT2_N_BONES_IDENTITY_ONLY") == "1").WithSettings();
#else
        return default;
#endif
    }

    internal NeowStructuralTrialOptions WithSettings()
    {
        var settings = SearchOptimizationScope.Current;
        return new(KaleidoscopePoolGate && settings.CostOrderedFilters,
            FinalCurseIntervals && settings.ExactDrawIntervals,
            BonesIdentityOnly && settings.CostOrderedFilters);
    }

    internal bool Any => KaleidoscopePoolGate || FinalCurseIntervals || BonesIdentityOnly;
}

internal sealed record NeowStructuralPhysicalPlan(
    bool KaleidoscopePoolGate, bool FinalCurseIntervals, bool BonesIdentityOnly,
    string? InternalOrderSource, string? Revision, string Reason,
    uint Target0Pools = 0, uint Target1Pools = 0)
{
    internal static readonly NeowStructuralPhysicalPlan None = new(false, false, false, null, null, "NotRequested");
}

internal static class NeowStructuralCompiler
{
    internal const string PoolRevision = "KaleidoscopePool4.ReusedNiche0And3.20261025.v1";
    internal const string CurseRevision = "FinalCurse.ExactMantissaIntervals53.20261025.v1";
    internal const string BonesRevision = "BonesIdentityOnly.NoObservedInternalDraws.20261025.v1";
    private const int MaximumCurseIntervals = 2;

    internal static NeowStructuralPhysicalPlan Compile(NeowFamilyGpuPlan plan,
        NeowStructuralTrialOptions requested, NeowInternalFilterOrderPlan internalOrder,
        bool internalOrderEnabled, bool ordinaryScope)
    {
        if (!requested.Any) return NeowStructuralPhysicalPlan.None;
        if (!ordinaryScope || plan.IsMultiplayer || plan.JointTransformMetaOffset >= 0 || plan.DirectNestedMode != 0)
            return NeowStructuralPhysicalPlan.None with { Reason = "OrdinarySoloNoCompositeScopeRequired" };
        var reasons = new List<string>();
        bool bones = requested.BonesIdentityOnly && TryBonesIdentityOnly(plan, out _);
        if (requested.BonesIdentityOnly)
        {
            TryBonesIdentityOnly(plan, out string bonesReason);
            reasons.Add("Bones=" + bonesReason);
        }
        StaticDrawGateCompiler.Gate? curse = null;
        bool intervals = requested.FinalCurseIntervals && internalOrderEnabled &&
            TryFinalCurseIntervals(plan, out curse);
        if (requested.FinalCurseIntervals)
            reasons.Add("Curse=" + (intervals ? "ExactRankIntervals" : "ExistingStage4AndBoundedIntervalsRequired"));
        uint target0 = 0, target1 = 0;
        bool ordered = false;
        bool pools = requested.KaleidoscopePoolGate && internalOrderEnabled &&
            TryKaleidoscopePoolGate(plan, internalOrder, out target0, out target1, out ordered, out _);
        if (requested.KaleidoscopePoolGate)
        {
            TryKaleidoscopePoolGate(plan, internalOrder, out _, out _, out _, out string poolReason);
            reasons.Add("Pools=" + (pools ? poolReason : "Fallback." + poolReason));
        }
        string? source = pools || intervals ? InternalSource(internalOrder, pools, intervals ? curse : null,
            target0, target1, ordered) : null;
        var revisions = new List<string>();
        if (pools) revisions.Add(PoolRevision);
        if (intervals) revisions.Add(CurseRevision);
        if (bones) revisions.Add(BonesRevision);
        return new(pools, intervals, bones, source,
            revisions.Count == 0 ? null : string.Join('.', revisions), string.Join('|', reasons), target0, target1);
    }

    internal static bool TryFinalCurseIntervals(NeowFamilyGpuPlan plan, out StaticDrawGateCompiler.Gate? gate)
    {
        gate = null;
        if (plan.Meta.Length < 85 || plan.Meta[83] == 0) return false;
        ulong trailer64 = 20UL + 6UL * plan.Meta[9];
        if (trailer64 + 13 >= (ulong)plan.PoolMeta.Length) return false;
        int trailer = (int)trailer64;
        uint requiredAt = plan.PoolMeta[trailer + 6], requiredCount = plan.PoolMeta[trailer + 7];
        uint bannedAt = plan.PoolMeta[trailer + 8], bannedCount = plan.PoolMeta[trailer + 9];
        uint poolAt = plan.PoolMeta[trailer + 12], count = plan.PoolMeta[trailer + 13];
        bool Fits(uint at, uint length) => (ulong)at + length <= (ulong)plan.Cards.Length;
        if (count is 0 or > int.MaxValue || !Fits(requiredAt, requiredCount) ||
            !Fits(bannedAt, bannedCount) || !Fits(poolAt, count)) return false;
        // Required IDs are the original conjunction, never an Any/union set.
        // Every repeated pool rank keeps its complete finite-mantissa mass.
        return StaticDrawGateCompiler.TryCompileRanks((int)count, rank =>
        {
            uint id = plan.Cards[poolAt + (uint)rank];
            for (uint i = 0; i < requiredCount; i++) if (plan.Cards[requiredAt + i] != id) return false;
            for (uint i = 0; i < bannedCount; i++) if (plan.Cards[bannedAt + i] == id) return false;
            return true;
        }, MaximumCurseIntervals, out gate);
    }

    internal static bool TryKaleidoscopePoolGate(NeowFamilyGpuPlan plan, NeowInternalFilterOrderPlan order,
        out uint target0Pools, out uint target1Pools, out bool ordered, out string reason)
    {
        target0Pools = target1Pools = 0; ordered = false;
        reason = "Pool4FixedKAndZeroNicheCompanionRequired";
        if (plan.Meta.Length < 85 || !order.Enabled || plan.Meta[9] != 4 || (plan.Meta[82] & 3u) != 3u ||
            plan.JointTransformMetaOffset >= 0 || plan.DirectNestedMode != 0 ||
            order.NicheAdvances.Length != 1 || order.NicheAdvances[0] != 6) return false;
        uint a = plan.Meta[27], b = plan.Meta[28];
        uint other = a == Beta110FastRelicCatalog.Kaleidoscope ? b :
            b == Beta110FastRelicCatalog.Kaleidoscope ? a : uint.MaxValue;
        // These three audited companions use other RNG streams only. A total
        // final offset alone cannot prove K's arrival for NewLeaf/upgrades/etc.
        if (other is not (Beta110FastRelicCatalog.LeafyPoultice or
                Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule)) return false;
        if (!ZeroAuthoredOnRequestedRoutes(plan, a, b))
        { reason = "NonzeroOrUnknownAuthoredKArrival"; return false; }
        int at = Beta110FastRelicCatalog.Kaleidoscope * 5;
        if (plan.Conditions.Length < at + 5 || plan.Conditions[at] != 2 ||
            (plan.Conditions[at + 4] & 0x80000000u) == 0 ||
            plan.Conditions[at + 1] == uint.MaxValue || plan.Conditions[at + 2] == uint.MaxValue)
        { reason = "CompleteKTwoTargetPredicateRequired"; return false; }
        uint first = plan.Conditions[at + 1], second = plan.Conditions[at + 2];
        ordered = (plan.Conditions[at + 4] & 4u) != 0;
        for (int pool = 0; pool < 4; pool++)
        {
            bool any = false;
            for (int rarity = 0; rarity < 3; rarity++)
            {
                int index = 20 + pool * 6 + rarity * 2;
                if (index + 1 >= plan.PoolMeta.Length)
                { reason = "PoolMembershipMetadataUnavailable"; return false; }
                uint offset = plan.PoolMeta[index], count = plan.PoolMeta[index + 1];
                if ((ulong)offset + count > (ulong)plan.Cards.Length)
                { reason = "PoolMembershipRangeInvalid"; return false; }
                any |= count > 0;
                for (uint i = 0; i < count; i++)
                {
                    uint id = plan.Cards[offset + i];
                    if (id == first) target0Pools |= 1u << pool;
                    if (id == second) target1Pools |= 1u << pool;
                }
            }
            if (!any) { reason = "CanonicalKPoolMustBeNonempty"; return false; }
        }
        // A pool membership necessity is useful only if excluding one of the
        // four pools can remove at least one complete target's availability.
        if (BitOperations.PopCount(target0Pools) > 1 && BitOperations.PopCount(target1Pools) > 1)
        { reason = "AllExclusionsRetainBothTargets"; return false; }
        reason = "ActualPackedPoolMembership.KArrival0.ZeroAuthoredOnRequestedRoutes.NoRandomIndependenceAssumption";
        return true;
    }

    private static bool ZeroAuthoredOnRequestedRoutes(NeowFamilyGpuPlan plan, uint a, uint b)
    {
        if (!plan.HasAuthoredUpgrades) return true;
        if (plan.Conditions.Length < 226) return false;
        bool Zero(uint source, uint prior) => source is not
            (Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule) ||
            plan.Conditions[160u + (source == Beta110FastRelicCatalog.SmallCapsule ? 0u : 33u) +
                (prior == 255 ? 32u : prior)] == 0;
        bool Route(uint first, uint second) => Zero(first, 255) && Zero(second, first);
        if (plan.Meta[12] == 255 && plan.Meta[13] == 255) return Route(a, b) && Route(b, a);
        return ((plan.Meta[12] == a && plan.Meta[13] == b) || (plan.Meta[12] == b && plan.Meta[13] == a)) &&
            Route(plan.Meta[12], plan.Meta[13]);
    }

    internal static bool Pool4Allows(uint excluded0, uint excluded1, uint target0Pools, uint target1Pools, bool ordered)
    {
        bool Has(uint excluded, uint mask) => (mask & (15u ^ (1u << (int)excluded))) != 0;
        bool forward = Has(excluded0, target0Pools) && Has(excluded1, target1Pools);
        return forward || !ordered && Has(excluded0, target1Pools) && Has(excluded1, target0Pools);
    }

    internal static bool TryBonesIdentityOnly(NeowFamilyGpuPlan plan, out string reason)
    {
        reason = "NoInternalIdentityOrLocalObservationRequired";
        if (plan.Meta.Length < 85 || plan.IsMultiplayer || plan.UsesStagedDense || plan.HasLocalResults ||
            plan.HasAuthoredUpgrades || plan.JointTransformMetaOffset >= 0 || plan.DirectNestedMode != 0 ||
            (plan.Meta[56] & 2u) == 0 || plan.Meta[81] != Beta110FastRelicCatalog.NeowsBones ||
            plan.Meta[12] != 255 || plan.Meta[13] != 255 || plan.Meta[67] != 0 || plan.Meta[82] != 0 ||
            plan.Meta[8] is < 2 or > 64 || plan.Bones.Length < plan.Meta[8]) return false;
        for (int i = 48; i < 54; i++) if (plan.Meta[i] != 0) return false;
        for (int i = 0; i < plan.Meta[8]; i++) if (plan.Bones[i] >= 64) return false;
        reason = "TopPredicateAlreadyChecked.NoInternalConstraint.NoRngContinuationExport.OrdinalOnly";
        return true;
    }

    private static string InternalSource(NeowInternalFilterOrderPlan order, bool pools,
        StaticDrawGateCompiler.Gate? curse, uint target0, uint target1, bool ordered)
    {
        if (order.AcceptedFraction == 0) return order.Source;
        var source = new StringBuilder("bool internal_final_curse_gate(uint64_t root) {\n");
        foreach (uint advance in order.NicheAdvances)
        {
            source.Append("    {\n        RngState projected_niche=rng_initialize(root+plan_u64(35u));\n");
            if (pools)
            {
                // Preserve exactly the original six transitions and final draw.
                // Save only the words controlling outputs 0/3; no second replay.
                source.Append("        uint64_t k_pool_s1_0=projected_niche.s1;\n")
                    .Append("        for(uint i=0u;i<3u;++i) next_u64(projected_niche);\n")
                    .Append("        uint64_t k_pool_s1_3=projected_niche.s1;\n")
                    .Append("        for(uint i=0u;i<3u;++i) next_u64(projected_niche);\n");
            }
            else if (advance != 0)
                source.Append("        for(uint i=0u;i<").Append(advance.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append("u;++i) next_u64(projected_niche);\n");
            if (curse is not null)
                source.Append("        uint64_t final_mantissa=next_u64(projected_niche)>>11u;\n")
                    .Append("        bool final_ok=(").Append(curse.GlslExpression("final_mantissa")).Append(");\n");
            else
                source.Append("        uint projected_curse=other_card_ids.ids[n1c_meta(12u)+next_int(projected_niche,n1c_meta(13u))];\n")
                    .Append("        bool final_ok=final_curse_matches(projected_curse);\n");
            if (!pools) source.Append("        if(final_ok) return true;\n");
            else
            {
                source.Append("        if(!final_ok) return false;\n")
                    .Append("        uint64_t k_pool_draw0=(rotl64(k_pool_s1_0*uint64_t(5u),7u)*uint64_t(9u))>>11u;\n")
                    .Append("        uint64_t k_pool_draw3=(rotl64(k_pool_s1_3*uint64_t(5u),7u)*uint64_t(9u))>>11u;\n");
                string Presence(uint mask, string variable)
                {
                    if (!StaticDrawGateCompiler.TryCompileRanks(4,
                            rank => (mask & (15u ^ (1u << rank))) != 0, 2, out var gate))
                        throw new InvalidOperationException("N.StructuralPool4IntervalBudget");
                    return "(" + gate!.GlslExpression(variable) + ")";
                }
                string forward = Presence(target0, "k_pool_draw0") + " && " + Presence(target1, "k_pool_draw3");
                string reverse = Presence(target1, "k_pool_draw0") + " && " + Presence(target0, "k_pool_draw3");
                source.Append("        return (").Append(forward).Append(")")
                    .Append(ordered ? ";\n" : " || (" + reverse + ");\n");
            }
            source.Append("    }\n");
        }
        return source.Append("    return false;\n}\n").ToString();
    }
}

internal sealed partial class NeowFamilyGpuExecutor
{
    private readonly NeowStructuralTrialOptions _structuralTrials;
    private NeowStructuralPhysicalPlan _structuralPhysical = NeowStructuralPhysicalPlan.None;

    internal NeowStructuralPhysicalPlan StructuralPhysical => _structuralPhysical;
    internal bool StructuralObservedFor(bool compactAbi1Input) =>
        compactAbi1Input || _privateInput || !_plan.UsesStagedDense ? _pipeline.IsValid : _stagedModulesReady;
    internal string? StructuralRevisionFor(bool compactAbi1Input) => StructuralObservedFor(compactAbi1Input)
        ? compactAbi1Input || _privateInput
            ? _structuralPhysical.BonesIdentityOnly ? NeowStructuralCompiler.BonesRevision : null
            : _structuralPhysical.Revision : null;

    internal static string? RequestedStructuralRevisionFor(NeowFamilyGpuPlan plan, bool compactAbi1Input,
        NeowStructuralTrialOptions requested)
    {
        requested = requested.WithSettings();
        if (!requested.Any) return null;
        var order = NeowInternalFilterOrder.Compile(plan);
        var physical = NeowStructuralCompiler.Compile(plan, requested, order,
            !compactAbi1Input && RequestInternalFilterOrder && !RequestIndirectStaging && order.Enabled,
            ordinaryScope: true);
        return physical.Revision is { } revision ? revision + ".Requested" : null;
    }

    private void InitializeStructuralTrials() => _structuralPhysical = NeowStructuralCompiler.Compile(_plan,
        _structuralTrials, _internalFilterOrderPlan, InternalFilterOrderEnabled,
        _composite is null && _partySource is null && _bonesKMode == 0 && _capsulePhysicalMode == 0);

    private string ApplyStructuralTrials(string source, int stage)
    {
        if (stage != 3 || !_structuralPhysical.BonesIdentityOnly) return source;
        const string anchor = "    RngState rewards = rng_initialize(root + plan_u64(33u));\n    uint a,b;\n    return pair_identity(rewards,a,b) && local_matches(root,rewards,a,b);";
        // This is a pure root predicate. Earlier code has already verified the
        // top-level offer. This narrowly admitted body has no internal filter,
        // local result or exported Rewards/Niche state; downstream Families and
        // Exact replay their own state. Keep every earlier top predicate intact.
        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        int at = normalized.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0 || normalized.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("N.StructuralBonesOnlySourceSeamChanged");
        return normalized.Replace(anchor, "    return true; // RT2_BONES_IDENTITY_ONLY_NO_OBSERVED_INTERNAL_STATE", StringComparison.Ordinal);
    }
}
