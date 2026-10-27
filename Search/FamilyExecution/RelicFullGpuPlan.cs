using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace RolltheSpire2.Search.FamilyExecution;

// Rfull owns one initial World bag. Direct and fixed-Bones arrivals share the
// rarity/target-rank matcher, but compile distinct Rewards checkpoints.
internal sealed partial record RelicFullGpuPlan(RelicFamilyPlan SequencePlan, uint[] CapsuleMetadata, bool UsesBonesKBoundary = false)
{
    internal bool GenericReplay { get; init; }
    internal const string AlgorithmRevision =
        "R.Relic.Gpu.Rfull.v4.TrackedSequence.CapsuleRarityTrackedRank.EmptySequenceElided.LastTargetBucket";
    internal const string DenseRevision =
        "R.Relic.Rfull.DenseCarry8.CanonicalAbi1Ready.20260906.v4";
    internal const string CompactRevision =
        "R.Relic.Rfull.CompactAbi1FullDecode.CanonicalAbi1Ready.20260906.v4";

    internal const uint DirectArrival = 0;
    internal const uint FixedBonesArrival = 1;
    internal const uint GroupedBonesArrival = 2;
    internal const uint SourceConstrainedBonesArrival = 3;
    private readonly string _sharedPrefixRevision = SharedVisibleSeedGpuCache.RevisionSuffix;
    internal bool SourceConstrainedCapsules => CapsuleMetadata[0] == SourceConstrainedBonesArrival;
    internal string Revision(bool compact) => (GenericReplay ? "R.Relic.Gpu.GenericCapsule.20260922." + (compact ? "Compact" : "Dense") : SourceConstrainedCapsules
        ? $"R.Relic.Rfull.SourceConstrainedSmallLarge.{(compact ? "Compact" : "Dense")}.CanonicalAbi1Ready.20260913.v2"
        : CapsuleMetadata[0] == FixedBonesArrival
        ? $"R.Relic.Rfull.FixedBones.{(compact ? "Compact" : "Dense")}.CanonicalAbi1Ready.20260913.v5"
        : CapsuleMetadata[0] == GroupedBonesArrival && CapsuleMetadata[29] != 0
        ? $"R.Relic.Rfull.GroupedPinned.{(compact ? "Compact" : "Dense")}.CanonicalAbi1Ready.20260913.v5"
        : compact ? CompactRevision : DenseRevision) + _sharedPrefixRevision;
    private const int CurseIdsOffset = 32;
    private const int BonesIdsOffset = 42;
    private const int BucketLengthsOffset = 106;

    internal static bool TryCreate(
        ExactSearchExecutionRequest request,
        NeowReplayPlan route,
        out RelicFullGpuPlan? plan,
        out string issue)
    {
        if (request.Authority.PlayersCount > 1 && RolltheSpire2.Search.Semantics.PartyInitialQuery.CapsuleEffectPremise(request.CompiledSearch.NormalizedQuery)
            .Values.SelectMany(keys => keys).Any(k => !Core.Rewards.VanillaRelicRewardEffects.TryGet(request.ProfileId, k, out var e) ||
                !e.NestedOnObtainPreservesRewardContinuation)) return TryCreateGeneric(request, route, out plan, out issue);
        if (TryCreateCore(request, route, false, out plan, out issue)) return true;
        return request.Authority.PlayersCount > 1 && TryCreateGeneric(request, route, out plan, out issue);
    }

    // Only the bounded N physical supplies both actual Capsule arrivals. This
    // entry compiles immutable target metadata; standalone Rfull stays unchanged.
    internal static bool TryCreateBonesArcaneCheckpoint(
        ExactSearchExecutionRequest request, NeowReplayPlan route,
        out RelicFullGpuPlan? plan, out string issue)
        => TryCreateCore(request, route, true, out plan, out issue);

    // Explicit bounded private N/R experiment only; normal Rfull admission is unchanged.
    internal static bool TryCreateBonesKBoundary(ExactSearchExecutionRequest request, NeowReplayPlan route,
        out RelicFullGpuPlan? plan, out string issue)
    {
        plan = null;
        if (!route.Bones || !route.DirectNestedVanilla111 || request.Evaluation.RequiredBonesAcquisitionOrder.Count != 0 ||
            request.Evaluation.RequiredBonesCombination.Count != 2 ||
            !request.Evaluation.RequiredBonesCombination.ToHashSet().SetEquals([BaseGameModelKeys.Relics.LargeCapsule, BaseGameModelKeys.Relics.Kaleidoscope]) ||
            request.Evaluation.RelicSequenceConditions.Any(c => !c.IsEmpty) || request.Evaluation.RelicShopSequenceConditions.Any(c => !c.IsEmpty))
            return Unsupported("RfullBonesKBoundaryShape", out issue);
        return TryCreateCore(request, route, false, out plan, out issue, bonesKBoundary: true);
    }

