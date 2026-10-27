using System.Collections.Concurrent;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;

namespace CpuExecutionChecks;

// Runs the actual production class. The request intentionally omits gameplay
// authority because numerical root filtering never reads that state.
public static class CpuExecutionIntegration
{
    public static void Run(Action<string> emit)
    {
        int assertions = 0, cases = 0;
        void Require(bool condition, string message)
        {
            Interlocked.Increment(ref assertions);
            if (!condition) throw new InvalidDataException(message);
        }
        void Report(object value) => emit("RT2_CPU_INTEGRATION " + JsonSerializer.Serialize(value));
        void EqualOrdinals(ulong[] expected, FamilyCandidateSet actual, string label)
        {
            Require(expected.Length == actual.Count, label + ": output count mismatch.");
            if (actual.IsDense && actual.Count == 0) return;
            Require(!actual.IsDense, label + ": CPU output must expose canonical ABI1.");
            ReadOnlySpan<ulong> values = actual.ExportAbi1().Span;
            for (int i = 0; i < values.Length; i++)
                Require(values[i] == expected[i], label + $": ordinal/order mismatch at {i}.");
        }
        static bool Predicate(ulong root) => ((root >> 13) ^ root) % 7 <= 1;
        static ulong[] Reference(FamilyCandidateSet input, Func<ulong, bool> predicate) =>
            input.EnumerateLogicalOrdinals().Where(ordinal => predicate(Beta111Profile.Instance.ComputeRootSeed(
                VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, input.Batch.GlobalCandidate(ordinal))))).ToArray();

