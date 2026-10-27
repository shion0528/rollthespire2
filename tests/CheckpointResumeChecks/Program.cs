using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Diagnostics;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Enumeration;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

namespace CheckpointResumeChecks;

internal static partial class Program
{
    private static int _assertions;
    private static void Require(bool condition, string issue)
    {
        Interlocked.Increment(ref _assertions);
        if (!condition) throw new InvalidDataException(issue);
    }
    private static void Report(object value) => Console.WriteLine("RT2_CHECKPOINT " + JsonSerializer.Serialize(value));
    private static ulong Parse(string seed)
    {
        Require(Beta110SeedCodec.TryParseOrdinal(seed, out ulong ordinal, out string canonical, out string issue) && canonical == seed,
            "Invalid fixture seed: " + seed + ";" + issue);
        return ordinal;
    }
    private static SearchWorkspacePersistence Open(string root) => new(root, RuntimeProfileId.Beta111, initializeSearchCursor: false);
    private static string CursorFile(string root) => Path.Combine(root, "RolltheSpire2", "state", "search_cursor.json");

    private static async Task Main()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "Rt2-CheckpointChecks-" + Guid.NewGuid().ToString("N"));
        try
        {
            CheckPersistedNonalignedSuffix(Path.Combine(fixture, "suffix"));
            await CheckUnfinishedFamilyCancellation(Path.Combine(fixture, "cancel"));
            await CheckBoundaryContinuation(Path.Combine(fixture, "boundary"));
            await CheckTerminalSpaceWrap(Path.Combine(fixture, "wrap"));
            CheckQueryBinding(Path.Combine(fixture, "binding"));
            string assembly = typeof(ProductionSearchSession).Assembly.Location;
            Report(new { phase = "complete", result = "PASS", assertions = _assertions,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
                fixtureScope = "NewTemporaryDirectoryOnly", gpuDeviceCreated = false });
        }
        finally
        {
            string absolute = Path.GetFullPath(fixture);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Checkpoint fixture cleanup escaped the temporary directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private static void CheckPersistedNonalignedSuffix(string root)
    {
        const string seed = "12345678ZZFZ";
        ulong start = Parse(seed);
        var persistence = Open(root);
        Require(persistence.TrySetPersistentOrigin("123456780000", out _, out _), "Manual fixture origin was rejected.");
        persistence.ObserveSafeNextCursor(start);
        persistence.FlushCursor();
        byte[] saved = File.ReadAllBytes(CursorFile(root));
        var reloaded = Open(root);
        reloaded.InitializeWorkbenchCursor();
        Require(reloaded.CurrentNextCursorOrdinal == start && reloaded.CurrentNextCursorSeed == seed,
            "Reload rounded the saved ZZFZ cursor to a block boundary.");
        Require(saved.AsSpan().SequenceEqual(File.ReadAllBytes(CursorFile(root))), "Reload rewrote a valid cursor.");
        uint[] table = PrefixHashCache.BuildTable(reloaded.CurrentNextCursorOrdinal, 614);
        uint suffix = checked((uint)(start % PrefixHashCache.BlockSize));
        Require(table[0] == suffix && PrefixHashCache.BlockSize - suffix == 613 && table[1] == 2,
            "Resumed prefix table lost the exact suffix or the next prefix row.");
        var enumerator = new Base34SeedEnumerator(Beta111Profile.Instance, seed);
        Require(enumerator.SeedAt(0) == seed && enumerator.SeedAt(612) == "12345678ZZZZ" &&
            enumerator.SeedAt(613) == "123456790000", "Sequential enumeration skipped the remaining suffix or failed to carry.");
        Require(PrefixHashCache.ComputeRootHashFromPrefix(PrefixHashCache.ComputePrefixHash(PrefixHashCache.EncodeFirst8(start / PrefixHashCache.BlockSize)),
            PrefixHashCache.EncodeTail4(suffix)) == Beta111Profile.Instance.ComputeRootSeed(seed),
            "Resumed prefix cache does not preserve the production root hash.");
        Report(new { phase = "diskReloadSuffix", result = "PASS", resumeSeed = seed, remainingInPrefix = 613, nextPrefix = "123456790000" });
    }

    private static async Task CheckUnfinishedFamilyCancellation(string root)
    {
        ulong start = Parse("12345678ZZFZ");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var family = new ControlledFamily(async (input, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return FamilyCandidateSet.FromAbi1(input.Batch, []);
        });
        var persistence = Open(root);
        Require(persistence.TrySetPersistentOrigin("12345678ZZFZ", out _, out _), "Cancellation fixture origin was rejected.");
        await using (var session = Session(Request(start, 900), family, 257))
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.TryGetSafeNextOrdinal(out ulong live) && live == start,
                "An in-flight Family batch advanced the live safe cursor.");
            persistence.DrainSessionResults(session, "CheckpointChecks");
            persistence.FlushCursor();
            session.Cancel();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.GetProgress().State == SearchRunState.Cancelled && session.GetSafeNextOrdinal() == start,
                "Cancelling an unfinished Family committed its batch.");
            Require(persistence.DrainSessionResults(session, "CheckpointChecks"), "Cancelled session did not finish its handoff.");
            persistence.FlushCursor();
        }
        var reloaded = Open(root);
        Require(reloaded.CurrentNextCursorOrdinal == start && reloaded.CurrentNextCursorSeed == "12345678ZZFZ",
            "Cancel/save/reload skipped an uncompleted batch.");
        Require(family.Ranges.Count == 1, "Cancellation fixture unexpectedly began another batch.");
        Report(new { phase = "cancelUnfinishedFamily", result = "PASS", resumeSeed = reloaded.CurrentNextCursorSeed, uncommittedBatches = 1 });
    }

    private static async Task CheckBoundaryContinuation(string root)
    {
        ulong start = Parse("12345678ZZFZ");
        var family = new ControlledFamily((input, _) => ValueTask.FromResult(FamilyCandidateSet.FromAbi1(input.Batch, [])));
        var persistence = Open(root);
        Require(persistence.TrySetPersistentOrigin("12345678ZZFZ", out _, out _), "Boundary fixture origin was rejected.");
        await using (var session = Session(Request(start, 614), family, 127))
        {
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.GetProgress().State == SearchRunState.Completed && session.GetSafeNextOrdinal() == start + 614,
                "Completed boundary scan has an incorrect safe end.");
            Require(persistence.DrainSessionResults(session, "CheckpointChecks"), "Boundary session did not finish its handoff.");
            persistence.FlushCursor();
        }
        ulong next = start;
        var roots = new HashSet<ulong>();
        foreach (SearchBatch batch in family.Ranges)
        {
            Require(batch.BatchBase == next, "Root batches contain a gap or overlap.");
            for (int i = 0; i < batch.BatchCandidateCount; i++) Require(roots.Add(batch.BatchBase + (ulong)i), "A root was dispatched twice.");
            next += (ulong)batch.BatchCandidateCount;
        }
        var reloaded = Open(root);
        Require(roots.Count == 614 && next == start + 614 && reloaded.CurrentNextCursorSeed == "123456790001",
            "Boundary scan reset its cursor or lost a root.");
        Report(new { phase = "carryBoundary", result = "PASS", roots = roots.Count, nextSeed = reloaded.CurrentNextCursorSeed });
    }

    private static async Task CheckTerminalSpaceWrap(string root)
    {
        ulong start = Parse("ZZZZZZZZZZFZ");
        int remaining = checked((int)(Beta110SeedCodec.SpaceSize - start));
        var persistence = Open(root);
        Require(persistence.TrySetPersistentOrigin("ZZZZZZZZZZFZ", out _, out _), "Terminal fixture origin was rejected.");
        var sentinel = new SentinelSession(Beta110SeedCodec.SpaceSize);
        Require(!persistence.DrainSessionResults(sentinel, "CheckpointChecks") && persistence.CurrentNextCursorOrdinal == start,
            "An unfinished session wrapped its end sentinel.");
        var family = new ControlledFamily((input, _) => ValueTask.FromResult(FamilyCandidateSet.FromAbi1(input.Batch, [])));
        await using (var session = Session(Request(start, remaining), family, 128))
        {
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.GetSafeNextOrdinal() == Beta110SeedCodec.SpaceSize, "Terminal scan did not preserve the end sentinel.");
            Require(persistence.DrainSessionResults(session, "CheckpointChecks"), "Terminal session did not complete.");
            Require(persistence.DrainSessionResults(session, "CheckpointChecks") && persistence.Cursor.WrapCount == 1,
                "Polling the same completed terminal session counted a second wrap.");
            persistence.FlushCursor();
        }
        var reloaded = Open(root);
        Require(reloaded.CurrentNextCursorOrdinal == 0 && reloaded.CurrentNextCursorSeed == "000000000000" && reloaded.Cursor.WrapCount == 1 &&
            reloaded.CurrentPersistentOriginSeed == "ZZZZZZZZZZFZ", "Terminal wrap/reload lost zero, the original seed, or its wrap count.");
        reloaded.InitializeWorkbenchCursor();
        Require(reloaded.CurrentNextCursorOrdinal == 0 && reloaded.Cursor.WrapCount == 1, "Reopening randomized an already wrapped cursor.");
        reloaded.CommitEndOfSpaceWrap();
        Require(reloaded.Cursor.WrapCount == 1, "Reloading forgot that the terminal wrap had already been committed.");
        Report(new { phase = "terminalWrap", result = "PASS", remaining, nextSeed = reloaded.CurrentNextCursorSeed, wraps = reloaded.Cursor.WrapCount });
    }

    private static ExactSearchExecutionRequest Request(ulong start, int count)
    {
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "CheckpointChecks", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "CheckpointChecks")), false, "CheckpointChecks");
        string seed = Beta110SeedCodec.FormatOrdinal(start);
        return new(compiled, new SearchRunOptions(seed, count, 1, 2) { Resources = new SearchResourceBudget(100, 0) },
            seed, count, ExactSearchEvaluationProjection.Empty, CombatRewardRoutePolicyContract.None, "CheckpointChecks");
    }

    private static ProductionSearchSession Session(ExactSearchExecutionRequest request, IFamilyInvocation family, int window)
    {
        FamilyExecutionPlan plan = FamilyPlanner.InOrder([family]);
        var eta = new FamilySearchEtaProjectionV1(FamilySearchEtaStatus.Unavailable, null, "CheckpointChecks", plan,
            request.TargetMatchCount, request.ScanCount, null, null, null, null, null, null, null, null, null, null, null, [], []);
        return new(request, plan, eta, window, 1, null, "", exactEvaluator: (r, _) =>
            throw new InvalidDataException("Zero-survivor fixture unexpectedly entered Exact."));
    }

    private sealed class ControlledFamily(Func<FamilyCandidateSet, CancellationToken, ValueTask<FamilyCandidateSet>> filter) : IFamilyInvocation
    {
        internal ConcurrentQueue<SearchBatch> Ranges { get; } = new();
        public string FamilyId => "Check.Checkpoint";
        public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId, 8192, [], "CheckpointChecks");
        public FamilySurvivalProjection Survival => FamilySurvivalProjection.Unresolved(FamilyId, "ControlledNoMatches");
        public FamilyConditionPerformanceProjection ConditionPerformance => new(FamilyId, "Check.Checkpoint.Cpu.v1", "Check.Checkpoint.v1", 1, "CheckpointChecks", usesGpu: false);
        public ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow observation, FamilyCandidateSet input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Ranges.Enqueue(input.Batch); return filter(input, token);
        }
        public FamilyPerformanceObservation CapturePerformanceObservation() => new(ConditionPerformance, "CPU", 0, 0, 0, Ranges.Count, 0, 0, 0, 0, false, "CheckpointChecks");
        public ValueTask DisposeAsync(FamilyExecutionContext context) => ValueTask.CompletedTask;
    }

    private sealed class SentinelSession(ulong next) : IProductionSearchSession
    {
        public Task Completion { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        public void Cancel() { }
        public bool TryReadCandidate(out SearchCandidate? candidate) { candidate = null; return false; }
        public SearchProgressSnapshot GetProgress() => throw new NotSupportedException();
        public SearchDiagnosticSummarySnapshot GetDiagnosticSummary() => throw new NotSupportedException();
        public ulong GetSafeNextOrdinal() => next;
        public bool TryGetSafeNextOrdinal(out ulong ordinal) { ordinal = next; return true; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static partial void CheckQueryBinding(string root);
}
