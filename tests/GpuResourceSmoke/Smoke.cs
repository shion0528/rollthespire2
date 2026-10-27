using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text.Json;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke : Node
{
    public override void _Ready()
    {
        RenderingDevice? rd = null;
        Exception? failure = null;
        try
        {
            rd = RenderingServer.CreateLocalRenderingDevice()
                ?? throw new InvalidOperationException("A real RenderingDevice is required; do not launch with --headless or Compatibility.");
            var testedAssembly = typeof(FamilyCpuExecution).Assembly;
            string testedPath = string.IsNullOrEmpty(testedAssembly.Location)
                ? ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/RolltheSpire2.dll")
                : testedAssembly.Location;
            byte[] testedBytes = File.ReadAllBytes(testedPath);
            using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(testedBytes)))
            {
                var metadata = pe.GetMetadataReader();
                if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != testedAssembly.ManifestModule.ModuleVersionId)
                    throw new InvalidDataException("The loaded Mod differs from the artifact on disk.");
            }
            Emit(new { phase = "start", device = rd.GetDeviceName(),
                assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    testedBytes)),
                assemblyVersion = testedAssembly.GetName().Version?.ToString() });
            TestBudgetStartup();
            TestPayloadLengthValidation();
            using var reuse = new FamilyGpuComputeUtility.ShaderReuse(rd);
            using var scope = reuse.Activate();
            if (System.Environment.GetEnvironmentVariable("RT2_NEOW_STRUCTURAL_CHECK") == "1")
                TestNeowStructuralOptimizations(rd);
            else if (System.Environment.GetEnvironmentVariable("RT2_MERCHANT_ORDERED_SEQUENCE_CHECK") == "1")
                TestMerchantOrderedSequenceSlots(rd);
            else if (System.Environment.GetEnvironmentVariable("RT2_MERCHANT_EARLY_REJECT_CHECK") == "1")
                TestMerchantEarlyReject(rd);
            else if (System.Environment.GetEnvironmentVariable("RT2_STRUCTURAL_CHECK") == "1")
            {
                TestSparsePayloadReadback(rd);
                TestHeaderReadbackChecks(rd);
                TestDoubleWindows(rd);
                TestDoubleWindowAllocationFallback(rd);
            }
            else if (System.Environment.GetEnvironmentVariable("RT2_OPTIMIZATION_CHECK") == "1")
            {
                TestOptimizationSettings(rd);
#if RT2_SHARED_PREFIX_TESTS
                TestOptimizationHeaderReadback(rd);
#endif
                TestHeaderReadbackChecks(rd);
                TestDoubleWindows(rd);
                TestDoubleWindowAllocationFallback(rd);
            }
            else if (System.Environment.GetEnvironmentVariable("RT2_GROUP_OUTPUT_CHECK") == "1")
                TestGroupOutput(rd);
            else if (System.Environment.GetEnvironmentVariable("RT2_INTERNAL_ORDER_CHECK") == "1")
                TestInternalFilterOrder(rd);
            else
#if RT2_SHARED_PREFIX_TESTS
            if (System.Environment.GetEnvironmentVariable("RT2_GPU_SHARED_WORLD_ONLY") == "1")
                TestSharedWorldPrefixCache(rd);
            else
