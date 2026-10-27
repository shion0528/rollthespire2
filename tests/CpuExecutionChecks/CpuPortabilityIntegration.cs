using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

namespace CpuExecutionChecks;

// Parameter matrices are simulations. This process only runs on its actual host;
// DOTNET_PROCESSOR_COUNT changes runtime concurrency reporting, not the hardware.
public static class CpuPortabilityIntegration
{
    public static void Run(Action<string> emit)
    {
        int assertions = 0;
        void Require(bool value, string message)
        {
            Interlocked.Increment(ref assertions);
            if (!value) throw new InvalidDataException(message);
        }
        void Report(object value) => emit("RT2_CPU_PORTABILITY " + JsonSerializer.Serialize(value));
        int[] cores = [1, 2, 4, 8, 16, 24, 32, 64, 96, 128];
        foreach (int processors in cores)
        {
            SearchResourceBudget defaults = SearchWorkspacePersistence.ResolveSearchResources(null, 100, null, processors);
            Require(defaults == new SearchResourceBudget(50, 100), "New-machine defaults must remain percentages.");
            SearchResourceBudget explicitBudget = SearchWorkspacePersistence.ResolveSearchResources(40, 0, 64, processors);
            Require(explicitBudget == new SearchResourceBudget(40, 0), "A saved percentage must take precedence over stale worker counts.");
            foreach (int requested in new[] { 1, 2, 4, 16, 32, 63, 64 })
            {
                int full = Math.Min(requested, new SearchResourceBudget(100).ResolveCpuWorkers(processors));
                Require(full == Math.Min(requested, Math.Min(processors, 64)), "Full CPU budget exceeded its caller/hardware/runtime ceiling.");
            }
        }

        // These expected values are explicit migration outcomes rather than a
        // second implementation of the production migration formula.
        (int Workers, int Cores, int Percent, int Effective)[] migrations =
        [
            (16, 1, 100, 1), (16, 2, 100, 2), (16, 4, 100, 4),
            (16, 8, 100, 8), (16, 16, 100, 16), (16, 24, 66, 16),
            (16, 32, 50, 16), (16, 64, 25, 16), (16, 96, 16, 16), (16, 128, 12, 16),
            (63, 128, 49, 63), (64, 128, 50, 64), (1, 128, 1, 2),
            (0, 32, 3, 1), (65, 96, 66, 64)
        ];
        foreach (var migration in migrations)
        {
            SearchResourceBudget actual = SearchWorkspacePersistence.ResolveSearchResources(null, 100, migration.Workers, migration.Cores);
            Require(actual.CpuPercent == migration.Percent && actual.ResolveCpuWorkers(migration.Cores) == migration.Effective,
                $"Legacy preference migration changed for workers={migration.Workers},cores={migration.Cores}.");
        }
        SearchResourceBudget migratedOnOldMachine = SearchWorkspacePersistence.ResolveSearchResources(null, 100, 16, 32);
        SearchResourceBudget copiedAfterMigration = SearchWorkspacePersistence.ResolveSearchResources(migratedOnOldMachine.CpuPercent, 100, 16, 64);
        Require(copiedAfterMigration.CpuPercent == 50 && copiedAfterMigration.ResolveCpuWorkers(64) == 32,
            "A previously migrated percentage did not scale on the new machine.");
        SearchResourceBudget copiedBeforeMigration = SearchWorkspacePersistence.ResolveSearchResources(null, 100, 16, 64);
        Require(copiedBeforeMigration.CpuPercent == 25 && copiedBeforeMigration.ResolveCpuWorkers(64) == 16,
            "An unmigrated worker preference did not retain its count on the new machine.");
        Report(new { phase = "preferenceMatrix", result = "PASS", simulatedLogicalProcessors = cores,
            legacyMigrationCases = migrations.Length,
            beforeMigrationCopy = new { cpuPercent = copiedBeforeMigration.CpuPercent, workers = copiedBeforeMigration.ResolveCpuWorkers(64) },
            afterMigrationCopy = new { cpuPercent = copiedAfterMigration.CpuPercent, workers = copiedAfterMigration.ResolveCpuWorkers(64) },
            integerGranularityExample = new { logicalProcessors = 128, savedWorkers = 1, cpuPercent = 1, effectiveWorkers = 2 } });

        int reported = Environment.ProcessorCount;
        string? overrideText = Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT");
        if (int.TryParse(overrideText, out int overridden)) Require(reported == overridden, "Runtime did not honor DOTNET_PROCESSOR_COUNT.");
        bool intrinsicsDisabled = Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") == "0" ||
            Environment.GetEnvironmentVariable("COMPlus_EnableHWIntrinsic") == "0";
        if (intrinsicsDisabled)
            Require(!Vector.IsHardwareAccelerated && !Avx2.IsSupported && !Sse2.IsSupported,
                "Requested scalar runtime did not disable hardware intrinsics.");

        // A 256-root input admits only two production partitions. Thus a
        // simulated 64-core runtime tests P63/P64 without creating 64 busy jobs.
        var batch = new SearchBatch(1234567, 256);
        FamilyCandidateSet input = FamilyCandidateSet.Dense(batch);
        bool Match(ulong root) => (root ^ (root >> 17)) % 5 == 0;
        ulong[] expected = input.EnumerateLogicalOrdinals().Where(o => Match(Beta111Profile.Instance.ComputeRootSeed(
            VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, batch.GlobalCandidate(o))))).ToArray();
        var effective = new List<object>();
        int maximumActive = 0, maximumThreads = 0;
        ThreadPool.GetMinThreads(out int previousMin, out int previousIo);
        ThreadPool.SetMinThreads(Math.Max(4, previousMin), previousIo);
        try
        {
            foreach (int requested in new[] { 1, 2, 4, 63, 64 })
            {
                var request = Request(requested, new SearchResourceBudget(100, 0));
                var cpu = new FamilyCpuExecution(request, "Check.Portable", "Check.Portable.v1", workers: 1, numericalRoots: true);
                Require(cpu.Workers == Math.Min(requested, Math.Min(reported, 64)), "Actual FamilyCpuExecution ignored reported processor count or runtime cap.");
                var threads = new ConcurrentDictionary<int, byte>();
                int active = 0, peak = 0;
                using var firstThreads = new CountdownEvent(Math.Min(cpu.Workers, 2));
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var ceiling = new SearchCpuConcurrencyBudget(2);
                using var scope = ceiling.EnterScope();
                FamilyCandidateSet result = cpu.Execute(new(batch, request), input, cancellation.Token, root =>
                {
                    int current = Interlocked.Increment(ref active);
                    int before;
                    do { before = Volatile.Read(ref peak); }
                    while (current > before && Interlocked.CompareExchange(ref peak, current, before) != before);
                    try
                    {
                        Require(current <= 2, "Short portability probe started too much simultaneous CPU work.");
                        if (threads.TryAdd(Environment.CurrentManagedThreadId, 0))
                        {
                            firstThreads.Signal();
                            Require(firstThreads.Wait(TimeSpan.FromSeconds(5), cancellation.Token), "Two real production partitions did not rendezvous.");
                        }
                        Thread.SpinWait(64);
                        return Match(root);
                    }
                    finally { Interlocked.Decrement(ref active); }
                });
                Require(result.ExportAbi1().Span.SequenceEqual(expected), "Portable CPU execution changed roots, order, or output.");
                Require(active == 0 && peak <= 2 && (cpu.Workers == 1 || threads.Count == 2), "Expected short production parallelism was not exercised.");
                maximumActive = Math.Max(maximumActive, peak);
                maximumThreads = Math.Max(maximumThreads, threads.Count);
                effective.Add(new { requested, workers = cpu.Workers, actualPartitions = threads.Count });
            }
            foreach (int workers in new[] { 63, 64 })
            {
                var legacyRequest = Request(64, null);
                var cpu = new FamilyCpuExecution(legacyRequest, "Check.Upper", "Check.Upper.v1", workers: workers, numericalRoots: true);
                Require(cpu.Workers == workers, "Legacy donor configuration rejected the 63/64 worker boundary.");
                Require(cpu.Execute(new(batch, legacyRequest), input, CancellationToken.None, Match).ExportAbi1().Span.SequenceEqual(expected),
                    "Upper-bound worker configuration changed execution results.");
            }
            try
            {
                _ = new FamilyCpuExecution(Request(64, null), "Check.Invalid", "Check.Invalid.v1", workers: 65);
                throw new InvalidDataException("Family CPU accepted an unsupported worker count.");
            }
            catch (ArgumentOutOfRangeException) { assertions++; }
        }
        finally { ThreadPool.SetMinThreads(previousMin, previousIo); }
        Report(new { phase = "actualRuntime", result = "PASS", architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            reportedLogicalProcessors = reported, processorCountOverride = overrideText,
            hardwareIntrinsicsDisabled = intrinsicsDisabled, vectorHardwareAccelerated = Vector.IsHardwareAccelerated,
            avx2Supported = Avx2.IsSupported, maximumActive, maximumThreads, configurations = effective,
            simulationOnly = overrideText is not null });
        Report(new { phase = "complete", result = "PASS", assertions });
    }

    private static ExactSearchExecutionRequest Request(int workers, SearchResourceBudget? resources)
    {
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "PortabilityChecks", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "PortabilityChecks")), false, "PortabilityChecks");
        return new(compiled, new SearchRunOptions("000000000000", 256, 1, workers) { Resources = resources },
            "000000000000", 256, ExactSearchEvaluationProjection.Empty, CombatRewardRoutePolicyContract.None, "PortabilityChecks");
    }
}
