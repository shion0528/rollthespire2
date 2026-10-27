using System.Globalization;
using System.Text;

namespace RolltheSpire2.Search.FamilyExecution;

// Pure planning over the same immutable numerical ABI as the ordinary solo N
// matcher. The caller additionally excludes composites and private transports.
internal sealed record NeowInternalFilterOrderPlan(
    bool Enabled, uint[] NicheAdvances, double AcceptedFraction, string Reason)
{
    internal NeowInternalFilterOrderCostModel? CostModel { get; init; }

    // This is only a necessary predicate. It neither supplies nor changes the
    // continuation state used by the original pair/local matchers.
    internal string Source
    {
        get
        {
            if (!Enabled) return "bool internal_final_curse_gate(uint64_t root) { return true; }\n";
            if (AcceptedFraction == 0) return "bool internal_final_curse_gate(uint64_t root) { return false; }\n";
            var source = new StringBuilder("bool internal_final_curse_gate(uint64_t root) {\n");
            foreach (uint advance in NicheAdvances)
            {
                source.Append("    {\n        RngState projected_niche=rng_initialize(root+plan_u64(35u));\n");
                if (advance != 0)
                    source.Append("        for(uint i=0u;i<")
                        .Append(advance.ToString(CultureInfo.InvariantCulture))
                        .Append("u;++i) next_u64(projected_niche);\n");
                source.Append("        uint projected_curse=other_card_ids.ids[n1c_meta(12u)+next_int(projected_niche,n1c_meta(13u))];\n")
                    .Append("        if(final_curse_matches(projected_curse)) return true;\n    }\n");
            }
            return source.Append("    return false;\n}\n").ToString();
        }
    }
}

// Deliberately coarse integer-operation units, not measured nanoseconds. The
// admission comparison conservatively credits only pair work saved, omitting
// every later local check. Probabilities affect ordering, never acceptance.
internal sealed record NeowInternalFilterOrderCostModel(
    double GateWorkUnits, double PairWorkUnits, double PairSurvivalEstimate,
    double GateSurvivalUpperBound, double EstimatedReorderedWorkUnits)
{
    internal string Model => "NicheFixedRank.PairOnlySavedWork.20261022.v1";
    internal double GateCost => GateWorkUnits;
    internal double PairCost => PairWorkUnits;
    internal double RejectionLowerBound => 1 - GateSurvivalUpperBound;
    internal double EstimatedGainFraction => PairWorkUnits == 0 ? 0 :
        1 - EstimatedReorderedWorkUnits / PairWorkUnits;
    internal double GateCostPerRejected => GateSurvivalUpperBound >= 1 ? double.PositiveInfinity :
        GateWorkUnits / (1 - GateSurvivalUpperBound);
    internal double PairCostPerRejected => PairSurvivalEstimate >= 1 ? double.PositiveInfinity :
        PairWorkUnits / (1 - PairSurvivalEstimate);
}

internal static class NeowInternalFilterOrder
{
    internal const uint MaximumNicheAdvance = 128;
    internal const string Revision = "FinalCurseBeforeFixedPair.CostOrdered.20261022.v1";

