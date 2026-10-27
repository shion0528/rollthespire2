using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Parent host may dispatch this entry exclusively before its ordinary suite.
    // This file deliberately contains no benchmark, installation or game state IO.
    private static void TestNeowStructuralOptimizations(RenderingDevice rd)
    {
        TestStructuralPurePlanning();
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        SearchBatch[] batches = [new(0, 262144), new(0, 0), new(0, 1), new(33, 7), new(zzfz, 1031),
            new(zzfz + 7, 511), new(zzfz + 9, 513), new(PrefixHashCache.SpaceSize - 513, 513)];
        foreach (var fixture in new (string Name, bool Ordered, bool Same, bool Duplicate)[] { ("unorderedDifferentPools", false, false, false),
            ("orderedDifferentPools", true, false, false), ("unorderedSamePool", false, true, false),
            ("unorderedRepeatedMembership", false, false, true) })
            TestStructuralKPublic(rd, fixture.Name, StructuralKPlan(fixture.Ordered, fixture.Same, fixture.Duplicate), batches);
        var authoredZero = StructuralKPlan();
        uint[] zeroConditions = new uint[226]; authoredZero.Conditions.CopyTo(zeroConditions, 0);
        authoredZero = authoredZero with { Conditions = zeroConditions, HasAuthoredUpgrades = true };
        TestStructuralKPublic(rd, "knownAuthoredZeroBothRoutes", authoredZero, batches.Take(2).ToArray());
        TestStructuralFinalOnly(rd, batches);
        TestStructuralBonesOnly(rd, batches);
        TestStructuralDoubleWindow(rd);
        Emit(new { phase = "neowStructuralOptimizations", result = "PASS", realProductionExecutors = true,
            independentFullShuffleAndKCpuOracle = true, positiveResultsRequired = true,
            exactRetainedRewardsCheckpoints = true, fixedRangeFullLists = true,
            orderedUnorderedSameAndRepeatedPoolMembership = true, duplicateCurseRanksAndRequiredConjunction = true,
            zzfzLastSeedTailCancellationReuse = true, denseCompactPrivateOutput = true, capturedDoubleWindowOptions = true,
            newExperimentsDefaultOff = true });
    }

    private static NeowFamilyGpuPlan StructuralKPlan(bool ordered = false, bool same = false,
        bool repeatedMembership = false, bool leafy = false)
    {
        var original = InternalOrderPlan(14, leafy ? 4u : 28u);
        uint[] meta = original.Meta.ToArray(), conditions = original.Conditions.ToArray();
        meta[9] = 4; meta[82] = 3; meta[84] = 1; meta[85] = 14;
        conditions[70] = 2; conditions[71] = 100; conditions[72] = 200;
        conditions[74] = 0x80000000u | (ordered ? 4u : 0u);
        if (leafy)
        {
            meta[70] = 1; meta[82] = 7; meta[84] = 2; meta[86] = 4;
            meta[58] = meta[59] = 2;
            conditions[20] = 2; conditions[21] = 100; conditions[22] = 200; conditions[24] = 0x80000000u;
        }
        var cards = new List<uint>();
        uint[] pools = new uint[20 + 4 * 6 + 15];
        for (int pool = 0; pool < 4; pool++)
        for (int rarity = 0; rarity < 3; rarity++)
        {
            int at = 20 + pool * 6 + rarity * 2;
            pools[at] = (uint)cards.Count; pools[at + 1] = same && pool == 0 ? 2u : 1u;
            cards.Add(same ? pool == 0 ? 100u : (uint)(300 + pool) :
                pool == 0 || repeatedMembership && pool == 2 ? 100u : pool == 1 ? 200u : (uint)(300 + pool));
            if (same && pool == 0) cards.Add(200);
        }
        const int trailer = 44;
        pools[trailer + 12] = (uint)cards.Count; pools[trailer + 13] = 8;
        cards.AddRange(Enumerable.Range(500, 8).Select(i => (uint)i));
        pools[trailer + 6] = (uint)cards.Count; pools[trailer + 7] = 1; cards.Add(500);
        pools[trailer + 8] = (uint)cards.Count; pools[trailer + 9] = 0;
        return original with { Meta = meta, Conditions = conditions, PoolMeta = pools, Cards = cards.ToArray(),
            Strike = leafy ? [100, 999] : original.Strike, Defend = leafy ? [200, 999] : original.Defend };
    }

    private static void TestStructuralPurePlanning()
    {
        var plan = StructuralKPlan();
        var order = NeowInternalFilterOrder.Compile(plan);
        Require(order.Enabled && order.NicheAdvances.SequenceEqual(new uint[] { 6 }), "K fixture did not prove arrival0/final6.");
        Require(NeowStructuralCompiler.TryKaleidoscopePoolGate(plan, order, out uint target0, out uint target1,
            out bool ordered, out _ ) && target0 == 1 && target1 == 2 && !ordered, "K membership must use actual packed rarity slices.");
        // Independently perform both complete four-element shuffles, including
        // every possible later draw, to prove that only their first draw fixes
        // the excluded pool. No PRNG-stream independence is assumed.
        for (int excluded0 = 0; excluded0 < 4; excluded0++)
        for (int excluded1 = 0; excluded1 < 4; excluded1++)
        for (int later0 = 0; later0 < 6; later0++)
        for (int later1 = 0; later1 < 6; later1++)
        {
            uint Selected(int excluded, int later)
            {
                int[] pool = [0, 1, 2, 3];
                (pool[excluded], pool[3]) = (pool[3], pool[excluded]);
                int j = later / 2; (pool[j], pool[2]) = (pool[2], pool[j]);
                j = later % 2; (pool[j], pool[1]) = (pool[1], pool[j]);
                return (1u << pool[0]) | (1u << pool[1]) | (1u << pool[2]);
            }
            uint selected0 = Selected(excluded0, later0), selected1 = Selected(excluded1, later1);
            foreach (var masks in new[] { (1u, 2u), (1u, 1u), (5u, 2u), (0u, 2u), (15u, 15u) })
            foreach (bool inOrder in new[] { false, true })
            {
                bool expected = (selected0 & masks.Item1) != 0 && (selected1 & masks.Item2) != 0 ||
                    !inOrder && (selected0 & masks.Item2) != 0 && (selected1 & masks.Item1) != 0;
                Require(NeowStructuralCompiler.Pool4Allows((uint)excluded0, (uint)excluded1,
                    masks.Item1, masks.Item2, inOrder) == expected, "K pool gate rejected a possible ordered/unordered assignment.");
            }
        }
        foreach (var curseFixture in new[] {
            InternalOrderCurseLists(InternalOrderPlan(), [500, 501, 500, 502], [500, 500], [502]),
            InternalOrderCurseLists(InternalOrderPlan(), [500, 501, 500, 502], [500, 501], []),
            InternalOrderCurseLists(InternalOrderPlan(), [500, 501, 500, 502], [500], [500]) })
        {
            Require(NeowStructuralCompiler.TryFinalCurseIntervals(curseFixture, out var gate), "Bounded final-curse intervals failed.");
            int trailer = 20 + (int)curseFixture.Meta[9] * 6, count = (int)curseFixture.PoolMeta[trailer + 13];
            for (int rank = 0; rank <= count; rank++)
            foreach (long delta in new long[] { -1, 0, 1 })
            {
                long value = checked((long)StaticDrawGateCompiler.Boundary(rank, count)) + delta;
                if (value < 0 || (ulong)value >= StaticDrawGateCompiler.MantissaSpace) continue;
                int actualRank = (int)(((UInt128)(ulong)value * (uint)count) >> 53);
                uint id = curseFixture.Cards[curseFixture.PoolMeta[trailer + 12] + (uint)actualRank];
                bool expected = StructuralCurseMatches(curseFixture, id);
                Require(gate!.Contains((ulong)value) == expected, "Final-curse interval boundary changed conjunction/ban/repeated ranks.");
            }
        }
        foreach (var rejected in new[] { plan with { HasAuthoredUpgrades = true },
            InternalOrderPlan(14, 19), plan with { JointTransformMetaOffset = 0 }, plan with { DirectNestedMode = 1 } })
            Require(!NeowStructuralCompiler.TryKaleidoscopePoolGate(rejected, NeowInternalFilterOrder.Compile(rejected),
                out _, out _, out _, out _), "K gate accepted an unknown arrival or unsupported route.");
        var both = new NeowStructuralTrialOptions(true, true, true);
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled))
            Require(!both.WithSettings().Any, "All-disabled preferences enabled a new structural optimization.");
        Emit(new { phase = "neowStructuralPurePlanning", result = "PASS", exhaustiveFourPoolShuffles = true,
            exactCurseBoundaries = true, duplicateAndContradictoryRequiredLists = true, settingsAllOff = true });
    }

    private static bool StructuralCurseMatches(NeowFamilyGpuPlan plan, uint id)
    {
        int trailer = 20 + (int)plan.Meta[9] * 6;
        for (uint i = 0; i < plan.PoolMeta[trailer + 7]; i++)
            if (plan.Cards[plan.PoolMeta[trailer + 6] + i] != id) return false;
        for (uint i = 0; i < plan.PoolMeta[trailer + 9]; i++)
            if (plan.Cards[plan.PoolMeta[trailer + 8] + i] == id) return false;
        return true;
    }

    private static bool StructuralKRouteOracle(NeowFamilyGpuPlan plan, ulong root, InternalOrderPair pair, bool reverse)
    {
        var rewards = new Beta110FastRng(pair.Rewards);
        var niche = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 35)));
        uint first = plan.Conditions[71], second = plan.Conditions[72];
        bool ordered = (plan.Conditions[74] & 4) != 0;
        foreach (uint source in reverse ? new[] { pair.B, pair.A } : new[] { pair.A, pair.B })
        {
            if (source == 28) { rewards.NextFloat(); continue; }
            if (source == 4)
            {
                var transformations = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 37)));
                uint strike = plan.Strike[transformations.NextInt((int)plan.Meta[58])];
                uint defend = plan.Defend[transformations.NextInt((int)plan.Meta[59])];
                uint a = plan.Conditions[21], b = plan.Conditions[22];
                if (!(strike == a && defend == b || strike == b && defend == a)) return false;
                continue;
            }
            Require(source == 14, "Structural K oracle supports only the documented K+small capsule fixture.");
            bool[] groupA = new bool[2], groupB = new bool[2];
            for (int group = 0; group < 2; group++)
            {
                uint[] pools = [0, 1, 2, 3]; niche.UnstableShuffle<uint>(pools);
                for (int item = 0; item < 3; item++)
                {
                    float roll = rewards.NextFloat();
                    int rarity = roll < .0149f ? 2 : roll < .0149f + .37f ? 1 : 0;
                    int at = 20 + (int)pools[item] * 6 + rarity * 2;
                    uint id = plan.Cards[plan.PoolMeta[at] + (uint)rewards.NextInt((int)plan.PoolMeta[at + 1])];
                    rewards.NextFloat();
                    groupA[group] |= id == first; groupB[group] |= id == second;
                }
            }
            if (!(groupA[0] && groupB[1] || !ordered && groupB[0] && groupA[1])) return false;
        }
        int trailer = 20 + (int)plan.Meta[9] * 6;
        uint curse = plan.Cards[plan.PoolMeta[trailer + 12] + (uint)niche.NextInt((int)plan.PoolMeta[trailer + 13])];
        return StructuralCurseMatches(plan, curse);
    }

    private static ulong[] StructuralKExpected(NeowFamilyGpuPlan plan, SearchBatch batch, IEnumerable<ulong>? subset = null) =>
        (subset ?? Enumerable.Range(0, batch.BatchCandidateCount).Select(i => (ulong)i)).Where(ordinal =>
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            return InternalOrderPairOracle(plan, root, out var pair) &&
                (StructuralKRouteOracle(plan, root, pair, false) || plan.Meta[12] == 255 && StructuralKRouteOracle(plan, root, pair, true));
        }).Order().ToArray();

    private static void TestStructuralKPublic(RenderingDevice rd, string name, NeowFamilyGpuPlan plan, SearchBatch[] batches)
    {
        NeowStructuralTrialOptions[] controls = [default, new(true, false, false), new(false, true, false), new(true, true, false)];
        foreach (bool privateOutput in new[] { false, true })
        foreach (var options in controls)
        {
            using var output = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
            using var executor = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: output,
                useIndirectStaging: false, useInternalFilterOrder: true, structuralTrials: options);
            Require(executor.StructuralPhysical.KaleidoscopePoolGate == options.KaleidoscopePoolGate &&
                executor.StructuralPhysical.FinalCurseIntervals == options.FinalCurseIntervals,
                "The structural fixture did not execute its requested path.");
            int positive = 0;
            foreach (var batch in batches)
            {
                ulong[] expected = StructuralKExpected(plan, batch);
                positive += expected.Length;
                var result = RunPrefixScope(executor, output, FamilyCandidateSet.Dense(batch), CancellationToken.None, out var metrics);
                Require(result.ExportAbi1().Span.SequenceEqual(expected), "Full K/curse result differs from independent CPU oracle: " + name);
                if (batch.BatchCandidateCount > 0)
                    CheckInternalOrderPairCheckpoints(rd, executor, plan, batch, metrics.PairSurvivors);
                if (!privateOutput)
                {
                    ulong[] subset = SharedNeowSubset(batch, expected);
                    var compact = executor.Execute(FamilyCandidateSet.FromSortedAbi1(batch, subset), CancellationToken.None, out _);
                    Require(compact.ExportAbi1().Span.SequenceEqual(StructuralKExpected(plan, batch, subset)) &&
                        executor.StructuralRevisionFor(true) is null, "Compact K path did not retain the original matcher/identity.");
                }
            }
            Require(positive > 0, "Structural K correctness had no positive complete-route payload: " + name);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            long submitted = FamilyGpuComputeUtility.CompletedSubmissionTicks;
            Expect<OperationCanceledException>(() => RunPrefixScope(executor, output,
                FamilyCandidateSet.Dense(batches[0]), cancelled.Token, out _));
            Require(submitted == FamilyGpuComputeUtility.CompletedSubmissionTicks, "Cancelled structural K call submitted work.");
            var reused = RunPrefixScope(executor, output, FamilyCandidateSet.Dense(batches[0]), CancellationToken.None, out _);
            Require(reused.ExportAbi1().Span.SequenceEqual(StructuralKExpected(plan, batches[0])),
                "Structural K owner was not reusable after pre-cancellation.");
            Emit(new { phase = "neowStructuralKParity", result = "PASS", fixture = name, privateOutput,
                pools = options.KaleidoscopePoolGate, curse = options.FinalCurseIntervals, positive,
                fullCpuOracle = true, exactRewardsCheckpoints = true, compactFallback = true });
        }
    }

    private static void TestStructuralFinalOnly(RenderingDevice rd, SearchBatch[] batches)
    {
        foreach (var fixture in InternalOrderFixtures())
        {
            using var executor = new NeowFamilyGpuExecutor(rd, fixture.Plan, 42,
                useIndirectStaging: false, useInternalFilterOrder: true,
                structuralTrials: new(false, true, false));
            foreach (var batch in batches.Take(3))
            {
                var actual = executor.Execute(FamilyCandidateSet.Dense(batch), CancellationToken.None, out _);
                Require(actual.ExportAbi1().Span.SequenceEqual(InternalOrderExpected(fixture.Plan, batch)),
                    "Final intervals changed legal-route OR, repeats, contradiction or unknown fallback: " + fixture.Name);
            }
        }
        Emit(new { phase = "neowStructuralFinalOnly", result = "PASS", originalIndependentFixtures = true,
            multipleKnownArrivals = true, unknownArrivalFallback = true });
    }

    private static NeowFamilyGpuPlan StructuralBonesPlan()
    {
        var plan = Plan(false);
        plan.Meta[66] = plan.Meta[67] = plan.Meta[82] = plan.Meta[83] = plan.Meta[84] = 0;
        plan.Meta[81] = Beta110FastRelicCatalog.NeowsBones; plan.Meta[71] = Beta110FastRelicCatalog.NeowsBones;
        for (int at = 48; at < 54; at++) plan.Meta[at] = 0;
        return plan;
    }

    private static void TestStructuralBonesOnly(RenderingDevice rd, SearchBatch[] batches)
    {
        var plan = StructuralBonesPlan();
        Require(NeowStructuralCompiler.TryBonesIdentityOnly(plan, out _), "Bones-only fixture not admitted.");
        var constrained = StructuralBonesPlan(); constrained.Meta[48] = 1;
        Require(!NeowStructuralCompiler.TryBonesIdentityOnly(constrained, out _) &&
            !NeowStructuralCompiler.TryBonesIdentityOnly(StructuralKPlan(), out _), "Bones-only ignored an internal identity/local condition.");
        foreach (bool enabled in new[] { false, true })
        foreach (bool privateOutput in new[] { false, true })
        {
            using var output = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
            using var executor = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: output,
                structuralTrials: new(false, false, enabled));
            Require(executor.StructuralPhysical.BonesIdentityOnly == enabled, "Bones-only actual control differs from request.");
            int positive = 0;
            foreach (var batch in batches)
            {
                ulong[] expected = Enumerable.Range(0, batch.BatchCandidateCount).Select(i => (ulong)i).Where(ordinal =>
                {
                    ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
                    var entry = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 31)));
                    if ((plan.Meta[69] & (1u << entry.NextInt((int)plan.Meta[39]))) != 0) return false;
                    // The independent old path still performs the entire real
                    // shuffle, even though no condition observes its result.
                    var rewards = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 33)));
                    uint[] bones = plan.Bones.ToArray(); rewards.UnstableShuffle<uint>(bones);
                    return true;
                }).ToArray();
                positive += expected.Length;
                var result = RunPrefixScope(executor, output, FamilyCandidateSet.Dense(batch), CancellationToken.None, out _);
                Require(result.ExportAbi1().Span.SequenceEqual(expected), "Bones-only removed a top-level condition or changed ordinals.");
                if (!privateOutput)
                {
                    ulong[] subset = SharedNeowSubset(batch, expected);
                    var compact = executor.Execute(FamilyCandidateSet.FromSortedAbi1(batch, subset), CancellationToken.None, out _);
                    Require(compact.ExportAbi1().Span.SequenceEqual(expected.Intersect(subset).Order().ToArray()), "Bones-only compact results differ.");
                }
            }
            Require(positive > 0, "Bones-only check did not test any accepted output.");
        }
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled))
        using (var executor = new NeowFamilyGpuExecutor(rd, plan, 42, structuralTrials: new(true, true, true)))
            Require(executor.StructuralPhysical.Revision is null, "All settings off did not restore the old N body.");
        Emit(new { phase = "neowStructuralBonesOnly", result = "PASS", fullShuffleCpuControl = true,
            internalConstraintsExcluded = true, positiveOutput = true, noRngStateCrossesFamilyBoundary = true });
    }

    private static void TestStructuralDoubleWindow(RenderingDevice rd)
    {
        var plan = StructuralKPlan(leafy: true);
        using var single = new NeowFamilyGpuExecutor(rd, plan, 42,
            useIndirectStaging: false, useInternalFilterOrder: true,
            useDoubleWindowBatching: false, structuralTrials: default(NeowStructuralTrialOptions));
        using var paired = new NeowFamilyGpuExecutor(rd, plan, 42,
            useIndirectStaging: false, useInternalFilterOrder: true,
            useDoubleWindowBatching: true, structuralTrials: new(true, true, false));
        Require(paired.SupportsDenseWindowBatching && paired.StructuralPhysical.KaleidoscopePoolGate,
            "The paired fixture did not admit both K pool gate and existing transport.");
        int size = NeowFamilyGpuExecutor.Capacity;
        var left = single.Execute(FamilyCandidateSet.Dense(new SearchBatch(0, size)), CancellationToken.None, out _);
        var right = single.Execute(FamilyCandidateSet.Dense(new SearchBatch((ulong)size, size)), CancellationToken.None, out _);
        ulong[] expected = left.ExportAbi1().ToArray().Concat(right.ExportAbi1().ToArray().Select(i => i + (ulong)size)).ToArray();
        var batch = new SearchBatch(0, NeowFamilyGpuExecutor.DenseWindowBatchCapacity);
        var actual = paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(batch), CancellationToken.None, out _);
        Require(actual.ExportAbi1().Span.SequenceEqual(expected) && actual.Count > 0 &&
            paired.WindowTransportRevision == NeowFamilyGpuExecutor.DoubleWindowRevision,
            "Double-window structural trials omitted a window or changed complete results.");
        foreach (ulong ordinal in actual.ExportAbi1().Span)
            Require(StructuralKExpected(plan, new SearchBatch(ordinal, 1)).SequenceEqual(new ulong[] { 0 }),
                "A paired structural output failed the independent complete K oracle.");
        var field = typeof(NeowFamilyGpuExecutor).GetField("_secondWindow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var second = (NeowFamilyGpuExecutor)field.GetValue(paired)!;
        Require(second.StructuralPhysical.KaleidoscopePoolGate && second.StructuralPhysical.FinalCurseIntervals &&
            second.StructuralRevisionFor(false) == paired.StructuralRevisionFor(false),
            "The second workspace did not capture identical options and physical identity.");
        Emit(new { phase = "neowStructuralDoubleWindow", result = "PASS", outputs = actual.Count,
            oldTwoSingleWindowsCompared = true, positiveOutputsCpuChecked = true, noWindowPolicyChange = true });
    }
}
