using System.Collections.Concurrent;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;

namespace CpuExecutionChecks;

// Controllable physical Family/Exact seams exercise the real production session,
// channels, CPU scopes, cancellation, completion bookkeeping and safe cursor.
public static class ProductionSessionIntegration
{
    public static void Run(Action<string> emit) => RunAsync(emit).WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string> emit)
    {
        int assertions = 0;
        void Require(bool condition, string message)
        {
            Interlocked.Increment(ref assertions);
            if (!condition) throw new InvalidDataException(message);
        }
        void Report(object value) => emit("RT2_SESSION_INTEGRATION " + JsonSerializer.Serialize(value));
        async Task Finish(ProductionSearchSession session) => await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        static ulong Ordinal(TrustedRootHashInput input)
        {
            if (!VisibleSeedCandidateCodec.TryParseOrdinal(Beta111Profile.Instance, input.CanonicalSeed, out ulong ordinal, out _, out string issue))
                throw new InvalidDataException(issue);
            return ordinal;
        }
        static ProductionExactSearchResult Reject(ExactSearchExecutionRequest request) =>
            new(null, null, request.Authority, SearchQueryEvaluation.NoMatch(""), "");

        // A complete multi-window scan must evaluate each survivor exactly once,
        // count rejected roots too, and commit a continuous end cursor.
        const ulong start = 1234567;
        const int count = 2057, window = 257;
        var request = Request(start, count, 32);
        var evaluated = new ConcurrentDictionary<ulong, byte>();
        var filtered = new ControlledFamily((_, input, _) => FamilyCandidateSet.FromAbi1(input.Batch,
            input.EnumerateLogicalOrdinals().Where(o => input.Batch.GlobalCandidate(o) % 5 == 1).ToArray()));
        await using (var session = Session(request, filtered, window, (r, input) =>
        {
            Require(evaluated.TryAdd(Ordinal(input), 0), "A completed session evaluated a root twice.");
            Thread.SpinWait(500);
            return Reject(r);
        }))
        {
            ulong previous = start;
            while (!session.Completion.IsCompleted)
            {
                if (session.TryGetSafeNextOrdinal(out ulong current))
                {
                    Require(current >= previous && current <= start + count, "Live safe cursor moved backward or beyond the root range.");
                    previous = current;
                }
                await Task.Delay(1);
            }
            await Finish(session);
            var progress = session.GetProgress();
            Require(progress.State == SearchRunState.Completed && progress.ScannedCount == count && progress.MatchCount == 0,
                "Completed multi-batch scan has incorrect progress.");
            Require(session.GetSafeNextOrdinal() == start + count, "Completed scan did not commit its exact end cursor.");
            ulong[] expected = Enumerable.Range(0, count).Select(i => start + (ulong)i).Where(o => o % 5 == 1).ToArray();
            Require(expected.SequenceEqual(evaluated.Keys.Order()), "The actual Exact calls skipped or added a filtered root.");
            Require(!session.TryReadCandidate(out _), "NoMatch Exact evaluator published a result.");
        }
        Require(filtered.DisposeCount == 1, "Family resources were not disposed exactly once.");
        AssertRanges(filtered.Ranges, start, count, window, Require);
        Report(new { phase = "completeScan", result = "PASS", roots = count, exactCalls = evaluated.Count, batches = filtered.Ranges.Count });

        // Empty terminal batches must bypass the supplied evaluator entirely.
        var zero = new ControlledFamily((_, input, _) => FamilyCandidateSet.FromAbi1(input.Batch, []));
        int zeroExactCalls = 0;
        await using (var session = Session(Request(start, 1031, 32), zero, 128, (r, _) =>
        {
            Interlocked.Increment(ref zeroExactCalls);
            return Reject(r);
        }))
        {
            await Finish(session);
            Require(session.GetProgress().State == SearchRunState.Completed && session.GetProgress().ScannedCount == 1031,
                "Zero-survivor batches did not complete/count every rejected root.");
            Require(zeroExactCalls == 0 && session.GetSafeNextOrdinal() == start + 1031,
                "Zero-survivor path invoked Exact or lost its safe cursor.");
        }
        AssertRanges(zero.Ranges, start, 1031, 128, Require);
        Require(zero.DisposeCount == 1, "Zero-survivor Family was not disposed.");
        Report(new { phase = "zeroSurvivors", result = "PASS", roots = 1031, batches = zero.Ranges.Count });

        // The earliest survivor remains unfinished while later survivors finish.
        // Cancel must leave that root resumable, even with producer lookahead.
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        int parallelLater = Environment.ProcessorCount > 1 ? 1 : 0;
        using var laterFinished = new CountdownEvent(parallelLater);
        var cancelledCompleted = new ConcurrentDictionary<ulong, byte>();
        bool Keep(ulong global) => (global - start) % 128 is 5 or 29 or 61 or 99;
        var cancellationFamily = new ControlledFamily((_, input, _) => FamilyCandidateSet.FromAbi1(input.Batch,
            input.EnumerateLogicalOrdinals().Where(o => Keep(input.Batch.GlobalCandidate(o))).ToArray()));
        ulong resume;
        await using (var session = Session(Request(start, 1024, 32), cancellationFamily, 128, (r, input) =>
        {
            ulong ordinal = Ordinal(input);
            if (ordinal == start + 5)
            {
                firstEntered.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation probe was never released.");
                throw new OperationCanceledException(new CancellationToken(true));
            }
            cancelledCompleted.TryAdd(ordinal, 0);
            if (laterFinished.CurrentCount > 0)
                try { laterFinished.Signal(); } catch (InvalidOperationException) { }
            return Reject(r);
        }))
        {
            try
            {
                Require(firstEntered.Wait(TimeSpan.FromSeconds(5)), "Cancellation probe never reached its first Exact candidate.");
                Require(laterFinished.Wait(TimeSpan.FromSeconds(5)), "Later Exact candidate did not finish out of order.");
                session.Cancel();
            }
            finally { releaseFirst.Set(); }
            await Finish(session);
            resume = session.GetSafeNextOrdinal();
            Require(session.GetProgress().State == SearchRunState.Cancelled, "Cancelled session has the wrong state.");
            Require(resume == start + 5 && !cancelledCompleted.ContainsKey(resume),
                "Cancellation skipped the first unfinished survivor.");
        }
        var resumedCalls = new ConcurrentDictionary<ulong, byte>();
        var resumeFamily = new ControlledFamily((_, input, _) => FamilyCandidateSet.FromAbi1(input.Batch,
            input.EnumerateLogicalOrdinals().Where(o => Keep(input.Batch.GlobalCandidate(o))).ToArray()));
        await using (var session = Session(Request(resume, (int)(start + 1024 - resume), 32), resumeFamily, 128, (r, input) =>
        {
            resumedCalls.TryAdd(Ordinal(input), 0);
            return Reject(r);
        }))
        {
            await Finish(session);
            Require(session.GetProgress().State == SearchRunState.Completed && session.GetSafeNextOrdinal() == start + 1024,
                "Resuming the cancelled cursor did not reach the original range end.");
            ulong[] expected = Enumerable.Range(0, 1024).Select(i => start + (ulong)i).Where(Keep).ToArray();
            Require(resumedCalls.ContainsKey(resume) && expected.SequenceEqual(cancelledCompleted.Keys.Union(resumedCalls.Keys).Order()),
                "Cancellation/resume lost an unfinished candidate.");
        }
        Report(new { phase = "cancelResume", result = "PASS", safeCursor = resume,
            completedBeforeCancel = cancelledCompleted.Count, resumedExactCalls = resumedCalls.Count });

        // Exact faults must dominate the cancellation used to drain the pipeline.
        var faultFamily = new ControlledFamily((_, input, _) => input);
        await using (var session = Session(Request(start, 2048, 32), faultFamily, 128, (_, input) =>
        {
            if (Ordinal(input) == start) throw new InvalidOperationException("SessionExactProbe");
            return Reject(request);
        }))
        {
            await Finish(session);
            Require(session.GetProgress().State == SearchRunState.Faulted && session.GetProgress().FailureCode.Contains("SessionExactProbe", StringComparison.Ordinal),
                "An Exact exception was lost during pipeline cancellation.");
            Require(session.GetSafeNextOrdinal() == start, "Faulted first Exact root was committed as completed.");
        }
        Require(faultFamily.DisposeCount == 1, "Faulted session did not drain/dispose its Family.");
        Report(new { phase = "exactFault", result = "PASS" });

        try
        {
            await using var impossible = new FamilyExecutionContext(requiresGpu: true, resources: new SearchResourceBudget(100, 0));
            throw new InvalidDataException("GPU-disabled budget admitted a GPU owner.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "GpuExecutionDisabledByResourceBudget") { assertions++; }
        Report(new { phase = "gpuDisabled", result = "PASS", deviceCreated = false });

        // Actual FamilyCpuExecution partitions and Production Exact share the
        // context's scope. Missing scope on either side would exceed this ceiling.
        var sharedRequest = Request(start, 769, 2);
        int active = 0, peak = 0, familyActive = 0, exactActive = 0, overlap = 0, exactCalls = 0;
        using var exactStarted = new ManualResetEventSlim();
        using var secondFamilyStarted = new ManualResetEventSlim();
        void Enter(bool family)
        {
            if (family) Interlocked.Increment(ref familyActive); else Interlocked.Increment(ref exactActive);
            int current = Interlocked.Increment(ref active);
            int before;
            do { before = Volatile.Read(ref peak); }
            while (current > before && Interlocked.CompareExchange(ref peak, current, before) != before);
            if (Volatile.Read(ref familyActive) > 0 && Volatile.Read(ref exactActive) > 0) Interlocked.Exchange(ref overlap, 1);
            Require(current <= 2, "Production Family and Exact scopes exceeded their shared worker budget.");
        }
        void Exit(bool family)
        {
            Interlocked.Decrement(ref active);
            if (family) Interlocked.Decrement(ref familyActive); else Interlocked.Decrement(ref exactActive);
        }
        var realCpu = new FamilyCpuExecution(sharedRequest, "Check.SessionCpu", "Check.SessionCpu.v1", numericalRoots: true);
        var sharedFamily = new ControlledFamily((observation, input, token) =>
        {
            bool second = input.Batch.BatchBase >= start + 256;
            if (second && Environment.ProcessorCount > 1)
                Require(exactStarted.Wait(TimeSpan.FromSeconds(5), token), "First Exact work never started.");
            return realCpu.Execute(observation, input, token, _ =>
            {
                Enter(true);
                try
                {
                    if (second) secondFamilyStarted.Set();
                    Thread.SpinWait(5000);
                    return true;
                }
                finally { Exit(true); }
            });
        });
        await using (var session = Session(sharedRequest, sharedFamily, 256, (r, input) =>
        {
            Enter(false);
            try
            {
                Interlocked.Increment(ref exactCalls);
                if (Ordinal(input) == start && Environment.ProcessorCount > 1)
                {
                    exactStarted.Set();
                    Require(secondFamilyStarted.Wait(TimeSpan.FromSeconds(5)), "Family and Exact did not overlap.");
                }
                Thread.Sleep(1);
                return Reject(r);
            }
            finally { Exit(false); }
        }))
        {
            await Finish(session);
            Require(session.GetProgress().State == SearchRunState.Completed && session.GetProgress().ScannedCount == 769,
                "Shared-budget session did not complete all roots.");
            Require(session.GetSafeNextOrdinal() == start + 769 && exactCalls == 769 && active == 0 && peak <= 2,
                "Shared-budget session lost work or leaked active execution.");
            Require(overlap == 1 || Environment.ProcessorCount == 1, "Shared-budget check did not exercise cross-batch overlap.");
        }
        Report(new { phase = "productionSharedBudget", result = "PASS", peak, overlap = overlap == 1, exactCalls });
        Report(new { phase = "complete", result = "PASS", lifecycleCases = 6, assertions });
    }

    private static ExactSearchExecutionRequest Request(ulong start, int count, int workers)
    {
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "SessionChecks", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "SessionChecks")), false, "SessionChecks");
        string seed = VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, start);
        return new(compiled, new SearchRunOptions(seed, count, 1, workers) { Resources = new SearchResourceBudget(100, 0) },
            seed, count, ExactSearchEvaluationProjection.Empty, CombatRewardRoutePolicyContract.None, "SessionChecks");
    }

    private static ProductionSearchSession Session(ExactSearchExecutionRequest request, IFamilyInvocation family, int window,
        Func<ExactSearchExecutionRequest, TrustedRootHashInput, ProductionExactSearchResult> exact)
    {
        FamilyExecutionPlan plan = FamilyPlanner.InOrder([family]);
        var eta = new FamilySearchEtaProjectionV1(FamilySearchEtaStatus.Unavailable, null, "SyntheticLifecycleProbe", plan,
            request.TargetMatchCount, request.ScanCount, null, null, null, null, null, null, null, null, null, null, null, [], []);
        return new(request, plan, eta, window, 1, null, "", exactEvaluator: exact);
    }

    private static void AssertRanges(ConcurrentQueue<SearchBatch> ranges, ulong start, int count, int window, Action<bool, string> require)
    {
        ulong next = start;
        foreach (SearchBatch batch in ranges)
        {
            require(batch.BatchBase == next && batch.BatchCandidateCount == Math.Min(window, count - (int)(next - start)),
                "Family root batches have a gap, overlap, or wrong final partial count.");
            next += (ulong)batch.BatchCandidateCount;
        }
        require(next == start + (ulong)count, "Family did not receive the complete configured scan range.");
    }

    private sealed class ControlledFamily(Func<FamilyObservationWindow, FamilyCandidateSet, CancellationToken, FamilyCandidateSet> filter) : IFamilyInvocation
    {
        private long _inputs, _outputs;
        private int _calls, _disposed;
        internal ConcurrentQueue<SearchBatch> Ranges { get; } = new();
        internal int DisposeCount => Volatile.Read(ref _disposed);
        public string FamilyId => "Check.Session";
        public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId, 8192, [], "ControlledIntegrationFamily");
        public FamilySurvivalProjection Survival => FamilySurvivalProjection.Unresolved(FamilyId, "SyntheticPredicate");
        public FamilyConditionPerformanceProjection ConditionPerformance => new(FamilyId, "Check.Session.Cpu.v1", "Check.Session.v1", 1, "IntegrationOnly", usesGpu: false);
        public ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow observation,
            FamilyCandidateSet input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Ranges.Enqueue(input.Batch);
            FamilyCandidateSet output = filter(observation, input, token);
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _inputs, input.Count);
            Interlocked.Add(ref _outputs, output.Count);
            return ValueTask.FromResult(output);
        }
        public FamilyPerformanceObservation CapturePerformanceObservation() => new(ConditionPerformance, "CPU", 0, 0, 0,
            Volatile.Read(ref _calls), 0, Interlocked.Read(ref _inputs), Interlocked.Read(ref _outputs), 0, false, "IntegrationOnly");
        public ValueTask DisposeAsync(FamilyExecutionContext context)
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }
}
