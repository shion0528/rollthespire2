using System.Reflection;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static void TestSharedWorldPrefixCache(RenderingDevice rd)
    {
        string? prior = System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH");
        try
        {
            Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
            var plan = SharedWorldPlan();
            SearchBatch[] batches = [new(0, 0), new(0, 1), new(1, 7), new(33, 8), new(zzfz, 64), new(zzfz, 65),
                new(zzfz, 66), new(zzfz, 259), new(zzfz, 260), new(zzfz, 261), new(zzfz, 1031), new(zzfz, 4097),
                new(0x100000003UL, 513), new(PrefixHashCache.SpaceSize - 513, 513),
                new(PrefixHashCache.SpaceSize - 1, 1), new(PrefixHashCache.SpaceSize, 0)];
            SharedWorldPublic(rd, plan, batches, zzfz);
            SharedWorldPrivate(rd, plan, zzfz);
            SharedWorldEw(rd, plan, zzfz);
            Emit(new { phase = "sharedWorldPrefixCache", result = "PASS", realExecutors = new[] { "W.K4", "EW.PrivateOrdinal.VerifyFullPopulation" },
                capacity = plan.Capacity, groupingK = plan.GroupingK, denseCompactPrivate = true,
                cpuOracle = "CanonicalVisibleSeedXxHash64;IndependentActSelectionRng",
                nonzeroAndWholeOutputParity = true, groupingAndWindowOffsets = true,
                zzfzFinal12ZAndEndSentinel = true, world32MFallback = true, ew32MRejectedByExistingBound = true,
                preCancelledAndReused = true, badPrivateOrdinalRejected = true,
                limits = "SyntheticImmutableVariantOnlyWorldPlan;NotProgressionBossEncounterOrPlanAdmissionCoverage" });
        }
        finally { System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", prior); }
    }

    // The production constructor consumes a captured World replay. Populate its
    // immutable numerical DTO directly, keeping the exact production source,
    // descriptors, four resident slots and command/readback implementation.
    private static WorldFamilyGpuPlan SharedWorldPlan()
    {
        var plan = SharedOtherEmptyPlan<WorldFamilyGpuPlan>();
        uint[][] buffers = Enumerable.Range(0, 19).Select(_ => new uint[] { 0 }).ToArray();
        var meta = new uint[17]; meta[2] = 1; meta[3] = 2; meta[4] = 2; meta[11] = uint.MaxValue;
        buffers[2] = meta; buffers[3] = [0, 2, 0, 0]; buffers[4] = [0, 1]; buffers[5] = [0, 1];
        buffers[6] = new uint[48]; buffers[13] = new uint[24]; buffers[13][0] = 1;
        SharedOtherProperty(plan, "Buffers", buffers); SharedOtherProperty(plan, "Capacity", 65);
        SharedOtherProperty(plan, "GroupingK", 4); SharedOtherProperty(plan, "ScratchWords", 1);
        SharedOtherProperty(plan, "VariantOnly", true);
        return plan;
    }

    private static WorldFamilyGpuExecutor SharedWorldCreate(RenderingDevice rd, WorldFamilyGpuPlan plan, bool enabled,
        PrivateOrdinalBuffer? input = null, PrivateOrdinalBuffer? output = null)
    {
        System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", enabled ? "1" : "0");
        return new(rd, plan, input?.Buffer, output);
    }

    private static ulong[] SharedWorldExpected(SearchBatch batch, IEnumerable<ulong> ordinals) => ordinals.Where(ordinal =>
    {
        ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.GlobalCandidate(ordinal)), 0);
        var rng = new Beta110FastRng(unchecked(root + 0x79a5d5299dc62d50UL));
        return rng.NextInt(2) == 0;
    }).ToArray();

    private static FamilyCandidateSet SharedWorldSparse(SearchBatch batch)
    {
        var head = Enumerable.Range(0, Math.Min(1025, batch.BatchCandidateCount)).Where(i => i % 3 != 1).Select(i => (ulong)i);
        ulong[] edges = [64, 65, 66, 259, 260, 261, 511, 512, 1336335, 1336336, 16777215, 16777216,
            batch.BatchCandidateCount == 0 ? 0 : (ulong)batch.BatchCandidateCount - 1];
        return FamilyCandidateSet.FromSortedAbi1(batch, head.Concat(edges).Where(i => i < (ulong)batch.BatchCandidateCount).Distinct().Order().ToArray());
    }

    private static void SharedWorldPublic(RenderingDevice rd, WorldFamilyGpuPlan plan, IReadOnlyList<SearchBatch> batches, ulong zzfz)
    {
        using var legacy = SharedWorldCreate(rd, plan, false);
        using var cached = SharedWorldCreate(rd, plan, true);
        Require(SharedWorldField<SharedVisibleSeedGpuCache?>(legacy, "_seedCache") is null &&
            SharedWorldField<SharedVisibleSeedGpuCache?>(cached, "_seedCache") is not null, "W did not capture cache mode.");
        int cases = 0, nonzero = 0;
        foreach (var batch in batches)
        foreach (bool compact in new[] { false, true })
        {
            var input = compact ? SharedWorldSparse(batch) : FamilyCandidateSet.Dense(batch);
            var a = legacy.Execute(input, CancellationToken.None, out var am);
            var b = cached.Execute(input, CancellationToken.None, out var bm);
            SharedWorldCompare(input, a, b, am, bm, plan);
            SharedWorldHeaders(rd, legacy, input, resident: false);
            SharedWorldHeaders(rd, cached, input, resident: false);
            SharedWorldTable(rd, cached, input.Batch, input.Count > 0);
            if (b.Count > 0) nonzero++; cases++;
        }
        var fallback = SharedWorldSparse(new(zzfz, 1 << 25));
        var fa = legacy.Execute(fallback, CancellationToken.None, out var fam);
        var fb = cached.Execute(fallback, CancellationToken.None, out var fbm);
        SharedWorldCompare(fallback, fa, fb, fam, fbm, plan);
        SharedWorldTable(rd, cached, fallback.Batch, populated: true);
        var reuse = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 1031));
        SharedWorldPreCancel(() => cached.Execute(reuse, new CancellationToken(canceled: true), out _));
        SharedWorldCompare(reuse, legacy.Execute(reuse, CancellationToken.None, out var ram),
            cached.Execute(reuse, CancellationToken.None, out var rbm), ram, rbm, plan);
        Require(nonzero > 0 && fb.Count > 0 && fbm.SubmissionGroups > 1, "W public fixture missed nonzero/multiple grouped submissions.");
        Emit(new { phase = "sharedWorldPublic", result = "PASS", cases, nonzeroBatches = nonzero,
            capacity = plan.Capacity, groupingK = plan.GroupingK, fallback32M = true, fallbackPopulation = fallback.Count,
            fallbackGroupedSubmissions = fbm.SubmissionGroups, preCancelledAndReused = true });
    }

    private static void SharedWorldPrivate(RenderingDevice rd, WorldFamilyGpuPlan plan, ulong zzfz)
    {
        var batch = new SearchBatch(zzfz, 4097);
        var sparse = SharedWorldSparse(batch); uint[] packed = sparse.ExportAbi1().Span.ToArray().Select(i => (uint)i).ToArray();
        using var input = new PrivateOrdinalBuffer(rd);
        using var outputA = new PrivateOrdinalBuffer(rd);
        using var outputB = new PrivateOrdinalBuffer(rd);
        FamilyGpuComputeUtility.Update(rd, input.Buffer, packed, "Smoke.SharedW.Input");
        using (var legacy = SharedWorldCreate(rd, plan, false, input))
        using (var cached = SharedWorldCreate(rd, plan, true, input))
        {
            var a = legacy.ExecutePrivate(batch, packed.Length, CancellationToken.None, out var am);
            var b = cached.ExecutePrivate(batch, packed.Length, CancellationToken.None, out var bm);
            SharedWorldCompare(sparse, a, b, am, bm, plan);
            SharedWorldHeaders(rd, cached, sparse, resident: false);
            SharedWorldTable(rd, cached, batch, populated: true);
            SharedWorldPreCancel(() => cached.ExecutePrivate(batch, packed.Length, new CancellationToken(canceled: true), out _));
            FamilyGpuComputeUtility.Update(rd, input.Buffer, [(uint)batch.BatchCandidateCount], "Smoke.SharedW.Invalid");
            Expect<InvalidDataException>(() => cached.ExecutePrivate(batch, 1, CancellationToken.None, out _));
            FamilyGpuComputeUtility.Update(rd, input.Buffer, packed, "Smoke.SharedW.Reuse");
            var reused = cached.ExecutePrivate(batch, packed.Length, CancellationToken.None, out _);
            Require(reused.ExportAbi1().Span.SequenceEqual(a.ExportAbi1().Span), "W terminal private reuse changed output.");
        }
        foreach (bool compact in new[] { false, true })
        {
            using var legacy = SharedWorldCreate(rd, plan, false, compact ? input : null, outputA);
            using var cached = SharedWorldCreate(rd, plan, true, compact ? input : null, outputB);
            var source = compact ? sparse : FamilyCandidateSet.Dense(batch);
            int ac = legacy.ExecutePrivateStage(batch, source.Count, CancellationToken.None, out var am);
            int bc = cached.ExecutePrivateStage(batch, source.Count, CancellationToken.None, out var bm);
            var a = outputA.ReadForVerification(batch, ac); var b = outputB.ReadForVerification(batch, bc);
            SharedWorldCompare(source, a, b, am, bm, plan, resident: true);
            SharedWorldHeaders(rd, cached, source, resident: true);
            SharedWorldTable(rd, cached, batch, populated: true);
            Require(ac > 0 && am.SubmissionGroups > 1 && bm.PayloadReadCalls == 0 && bm.PayloadReadBytes == 0,
                "W resident path missed nonzero/groups or added a production payload readback.");
            SharedWorldPreCancel(() => cached.ExecutePrivateStage(batch, source.Count, new CancellationToken(canceled: true), out _));
            if (compact)
            {
                FamilyGpuComputeUtility.Update(rd, input.Buffer, [(uint)batch.BatchCandidateCount], "Smoke.SharedW.InvalidResident");
                Expect<InvalidDataException>(() => cached.ExecutePrivateStage(batch, 1, CancellationToken.None, out _));
                FamilyGpuComputeUtility.Update(rd, input.Buffer, packed, "Smoke.SharedW.ResidentReuse");
            }
            bc = cached.ExecutePrivateStage(batch, source.Count, CancellationToken.None, out _);
            Require(outputB.ReadForVerification(batch, bc).ExportAbi1().Span.SequenceEqual(a.ExportAbi1().Span), "W resident post-cancel/error reuse changed output.");
        }
        Emit(new { phase = "sharedWorldPrivate", result = "PASS", sparseInputs = packed.Length, rootRange = batch.BatchCandidateCount,
            terminalPrivateInput = true, denseAndSparsePrivateOutput = true, privateGroupedOffsets = true,
            badOrdinalRejected = true, preCancelledAndReused = true });
    }

    private static void SharedWorldEw(RenderingDevice rd, WorldFamilyGpuPlan plan, ulong zzfz)
    {
        var eventPlan = SharedOtherEventPlan();
        System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", "0");
        using var publicE = new EventResultFamilyGpuExecutor(rd, eventPlan);
        using var publicW = new WorldFamilyGpuExecutor(rd, plan);
        using var legacy = new EwPrivateGpuExecutor(rd, eventPlan, plan, verify: true);
        System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", "1");
        using var cached = new EwPrivateGpuExecutor(rd, eventPlan, plan, verify: true);
        Require(SharedWorldField<SharedVisibleSeedGpuCache?>(legacy, "_seedCache") is null &&
            SharedWorldField<SharedVisibleSeedGpuCache?>(cached, "_seedCache") is not null &&
            SharedWorldField<SharedVisibleSeedGpuCache?>(SharedWorldField<WorldFamilyGpuExecutor>(cached, "_world"), "_seedCache") is not null,
            "EW custom producer or borrowed W consumer missed the requested cache.");
        SearchBatch[] batches = [new(0, 0), new(0, 1), new(zzfz, 1031), new(zzfz, 4097),
            new(0x100000003UL, 4097), new(PrefixHashCache.SpaceSize - 513, 513), new(PrefixHashCache.SpaceSize, 0)];
        int nonzero = 0;
        foreach (SearchBatch batch in batches)
        {
            var input = FamilyCandidateSet.Dense(batch);
            var eventResult = publicE.Execute(input, CancellationToken.None, out _);
            ulong[] eventExpected = SharedOtherExpected("E", batch, input.EnumerateLogicalOrdinals());
            Require(eventResult.ExportAbi1().Span.SequenceEqual(eventExpected), "EW public E differs from canonical CPU oracle.");
            var expected = publicW.Execute(eventResult, CancellationToken.None, out _);
            Require(expected.ExportAbi1().Span.SequenceEqual(SharedWorldExpected(batch, eventExpected)), "EW public W differs from canonical CPU oracle.");
            var a = legacy.Execute(input, CancellationToken.None, out _);
            var b = cached.Execute(input, CancellationToken.None, out _);
            Require(a.Batch == batch && b.Batch == batch && a.ExportAbi1().Span.SequenceEqual(expected.ExportAbi1().Span) &&
                b.ExportAbi1().Span.SequenceEqual(expected.ExportAbi1().Span), "EW custom cache changed complete final population.");
            // verify:true has also compared every intermediate GPU ordinal.
            uint[] header = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(SharedWorldField<Rid>(cached, "_header")), 8);
            Require(header[0] == 0x45524652u && header[1] == 1 && header[2] == eventExpected.Length && header[3] == 0 &&
                header[4] == input.Count && header[5] == 0x45524652u, "EW producer native identity/population counters changed.");
            SharedWorldTable(rd, cached, batch, input.Count > 0);
            if (b.Count > 0) nonzero++;
        }
        var reuse = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 4097));
        SharedWorldPreCancel(() => cached.Execute(reuse, new CancellationToken(canceled: true), out _));
        Require(cached.Execute(reuse, CancellationToken.None, out _).ExportAbi1().Span.SequenceEqual(
            legacy.Execute(reuse, CancellationToken.None, out _).ExportAbi1().Span), "EW post-cancel reuse changed output.");
        Expect<InvalidOperationException>(() => cached.Execute(FamilyCandidateSet.Dense(new(zzfz, 1 << 25)), CancellationToken.None, out _));
        Expect<InvalidOperationException>(() => cached.Execute(SharedWorldSparse(reuse.Batch), CancellationToken.None, out _));
        Require(nonzero > 0, "EW fixture only exercised zero survivors.");
        Emit(new { phase = "sharedWorldEw", result = "PASS", cases = batches.Length, nonzeroBatches = nonzero,
            customProducerAndWorldConsumer = true, productionIntermediateAndFinalVerification = true,
            publicChainAndCpuOracle = true, preCancelledAndReused = true, capacityAndDenseAdmissionRetained = true });
    }

    private static void SharedWorldCompare(FamilyCandidateSet input, FamilyCandidateSet a, FamilyCandidateSet b,
        WorldFamilyGpuMetrics am, WorldFamilyGpuMetrics bm, WorldFamilyGpuPlan plan, bool resident = false)
    {
        ulong[] expected = SharedWorldExpected(input.Batch, input.EnumerateLogicalOrdinals());
        Require(a.Batch == input.Batch && b.Batch == input.Batch && a.ExportAbi1().Span.SequenceEqual(expected) &&
            b.ExportAbi1().Span.SequenceEqual(expected), "W changed full result or differs from canonical CPU oracle.");
        int dispatches = (input.Count + plan.Capacity - 1) / plan.Capacity;
        int groups = (dispatches + plan.GroupingK - 1) / plan.GroupingK;
        foreach (var m in new[] { am, bm })
            Require(m.Input == input.Count && m.Output == expected.Length && m.Dispatches == dispatches &&
                m.SubmissionGroups == groups && m.HeaderReadCalls == (resident && groups > 0 ? 1 : groups) &&
                m.HeaderReadBytes == (resident ? (groups > 0 ? 1 : 0) : dispatches) * 32L &&
                m.PayloadReadBytes == (resident ? 0 : expected.Length * 4L), "W grouped population/readback/dispatch counters changed.");
    }

    private const BindingFlags SharedWorldFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T SharedWorldField<T>(object value, string name)
    {
        var field = value.GetType().GetField(name, SharedWorldFlags) ?? throw new InvalidDataException("W test seam changed:" + name);
        return (T)field.GetValue(value)!;
    }

    private static void SharedWorldTable(RenderingDevice rd, object executor, SearchBatch batch, bool populated)
    {
        if (!populated) return;
        var cache = SharedWorldField<SharedVisibleSeedGpuCache>(executor, "_seedCache");
        uint[] header = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(cache.Buffer, 0, 16), 4);
        Require(header[2] == (uint)batch.BatchBase && header[3] == (uint)(batch.BatchBase >> 32) &&
            (batch.BatchCandidateCount > PrefixHashCache.MaxCandidateCount ? header[1] == 0 : header[1] > 0),
            "W/EW cache used a K4 subwindow range or failed its 32M fallback.");
    }

    private static void SharedWorldHeaders(RenderingDevice rd, WorldFamilyGpuExecutor executor, FamilyCandidateSet input, bool resident)
    {
        if (input.Count == 0) return;
        Rid[] headers = SharedWorldField<Rid[]>(executor, "_header");
        if (resident)
        {
            uint[] h = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(headers[0], 0, 32), 8);
            Require(h[0] == 0x57464d52u && h[1] == 1 && h[3] == 0 && h[4] == input.Count &&
                h[2] == SharedWorldExpected(input.Batch, input.EnumerateLogicalOrdinals()).Length && h[5] == 0x57463031u,
                "W resident native cumulative population/header mismatch.");
            return;
        }
        int capacity = executor.Capacity, k = executor.GroupingK;
        int first = ((input.Count - 1) / (capacity * k)) * capacity * k;
        ulong[] ordinals = input.EnumerateLogicalOrdinals().ToArray();
        for (int slot = 0; slot < k && first + slot * capacity < input.Count; slot++)
        {
            int offset = first + slot * capacity, count = Math.Min(capacity, input.Count - offset);
            uint[] h = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(headers[slot], 0, 32), 8);
            Require(h[0] == 0x57464d52u && h[1] == 1 && h[3] == 0 && h[4] == count && h[5] == 0x57463031u &&
                h[2] == SharedWorldExpected(input.Batch, ordinals.AsSpan(offset, count).ToArray()).Length,
                "W K4 native per-slot population/header mismatch.");
        }
    }

    private static void SharedWorldPreCancel(Action execute)
    {
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(execute);
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, "W/EW pre-cancellation submitted GPU work.");
    }
}