        ThreadPool.GetMinThreads(out int oldWorkers, out int oldIo);
        ThreadPool.SetMinThreads(Math.Max(64, oldWorkers), oldIo);
        try
        {
            Report(new { phase = "start", logicalProcessors = Environment.ProcessorCount,
                workerRequests = new[] { 1, 32, 64 } });
            var inputs = new List<FamilyCandidateSet>();
            foreach (int count in new[] { 0, 1, 127, 128, 8191, 8192, 8193, 20003 })
                inputs.Add(FamilyCandidateSet.Dense(new SearchBatch(0x100000003, count)));
            inputs.Add(FamilyCandidateSet.Dense(new SearchBatch(Beta110SeedCodec.SpaceSize - 20003, 20003)));
            var compactBatch = new SearchBatch(1048573, 24007);
            inputs.Add(FamilyCandidateSet.FromAbi1(compactBatch,
                Enumerable.Range(0, compactBatch.BatchCandidateCount).Where(i => i % 3 != 1).Select(i => (ulong)i).ToArray()));
            inputs.Add(FamilyCandidateSet.FromAbi1(compactBatch, []));
            inputs.Add(FamilyCandidateSet.FromAbi1(compactBatch, [127, 8191, 8192, 8193, 16383, 16384, 24006]));

            foreach (FamilyCandidateSet input in inputs)
            {
                ulong[] expected = Reference(input, Predicate);
                foreach (int requested in new[] { 1, 32, 64 })
                foreach (bool numericalRoots in new[] { false, true })
                {
                    var request = Request(requested);
                    var cpu = new FamilyCpuExecution(request, "Check.Cpu", "Check.Cpu.v1",
                        workers: 1, numericalRoots: numericalRoots);
                    int effective = Math.Min(requested, request.RunOptions.Resources!.ResolveCpuWorkers(Environment.ProcessorCount));
                    Require(cpu.Workers == effective, "Explicit budget must override the donor's one-worker shape.");
                    Require(cpu.Condition(!input.IsDense).PhysicalImplementationRevision.Contains(".ExplicitCpuBudget.v1.", StringComparison.Ordinal),
                        "Budgeted CPU work must use its own physical revision.");
                    FamilyCandidateSet actual = cpu.Execute(new(input.Batch, request), input, CancellationToken.None, Predicate);
                    Require(actual.Batch == input.Batch, "CPU execution changed the original root range.");
                    EqualOrdinals(expected, actual, $"requested={requested},numericalRoots={numericalRoots},dense={input.IsDense},count={input.Count}");
                    cases++;
                }
            }
            // The pre-budget path retains its donor worker count and result.
            var legacyRequest = Request(64) with { RunOptions = Request(64).RunOptions with { Resources = null } };
            var legacy = new FamilyCpuExecution(legacyRequest, "Check.Legacy", "Check.Legacy.v1", workers: 8, numericalRoots: true);
            Require(legacy.Workers == 8, "A null resource budget must keep the donor worker count.");
            EqualOrdinals(Reference(inputs[8], Predicate), legacy.Execute(new(inputs[8].Batch, legacyRequest), inputs[8], CancellationToken.None, Predicate),
                "Legacy parity");
            Report(new { phase = "parity", result = "PASS", cases,
                effectiveWorkers = new[] { 1, 32, 64 }.Select(w => Math.Min(w, new SearchResourceBudget(100).ResolveCpuWorkers(Environment.ProcessorCount))).ToArray() });

            var cancellationRequest = Request(32);
            var cancellationCpu = new FamilyCpuExecution(cancellationRequest, "Check.Cancel", "Check.Cancel.v1", numericalRoots: true);
            var cancellationInput = FamilyCandidateSet.Dense(new SearchBatch(1234567, 20003));
            int visited = 0;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    cancellationCpu.Execute(new(cancellationInput.Batch, cancellationRequest), cancellationInput, cancelled.Token,
                        _ => { Interlocked.Increment(ref visited); return true; });
                    throw new InvalidDataException("Pre-cancelled execution unexpectedly succeeded.");
                }
                catch (OperationCanceledException) { assertions++; }
                Require(visited == 0, "Pre-cancellation must stop before evaluating any root.");
            }
            using (var cancelled = new CancellationTokenSource())
            {
                try
                {
                    cancellationCpu.Execute(new(cancellationInput.Batch, cancellationRequest), cancellationInput, cancelled.Token,
                        _ => { if (Interlocked.Increment(ref visited) == 97) cancelled.Cancel(); return true; });
                    throw new InvalidDataException("In-flight cancellation unexpectedly succeeded.");
                }
                catch (OperationCanceledException) { assertions++; }
                Require(visited >= 97 && visited < cancellationInput.Count, "Cancellation did not stop the actual numerical loop.");
            }
            EqualOrdinals(Reference(cancellationInput, Predicate), cancellationCpu.Execute(new(cancellationInput.Batch, cancellationRequest),
                cancellationInput, CancellationToken.None, Predicate), "Post-cancellation reuse");
            Report(new { phase = "cancellation", result = "PASS", visited });

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var cpuBudget = new SearchCpuConcurrencyBudget(2);
            using (cpuBudget.EnterScope())
            {
                int active = 0, peak = 0, familyCalls = 0, exactCalls = 0;
                var threads = new ConcurrentDictionary<int, byte>();
                using var firstWorkers = new CountdownEvent(Math.Min(2, cancellationCpu.Workers));
                void Enter()
                {
                    int concurrent = Interlocked.Increment(ref active);
                    int before;
                    do { before = Volatile.Read(ref peak); }
                    while (concurrent > before && Interlocked.CompareExchange(ref peak, concurrent, before) != before);
                    if (concurrent > 2) throw new InvalidDataException("Family and Exact exceeded their shared CPU permits.");
                }
                bool TrackedPredicate(ulong root)
                {
                    Enter();
                    try
                    {
                        if (threads.TryAdd(Environment.CurrentManagedThreadId, 0) && firstWorkers.CurrentCount > 0)
                        {
                            try { firstWorkers.Signal(); } catch (InvalidOperationException) { }
                            Require(firstWorkers.Wait(TimeSpan.FromSeconds(5), deadline.Token), "Production CPU partitions did not execute in parallel.");
                        }
                        Interlocked.Increment(ref familyCalls);
                        Thread.SpinWait(250);
                        return Predicate(root);
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
                Task<FamilyCandidateSet> familyTask = Task.Run(() => cancellationCpu.Execute(new(cancellationInput.Batch, cancellationRequest),
                    cancellationInput, deadline.Token, TrackedPredicate), deadline.Token);
                Task exactTask = Task.Run(() =>
                {
                    for (int i = 0; i < 40; i++)
                    {
                        using var lease = SearchCpuConcurrencyBudget.Acquire(deadline.Token);
                        Enter();
                        try { Thread.Sleep(1); Interlocked.Increment(ref exactCalls); }
                        finally { Interlocked.Decrement(ref active); }
                    }
                }, deadline.Token);
                Task.WhenAll(familyTask, exactTask).GetAwaiter().GetResult();
                EqualOrdinals(Reference(cancellationInput, Predicate), familyTask.Result, "Shared-permit parity");
                Require(active == 0 && peak <= 2 && familyCalls == cancellationInput.Count && exactCalls == 40,
                    "Shared production CPU work did not drain cleanly.");
                Require(threads.Count > 1 || cancellationCpu.Workers == 1, "Multiple configured CPU workers were never exercised.");
                // Acquiring every permit after completion detects leaked leases.
                using var recoveredA = SearchCpuConcurrencyBudget.Acquire(deadline.Token);
                using var recoveredB = SearchCpuConcurrencyBudget.Acquire(deadline.Token);
                Report(new { phase = "sharedPermits", result = "PASS", peak, familyThreads = threads.Count, familyCalls, exactCalls });
            }
            Report(new { phase = "complete", result = "PASS", cases, assertions });
        }
        finally { ThreadPool.SetMinThreads(oldWorkers, oldIo); }
    }

    private static ExactSearchExecutionRequest Request(int workers)
    {
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "CpuExecutionChecks", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "CpuExecutionChecks")),
            false, "CpuExecutionChecks");
        return new(compiled, new SearchRunOptions("000000000000", 24007, 1, workers)
            { Resources = new SearchResourceBudget(100, 0) }, "000000000000", 24007,
            ExactSearchEvaluationProjection.Empty, CombatRewardRoutePolicyContract.None, "CpuExecutionChecks");
    }
}