    private static bool TryCreateCore(
        ExactSearchExecutionRequest request, NeowReplayPlan route, bool bonesArcaneCheckpoint,
        out RelicFullGpuPlan? plan, out string issue, bool bonesKBoundary = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);
        plan = null;
        issue = string.Empty;

        if (route.ExactOnly.Length != 0)
            return Unsupported("RfullProjectionContainsExactOnly:" + string.Join(',', route.ExactOnly), out issue);
        if (!route.Authority.IdentityAuthorityExact || route.Authority.EligibleCurseRelicIds.Length is < 1 or > 10)
            return Unsupported("RfullNeowIdentityAuthorityUnavailable", out issue);
        if (!request.Evaluation.CapsuleContainedRelics.IsEmpty || request.Evaluation.RequireWhetstone || request.Evaluation.RequireWarPaint)
            return Unsupported("RfullLegacyCapsuleProjectionUnsupported", out issue);

        NeowStructuredEffectSearchCondition[] conditions = request.Evaluation.StructuredNeowEffects
            .Where(condition => !condition.IsEmpty && NeowReplayPlan.IsCapsule(condition)).ToArray();
        ModelKey[] authoredPair = request.Evaluation.RequiredBonesAcquisitionOrder.Count == 2
            ? request.Evaluation.RequiredBonesAcquisitionOrder.ToArray() : request.Evaluation.RequiredBonesCombination.ToArray();
        bool sourceConstrained = conditions.Length is >= 1 and <= 2 && route.Bones && route.DirectNestedVanilla111 &&
            authoredPair.Length == 2 && authoredPair.ToHashSet().SetEquals([BaseGameModelKeys.Relics.SmallCapsule, BaseGameModelKeys.Relics.LargeCapsule]) &&
            conditions.All(c => !NeowReplayPlan.IsGroupedCapsule(c) && (c.SourceRelicKey == BaseGameModelKeys.Relics.SmallCapsule || c.SourceRelicKey == BaseGameModelKeys.Relics.LargeCapsule)) &&
            conditions.Select(c => c.SourceRelicKey).Distinct(ModelKeyComparer.Instance).Count() == conditions.Length;
        if (conditions.Length != 1 && !sourceConstrained)
            return Unsupported("RfullSingleCapsulePredicateRequired", out issue);

        // Source identity is immutable predicate metadata, not acquisition order.
        // Both sources always draw; either may have no predicate. Small has at most one required output; Large one or two
        // required targets. The matcher preserves both source partitions.
        if (sourceConstrained)
        {
            conditions = conditions.OrderBy(c => c.SourceRelicKey == BaseGameModelKeys.Relics.SmallCapsule ? 0 : 1).ToArray();
            if (conditions.Any(c => c.OutputKeys.Count < 1 || c.OutputKeys.Count > (c.SourceRelicKey == BaseGameModelKeys.Relics.SmallCapsule ? 1 : 2)) ||
                conditions.Any(c => c.AllowDuplicateOutputs || c.OutputKeys.Distinct(ModelKeyComparer.Instance).Count() != c.OutputKeys.Count))
                return Unsupported("RfullSourceConstrainedTargetShape", out issue);
        }

        NeowStructuredEffectSearchCondition condition = conditions[0];
        bool grouped = NeowReplayPlan.IsGroupedCapsule(condition);
        uint arrival;
        byte firstCapsule;
        byte secondCapsule = Beta110FastRelicCatalog.InvalidId;
        ModelKey[] targets = sourceConstrained ? conditions.SelectMany(c => c.OutputKeys).ToArray() : condition.OutputKeys.ToArray();
        byte[] fixedBones = [];
        int fixedBonesPosition0 = -1;
        int fixedBonesPosition1 = -1;
        bool exactBonesOrder = false;
        byte orderedFirst = Beta110FastRelicCatalog.InvalidId;
        byte orderedSecond = Beta110FastRelicCatalog.InvalidId;

