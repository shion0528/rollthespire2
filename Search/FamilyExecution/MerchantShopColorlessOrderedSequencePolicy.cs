using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Merchant;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

// Pure GPU numerical-plan lowering. The authored query, CPU realization and
// Production Exact keep the original sequence semantics and fingerprint.
internal sealed record MerchantOrderedSequenceSlotsPlan(
    ExactSearchEvaluationProjection Evaluation, bool Requested, bool Applied,
    int ExpandedSequences, int AddedSlots, int OriginalMaximumOrdinal,
    int PackedMaximumOrdinal, string Reason)
{
    internal string RevisionSuffix => Applied ? "." + MerchantShopColorlessOrderedSequencePolicy.Revision : "";
}

internal static class MerchantShopColorlessOrderedSequencePolicy
{
    internal const string Revision = "OrderedSequenceAsSlots.20261025.v1";
    internal const string DiagnosticVariable = "RT2_S_ORDERED_SEQUENCE_SLOTS";

    internal static bool Requested(SearchOptimizationSettings settings)
    {
        // The player's existing filter-order switch controls the released path.
        // Diagnostics can retain the measured all-off arm without changing the
        // default used by either build or bypassing the player's setting.
        if (!settings.CostOrderedFilters) return false;
#if RT2_GPU_PROFILING
        return System.Environment.GetEnvironmentVariable(DiagnosticVariable) != "0";
#else
        return true;
#endif
    }

    internal static MerchantOrderedSequenceSlotsPlan Compile(ExactSearchExecutionRequest request,
        bool? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool requested = (enabled ?? Requested(request.RunOptions.Optimizations)) &&
            request.RunOptions.Optimizations.CostOrderedFilters;
        return Compile(request.Evaluation, Beta111MerchantColorlessAuthority.From(request.Authority), requested);
    }

    internal static int MaximumOrdinal(ExactSearchEvaluationProjection evaluation) =>
        evaluation.MerchantColorlessConditions.Select(c => c.MerchantOrdinal)
            .Concat(evaluation.MerchantColorlessSequenceConditions.Select(c => c.Count))
            .DefaultIfEmpty(0).Max();

    internal static MerchantOrderedSequenceSlotsPlan Compile(ExactSearchEvaluationProjection evaluation,
        Beta111MerchantColorlessAuthority authority, bool requested)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(authority);
        int oldMaximum = MaximumOrdinal(evaluation);
        MerchantOrderedSequenceSlotsPlan Fallback(string reason) =>
            new(evaluation, requested, false, 0, 0, oldMaximum, oldMaximum, reason);
        if (!requested) return Fallback("DiagnosticControlOrCostOrderedFiltersDisabled");
        if (!authority.HasExactV1Inputs) return Fallback("CapturedMerchantAuthorityRequired");
        var ordered = evaluation.MerchantColorlessSequenceConditions.Where(c =>
            c.OrderMode == CombatRewardSequenceOrderMode.Ordered).ToArray();
        if (ordered.Length == 0) return Fallback("NoOrderedSequences");
        if (evaluation.MerchantColorlessSequenceConditions.Any(c => c.Count is < 1 or > 5 ||
            c.Slots.Count != c.Count || c.Slot is not (MerchantColorlessSlot.Uncommon or MerchantColorlessSlot.Rare) ||
            c.OrderMode is not (CombatRewardSequenceOrderMode.Ordered or CombatRewardSequenceOrderMode.Unordered)))
            return Fallback("SequenceShapeNotProven");
        var additions = new List<MerchantColorlessSlotCondition>();
        foreach (var sequence in ordered)
        {
            if (sequence.Count is < 1 or > Beta111NormalMerchantColorlessSequenceProjector.MerchantCount ||
                sequence.Slots.Count != sequence.Count ||
                sequence.Slot is not (MerchantColorlessSlot.Uncommon or MerchantColorlessSlot.Rare))
                return Fallback("OrderedSequenceShapeNotProven");
            IReadOnlyList<ModelKey> pool = sequence.Slot == MerchantColorlessSlot.Uncommon
                ? authority.UncommonPool : authority.RarePool;
            for (int ordinal = 0; ordinal < sequence.Count; ordinal++)
            {
                if (sequence.Slots[ordinal] is not { } key) continue;
                // Old GPU packing conflates an unknown target and a wildcard.
                // Leave that unsupported Fast shape unchanged for Exact rather
                // than silently changing the old necessary-condition boundary.
                if (!key.IsValid || !pool.Contains(key))
                    return Fallback("OrderedTargetAbsentFromCapturedPool");
                additions.Add(new(ordinal + 1, sequence.Slot, key));
            }
        }
        // Keep duplicate/conflicting fixed rows as their original conjunction.
        // Repeated targets at different ordinals remain different observations.
        var physical = evaluation with
        {
            MerchantColorlessConditions = evaluation.MerchantColorlessConditions.Concat(additions).ToArray(),
            MerchantColorlessSequenceConditions = evaluation.MerchantColorlessSequenceConditions.Where(c =>
                c.OrderMode != CombatRewardSequenceOrderMode.Ordered).ToArray()
        };
        return new(physical, requested, true, ordered.Length, additions.Count, oldMaximum,
            MaximumOrdinal(physical), "OrderedNonWildcardPositionsLoweredToExistingSlots.UnorderedUnchanged");
    }
}
