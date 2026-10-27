using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Core.Merchant;

namespace RolltheSpire2.Search.FamilyExecution;

internal static class MerchantShopColorlessPhysicalPricing
{
    internal static FamilyPhysicalQuote? Quote(ExactSearchExecutionRequest request,
        MerchantShopColorlessCompactionDecision publicDecision, FamilyPhysicalQuoteRequest geometry,
        FamilyExpectedFilteringCostProjection expected, MerchantOrderedSequenceSlotsPlan? orderedSequenceSlots = null)
    {
        var lowering = orderedSequenceSlots ?? MerchantShopColorlessOrderedSequencePolicy.Compile(request);
        if (!lowering.Applied)
            return QuoteMeasured(request, publicDecision, geometry) ?? QuoteModel(request, publicDecision, geometry, expected);
        var physical = request with { Evaluation = lowering.Evaluation };
        var physicalExpected = orderedSequenceSlots is not null ? expected :
            MerchantShopColorlessExpectedFilteringCost.ProjectPhysical(request, lowering.Evaluation,
                MerchantShopColorlessAnalyticalCost.Project(lowering.Evaluation), MerchantShopColorlessSurvival.Project(request));
        var quote = QuoteMeasured(physical, publicDecision, geometry) ??
            QuoteModel(physical, publicDecision, geometry, physicalExpected);
        return quote is null ? null : quote with
        {
            Shape = quote.Shape + lowering.RevisionSuffix,
            Evidence = quote.Evidence + ";GpuOrderedSequenceLoweredToEquivalentExistingSlotBody;" +
                "PhysicalIdentity=" + MerchantShopColorlessOrderedSequencePolicy.Revision + ";OriginalQueryAndScalarSurvivalUnchanged"
        };
    }

    private static FamilyPhysicalQuote? QuoteMeasured(ExactSearchExecutionRequest request,
        MerchantShopColorlessCompactionDecision publicDecision, FamilyPhysicalQuoteRequest geometry)
    {
        if (!FamilyPhysicalQuote.AdmittedRequest(geometry) || !FamilyPhysicalQuote.HasReferenceBackend() ||
            request.ProfileId != Compatibility.RuntimeProfileId.Beta111 || request.Authority.PlayersCount != 1 ||
            request.Authority.CanUseCurrentModel != true) return null;
        var e = request.Evaluation;
        var slots = e.MerchantColorlessConditions.OrderBy(c => c.MerchantOrdinal).ToArray();
        bool singleRare = slots is [{ MerchantOrdinal: 1, Slot: MerchantColorlessSlot.Rare }];
        if (e.MerchantColorlessSequenceConditions.Count != 0 || slots.Length is < 1 or > 3 ||
            (!singleRare && slots.Where((s, i) => s.MerchantOrdinal != i + 1 || s.Slot != MerchantColorlessSlot.Uncommon).Any())) return null;
        bool stable = !geometry.CompactInput && !geometry.PrivateOutput &&
            publicDecision.Path == MerchantShopColorlessCompactionPath.StableOrderedCompaction;
        // Same single-slot traversal for all immutable targets. Measured 1/2/3
        // horizons; private always uses unordered atomic ordinals, never Stable.
        double ns = geometry.CompactInput ? slots.Length == 1 ? .60 : .68 :
            slots.Length == 1 ? .25 : slots.Length == 2 ? .31 : .33;
        if (singleRare) ns = geometry.CompactInput ? .53 : stable ? .24 : .22;
        // Public execution also windows Dense input to bound worst-case survivors.
        // Keep measured rates unchanged; quote the geometry actually executed.
        int capacity = !geometry.PrivateInput && !geometry.PrivateOutput
            ? MerchantShopColorlessGpuExecutor.SurvivorCapacity : MerchantShopColorlessGpuExecutor.Capacity;
        return new("S.MerchantShopColorless", $"S.OrderedSingle{(singleRare ? "Rare" : "Uncommon")}.H{slots.Length}." + (stable ? "StablePublic" : "Atomic"),
            ns, capacity, 100, "FamilyMatrix.20260910.S1-S3;NumericalDeviceEmission;PublicSortExcluded",
            OutputElementBytes: geometry.PrivateOutput ? 4 : 8, OutputAlreadyOrdered: stable);
    }