        if (!route.Bones)
        {
            if (grouped || route.Selected is not (Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule) ||
                !Beta110FastRelicCatalog.TryGetId(condition.SourceRelicKey, out firstCapsule) || firstCapsule != route.Selected)
                return Unsupported("RfullDirectCapsuleSourceRequired", out issue);
            arrival = DirectArrival;
        }
        else
        {
            if (!route.Authority.BonesAuthorityExact || route.Authority.BonesEligibleRelicIds.Length is < 2 or > 64)
                return Unsupported("RfullBonesAuthorityUnavailable", out issue);
            ModelKey[] fixedKeys = request.Evaluation.RequiredBonesAcquisitionOrder.Count == 2
                ? request.Evaluation.RequiredBonesAcquisitionOrder.ToArray()
                : request.Evaluation.RequiredBonesCombination.ToArray();
            if (fixedKeys.Length != 2 || fixedKeys.Distinct(ModelKeyComparer.Instance).Count() != 2 ||
                fixedKeys.Any(key => !Beta110FastRelicCatalog.TryGetId(key, out _)))
                return Unsupported("RfullFixedBonesPairRequired", out issue);
            fixedBones = fixedKeys.Select(key => { Beta110FastRelicCatalog.TryGetId(key, out byte id); return id; }).ToArray();
            fixedBonesPosition0 = Array.IndexOf(route.Authority.BonesEligibleRelicIds, fixedBones[0]);
            fixedBonesPosition1 = Array.IndexOf(route.Authority.BonesEligibleRelicIds, fixedBones[1]);
            if (fixedBonesPosition0 < 0 || fixedBonesPosition1 < 0)
                return Unsupported("RfullFixedBonesPairOutsideAuthority", out issue);
            exactBonesOrder = request.Evaluation.RequiredBonesAcquisitionOrder.Count == 2;
            if (exactBonesOrder)
            {
                orderedFirst = fixedBones[0];
                orderedSecond = fixedBones[1];
            }

            if (sourceConstrained)
            {
                if (!fixedBones.ToHashSet().SetEquals([Beta110FastRelicCatalog.SmallCapsule, Beta110FastRelicCatalog.LargeCapsule]))
                    return Unsupported("RfullSourceConstrainedFixedSmallLargeRequired", out issue);
                arrival = SourceConstrainedBonesArrival;
                firstCapsule = Beta110FastRelicCatalog.SmallCapsule;
                secondCapsule = Beta110FastRelicCatalog.LargeCapsule;
            }
            else if (grouped)
            {
                // Grouped outputs are chronological multiset containment. Both
                // pickup orders consume the same three rarity draws and lanes;
                // pinning source assignment is irrelevant to this predicate.
                if (fixedBones.ToHashSet().SetEquals(
                        [Beta110FastRelicCatalog.SmallCapsule, Beta110FastRelicCatalog.LargeCapsule]) == false ||
                    targets.Length is < 1 or > 3)
                    return Unsupported("RfullGroupedSmallLargePairRequired", out issue);
                arrival = GroupedBonesArrival;
                firstCapsule = Beta110FastRelicCatalog.SmallCapsule;
                secondCapsule = Beta110FastRelicCatalog.LargeCapsule;
            }
            else
            {
                if (!Beta110FastRelicCatalog.TryGetId(condition.SourceRelicKey, out firstCapsule) ||
                    firstCapsule is not (Beta110FastRelicCatalog.SmallCapsule or Beta110FastRelicCatalog.LargeCapsule) ||
                    !fixedBones.Contains(firstCapsule))
                    return Unsupported("RfullBonesCapsuleSourceMissing", out issue);
                byte companion = fixedBones[0] == firstCapsule ? fixedBones[1] : fixedBones[0];
                bool companionPrecedesCapsule = exactBonesOrder && orderedSecond == firstCapsule;
                bool suppliedArcaneArrival = bonesArcaneCheckpoint &&
                    firstCapsule == Beta110FastRelicCatalog.LargeCapsule && companion == Beta110FastRelicCatalog.ArcaneScroll;
                bool suppliedKArrivals = bonesKBoundary && !exactBonesOrder &&
                    firstCapsule == Beta110FastRelicCatalog.LargeCapsule && companion == Beta110FastRelicCatalog.Kaleidoscope;
                if ((!exactBonesOrder || companionPrecedesCapsule) && !CapsuleContinuationNeutralCompanion(companion) &&
                    !suppliedArcaneArrival && !suppliedKArrivals)
                    return Unsupported("RfullPreCapsuleRewardsContinuationUnsupported", out issue);
                arrival = FixedBonesArrival;
            }
        }

