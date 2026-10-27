using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static void TestSharedNeowPrefixCache(RenderingDevice rd)
    {
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        SearchBatch[] batches = [new(0, 32768), new(zzfz, 1031), new(zzfz + 7, 513),
            new(PrefixHashCache.SpaceSize - 513, 513), new(PrefixHashCache.SpaceSize, 0)];
        foreach (bool leafy in new[] { false, true })
        {
            var plan = SharedNeowFusedPlan(leafy);
            TestSharedNeowPublic(rd, "solo." + leafy, plan, null, null, batches,
                (batch, ordinals) => SharedNeowExpected(plan, batch, ordinals), 11);
            TestSharedNeowPrivate(rd, plan, batches, privateOutput: false);
            TestSharedNeowPrivate(rd, plan, batches, privateOutput: true);
        }
        var party = new[] { SharedNeowPartyPlan(0), SharedNeowPartyPlan(1) };
        TestSharedNeowPublic(rd, "party.twoPlans", party[0], null, party, batches,
            (batch, ordinals) => SharedNeowPartyExpected(party, batch, ordinals), 11);
        var capsulePlan = SharedNeowFusedPlan(false);
        capsulePlan.Meta[56] = 0; capsulePlan.Meta[81] = 28;
        var capsule = new NeowCapsuleComposite([1, 28, 1, 0, 0, 1, 0, 0, 0, 1], null!, null!);
        TestSharedNeowPublic(rd, "composite.directCapsule", capsulePlan, capsule, null, batches,
            (batch, ordinals) => SharedNeowCapsuleExpected(capsulePlan, batch, ordinals), 12);
        TestSharedNeowCheckpointBinding(rd, zzfz);
        TestSharedNeowStage0Isolation(rd, zzfz);
        Emit(new { phase = "sharedNeowPrefixCache", result = "PASS", stagedCacheUnchanged = true,
            nonStagedDenseAndCompact = true, privateInputAndOutput = true, packedParty = true,
            compositeProjectionPreserved = true, checkpointBinding13Preserved = true });
    }

    private static NeowFamilyGpuPlan SharedNeowFusedPlan(bool leafy)
    {
        var plan = PrefixScopePlan(leafy); plan.Meta[66] = 0;
        return plan;
    }
    private static NeowFamilyGpuPlan SharedNeowPartyPlan(uint slot)
    {
        var plan = SharedNeowFusedPlan(false);
        plan.Meta[5] = slot; plan.Meta[6] = 2; plan.Meta[56] = 0; plan.Meta[82] = 0;
        return plan;
    }
    private static IEnumerable<ulong> SharedNeowOrdinals(SearchBatch batch, IEnumerable<ulong>? ordinals) =>
        ordinals ?? Enumerable.Range(0, batch.BatchCandidateCount).Select(i => (ulong)i);

    private static ulong[] SharedNeowExpected(NeowFamilyGpuPlan plan, SearchBatch batch, IEnumerable<ulong>? ordinals = null)
    {
        var all = PrefixScopeExpected(plan, batch);
        return ordinals is null ? all : all.Intersect(ordinals).Order().ToArray();
    }
    private static ulong[] SharedNeowPartyExpected(NeowFamilyGpuPlan[] plans, SearchBatch batch, IEnumerable<ulong>? ordinals)
    {
        ulong salt = XxHash64.HashUtf8("NEOW", 0);
        return SharedNeowOrdinals(batch, ordinals).Where(ordinal =>
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            foreach (var plan in plans)
            {
                var rng = new Beta110FastRng(unchecked(root + salt + plan.Meta[5]));
                if (rng.NextInt((int)plan.Meta[39]) != 0) return false;
            }
            return true;
        }).ToArray();
    }
    private static ulong[] SharedNeowCapsuleExpected(NeowFamilyGpuPlan plan, SearchBatch batch, IEnumerable<ulong>? ordinals)
    {
        return SharedNeowOrdinals(batch, ordinals).Where(ordinal =>
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            var curse = new Beta110FastRng(unchecked(root + XxHash64.HashUtf8("NEOW", 0)));
            if (curse.NextInt((int)plan.Meta[39]) != 0) return false;
            var rewards = new Beta110FastRng(unchecked(root + XxHash64.HashUtf8("Rewards", 0)));
            return rewards.NextFloat() < 0.5f;
        }).ToArray();
    }

    private static ulong[] SharedNeowSubset(SearchBatch batch, ulong[] expected) => batch.BatchCandidateCount == 0 ? [] :
        new ulong[] { 0, (ulong)Math.Min(7, batch.BatchCandidateCount - 1), (ulong)Math.Min(511, batch.BatchCandidateCount - 1),
            (ulong)batch.BatchCandidateCount - 1 }.Concat(expected.Take(3)).Distinct().Order().ToArray();

    private static void TestSharedNeowPublic(RenderingDevice rd, string fixture, NeowFamilyGpuPlan plan,
        NeowCapsuleComposite? composite, NeowFamilyGpuPlan[]? partyPlans, IReadOnlyList<SearchBatch> batches,
        Func<SearchBatch, IEnumerable<ulong>?, ulong[]> expectedFor, int binding)
    {
        using var control = new NeowFamilyGpuExecutor(rd, plan, 42, composite: composite, partyPlans: partyPlans,
            useStaticDrawGates: false, useSharedPrefixCache: false);
        using var cached = new NeowFamilyGpuExecutor(rd, plan, 42, composite: composite, partyPlans: partyPlans,
            useStaticDrawGates: false, useSharedPrefixCache: true);
        int nonzero = 0;
        foreach (SearchBatch batch in batches)
        {
            ulong[] full = expectedFor(batch, null), subset = SharedNeowSubset(batch, full);
            foreach (bool compact in new[] { false, true })
            {
                var input = compact ? FamilyCandidateSet.FromSortedAbi1(batch, subset) : FamilyCandidateSet.Dense(batch);
                ulong[] expected = compact ? expectedFor(batch, subset) : full;
                var a = control.Execute(input, CancellationToken.None, out var am);
                var b = cached.Execute(input, CancellationToken.None, out var bm);
                Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected),
                    "Shared Neow root/ordinal population mismatch: " + fixture);
                Require(am.Processed == bm.Processed && am.Survivors == bm.Survivors && am.Dispatches == bm.Dispatches &&
                    am.ReadbackBytes == bm.ReadbackBytes && PrefixScopeHeader(rd, control).AsSpan().SequenceEqual(PrefixScopeHeader(rd, cached)),
                    "Shared Neow header/transport mismatch: " + fixture);
                nonzero += expected.Length > 0 ? 1 : 0;
            }
        }
        Require(nonzero > 0 && !control.UsesSharedPrefixHashCache && cached.UsesSharedPrefixHashCache &&
            cached.SharedPrefixHashBinding == binding && cached.ResidentBytes == control.ResidentBytes + SharedVisibleSeedGpuCache.Bytes &&
            cached.SharedPrefixHashRevisionFor(false) == SharedVisibleSeedGpuCache.Revision &&
            cached.SharedPrefixHashRevisionFor(true) == SharedVisibleSeedGpuCache.Revision,
            "Shared Neow test missed a nonzero output or the actual module/binding identity: " + fixture);
        Emit(new { phase = "sharedNeowPublicParity", result = "PASS", fixture, nonzeroBatches = nonzero,
            denseAndCompact = true, binding = cached.SharedPrefixHashBinding, tableBytes = SharedVisibleSeedGpuCache.Bytes });
    }

    private static void TestSharedNeowPrivate(RenderingDevice rd, NeowFamilyGpuPlan plan,
        IReadOnlyList<SearchBatch> batches, bool privateOutput)
    {
        using var inputBuffer = new PrivateOrdinalBuffer(rd);
        using var oldOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var newOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var control = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: inputBuffer, privateOutput: oldOutput,
            useStaticDrawGates: false, useSharedPrefixCache: false);
        using var cached = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: inputBuffer, privateOutput: newOutput,
            useStaticDrawGates: false, useSharedPrefixCache: true);
        FamilyCandidateSet Run(NeowFamilyGpuExecutor executor, PrivateOrdinalBuffer? output, SearchBatch batch, int count,
            CancellationToken token, out NeowFamilyGpuMetrics metrics)
        {
            if (output is null) return executor.ExecutePrivate(batch, count, token, out metrics);
            int survivors = executor.ExecutePrivateStage(batch, count, token, out metrics);
            return output.ReadForVerification(batch, survivors);
        }
        int nonzero = 0;
        foreach (SearchBatch batch in batches)
        {
            ulong[] full = SharedNeowExpected(plan, batch), subset = SharedNeowSubset(batch, full);
            FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, subset.Select(x => (uint)x).ToArray(), "SharedNeow.PrivateInput");
            var a = Run(control, oldOutput, batch, subset.Length, CancellationToken.None, out var am);
            var b = Run(cached, newOutput, batch, subset.Length, CancellationToken.None, out var bm);
            ulong[] expected = SharedNeowExpected(plan, batch, subset);
            Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected) &&
                am.Processed == subset.Length && am.Processed == bm.Processed && am.Survivors == bm.Survivors &&
                am.ReadbackBytes == bm.ReadbackBytes && PrefixScopeHeader(rd, control).AsSpan().SequenceEqual(PrefixScopeHeader(rd, cached)),
                "Shared private Neow changed noncontiguous logical ordinals or headers.");
            nonzero += expected.Length > 0 ? 1 : 0;
        }
        var badBatch = batches.First(b => b.BatchCandidateCount == 1031);
        FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, [0u, (uint)badBatch.BatchCandidateCount, 7u], "SharedNeow.BadInput");
        Expect<InvalidDataException>(() => Run(control, oldOutput, badBatch, 3, CancellationToken.None, out _));
        Expect<InvalidDataException>(() => Run(cached, newOutput, badBatch, 3, CancellationToken.None, out _));
        uint[] valid = [0, 7, 8, 511];
        FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, valid, "SharedNeow.Reuse");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => Run(cached, newOutput, badBatch, valid.Length, cancelled.Token, out _));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, "Cancelled shared Neow submitted work.");
        Require(Run(cached, newOutput, badBatch, valid.Length, CancellationToken.None, out _).ExportAbi1().Span
            .SequenceEqual(SharedNeowExpected(plan, badBatch, valid.Select(x => (ulong)x))) && nonzero > 0 &&
            cached.SharedPrefixHashRevisionFor(true) == SharedVisibleSeedGpuCache.Revision,
            "Shared Neow failed reuse after invalid-ordinal rejection/cancellation.");
        Emit(new { phase = "sharedNeowPrivateParity", result = "PASS", leafy = plan.UsesLeafyPreGate,
            privateOutput, nonzeroBatches = nonzero, invalidOrdinalRejected = true, cancelledAndReused = true });
    }

    private static void TestSharedNeowCheckpointBinding(RenderingDevice rd, ulong batchBase)
    {
        using var input = new PrivateOrdinalBuffer(rd);
        var plan = SharedNeowFusedPlan(false);
        using var cached = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: input, bonesKMode: 2,
            bonesKMetadata: new uint[256], useStaticDrawGates: false, useSharedPrefixCache: true);
        // Compile the real projection/checkpoint module, but do not evaluate an
        // invented BonesK query. Empty input is enough to check the binding ABI.
        var result = cached.ExecutePrivate(new SearchBatch(batchBase, 0), 0, CancellationToken.None, out var metrics);
        Require(cached.UsesSharedPrefixHashCache && cached.SharedPrefixHashBinding == 14 && result.Count == 0 &&
            metrics.Dispatches == 0 && metrics.ReadbackBytes == 64, "Shared cache collided with checkpoint projection binding13.");
        Emit(new { phase = "sharedNeowCheckpointBinding", result = "PASS", projectionBinding = 13,
            prefixBinding = cached.SharedPrefixHashBinding, syntheticProjectionExecuted = false });
    }

    private static void TestSharedNeowStage0Isolation(RenderingDevice rd, ulong batchBase)
    {
        var plan = PrefixScopePlan(false);
        using var control = new NeowFamilyGpuExecutor(rd, plan, 42, useSharedPrefixCache: false);
        using var cached = new NeowFamilyGpuExecutor(rd, plan, 42, useSharedPrefixCache: true);
        var batch = new SearchBatch(batchBase, 1031);
        var dense = FamilyCandidateSet.Dense(batch);
        var a = control.Execute(dense, CancellationToken.None, out _);
        var b = cached.Execute(dense, CancellationToken.None, out _);
        Require(a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span) && !cached.UsesSharedPrefixHashCache &&
            cached.ResidentBytes == control.ResidentBytes && cached.SharedPrefixHashRevisionFor(false) is null,
            "Shared stage3 cache changed the original Dense stage0 workspace.");
        ulong[] subset = SharedNeowSubset(batch, PrefixScopeExpected(plan, batch));
        var compact = FamilyCandidateSet.FromSortedAbi1(batch, subset);
        a = control.Execute(compact, CancellationToken.None, out _); b = cached.Execute(compact, CancellationToken.None, out _);
        Require(a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span) && cached.UsesSharedPrefixHashCache &&
            cached.SharedPrefixHashRevisionFor(true) == SharedVisibleSeedGpuCache.Revision &&
            cached.SharedPrefixHashRevisionFor(false) is null, "Shared cache lost Compact versus Dense module identity.");
        Require(cached.Execute(dense, CancellationToken.None, out _).ExportAbi1().Span.SequenceEqual(PrefixScopeExpected(plan, batch)),
            "Returning from shared Compact to original Dense broke prefix-table refresh.");
        Emit(new { phase = "sharedNeowStage0Isolation", result = "PASS", compactThenDenseReuse = true });
    }
}