    private static bool ModelAuthority(ExactSearchExecutionRequest request)
    {
        var a=Beta111MerchantColorlessAuthority.From(request.Authority);
        return request.ProfileId==Compatibility.RuntimeProfileId.Beta111 && request.Authority.CanUseCurrentModel &&
            request.Authority.PlayersCount==1 && a.HasExactV1Inputs && a.UncommonPool.Count==32 && a.RarePool.Count==21;
    }

    private static FamilyPhysicalQuote? QuoteModel(ExactSearchExecutionRequest request,
        MerchantShopColorlessCompactionDecision decision,FamilyPhysicalQuoteRequest g,FamilyExpectedFilteringCostProjection expected)
    {
        var e=request.Evaluation;
        if(!FamilyPhysicalQuote.AdmittedRequest(g) || !FamilyPhysicalQuote.HasReferenceBackend() || !ModelAuthority(request) ||
            !expected.IsResolved || expected.FinalSurvival is not (>=0 and <=.15) ||
            e.MerchantColorlessConditions.Count>5 || e.MerchantColorlessSequenceConditions.Count>2) return null;
        var replay=expected.Segments.Where(s=>s.Operation==FamilyAnalyticalOperation.RngAdvance &&
            s.Name.StartsWith("S.MerchantReplay[",StringComparison.Ordinal)).ToArray();
        if(replay.Length is <1 or >5) return null;
        // Reference GPU lane-reach envelope, not a change to scalar Survival.
        // Prefix checks leave a SIMD group active when any lane continues. This
        // bounded32-lane reference passed withheld consecutive and distant slots.
        double visits=replay.Sum(s=>1-Math.Pow(1-s.ReachProbability,32));
        bool stable=!g.CompactInput && !g.PrivateOutput && decision.Path==MerchantShopColorlessCompactionPath.StableOrderedCompaction;
        if(stable && g.MeanInputPopulation<4194304) return null;
        int extraRows = Math.Max(0, e.MerchantColorlessSequenceConditions.Count + (e.MerchantColorlessConditions.Count > 0 ? 1 : 0) - 1);
        double ns=.1+.12*visits+(g.CompactInput?.3:0)+(stable?.06:0)+.04*extraRows;
        bool sparse = g.MeanInputPopulation < CompactSparseGpuPricing.DenseAnchorPopulation;
        double? fixedMs = sparse ? CompactSparseGpuPricing.AdditionalFixedMilliseconds(
            SearchOptimizationScope.Current.CostOrderedFilters, g.CompactInput, g.MeanInputPopulation, ns) : 0;
        if (fixedMs is null) return null;
        int capacity=!g.PrivateInput&&!g.PrivateOutput?MerchantShopColorlessGpuExecutor.SurvivorCapacity:MerchantShopColorlessGpuExecutor.Capacity;
        return new("S.MerchantShopColorless","S.MerchantWork.20260913.v1."+(stable?"Stable":"Atomic") +
            (sparse ? "." + CompactSparseGpuPricing.Revision : ""),ns,capacity,100,
            "FamilyCostClosure.20260913;Model=BoundedCoarse;ReferenceLaneReach32;ScalarSurvivalUnchanged;"+
            "SlotsAndSequences;SharedMerchantTraversal;Horizon1..5;Survival<=.15;NumericalDeviceEmission;HostPayloadExcluded;metric=ns/ActualStageInput;" +
            (extraRows > 0 ? "ConservativeCoarse" : "ValidatedMerchantRegion") +
            (sparse ? ";" + CompactSparseGpuPricing.Evidence : ""),
            OutputElementBytes:g.PrivateOutput?4:8,OutputAlreadyOrdered:stable)
        { FixedWindowMilliseconds = fixedMs.Value };
    }

