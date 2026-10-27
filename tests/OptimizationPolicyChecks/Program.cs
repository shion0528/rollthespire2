using System.Reflection;
using System.Text.Json;
using Godot;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Diagnostics;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;

namespace OptimizationPolicyChecks;

internal static partial class Program
{
    private static int _assertions;
    private static void Require(bool condition, string issue)
    {
        Interlocked.Increment(ref _assertions);
        if (!condition) throw new InvalidDataException(issue);
    }
    private static void Report(object value) => Console.WriteLine("RT2_OPTIMIZATION_POLICY " + JsonSerializer.Serialize(value));

    private static async Task Main()
    {
        TestImmutableSettings();
        TestNestedRestoration();
        TestHeaderTransportIdentity();
        TestCpuQuoteScope();
        TestPlanIdentityCapture();
        TestNeowSharedPrefixPhysicalIdentity();
        await TestAsyncIsolation();
        await TestGpuWorkItemCaptureWithoutGpu();
        await TestConcurrentSessions();
        Require(SearchOptimizationScope.Current == SearchOptimizationSettings.AllEnabled,
            "A completed test leaked an optimization policy into its caller.");
        Report(new { phase = "complete", result = "PASS", assertions = _assertions,
            all128Settings = true, concurrentCpuSessions = true,
            realGpuWorkItemWithoutDevice = true, gpuDeviceCreated = false });
    }

    private static SearchOptimizationSettings Settings(int bits) => new()
    {
        PrefixCache = (bits & 1) != 0,
        ExactDrawIntervals = (bits & 2) != 0,
        LeafyLazyInitialization = (bits & 4) != 0,
        CostOrderedFilters = (bits & 8) != 0,
        GroupedOutput = (bits & 16) != 0,
        AsyncHeaderReadback = (bits & 32) != 0,
        DoubleWindowBatching = (bits & 64) != 0
    };

