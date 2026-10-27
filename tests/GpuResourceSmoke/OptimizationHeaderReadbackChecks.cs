#if RT2_SHARED_PREFIX_TESTS
using Godot;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static T HeaderMode<T>(bool queued, Func<T> create)
    {
        using var scope = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled with { AsyncHeaderReadback = queued });
        return create();
    }

    private static string HeaderRevision(object executor) => executor switch
    {
        CombatRewardGpuExecutor value => value.HeaderReadbackRevision,
        EventResultFamilyGpuExecutor value => value.HeaderReadbackRevision,
        AncientOptionFamilyGpuExecutor value => value.HeaderReadbackRevision,
        TransformationAggregateGpuExecutor value => value.HeaderReadbackRevision,
        WorldFamilyGpuExecutor value => value.HeaderReadbackRevision,
        EwPrivateGpuExecutor value => value.HeaderReadbackRevision,
        RelicFamilyGpuExecutor value => value.HeaderReadbackRevision,
        MerchantShopColorlessGpuExecutor value => value.HeaderReadbackRevision,
        _ => throw new ArgumentException("Unknown header executor.")
    };

    private static void HeaderSelected(object control, object queued)
    {
        Require(HeaderRevision(control) == FamilyGpuHeaderReadback.SynchronousRevision &&
            HeaderRevision(queued) == FamilyGpuHeaderReadback.QueuedRevision,
            "Actual header transport did not retain its constructor-time selection.");
    }

    private static void TestOptimizationHeaderReadback(RenderingDevice rd)
    {
        string? priorSync = System.Environment.GetEnvironmentVariable("RT2_NEOW_SYNC_HEADER");
        string? priorPrefix = System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH");
        try
        {
            System.Environment.SetEnvironmentVariable("RT2_NEOW_SYNC_HEADER", null);
            Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
            HeaderOtherFamilies(rd, zzfz);
            HeaderWorldAndEw(rd, zzfz);
            HeaderRelicAndShop(rd, zzfz);
            Emit(new { phase = "optimizationHeaderReadback", result = "PASS",
                families = new[] { "C", "A", "E", "T", "W", "EW", "R", "S.Atomic", "S.Stable" },
                actualTransportSelected = true, constructorSnapshotRetained = true,
                wholePopulationsAndNativeCounters = true, denseCompactAndPrivate = true,
                noAddedDispatchOrSync = true, limits = "SyntheticNumericalPlans;NotAllGameplayPlanAdmissionPaths" });
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("RT2_NEOW_SYNC_HEADER", priorSync);
            System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", priorPrefix);
        }
    }

    private static void HeaderOtherFamilies(RenderingDevice rd, ulong zzfz)
    {
        foreach (string family in new[] { "C", "E", "A", "T", "Tfull" })
        {
            using (var a = HeaderMode(false, () => SharedOtherCreate(rd, family, true)))
            using (var b = HeaderMode(true, () => SharedOtherCreate(rd, family, true)))
            {
                foreach (SearchBatch batch in new[] { new SearchBatch(0, 0), new SearchBatch(0, 1),
                    new SearchBatch(zzfz, 1537), new SearchBatch(PrefixHashCache.SpaceSize - 513, 513) })
                foreach (bool compact in new[] { false, true })
                {
                    var input = compact ? SharedOtherSparse(family, batch) : FamilyCandidateSet.Dense(batch);
                    var ar = a.Run(input, input.Count, CancellationToken.None);
                    var br = b.Run(input, input.Count, CancellationToken.None);
                    SharedOtherCompare(rd, family, a, b, input, ar, br, oracle: true);
                    if (input.Count > 0) HeaderSelected(a.Native, b.Native);
                }
            }
            // A/E cross three small windows; C/T cross their real 1M window.
            // Private IDs arrive in reverse order, so the transport cannot rely
            // on a public sort or on an accidentally consecutive input range.
            int rootCount = family is "C" or "T" ? (1 << 20) + 17 : 1537;
            var privateBatch = new SearchBatch(zzfz, rootCount);
            uint[] ids = Enumerable.Range(0, rootCount).Reverse().Select(i => (uint)i).ToArray();
            using var inputBuffer = new PrivateOrdinalBuffer(rd);
            using var outputA = new PrivateOrdinalBuffer(rd);
            using var outputB = new PrivateOrdinalBuffer(rd);
            FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, ids, "HeaderMode.PrivateInput");
            foreach (bool privateOutput in new[] { false, true })
            {
                using var a = HeaderMode(false, () => SharedOtherCreate(rd, family, true, inputBuffer, privateOutput ? outputA : null));
                using var b = HeaderMode(true, () => SharedOtherCreate(rd, family, true, inputBuffer, privateOutput ? outputB : null));
                var input = FamilyCandidateSet.Dense(privateBatch);
                var ar = a.Run(input, ids.Length, CancellationToken.None);
                var br = b.Run(input, ids.Length, CancellationToken.None);
                SharedOtherCompare(rd, family, a, b, input, ar, br, oracle: rootCount < 10000);
                HeaderSelected(a.Native, b.Native);
                Require(ar.Survivors > 0 && (!privateOutput || ar.ReadbackBytes == 32 && br.ReadbackBytes == 32),
                    family + " private header was read before the final accumulated window, or fixture was empty.");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
                Expect<OperationCanceledException>(() => b.Run(input, ids.Length, cancelled.Token));
                Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, "Pre-cancelled header path submitted work.");
            }
            Emit(new { phase = "optimizationHeaderOther", result = "PASS", family, privateRootCount = rootCount,
                lastAccumulatedHeaderOnly = true, realWindowBoundaries = true });
        }
    }

    private static void HeaderWorldAndEw(RenderingDevice rd, ulong zzfz)
    {
        foreach (int grouping in new[] { 1, 4 })
        {
            var plan = SharedWorldPlan(); SharedOtherProperty(plan, "GroupingK", grouping);
            using var a = HeaderMode(false, () => SharedWorldCreate(rd, plan, true));
            using var b = HeaderMode(true, () => SharedWorldCreate(rd, plan, true));
            foreach (SearchBatch batch in new[] { new SearchBatch(0, 0), new SearchBatch(zzfz, 1),
                new SearchBatch(zzfz, 1031), new SearchBatch(PrefixHashCache.SpaceSize - 513, 513) })
            foreach (bool compact in new[] { false, true })
            {
                var input = compact ? SharedWorldSparse(batch) : FamilyCandidateSet.Dense(batch);
                var ar = a.Execute(input, CancellationToken.None, out var am);
                var br = b.Execute(input, CancellationToken.None, out var bm);
                SharedWorldCompare(input, ar, br, am, bm, plan);
                if (input.Count > 0) HeaderSelected(a, b);
            }
            var rootBatch = new SearchBatch(zzfz, 4097);
            var sparse = SharedWorldSparse(rootBatch);
            using var source = new PrivateOrdinalBuffer(rd);
            using var outputA = new PrivateOrdinalBuffer(rd);
            using var outputB = new PrivateOrdinalBuffer(rd);
            FamilyGpuComputeUtility.Update(rd, source.Buffer, sparse.ExportAbi1().Span.ToArray().Reverse().Select(i => (uint)i).ToArray(), "HeaderMode.WorldPrivate");
            using var pa = HeaderMode(false, () => SharedWorldCreate(rd, plan, true, source, outputA));
            using var pb = HeaderMode(true, () => SharedWorldCreate(rd, plan, true, source, outputB));
            int ac = pa.ExecutePrivateStage(rootBatch, sparse.Count, CancellationToken.None, out var pam);
            int bc = pb.ExecutePrivateStage(rootBatch, sparse.Count, CancellationToken.None, out var pbm);
            SharedWorldCompare(sparse, outputA.ReadForVerification(rootBatch, ac), outputB.ReadForVerification(rootBatch, bc), pam, pbm, plan, resident: true);
            HeaderSelected(pa, pb);
            Require(ac > 0 && pam.HeaderReadCalls == 1 && pbm.HeaderReadCalls == 1, "Private World did not use one final header.");
        }
        using var ea = HeaderMode(false, () => new EwPrivateGpuExecutor(rd, SharedOtherEventPlan(), SharedWorldPlan(), verify: true));
        using var eb = HeaderMode(true, () => new EwPrivateGpuExecutor(rd, SharedOtherEventPlan(), SharedWorldPlan(), verify: true));
        foreach (int count in new[] { 0, 1, 1031, 4097 })
        {
            var input = FamilyCandidateSet.Dense(new SearchBatch(zzfz, count));
            SharedRsEqual(ea.Execute(input, CancellationToken.None, out _), eb.Execute(input, CancellationToken.None, out _), "EW header transport parity");
            if (count > 0) HeaderSelected(ea, eb);
        }
        Emit(new { phase = "optimizationHeaderWorld", result = "PASS", groupedK1AndK4 = true,
            bankSizes = "8 or activeSlots*8 words", privateFinalOnly = true, ewFullPopulationVerification = true });
    }

    private static void HeaderRelicAndShop(RenderingDevice rd, ulong zzfz)
    {
        var request = SharedRsRequest();
        foreach (bool full in new[] { false, true })
        {
            using var a = HeaderMode(false, () => SharedRsCreateRelic(rd, request, full, true));
            using var b = HeaderMode(true, () => SharedRsCreateRelic(rd, request, full, true));
            foreach (var input in new[] { FamilyCandidateSet.Dense(new SearchBatch(zzfz, 4097)), SharedRsSparse(new(zzfz, 4097)) })
            {
                var ar = a.Execute(input, CancellationToken.None, out var am);
                var br = b.Execute(input, CancellationToken.None, out var bm);
                SharedRsEqual(ar, br, "R header transport parity");
                if (!full) SharedRsOracle(input, br, shop: false);
                SharedRsCounters(rd, a, input.Count, ar.Count); SharedRsCounters(rd, b, input.Count, br.Count);
                Require(am.ReadbackBytes == bm.ReadbackBytes && am.Survivors == bm.Survivors, "R header metrics differ.");
                HeaderSelected(a, b);
            }
        }
        foreach (var path in new[] { MerchantShopColorlessCompactionPath.AtomicAppendHostSort, MerchantShopColorlessCompactionPath.StableOrderedCompaction })
        {
            using var a = HeaderMode(false, () => MerchantShopColorlessGpuExecutor.Create(rd, request, path));
            using var b = HeaderMode(true, () => MerchantShopColorlessGpuExecutor.Create(rd, request, path));
            foreach (var input in new[] { FamilyCandidateSet.Dense(new SearchBatch(zzfz, 4097)), SharedRsSparse(new(zzfz, 4097)) })
            {
                var ar = a.Execute(input, CancellationToken.None, out var am);
                var br = b.Execute(input, CancellationToken.None, out var bm);
                SharedRsEqual(ar, br, "S header transport parity"); SharedRsOracle(input, br, shop: true);
                Require(am.Dispatches == bm.Dispatches && am.HeaderReadbackBytes == bm.HeaderReadbackBytes &&
                    am.Survivors == bm.Survivors, "S header metrics differ.");
                HeaderSelected(a, b);
            }
        }
        // Private R/S terminal and middle ports retain zero production payload downloads.
        var batch = new SearchBatch(zzfz, 4097);
        uint[] ids = SharedRsSparse(batch).ExportAbi1().Span.ToArray().Reverse().Select(i => (uint)i).ToArray();
        using var source = new PrivateOrdinalBuffer(rd);
        using var outputA = new PrivateOrdinalBuffer(rd);
        using var outputB = new PrivateOrdinalBuffer(rd);
        FamilyGpuComputeUtility.Update(rd, source.Buffer, ids, "HeaderMode.RsPrivate");
        using (var a = HeaderMode(false, () => SharedRsCreateRelic(rd, request, false, true, source, outputA)))
        using (var b = HeaderMode(true, () => SharedRsCreateRelic(rd, request, false, true, source, outputB)))
        {
            int ac = a.ExecutePrivateStage(batch, ids.Length, CancellationToken.None, out var am);
            int bc = b.ExecutePrivateStage(batch, ids.Length, CancellationToken.None, out var bm);
            SharedRsEqual(outputA.ReadForVerification(batch, ac), outputB.ReadForVerification(batch, bc), "R private header parity");
            Require(am.ReadbackBytes == 32 && bm.ReadbackBytes == 32, "R private added payload readback."); HeaderSelected(a, b);
        }
        using (var a = HeaderMode(false, () => MerchantShopColorlessGpuExecutor.Create(rd, request, MerchantShopColorlessCompactionPath.AtomicAppendHostSort, source, outputA)))
        using (var b = HeaderMode(true, () => MerchantShopColorlessGpuExecutor.Create(rd, request, MerchantShopColorlessCompactionPath.AtomicAppendHostSort, source, outputB)))
        {
            int ac = a.ExecutePrivateStage(batch, ids.Length, CancellationToken.None, out var am);
            int bc = b.ExecutePrivateStage(batch, ids.Length, CancellationToken.None, out var bm);
            SharedRsEqual(outputA.ReadForVerification(batch, ac), outputB.ReadForVerification(batch, bc), "S private header parity");
            Require(am.HeaderReadbackBytes == 32 && bm.HeaderReadbackBytes == 32 && am.PayloadReadbackBytes == 0 && bm.PayloadReadbackBytes == 0,
                "S private added payload readback."); HeaderSelected(a, b);
        }
        Emit(new { phase = "optimizationHeaderRs", result = "PASS", rfull = true, stableTwoSubmissions = true,
            denseCompactAndPrivate = true, actualRevisionSelected = true });
    }
}
#endif