    internal static FamilyPhysicalQuote? QuoteCpuSlots(ExactSearchExecutionRequest request,FamilyPhysicalQuoteRequest g)
    {
        var e=request.Evaluation;
        if(!FamilyPhysicalQuote.AdmittedRequest(g)||!ModelAuthority(request)||g.PrivateInput||g.PrivateOutput||g.CompactInput||
            g.MeanInputPopulation<4096||e.MerchantColorlessSequenceConditions.Count!=0||e.MerchantColorlessConditions.Count is <1 or >5) return null;
        double calls=0,reach=1;int previous=0;
        foreach(var group in e.MerchantColorlessConditions.GroupBy(c=>(c.MerchantOrdinal-1)*Beta111NormalMerchantShopsContinuation.CallsPerMerchant+
            (c.Slot==MerchantColorlessSlot.Uncommon?Beta111NormalMerchantShopsContinuation.UncommonCallWithinMerchant:Beta111NormalMerchantShopsContinuation.RareCallWithinMerchant)).OrderBy(g=>g.Key))
        {
            if(group.Any(c=>c.MerchantOrdinal is <1 or >5)||group.Select(c=>c.TargetCardKey).Distinct().Count()!=1)return null;
            calls+=(group.Key-previous)*reach;previous=group.Key;
            reach/=group.First().Slot==MerchantColorlessSlot.Uncommon?32:21;
        }
        return new("S.MerchantShopColorless","S.SlotCallWork.20260913.v1.P1",40+.86*calls,65536,null,
            "FamilyCostClosure.20260913;Model=BoundedCoarse;Work=ExpectedActualSlotCalls;Workers=1;"+
            "CpuCanonicalAbi1;MinInput=4096;SequenceReferenceExcluded;metric=ns/ActualStageInput",
            OutputElementBytes:8,OutputAlreadyOrdered:true,PublicTransportClass:"CpuOrderedAbi1");
    }

    internal static FamilyPhysicalQuote? QuoteCpuSequence(ExactSearchExecutionRequest request, FamilyPhysicalQuoteRequest g,
        FamilyExpectedFilteringCostProjection? expected = null)
        => QuoteCpuSingleSequence(request, g) ?? QuoteCpuSequenceEnvelope(request, g, expected);