        int drawCount = grouped || sourceConstrained ? 3 : firstCapsule == Beta110FastRelicCatalog.LargeCapsule ? 2 : 1;
        if (!grouped && !sourceConstrained && (targets.Length is < 1 or > 2 ||
                         (firstCapsule == Beta110FastRelicCatalog.SmallCapsule && targets.Length != 1) ||
                         targets.Distinct(ModelKeyComparer.Instance).Count() != targets.Length ||
                         condition.AllowDuplicateOutputs))
            return Unsupported("RfullCapsuleTargetShapeUnsupported", out issue);
        // Grouped truth is multiset containment in CPU replay / Exact. The editor's
        // duplicate-selection flag does not change that predicate or its GPU metadata.
        // Current UI emits false for distinct relic targets; legacy presets may emit true.
        if (grouped && (targets.Length is < 1 or > 3))
            return Unsupported("RfullGroupedMultisetShapeUnsupported", out issue);

        if (!RelicFamilyPlanCompiler.TryCompilePool(
                request, out RelicFamilyPool pool, out Dictionary<ModelKey, ushort> dense,
                out _, out issue))
            return false;

        // No fallback/Circlet specialization: every ordinary lane must remain
        // non-empty for the maximum number of Capsule draws in this plan.
        for (byte rarity = 1; rarity <= 3; rarity++)
        {
            int[] buckets = Enumerable.Range(0, pool.BucketCount)
                .Where(index => pool.BucketScopes[index] == 1 && pool.BucketKinds[index] == rarity).ToArray();
            if (buckets.Length != 1 || pool.BucketLengths[buckets[0]] < drawCount || pool.DrawDirections[buckets[0]] != 1)
                return Unsupported("RfullNonDepletingOrdinaryLanesRequired", out issue);
        }

        bool hasSequence = request.Evaluation.RelicSequenceConditions.Any(item => !item.IsEmpty) ||
                           request.Evaluation.RelicShopSequenceConditions.Any(item => !item.IsEmpty);
        RelicFamilyPlan sequencePlan;
        if (hasSequence)
        {
            if (!RelicFamilyPlanCompiler.TryCompile(request, out RelicFamilyPlan? compiled, out issue) || compiled is null)
                return false;
            sequencePlan = compiled;
        }
        else
        {
            sequencePlan = new RelicFamilyPlan(
                pool, [], [], [], new byte[4], new byte[4], [], new int[4], new byte[4], 0, false);
        }

