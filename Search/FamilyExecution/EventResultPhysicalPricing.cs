using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

internal static class EventResultPhysicalPricing
{
    internal static FamilyPhysicalQuote? QuoteCpuCompiledColor(ExactSearchExecutionRequest request,
        EventResultFamilyPlan plan, EventResultCpuPlan compiled, FamilyPhysicalQuoteRequest g)
    {
        if (!request.Authority.CanUseCurrentModel || request.Authority.PlayersCount != 1 || plan.PlayerSlot != 0 ||
            !FamilyPhysicalQuote.AdmittedRequest(g) || g.CompactInput || g.PrivateInput || g.PrivateOutput ||
            g.MeanInputPopulation < 1024 || compiled.Remaining.Length > 4) return null;
        double residual = 0;
        foreach (var condition in compiled.Remaining)
        {
            double? cost = condition.Kind switch {
                EventResultConditionKind.FakeMerchantOfferedFakeRelic => 100,
                EventResultConditionKind.TrashHeapGrabCard or EventResultConditionKind.TrashHeapDiveRelic => 45,
                _ => null };
            if (cost is null) return null;
            residual += cost.Value; // No invented early-rejection probability.
        }
        // Compiled mask is deterministic modeled reach, never observed survivors.
        double reach = System.Numerics.BitOperations.PopCount(compiled.AllowedRemovalMask) / 4d;
        return new("E.EventResult", "E.CompiledColor.20260913.v1.P1", 25 + reach * residual,
            65536, null, "CpuNumericalClosure.20260913;CompiledFourColorRemoval;ResidualOperatorSum0..4;" +
            (compiled.Remaining.Length > 1 ? "ConservativeCoarse;" : "") +
            "Workers=1;MinInput=1024;CpuCanonicalAbi1;metric=ns/ActualStageInput", OutputElementBytes:8,
            OutputAlreadyOrdered:true,PublicTransportClass:"CpuOrderedAbi1");
    }

    internal static PrivateOrdinalAllocationPricing.Work? LegacyEwWork(EventResultFamilyPlan plan) =>
        plan.HasMorphic || plan.HasNewWhitelist ? null : new(.85, 1, 1 << 20);
    internal static FamilyPhysicalQuote? Quote(EventResultFamilyPlan plan, FamilyPhysicalQuoteRequest geometry)
        => QuoteMeasured(plan, geometry) ?? QuoteModel(plan, geometry) ?? QuoteNew(plan, geometry);

    private static FamilyPhysicalQuote? QuoteNew(EventResultFamilyPlan plan, FamilyPhysicalQuoteRequest g)
    {
        if (!plan.GpuSupported || !FamilyPhysicalQuote.AdmittedRequest(g) ||
            !(plan.HasMorphic || plan.HasNewWhitelist) || plan.Conditions.Length is < 1 or > 16) return null;
        var device = Runtime.SearchPerformanceProfileFoundation.CaptureKnownDeviceIdentity();
        if (device.RenderingBackend is not ("d3d12" or "vulkan") ||
            !device.GpuIdentity.Contains("RTX 4060 Laptop GPU", StringComparison.OrdinalIgnoreCase)) return null;
        int transforms = plan.Conditions.Count(c => c.Kind is
            EventResultConditionKind.MorphicGroveGroupInitialBasicsContains or
            EventResultConditionKind.SymbioteInitialBasicTransform or
            EventResultConditionKind.AromaOfChaosInitialBasicTransform or
            EventResultConditionKind.WhisperingHollowInitialBasicTransform or
            EventResultConditionKind.TrialNondescriptInitialBasicsContains);
        int comparisons = plan.Conditions.Length;
        // The same event-local RNG/pool body serves the five closed transform
        // forms. Trial/Tinker scalar additions pay bounded extra comparisons;
        // public transport remains in the caller's existing quote composition.
        double ns = 1 + .25 * transforms + .08 * comparisons;
        double floor = device.RenderingBackend == "vulkan" ? 6.5 : 4.2;
        double setup = device.RenderingBackend == "vulkan" ? 30 : 100;
        return new("E.EventResult", "E.WhitelistClosed.20260923.v1.T" + transforms + ".P" + comparisons,
            ns, plan.Capacity, setup,
            "LocalMeasuredSingleStage;RTX4060Laptop;Backend=" + device.RenderingBackend +
            ";PartySlot1SameShaderSource;FiveTransformForms262144Roots;" +
            "TrialTinkerScalarComparisonEstimated;Model=BoundedCoarse;" +
            "FixedSubmitSyncFloorSeparateFromNumericalAndPublicPayload",
            OutputElementBytes: 4, PublicTransportClass: "Counted32")
        { FixedWindowMilliseconds = floor, LocalCostSource = "LocalMeasurement" };
    }

