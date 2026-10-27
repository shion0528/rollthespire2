using System.Globalization;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Merchant;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Selectivity;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// S-local analytical projection of the current Merchant replay/checkpoint order.
/// Probability comes only from Query and immutable runtime card-pool authority.
/// </summary>
internal static class MerchantShopColorlessExpectedFilteringCost
{
    internal static FamilyExpectedFilteringCostProjection Project(
        ExactSearchExecutionRequest request,
        FamilyAnalyticalCostProjection fullCost,
        FamilySurvivalProjection survival)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ProjectPhysical(request, request.Evaluation, fullCost, survival);
    }

    internal static FamilyExpectedFilteringCostProjection ProjectPhysical(
        ExactSearchExecutionRequest request, ExactSearchEvaluationProjection physicalEvaluation,
        FamilyAnalyticalCostProjection fullCost, FamilySurvivalProjection survival)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(physicalEvaluation);
        ArgumentNullException.ThrowIfNull(fullCost);
        ArgumentNullException.ThrowIfNull(survival);

        if (!survival.IsResolved)
            return Unresolved(fullCost, survival.SurvivalProbability, "FinalSurvivalUnresolved:" + survival.Evidence);

        Beta111MerchantColorlessAuthority authority = Beta111MerchantColorlessAuthority.From(request.Authority);
        if (!authority.HasExactV1Inputs)
            return Unresolved(fullCost, survival.SurvivalProbability, "MerchantColorlessAuthorityUnavailable");

        try
        {
            return Build(physicalEvaluation, fullCost, authority, survival.SurvivalProbability);
        }
        catch (Exception ex)
        {
            return Unresolved(fullCost, survival.SurvivalProbability,
                "MerchantExpectedFilteringProjectionFailed:" + ex.GetType().Name + ':' + ex.Message);
        }
    }

    internal static FamilyExpectedFilteringCostProjection ProjectForTesting(
        ExactSearchEvaluationProjection evaluation,
        FamilyAnalyticalCostProjection fullCost,
        Beta111MerchantColorlessAuthority authority) =>
        Build(evaluation, fullCost, authority, authoritativeFinalSurvival: null);

    private static FamilyExpectedFilteringCostProjection Build(
        ExactSearchEvaluationProjection evaluation,
        FamilyAnalyticalCostProjection fullCost,
        Beta111MerchantColorlessAuthority authority,
        double? authoritativeFinalSurvival)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(fullCost);
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.UncommonPool.Count == 0 || authority.RarePool.Count == 0)
            throw new InvalidOperationException("SExpectedCostPoolUnavailable");

        MerchantColorlessSlotCondition[] slots = evaluation.MerchantColorlessConditions.ToArray();
        MerchantColorlessSequenceSearchCondition[] sequences =
            evaluation.MerchantColorlessSequenceConditions.ToArray();
        int maximumOrdinal = slots.Select(condition => condition.MerchantOrdinal)
            .Concat(sequences.Select(condition => condition.Count))
            .DefaultIfEmpty(0)
            .Max();
        if (maximumOrdinal is < 0 or > Beta111NormalMerchantColorlessSequenceProjector.MerchantCount)
            throw new InvalidOperationException("SExpectedCostMaximumOrdinalOutsidePhysicalRange:" + maximumOrdinal);

        var segments = new List<FamilyExpectedFilteringCostSegment>();
        void Add(string name, FamilyAnalyticalOperation operation, double work, double reach)
        {
            if (!double.IsFinite(work) || work < 0d)
                throw new InvalidOperationException("SExpectedCostInvalidWork:" + name);
            if (!double.IsFinite(reach) || reach is < 0d or > 1d)
                throw new InvalidOperationException("SExpectedCostInvalidReach:" + name);
            if (work > 0d)
                segments.Add(new FamilyExpectedFilteringCostSegment(name, operation, work, reach));
        }

        var acceptedSlots = new Dictionary<MerchantColorlessSlot, List<MerchantColorlessSlotCondition>>
        {
            [MerchantColorlessSlot.Uncommon] = [],
            [MerchantColorlessSlot.Rare] = []
        };
        var acceptedSequences = new Dictionary<MerchantColorlessSlot, List<MerchantColorlessSequenceSearchCondition>>
        {
            [MerchantColorlessSlot.Uncommon] = [],
            [MerchantColorlessSlot.Rare] = []
        };

        double Reach() => CombinedReach(authority, acceptedSlots, acceptedSequences);
        double currentReach = 1d;

        Add("S.InputPrefix.CandidateReconstruction", FamilyAnalyticalOperation.CandidateReconstruction, 1d, 1d);
        Add("S.InputPrefix.RngInitialization", FamilyAnalyticalOperation.RngInitialization,
            maximumOrdinal > 0 ? 1d : 0d, 1d);
        Add("S.InputPrefix.MaximumOrdinalSlotRows", FamilyAnalyticalOperation.PredicateProbe, slots.Length, 1d);
        Add("S.InputPrefix.MaximumOrdinalSequenceRows", FamilyAnalyticalOperation.PredicateProbe, sequences.Length, 1d);

        for (int merchant = 1; merchant <= maximumOrdinal; merchant++)
        {
            Add($"S.MerchantReplay[{merchant}].RngAdvance", FamilyAnalyticalOperation.RngAdvance, 26d, currentReach);
            Add($"S.MerchantReplay[{merchant}].HistoricalNextInt", FamilyAnalyticalOperation.HistoricalNextInt, 2d, currentReach);
            Add($"S.MerchantReplay[{merchant}].Observations", FamilyAnalyticalOperation.ObservationWrite, 2d, currentReach);

            for (int row = 0; row < slots.Length; row++)
            {
                Add($"S.Merchant[{merchant}].SlotPlanRowScan[{row}]",
                    FamilyAnalyticalOperation.PredicateProbe, 1d, currentReach);
                MerchantColorlessSlotCondition condition = slots[row];
                if (condition.MerchantOrdinal != merchant) continue;
                Add($"S.Merchant[{merchant}].SlotIdentityProbe[{row}]",
                    FamilyAnalyticalOperation.PredicateProbe, 1d, currentReach);
                acceptedSlots[condition.Slot].Add(condition);
                currentReach = Reach();
            }

            for (int row = 0; row < sequences.Length; row++)
            {
                Add($"S.Merchant[{merchant}].SequencePlanRowScan[{row}]",
                    FamilyAnalyticalOperation.PredicateProbe, 1d, currentReach);
                MerchantColorlessSequenceSearchCondition condition = sequences[row];
                if (condition.Count != merchant) continue;
                if (condition.Slots.Count != condition.Count)
                    throw new InvalidOperationException("SExpectedCostSequenceSlotsDoNotMatchHorizon:" + row);

                if (condition.OrderMode == CombatRewardSequenceOrderMode.Ordered)
                {
                    for (int target = 0; target < condition.Slots.Count; target++)
                    {
                        if (condition.Slots[target] is not ModelKey key) continue;
                        Add($"S.Merchant[{merchant}].OrderedTargetProbe[{row},{target}]",
                            FamilyAnalyticalOperation.PredicateProbe, 1d, currentReach);
                        acceptedSlots[condition.Slot].Add(new MerchantColorlessSlotCondition(
                            target + 1, condition.Slot, key));
                        currentReach = Reach();
                    }
                    continue;
                }

                if (condition.OrderMode != CombatRewardSequenceOrderMode.Unordered)
                    throw new InvalidOperationException("SExpectedCostSequenceModeUnsupported:" + row);

                for (int target = 0; target < condition.Slots.Count; target++)
                {
                    if (condition.Slots[target] is not ModelKey key) continue;
                    Add($"S.Merchant[{merchant}].UnorderedMultiplicityProbe[{row},{target}]",
                        FamilyAnalyticalOperation.PredicateProbe,
                        condition.Slots.Count + condition.Count,
                        currentReach);
                    int requested = condition.Slots.Count(item =>
                        item is ModelKey value && ModelKeyComparer.Instance.Equals(value, key));
                    acceptedSequences[condition.Slot].Add(UnorderedTargetRequirement(
                        condition.Count, condition.Slot, key, requested));
                    currentReach = Reach();
                }
            }
        }

        double finalSurvival = authoritativeFinalSurvival ?? currentReach;
        if (authoritativeFinalSurvival.HasValue &&
            !NearlyEqual(authoritativeFinalSurvival.Value, currentReach))
        {
            throw new InvalidOperationException(
                $"SExpectedCostFinalSurvivalMismatch:authority={F(authoritativeFinalSurvival.Value)};segments={F(currentReach)}");
        }

        Add("S.OutputCompaction", FamilyAnalyticalOperation.OutputCompaction,
            fullCost.WorkUnitsPerOutput, finalSurvival);
        return Complete(
            fullCost,
            segments,
            currentReach,
            authoritativeFinalSurvival,
            "MerchantReplacementPrefixReach;DistinctLanePolicy=AcceptedModel;RuntimeSurvivalUsed=false");
    }

    private static double CombinedReach(
        Beta111MerchantColorlessAuthority authority,
        IReadOnlyDictionary<MerchantColorlessSlot, List<MerchantColorlessSlotCondition>> acceptedSlots,
        IReadOnlyDictionary<MerchantColorlessSlot, List<MerchantColorlessSequenceSearchCondition>> acceptedSequences)
    {
        double probability = 1d;
        foreach (MerchantColorlessSlot lane in Enum.GetValues<MerchantColorlessSlot>())
        {
            IReadOnlyList<ModelKey> pool = lane == MerchantColorlessSlot.Uncommon
                ? authority.UncommonPool
                : authority.RarePool;
            if (!ShopSequenceProbabilityEstimator.TrySolveColorlessConjunction(
                    pool,
                    acceptedSlots[lane],
                    acceptedSequences[lane],
                    out double laneProbability,
                    out string issue))
            {
                throw new InvalidOperationException("SExpectedCostPrefixProbabilityUnavailable:" + lane + ':' + issue);
            }
            probability *= laneProbability;
            if (probability == 0d) break;
        }
        return Math.Clamp(probability, 0d, 1d);
    }

    private static MerchantColorlessSequenceSearchCondition UnorderedTargetRequirement(
        int horizon,
        MerchantColorlessSlot lane,
        ModelKey key,
        int requested)
    {
        ModelKey?[] slots = Enumerable.Repeat<ModelKey?>(key, requested)
            .Concat(Enumerable.Repeat<ModelKey?>(null, Math.Max(0, horizon - requested)))
            .Take(horizon)
            .ToArray();
        return new MerchantColorlessSequenceSearchCondition(
            horizon,
            CombatRewardSequenceOrderMode.Unordered,
            lane,
            slots);
    }

    private static FamilyExpectedFilteringCostProjection Complete(
        FamilyAnalyticalCostProjection fullCost,
        IReadOnlyList<FamilyExpectedFilteringCostSegment> segments,
        double calculatedFinalSurvival,
        double? authoritativeFinalSurvival,
        string evidence)
    {
        double segmentedFull = segments.Sum(segment => segment.FullWorkUnits);
        double flatFull = fullCost.WorkUnitsPerInput + fullCost.WorkUnitsPerOutput;
        if (!NearlyEqual(segmentedFull, flatFull))
        {
            throw new InvalidOperationException(
                $"SExpectedCostFullSegmentationMismatch:flat={F(flatFull)};segments={F(segmentedFull)}");
        }

        return new FamilyExpectedFilteringCostProjection(
            "S.MerchantShopColorless",
            flatFull,
            segments.Sum(segment => segment.ExpectedContribution),
            authoritativeFinalSurvival ?? calculatedFinalSurvival,
            fullCost.CompactInputWorkUnitsPerInput,
            segments,
            evidence);
    }

    private static FamilyExpectedFilteringCostProjection Unresolved(
        FamilyAnalyticalCostProjection fullCost,
        double? finalSurvival,
        string evidence) => new(
        "S.MerchantShopColorless",
        fullCost.WorkUnitsPerInput + fullCost.WorkUnitsPerOutput,
        null,
        finalSurvival,
        fullCost.CompactInputWorkUnitsPerInput,
        [],
        evidence);

    private static bool NearlyEqual(double left, double right)
    {
        double scale = Math.Max(1d, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= 1e-11 * scale;
    }

    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}