    internal static NeowInternalFilterOrderPlan Compile(NeowFamilyGpuPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NeowInternalFilterOrderPlan Fallback(string reason) => new(false, [], double.NaN, reason);
        uint[] meta = plan.Meta;
        if (meta.Length < 85) return Fallback("MetadataTooShort");
        if (meta[5] != 0 || meta[6] != 1) return Fallback("SingleplayerOnly");
        if (meta[66] == 0 || meta[67] != 1 || (meta[56] & 2u) == 0)
            return Fallback("StagedFixedPairRequired");
        if (meta[83] == 0) return Fallback("FinalCursePredicateRequired");
        if (plan.DirectNestedMode != 0) return Fallback("OrdinaryLocalRouteRequired");
        if (plan.JointTransformMetaOffset >= 0) return Fallback("JointTransformUsesCanonicalOrder");
        if ((meta[82] & 2u) == 0) return Fallback("NicheStreamRequired");
        if (meta[8] < 2 || meta[8] > 64 || plan.Bones.Length < meta[8] ||
            meta[25] >= meta[8] || meta[26] >= meta[8] || meta[25] == meta[26] ||
            plan.Bones[meta[25]] != meta[27] || plan.Bones[meta[26]] != meta[28] || meta[27] == meta[28])
            return Fallback("TrackedPairMetadataInvalid");
        uint a = meta[27], b = meta[28];
        int copiesA = 0, copiesB = 0;
        for (int i = 0; i < meta[8]; ++i)
        {
            if (plan.Bones[i] == a) ++copiesA;
            if (plan.Bones[i] == b) ++copiesB;
        }
        if (copiesA != 1 || copiesB != 1) return Fallback("TrackedPairIdentityNotUnique");
        if (85UL + meta[84] > (ulong)meta.Length) return Fallback("StructuredSourceMetadataTooShort");
        for (uint i = 0; i < meta[84]; ++i)
            if (meta[85 + i] != a && meta[85 + i] != b)
                return Fallback("StructuredSourceOutsideTrackedPair");
        if (!TryNicheFootprint(a, meta[9], out uint aDraws) ||
            !TryNicheFootprint(b, meta[9], out uint bDraws))
            return Fallback("UnknownNicheFootprint");

        (uint First, uint Second)[] routes;
        if (meta[12] == 255 && meta[13] == 255) routes = [(a, b), (b, a)];
        else if (meta[12] == 255 || meta[13] == 255) return Fallback("IncompleteAcquisitionOrder");
        else if ((meta[12] == a && meta[13] == b) || (meta[12] == b && meta[13] == a))
            routes = [(meta[12], meta[13])];
        else return Fallback("AcquisitionOrderDoesNotMatchTrackedPair");

        if (plan.HasAuthoredUpgrades && plan.Conditions.Length < 226)
            return Fallback("AuthoredAdvanceMetadataTooShort");
        var advances = new SortedSet<uint>();
        foreach (var route in routes)
        {
            uint firstAdvance = AuthoredAdvance(plan, route.First, 255);
            uint secondAdvance = AuthoredAdvance(plan, route.Second, route.First);
            // Canonical Route returns true before its local/final-curse checks
            // when an authored advance is unknown. This gate must do the same.
            if (firstAdvance == uint.MaxValue || secondAdvance == uint.MaxValue)
                return Fallback("UnknownAuthoredAdvance");
            ulong total = (ulong)aDraws + bDraws + firstAdvance + secondAdvance;
            if (total > MaximumNicheAdvance) return Fallback("AdvanceBudgetExceeded");
            advances.Add((uint)total);
        }

        // This is the donor n1c_meta layout: required at +6/+7, banned at +8/+9,
        // generated curses at +12/+13. Validate ranges before reading any data.
        ulong trailer64 = 20UL + (ulong)meta[9] * 6;
        if (trailer64 + 13 >= (ulong)plan.PoolMeta.Length)
            return Fallback("CurseMetadataTooShort");
        int trailer = (int)trailer64;
        uint requiredOffset = plan.PoolMeta[trailer + 6], requiredCount = plan.PoolMeta[trailer + 7];
        uint bannedOffset = plan.PoolMeta[trailer + 8], bannedCount = plan.PoolMeta[trailer + 9];
        uint curseOffset = plan.PoolMeta[trailer + 12], curseCount = plan.PoolMeta[trailer + 13];
        bool Fits(uint offset, uint count) => (ulong)offset + count <= (ulong)plan.Cards.Length;
        if (curseCount == 0 || !Fits(requiredOffset, requiredCount) ||
            !Fits(bannedOffset, bannedCount) || !Fits(curseOffset, curseCount))
            return Fallback("CursePoolMetadataInvalid");

        // Required IDs are a conjunction, not a set union. Repeated pool IDs
        // retain all their probability mass. Exact 53-bit bucket widths include
        // NextInt's tiny finite-mantissa bias in this scheduling estimate.
        ulong acceptedMantissas = 0;
        for (int rank = 0; rank < (int)curseCount; ++rank)
        {
            uint id = plan.Cards[(int)curseOffset + rank];
            bool accepted = true;
            for (uint i = 0; i < requiredCount; ++i)
                accepted &= plan.Cards[requiredOffset + i] == id;
            for (uint i = 0; i < bannedCount; ++i)
                accepted &= plan.Cards[bannedOffset + i] != id;
            if (accepted) acceptedMantissas += StaticDrawGateCompiler.Boundary(rank + 1, (int)curseCount) -
                StaticDrawGateCompiler.Boundary(rank, (int)curseCount);
        }
        double fraction = acceptedMantissas / (double)StaticDrawGateCompiler.MantissaSpace;
        uint[] offsets = advances.ToArray();
        // OR routes can be correlated. A union bound is conservative and needs
        // no independence assumption for different offsets from one RNG stream.
        double survivalBound = Math.Min(1, fraction * offsets.Length);
        const double initialize = 32, transition = 6, observeAndRank = 10, trackedSwap = 4;
        double gateWork = fraction == 0 ? 1 : offsets.Sum(offset =>
            initialize + offset * transition + observeAndRank + 1 + requiredCount + bannedCount);
        double pairWork = initialize + (meta[8] - 1) * (transition + observeAndRank + trackedSwap);
        double pairSurvival = 2.0 / (meta[8] * (meta[8] - 1.0));
        var costs = new NeowInternalFilterOrderCostModel(gateWork, pairWork, pairSurvival,
            survivalBound, gateWork + survivalBound * pairWork);
        bool enabled = costs.EstimatedReorderedWorkUnits < pairWork;
        return new(enabled, offsets, fraction, enabled ? "FixedNicheFinalCurseBeforePair" : "ModeledSavingInsufficient")
        { CostModel = costs };
    }

