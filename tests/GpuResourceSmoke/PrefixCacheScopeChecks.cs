using System.Diagnostics;
using System.Reflection;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Synthetic numeric fixtures use the real stage0/1/2 executors and borrowed
    // output buffer, rather than a replacement shader or an installed game.
    private static void TestExpandedPrefixCacheScope(RenderingDevice rd)
    {
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        Require(zzfz % PrefixHashCache.BlockSize + 1031UL > PrefixHashCache.BlockSize,
            "The ZZFZ fixture no longer crosses a prefix row.");
        SearchBatch[] batches = [new(0, 65536), new(0, 0), new(0, 1), new(1, 7), new(33, 8),
            new(zzfz, 1031), new(zzfz + 1, 511), new(zzfz + 7, 512), new(zzfz + 9, 513),
            new(PrefixHashCache.BlockSize - 7UL, 513), new(0x100000003UL, 4097),
            new(123456789012345UL, 513), new(PrefixHashCache.SpaceSize - 513, 513),
            new(PrefixHashCache.SpaceSize - 1, 1), new(PrefixHashCache.SpaceSize, 0)];
        foreach (bool leafy in new[] { false, true })
        foreach (bool privateOutput in new[] { false, true })
            TestExpandedPrefixCacheParity(rd, leafy, privateOutput, batches);
        TestExpandedPrefixPrivateInputGuard(rd, zzfz);
        Emit(new { phase = "prefixCacheExpandedScope", result = "PASS", paths = 4,
            publicAndPrivateOutput = true, stagedWithAndWithoutLeafy = true,
            cpuOracle = "CanonicalXxHash64AndRng;SyntheticTwoIdentityPool",
            arbitraryOffsets = true, zzfzCrossesPrefix = true, final12Z = true,
            privateInputStillLegacy = true, doubleWindowScopeUnchanged = true });
    }

    private static NeowFamilyGpuPlan PrefixScopePlan(bool leafy)
    {
        var plan = Plan(leafy);
        // Both tracked identities always survive the two-entry shuffle. The
        // first-curse RNG and optional 2x3 transform draw remain root-dependent,
        // so a wrong hash changes the observable output/counters densely enough
        // to catch a bad binding or row mapping without a rare-output fixture.
        plan.Meta[8] = 2;
        if (leafy) { plan.Meta[58] = 2; plan.Meta[59] = 3; }
        return plan;
    }

    private static ulong[] PrefixScopeExpected(NeowFamilyGpuPlan plan, SearchBatch batch,
        IEnumerable<ulong>? ordinals = null)
    {
        var result = new List<ulong>();
        ulong eventSalt = XxHash64.HashUtf8("NEOW", 0), transformSalt = XxHash64.HashUtf8("transformations", 0);
        foreach (ulong ordinal in ordinals ?? Enumerable.Range(0, batch.BatchCandidateCount).Select(i => (ulong)i))
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            if (plan.UsesLeafyPreGate)
            {
                var transform = new Beta110FastRng(unchecked(root + transformSalt));
                uint first = plan.Strike[transform.NextInt((int)plan.Meta[58])];
                uint second = plan.Defend[transform.NextInt((int)plan.Meta[59])];
                if (!((first == 100 && second == 200) || (first == 200 && second == 100))) continue;
            }
            var eventRng = new Beta110FastRng(unchecked(root + eventSalt));
            if (eventRng.NextInt((int)plan.Meta[39]) == 0) result.Add(ordinal);
        }
        return result.ToArray();
    }

    private static FamilyCandidateSet RunPrefixScope(NeowFamilyGpuExecutor executor, PrivateOrdinalBuffer? output,
        FamilyCandidateSet input, CancellationToken token, out NeowFamilyGpuMetrics metrics)
    {
        if (output is null) return executor.Execute(input, token, out metrics);
        int count = executor.ExecutePrivateStage(input.Batch, input.Count, token, out metrics);
        return output.ReadForVerification(input.Batch, count);
    }

    private static uint[] PrefixScopeHeader(RenderingDevice rd, NeowFamilyGpuExecutor executor)
    {
        var field = typeof(NeowFamilyGpuExecutor).GetField("_header", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidDataException("N header inspection seam changed.");
        return FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData((Rid)field.GetValue(executor)!), 8);
    }

    private static void TestExpandedPrefixCacheParity(RenderingDevice rd, bool leafy, bool privateOutput,
        IReadOnlyList<SearchBatch> batches)
    {
        var plan = PrefixScopePlan(leafy);
        using var legacyOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var cachedOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var legacy = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: legacyOutput,
            useIndirectStaging: false, usePreSubmitHeaderReadback: true, usePrefixHashCache: false);
        using var cached = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: cachedOutput,
            useIndirectStaging: false, usePreSubmitHeaderReadback: true, usePrefixHashCache: true);
        Require(!legacy.UsesPrefixHashCache && cached.UsesPrefixHashCache &&
            legacy.PrefixHashRevision == NeowFamilyGpuExecutor.LegacyPrefixHashRevision &&
            cached.PrefixHashRevision == NeowFamilyGpuExecutor.PrefixHashCacheRevision,
            "Expanded-scope executor did not capture the requested cache mode/revision.");
        bool expectedDoubleWindow = leafy && !privateOutput;
        Require(legacy.SupportsDenseWindowBatching == expectedDoubleWindow &&
            cached.SupportsDenseWindowBatching == expectedDoubleWindow, "Prefix expansion changed double-window admission.");
        long outputs = 0;
        int nonzero = 0, headerRejects = 0;
        foreach (SearchBatch batch in batches)
        {
            var input = FamilyCandidateSet.Dense(batch);
            ulong[] expected = PrefixScopeExpected(plan, batch);
            var a = RunPrefixScope(legacy, legacyOutput, input, CancellationToken.None, out var am);
            var b = RunPrefixScope(cached, cachedOutput, input, CancellationToken.None, out var bm);
            Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected),
                $"Expanded prefix output mismatch: leafy={leafy},private={privateOutput},base={batch.BatchBase},count={batch.BatchCandidateCount}.");
            Require(am.Processed == batch.BatchCandidateCount && bm.Processed == am.Processed &&
                am.Survivors == expected.Length && bm.Survivors == expected.Length &&
                am.CurseSurvivors == expected.Length && bm.CurseSurvivors == am.CurseSurvivors &&
                am.PairSurvivors == expected.Length && bm.PairSurvivors == am.PairSurvivors &&
                am.Dispatches == (batch.BatchCandidateCount == 0 ? 0 : 3) && bm.Dispatches == am.Dispatches,
                "Expanded prefix processed/survivor/checkpoint/dispatch metrics differ.");
            uint[] ah = PrefixScopeHeader(rd, legacy), bh = PrefixScopeHeader(rd, cached);
            Require(ah.AsSpan().SequenceEqual(bh) && bh[0] == 0x4e464d52u && bh[1] == 1 && bh[3] == 0 &&
                bh[4] == batch.BatchCandidateCount && bh[5] == 42 && bh[2] == expected.Length &&
                bh[6] == expected.Length && bh[7] == expected.Length,
                "Expanded prefix native headers differ from the independently checked population.");
            long prefixBytes = cached.LastSparsePayloadPrefetchBytes;
            long expectedBytes = 32L + prefixBytes +
                (privateOutput || (long)expected.Length * sizeof(uint) <= prefixBytes ? 0 : expected.Length * sizeof(uint));
            Require(am.ReadbackBytes == expectedBytes && bm.ReadbackBytes == expectedBytes,
                "Private output gained a production payload readback or public payload bytes changed.");
            if (batch.BatchCandidateCount > 0)
                Require(cached.ResidentBytes == legacy.ResidentBytes + PrefixHashCache.MaximumTableWords * sizeof(uint),
                    "Expanded cache workspace did not contain exactly the bounded prefix table.");
            if (expected.Length > 0)
            {
                nonzero++; outputs += expected.Length;
                if (privateOutput && headerRejects == 0)
                    headerRejects = TestExpandedPrefixHeaderValidation(cached, input, bh);
            }
        }
        Require(nonzero > 0 && outputs > 0 && (!privateOutput || headerRejects > 0),
            "Expanded prefix test only exercised zero-output batches.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var tail = FamilyCandidateSet.Dense(batches.Single(b => b.BatchCandidateCount == 1031));
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => RunPrefixScope(cached, cachedOutput, tail, cancelled.Token, out _));
        Require(FamilyGpuComputeUtility.CompletedSubmissionTicks == before, "Pre-cancelled expanded cache submitted GPU work.");
        var reused = RunPrefixScope(cached, cachedOutput, tail, CancellationToken.None, out _);
        Require(reused.ExportAbi1().Span.SequenceEqual(PrefixScopeExpected(plan, tail.Batch)),
            "Expanded cache cancellation broke subsequent row-table/workspace reuse.");
        Emit(new { phase = "prefixCacheScopeParity", result = "PASS", leafy, privateOutput,
            cases = batches.Count, nonzeroBatches = nonzero, outputs, cacheEnabled = cached.UsesPrefixHashCache,
            cachedRevision = cached.PrefixHashRevision, legacyRevision = legacy.PrefixHashRevision,
            doubleWindowAdmitted = cached.SupportsDenseWindowBatching, headerRejects, cancelledBeforeSubmit = true, safeReuse = true });
    }

    // Call the real completion/validation entry after the GPU has synchronized.
    // A private-output completion only returns validated counts, so these header
    // mutations cannot submit another shader or read an invented payload size.
    private static int TestExpandedPrefixHeaderValidation(NeowFamilyGpuExecutor executor,
        FamilyCandidateSet input, uint[] original)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type recordedType = typeof(NeowFamilyGpuExecutor).GetNestedType("RecordedWindow", BindingFlags.NonPublic)
            ?? throw new InvalidDataException("N recorded-window validation seam changed.");
        var constructor = recordedType.GetConstructors(flags).Single(c => c.GetParameters().Length == 6);
        object recorded = constructor.Invoke([input, input.Count, true, (uint)((input.Count + 511) / 512), 0d, Stopwatch.StartNew()]);
        var complete = typeof(NeowFamilyGpuExecutor).GetMethod("CompleteWindow", flags)
            ?? throw new InvalidDataException("N window completion validation seam changed.");
        NeowFamilyGpuMetrics Complete(uint[] header)
        {
            object?[] args = [recorded, header, 0d, null];
            Require(complete.Invoke(executor, args) is null, "Private completion exposed a public candidate payload.");
            return (NeowFamilyGpuMetrics)args[3]!;
        }
        Require(Complete(original).Survivors == original[2], "Valid nonzero private header was rejected.");
        uint[] zero = (uint[])original.Clone(); zero[2] = 0;
        Require(Complete(zero).Survivors == 0, "Valid zero-output private header was rejected.");
        var mutations = new List<uint[]>();
        mutations.Add(original[..7]);
        mutations.Add([..original, 0u]);
        void Add(int at, uint value) { var h = (uint[])original.Clone(); h[at] = value; mutations.Add(h); }
        Add(0, 0); Add(1, 2); Add(3, 1); Add(4, original[4] - 1); Add(5, 43);
        Add(2, original[4] + 1); Add(6, NeowFamilyGpuPlan.CurseCapacity + 1u);
        Add(7, NeowFamilyGpuPlan.PairCapacity + 1u); Add(6, 0);
        foreach (uint[] invalid in mutations)
        {
            bool rejected = false;
            try { Complete(invalid); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
            Require(rejected, "Expanded private cache completion accepted a corrupt header.");
        }
        return mutations.Count;
    }

    private static void TestExpandedPrefixPrivateInputGuard(RenderingDevice rd, ulong batchBase)
    {
        var plan = PrefixScopePlan(false);
        var batch = new SearchBatch(batchBase, 4097);
        uint[] sparse = [0, 7, 8, 511, 512, 4096];
        using var input = new PrivateOrdinalBuffer(rd);
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "Smoke.PrefixScope.PrivateInput");
        using var executor = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: input,
            useIndirectStaging: false, usePrefixHashCache: true);
        Require(!executor.UsesPrefixHashCache && executor.PrefixHashRevision == NeowFamilyGpuExecutor.LegacyPrefixHashRevision &&
            !executor.SupportsDenseWindowBatching, "Noncontiguous private input incorrectly enabled the expanded Dense cache.");
        var actual = executor.ExecutePrivate(batch, sparse.Length, CancellationToken.None, out var metrics);
        Require(actual.ExportAbi1().Span.SequenceEqual(PrefixScopeExpected(plan, batch, sparse.Select(i => (ulong)i))) &&
            metrics.Processed == sparse.Length && metrics.Dispatches == 1,
            "Private compact input changed while expanding the Dense cache.");
        Emit(new { phase = "prefixCachePrivateInputGuard", result = "PASS", cacheEnabled = executor.UsesPrefixHashCache,
            revision = executor.PrefixHashRevision, noncontiguousInput = true });
    }
}