    private static FamilyPhysicalQuote? QuoteMeasured(EventResultFamilyPlan plan, FamilyPhysicalQuoteRequest geometry)
    {
        if (!plan.GpuSupported || !FamilyPhysicalQuote.AdmittedRequest(geometry) ||
            !FamilyPhysicalQuote.HasReferenceBackend()) return null;
        if (plan.Conditions.Length == 0 || plan.Conditions.GroupBy(c => c.Kind).Any(g => g.Count() != 1)) return null;
        var kinds = plan.Conditions.Select(c => c.Kind).Order().ToArray();
        if (kinds.Contains(EventResultConditionKind.ColorfulPhilosophersOfferedColor) &&
            plan.Authority.UnlockedCharacterCardPoolKeys.Count != 5) return null;
        double? ns = kinds switch {
            [EventResultConditionKind.TrashHeapGrabCard] => geometry.CompactInput ? .7 : .65,
            [EventResultConditionKind.TrashHeapDiveRelic] => .67,
            [EventResultConditionKind.FakeMerchantOfferedFakeRelic] => geometry.CompactInput ? 1.15 : 1.10,
            [EventResultConditionKind.ColorfulPhilosophersOfferedColor] => .64,
            [EventResultConditionKind.TrashHeapGrabCard, EventResultConditionKind.TrashHeapDiveRelic] => .63,
            [EventResultConditionKind.TrashHeapGrabCard, EventResultConditionKind.FakeMerchantOfferedFakeRelic] => 1.03,
            _ => null
        };
        if (ns is null) return null;
        return new("E.EventResult", "E.EventLocal." + string.Join('+', kinds), ns.Value,
            plan.Capacity, 40, "FamilyMatrix.20260910.EventOperators;NumericalDeviceEmission;NoPublicMaterialization");
    }

    private static bool ModelSupported(EventResultFamilyPlan plan) => plan.GpuSupported &&
        !plan.HasMorphic && !plan.HasNewWhitelist && // New operators cannot inherit old measured quotes.
        plan.PlayerSlot == 0 && plan.Conditions.Length is >= 1 and <= 8 &&
        plan.Authority.UnlockedCharacterCardPoolKeys.Count == 5;

    private static FamilyPhysicalQuote? QuoteModel(EventResultFamilyPlan plan, FamilyPhysicalQuoteRequest g)
    {
        if (!ModelSupported(plan) || !FamilyPhysicalQuote.AdmittedRequest(g) ||
            !FamilyPhysicalQuote.HasReferenceBackend()) return null;
        // Each operator generates its facts once before target comparisons. GPU
        // Grab/Dive share the Trash draw; additional targets do not replay Fake.
        bool fake = plan.Conditions.Any(c => c.Kind == EventResultConditionKind.FakeMerchantOfferedFakeRelic);
        double extra = Math.Max(0, plan.Conditions.Length - 2) * .08;
        double ns = (fake ? .95 : .55) + extra;
        bool sparse = g.MeanInputPopulation < CompactSparseGpuPricing.DenseAnchorPopulation;
        double? fixedMs = sparse ? CompactSparseGpuPricing.AdditionalFixedMilliseconds(
            SearchOptimizationScope.Current.CostOrderedFilters, g.CompactInput, g.MeanInputPopulation, ns) : 0;
        if (fixedMs is null) return null;
        return new("E.EventResult", "E.OperatorGroups.20260913.v1" +
            (sparse ? "." + CompactSparseGpuPricing.Revision : ""), ns,
            plan.Capacity, 40, "FamilyCostClosure.20260913;Model=BoundedCoarse;OperatorFactsOnce;" +
            "Predicates1..8;ExtraComparisonEnvelope;" + (sparse ? "DenseAnchorMinInput=1048576;" : "MinInput=1048576;") +
            "NumericalDeviceEmission;HostPayloadExcluded;metric=ns/ActualStageInput;" +
            (extra > 0 ? "ConservativeCoarse" : "ValidatedOperatorRegion") +
            (sparse ? ";" + CompactSparseGpuPricing.Evidence : ""))
        { FixedWindowMilliseconds = fixedMs.Value };
    }