    private static uint AuthoredAdvance(NeowFamilyGpuPlan plan, uint source, uint prior)
    {
        if (!plan.HasAuthoredUpgrades || source is not (Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule))
            return 0;
        int row = source == Beta110FastRelicCatalog.SmallCapsule ? 0 : 33;
        return plan.Conditions[160 + row + (prior == 255 ? 32 : (int)prior)];
    }

    private static bool TryNicheFootprint(uint source, uint otherPools, out uint draws)
    {
        draws = 0;
        if (source == Beta110FastRelicCatalog.Kaleidoscope)
        {
            if (otherPools is < 3 or > 16) return false;
            draws = 2 * (otherPools - 1);
            return true;
        }
        if (source == Beta110FastRelicCatalog.NewLeaf) { draws = 1; return true; }
        // Explicitly audited solo operators: other RNG streams, deterministic
        // opening effects, or player choices that consume no Niche draw. Do not
        // turn this into an ID-range check: new/recursive operators need audit.
        return source is Beta110FastRelicCatalog.CursedPearl or Beta110FastRelicCatalog.DowsingRod or
            Beta110FastRelicCatalog.HeftyTablet or Beta110FastRelicCatalog.LargeCapsule or
            Beta110FastRelicCatalog.LeafyPoultice or Beta110FastRelicCatalog.NeowsSacrifice or
            Beta110FastRelicCatalog.PrecariousShears or Beta110FastRelicCatalog.SilkenTress or
            Beta110FastRelicCatalog.SilverCrucible or Beta110FastRelicCatalog.ArcaneScroll or
            Beta110FastRelicCatalog.BoomingConch or Beta110FastRelicCatalog.FishingRod or
            Beta110FastRelicCatalog.GoldenPearl or Beta110FastRelicCatalog.LeadPaperweight or
            Beta110FastRelicCatalog.LostCoffer or Beta110FastRelicCatalog.NeowsTorment or
            Beta110FastRelicCatalog.PhialHolster or Beta110FastRelicCatalog.PreciseScissors or
            Beta110FastRelicCatalog.ScrollBoxes or Beta110FastRelicCatalog.WingedBoots or
            Beta110FastRelicCatalog.LavaRock or Beta110FastRelicCatalog.NeowsTalisman or
            Beta110FastRelicCatalog.NutritiousOyster or Beta110FastRelicCatalog.Pomander or
            Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.StoneHumidifier;
    }
}
