using Godot;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Merchant;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Root integrates this entry separately. No changes to the central Smoke
    // dispatcher, benchmark meter, query fingerprint or installed game state.
    private static void TestMerchantOrderedSequenceSlots(RenderingDevice rd)
    {
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong start, out _, out string issue), issue);
        using var settings = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled);
        var fixtures = MerchantEarlyFixtures(start).Concat(MerchantOrderedExtraFixtures(start)).ToArray();
        TestMerchantOrderedPlanning(fixtures.Select(f => f.Request).First(r =>
            r.Evaluation.MerchantColorlessSequenceConditions.Any(c => c.OrderMode == CombatRewardSequenceOrderMode.Ordered)));
        string? prefixBefore = System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH");
        int comparisons = 0, positiveFixtures = 0, loweredFixtures = 0;
        try
        {
            foreach (bool prefix in new[] { false, true })
            {
                System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", prefix ? "1" : "0");
                foreach (var fixture in fixtures)
                {
                    var compiled = MerchantShopColorlessOrderedSequencePolicy.Compile(fixture.Request, true);
                    var batch = new SearchBatch(start, 1031);
                    var dense = FamilyCandidateSet.Dense(batch);
                    var compact = MerchantEarlySparse(batch, fixture.WitnessLogicalOrdinal);
                    var expectedDense = MerchantEarlyExpected(fixture.Request, dense);
                    var expectedCompact = MerchantEarlyExpected(fixture.Request, compact);
                    Require(fixture.HasWitness ? expectedDense.Contains((ulong)fixture.WitnessLogicalOrdinal) : expectedDense.Length == 0,
                        "Merchant ordered fixture lost its independent positive/contradictory witness: " + fixture.Name);
                    if (expectedDense.Length > 0) positiveFixtures++;
                    if (compiled.Applied) loweredFixtures++;
                    foreach (var path in new[] { MerchantShopColorlessCompactionPath.AtomicAppendHostSort,
                        MerchantShopColorlessCompactionPath.StableOrderedCompaction })
                    {
                        using var control = MerchantShopColorlessGpuExecutor.Create(rd, fixture.Request, path, useOrderedSequenceSlots: false);
                        using var optimized = MerchantShopColorlessGpuExecutor.Create(rd, fixture.Request, path, useOrderedSequenceSlots: true);
                        Require(!control.OrderedSequenceSlotsEnabled && optimized.OrderedSequenceSlotsEnabled == compiled.Applied &&
                            optimized.PhysicalImplementationRevision.Contains(MerchantShopColorlessOrderedSequencePolicy.Revision) == compiled.Applied &&
                            !control.PhysicalImplementationRevision.Contains(MerchantShopColorlessOrderedSequencePolicy.Revision),
                            "Merchant ordered actual implementation identity did not match the captured control.");
                        foreach (var (input, expected) in new[] { (dense, expectedDense), (compact, expectedCompact) })
                        {
                            var a = control.Execute(input, CancellationToken.None, out var am);
                            var b = optimized.Execute(input, CancellationToken.None, out var bm);
                            MerchantEarlyEqual(a.EnumerateLogicalOrdinals().ToArray(), expected, fixture.Name + ".orderedOff");
                            MerchantEarlyEqual(b.EnumerateLogicalOrdinals().ToArray(), expected, fixture.Name + ".orderedOn");
                            Require(am.ProcessedCandidates == bm.ProcessedCandidates && am.Survivors == bm.Survivors &&
                                am.Dispatches == bm.Dispatches && am.ReadbackBytes == bm.ReadbackBytes,
                                "Merchant ordered lowering changed transport, processed work or dispatches.");
                            comparisons++;
                        }
                        MerchantEarlyPreCancel(() => optimized.Execute(dense, new(canceled: true), out _));
                        MerchantEarlyEqual(optimized.Execute(compact, CancellationToken.None, out _).EnumerateLogicalOrdinals().ToArray(),
                            expectedCompact, fixture.Name + ".orderedCancelReuse");
                    }
                    // Existing helper covers every private addressing direction;
                    // the explicit policy override avoids environment defaults.
                    TestMerchantOrderedPrivate(rd, fixture.Request, dense, compact, expectedDense, expectedCompact);
                    comparisons += 3;
                }
            }
            Require(positiveFixtures > 0 && loweredFixtures > 0, "Merchant ordered checks were all empty/inapplicable.");
            var boundaryRequest = fixtures.First(f => f.Name.Contains("ordered3.")).Request;
            foreach (var batch in new[] { new SearchBatch(0, 0), new SearchBatch(0, 1), new SearchBatch(1, 7),
                new SearchBatch(33, 8), new SearchBatch(start, 511), new SearchBatch(start, 512),
                new SearchBatch(start, 513), new SearchBatch(0x100000003UL, 4097),
                new SearchBatch(PrefixHashCache.SpaceSize - 513, 513), new SearchBatch(PrefixHashCache.SpaceSize - 1, 1) })
            {
                var input = FamilyCandidateSet.Dense(batch);
                var expected = MerchantEarlyExpected(boundaryRequest, input);
                using var owner = MerchantShopColorlessGpuExecutor.Create(rd, boundaryRequest,
                    MerchantShopColorlessCompactionPath.AtomicAppendHostSort, useOrderedSequenceSlots: true);
                MerchantEarlyEqual(owner.Execute(input, CancellationToken.None, out _).EnumerateLogicalOrdinals().ToArray(),
                    expected, "merchantOrdered.boundary");
                comparisons++;
            }
            Emit(new { phase = "merchantOrderedSequenceSlots", result = "PASS", fixtures = fixtures.Length,
                comparisons, positiveFixtures, loweredFixtures, independentCanonicalFullLists = true,
                ordered3And5 = true, positionalWildcardsAndRepeatedTargets = true,
                conflictingConjunction = true, unknownTargetIsUnchangedFastFallback = true,
                unorderedPreserved = true, privateInputsAndOutputs = true,
                prefixOnOffBoundaryAndFinalSeed = true, cancellationAndReuse = true,
                rngAndShaderUnchanged = true, cpuImplementationAndExactUnchanged = true });
        }
        finally { System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", prefixBefore); }
    }

    private static IEnumerable<MerchantEarlyFixture> MerchantOrderedExtraFixtures(ulong witnessOrdinal)
    {
        foreach ((int u, int r) in new[] { (3, 2), (32, 21) })
        {
            var basis = MerchantEarlyRequest(u, r);
            var authority = Beta111MerchantColorlessAuthority.From(basis.Authority);
            var root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(witnessOrdinal), 0);
            var merchants = Beta111NormalMerchantColorlessSequenceProjector.Project(root, authority).Merchants;
            foreach (var slot in new[] { MerchantColorlessSlot.Uncommon, MerchantColorlessSlot.Rare })
            {
                ModelKey Key(int i) => slot == MerchantColorlessSlot.Uncommon ? merchants[i].UncommonColorless : merchants[i].RareColorless;
                MerchantEarlyFixture Make(string name, ModelKey?[] targets) => new($"{u}x{r}.{name}.{slot}",
                    basis with { Evaluation = basis.Evaluation with { MerchantColorlessSequenceConditions =
                        [new(targets.Length, CombatRewardSequenceOrderMode.Ordered, slot, targets)] } });
                yield return Make("ordered3", [Key(0), Key(1), Key(2)]);
                yield return Make("ordered5TrailingWildcards", [Key(0), null, null, null, null]);
                yield return Make("ordered5LeadingWildcards", [null, null, null, null, Key(4)]);
                yield return Make("ordered5PositionalWildcards", [Key(0), null, Key(2), null, Key(4)]);
                var request = Make("ordered3", [Key(0), Key(1), Key(2)]).Request;
                var opposite = (slot == MerchantColorlessSlot.Uncommon ? authority.UncommonPool : authority.RarePool).First(k => k != Key(0));
                yield return new($"{u}x{r}.orderedConflict.{slot}", request with
                { Evaluation = request.Evaluation with { MerchantColorlessConditions = [new(1, slot, opposite)] } }, false);
            }
        }
    }

    private static void TestMerchantOrderedPlanning(ExactSearchExecutionRequest request)
    {
        TestMerchantOrderedDefaults(request);
        var authority = Beta111MerchantColorlessAuthority.From(request.Authority);
        var before = request.Evaluation;
        var off = MerchantShopColorlessOrderedSequencePolicy.Compile(request, false);
        var on = MerchantShopColorlessOrderedSequencePolicy.Compile(request, true);
        Require(!off.Applied && ReferenceEquals(off.Evaluation, before) && on.Applied && ReferenceEquals(request.Evaluation, before),
            "Merchant ordered lowering modified the authored request or disabled control.");
        foreach (var c in before.MerchantColorlessSequenceConditions.Where(c => c.OrderMode == CombatRewardSequenceOrderMode.Ordered))
        for (int i = 0; i < c.Count; i++)
            if (c.Slots[i] is { } target)
                Require(on.Evaluation.MerchantColorlessConditions.Contains(new(i + 1, c.Slot, target)),
                    "Merchant ordered lowering lost a positional target or repeated identity.");
        Require(on.Evaluation.MerchantColorlessSequenceConditions.All(c => c.OrderMode != CombatRewardSequenceOrderMode.Ordered),
            "An expanded ordered sequence still forced its original terminal horizon.");
        var disabled = request with { RunOptions = request.RunOptions with
        { Optimizations = request.RunOptions.Optimizations with { CostOrderedFilters = false } } };
        Require(!MerchantShopColorlessOrderedSequencePolicy.Compile(disabled, true).Applied,
            "The explicit diagnostic switch bypassed the player's CostOrderedFilters setting.");
        var tail = request with { Evaluation = ExactSearchEvaluationProjection.Empty with
        { MerchantColorlessSequenceConditions = [new(5, CombatRewardSequenceOrderMode.Ordered,
            MerchantColorlessSlot.Uncommon, [authority.UncommonPool[0], null, null, null, null])] } };
        var trimmed = MerchantShopColorlessOrderedSequencePolicy.Compile(tail, true);
        Require(trimmed.Applied && trimmed.OriginalMaximumOrdinal == 5 && trimmed.PackedMaximumOrdinal == 1,
            "Trailing ordered wildcards retained an unobserved merchant horizon.");
        var ordered = before.MerchantColorlessSequenceConditions.First(c => c.OrderMode == CombatRewardSequenceOrderMode.Ordered);
        var unknown = ordered with { Slots = ordered.Slots.Select((k, i) => i == 0 ? (ModelKey?)new ModelKey("CARD", "NOT_CAPTURED_MERCHANT_TARGET") : k).ToArray() };
        var absent = before with { MerchantColorlessSequenceConditions = [unknown] };
        var fallback = MerchantShopColorlessOrderedSequencePolicy.Compile(absent, authority, true);
        Require(!fallback.Applied && ReferenceEquals(fallback.Evaluation, absent) &&
            fallback.Reason == "OrderedTargetAbsentFromCapturedPool",
            "An unsupported target silently changed the old Fast necessary-condition boundary.");
        foreach (var invalid in new[] { ordered with { Count = 6 }, ordered with { Slots = [] }, ordered with { Slot = (MerchantColorlessSlot)77 } })
            Require(!MerchantShopColorlessOrderedSequencePolicy.Compile(before with
                { MerchantColorlessSequenceConditions = [invalid] }, authority, true).Applied,
                "An invalid sequence shape entered ordered GPU lowering.");
        Require(!MerchantShopColorlessOrderedSequencePolicy.Compile(before, authority with { OrderedPoolExact = false }, true).Applied,
            "An incomplete captured pool entered ordered GPU lowering.");
        var full = MerchantShopColorlessAnalyticalCost.Project(before);
        var survival = MerchantShopColorlessSurvival.Project(request);
        var legacyCost = MerchantShopColorlessExpectedFilteringCost.Project(request, full, survival);
        var physicalCost = MerchantShopColorlessExpectedFilteringCost.ProjectPhysical(request, on.Evaluation,
            MerchantShopColorlessAnalyticalCost.Project(on.Evaluation), survival);
        Require(legacyCost.FinalSurvival == physicalCost.FinalSurvival,
            "Ordered physical lowering changed scalar query survival.");
        Emit(new { phase = "merchantOrderedPlanning", result = "PASS", exactQueryObjectRetained = true,
            fixedSlotsPreserveOriginalConjunction = true, tailHorizonTrimmed = true,
            unknownAndMalformedFallback = true, playerOptimizationSwitchHonored = true,
            oldAndNewFinalSurvival = physicalCost.FinalSurvival });
    }

    private static void TestMerchantOrderedDefaults(ExactSearchExecutionRequest request)
    {
        string? previous = System.Environment.GetEnvironmentVariable(MerchantShopColorlessOrderedSequencePolicy.DiagnosticVariable);
        // The test host can reference either the release or diagnostic DLL;
        // its own preprocessor symbols do not identify the loaded Mod build.
        bool diagnosticBuild = typeof(MerchantShopColorlessOrderedSequencePolicy).Assembly.GetType(
            "RolltheSpire2.Diagnostics.GpuProfiling.GpuProfilingSession") is not null;
        var disabled = request with { RunOptions = request.RunOptions with
        { Optimizations = request.RunOptions.Optimizations with { CostOrderedFilters = false } } };
        try
        {
            foreach (string? control in new string?[] { null, "0", "1", "unrecognized" })
            {
                System.Environment.SetEnvironmentVariable(MerchantShopColorlessOrderedSequencePolicy.DiagnosticVariable, control);
                bool expected = !diagnosticBuild || control != "0";
                Require(MerchantShopColorlessOrderedSequencePolicy.Requested(request.RunOptions.Optimizations) == expected &&
                    MerchantShopColorlessOrderedSequencePolicy.Compile(request).Applied == expected,
                    "Merchant ordered release default or diagnostic override did not follow the loaded Mod build.");
                var off = MerchantShopColorlessOrderedSequencePolicy.Compile(disabled);
                Require(!MerchantShopColorlessOrderedSequencePolicy.Requested(disabled.RunOptions.Optimizations) &&
                    !off.Applied && ReferenceEquals(off.Evaluation, disabled.Evaluation) &&
                    !MerchantShopColorlessOrderedSequencePolicy.Compile(disabled, true).Applied,
                    "Merchant ordered defaults or diagnostic controls bypassed the player's disabled optimization setting.");
                var explicitOff = MerchantShopColorlessOrderedSequencePolicy.Compile(request, false);
                Require(!explicitOff.Applied && ReferenceEquals(explicitOff.Evaluation, request.Evaluation),
                    "Merchant ordered explicit off no longer restores the original numerical plan.");
            }
            Emit(new { phase = "merchantOrderedDefaults", result = "PASS", diagnosticBuild,
                unsetControlMatchesReleaseDefault = true, playerCostOrderedFiltersAlwaysAuthoritative = true,
                diagnosticOffAndOnCovered = true, releaseIgnoresDiagnosticEnvironment = !diagnosticBuild,
                originalPlanRetainedWhenDisabled = true });
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(MerchantShopColorlessOrderedSequencePolicy.DiagnosticVariable, previous);
        }
    }

    private static void TestMerchantOrderedPrivate(RenderingDevice rd, ExactSearchExecutionRequest request,
        FamilyCandidateSet dense, FamilyCandidateSet compact, ulong[] denseExpected, ulong[] compactExpected)
    {
        using var input = new PrivateOrdinalBuffer(rd);
        using var output = new PrivateOrdinalBuffer(rd);
        uint[] reverse = compact.EnumerateLogicalOrdinals().Reverse().Select(i => (uint)i).ToArray();
        FamilyGpuComputeUtility.Update(rd, input.Buffer, reverse, "MerchantOrdered.PrivateInput");
        using (var owner = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, output, useOrderedSequenceSlots: true))
        {
            int count = owner.ExecutePrivateStage(compact.Batch, compact.Count, CancellationToken.None, out _);
            MerchantEarlyEqual(output.ReadForVerification(compact.Batch, count).EnumerateLogicalOrdinals().ToArray(),
                compactExpected, "merchantOrdered.private");
            FamilyGpuComputeUtility.Update(rd, input.Buffer, [(uint)compact.Batch.BatchCandidateCount], "MerchantOrdered.BadOrdinal");
            Expect<InvalidDataException>(() => owner.ExecutePrivateStage(compact.Batch, 1, CancellationToken.None, out _));
            FamilyGpuComputeUtility.Update(rd, input.Buffer, reverse, "MerchantOrdered.PrivateRestore");
            count = owner.ExecutePrivateStage(compact.Batch, compact.Count, CancellationToken.None, out _);
            MerchantEarlyEqual(output.ReadForVerification(compact.Batch, count).EnumerateLogicalOrdinals().ToArray(),
                compactExpected, "merchantOrdered.privateReuse");
        }
        using (var owner = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, null, output, useOrderedSequenceSlots: true))
        {
            int count = owner.ExecutePrivateStage(dense.Batch, dense.Count, CancellationToken.None, out _);
            MerchantEarlyEqual(output.ReadForVerification(dense.Batch, count).EnumerateLogicalOrdinals().ToArray(),
                denseExpected, "merchantOrdered.densePrivate");
        }
        using var terminal = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, null, useOrderedSequenceSlots: true);
        MerchantEarlyEqual(terminal.ExecutePrivate(compact.Batch, compact.Count, CancellationToken.None, out _)
            .EnumerateLogicalOrdinals().ToArray(), compactExpected, "merchantOrdered.privateTerminal");
    }
}
