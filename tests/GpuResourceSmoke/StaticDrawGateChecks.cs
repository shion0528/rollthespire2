using System.Numerics;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Entry for the central real-device suite. No game singleton or installed
    // state is used; these are synthetic plans and the production executor.
    private static void TestStaticDrawGates(RenderingDevice rd)
    {
        TestStaticDrawGateMath();
        TestStaticDrawGateSources();
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        SearchBatch[] batches = [new(0, 32768), new(0, 0), new(zzfz, 1031), new(zzfz + 7, 513),
            new(PrefixHashCache.SpaceSize - 513, 513), new(PrefixHashCache.SpaceSize - 1, 1)];
        foreach (var fixture in StaticDrawGatePlans())
        foreach (bool privateOutput in new[] { false, true })
            TestStaticDrawGateParity(rd, fixture.Name, fixture.Plan, privateOutput, batches);
        Emit(new { phase = "staticDrawGates", result = "PASS", productionExecutor = true,
            exactMantissaIntervals = true, duplicateIds = true, unorderedAndRepeatedTargets = true,
            stagedAndFused = true, zzfzAndLastSeed = true, emptyAndNonzeroPayload = true });
    }

    private static void TestStaticDrawGateMath()
    {
        int cases = 0;
        ulong space = StaticDrawGateCompiler.MantissaSpace;
        for (int length = 1; length <= 128; length++)
        for (int selected = 0; selected < length; selected++)
        {
            Require(StaticDrawGateCompiler.TryCompileRanks(length, i => i == selected, 1, out var gate),
                "An isolated rank failed to compile.");
            ulong lo = StaticDrawGateCompiler.Boundary(selected, length), hi = StaticDrawGateCompiler.Boundary(selected + 1, length);
            var probes = new HashSet<ulong> { 0, space - 1, lo, hi - 1 };
            if (lo > 0) probes.Add(lo - 1);
            if (hi < space) probes.Add(hi);
            foreach (ulong x in probes)
            {
                ulong lowProduct = (uint)x * (ulong)(uint)length;
                ulong upper = (x >> 32) * (uint)length + (lowProduct >> 32);
                uint rank = (uint)(upper >> 21);
                Require(gate!.Contains(x) == (rank == selected), "Compiled interval changed production limb arithmetic.");
                cases++;
            }
        }
        foreach (int length in new[] { 1, 10, 64, 128, 65536, int.MaxValue })
        foreach (int rank in new[] { 0, length / 2, length - 1, length }.Distinct())
        {
            ulong expected = (ulong)(((BigInteger)rank * space + length - 1) / length);
            Require(StaticDrawGateCompiler.Boundary(rank, length) == expected, "Wide interval construction overflowed.");
        }
        uint[] duplicatePool = [9, 4, 9, 4, 9, 9, 4];
        Require(!StaticDrawGateCompiler.TryCompilePool(duplicatePool, duplicatePool.Length, [9u], 2, out _),
            "An interval budget silently dropped duplicate ID positions.");
        Require(StaticDrawGateCompiler.TryCompilePool(duplicatePool, duplicatePool.Length, [9u], 3, out var duplicate) &&
            duplicate!.Intervals.Count == 3, "Duplicate IDs or adjacent interval merging changed.");
        Require(StaticDrawGateCompiler.TryCompileRanks(10, _ => false, 2, out var none) && none!.GlslExpression("x") == "false" &&
            StaticDrawGateCompiler.TryCompileRanks(10, _ => true, 2, out var all) && all!.GlslExpression("x") == "true",
            "Constant membership gates were not preserved.");
        Emit(new { phase = "staticDrawGateMath", result = "PASS", boundaryCases = cases,
            largeBoundUsesBigIntegerOracle = true, intervalOverflowFallsBack = true });
    }

    private static void TestStaticDrawGateSources()
    {
        var plan = PrefixScopePlan(true);
        string dense = NeowFamilyGpuExecutor.ShaderSource(0, leafyPreGate: true, localResults: true, prefixHashCache: true);
        var off = NeowStaticDrawGates.Apply(dense, 0, plan, false);
        Require(off.Source == dense && off.Applied == NeowStaticDrawGates.Kinds.None, "Disabled gates changed the control shader.");
        var on = NeowStaticDrawGates.Apply(dense, 0, plan, true);
        Require(on.Applied == (NeowStaticDrawGates.Kinds.Curse | NeowStaticDrawGates.Kinds.Leafy) &&
            on.Source.Contains("// RT2_STATIC_DRAW_CURSE", StringComparison.Ordinal) &&
            on.Source.Contains("// RT2_STATIC_DRAW_LEAFY", StringComparison.Ordinal), "Dense gates did not apply.");
        string fused = NeowFamilyGpuExecutor.ShaderSource(3, leafyPreGate: true, localResults: true);
        var fusedOn = NeowStaticDrawGates.Apply(fused, 3, plan, true);
        Require(fusedOn.Applied == NeowStaticDrawGates.Kinds.Leafy &&
            fusedOn.Source.Contains("if (!draw_neow_curse(root,event_rng,curse_ordinal,curse)) return false;", StringComparison.Ordinal) &&
            !fusedOn.Source.Contains("// RT2_STATIC_DRAW_CURSE", StringComparison.Ordinal),
            "Fused top replay lost its full RNG continuation.");
        string consumer = NeowFamilyGpuExecutor.ShaderSource(1, leafyPreGate: true);
        Require(NeowStaticDrawGates.Apply(consumer, 1, plan, true).Source == consumer,
            "A downstream consumer was rewritten.");
        string legacyLeafy = NeowFamilyGpuExecutor.ShaderSource(0, leafyPreGate: true, legacyLeafyGate: true);
        Require(NeowStaticDrawGates.Apply(legacyLeafy, 0, plan, true).Applied == NeowStaticDrawGates.Kinds.Curse,
            "Static gates silently replaced the explicit legacy Leafy control.");
        var many = PrefixScopePlan(true);
        many.Meta[58] = 7;
        many = many with { Strike = [100, 200, 100, 200, 100, 200, 100] };
        string manySource = NeowFamilyGpuExecutor.ShaderSource(3, leafyPreGate: true);
        var fallback = NeowStaticDrawGates.Apply(manySource, 3, many, true);
        Require(fallback.Applied == NeowStaticDrawGates.Kinds.None && fallback.Source == manySource,
            "Too many Leafy intervals did not preserve the original shader.");
        Emit(new { phase = "staticDrawGateSources", result = "PASS", disabledSourceIdentical = true,
            fusedContinuationPreserved = true, legacyControlPreserved = true, largeIntervalFallback = true });
    }

    private static IEnumerable<(string Name, NeowFamilyGpuPlan Plan)> StaticDrawGatePlans()
    {
        yield return ("stagedCurse", PrefixScopePlan(false));
        yield return ("stagedLeafy", PrefixScopePlan(true));
        var duplicates = PrefixScopePlan(true);
        duplicates.Meta[58] = duplicates.Meta[59] = 5;
        duplicates = duplicates with { Strike = [100, 200, 100, 300, 200], Defend = [200, 100, 400, 200, 100] };
        yield return ("stagedDuplicateIds", duplicates);
        var repeated = PrefixScopePlan(true);
        repeated.Meta[58] = repeated.Meta[59] = 5; repeated.Conditions[22] = 100;
        repeated = repeated with { Strike = [100, 200, 100, 300, 200], Defend = [200, 100, 400, 200, 100] };
        yield return ("stagedRepeatedTarget", repeated);
        var fused = PrefixScopePlan(true); fused.Meta[66] = 0;
        yield return ("fusedLeafy", fused);
    }

    private static ulong[] StaticDrawGateExpected(NeowFamilyGpuPlan plan, SearchBatch batch)
    {
        var result = new List<ulong>();
        for (int ordinal = 0; ordinal < batch.BatchCandidateCount; ordinal++)
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + (ulong)ordinal), 0);
            if (plan.UsesLeafyPreGate)
            {
                var rng = new Beta110FastRng(unchecked(root + XxHash64.HashUtf8("transformations", 0)));
                uint a = plan.Strike[rng.NextInt((int)plan.Meta[58])], b = plan.Defend[rng.NextInt((int)plan.Meta[59])];
                uint target0 = plan.Conditions[21], target1 = plan.Conditions[22];
                if (!((a == target0 && b == target1) || (a == target1 && b == target0))) continue;
            }
            var curse = new Beta110FastRng(unchecked(root + XxHash64.HashUtf8("NEOW", 0)));
            int index = curse.NextInt((int)plan.Meta[39]);
            if ((plan.Meta[69] & (1u << index)) == 0 && plan.Meta[71 + index] != uint.MaxValue)
                result.Add((ulong)ordinal);
        }
        return result.ToArray();
    }

    private static void TestStaticDrawGateParity(RenderingDevice rd, string name, NeowFamilyGpuPlan plan,
        bool privateOutput, IReadOnlyList<SearchBatch> batches)
    {
        using var oldOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var newOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var control = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: oldOutput,
            useIndirectStaging: false, useStaticDrawGates: false);
        using var optimized = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: newOutput,
            useIndirectStaging: false, useStaticDrawGates: true);
        int nonzero = 0;
        foreach (SearchBatch batch in batches)
        {
            var input = FamilyCandidateSet.Dense(batch);
            ulong[] expected = StaticDrawGateExpected(plan, batch);
            var a = RunPrefixScope(control, oldOutput, input, CancellationToken.None, out var am);
            var b = RunPrefixScope(optimized, newOutput, input, CancellationToken.None, out var bm);
            Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected),
                "Static draw gate population mismatch: " + name);
            Require(am.Processed == bm.Processed && am.Survivors == bm.Survivors &&
                am.CurseSurvivors == bm.CurseSurvivors && am.PairSurvivors == bm.PairSurvivors &&
                am.Dispatches == bm.Dispatches && am.ReadbackBytes == bm.ReadbackBytes &&
                control.ResidentBytes == optimized.ResidentBytes, "Static draw gate changed transport or checkpoints.");
            Require(PrefixScopeHeader(rd, control).AsSpan().SequenceEqual(PrefixScopeHeader(rd, optimized)),
                "Static draw gate changed native validation headers.");
            nonzero += expected.Length > 0 ? 1 : 0;
        }
        Require(nonzero > 0 && control.StaticDrawGateRevision == NeowFamilyGpuExecutor.LegacyStaticDrawGateRevision &&
            optimized.UsesStaticCurseDrawGate == plan.UsesStagedDense && optimized.UsesStaticLeafyDrawGate == plan.UsesLeafyPreGate,
            "Static draw tests did not exercise nonzero output and actual gate identities.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        var retry = FamilyCandidateSet.Dense(batches.First(b => b.BatchCandidateCount == 1031));
        Expect<OperationCanceledException>(() => RunPrefixScope(optimized, newOutput, retry, cancelled.Token, out _));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, "Cancelled static gates submitted work.");
        Require(RunPrefixScope(optimized, newOutput, retry, CancellationToken.None, out _).ExportAbi1().Span
            .SequenceEqual(StaticDrawGateExpected(plan, retry.Batch)), "Static gate reuse failed after cancellation.");
        Emit(new { phase = "staticDrawGateParity", result = "PASS", fixture = name, privateOutput, nonzeroBatches = nonzero,
            revision = optimized.StaticDrawGateRevision, curse = optimized.UsesStaticCurseDrawGate,
            leafy = optimized.UsesStaticLeafyDrawGate, headerParity = true, cancelledAndReused = true });
    }
}