    internal static FamilyPhysicalQuote? QuoteCpu(ExactSearchExecutionRequest request, EventResultFamilyPlan plan, FamilyPhysicalQuoteRequest g)
    {
        if (QuoteCpuNew(request, plan, g) is { } newQuote) return newQuote;
        if (!request.Authority.CanUseCurrentModel || request.Authority.PlayersCount != 1 ||
            !ModelSupported(plan) || !FamilyPhysicalQuote.AdmittedRequest(g) || g.CompactInput ||
            g.PrivateInput || g.PrivateOutput || g.MeanInputPopulation < 4096) return null;
        int scalar = plan.Conditions.Select(c => c.Kind).Distinct().Count(k =>
            k is EventResultConditionKind.TrashHeapGrabCard or EventResultConditionKind.TrashHeapDiveRelic);
        int fake = plan.Conditions.Count(c => c.Kind == EventResultConditionKind.FakeMerchantOfferedFakeRelic);
        int color = plan.Conditions.Count(c => c.Kind == EventResultConditionKind.ColorfulPhilosophersOfferedColor);
        // CPU currently initializes both Trash projections separately. These are
        // canonical CPU costs, including ordered output, not GPU coefficients.
        double ns = 80 + 20 * scalar + (fake > 0 ? 120 : 0) + (color > 0 ? 360 : 0) +
            20 * (Math.Max(0, fake - 1) + Math.Max(0, color - 1));
        return new("E.EventResult", "E.OperatorGroups.20260913.v1.P1", ns, 65536, null,
            "FamilyCostClosure.20260913;Model=BoundedCoarse;Workers=1;MinInput=4096;" +
            "CpuCanonicalAbi1;metric=ns/ActualStageInput;" + (plan.Conditions.Length > 2 ? "ConservativeCoarse" : "ValidatedOperatorRegion"), OutputElementBytes: 8,
            OutputAlreadyOrdered: true, PublicTransportClass: "CpuOrderedAbi1");
    }

    private static FamilyPhysicalQuote? QuoteCpuNew(ExactSearchExecutionRequest request,
        EventResultFamilyPlan plan, FamilyPhysicalQuoteRequest g)
    {
        if (!(plan.HasMorphic || plan.HasNewWhitelist) || !plan.GpuSupported ||
            request.Authority.PlayersCount != 1 || !request.Authority.CanUseCurrentModel ||
            !FamilyPhysicalQuote.AdmittedRequest(g) || g.PrivateInput || g.PrivateOutput ||
            plan.Conditions.Length is < 1 or > 16) return null;
        double operators = plan.Conditions.Sum(c => c.Kind switch
        {
            EventResultConditionKind.MorphicGroveGroupInitialBasicsContains => 35,
            EventResultConditionKind.SymbioteInitialBasicTransform or
                EventResultConditionKind.AromaOfChaosInitialBasicTransform or
                EventResultConditionKind.WhisperingHollowInitialBasicTransform or
                EventResultConditionKind.TrialNondescriptInitialBasicsContains => 20,
            EventResultConditionKind.FakeMerchantOfferedFakeRelic => 120,
            EventResultConditionKind.ColorfulPhilosophersOfferedColor => 360,
            _ => 20
        });
        double ns = 30 + operators + (g.CompactInput ? 17 : 0);
        return new("E.EventResult", "E.WhitelistClosed.20260923.v1.P1", ns,
            65536, 0,
            "LocalMeasuredCpuSingleTransform;Beta111PartySlot1SameNumericalBody;" +
            "8192RootCanonicalAbi1;Morphic60nsOthers50ns;" +
            "MultiOperatorSumIsConservativeEnvelope;CompactOrdinalCost17ns;" +
            "Workers1;NoObservedSurvivalFit",
            OutputElementBytes: 8, OutputAlreadyOrdered: true,
            PublicTransportClass: "CpuOrderedAbi1")
        { FixedWindowMilliseconds = .002 };
    }
}
