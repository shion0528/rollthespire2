using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Synthetic immutable numerical plans avoid game-content capture. All shader
    // bodies, bindings, executor windows, counters and private ports are real.
    // E/A/T constructor admission and live game authority are not tested here.
    private static void TestSharedOtherFamilyPrefixCache(RenderingDevice rd)
    {
        string? prior = System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH");
        string? priorTransform = System.Environment.GetEnvironmentVariable("RT2_T_FULL_TARGET_EXPERIMENT");
        try
        {
            System.Environment.SetEnvironmentVariable("RT2_T_FULL_TARGET_EXPERIMENT", "");
            Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
            SearchBatch[] batches = [new(0, 0), new(0, 1), new(1, 7), new(33, 8), new(zzfz, 511),
                new(zzfz, 512), new(zzfz, 513), new(zzfz, 1537), new(0x100000003UL, 1031),
                new(PrefixHashCache.SpaceSize - 513, 513), new(PrefixHashCache.SpaceSize - 1, 1),
                new(PrefixHashCache.SpaceSize, 0)];
            foreach (string family in new[] { "C", "E", "A", "T", "Tfull" })
            {
                SharedOtherPublic(rd, family, batches, zzfz);
                SharedOtherPrivate(rd, family, zzfz, privateOutput: false);
                SharedOtherPrivate(rd, family, zzfz, privateOutput: true);
            }
            Emit(new { phase = "sharedOtherFamilyPrefixCache", result = "PASS",
                realExecutors = new[] { "C.GenericStreaming", "E", "A", "T.Generic", "T.FullTargetCarry8" },
                canonicalCpuHashAndRngOracle = true, variableNonzeroOutputs = true, denseCompactPrivate = true,
                zzfzFinal12ZAndEmptyEnd = true, fallback32M = true, privateInputOffsetWindows = true,
                limits = "SyntheticNumericalPlans;EATContentCaptureAndPlanAdmissionExcluded;NotEveryRewardContextOrAncientGenerator" });
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", prior);
            System.Environment.SetEnvironmentVariable("RT2_T_FULL_TARGET_EXPERIMENT", priorTransform);
        }
    }

    private readonly record struct SharedOtherResult(FamilyCandidateSet Population, int Processed,
        int Survivors, int Dispatches, long ReadbackBytes);

    private sealed class SharedOtherExecutor(IDisposable native,
        Func<FamilyCandidateSet, int, CancellationToken, SharedOtherResult> run) : IDisposable
    {
        internal object Native => native;
        internal SharedOtherResult Run(FamilyCandidateSet input, int count, CancellationToken token) => run(input, count, token);
        public void Dispose() => native.Dispose();
    }

    private static SharedOtherExecutor SharedOtherCreate(RenderingDevice rd, string family, bool enabled,
        PrivateOrdinalBuffer? privateInput = null, PrivateOrdinalBuffer? privateOutput = null)
    {
        System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", enabled ? "1" : "0");
        FamilyCandidateSet Population(FamilyCandidateSet? value, SearchBatch batch, int count) =>
            value ?? privateOutput!.ReadForVerification(batch, count);
        switch (family)
        {
            case "C":
            {
                var executor = new CombatRewardGpuExecutor(rd, SharedOtherCombatPlan(), forceGeneric: true,
                    privateInput: privateInput, privateOutput: privateOutput);
                return new(executor, (input, count, token) =>
                {
                    FamilyCandidateSet? value = null; CombatRewardGpuMetrics m;
                    if (privateOutput is not null) executor.ExecutePrivateStage(input.Batch, count, token, out m);
                    else if (privateInput is not null) value = executor.ExecutePrivateInput(input.Batch, count, token, out m);
                    else value = executor.Execute(input, token, out m);
                    return new(Population(value, input.Batch, m.Output), m.Input, m.Output, m.Dispatches, m.ReadbackBytes);
                });
            }
            case "E":
            {
                var executor = new EventResultFamilyGpuExecutor(rd, SharedOtherEventPlan(), privateInput, privateOutput);
                return new(executor, (input, count, token) =>
                {
                    FamilyCandidateSet? value = null; EventResultFamilyGpuMetrics m;
                    if (privateOutput is not null) executor.ExecutePrivateStage(input.Batch, count, token, out m);
                    else if (privateInput is not null) value = executor.ExecutePrivate(input.Batch, count, token, out m);
                    else value = executor.Execute(input, token, out m);
                    return new(Population(value, input.Batch, m.Output), m.Input, m.Output, m.Dispatches, m.ReadbackBytes);
                });
            }
            case "A":
            {
                var executor = new AncientOptionFamilyGpuExecutor(rd, SharedOtherAncientPlan(), privateInput, privateOutput);
                return new(executor, (input, count, token) =>
                {
                    FamilyCandidateSet? value = null; AncientOptionFamilyGpuMetrics m;
                    if (privateOutput is not null) executor.ExecutePrivateStage(input.Batch, count, token, out m);
                    else if (privateInput is not null) value = executor.ExecutePrivate(input.Batch, count, token, out m);
                    else value = executor.Execute(input, token, out m);
                    return new(Population(value, input.Batch, m.Output), m.Input, m.Output, m.Dispatches, m.ReadbackBytes);
                });
            }
            case "T": case "Tfull":
            {
                var executor = new TransformationAggregateGpuExecutor(rd, SharedOtherTransformationPlan(family == "Tfull"),
                    privateInput, privateOutput);
                return new(executor, (input, count, token) =>
                {
                    FamilyCandidateSet? value = null; TransformationAggregateGpuMetrics m;
                    if (privateOutput is not null) executor.ExecutePrivateStage(input.Batch, count, token, out m);
                    else if (privateInput is not null) value = executor.ExecutePrivate(input.Batch, count, token, out m);
                    else value = executor.Execute(input, token, out m);
                    return new(Population(value, input.Batch, m.Output), m.Input, m.Output, m.Dispatches, m.ReadbackBytes);
                });
            }
            default: throw new ArgumentOutOfRangeException(nameof(family));
        }
    }

    private static CombatRewardGpuPlan SharedOtherCombatPlan()
    {
        var meta = new uint[64]; meta[1] = 0x43463031; meta[3] = 1; meta[5] = 1; meta[11] = 1;
        SharedOtherSalt(meta, 14, "SharedPrefix.CombatFixture");
        var pools = new uint[48];
        for (int rarity = 0; rarity < 3; rarity++)
        {
            pools[20 + rarity * 2] = 0; pools[21 + rarity * 2] = 4;
            pools[32 + rarity * 2] = 4; pools[33 + rarity * 2] = 1;
        }
        var predicate = new uint[16]; predicate[0] = 1; predicate[14] = predicate[15] = 15;
        return new(meta, pools, [11, 22, 33, 44, 99], predicate, [0],
            Beta110GpuCombatRewardHotLoopPlan.Disabled("SharedPrefixFixture"), 1);
    }

    private static EventResultFamilyPlan SharedOtherEventPlan()
    {
        var plan = SharedOtherEmptyPlan<EventResultFamilyPlan>();
        var meta = new uint[11]; meta[0] = 1; meta[9] = 0; meta[10] = 3;
        SharedOtherSalt(meta, 3, "SharedPrefix.EventFixture");
        SharedOtherProperty(plan, "Capacity", 512);
        SharedOtherProperty(plan, "Buffers", new uint[][] { meta, [0] });
        return plan;
    }

    private static AncientOptionFamilyPlan SharedOtherAncientPlan()
    {
        var plan = SharedOtherEmptyPlan<AncientOptionFamilyPlan>();
        var option = new uint[16]; option[0] = 7; option[1] = 5; option[7] = 4;
        option[10] = 65535; option[12] = 3;
        SharedOtherSalt(option, 4, "SharedPrefix.AncientFixture");
        var branch = new uint[12]; branch[0] = 7; branch[2] = 1; branch[6] = 1;
        SharedOtherProperty(plan, "Capacity", 512); SharedOtherProperty(plan, "ScratchWords", 3);
        SharedOtherProperty(plan, "Buffers", new uint[][]
        {
            [1, 0, 1, 3, 512, 0, 0, 0], [1, 7, 0, 0], option,
            [15, 0, 2, 0, 16, 2, 1, 0, 17, 3, 1, 0, 18, 4, 1, 0],
            [11, 22, 33, 44, 55], [0], branch, [11]
        });
        return plan;
    }

    private static TransformationAggregateGpuPlan SharedOtherTransformationPlan(bool fullTarget)
    {
        var plan = SharedOtherEmptyPlan<TransformationAggregateGpuPlan>();
        // One required target, one draw from [target, other, other]. The same
        // numerical predicate exercises both generic and dense Carry8 kernels.
        var meta = new uint[40]; meta[1] = 1; meta[3] = meta[4] = meta[5] = 1; meta[6] = 32;
        SharedOtherSalt(meta, 32, "SharedPrefix.TransformFixture");
        meta[35] = 1; meta[36] = 0; meta[37] = 3;
        SharedOtherProperty(plan, "FullTarget", fullTarget); SharedOtherProperty(plan, "BonesOptimized", false);
        SharedOtherProperty(plan, "Supported", true); SharedOtherProperty(plan, "Buffers", new uint[][] { meta, [1, 0, 0] });
        return plan;
    }

    private static T SharedOtherEmptyPlan<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void SharedOtherProperty(object plan, string name, object value) =>
        (plan.GetType().GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing fixture property: " + name)).SetValue(plan, value);
    private static void SharedOtherSalt(uint[] words, int at, string salt)
    {
        ulong hash = XxHash64.HashUtf8(salt, 0); words[at] = (uint)hash; words[at + 1] = (uint)(hash >> 32);
    }

    private static ulong[] SharedOtherExpected(string family, SearchBatch batch, IEnumerable<ulong> ordinals)
    {
        string salt = family switch
        {
            "C" => "SharedPrefix.CombatFixture", "E" => "SharedPrefix.EventFixture",
            "A" => "SharedPrefix.AncientFixture", _ => "SharedPrefix.TransformFixture"
        };
        ulong stream = XxHash64.HashUtf8(salt, 0);
        return ordinals.Where(ordinal =>
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.GlobalCandidate(ordinal)), 0);
            var rng = new Beta110FastRng(unchecked(root + stream));
            if (family == "C") { rng.NextFloat(); return rng.NextInt(11) == 5; }
            return rng.NextInt(family == "E" ? 10 : 3) == (family == "E" ? 3 : 0);
        }).Order().ToArray();
    }
    private static FamilyCandidateSet SharedOtherSparse(string family, SearchBatch batch)
    {
        if (batch.BatchCandidateCount == 0) return FamilyCandidateSet.FromSortedAbi1(batch, []);
        ulong[] candidates = [0, (ulong)Math.Min(7, batch.BatchCandidateCount - 1), (ulong)Math.Min(8, batch.BatchCandidateCount - 1),
            (ulong)Math.Min(511, batch.BatchCandidateCount - 1), (ulong)Math.Min(512, batch.BatchCandidateCount - 1),
            (ulong)batch.BatchCandidateCount - 1];
        var head = Enumerable.Range(0, Math.Min(128, batch.BatchCandidateCount)).Select(i => (ulong)i);
        ulong[] values = candidates.Concat(SharedOtherExpected(family, batch, head).Take(3)).Distinct().Order().ToArray();
        return FamilyCandidateSet.FromSortedAbi1(batch, values);
    }

    private static void SharedOtherPublic(RenderingDevice rd, string family, IReadOnlyList<SearchBatch> batches, ulong zzfz)
    {
        using var control = SharedOtherCreate(rd, family, false);
        using var cached = SharedOtherCreate(rd, family, true);
        Require(SharedOtherCache(control.Native) is null && SharedOtherCache(cached.Native) is not null,
            family + " did not capture the requested cache state.");
        int nonzero = 0;
        foreach (SearchBatch batch in batches)
        foreach (bool compact in new[] { false, true })
        {
            var input = compact ? SharedOtherSparse(family, batch) : FamilyCandidateSet.Dense(batch);
            var a = control.Run(input, input.Count, CancellationToken.None);
            var b = cached.Run(input, input.Count, CancellationToken.None);
            SharedOtherCompare(rd, family, control, cached, input, a, b, oracle: true);
            SharedOtherTable(rd, cached.Native, batch);
            nonzero += b.Survivors > 0 ? 1 : 0;
        }
        // A 32M root range with a tiny sparse population exercises the actual
        // fallback in every full executor without allocating a 32M table.
        var fallback = SharedOtherSparse(family, new SearchBatch(zzfz, 1 << 25));
        var fa = control.Run(fallback, fallback.Count, CancellationToken.None);
        var fb = cached.Run(fallback, fallback.Count, CancellationToken.None);
        SharedOtherCompare(rd, family, control, cached, fallback, fa, fb, oracle: true);
        SharedOtherTable(rd, cached.Native, fallback.Batch);
        if (family is "C" or "T")
        {
            var wide = FamilyCandidateSet.Dense(new SearchBatch(zzfz, (1 << 20) + 17));
            var wa = control.Run(wide, wide.Count, CancellationToken.None);
            var wb = cached.Run(wide, wide.Count, CancellationToken.None);
            SharedOtherCompare(rd, family, control, cached, wide, wa, wb, oracle: false);
            Require(wa.Dispatches == 2 && wb.Survivors > 0, family + " missed actual 1M host-window rebasing.");
            SharedOtherTable(rd, cached.Native, wide.Batch);
        }
        var reuse = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 1537));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => cached.Run(reuse, reuse.Count, cancelled.Token));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, family + " cancelled operation submitted work.");
        SharedOtherCompare(rd, family, control, cached, reuse,
            control.Run(reuse, reuse.Count, CancellationToken.None), cached.Run(reuse, reuse.Count, CancellationToken.None), oracle: true);
        Require(nonzero > 0, family + " only tested zero survivors.");
        Emit(new { phase = "sharedOtherPublicParity", result = "PASS", family, nonzeroCases = nonzero,
            rootHashOracle = true, fallback32M = true, multiHostWindow = family != "Tfull", reusedAfterCancel = true });
    }

    private static void SharedOtherPrivate(RenderingDevice rd, string family, ulong zzfz, bool privateOutput)
    {
        using var inputBuffer = new PrivateOrdinalBuffer(rd);
        using var outputA = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var outputB = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
        using var control = SharedOtherCreate(rd, family, false, inputBuffer, outputA);
        using var cached = SharedOtherCreate(rd, family, true, inputBuffer, outputB);
        int nonzero = 0;
        foreach (SearchBatch batch in new[] { new SearchBatch(zzfz, 1537), new SearchBatch(PrefixHashCache.SpaceSize - 513, 513),
            new SearchBatch(PrefixHashCache.SpaceSize, 0) })
        {
            // Deliberately arbitrary private order and >512 input candidates.
            uint[] values = Enumerable.Range(0, batch.BatchCandidateCount).Where(i => i % 3 != 1).Reverse().Select(i => (uint)i).ToArray();
            if (values.Length > 0) FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, values, "SharedOther.PrivateInput");
            var a = control.Run(FamilyCandidateSet.Dense(batch), values.Length, CancellationToken.None);
            var b = cached.Run(FamilyCandidateSet.Dense(batch), values.Length, CancellationToken.None);
            var canonicalInput = FamilyCandidateSet.FromSortedAbi1(batch, values.Select(i => (ulong)i).Order().ToArray());
            SharedOtherCompare(rd, family, control, cached, canonicalInput, a, b, oracle: true);
            SharedOtherTable(rd, cached.Native, batch);
            if (batch.BatchCandidateCount == 1537 && family is ("E" or "A")) Require(a.Dispatches == 3,
                family + " private source offset was not exercised across three host windows.");
            nonzero += b.Survivors > 0 ? 1 : 0;
        }
        var invalid = new SearchBatch(zzfz, 1537);
        FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, [0u, (uint)invalid.BatchCandidateCount, 7u], "SharedOther.BadOrdinal");
        Expect<InvalidDataException>(() => control.Run(FamilyCandidateSet.Dense(invalid), 3, CancellationToken.None));
        Expect<InvalidDataException>(() => cached.Run(FamilyCandidateSet.Dense(invalid), 3, CancellationToken.None));
        uint[] repaired = Enumerable.Range(0, 129).Reverse().Select(i => (uint)i).ToArray();
        FamilyGpuComputeUtility.Update(rd, inputBuffer.Buffer, repaired, "SharedOther.Repaired");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => cached.Run(FamilyCandidateSet.Dense(invalid), repaired.Length, cancelled.Token));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, family + " private cancel submitted work.");
        var source = FamilyCandidateSet.FromSortedAbi1(invalid, repaired.Select(i => (ulong)i).Order().ToArray());
        SharedOtherCompare(rd, family, control, cached, source,
            control.Run(FamilyCandidateSet.Dense(invalid), repaired.Length, CancellationToken.None),
            cached.Run(FamilyCandidateSet.Dense(invalid), repaired.Length, CancellationToken.None), oracle: true);
        Require(nonzero > 0 && SharedOtherCache(control.Native) is null && SharedOtherCache(cached.Native) is not null,
            family + " private fixture missed nonzero outputs or actual cache state.");
        Emit(new { phase = "sharedOtherPrivateParity", result = "PASS", family, privateOutput,
            nonzeroCases = nonzero, badOrdinalRejected = true, arbitraryOrder = true, repairedAndReusedAfterCancel = true });
    }

    private static SharedVisibleSeedGpuCache? SharedOtherCache(object executor) => (SharedVisibleSeedGpuCache?)
        (executor.GetType().GetField("_visibleSeedCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing actual cache field.")).GetValue(executor);

    private static void SharedOtherTable(RenderingDevice rd, object executor, SearchBatch batch)
    {
        var cache = SharedOtherCache(executor) ?? throw new InvalidOperationException("Expected actual cache.");
        uint[] actual = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(cache.Buffer));
        uint[] expected = batch.BatchCandidateCount <= PrefixHashCache.MaxCandidateCount
            ? PrefixHashCache.BuildTable(batch.BatchBase, batch.BatchCandidateCount) : new uint[4];
        expected[2] = (uint)batch.BatchBase; expected[3] = (uint)(batch.BatchBase >> 32);
        Require(actual.AsSpan(0, expected.Length).SequenceEqual(expected), "Actual prefix table was rebuilt for a host subwindow or fallback stayed stale.");
    }

    private static uint[] SharedOtherHeader(RenderingDevice rd, object executor)
    {
        var field = executor.GetType().GetField("_header", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing real family header.");
        return FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData((Rid)field.GetValue(executor)!));
    }
    private static void SharedOtherCompare(RenderingDevice rd, string family, SharedOtherExecutor control, SharedOtherExecutor cached,
        FamilyCandidateSet input, SharedOtherResult a, SharedOtherResult b, bool oracle)
    {
        Require(a.Population.Batch == input.Batch && b.Population.Batch == input.Batch &&
            a.Population.ExportAbi1().Span.SequenceEqual(b.Population.ExportAbi1().Span) &&
            a.Processed == input.Count && b.Processed == input.Count && a.Survivors == a.Population.Count &&
            b.Survivors == b.Population.Count && a.Dispatches == b.Dispatches && a.ReadbackBytes == b.ReadbackBytes,
            family + " actual population/counter/transport mismatch.");
        Require(SharedOtherHeader(rd, control.Native).AsSpan().SequenceEqual(SharedOtherHeader(rd, cached.Native)),
            family + " actual native headers changed.");
        if (oracle) Require(b.Population.ExportAbi1().Span.SequenceEqual(SharedOtherExpected(family, input.Batch, input.EnumerateLogicalOrdinals())),
            family + " disagrees with canonical visible-seed xxHash64 and CPU RNG.");
    }
}