    private static FamilyPhysicalQuote? QuoteCpuSequenceEnvelope(ExactSearchExecutionRequest request, FamilyPhysicalQuoteRequest g,
        FamilyExpectedFilteringCostProjection? expected)
    {
        var e = request.Evaluation;
        if (!FamilyPhysicalQuote.AdmittedRequest(g) || !ModelAuthority(request) || g.PrivateInput || g.PrivateOutput ||
            g.CompactInput || g.MeanInputPopulation < 4096 || e.MerchantColorlessConditions.Count > 5 ||
            e.MerchantColorlessSequenceConditions.Count is < 1 or > 2 || MerchantShopColorlessCpuPlan.TryCompile(request) is null) return null;
        var fixedCalls = new Dictionary<int, int>();
        var observed = new SortedSet<int>();
        double probes = 0;
        int Call(int merchant, MerchantColorlessSlot slot) => (merchant - 1) * Beta111NormalMerchantShopsContinuation.CallsPerMerchant +
            (slot == MerchantColorlessSlot.Uncommon ? Beta111NormalMerchantShopsContinuation.UncommonCallWithinMerchant : Beta111NormalMerchantShopsContinuation.RareCallWithinMerchant);
        void Fixed(int merchant, MerchantColorlessSlot slot)
        { int call = Call(merchant,slot); observed.Add(call); fixedCalls[call] = slot == MerchantColorlessSlot.Uncommon ? 32 : 21; }
        foreach (var c in e.MerchantColorlessConditions) Fixed(c.MerchantOrdinal,c.Slot);
        foreach (var c in e.MerchantColorlessSequenceConditions)
        {
            for (int i=0;i<c.Count;i++)
                if(c.OrderMode == CombatRewardSequenceOrderMode.Ordered)
                { if(c.Slots[i] is not null) Fixed(i+1,c.Slot); }
                else observed.Add(Call(i+1,c.Slot));
            if(c.OrderMode != CombatRewardSequenceOrderMode.Ordered) probes += c.Count*c.Slots.Count(s=>s is not null);
        }
        double reach=1,calls=0,observations=0;int previous=0;
        foreach(int call in observed)
        {
            calls+=(call-previous)*reach;observations+=reach;previous=call;
            if(fixedCalls.TryGetValue(call,out int pool))reach/=pool;
        }
        if (expected is { IsResolved: true })
        {
            // Existing S-owned prefix probabilities include completed unordered
            // checkpoints. Do not charge later merchants to every root.
            calls = Math.Min(calls, expected.Segments.Where(s => s.Operation is FamilyAnalyticalOperation.RngAdvance or FamilyAnalyticalOperation.HistoricalNextInt).Sum(s => s.ExpectedContribution));
            observations = Math.Min(observations, expected.Segments.Where(s => s.Operation == FamilyAnalyticalOperation.ObservationWrite).Sum(s => s.ExpectedContribution));
            probes = Math.Min(probes, expected.Segments.Where(s => s.Operation == FamilyAnalyticalOperation.PredicateProbe).Sum(s => s.ExpectedContribution));
        }
        return new("S.MerchantShopColorless","S.MixedTraversalEnvelope.20260913.v1.P1",42+.85*calls+8*observations+2*probes,
            65536,null,"PricingHoleSweep.20260913;Model=ConservativeCoarse;SharedShopsCalls;FixedSlotReach;UnorderedProbeUpperBound;CpuCanonicalAbi1;MinInput=4096",
            OutputElementBytes:8,OutputAlreadyOrdered:true,PublicTransportClass:"CpuOrderedAbi1");
    }

    private static FamilyPhysicalQuote? QuoteCpuSingleSequence(ExactSearchExecutionRequest request, FamilyPhysicalQuoteRequest g)
    {
        var e = request.Evaluation;
        if (!FamilyPhysicalQuote.AdmittedRequest(g) || !ModelAuthority(request) || g.PrivateInput ||
            g.PrivateOutput || g.CompactInput || g.MeanInputPopulation < 1024 ||
            e.MerchantColorlessConditions.Count != 0 || e.MerchantColorlessSequenceConditions.Count != 1) return null;
        var c = e.MerchantColorlessSequenceConditions[0];
        if (c.Count is < 1 or > 5 || c.Slots.Count != c.Count) return null;
        double reach = 1, calls = 0, observations = 0;
        int previous = 0, pool = c.Slot == MerchantColorlessSlot.Uncommon ? 32 : 21;
        for (int i = 0; i < c.Count; i++)
        {
            if (c.OrderMode == CombatRewardSequenceOrderMode.Ordered && c.Slots[i] is null) continue;
            int call = i * Beta111NormalMerchantShopsContinuation.CallsPerMerchant +
                (c.Slot == MerchantColorlessSlot.Uncommon ? Beta111NormalMerchantShopsContinuation.UncommonCallWithinMerchant :
                    Beta111NormalMerchantShopsContinuation.RareCallWithinMerchant);
            calls += (call - previous) * reach;
            observations += reach; previous = call;
            if (c.OrderMode == CombatRewardSequenceOrderMode.Ordered) reach /= pool;
        }
        return new("S.MerchantShopColorless", "S.SequenceCalls.20260913.v1.P1", 42 + .8 * calls + 8 * observations,
            65536, null, "CpuNumericalClosure.20260913;Model=BoundedCoarse;SingleSequence;Horizon1..5;Workers=1;" +
            "MinInput=1024;CpuCanonicalAbi1;metric=ns/ActualStageInput", OutputElementBytes: 8,
            OutputAlreadyOrdered: true, PublicTransportClass: "CpuOrderedAbi1");
    }
}
