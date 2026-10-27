using System.Reflection;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static void TestGroupOutput(RenderingDevice rd)
    {
        TestGroupOutputReservations();
        TestGroupOutputScopeIdentity();
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        SearchBatch[] batches = [new(0, 262144), new(0, 0), new(0, 1), new(1, 7),
            new(33, 8), new(zzfz, 1031), new(zzfz + 1, 511), new(zzfz + 7, 512),
            new(zzfz + 9, 513), new(PrefixHashCache.SpaceSize - 513, 513),
            new(PrefixHashCache.SpaceSize - 1, 1), new(PrefixHashCache.SpaceSize, 0)];
        foreach (bool privateOutput in new[] { false, true })
        {
            TestGroupOutputPublic(rd, "kaleidoscopeCapsule.authoredEither", InternalOrderPlan(authored: true),
                batches, privateOutput, internalOracle: true);
            TestGroupOutputPublic(rd, "newLeafCapsule.authoredEither", InternalOrderPlan(19, 28, authored: true),
                batches, privateOutput, internalOracle: true);
            TestGroupOutputPublic(rd, "ordinaryNoLeafy", PrefixScopePlan(false),
                batches.Where(b => b.BatchCandidateCount != 262144).Prepend(new SearchBatch(0, 32768)).ToArray(),
                privateOutput, internalOracle: false);
            TestGroupOutputPublic(rd, "ordinaryLeafy", PrefixScopePlan(true),
                batches.Where(b => b.BatchCandidateCount != 262144).Prepend(new SearchBatch(0, 32768)).ToArray(),
                privateOutput, internalOracle: false);
        }
        TestGroupOutputPrivateFallback(rd);
        TestGroupOutputOverflow(rd, false);
        TestGroupOutputOverflow(rd, true);
        Emit(new { phase = "groupOutput", result = "PASS", realProductionExecutors = true,
            independentCpuOracle = true, nonzeroOutputs = true, exactStage0Payload = true,
            exactSuccessfulRewardsCheckpoint = true, fullAndPartialWorkgroups = true,
            zzfzAndFinalSeed = true, publicAndPrivateOutput = true,
            compactAndPrivateInputFallback = true, actualOverflowRejected = true,
            cancellationAndReuse = true, boundedSharedBytesPerWorkgroup = 6152 });
    }

    private static void TestGroupOutputScopeIdentity()
    {
        var plan = InternalOrderPlan(authored: true);
        Require(NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, true) ==
            NeowFamilyGpuExecutor.GroupOutputRevision + ".Requested",
            "Group output planning inherited the observed performance identity.");
        Require(NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, true, true) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, false) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, true, privateInput: true) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, true, capsuleComposite: true) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, true, partyPacking: true) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, true, bonesKMode: 1) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan, false, true, capsulePhysicalMode: 1) is null &&
            NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(plan with { JointTransformMetaOffset = 0 }, false, true) is null,
            "Group output requested identity escaped its proven transport scope.");
        Emit(new { phase = "groupOutputScopeIdentity", result = "PASS", requestedSeparatedFromObserved = true,
            compactPrivateCompositePartyBonesAndJointExcluded = true });
    }

    private static void TestGroupOutputReservations()
    {
        const string source = "#define RT2_N_GROUP_OUTPUT 0\n";
        Require(NeowFamilyGpuExecutor.ApplyGroupOutput(source, 0, false) == source &&
            NeowFamilyGpuExecutor.ApplyGroupOutput(source, 1, true) == source &&
            NeowFamilyGpuExecutor.ApplyGroupOutput(source, 0, true).Contains("RT2_N_GROUP_OUTPUT 1") &&
            NeowFamilyGpuExecutor.ApplyGroupOutput(source, 4, true).Contains("RT2_N_GROUP_OUTPUT 1"),
            "Group output source controls changed off/non-output stages.");
        Expect<InvalidOperationException>(() => NeowFamilyGpuExecutor.ApplyGroupOutput("missing", 0, true));
        Expect<InvalidOperationException>(() => NeowFamilyGpuExecutor.ApplyGroupOutput(source + source, 0, true));
        // A group has exactly 64 * at most 8 successful inputs. Reserving all
        // successes preserves the original overflow count; only bounded slots
        // are written, and each reservation interval is disjoint.
        foreach (uint capacity in new uint[] { 0, 1, 511, 512, 513, NeowFamilyGpuPlan.CurseCapacity })
        foreach (uint first in new uint[] { 0, 1, capacity == 0 ? 0 : capacity - 1, capacity, capacity + 1 })
        foreach (uint count in new uint[] { 0, 1, 7, 8, 63, 64, 511, 512 })
        {
            ulong end = (ulong)first + count;
            bool shaderOverflow = count != 0 && (first > capacity || count > unchecked(capacity - first));
            Require(shaderOverflow == (count != 0 && end > capacity), "Group reservation overflow predicate changed.");
            uint expectedWrites = (uint)(Math.Min(end, capacity) - Math.Min((ulong)first, capacity));
            uint writes = 0;
            for (uint lane = 0; lane < 64; lane++)
            for (uint index = lane; index < count; index += 64)
                if ((ulong)first + index < capacity) writes++;
            Require(writes == expectedWrites && end <= uint.MaxValue,
                "Group reservation wrapped or cooperative copy missed bounded records.");
        }
        Require(1L + 3L * NeowFamilyGpuPlan.CurseCapacity <= NeowFamilyGpuExecutor.Capacity,
            "Stage 4 group scratch exceeds the existing output buffer.");
        Emit(new { phase = "groupOutputReservations", result = "PASS", maximumSurvivorsPerGroup = 512,
            emptyGroupSkipsAtomic = true, overflowCounterPreserved = true, noScratchAllocation = true });
    }

    private static ulong[] GroupOutputCurseExpected(NeowFamilyGpuPlan plan, SearchBatch batch, bool internalOracle)
    {
        if (!internalOracle) return PrefixScopeExpected(plan, batch);
        var accepted = new List<ulong>();
        ulong salt = InternalOrderU64(plan.Meta, 31);
        for (int i = 0; i < batch.BatchCandidateCount; i++)
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + (ulong)i), 0);
            var rng = new Beta110FastRng(unchecked(root + salt));
            int curse = rng.NextInt((int)plan.Meta[39]);
            if ((plan.Meta[69] & (1u << curse)) == 0 && plan.Meta[71 + curse] != uint.MaxValue)
                accepted.Add((ulong)i);
        }
        return accepted.ToArray();
    }

    private static void CheckGroupOutputCursePayload(RenderingDevice rd, NeowFamilyGpuExecutor executor,
        SearchBatch batch, ulong[] expected, uint count)
    {
        Require(count == expected.Length, "Group output changed the original stage 0 population.");
        var field = typeof(NeowFamilyGpuExecutor).GetField("_stagedBuffers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Group output stage buffer inspection seam changed.");
        Rid[] buffers = (Rid[])field.GetValue(executor)!;
        uint[] records = count == 0 ? [] : FamilyGpuComputeUtility.FromUInt32Bytes(
            rd.BufferGetData(buffers[11], 0, count * 12), count * 3);
        var ordinals = new List<ulong>();
        for (int at = 0; at < records.Length; at += 3)
        {
            uint ordinal = records[at];
            Require(ordinal < batch.BatchCandidateCount, "A group output ordinal escaped its input batch.");
            ulong root = records[at + 1] | (ulong)records[at + 2] << 32;
            Require(root == XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0),
                "Group output changed an exact retained root or ordinal.");
            ordinals.Add(ordinal);
        }
        Require(ordinals.Order().SequenceEqual(expected), "Group output omitted or duplicated a stage 0 record.");
    }

    private static void TestGroupOutputPublic(RenderingDevice rd, string name, NeowFamilyGpuPlan plan,
        IReadOnlyList<SearchBatch> batches, bool privateOutput, bool internalOracle)
    {
        using var oldOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var newOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var control = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: oldOutput,
            useIndirectStaging: false, useInternalFilterOrder: internalOracle, useGroupOutput: false);
        using var optimized = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: newOutput,
            useIndirectStaging: false, useInternalFilterOrder: internalOracle, useGroupOutput: true);
        Require(!control.GroupOutputEnabledFor(false) && optimized.GroupOutputEnabledFor(false) &&
            !optimized.GroupOutputEnabledFor(true), "Group output requested/actual staged identity changed.");
        if (!privateOutput)
            Require(!optimized.GroupOutputObservedFor(false) && optimized.GroupOutputRevisionFor(false) is null,
                "An uncompiled public owner claimed the observed group-output identity.");
        int nonzero = 0; long totalOutputs = 0;
        ulong[] Expected(SearchBatch b, IEnumerable<ulong>? ordinals = null) => internalOracle
            ? InternalOrderExpected(plan, b, ordinals) : PrefixScopeExpected(plan, b, ordinals);
        foreach (SearchBatch batch in batches)
        {
            var input = FamilyCandidateSet.Dense(batch);
            ulong[] expected = Expected(batch);
            var a = RunPrefixScope(control, oldOutput, input, CancellationToken.None, out var am);
            var b = RunPrefixScope(optimized, newOutput, input, CancellationToken.None, out var bm);
            if (batch.BatchCandidateCount > 0)
                Require(control.GroupOutputObservedFor(false) && optimized.GroupOutputObservedFor(false) &&
                    control.GroupOutputRevisionFor(false) is null &&
                    optimized.GroupOutputRevisionFor(false) == NeowFamilyGpuExecutor.GroupOutputRevision,
                    "Compiled group output did not replace the requested identity with the actual result.");
            Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected),
                "Group output disagreed with an independent CPU oracle: " + name);
            Require(am.Processed == bm.Processed && am.Survivors == bm.Survivors &&
                am.CurseSurvivors == bm.CurseSurvivors && am.PairSurvivors == bm.PairSurvivors &&
                am.Dispatches == bm.Dispatches && am.ReadbackBytes == bm.ReadbackBytes &&
                control.ResidentBytes == optimized.ResidentBytes &&
                PrefixScopeHeader(rd, control).AsSpan().SequenceEqual(PrefixScopeHeader(rd, optimized)),
                "Group output changed stage populations, headers, transport, allocation or dispatch count: " + name);
            if (batch.BatchCandidateCount > 0)
            {
                ulong[] curse = GroupOutputCurseExpected(plan, batch, internalOracle);
                CheckGroupOutputCursePayload(rd, control, batch, curse, am.CurseSurvivors);
                CheckGroupOutputCursePayload(rd, optimized, batch, curse, bm.CurseSurvivors);
                if (internalOracle)
                {
                    CheckInternalOrderPairCheckpoints(rd, control, plan, batch, am.PairSurvivors);
                    CheckInternalOrderPairCheckpoints(rd, optimized, plan, batch, bm.PairSurvivors);
                }
                nonzero += expected.Length > 0 ? 1 : 0;
                totalOutputs += expected.Length;
                if (privateOutput) continue;
                ulong[] sparse = new ulong[] { 0, (ulong)batch.BatchCandidateCount - 1, (ulong)Math.Min(7, batch.BatchCandidateCount - 1) }
                    .Concat(expected.Take(5)).Distinct().Order().ToArray();
                var compact = FamilyCandidateSet.FromSortedAbi1(batch, sparse);
                var ac = control.Execute(compact, CancellationToken.None, out var acm);
                var bc = optimized.Execute(compact, CancellationToken.None, out var bcm);
                Require(ac.ExportAbi1().Span.SequenceEqual(Expected(batch, sparse)) &&
                    ac.ExportAbi1().Span.SequenceEqual(bc.ExportAbi1().Span) && acm.Dispatches == 1 && bcm.Dispatches == 1,
                    "Group output changed compact fallback semantics or dispatches: " + name);
            }
        }
        Require(nonzero > 0 && totalOutputs > 0, "Group output fixture only produced empty payloads: " + name);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var retry = FamilyCandidateSet.Dense(batches[0]);
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => RunPrefixScope(optimized, newOutput, retry, cancelled.Token, out _));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks &&
            RunPrefixScope(optimized, newOutput, retry, CancellationToken.None, out _).ExportAbi1().Span.SequenceEqual(Expected(retry.Batch)),
            "Cancelled group output submitted work or corrupted subsequent reuse: " + name);
        if (privateOutput)
        {
            uint[] header = PrefixScopeHeader(rd, optimized);
            Require(TestExpandedPrefixHeaderValidation(optimized, retry, header) > 0,
                "Group output bypassed native header validation.");
        }
        Emit(new { phase = "groupOutputPublic", result = "PASS", fixture = name, privateOutput,
            cases = batches.Count, nonzeroBatches = nonzero, totalOutputs, internalOrder = internalOracle,
            sharedCompactionActuallyEnabled = optimized.GroupOutputEnabledFor(false),
            originalStage0Population = true, exactRewardsCheckpoints = internalOracle });
    }

    private static void TestGroupOutputPrivateFallback(RenderingDevice rd)
    {
        var plan = InternalOrderPlan(authored: true);
        var batch = new SearchBatch(0, 262144);
        ulong[] expected = InternalOrderExpected(plan, batch);
        Require(expected.Length > 0, "Group private fallback fixture has no successful candidates.");
        uint[] sparse = expected.Take(5).Concat(new ulong[] { 0, 7, (ulong)batch.BatchCandidateCount - 1 })
            .Distinct().Reverse().Select(i => (uint)i).ToArray();
        using var input = new PrivateOrdinalBuffer(rd);
        using var output = new PrivateOrdinalBuffer(rd);
        using var executor = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: input, privateOutput: output,
            useIndirectStaging: false, useInternalFilterOrder: true, useGroupOutput: true);
        Require(!executor.GroupOutputEnabledFor(false), "Group output was enabled on a fused private input.");
        FamilyCandidateSet Run(int count, CancellationToken token)
        {
            int survivors = executor.ExecutePrivateStage(batch, count, token, out var m);
            Require(m.Dispatches == 1, "Private fallback gained a group-output dispatch.");
            return output.ReadForVerification(batch, survivors);
        }
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "GroupOutput.PrivateInput");
        Require(Run(sparse.Length, CancellationToken.None).ExportAbi1().Span.SequenceEqual(
            InternalOrderExpected(plan, batch, sparse.Select(i => (ulong)i))), "Group private fallback changed successful payloads.");
        FamilyGpuComputeUtility.Update(rd, input.Buffer, [0u, (uint)batch.BatchCandidateCount, 7u], "GroupOutput.BadInput");
        Expect<InvalidDataException>(() => Run(3, CancellationToken.None));
        uint[] good = expected.Take(3).Select(i => (uint)i).ToArray();
        FamilyGpuComputeUtility.Update(rd, input.Buffer, good, "GroupOutput.PrivateReuse");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => Run(good.Length, cancelled.Token));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks &&
            Run(good.Length, CancellationToken.None).ExportAbi1().Span.SequenceEqual(good.Select(i => (ulong)i).ToArray()),
            "Group private fallback failed invalid-input/cancellation reuse.");
        Emit(new { phase = "groupOutputPrivateFallback", result = "PASS", successfulPayload = good.Length,
            unsortedInput = true, invalidInputRejected = true, cancellationAndReuse = true });
    }

    private static void TestGroupOutputOverflow(RenderingDevice rd, bool pair)
    {
        var plan = Plan(false, pairOverflow: pair, curseOverflow: !pair);
        int count = (pair ? NeowFamilyGpuPlan.PairCapacity : NeowFamilyGpuPlan.CurseCapacity) + 1;
        using var executor = new NeowFamilyGpuExecutor(rd, plan, 42,
            useIndirectStaging: false, useInternalFilterOrder: false, useGroupOutput: true);
        Expect<InvalidDataException>(() => executor.Execute(FamilyCandidateSet.Dense(new SearchBatch(0, count)), CancellationToken.None, out _));
        uint[] failed = PrefixScopeHeader(rd, executor);
        Require(failed[3] != 0 && failed[pair ? 7 : 6] > (pair ? NeowFamilyGpuPlan.PairCapacity : NeowFamilyGpuPlan.CurseCapacity),
            "Group overflow lost the checked counter/error header.");
        var reuse = FamilyCandidateSet.Dense(new SearchBatch(0, 513));
        executor.Execute(reuse, CancellationToken.None, out var m);
        uint[] recovered = PrefixScopeHeader(rd, executor);
        Require(recovered[3] == 0 && m.Processed == reuse.Count && m.CurseSurvivors == reuse.Count,
            "Group overflow left a stale shared/global counter in the next partial batch.");
        Emit(new { phase = "groupOutputOverflow", result = "PASS", checkpoint = pair ? "Pair" : "Curse", count,
            nativeErrorHeader = true, safeReuse = true });
    }
}
