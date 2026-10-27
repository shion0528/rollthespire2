using System.Reflection;
using Godot;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // No replacement predicate/shader: these fixtures invoke the real executor,
    // its native headers, payload decoding, windows, private ports and cleanup.
    // The only synthetic inputs are immutable authority and numerical plans.
    private static void TestSharedRelicShopPrefixCache(RenderingDevice rd)
    {
        string? prior = System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH");
        try
        {
            Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
            var request = SharedRsRequest();
            SearchBatch[] batches = [new(0, 0), new(0, 1), new(1, 7), new(33, 8), new(zzfz, 1031),
                new(0x100000003UL, 4097), new(PrefixHashCache.SpaceSize - 513, 513),
                new(PrefixHashCache.SpaceSize - 1, 1), new(PrefixHashCache.SpaceSize, 0)];
            foreach (bool full in new[] { false, true })
            {
                TestSharedRelicPublic(rd, request, full, batches, zzfz);
                TestSharedRelicPrivate(rd, request, full, zzfz);
            }
            var wide = FamilyCandidateSet.Dense(new SearchBatch(zzfz, MerchantShopColorlessGpuExecutor.SurvivorCapacity + 17));
            ulong[] wideExpected = SharedRsExpected(wide, shop: true);
            foreach (var path in new[] { MerchantShopColorlessCompactionPath.AtomicAppendHostSort,
                MerchantShopColorlessCompactionPath.StableOrderedCompaction })
                TestSharedShopPublic(rd, request, path, batches, zzfz, wide, wideExpected);
            TestSharedShopPrivate(rd, request, zzfz);
            TestSharedRsSourceSeams();
            Emit(new { phase = "sharedRelicShopPrefixCache", result = "PASS", realExecutors = new[] { "R", "RfullDirectSmallCapsule", "SAtomic", "SStable" },
                denseAndSparse = true, privateInputAndOutput = true, badPrivateOrdinalRejected = true,
                preCancelledAndReused = true, nativeCounterValidation = true, zzfzAndFinal12Z = true,
                shopRebasedWindow = MerchantShopColorlessGpuExecutor.SurvivorCapacity + 17,
                fallbackRootRange = 1 << 25, fallbackUsesRealCompactExecutors = true,
                oracle = "R/S:CanonicalVisibleSeedXxHash64AndCpuRng;Rfull:CompleteLegacyProductionOutputParity",
                limits = "SyntheticAuthority;DirectSmallCapsuleRfullOnly;NoClaimOfAllRfullArrivalOrModShapes" });
        }
        finally { System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", prior); }
    }

    private static void SharedRsMode(bool enabled) =>
        System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", enabled ? "1" : "0");

    private static RelicFamilyPlan SharedRsRelicPlan(bool capsule = false)
    {
        int buckets = capsule ? 3 : 1;
        var pool = new RelicFamilyPool(Enumerable.Range(1, buckets * 2).Select(i => (ushort)i).ToArray(),
            Enumerable.Repeat((byte)1, buckets * 2).ToArray(), Enumerable.Range(0, buckets).Select(i => i * 2).ToArray(),
            Enumerable.Repeat(2, buckets).ToArray(), Enumerable.Repeat((byte)1, buckets).ToArray(),
            Enumerable.Range(1, buckets).Select(i => (byte)i).ToArray(), Enumerable.Repeat((byte)1, buckets).ToArray(), 2);
        return capsule
            ? new(pool, [], [], [], new byte[4], new byte[4], [], new int[4], new byte[4], 0, false)
            : new(pool, [new(0, 0, 1, 0, 1, 1, 0, 1, 0)], [0], [], [1, 0, 0, 0], new byte[4],
                [0], [0, 1, 1, 1], [1, 0, 0, 0], 0, false);
    }

    private static RelicFullGpuPlan SharedRsRfullPlan()
    {
        var sequence = SharedRsRelicPlan(capsule: true);
        var meta = new uint[109];
        meta[0] = RelicFullGpuPlan.DirectArrival;
        meta[1] = Beta110FastRelicCatalog.SmallCapsule;
        meta[2] = uint.MaxValue;
        meta[3] = 1; // One target, common lane bucket 0, original rank 0.
        meta[4] = 0; meta[5] = 0; meta[6] = 1;
        for (int i = 7; i <= 12; i++) meta[i] = uint.MaxValue;
        meta[13] = 3; meta[14] = 0; meta[15] = 1; meta[16] = 12;
        meta[17] = 1; meta[32] = 0; // A one-entry real curse identity domain.
        void Salt(int at, ulong value) { meta[at] = (uint)value; meta[at + 1] = (uint)(value >> 32); }
        Salt(18, XxHash64.HashUtf8("NEOW", 0)); Salt(20, NeowFamilyReplay.RewardsHash); Salt(22, XxHash64.HashUtf8("up_front", 0));
        meta[106] = meta[107] = meta[108] = 2;
        return new(sequence, meta);
    }

    private static RelicFamilyGpuExecutor SharedRsCreateRelic(RenderingDevice rd, ExactSearchExecutionRequest request,
        bool full, bool cached, PrivateOrdinalBuffer? input = null, PrivateOrdinalBuffer? output = null)
    {
        SharedRsMode(cached);
        return full ? RelicFamilyGpuExecutor.Create(rd, request, SharedRsRfullPlan(), input, false, output)
            : RelicFamilyGpuExecutor.Create(rd, request, SharedRsRelicPlan(), input, output);
    }

    private static void TestSharedRelicPublic(RenderingDevice rd, ExactSearchExecutionRequest request, bool full,
        IReadOnlyList<SearchBatch> batches, ulong zzfz)
    {
        using var legacy = SharedRsCreateRelic(rd, request, full, false);
        using var cached = SharedRsCreateRelic(rd, request, full, true);
        int cases = 0, nonzero = 0;
        foreach (SearchBatch batch in batches)
        foreach (bool compact in new[] { false, true })
        {
            var input = compact ? SharedRsSparse(batch) : FamilyCandidateSet.Dense(batch);
            var a = legacy.Execute(input, CancellationToken.None, out var am);
            var b = cached.Execute(input, CancellationToken.None, out var bm);
            SharedRsEqual(a, b, "R shared legacy/cached");
            if (!full) SharedRsOracle(input, b, shop: false);
            SharedRsCounters(rd, legacy, input.Count, a.Count);
            SharedRsCounters(rd, cached, input.Count, b.Count);
            Require(am.ProcessedCandidates == input.Count && bm.ProcessedCandidates == input.Count &&
                am.Survivors == a.Count && bm.Survivors == b.Count, "R shared processed/population metrics changed.");
            SharedRsTable(rd, cached, batch, input.Count > 0);
            if (b.Count > 0) nonzero++;
            cases++;
        }
        // Root ranges larger than the table are legal with a small Compact
        // population. This exercises the real executor's disabled-table path.
        var fallback = SharedRsSparse(new(zzfz, 1 << 25));
        var fa = legacy.Execute(fallback, CancellationToken.None, out _);
        var fb = cached.Execute(fallback, CancellationToken.None, out _);
        SharedRsEqual(fa, fb, "R 32M fallback");
        if (!full) SharedRsOracle(fallback, fb, shop: false);
        SharedRsTable(rd, cached, fallback.Batch, populated: true);
        var tail = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 4097));
        SharedRsPreCancel(() => cached.Execute(tail, SharedRsCancelled, out _));
        var reused = cached.Execute(tail, CancellationToken.None, out _);
        SharedRsEqual(legacy.Execute(tail, CancellationToken.None, out _), reused, "R post-cancel reuse");
        Require(nonzero > 0, "R fixture only exercised zero survivors.");
        int rejects = SharedRsRejectHeaders(rd, cached, tail.Count, reused.Count);
        Emit(new { phase = "sharedRelicPublic", result = "PASS", full, cases, nonzeroBatches = nonzero,
            headerRejections = rejects, fallback32M = true, preCancelled = true });
    }

    private static void TestSharedRelicPrivate(RenderingDevice rd, ExactSearchExecutionRequest request, bool full, ulong zzfz)
    {
        var batch = new SearchBatch(zzfz, 4097);
        uint[] sparse = Enumerable.Range(0, batch.BatchCandidateCount).Where(i => i % 3 != 1).Select(i => (uint)i).ToArray();
        using var input = new PrivateOrdinalBuffer(rd);
        using var outputA = new PrivateOrdinalBuffer(rd);
        using var outputB = new PrivateOrdinalBuffer(rd);
        using var legacy = SharedRsCreateRelic(rd, request, full, false, input, outputA);
        using var cached = SharedRsCreateRelic(rd, request, full, true, input, outputB);
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "Smoke.SharedR.PrivateInput");
        int ac = legacy.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out var am);
        int bc = cached.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out var bm);
        var a = outputA.ReadForVerification(batch, ac); var b = outputB.ReadForVerification(batch, bc);
        SharedRsEqual(a, b, "R private shared");
        Require(ac > 0, "R private fixture only exercised zero survivors.");
        if (!full) SharedRsOracle(FamilyCandidateSet.FromSortedAbi1(batch, sparse.Select(i => (ulong)i).ToArray()), b, shop: false);
        Require(am.ReadbackBytes == 32 && bm.ReadbackBytes == 32, "R private cache added a payload readback.");
        SharedRsCounters(rd, cached, sparse.Length, bc);
        SharedRsPreCancel(() => cached.ExecutePrivateStage(batch, sparse.Length, SharedRsCancelled, out _));
        FamilyGpuComputeUtility.Update(rd, input.Buffer, [(uint)batch.BatchCandidateCount], "Smoke.SharedR.InvalidOrdinal");
        Expect<InvalidDataException>(() => cached.ExecutePrivateStage(batch, 1, CancellationToken.None, out _));
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "Smoke.SharedR.PrivateReuse");
        bc = cached.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out _);
        SharedRsEqual(a, outputB.ReadForVerification(batch, bc), "R bad-ordinal recovery");
        using (var denseA = SharedRsCreateRelic(rd, request, full, false, output: outputA))
        using (var denseB = SharedRsCreateRelic(rd, request, full, true, output: outputB))
        {
            ac = denseA.ExecutePrivateStage(batch, batch.BatchCandidateCount, CancellationToken.None, out _);
            bc = denseB.ExecutePrivateStage(batch, batch.BatchCandidateCount, CancellationToken.None, out _);
            var da = outputA.ReadForVerification(batch, ac); var db = outputB.ReadForVerification(batch, bc);
            SharedRsEqual(da, db, "R Dense private output shared");
            if (!full) SharedRsOracle(FamilyCandidateSet.Dense(batch), db, shop: false);
            Require(ac > 0, "R Dense private fixture only exercised zero survivors.");
        }
        // Also exercise the terminal private-input/public-output entry.
        using var terminalA = SharedRsCreateRelic(rd, request, full, false, input);
        using var terminalB = SharedRsCreateRelic(rd, request, full, true, input);
        SharedRsEqual(terminalA.ExecutePrivate(batch, sparse.Length, CancellationToken.None, out _),
            terminalB.ExecutePrivate(batch, sparse.Length, CancellationToken.None, out _), "R terminal private shared");
        Emit(new { phase = "sharedRelicPrivate", result = "PASS", full, sparseInputs = sparse.Length,
            outputCount = ac, terminalPrivateInput = true, badOrdinalRejected = true, preCancelled = true });
    }

    private static void TestSharedShopPublic(RenderingDevice rd, ExactSearchExecutionRequest request,
        MerchantShopColorlessCompactionPath path, IReadOnlyList<SearchBatch> batches, ulong zzfz,
        FamilyCandidateSet wide, ulong[] wideExpected)
    {
        SharedRsMode(false); using var legacy = MerchantShopColorlessGpuExecutor.Create(rd, request, path);
        SharedRsMode(true); using var cached = MerchantShopColorlessGpuExecutor.Create(rd, request, path);
        Require(!legacy.PhysicalImplementationRevision.Contains(SharedVisibleSeedGpuCache.Revision, StringComparison.Ordinal) &&
            cached.PhysicalImplementationRevision.Contains(SharedVisibleSeedGpuCache.Revision, StringComparison.Ordinal), "S actual cache revision was not isolated.");
        int cases = 0, nonzero = 0;
        foreach (SearchBatch batch in batches)
        foreach (bool compact in new[] { false, true })
        {
            var input = compact ? SharedRsSparse(batch) : FamilyCandidateSet.Dense(batch);
            var a = legacy.Execute(input, CancellationToken.None, out var am);
            var b = cached.Execute(input, CancellationToken.None, out var bm);
            SharedRsEqual(a, b, "S shared legacy/cached"); SharedRsOracle(input, b, shop: true);
            SharedRsCounters(rd, legacy, input.Count, a.Count); SharedRsCounters(rd, cached, input.Count, b.Count);
            Require(am.ProcessedCandidates == input.Count && bm.ProcessedCandidates == input.Count &&
                am.Survivors == a.Count && bm.Survivors == b.Count, "S shared processed/population metrics changed.");
            SharedRsTable(rd, cached, batch, input.Count > 0);
            if (b.Count > 0) nonzero++;
            cases++;
        }
        var fallback = SharedRsSparse(new(zzfz, 1 << 25));
        var fa = legacy.Execute(fallback, CancellationToken.None, out _);
        var fb = cached.Execute(fallback, CancellationToken.None, out _);
        SharedRsEqual(fa, fb, "S 32M fallback"); SharedRsOracle(fallback, fb, shop: true);
        SharedRsTable(rd, cached, fallback.Batch, populated: true);
        // The real public window path rebases Dense metadata at 4M. Comparing
        // every output catches a cache table mistakenly rebuilt/renumbered per
        // private window, including the non-eight-aligned last window.
        var wa = legacy.Execute(wide, CancellationToken.None, out var wam);
        var wb = cached.Execute(wide, CancellationToken.None, out var wbm);
        SharedRsEqual(wa, wb, "S rebased 4M window");
        Require(wb.ExportAbi1().Span.SequenceEqual(wideExpected), "S rebased windows differ from canonical CPU hash/RNG oracle.");
        SharedRsTable(rd, cached, wide.Batch, populated: true);
        Require(wa.Count > 0 && wam.ProcessedCandidates == wide.Count && wbm.ProcessedCandidates == wide.Count &&
            wam.InputCandidates == wide.Count && wbm.InputCandidates == wide.Count,
            "S windows lost work/counters or only exercised an empty output.");
        var tail = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 4097));
        SharedRsPreCancel(() => cached.Execute(tail, SharedRsCancelled, out _));
        var reused = cached.Execute(tail, CancellationToken.None, out _);
        SharedRsEqual(legacy.Execute(tail, CancellationToken.None, out _), reused, "S post-cancel reuse");
        Require(nonzero > 0, "S fixture only exercised zero survivors.");
        int rejects = SharedRsRejectHeaders(rd, cached, tail.Count, reused.Count);
        Emit(new { phase = "sharedShopPublic", result = "PASS", path = path.ToString(), cases,
            nonzeroBatches = nonzero, rebasedWindowRoots = wide.Count, rebasedWindowOutputs = wb.Count,
            headerRejections = rejects, fallback32M = true, preCancelled = true });
    }

    private static void TestSharedShopPrivate(RenderingDevice rd, ExactSearchExecutionRequest request, ulong zzfz)
    {
        var batch = new SearchBatch(zzfz, 4097);
        uint[] sparse = Enumerable.Range(0, batch.BatchCandidateCount).Where(i => i % 3 != 1).Select(i => (uint)i).ToArray();
        using var input = new PrivateOrdinalBuffer(rd);
        using var outputA = new PrivateOrdinalBuffer(rd);
        using var outputB = new PrivateOrdinalBuffer(rd);
        SharedRsMode(false); using var legacy = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, outputA);
        SharedRsMode(true); using var cached = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, outputB);
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "Smoke.SharedS.PrivateInput");
        int ac = legacy.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out var am);
        int bc = cached.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out var bm);
        var a = outputA.ReadForVerification(batch, ac); var b = outputB.ReadForVerification(batch, bc);
        SharedRsEqual(a, b, "S private shared");
        Require(ac > 0, "S private fixture only exercised zero survivors.");
        SharedRsOracle(FamilyCandidateSet.FromSortedAbi1(batch, sparse.Select(i => (ulong)i).ToArray()), b, shop: true);
        Require(am.ReadbackBytes == 32 && bm.ReadbackBytes == 32, "S private cache added a payload readback.");
        SharedRsCounters(rd, cached, sparse.Length, bc);
        SharedRsPreCancel(() => cached.ExecutePrivateStage(batch, sparse.Length, SharedRsCancelled, out _));
        FamilyGpuComputeUtility.Update(rd, input.Buffer, [(uint)batch.BatchCandidateCount], "Smoke.SharedS.InvalidOrdinal");
        Expect<InvalidDataException>(() => cached.ExecutePrivateStage(batch, 1, CancellationToken.None, out _));
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "Smoke.SharedS.PrivateReuse");
        bc = cached.ExecutePrivateStage(batch, sparse.Length, CancellationToken.None, out _);
        SharedRsEqual(a, outputB.ReadForVerification(batch, bc), "S bad-ordinal recovery");
        SharedRsMode(false);
        using (var denseA = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, null, outputA))
        {
            SharedRsMode(true);
            using var denseB = MerchantShopColorlessGpuExecutor.Create(rd, request,
                MerchantShopColorlessCompactionPath.AtomicAppendHostSort, null, outputB);
            ac = denseA.ExecutePrivateStage(batch, batch.BatchCandidateCount, CancellationToken.None, out _);
            bc = denseB.ExecutePrivateStage(batch, batch.BatchCandidateCount, CancellationToken.None, out _);
            var da = outputA.ReadForVerification(batch, ac); var db = outputB.ReadForVerification(batch, bc);
            SharedRsEqual(da, db, "S Dense private output shared");
            SharedRsOracle(FamilyCandidateSet.Dense(batch), db, shop: true);
            Require(ac > 0, "S Dense private fixture only exercised zero survivors.");
        }
        SharedRsMode(false); using var terminalA = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, null);
        SharedRsMode(true); using var terminalB = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, null);
        SharedRsEqual(terminalA.ExecutePrivate(batch, sparse.Length, CancellationToken.None, out _),
            terminalB.ExecutePrivate(batch, sparse.Length, CancellationToken.None, out _), "S terminal private shared");
        Emit(new { phase = "sharedShopPrivate", result = "PASS", sparseInputs = sparse.Length, outputCount = ac,
            terminalPrivateInput = true, badOrdinalRejected = true, preCancelled = true });
    }

    private static FamilyCandidateSet SharedRsSparse(SearchBatch batch)
    {
        ulong[] ordinals = new ulong[] { 0, 1, 6, 7, 8, 33, 34, 511, 512, 1023, 1048575, 16777215, 16777216,
            batch.BatchCandidateCount == 0 ? 0 : (ulong)batch.BatchCandidateCount - 1 }
            .Where(i => i < (ulong)batch.BatchCandidateCount).Distinct().Order().ToArray();
        return FamilyCandidateSet.FromSortedAbi1(batch, ordinals);
    }

    private static void SharedRsEqual(FamilyCandidateSet a, FamilyCandidateSet b, string label) =>
        Require(a.Batch == b.Batch && a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span), label + ": full output differs.");

    private static void SharedRsOracle(FamilyCandidateSet input, FamilyCandidateSet actual, bool shop)
        => Require(actual.ExportAbi1().Span.SequenceEqual(SharedRsExpected(input, shop)),
            "Shared R/S differs from canonical CPU hash/RNG oracle.");

    private static ulong[] SharedRsExpected(FamilyCandidateSet input, bool shop)
    {
        ulong salt = shop ? XxHash64.HashUtf8("shops", 0) : XxHash64.HashUtf8("up_front", 0);
        var expected = new List<ulong>();
        foreach (ulong ordinal in input.EnumerateLogicalOrdinals())
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(input.Batch.BatchBase + ordinal), 0);
            var rng = new Beta110FastRng(unchecked(root + salt));
            if (shop) for (int i = 0; i < 12; i++) _ = rng.NextDouble();
            if (rng.NextInt(2) == (shop ? 0 : 1)) expected.Add(ordinal);
        }
        return expected.ToArray();
    }

    private const BindingFlags SharedRsFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T SharedRsField<T>(object executor, string name) =>
        (T)(executor.GetType().GetField(name, SharedRsFields)?.GetValue(executor)
            ?? throw new InvalidDataException("Shared R/S test seam changed:" + name));

    private static void SharedRsTable(RenderingDevice rd, object executor, SearchBatch batch, bool populated)
    {
        if (!populated) return; // Empty inputs intentionally do not refresh.
        var cache = SharedRsField<SharedVisibleSeedGpuCache>(executor, "_prefixCache");
        uint[] header = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(cache.Buffer, 0, 16), 4);
        Require(header[2] == (uint)batch.BatchBase && header[3] == (uint)(batch.BatchBase >> 32),
            "Cache was refreshed with an internal window instead of the whole root batch.");
        Require(batch.BatchCandidateCount > PrefixHashCache.MaxCandidateCount ? header[1] == 0 : header[1] > 0,
            "Shared cache did not enable/fall back at its true root-range capacity.");
    }

    private static void SharedRsCounters(RenderingDevice rd, object executor, int processed, int survivors)
    {
        // Empty execution does not dispatch/reset an earlier native header.
        if (processed == 0) return;
        uint[] h = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(SharedRsField<Rid>(executor, "_outputHeader"), 0, 32), 8);
        Require(h[1] == 1 && h[2] == survivors && h[3] == 0 && h[4] == processed &&
            h[5] == SharedRsField<uint>(executor, "_planTag"), "Native R/S population/header identity mismatch.");
    }

    private static int SharedRsRejectHeaders(RenderingDevice rd, object executor, int input, int survivors)
    {
        uint[] original = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(SharedRsField<Rid>(executor, "_outputHeader"), 0, 32), 8);
        var validate = executor.GetType().GetMethod("ValidateHeader", SharedRsFields)
            ?? throw new InvalidDataException("Shared R/S header validation seam changed.");
        Require((int)validate.Invoke(executor, [original, input])! == input, "Valid actual native header was rejected.");
        int rejects = 0;
        foreach ((int at, uint value) in new[] { (0, 0u), (1, 2u), (2, (uint)input + 1), (3, 1u),
            (4, (uint)input - 1), (5, original[5] ^ 1u) })
        {
            uint[] invalid = (uint[])original.Clone(); invalid[at] = value;
            bool rejected = false;
            try { _ = validate.Invoke(executor, [invalid, input]); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { rejected = true; }
            Require(rejected, "R/S completion accepted a corrupt identity/population/overflow header."); rejects++;
        }
        Require(original[2] == survivors && survivors > 0, "Header rejection fixture did not contain actual nonzero output.");
        return rejects;
    }

    private static CancellationToken SharedRsCancelled => new(canceled: true);
    private static void SharedRsPreCancel(Action execute)
    {
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(execute);
        Require(FamilyGpuComputeUtility.CompletedSubmissionTicks == before, "Pre-cancelled R/S cache execution submitted GPU work.");
    }

    private static void TestSharedRsSourceSeams()
    {
        string r = SharedVisibleSeedGpuCache.ApplySource(RelicFamilyGpuExecutor.ShaderSource(false), 13);
        string s = SharedVisibleSeedGpuCache.ApplySource(FamilyGpuComputeUtility.LoadFamilyShaderWithVisibleSeedRootHash("MerchantShopColorless.comp.glsl"), 6);
        foreach (string source in new[] { r, s })
        {
            Require(source.Contains("rt2_prefix_begin(global_ordinal", StringComparison.Ordinal) &&
                source.Contains("rt2_prefix_advance(rt2_row,rt2_suffix", StringComparison.Ordinal) &&
                source.Contains("rt2_legacy_visible_root", StringComparison.Ordinal), "R/S source cache/fallback/carry seam missing.");
            Require(!source.Contains("process_candidate_with_root(input_index, root_hash_for_seed12_packed(seed_first8, seed_tail4))", StringComparison.Ordinal) &&
                !source.Contains("evaluate_merchant_colorless(root_hash_for_seed12_packed(seed_first8, seed_tail4))", StringComparison.Ordinal),
                "A production Dense8 loop escaped shared prefix substitution.");
        }
    }

    private static ExactSearchExecutionRequest SharedRsRequest()
    {
        ModelKey card0 = new("CARD", "SHARED_PREFIX_TEST_0"), card1 = new("CARD", "SHARED_PREFIX_TEST_1");
        NeowEffectCardSnapshot Card(ModelKey key, int order, EffectCardRarity rarity) =>
            new(key.Serialized, key, order, rarity, EffectCardType.Skill, false, false, false, false, true);
        NeowEffectCardSnapshot[] cards = [Card(card0, 0, EffectCardRarity.Uncommon), Card(card1, 1, EffectCardRarity.Uncommon),
            Card(new("CARD", "SHARED_PREFIX_TEST_R0"), 2, EffectCardRarity.Rare), Card(new("CARD", "SHARED_PREFIX_TEST_R1"), 3, EffectCardRarity.Rare)];
        var effects = new NeowEffectAuthoritySnapshot(OrderedDeck: [], CharacterRewardPool: [], ColorlessRewardPool: cards,
            OtherCharacterPools: [], TransformPool: [], PotionPool: [], OrderedRelicBag: [], GeneratedCursePool: [], BonesEligibleRelics: [],
            CharacterStrikeKey: null, CharacterDefendKey: null, ClawKey: null, CardRewardPoolsExact: true, DeckExact: true,
            TransformPoolsExact: true, PotionPoolExact: true, RelicBagExact: true, CursePoolExact: true, BonesEligibilityExact: true,
            CardRewardHooksNoOpExact: true, PotionProcurementHooksNoOpExact: true, NestedRelicHooksExact: true,
            CurrentGold: 0, CurrentPotionCount: 0, PotionCapacity: 3, SourceAuthority: SourceAuthority.AuditedStaticExact,
            Completeness: SnapshotCompleteness.Complete, AuthoritySource: NeowEffectAuthoritySource.FixtureAuthority,
            CapturedProfileId: RuntimeProfileId.Beta111, CatalogFingerprint: "SharedRsSynthetic", SnapshotFingerprint: "SharedRsSynthetic",
            MerchantColorlessOrderedPool: cards, MerchantColorlessOrderedPoolExact: true, MerchantColorlessPoolIdentityHooksExact: true,
            MerchantColorlessRarityIdentityHooksExact: true, MerchantInitialInventoryShapeExact: true);
        var authority = new RuntimeContextAuthoritySnapshot(CharacterIdentity.FromKey(BaseGameModelKeys.Characters.Ironclad),
            0, 0, 1, true, true, true, 0, 0, "0.111.0", RuntimeProfileId.Beta111, true, null, "sts2",
            SourceAuthority.AuditedStaticExact, SnapshotCompleteness.Complete, IdentityResolutionStatus.Exact, effects);
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "SharedRsSynthetic", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, authority.Character.CharacterKey, 0, authority, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "SharedRsSynthetic")), false, "SharedRsSynthetic");
        var evaluation = ExactSearchEvaluationProjection.Empty with { MerchantColorlessConditions = [new(1, MerchantColorlessSlot.Uncommon, card0)] };
        return new(compiled, new("000000000000", 1 << 25, int.MaxValue, 1) { Resources = new(100, 100) },
            "000000000000", 1 << 25, evaluation, CombatRewardRoutePolicyContract.None, "SharedRsSynthetic");
    }
}