#endif
            if (System.Environment.GetEnvironmentVariable("RT2_GPU_PREFIX_SCOPE_ONLY") == "1")
                TestExpandedPrefixCacheScope(rd);
            else if (System.Environment.GetEnvironmentVariable("RT2_GPU_FOUR_WAY_BENCH") == "1")
                RunFourWayBenchmark(rd);
            else
            {
                TestStaticDrawGates(rd);
#if RT2_SHARED_PREFIX_TESTS
                TestSharedVisibleSeedCache(rd);
                TestSharedNeowPrefixCache(rd);
                TestSharedRelicShopPrefixCache(rd);
                TestSharedOtherFamilyPrefixCache(rd);
                TestSharedWorldPrefixCache(rd);
#endif
                TestHeaderReadbackChecks(rd);
                TestLeafyGate(rd);
                TestPrefixHashCache(rd);
                TestExpandedPrefixCacheScope(rd);
                TestProfilingChecks(rd);
                TestArguments(rd);
                TestParity(rd, leafy: false);
                TestParity(rd, leafy: true);
                TestOverflow(rd, pair: false);
                TestOverflow(rd, pair: true);
                Benchmark(rd);
                TestDoubleWindows(rd);
                TestDoubleWindowAllocationFallback(rd);
                TestNestedInitializationCleanupFailure(rd);
                BenchmarkDoubleWindows(rd);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // RenderingDevice is an Object, not a RefCounted resource. Dispose
            // only releases its managed wrapper; explicitly destroy the local
            // native device after all executor RIDs and shader owners close.
            if (rd is not null)
            {
                try { rd.Free(); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            }
        }
        if (failure is not null)
        {
            GD.PushError(failure.ToString());
            Emit(new { phase = "complete", result = "FAIL", error = failure.ToString() });
        }
        else Emit(new { phase = "complete", result = "PASS" });
        GetTree().Quit(failure is null ? 0 : 1);
    }

    private static void Emit(object value) => GD.Print("RT2_GPU_SMOKE " + JsonSerializer.Serialize(value));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void TestBudgetStartup()
    {
        var context = new FamilyExecutionContext(requiresGpu: true, resources: new SearchResourceBudget(100, 1));
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            long firstTicks = context.ExecuteGpuAsync(_ =>
            {
                long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
                // Simulated compilation on the real dedicated GPU owner. No
                // commands are submitted, so a 1% duty cycle owes no cooldown.
                System.Threading.Thread.Sleep(50);
                return FamilyGpuComputeUtility.CompletedSubmissionTicks - before;
            }, startupTimeout.Token).GetAwaiter().GetResult();
            Require(firstTicks == 0, "Startup-only action unexpectedly recorded submitted GPU work.");
            using var nextTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var timer = Stopwatch.StartNew();
            var second = context.ExecuteGpuAsync(_ => (Value: 42, Ticks: FamilyGpuComputeUtility.CompletedSubmissionTicks),
                nextTimeout.Token).GetAwaiter().GetResult();
            double secondMs = timer.Elapsed.TotalMilliseconds;
            Require(second.Value == 42 && second.Ticks == 0 && secondMs < 1500,
                "GPU budget multiplied CPU startup into an artificial cooldown.");
            Emit(new { phase = "budgetStartup", result = "PASS", gpuPercent = 1, simulatedCompileMs = 50,
                noSubmitActions = 2, completedSubmissionTicks = second.Ticks, nextActionMs = secondMs,
                evidence = "RealFamilyExecutionContext;CancelableCalls;NoGpuSubmission;CpuSetupExcludedFromDutyCycle" });
        }
        finally { context.DisposeAsync().GetAwaiter().GetResult(); }
    }

    private static void TestPayloadLengthValidation()
    {
        Require(FamilyGpuComputeUtility.FromUInt32Bytes([], 0).Length == 0,
            "A valid zero-survivor payload was rejected.");
        uint[] values = [42, 0, uint.MaxValue];
        Require(FamilyGpuComputeUtility.FromUInt32Bytes(FamilyGpuComputeUtility.ToBytes(values), 3).SequenceEqual(values),
            "An exact nonzero payload was changed.");
        int rejected = 0;
        void Reject(byte[] bytes, uint count)
        {
            Expect<InvalidDataException>(() => FamilyGpuComputeUtility.FromUInt32Bytes(bytes, count));
            rejected++;
        }
        Reject([], 1);                 // Native failure returning empty data.
        Reject(new byte[4], 2);         // Aligned but truncated.
        Reject(new byte[12], 2);        // Aligned but longer than the header.
        Reject(new byte[4], 0);         // Header says no survivors.
        Reject(new byte[3], 1);         // Truncated within a uint.
        Reject([], uint.MaxValue);     // Malformed count cannot wrap byte size.
        Emit(new { phase = "payloadLengthValidation", result = "PASS", accepted = 2, rejected,
            productionHelper = true, zeroEmptyAndNonzeroEmptyDistinguished = true });
    }

    private static void TestArguments(RenderingDevice rd)
    {
        var owned = new List<Rid>();
        Rid Own(Rid rid) { Require(rid.IsValid, "Argument-test resource allocation failed."); owned.Add(rid); return rid; }
        try
        {
            uint[] meta = [0, 0, 0, 0, 0, 0, NeowFamilyGpuPlan.CurseCapacity, NeowFamilyGpuPlan.PairCapacity];
            Rid batch = Own(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(meta)));
            Rid header = Own(FamilyGpuComputeUtility.CreateStorageBuffer(rd, new byte[32]));
            Rid arguments = Own(rd.StorageBufferCreate(24, Array.Empty<byte>(), RenderingDevice.StorageBufferUsage.Indirect));
            Rid shader = Own(FamilyGpuComputeUtility.CompileShader(rd,
                FamilyGpuComputeUtility.LoadEmbeddedShader("NeowDispatchArguments.comp.glsl"), "NeowArgumentsSmoke"));
            Rid pipeline = Own(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader));
            Rid uniforms = Own(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, [batch, header, arguments]));
            uint[] counts = [0, 1, 7, 8, 511, 512, 513, 65535, 65536, 65537,
                NeowFamilyGpuPlan.CurseCapacity - 1, NeowFamilyGpuPlan.CurseCapacity,
                NeowFamilyGpuPlan.CurseCapacity + 1, uint.MaxValue];
            int cases = 0;
            foreach (uint count in counts)
            foreach (uint error in new uint[] { 0, 1 })
            {
                uint[] source = [0x4e464d52, 1, 0, error, 0, 42, count, count];
                FamilyGpuComputeUtility.Update(rd, header, source, "Smoke.Header");
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, pipeline);
                rd.ComputeListBindUniformSet(list, uniforms, 0);
                rd.ComputeListDispatch(list, 1, 1, 1);
                rd.ComputeListEnd();
                FamilyGpuComputeUtility.SubmitAndSync(rd, "Smoke.Arguments");
                uint[] result = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(arguments));
                uint Expected(uint capacity) => error != 0 ? 0 : (Math.Min(count, capacity) + 511u) / 512u;
                Require(result.SequenceEqual(new uint[] { Expected(meta[6]), 1, 1, Expected(meta[7]), 1, 1 }),
                    $"Indirect arguments mismatch for count={count},error={error}.");
                Require(FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(header)).SequenceEqual(source),
                    "Argument generation changed error/overflow evidence.");
                cases++;
            }
            Emit(new { phase = "arguments", result = "PASS", cases });
        }
        finally { FamilyGpuComputeUtility.FreeAll(rd, owned); }
    }

    // Immutable synthetic numeric plans exercise the same production executors,
    // without a running game, content singleton, UI, or gameplay state.
    private static NeowFamilyGpuPlan Plan(bool leafy, bool pairOverflow = false, bool curseOverflow = false)
    {
        uint[] meta = new uint[128];
        meta[6] = 1; meta[7] = 10; meta[8] = pairOverflow ? 2u : 28u; meta[9] = 3;
        meta[12] = meta[13] = 255; meta[25] = 0; meta[26] = 1; meta[27] = 4; meta[28] = 14;
        void Hash(int at, string text)
        {
            ulong hash = XxHash64.HashUtf8(text, 0);
            meta[at] = (uint)hash; meta[at + 1] = (uint)(hash >> 32);
        }
        Hash(31, "NEOW"); Hash(33, "Rewards"); Hash(35, "niche");
        Hash(37, "transformations"); Hash(62, "combat_potion_generation");
        meta[39] = curseOverflow || pairOverflow ? 1u : 10u;
        meta[56] = 2; meta[58] = meta[59] = 64;
        meta[66] = meta[67] = 1;
        meta[68] = 1; meta[69] = curseOverflow || pairOverflow ? 0u : 0x3feu;
        for (int i = 0; i < 10; i++) meta[71 + i] = (uint)i;
        meta[81] = 255;
        uint[] conditions = new uint[160];
        if (leafy)
        {
            meta[70] = 1; meta[82] = 4; meta[84] = 1; meta[85] = 4;
            conditions[20] = 2; conditions[21] = 100; conditions[22] = 200; conditions[24] = 0x80000000u;
        }
        return new(meta, new uint[64], [0], Enumerable.Range(100, 64).Select(x => (uint)x).ToArray(),
            Enumerable.Range(200, 64).Select(x => (uint)x).ToArray(),
            Enumerable.Range(0, (int)meta[8]).Select(x => (uint)x).ToArray(), conditions);
    }

    private static void TestParity(RenderingDevice rd, bool leafy)
    {
        var plan = Plan(leafy);
        using var direct = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false);
        using var indirect = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: true);
        long outputs = 0;
        int cases = 0;
        foreach (var batch in new[] { new SearchBatch(0, 0), new SearchBatch(0, 1), new SearchBatch(0, 7),
            new SearchBatch(0, 8), new SearchBatch(0, 511), new SearchBatch(0, 512), new SearchBatch(0, 513),
            new SearchBatch(0, 65537), new SearchBatch(0x100000003, 513) })
        {
            var input = FamilyCandidateSet.Dense(batch);
            var a = direct.Execute(input, CancellationToken.None, out var am);
            var b = indirect.Execute(input, CancellationToken.None, out var bm);
            Require(a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span), $"Parity mismatch: leafy={leafy},batch={batch}.");
            Require(am.Processed == bm.Processed && am.CurseSurvivors == bm.CurseSurvivors && am.PairSurvivors == bm.PairSurvivors,
                "Direct/indirect checkpoint counters differ.");
            if (input.Count > 0)
            {
                Require(indirect.UsesIndirectStaging, "Indirect initialization fell back; this run cannot validate indirect execution.");
                Require(am.Dispatches == 3 && bm.Dispatches == 5, "Dispatch metrics do not include argument generators.");
            }
            outputs += b.Count; cases++;
        }
        // Compact is a distinct fused path. Initializing the indirect modules
        // first must not alter that transport or its one-dispatch accounting.
        var compact = FamilyCandidateSet.FromSortedAbi1(new SearchBatch(0, 65537), [0, 7, 8, 511, 512, 65536]);
        var compactA = direct.Execute(compact, CancellationToken.None, out var ca);
        var compactB = indirect.Execute(compact, CancellationToken.None, out var cb);
        Require(compactA.ExportAbi1().Span.SequenceEqual(compactB.ExportAbi1().Span) && ca.Dispatches == 1 && cb.Dispatches == 1,
            "Compact transport changed after staged initialization.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Expect<OperationCanceledException>(() => indirect.Execute(FamilyCandidateSet.Dense(new SearchBatch(0, 1)), cancelled.Token, out _));
        Expect<InvalidOperationException>(() => indirect.Execute(FamilyCandidateSet.Dense(new SearchBatch(0, NeowFamilyGpuExecutor.Capacity + 1)), CancellationToken.None, out _));
        Emit(new { phase = "parity", result = "PASS", leafy, cases, outputs });
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidDataException($"Expected {typeof(T).Name} was not raised.");
    }

    private static void TestOverflow(RenderingDevice rd, bool pair)
    {
        var plan = Plan(false, pairOverflow: pair, curseOverflow: !pair);
        int count = (pair ? NeowFamilyGpuPlan.PairCapacity : NeowFamilyGpuPlan.CurseCapacity) + 1;
        foreach (bool indirect in new[] { false, true })
        {
            using var executor = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: indirect);
            Expect<InvalidDataException>(() => executor.Execute(FamilyCandidateSet.Dense(new SearchBatch(0, count)), CancellationToken.None, out _));
            if (indirect) Require(executor.UsesIndirectStaging, "Overflow test unexpectedly used direct fallback.");
        }
        Emit(new { phase = "overflow", result = "PASS", checkpoint = pair ? "Pair" : "Curse", count });
    }

    private static void Benchmark(RenderingDevice rd)
    {
        int batches = int.TryParse(System.Environment.GetEnvironmentVariable("RT2_GPU_SMOKE_BATCHES"), out int n) ? Math.Clamp(n, 2, 512) : 32;
        using var direct = new NeowFamilyGpuExecutor(rd, Plan(true), 42, useIndirectStaging: false);
        using var indirect = new NeowFamilyGpuExecutor(rd, Plan(true), 42, useIndirectStaging: true);
        var input = FamilyCandidateSet.Dense(new SearchBatch(0, NeowFamilyGpuExecutor.Capacity));
        for (int i = 0; i < 2; i++)
        {
            direct.Execute(input, CancellationToken.None, out _);
            indirect.Execute(input, CancellationToken.None, out _);
        }
        Require(indirect.UsesIndirectStaging, "Benchmark requires actual indirect execution.");
        var directMs = new List<double>(); var indirectMs = new List<double>();
        double directDispatch = 0, indirectDispatch = 0, directReadback = 0, indirectReadback = 0;
        long curse = 0, pair = 0, survivors = 0;
        for (int i = 0; i < batches; i++)
        {
            var batch = FamilyCandidateSet.Dense(new SearchBatch((ulong)i * (ulong)NeowFamilyGpuExecutor.Capacity, NeowFamilyGpuExecutor.Capacity));
            NeowFamilyGpuMetrics a, b;
            FamilyCandidateSet ar, br;
            if (i % 2 == 0)
            {
                ar = direct.Execute(batch, CancellationToken.None, out a);
                br = indirect.Execute(batch, CancellationToken.None, out b);
            }
            else
            {
                br = indirect.Execute(batch, CancellationToken.None, out b);
                ar = direct.Execute(batch, CancellationToken.None, out a);
            }
            Require(ar.ExportAbi1().Span.SequenceEqual(br.ExportAbi1().Span) && a.CurseSurvivors == b.CurseSurvivors && a.PairSurvivors == b.PairSurvivors,
                "Benchmark parity/counters mismatch.");
            directMs.Add(a.CanonicalMs); indirectMs.Add(b.CanonicalMs);
            directDispatch += a.DispatchSyncMs; indirectDispatch += b.DispatchSyncMs;
            directReadback += a.ReadbackMs; indirectReadback += b.ReadbackMs;
            curse += b.CurseSurvivors; pair += b.PairSurvivors; survivors += br.Count;
        }
        Emit(new { phase = "benchmark", result = "PASS", batches, batchSize = NeowFamilyGpuExecutor.Capacity,
            directCanonicalMs = directMs.Sum(), indirectCanonicalMs = indirectMs.Sum(),
            directDispatchMs = directDispatch, indirectDispatchMs = indirectDispatch,
            directReadbackMs = directReadback, indirectReadbackMs = indirectReadback,
            canonicalSpeedup = directMs.Sum() / indirectMs.Sum(), curseSurvivors = curse, pairSurvivors = pair, survivors,
            indirect = indirect.StagedDispatchRevision, direct = direct.StagedDispatchRevision,
            evidence = "SyntheticLeafyFixedPair;AlternatingOrder;TwoWarmupsExcluded;HostCanonicalAndDispatchSync;NotGpuTimestamps;NotCalibration" });
    }

    private static FamilyCandidateSet ExecuteSingles(NeowFamilyGpuExecutor executor, SearchBatch batch,
        out NeowFamilyGpuMetrics a, out NeowFamilyGpuMetrics b)
    {
        int split = NeowFamilyGpuExecutor.Capacity;
        var left = executor.Execute(FamilyCandidateSet.Dense(new SearchBatch(batch.BatchBase, split)), CancellationToken.None, out a);
        var right = executor.Execute(FamilyCandidateSet.Dense(new SearchBatch(batch.BatchBase + (ulong)split,
            batch.BatchCandidateCount - split)), CancellationToken.None, out b);
        ulong[] merged = new ulong[left.Count + right.Count];
        left.ExportAbi1().Span.CopyTo(merged);
        for (int i = 0; i < right.Count; i++) merged[left.Count + i] = right.ExportAbi1().Span[i] + (ulong)split;
        return FamilyCandidateSet.FromSortedAbi1(batch, merged);
    }

    private static void TestDoubleWindows(RenderingDevice rd)
    {
        // Use the same Leafy physical path with a permissive synthetic predicate
        // so output from BOTH halves verifies the ordinal offset and sorting.
        var plan = Plan(true);
        plan.Meta[58] = plan.Meta[59] = 1;
        foreach (bool indirect in new[] { false, true })
        {
            using var single = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: indirect);
            using var paired = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: indirect);
            foreach (int count in new[] { NeowFamilyGpuExecutor.Capacity + 1,
                NeowFamilyGpuExecutor.Capacity + 513, NeowFamilyGpuExecutor.DenseWindowBatchCapacity })
            {
                var batch = new SearchBatch(0x100000003, count);
                var reference = ExecuteSingles(single, batch, out var a, out var b);
                var result = paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(batch), CancellationToken.None, out var m);
                Require(reference.ExportAbi1().Span.SequenceEqual(result.ExportAbi1().Span), "Double-window ordinal mapping differs.");
                Require(m.Processed == count && m.Survivors == result.Count && m.Groups == a.Groups + b.Groups &&
                    m.CurseSurvivors == a.CurseSurvivors + b.CurseSurvivors && m.PairSurvivors == a.PairSurvivors + b.PairSurvivors &&
                    m.Dispatches == a.Dispatches + b.Dispatches &&
                    m.ReadbackBytes >= 64L + result.Count * sizeof(uint) &&
                    m.ReadbackBytes <= 64L + result.Count * sizeof(uint) + 2L * NeowSparsePayloadReadback.PrefixWords * sizeof(uint),
                    "Double-window metrics/counters differ from its physical windows.");
                Require(paired.WindowTransportRevision == NeowFamilyGpuExecutor.DoubleWindowRevision,
                    "Double-window transport has the wrong physical revision.");
                if (count == NeowFamilyGpuExecutor.DenseWindowBatchCapacity)
                {
                    bool hasSecondWindowOutput = false;
                    foreach (ulong ordinal in result.ExportAbi1().Span)
                        if (ordinal >= (ulong)NeowFamilyGpuExecutor.Capacity) hasSecondWindowOutput = true;
                    Require(hasSecondWindowOutput, "Synthetic parity case did not exercise second-window survivors.");
                }
            }
            var small = paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(new SearchBatch(0, 513)), CancellationToken.None, out var smallMetrics);
            var smallReference = single.Execute(FamilyCandidateSet.Dense(new SearchBatch(0, 513)), CancellationToken.None, out _);
            Require(small.ExportAbi1().Span.SequenceEqual(smallReference.ExportAbi1().Span) &&
                smallMetrics.Dispatches == (indirect ? 5 : 3) && paired.WindowTransportRevision == NeowFamilyGpuExecutor.SingleWindowRevision,
                "Single tail after paired execution did not retain the original path.");
            Expect<InvalidOperationException>(() => paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(
                new SearchBatch(0, NeowFamilyGpuExecutor.DenseWindowBatchCapacity + 1)), CancellationToken.None, out _));
        }
        using var unsupported = new NeowFamilyGpuExecutor(rd, Plan(false), 42);
        Expect<InvalidOperationException>(() => unsupported.ExecuteDenseWindows(FamilyCandidateSet.Dense(
            new SearchBatch(0, NeowFamilyGpuExecutor.DenseWindowBatchCapacity)), CancellationToken.None, out _));
        Emit(new { phase = "doubleWindowParity", result = "PASS", modes = 2, denseCasesPerMode = 3,
            tailCase = true, secondWindowPayload = true, oversizedAndScopeRejected = true });
    }

    private static void BenchmarkDoubleWindows(RenderingDevice rd)
    {
        int batches = int.TryParse(System.Environment.GetEnvironmentVariable("RT2_GPU_SMOKE_BATCHES"), out int n)
            ? Math.Clamp(n, 2, 512) : 32;
        bool indirect = NeowFamilyGpuExecutor.RequestIndirectStaging;
        using var single = new NeowFamilyGpuExecutor(rd, Plan(true), 42, useIndirectStaging: indirect);
        using var paired = new NeowFamilyGpuExecutor(rd, Plan(true), 42, useIndirectStaging: indirect);
        var warmBatch = new SearchBatch(0, NeowFamilyGpuExecutor.DenseWindowBatchCapacity);
        for (int i = 0; i < 2; i++)
        {
            ExecuteSingles(single, warmBatch, out _, out _);
            paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(warmBatch), CancellationToken.None, out _);
        }
        double singleCanonical = 0, pairedCanonical = 0, singleDispatch = 0, pairedDispatch = 0,
            singleReadback = 0, pairedReadback = 0;
        long outputs = 0, curse = 0, pair = 0;
        for (int i = 0; i < batches; i++)
        {
            var batch = new SearchBatch((ulong)i * (ulong)NeowFamilyGpuExecutor.DenseWindowBatchCapacity,
                NeowFamilyGpuExecutor.DenseWindowBatchCapacity);
            FamilyCandidateSet a, b;
            NeowFamilyGpuMetrics am, bm, m;
            if (i % 2 == 0)
            {
                a = ExecuteSingles(single, batch, out am, out bm);
                b = paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(batch), CancellationToken.None, out m);
            }
            else
            {
                b = paired.ExecuteDenseWindows(FamilyCandidateSet.Dense(batch), CancellationToken.None, out m);
                a = ExecuteSingles(single, batch, out am, out bm);
            }
            Require(a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span), "Paired benchmark parity mismatch.");
            singleCanonical += am.CanonicalMs + bm.CanonicalMs; pairedCanonical += m.CanonicalMs;
            singleDispatch += am.DispatchSyncMs + bm.DispatchSyncMs; pairedDispatch += m.DispatchSyncMs;
            singleReadback += am.ReadbackMs + bm.ReadbackMs; pairedReadback += m.ReadbackMs;
            outputs += b.Count; curse += m.CurseSurvivors; pair += m.PairSurvivors;
        }
        Emit(new { phase = "windowBenchmark", result = "PASS", batches, indirect,
            batchSize = NeowFamilyGpuExecutor.DenseWindowBatchCapacity,
            singleCanonicalMs = singleCanonical, pairedCanonicalMs = pairedCanonical,
            singleDispatchMs = singleDispatch, pairedDispatchMs = pairedDispatch,
            singleReadbackMs = singleReadback, pairedReadbackMs = pairedReadback,
            canonicalSpeedup = singleCanonical / pairedCanonical,
            singleHeaderReads = 2 * batches, pairedHeaderReads = batches,
            survivors = outputs, curseSurvivors = curse, pairSurvivors = pair,
            evidence = "TwoIndependentWorkspaces;OneSubmitSyncAndHeaderRead;AlternatingOrder;TwoWarmupsExcluded;SyntheticLeafyFixedPair;NotCalibration" });
    }

    private static void TestDoubleWindowAllocationFallback(RenderingDevice rd)
    {
        var plan = Plan(true);
        plan.Meta[58] = plan.Meta[59] = 1;
        foreach (bool indirect in new[] { false, true })
        {
            using var cancelled = new CancellationTokenSource();
            int attempts = 0;
            using var fallback = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: indirect,
                beforeDoubleAllocation: () =>
                {
                    attempts++;
                    cancelled.Cancel();
                    throw new OutOfMemoryException("Smoke: optional second workspace unavailable.");
                });
            using var reference = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: indirect);
            var batch = new SearchBatch(0x100000003, NeowFamilyGpuExecutor.DenseWindowBatchCapacity);
            // Simulated OOM happens after mandatory initialization and before
            // any recording. Cancellation must publish no partial window and
            // the same executor must remain usable without another allocation.
            long submittedBefore = FamilyGpuComputeUtility.CompletedSubmissionTicks;
            Expect<OperationCanceledException>(() => fallback.ExecuteDenseWindows(FamilyCandidateSet.Dense(batch), cancelled.Token, out _));
            Require(FamilyGpuComputeUtility.CompletedSubmissionTicks == submittedBefore,
                "Cancelled optional allocation unexpectedly submitted GPU commands.");
            Require(attempts == 1 && fallback.ResidentBytes == reference.ResidentBytes +
                    (NeowFamilyGpuPlan.CurseCapacity * 12L + NeowFamilyGpuPlan.PairCapacity * 48L) + (indirect ? 24 : 0) +
                    (fallback.PrefixHashRevision == NeowFamilyGpuExecutor.PrefixHashCacheRevision ? PrefixHashCache.MaximumTableWords * sizeof(uint) : 0),
                "Fallback retained an optional workspace/header bank.");
            foreach (int count in new[] { NeowFamilyGpuExecutor.DenseWindowBatchCapacity, NeowFamilyGpuExecutor.Capacity + 513 })
            {
                var inputBatch = new SearchBatch(batch.BatchBase, count);
                var expected = ExecuteSingles(reference, inputBatch, out var a, out var b);
                var actual = fallback.ExecuteDenseWindows(FamilyCandidateSet.Dense(inputBatch), CancellationToken.None, out var m);
                Require(expected.ExportAbi1().Span.SequenceEqual(actual.ExportAbi1().Span) &&
                    m.Groups == a.Groups + b.Groups && m.Dispatches == a.Dispatches + b.Dispatches &&
                    m.CurseSurvivors == a.CurseSurvivors + b.CurseSurvivors && m.PairSurvivors == a.PairSurvivors + b.PairSurvivors &&
                    m.ReadbackBytes == a.ReadbackBytes + b.ReadbackBytes && m.Processed == count,
                    "Sequential allocation fallback changed ordinals or checkpoint metrics.");
                Require(attempts == 1 && fallback.ResidentBytes == reference.ResidentBytes &&
                    fallback.WindowTransportRevision == NeowFamilyGpuExecutor.SequentialWindowFallbackRevision,
                    "Fallback reallocated optional resources, retained extra memory, or reused the paired physical revision.");
                if (count == NeowFamilyGpuExecutor.DenseWindowBatchCapacity)
                {
                    bool hasRightPayload = false;
                    foreach (ulong ordinal in actual.ExportAbi1().Span)
                        if (ordinal >= (ulong)NeowFamilyGpuExecutor.Capacity) hasRightPayload = true;
                    Require(hasRightPayload, "Allocation fallback did not exercise right-window payload mapping.");
                }
            }
            var tail = FamilyCandidateSet.Dense(new SearchBatch(batch.BatchBase, 513));
            var tailExpected = reference.Execute(tail, CancellationToken.None, out _);
            var tailActual = fallback.ExecuteDenseWindows(tail, CancellationToken.None, out _);
            Require(tailExpected.ExportAbi1().Span.SequenceEqual(tailActual.ExportAbi1().Span) && attempts == 1 &&
                fallback.WindowTransportRevision == NeowFamilyGpuExecutor.SingleWindowRevision,
                "Single tail was unsafe after allocation fallback.");
        }
        Emit(new { phase = "doubleWindowAllocationFallback", result = "PASS", modes = 2,
            optionalAllocationAttemptsPerMode = 1, cancellationBeforeSubmission = true,
            reusedSingleWorkspace = true, rightPayload = true, physicalRevisionIsolated = true });
    }

    private static void TestNestedInitializationCleanupFailure(RenderingDevice rd)
    {
        Require(!FamilyGpuComputeUtility.HasInitializationCleanupFailure(new OutOfMemoryException("Ordinary allocation failure")),
            "A clean allocation failure must remain eligible for sequential fallback.");
        int attempts = 0;
        using var executor = new NeowFamilyGpuExecutor(rd, Plan(true), 42, useIndirectStaging: false,
            beforeDoubleAllocation: () =>
            {
                attempts++;
                try
                {
                    // Exercise the actual production helper, then wrap its
                    // cleanup failure as constructor/module layers would. Put
                    // it in a non-first AggregateException branch as well.
                    FamilyGpuComputeUtility.CleanupAfterFailure(new OutOfMemoryException("Optional initialization failed"),
                        () => throw new InvalidOperationException("Optional resource release failed"), "Smoke.OptionalInitialization");
                }
                catch (FamilyGpuComputeUtility.InitializationCleanupException cleanupFailure)
                {
                    throw new InvalidOperationException("Smoke.ModuleInitializationFailed",
                        new AggregateException("Smoke.NestedInitialization", new OutOfMemoryException("Primary failure"), cleanupFailure));
                }
                throw new InvalidDataException("The production helper did not preserve cleanup failure.");
            });
        var input = FamilyCandidateSet.Dense(new SearchBatch(0, NeowFamilyGpuExecutor.DenseWindowBatchCapacity));
        long submittedBefore = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Exception? observed = null;
        try { executor.ExecuteDenseWindows(input, CancellationToken.None, out _); }
        catch (AggregateException failure) { observed = failure; }
        Require(observed is not null && FamilyGpuComputeUtility.HasInitializationCleanupFailure(observed),
            "Nested initialization/release failure was swallowed by optional allocation fallback.");
        Require(attempts == 1 && FamilyGpuComputeUtility.CompletedSubmissionTicks == submittedBefore &&
            executor.WindowTransportRevision != NeowFamilyGpuExecutor.SequentialWindowFallbackRevision,
            "Uncertain cleanup submitted or retried work through sequential fallback.");
        Expect<InvalidOperationException>(() => executor.ExecuteDenseWindows(input, CancellationToken.None, out _));
        Require(attempts == 1 && FamilyGpuComputeUtility.CompletedSubmissionTicks == submittedBefore,
            "An executor with uncertain cleanup was reused or attempted optional allocation again.");
        Emit(new { phase = "nestedInitializationCleanupFailure", result = "PASS",
            productionHelper = true, recursivelyWrapped = true, nonFirstAggregateBranch = true,
            failedClosed = true, gpuSubmissions = 0, optionalAllocationAttempts = attempts });
    }
}