        var metadata = new uint[BucketLengthsOffset + pool.BucketCount];
        metadata[0] = arrival;
        metadata[1] = firstCapsule;
        metadata[2] = secondCapsule;
        metadata[3] = checked((uint)targets.Length);
        for (int targetIndex = 0; targetIndex < 3; targetIndex++)
        {
            int offset = 4 + targetIndex * 3;
            metadata[offset] = metadata[offset + 1] = metadata[offset + 2] = uint.MaxValue;
        }
        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            if (!dense.TryGetValue(targets[targetIndex], out ushort denseId))
                return Unsupported("RfullCapsuleTargetMissingFromWorldPool", out issue);
            bool found = false;
            for (int bucket = 0; bucket < pool.BucketCount; bucket++)
            {
                if (pool.BucketScopes[bucket] != 1 || pool.BucketKinds[bucket] is < 1 or > 3) continue;
                int absolute = Array.IndexOf(pool.DenseRelicIds, denseId, pool.BucketOffsets[bucket], pool.BucketLengths[bucket]);
                if (absolute < 0) continue;
                int offset = 4 + targetIndex * 3;
                metadata[offset] = checked((uint)bucket);
                metadata[offset + 1] = checked((uint)(absolute - pool.BucketOffsets[bucket]));
                metadata[offset + 2] = pool.BucketKinds[bucket];
                found = true;
                break;
            }
            if (!found) return Unsupported("RfullCapsuleTargetNotOrdinaryRelic", out issue);
        }

        metadata[13] = checked((uint)pool.BucketCount);
        metadata[14] = checked((uint)route.Authority.PlayerSlotIndex);
        metadata[15] = checked((uint)route.Authority.PlayersCount);
        metadata[16] = (route.Authority.AllCharacterCardPoolsUnlocked ? 4u : 0u) |
                       (route.Authority.ScrollBoxesAllowed ? 8u : 0u) |
                       (sourceConstrained && conditions.All(c => c.SourceRelicKey != BaseGameModelKeys.Relics.SmallCapsule) ? 16u : 0u);
        metadata[17] = checked((uint)route.Authority.EligibleCurseRelicIds.Length);
        WriteHash(metadata, 18, XxHash64.Hash("NEOW"u8, 0));
        WriteHash(metadata, 20, NeowFamilyReplay.RewardsHash);
        WriteHash(metadata, 22, XxHash64.Hash("up_front"u8, 0));
        metadata[24] = checked((uint)route.Authority.BonesEligibleRelicIds.Length);
        metadata[25] = fixedBones.Length == 2 ? fixedBones[0] : uint.MaxValue;
        metadata[26] = fixedBonesPosition0 >= 0 ? checked((uint)fixedBonesPosition0) : uint.MaxValue;
        metadata[27] = fixedBones.Length == 2 ? fixedBones[1] : uint.MaxValue;
        metadata[28] = fixedBonesPosition1 >= 0 ? checked((uint)fixedBonesPosition1) : uint.MaxValue;
        metadata[29] = exactBonesOrder ? 1u : 0u;
        metadata[30] = orderedFirst;
        metadata[31] = orderedSecond;
        for (int index = 0; index < route.Authority.EligibleCurseRelicIds.Length; index++)
            metadata[CurseIdsOffset + index] = route.Authority.EligibleCurseRelicIds[index];
        for (int index = 0; index < route.Authority.BonesEligibleRelicIds.Length; index++)
            metadata[BonesIdsOffset + index] = route.Authority.BonesEligibleRelicIds[index];
        for (int bucket = 0; bucket < pool.BucketCount; bucket++)
            metadata[BucketLengthsOffset + bucket] = checked((uint)pool.BucketLengths[bucket]);

        plan = new RelicFullGpuPlan(sequencePlan, metadata, bonesKBoundary);
        return true;
    }

    // This is intentionally an allow-list. Rfull's fixed-Bones kernel evaluates
    // the target Capsule at RewardsAfterBones, so an Any-order route is safe only
    // when the other pickup cannot change Capsule Rewards rolls or Relic Bag lanes.
    // Deterministic gold/card/potion/deck changes are not Rewards or bag changes.
    // This is NOT a claim that the pickup leaves all player state unchanged.
    // Census: CAPSULE_COMPOSITION_FOUNDATION_20260913.md. MassiveScroll consumes
    // Rewards (and is outside the single-player pool), so is not neutral.
    internal static bool CapsuleContinuationNeutralCompanion(byte relic) => relic is
        Beta110FastRelicCatalog.CursedPearl or
        Beta110FastRelicCatalog.DowsingRod or
        Beta110FastRelicCatalog.NeowsSacrifice or
        Beta110FastRelicCatalog.PrecariousShears or
        Beta110FastRelicCatalog.SilkenTress or
        Beta110FastRelicCatalog.SilverCrucible or
        Beta110FastRelicCatalog.BoomingConch or
        Beta110FastRelicCatalog.FishingRod or
        Beta110FastRelicCatalog.GoldenPearl or
        Beta110FastRelicCatalog.LeafyPoultice or
        Beta110FastRelicCatalog.NeowsTorment or
        Beta110FastRelicCatalog.NewLeaf or
        Beta110FastRelicCatalog.PhialHolster or
        Beta110FastRelicCatalog.PreciseScissors or
        Beta110FastRelicCatalog.WingedBoots or
        Beta110FastRelicCatalog.LavaRock or
        Beta110FastRelicCatalog.NeowsTalisman or
        Beta110FastRelicCatalog.NutritiousOyster or
        Beta110FastRelicCatalog.Pomander or
        Beta110FastRelicCatalog.StoneHumidifier;

    private static void WriteHash(uint[] metadata, int offset, ulong value)
    {
        metadata[offset] = unchecked((uint)value);
        metadata[offset + 1] = unchecked((uint)(value >> 32));
    }

    private static bool Unsupported(string reason, out string issue)
    {
        issue = reason;
        return false;
    }
}
