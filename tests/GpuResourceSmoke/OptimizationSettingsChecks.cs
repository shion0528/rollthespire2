using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static IEnumerable<(string Name, SearchOptimizationSettings Settings)> OptimizationVariants()
    {
        yield return ("allOn", SearchOptimizationSettings.AllEnabled);
        yield return ("allOff", SearchOptimizationSettings.AllDisabled);
        foreach (var property in typeof(SearchOptimizationSettings).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            var off = SearchOptimizationSettings.AllEnabled with { };
            property.SetValue(off, false);
            yield return ("onlyOff." + property.Name, off);
            var on = SearchOptimizationSettings.AllDisabled with { };
            property.SetValue(on, true);
            yield return ("onlyOn." + property.Name, on);
        }
    }

    private static SearchOptimizationSettings OppositeOptimizations(SearchOptimizationSettings s) => new()
    {
        PrefixCache = !s.PrefixCache, ExactDrawIntervals = !s.ExactDrawIntervals,
        LeafyLazyInitialization = !s.LeafyLazyInitialization, CostOrderedFilters = !s.CostOrderedFilters,
        GroupedOutput = !s.GroupedOutput, AsyncHeaderReadback = !s.AsyncHeaderReadback,
        DoubleWindowBatching = !s.DoubleWindowBatching
    };

    private static void TestOptimizationSettings(RenderingDevice rd)
    {
        string[] switches = ["RT2_NEOW_LEGACY_PREFIX_HASH", "RT2_SHARED_PREFIX_HASH", "RT2_NEOW_STATIC_DRAW_GATE",
            "RT2_NEOW_LEGACY_LEAFY_GATE", "RT2_N_INTERNAL_ORDER", "RT2_N_GROUP_OUTPUT", "RT2_NEOW_SYNC_HEADER",
            "RT2_NEOW_SINGLE_WINDOW"];
        var previous = switches.ToDictionary(k => k, System.Environment.GetEnvironmentVariable);
        try
        {
            foreach (string key in switches) System.Environment.SetEnvironmentVariable(key, null);
            Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
            var ordinary = InternalOrderPlan(authored: true);
            var leafy = PrefixScopePlan(true);
            var ordinaryBatch = new SearchBatch(0, 262144);
            var leafyBatch = new SearchBatch(0, 32768);
            ulong[] ordinaryExpected = InternalOrderExpected(ordinary, ordinaryBatch);
            ulong[] leafyExpected = PrefixScopeExpected(leafy, leafyBatch);
            Require(ordinaryExpected.Length > 0 && leafyExpected.Length > 0, "Optimization settings fixtures contain no successful CPU payload.");
            int variants = 0;
            foreach (var variant in OptimizationVariants())
            {
                using var scope = SearchOptimizationScope.Enter(variant.Settings);
                var s = variant.Settings;
                Require(NeowFamilyGpuExecutor.RequestPrefixHashCache == s.PrefixCache &&
                    SharedVisibleSeedGpuCache.Requested == s.PrefixCache &&
                    NeowFamilyGpuExecutor.RequestStaticDrawGates == s.ExactDrawIntervals &&
                    NeowFamilyGpuExecutor.RequestLegacyLeafyGate == !s.LeafyLazyInitialization &&
                    NeowFamilyGpuExecutor.RequestInternalFilterOrder == s.CostOrderedFilters &&
                    NeowFamilyGpuExecutor.RequestGroupOutput == s.GroupedOutput &&
                    FamilyGpuHeaderReadback.Requested == s.AsyncHeaderReadback &&
                    NeowFamilyGpuExecutor.RequestDoubleWindowBatching == s.DoubleWindowBatching,
                    "A per-search optimization setting did not reach its provider: " + variant.Name);
                TestOptimizationPublic(rd, variant.Name, s, ordinary, ordinaryBatch, ordinaryExpected, internalOracle: true);
                TestOptimizationPublic(rd, variant.Name, s, leafy, leafyBatch, leafyExpected, internalOracle: false);
                TestOptimizationPrivate(rd, variant.Name, s, ordinary, ordinaryBatch, ordinaryExpected);
                variants++;
                Emit(new { phase = "optimizationProviders", result = "PASS", variant = variant.Name, s.Key,
                    PrefixCache = NeowFamilyGpuExecutor.RequestPrefixHashCache,
                    SharedPrefixCache = SharedVisibleSeedGpuCache.Requested,
                    ExactDrawIntervals = NeowFamilyGpuExecutor.RequestStaticDrawGates,
                    LeafyLazyInitialization = !NeowFamilyGpuExecutor.RequestLegacyLeafyGate,
                    CostOrderedFilters = NeowFamilyGpuExecutor.RequestInternalFilterOrder,
                    GroupedOutput = NeowFamilyGpuExecutor.RequestGroupOutput,
                    AsyncHeaderReadback = FamilyGpuHeaderReadback.Requested,
                    DoubleWindowBatching = NeowFamilyGpuExecutor.RequestDoubleWindowBatching });
            }
            Require(variants == 16, "The all-on/all-off and seven independent setting controls were not all tested.");
            TestOptimizationCompositeDenseCache(rd, zzfz);
            TestOptimizationPartyCache(rd, zzfz);
            TestOptimizationBonesKBinding(rd, zzfz);
            Emit(new { phase = "optimizationSettings", result = "PASS", variants,
                independentNonzeroCpuOracle = true, denseCompactPrivate = true,
                constructorSnapshotSurvivesOppositeAmbientScope = true,
                allLeafyIntervalLazyCombinations = true, groupedCompositeDenseFullPayload = true,
                partyUsesProductionFusedPacking = true, partyStagedProductionPathExists = false,
                bonesKScope = "ActualConstructorAndEmptyOnly;NoSyntheticProjectionEvaluation" });
        }
        finally
        {
            foreach (var pair in previous) System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static void TestOptimizationPublic(RenderingDevice rd, string variant, SearchOptimizationSettings settings,
        NeowFamilyGpuPlan plan, SearchBatch batch, ulong[] expected, bool internalOracle)
    {
        using var executor = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false);
        FamilyCandidateSet actual; NeowFamilyGpuMetrics metrics;
        using (SearchOptimizationScope.Enter(OppositeOptimizations(settings)))
        {
            Require(executor.SupportsDenseWindowBatching == (settings.DoubleWindowBatching && plan.UsesLeafyPreGate),
                "Double-window admission did not retain the constructor policy: " + variant);
            actual = executor.Execute(FamilyCandidateSet.Dense(batch), CancellationToken.None, out metrics);
        }
        Require(actual.ExportAbi1().Span.SequenceEqual(expected), "Optimization settings changed Dense CPU population: " + variant);
        Require(executor.UsesPrefixHashCache == settings.PrefixCache && !executor.UsesSharedDensePrefixHashCache &&
            executor.UsesStaticCurseDrawGate == settings.ExactDrawIntervals &&
            executor.LeafyGateRevision == (settings.LeafyLazyInitialization ? NeowFamilyGpuExecutor.OptimizedLeafyGateRevision : NeowFamilyGpuExecutor.LegacyLeafyGateRevision) &&
            executor.InternalFilterOrderEnabled == (internalOracle && settings.CostOrderedFilters) &&
            executor.GroupOutputEnabledFor(false) == settings.GroupedOutput,
            "A captured optimization changed when ambient settings changed before Dense compilation: " + variant);
        Require(executor.HeaderReadbackRevision == (settings.AsyncHeaderReadback
                ? FamilyGpuHeaderReadback.QueuedRevision + "." + NeowSparsePayloadReadback.QueuedRevision
                : FamilyGpuHeaderReadback.SynchronousRevision + "." + NeowSparsePayloadReadback.SynchronousRevision),
            "Header transport ignored the captured per-search readback setting: " + variant);
        if (internalOracle) CheckInternalOrderPairCheckpoints(rd, executor, plan, batch, metrics.PairSurvivors);
        else Require(executor.UsesStaticLeafyDrawGate == (settings.ExactDrawIntervals && settings.LeafyLazyInitialization),
            "The Leafy static interval fallback did not respect its lazy-draw source dependency.");
        ulong[] subset = new ulong[] { 0, 7, (ulong)batch.BatchCandidateCount - 1 }.Concat(expected.Take(7)).Distinct().Order().ToArray();
        var compact = FamilyCandidateSet.FromSortedAbi1(batch, subset);
        using (SearchOptimizationScope.Enter(OppositeOptimizations(settings)))
            actual = executor.Execute(compact, CancellationToken.None, out metrics);
        Require(actual.ExportAbi1().Span.SequenceEqual(expected.Intersect(subset).ToArray()) && metrics.Dispatches == 1 &&
            !executor.GroupOutputEnabledFor(true) && executor.UsesSharedPrefixHashCache == settings.PrefixCache &&
            executor.SharedPrefixHashRevisionFor(true) == (settings.PrefixCache ? SharedVisibleSeedGpuCache.Revision : null),
            "Optimization settings changed Compact output, fallback or shared cache capture: " + variant);
        Emit(new { phase = "optimizationPublic", result = "PASS", variant, leafy = !internalOracle,
            denseOutputs = expected.Length, compactOutputs = actual.Count,
            prefix = executor.UsesPrefixHashCache, sharedCompact = executor.UsesSharedPrefixHashCache,
            exactCurse = settings.ExactDrawIntervals,
            exactLeafy = !internalOracle && settings.ExactDrawIntervals && settings.LeafyLazyInitialization,
            lazyLeafy = settings.LeafyLazyInitialization, costOrder = executor.InternalFilterOrderEnabled,
            groupedOutput = executor.GroupOutputEnabledFor(false), readback = executor.HeaderReadbackRevision });
    }

    private static void TestOptimizationPrivate(RenderingDevice rd, string variant, SearchOptimizationSettings settings,
        NeowFamilyGpuPlan plan, SearchBatch batch, ulong[] full)
    {
        using var output = new PrivateOrdinalBuffer(rd);
        using var staged = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: output, useIndirectStaging: false);
        int count;
        using (SearchOptimizationScope.Enter(OppositeOptimizations(settings)))
            count = staged.ExecutePrivateStage(batch, batch.BatchCandidateCount, CancellationToken.None, out _);
        Require(output.ReadForVerification(batch, count).ExportAbi1().Span.SequenceEqual(full) && count > 0,
            "Optimization settings changed private Dense output: " + variant);
        using var input = new PrivateOrdinalBuffer(rd);
        using var fusedOutput = new PrivateOrdinalBuffer(rd);
        using var fused = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: input, privateOutput: fusedOutput,
            useIndirectStaging: false);
        uint[] sparse = full.Take(5).Concat(new ulong[] { 0, 7, (ulong)batch.BatchCandidateCount - 1 })
            .Distinct().Reverse().Select(i => (uint)i).ToArray();
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "OptimizationSettings.PrivateInput");
        using (SearchOptimizationScope.Enter(OppositeOptimizations(settings)))
            count = fused.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out _);
        Require(fusedOutput.ReadForVerification(batch, count).ExportAbi1().Span.SequenceEqual(full.Intersect(sparse.Select(i => (ulong)i)).ToArray()) &&
            count > 0 && !fused.UsesPrefixHashCache && !fused.GroupOutputEnabledFor(false) &&
            !fused.InternalFilterOrderEnabled && fused.UsesSharedPrefixHashCache == settings.PrefixCache,
            "Optimization settings changed private sparse output or fallback: " + variant);
        Emit(new { phase = "optimizationPrivate", result = "PASS", variant, denseOutputs = full.Length,
            sparseOutputs = count, densePrefix = staged.UsesPrefixHashCache,
            privateInputSharedPrefix = fused.UsesSharedPrefixHashCache, sparseUsesCanonicalFallback = true });
    }

    private static (NeowFamilyGpuPlan Plan, NeowCapsuleComposite Composite) OptimizationCompositeFixture()
    {
        var plan = InternalOrderPlan(3, 28);
        plan.Meta[82] = plan.Meta[83] = plan.Meta[84] = 0;
        // Valid numerical Small+Large grouped arrival: one target in the first
        // ordinary rarity lane, and three nondepleting four-entry lanes. The
        // independent oracle shuffles complete arrays rather than tracked ranks.
        uint[] metadata = new uint[109];
        metadata[0] = 2; metadata[1] = 28; metadata[2] = 3; metadata[3] = 1;
        metadata[4] = 0; metadata[5] = 1; metadata[6] = 1;
        metadata[7] = metadata[8] = metadata[9] = uint.MaxValue;
        metadata[10] = metadata[11] = metadata[12] = uint.MaxValue;
        metadata[13] = 3; metadata[15] = 1; metadata[16] = 4;
        ulong salt = XxHash64.HashUtf8("up_front", 0);
        metadata[22] = (uint)salt; metadata[23] = (uint)(salt >> 32);
        metadata[106] = metadata[107] = metadata[108] = 4;
        return (plan, new NeowCapsuleComposite(metadata, null!, null!, UsesBonesCheckpoint: true));
    }

    private static ulong[] OptimizationCompositeExpected(NeowFamilyGpuPlan plan, SearchBatch batch)
    {
        var accepted = new List<ulong>();
        for (int i = 0; i < batch.BatchCandidateCount; i++)
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + (ulong)i), 0);
            if (!InternalOrderPairOracle(plan, root, out var pair)) continue;
            var rewards = new Beta110FastRng(pair.Rewards);
            int[] rarities = Enumerable.Range(0, 3).Select(_ => { float value = rewards.NextFloat(); return value < .5f ? 1 : value < .83f ? 2 : 3; }).ToArray();
            var upfront = new Beta110FastRng(unchecked(root + XxHash64.HashUtf8("up_front", 0)));
            int[][] lanes = [new[] { 1000, 1001, 1002, 1003 }, new[] { 2000, 2001, 2002, 2003 }, new[] { 3000, 3001, 3002, 3003 }];
            foreach (int[] lane in lanes) upfront.UnstableShuffle(lane.AsSpan());
            int[] consumed = new int[3]; bool found = false;
            foreach (int rarity in rarities)
            {
                int lane = rarity - 1;
                if (lanes[lane][consumed[lane]++] == 1001) found = true;
            }
            if (found) accepted.Add((ulong)i);
        }
        return accepted.ToArray();
    }

    private static void TestOptimizationCompositeDenseCache(RenderingDevice rd, ulong zzfz)
    {
        var fixture = OptimizationCompositeFixture();
        SearchBatch[] batches = [new(0, 262144), new(zzfz, 1031), new(PrefixHashCache.SpaceSize - 513, 513)];
        using var disabledScope = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled);
        using var control = new NeowFamilyGpuExecutor(rd, fixture.Plan, 42, composite: fixture.Composite, useIndirectStaging: false);
        using var enabledScope = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled with { PrefixCache = true });
        using var cached = new NeowFamilyGpuExecutor(rd, fixture.Plan, 42, composite: fixture.Composite, useIndirectStaging: false);
        int nonzero = 0;
        foreach (var batch in batches)
        {
            ulong[] expected = OptimizationCompositeExpected(fixture.Plan, batch);
            var a = control.Execute(FamilyCandidateSet.Dense(batch), CancellationToken.None, out var am);
            var b = cached.Execute(FamilyCandidateSet.Dense(batch), CancellationToken.None, out var bm);
            Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected) &&
                am.CurseSurvivors == bm.CurseSurvivors && am.PairSurvivors == bm.PairSurvivors &&
                PrefixScopeHeader(rd, control).AsSpan().SequenceEqual(PrefixScopeHeader(rd, cached)),
                "Complex staged shared prefix changed complete composite population or headers.");
            CheckGroupOutputCursePayload(rd, cached, batch, GroupOutputCurseExpected(fixture.Plan, batch, true), bm.CurseSurvivors);
            CheckInternalOrderPairCheckpoints(rd, cached, fixture.Plan, batch, bm.PairSurvivors);
            nonzero += expected.Length > 0 ? 1 : 0;
        }
        Require(nonzero > 0 && !control.UsesSharedDensePrefixHashCache && !cached.UsesPrefixHashCache &&
            cached.UsesSharedDensePrefixHashCache && cached.SharedDensePrefixHashBinding == 14 &&
            cached.SharedPrefixHashRevisionFor(false) == SharedVisibleSeedGpuCache.Revision &&
            cached.ResidentBytes == control.ResidentBytes + SharedVisibleSeedGpuCache.Bytes,
            "Complex staged shared prefix did not actually preserve projection binding13 and use binding14.");
        Emit(new { phase = "optimizationCompositeDenseCache", result = "PASS", nonzeroBatches = nonzero,
            source = "SmallLargeGroupedNumericalFixture", completeCpuArrayShuffle = true,
            exactStage0RootPayload = true, projectionBinding = 13, prefixBinding = cached.SharedDensePrefixHashBinding,
            fullGameContentAdmissionClaimed = false });
    }

    private static void TestOptimizationPartyCache(RenderingDevice rd, ulong zzfz)
    {
        var plans = new[] { PrefixScopePlan(false), PrefixScopePlan(false) };
        for (int slot = 0; slot < 2; slot++)
        { plans[slot].Meta[5] = (uint)slot; plans[slot].Meta[6] = 2; plans[slot].Meta[56] = plans[slot].Meta[66] = plans[slot].Meta[82] = 0; }
        var batch = new SearchBatch(zzfz, 32768);
        ulong[] expected = Enumerable.Range(0, batch.BatchCandidateCount).Select(i => (ulong)i).Where(ordinal =>
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            for (int slot = 0; slot < 2; slot++)
            {
                var rng = new Beta110FastRng(unchecked(root + InternalOrderU64(plans[slot].Meta, 31) + (ulong)slot));
                if (rng.NextInt((int)plans[slot].Meta[39]) != 0) return false;
            }
            return true;
        }).ToArray();
        using var off = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled);
        using var control = new NeowFamilyGpuExecutor(rd, plans[0], 42, partyPlans: plans);
        using var on = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled with { PrefixCache = true });
        using var cached = new NeowFamilyGpuExecutor(rd, plans[0], 42, partyPlans: plans);
        var a = control.Execute(FamilyCandidateSet.Dense(batch), CancellationToken.None, out var am);
        var b = cached.Execute(FamilyCandidateSet.Dense(batch), CancellationToken.None, out var bm);
        Require(expected.Length > 0 && a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected) &&
            am.Dispatches == 1 && bm.Dispatches == 1 && !cached.UsesSharedDensePrefixHashCache &&
            cached.UsesSharedPrefixHashCache && cached.SharedPrefixHashBinding == 11,
            "Production party packing failed the independent per-player root oracle or used a nonexistent staged path.");
        Emit(new { phase = "optimizationPartyCache", result = "PASS", outputs = expected.Length,
            productionPartyPacking = true, actualPath = "Fused", partyStagedPath = "NotPresentInProductionPacking",
            prefixBinding = cached.SharedPrefixHashBinding });
    }

    private static void TestOptimizationBonesKBinding(RenderingDevice rd, ulong zzfz)
    {
        using var scope = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled with { PrefixCache = true });
        using var output = new PrivateOrdinalBuffer(rd);
        using var executor = new NeowFamilyGpuExecutor(rd, InternalOrderPlan(), 42, privateOutput: output,
            bonesKMode: 2, bonesKMetadata: new uint[256], useIndirectStaging: false);
        int count = executor.ExecutePrivateStage(new SearchBatch(zzfz, 0), 0, CancellationToken.None, out var metrics);
        Require(count == 0 && metrics.Dispatches == 0 && executor.UsesSharedDensePrefixHashCache &&
            executor.SharedDensePrefixHashBinding == 14 && executor.SharedPrefixHashRevisionFor(false) == SharedVisibleSeedGpuCache.Revision,
            "BonesK staged projection compilation collided with shared prefix binding14.");
        Emit(new { phase = "optimizationBonesKBinding", result = "PASS", actualStagedModulesCompiled = true,
            projectionBinding = 13, prefixBinding = executor.SharedDensePrefixHashBinding,
            syntheticProjectionExecuted = false, wholeGameQuerySemanticsVerified = false });
    }
}