    private static void TestImmutableSettings()
    {
        Require(new SearchOptimizationSettings() == SearchOptimizationSettings.AllEnabled &&
            Settings(0) == SearchOptimizationSettings.AllDisabled &&
            SearchOptimizationSettings.AllEnabled.Key == "SearchOptimizations.v2.1111111" &&
            SearchOptimizationSettings.AllDisabled.Key == "SearchOptimizations.v2.0000000",
            "Optimization defaults or stable key spelling changed.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int bits = 0; bits < 128; bits++)
        {
            var settings = Settings(bits);
            Require(keys.Add(settings.Key), "Two distinct optimization combinations share a physical key.");
            string json = JsonSerializer.Serialize(settings);
            Require(!json.Contains("\"Key\"", StringComparison.Ordinal) &&
                JsonSerializer.Deserialize<SearchOptimizationSettings>(json) == settings,
                "Optimization settings did not round-trip independently of their computed key.");
            var options = new SearchRunOptions("000000000000", 17, 1, 2) { Optimizations = settings };
            Require(options.Optimizations == settings &&
                (options with { TargetMatchCount = 3 }).Optimizations == settings,
                "Cloning run options discarded their optimization snapshot.");
        }
        Require(JsonSerializer.Deserialize<SearchOptimizationSettings>("{}") == SearchOptimizationSettings.AllEnabled &&
            new SearchRunOptions("000000000000", 1, 1, 1).Optimizations == SearchOptimizationSettings.AllEnabled &&
            new SearchRunOptions("000000000000", 1, 1, 1) { Optimizations = null! }.Optimizations == SearchOptimizationSettings.AllEnabled,
            "Older/missing/null settings did not keep the all-enabled default.");
        Require(JsonSerializer.Deserialize<SearchOptimizationSettings>(
                "{\"PrefixCache\":false,\"ExactDrawIntervals\":false,\"LeafyLazyInitialization\":false," +
                "\"CostOrderedFilters\":false,\"GroupedOutput\":false,\"AsyncHeaderReadback\":false}") == Settings(64),
            "A saved six-switch policy failed to default its newly added double-window switch to enabled.");
        Report(new { phase = "immutableSettings", result = "PASS", combinations = keys.Count });
    }

    private static void TestNestedRestoration()
    {
        var original = SearchOptimizationScope.Current;
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled))
        {
            Require(SearchOptimizationScope.Current == SearchOptimizationSettings.AllDisabled, "An outer scope did not enter.");
            var restore = SearchOptimizationScope.Enter(Settings(21));
            Require(SearchOptimizationScope.Current == Settings(21), "A nested scope did not enter.");
            restore.Dispose();
            Require(SearchOptimizationScope.Current == SearchOptimizationSettings.AllDisabled, "A nested scope did not restore.");
            restore.Dispose();
            Require(SearchOptimizationScope.Current == SearchOptimizationSettings.AllDisabled, "Double disposal changed an unrelated scope.");
            try
            {
                using var throwing = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled);
                throw new InvalidOperationException("ExpectedScopeProbe");
            }
            catch (InvalidOperationException ex) when (ex.Message == "ExpectedScopeProbe") { }
            Require(SearchOptimizationScope.Current == SearchOptimizationSettings.AllDisabled, "An exception bypassed restoration.");
            using var context = new AsyncContextDisposer(new FamilyExecutionContext(false));
            Require(context.Context.Optimizations == SearchOptimizationSettings.AllDisabled,
                "An execution context failed to capture its creating scope.");
            using var explicitContext = new AsyncContextDisposer(new FamilyExecutionContext(false,
                optimizations: SearchOptimizationSettings.AllEnabled));
            Require(explicitContext.Context.Optimizations == SearchOptimizationSettings.AllEnabled &&
                SearchOptimizationScope.Current == SearchOptimizationSettings.AllDisabled,
                "Explicit context creation leaked its policy into the creating flow.");
        }
        Require(SearchOptimizationScope.Current == original, "The outer scope failed to restore its caller.");
        Report(new { phase = "nestedScopes", result = "PASS", exceptionAndDoubleDispose = true });
    }

    private sealed class AsyncContextDisposer(FamilyExecutionContext context) : IDisposable
    {
        internal FamilyExecutionContext Context => context;
        public void Dispose() => context.DisposeAsync().GetAwaiter().GetResult();
    }

    private static async Task TestAsyncIsolation()
    {
        var original = SearchOptimizationScope.Current;
        await Task.WhenAll(Enumerable.Range(0, 128).Select(async bits =>
        {
            var expected = Settings(bits);
            using var scope = SearchOptimizationScope.Enter(expected);
            for (int pass = 0; pass < 3; pass++)
            {
                await Task.Yield();
                Require(SearchOptimizationScope.Current == expected, "An interleaved async flow borrowed another policy.");
                await Task.Run(() => Require(SearchOptimizationScope.Current == expected,
                    "A CPU Task.Run lost its optimization scope."));
            }
        }));
        Require(SearchOptimizationScope.Current == original, "Async children changed their caller's policy.");
        Report(new { phase = "asyncIsolation", result = "PASS", concurrentFlows = 128 });
    }

    private static object WorkItem(Func<RenderingDevice, SearchOptimizationSettings> action, CancellationToken token)
    {
        Type owner = typeof(FamilyExecutionContext).Assembly.GetType(
            "RolltheSpire2.Search.FamilyExecution.FamilyGpuExecutionOwner", throwOnError: true)!;
        Type work = owner.GetNestedType("WorkItem`1", BindingFlags.NonPublic)!
            .MakeGenericType(typeof(SearchOptimizationSettings));
        return Activator.CreateInstance(work, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [action, token], culture: null)!;
    }

    private static Task<SearchOptimizationSettings> WorkTask(object item) =>
        (Task<SearchOptimizationSettings>)item.GetType().GetProperty("Task")!.GetValue(item)!;

    private static async Task TestGpuWorkItemCaptureWithoutGpu()
    {
        foreach (var expected in Enumerable.Range(0, 128).Select(Settings))
        {
            object item;
            using (SearchOptimizationScope.Enter(expected))
                item = WorkItem(_ => SearchOptimizationScope.Current, CancellationToken.None);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var other = SearchOptimizationScope.Enter(Settings(42));
                    // Execute the actual production work-item wrapper. Its
                    // delegate never reads this null argument; no native object,
                    // device, GPU owner or rendering API is constructed/called.
                    item.GetType().GetMethod("Execute")!.Invoke(item, [null]);
                    Require(SearchOptimizationScope.Current == Settings(42), "A GPU work item did not restore its owner's prior scope.");
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.Start();
            Require(thread.Join(TimeSpan.FromSeconds(5)), "The fake-owner scope probe timed out.");
            if (failure is not null) throw failure;
            Require(await WorkTask(item) == expected, "A queued production GPU action used its owner's inherited policy.");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        object skipped;
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled))
            skipped = WorkItem(_ => throw new InvalidDataException("A cancelled work item ran."), cancelled.Token);
        skipped.GetType().GetMethod("Execute")!.Invoke(skipped, [null]);
        Require(WorkTask(skipped).IsCanceled && SearchOptimizationScope.Current == SearchOptimizationSettings.AllEnabled,
            "A cancelled GPU work item ran or leaked its captured policy.");
        Report(new { phase = "gpuWorkItemCapture", result = "PASS", realWorkItem = true,
            combinations = 128, nativeGpuOwnerNotConstructed = true, capturedBeforeQueue = true, ownerScopeRestored = true });
    }

    private static ExactSearchExecutionRequest Request(SearchOptimizationSettings settings)
    {
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "OptimizationPolicyChecks", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "OptimizationPolicyChecks")), false,
            "OptimizationPolicyChecks");
        string seed = VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, 1000);
        return new(compiled, new SearchRunOptions(seed, 31, 1, 4)
            { Resources = new SearchResourceBudget(100, 0), Optimizations = settings }, seed, 31,
            ExactSearchEvaluationProjection.Empty, CombatRewardRoutePolicyContract.None, "OptimizationPolicyChecks");
    }

    private static async Task TestConcurrentSessions()
    {
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var families = new List<PolicyFamily>();
        var sessions = new List<ProductionSearchSession>();
        foreach (var expected in new[] { SearchOptimizationSettings.AllDisabled, SearchOptimizationSettings.AllEnabled })
        {
            var request = Request(expected);
            var family = new PolicyFamily(expected, async token =>
            {
                if (Interlocked.Increment(ref entered) == 2) joined.TrySetResult();
                await joined.Task.WaitAsync(token);
            });
            families.Add(family);
            // Deliberately construct each session under the opposite ambient
            // policy. Its explicit request must own every async/CPU descendant.
            using var opposite = SearchOptimizationScope.Enter(expected == SearchOptimizationSettings.AllEnabled
                ? SearchOptimizationSettings.AllDisabled : SearchOptimizationSettings.AllEnabled);
            var plan = FamilyPlanner.InOrder([family]);
            var eta = new FamilySearchEtaProjectionV1(FamilySearchEtaStatus.Unavailable, null, "OptimizationPolicyChecks", plan,
                request.TargetMatchCount, request.ScanCount, null, null, null, null, null, null, null, null, null, null, null, [], []);
            sessions.Add(new(request, plan, eta, 7, 1, null, "", exactEvaluator: (r, _) =>
            {
                Require(SearchOptimizationScope.Current == expected, "A parallel Exact action inherited another session's policy.");
                Interlocked.Increment(ref family.ExactCalls);
                return new(null, null, r.Authority, SearchQueryEvaluation.NoMatch(""), "");
            }));
        }
        try
        {
            await Task.WhenAll(sessions.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            for (int at = 0; at < sessions.Count; at++)
            {
                Require(sessions[at].GetProgress().State == SearchRunState.Completed &&
                    sessions[at].GetProgress().ScannedCount == 31 && families[at].ExactCalls == 31 &&
                    sessions[at].GetSafeNextOrdinal() == 1031 && families[at].Disposed,
                    "A scoped CPU session changed result count, range completion or disposal.");
            }
        }
        finally { foreach (var session in sessions) await session.DisposeAsync(); }
        Report(new { phase = "concurrentSessions", result = "PASS", sessions = sessions.Count,
            rootsPerSession = 31, explicitRequestWins = true, exactTasksAndDisposeScoped = true });
    }

    private sealed class PolicyFamily(SearchOptimizationSettings expected, Func<CancellationToken, Task> join) : IFamilyInvocation
    {
        private int _calls;
        internal int ExactCalls;
        internal bool Disposed;
        public string FamilyId => "Check.OptimizationPolicy";
        public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId, 8192, [], "OptimizationScopeOnly");
        public FamilySurvivalProjection Survival => FamilySurvivalProjection.Unresolved(FamilyId, "SyntheticIdentity");
        public FamilyConditionPerformanceProjection ConditionPerformance => new(FamilyId,
            "Check.OptimizationPolicy.Cpu.v1", "Check.OptimizationPolicy.v1", 1, "ScopeOnly", usesGpu: false);
        public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow observation,
            FamilyCandidateSet input, CancellationToken token)
        {
            Require(context.Optimizations == expected && SearchOptimizationScope.Current == expected,
                "A Family entered with another session's settings.");
            if (Interlocked.Increment(ref _calls) == 1) await join(token);
            await Task.Yield();
            Require(SearchOptimizationScope.Current == expected, "A CPU Family lost its settings after awaiting.");
            return input;
        }
        public FamilyPerformanceObservation CapturePerformanceObservation() => new(ConditionPerformance, "CPU", 0, 0, 0,
            Volatile.Read(ref _calls), 0, 0, 0, 0, false, "ScopeOnly");
        public async ValueTask DisposeAsync(FamilyExecutionContext context)
        {
            await Task.Yield();
            Require(context.Optimizations == expected && SearchOptimizationScope.Current == expected,
                "Async Family disposal did not retain its session's settings.");
            Disposed = true;
        }
    }
}
