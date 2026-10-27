using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Diagnostics;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Ui.Persistence;

internal static class Program
{
    private const string ExpectedSha = "4353E363660B3696E9EB1441A73E40F07864B09E0FD5BAEB64032F329C170CF8";
    private const string Fingerprint = "UiAsyncReceiverChecks";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static int _assertions;
    private static void Check(bool success, string issue)
    { _assertions++; if (!success) throw new InvalidDataException(issue); }
    private static void Report(string scenario, object data) => Console.WriteLine("RT2_RECEIVER " + JsonSerializer.Serialize(new { scenario, data }));
    private static async Task Main(string[] args)
    {
        string expectedSha = args.Length switch
        {
            0 => ExpectedSha,
            2 when args[0] == "--expected-sha256" && args[1].Length == 64 && args[1].All(Uri.IsHexDigit)
                => args[1].ToUpperInvariant(),
            _ => throw new ArgumentException("Usage: [--expected-sha256 <64 hex digits>]")
        };
        string root = Path.Combine(Path.GetTempPath(), "Rt2-UiReceiverChecks-" + Guid.NewGuid().ToString("N"));
        string assembly = typeof(ProductionSearchSession).Assembly.Location;
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
        Check(sha == expectedSha, "Wrong candidate assembly; no production rebuild is permitted by this check.");
        try
        {
            CheckCapacityBounds(root);
            await CheckThousandWithoutFrames();
            await CheckBoundedBackpressureAndTail();
            await CheckCancelWhileBufferFull();
            await CheckCancelledAndDetachedTail(Path.Combine(root, "detached"));
            await CheckReadinessFailureTail();
            await CheckCheckpointBuffer(Path.Combine(root, "buffer"));
            await CheckUncountedRawQueue(Path.Combine(root, "raw"));
            await CheckUncountedInFlight(Path.Combine(root, "inflight"));
            await CheckFlushFailure(Path.Combine(root, "flush"));
            Report("complete", new { result = "PASS", assertions = _assertions, assemblySha256 = sha,
                fixtureScope = "NewTemporaryDirectoryOnly", gameStarted = false, gpuDeviceCreated = false,
                detachedScope = "Formal shared drain/commit seam; SceneTree subscription is not created offline" });
        }
        finally
        {
            string absolute = Path.GetFullPath(root);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe fixture cleanup.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }

    private static void CheckCapacityBounds(string root)
    {
        using var source = new FixtureSession(64);
        Throws<ArgumentOutOfRangeException>(() => new SearchResultReceiver(source, 0), "Receiver accepted zero capacity.");
        Throws<ArgumentOutOfRangeException>(() => new SearchResultReceiver(source, 65537), "Receiver exceeded explicit maximum.");
        var store = Open(Path.Combine(root, "capacity"));
        Throws<ArgumentOutOfRangeException>(() => store.BeginResultReception(source, 1001), "UI exceeded 1000 capacity.");
        var large = new SearchResultReceiver(source, 65536);
        Check(large.Capacity == 65536, "Explicit benchmark capacity rejected.");
        source.Finish();
        large.ReceptionCompletion.WaitAsync(Deadline).GetAwaiter().GetResult();
        Check(large.IsFullyDrained, "Empty source did not finish.");
    }

    private static async Task CheckThousandWithoutFrames()
    {
        using var source = new FixtureSession(64);
        var receiver = new SearchResultReceiver(source, 1000);
        Task producer = source.Produce(1000);
        // There is deliberately no foreground receive call until both tasks end.
        await Task.WhenAll(producer, source.Completion, receiver.ReceptionCompletion).WaitAsync(Deadline);
        Check(receiver.BufferedCount == 1000 && receiver.ReceivedCount == 1000 && receiver.DeliveredCount == 0,
            "Source/receiver required a frame to finish target1000.");
        Check(!receiver.IsFullyDrained && receiver.PeakBufferedCount <= 1000, "Wrong target1000 bounded state.");
        List<SearchCandidate> all = Drain(receiver);
        AssertSeeds(all, 1000);
        Check(receiver.IsFullyDrained && receiver.DeliveredCount == 1000 && source.MaxReaders == 1,
            "Target1000 tail or reader ownership failed.");
        Report("target1000NoForegroundPoll", new { result = "PASS", receivedBeforeFirstPoll = 1000,
            sourceQueueCapacity = 64, receiverCapacity = 1000, receiver.PeakBufferedCount, source.MaxReaders });
    }

    private static async Task CheckBoundedBackpressureAndTail()
    {
        using var source = new FixtureSession(2);
        var ten = Signal(); source.AtPublished = count => { if (count == 10) ten.TrySetResult(); };
        var receiver = new SearchResultReceiver(source, 7);
        Task producer = source.Produce(200);
        await ten.Task.WaitAsync(Deadline);
        Check(receiver.BufferedCount <= 7 && receiver.PeakBufferedCount <= 7, "Buffer exceeded capacity while foreground stopped.");
        Check(!producer.IsCompleted, "Bounded fixture unexpectedly published all200 without foreground.");
        var all = new List<SearchCandidate>();
        while (await receiver.WaitToReadCandidateAsync().AsTask().WaitAsync(Deadline)) all.AddRange(Drain(receiver));
        await Task.WhenAll(producer, receiver.ReceptionCompletion).WaitAsync(Deadline);
        AssertSeeds(all, 200);
        Check(receiver.IsFullyDrained && receiver.PeakBufferedCount <= 7 && source.MaxReaders == 1,
            "Backpressure tail did not join or used multiple readers.");
        Report("boundedBackpressure", new { result = "PASS", candidates = all.Count, capacity = 7, receiver.PeakBufferedCount });
    }

    private static async Task CheckCancelledAndDetachedTail(string root)
    {
        using var source = new FixtureSession(64);
        var store = PrepareStore(root, source);
        SearchResultReceiver receiver = Receiver(store);
        var ready = Signal();
        Task producer = source.ProduceUntilCancelled(200, ready);
        await ready.Task.WaitAsync(Deadline);
        await UntilReceived(receiver, 200);
        var all = new List<SearchCandidate>();
        Check(store.DrainReceivedResults(source, Fingerprint, all) == 64, "Main UI batch is not64.");
        Check(!store.SessionResultsFullyDrained(source), "Live session announced completion.");
        source.Cancel(); // FinishDetachedSearch performs this before shared drain.
        await Task.WhenAll(producer, source.Completion, receiver.ReceptionCompletion).WaitAsync(Deadline);
        Check(ReferenceEquals(receiver, Receiver(store)), "Detached handoff replaced its single consumer.");
        int detachedCalls = 0;
        while (!store.DrainSessionResults(source, Fingerprint)) { detachedCalls++; Check(detachedCalls < 10, "Detached drain failed to progress."); }
        Check(store.DisplayResults.Count == 200 && receiver.DeliveredCount == 200, "Cancelled/detached tail was lost.");
        Check(store.TryCompleteResultReception(source, Fingerprint) && !store.HasSearchInFlight, "Detached owner was not released after durable completion.");
        Check(source.MaxReaders == 1 && source.GetProgress().State == SearchRunState.Cancelled, "Cancellation or single-reader invariant failed.");
        var reloaded = Open(root);
        Check(reloaded.CurrentNextCursorOrdinal == source.SafeNext, "Detached cursor lost the published tail boundary.");
        Report("cancelDetachedSharedLifecycle", new { result = "PASS", received = 200, mainStored = 64,
            detachedStored = 136, source.MaxReaders, safeNext = source.SafeNext });
    }

    private static async Task CheckCancelWhileBufferFull()
    {
        using var source = new FixtureSession(2);
        var ten = Signal(); source.AtPublished = count => { if (count == 10) ten.TrySetResult(); };
        var receiver = new SearchResultReceiver(source, 7);
        Task producer = source.Produce(10000);
        await ten.Task.WaitAsync(Deadline);
        source.Cancel();
        // Cancel must release the blocked publisher without needing a frame;
        // the receiver's full-buffer write must retain its already-read item.
        await Task.WhenAll(producer, source.Completion).WaitAsync(Deadline);
        int published = source.GetProgress().MatchCount;
        Check(published == 10 && !receiver.ReceptionCompletion.IsCompleted,
            "Full-buffer cancellation fixture did not retain a blocked receive tail.");
        var all = new List<SearchCandidate>();
        while (await receiver.WaitToReadCandidateAsync().AsTask().WaitAsync(Deadline)) all.AddRange(Drain(receiver));
        await receiver.ReceptionCompletion.WaitAsync(Deadline);
        AssertSeeds(all, published);
        Check(receiver.Failure is null && receiver.IsFullyDrained && receiver.PeakBufferedCount <= 7 && source.MaxReaders == 1,
            "Cancel while buffer full lost/duplicated tail or broke receive lifetime.");
        Report("cancelWhileBufferFull", new { result = "PASS", published, retained = all.Count, receiver.PeakBufferedCount });
    }

    private static async Task CheckReadinessFailureTail()
    {
        using var source = new FixtureSession(64) { FailNextReadiness = true };
        for (int i = 0; i < 20; i++) await source.Publish(i);
        source.Finish();
        var receiver = new SearchResultReceiver(source, 20);
        await receiver.ReceptionCompletion.WaitAsync(Deadline);
        Check(receiver.Failure is IOException && source.CancelCount == 1, "Readiness failure did not cancel source/report failure.");
        AssertSeeds(Drain(receiver), 20);
        Check(receiver.IsFullyDrained && source.MaxReaders == 1, "Readiness failure abandoned a published tail.");
        Report("readinessFailureTail", new { result = "PASS", retained = 20 });
    }

    private static async Task CheckCheckpointBuffer(string root)
    {
        using var source = new FixtureSession(64);
        var store = PrepareStore(root, source);
        byte[] originalCursor = File.ReadAllBytes(Cursor(root));
        SearchResultReceiver receiver = Receiver(store);
        await Task.WhenAll(source.Produce(1000), receiver.ReceptionCompletion).WaitAsync(Deadline);
        store.ObserveSessionCheckpoint(source, Fingerprint); store.FlushCursor();
        Check(store.CurrentNextCursorOrdinal == source.Start && originalCursor.SequenceEqual(File.ReadAllBytes(Cursor(root))),
            "Nonempty1000 buffer advanced checkpoint.");
        Throws<InvalidOperationException>(() => store.ReleaseSearch(source), "Premature release discarded buffered tail.");
        Check(!store.TryCompleteResultReception(source, Fingerprint), "Completion accepted an unpersisted tail.");
        var all = new List<SearchCandidate>();
        Check(store.DrainReceivedResults(source, Fingerprint, all) == 64 && receiver.BufferedCount == 936, "UI did not honor64 batch.");
        store.ObserveSessionCheckpoint(source, Fingerprint); store.FlushCursor();
        Check(store.CurrentNextCursorOrdinal == source.Start && originalCursor.SequenceEqual(File.ReadAllBytes(Cursor(root))),
            "Partial foreground storage advanced checkpoint over936 results.");
        while (store.DrainReceivedResults(source, Fingerprint, all) > 0) { }
        AssertSeeds(all, 1000);
        Check(store.DisplayResults.Count == 1000 && store.GetPersistedResults().Count == SearchWorkspacePersistence.ResultsPerQuery,
            "Display1000/existing disk quota30 contract changed.");
        Check(store.TryCompleteResultReception(source, Fingerprint), "Complete result tail could not commit.");
        Check(!store.HasSearchInFlight && Open(root).CurrentNextCursorOrdinal == source.SafeNext, "Durable cursor did not commit exact suffix.");
        Check(Open(root).CurrentNextCursorSeed == Beta110SeedCodec.FormatOrdinal(source.Start + 1000), "Resume rounded suffix or mishandled carry.");
        Report("bufferCheckpoint", new { result = "PASS", heldAt = Beta110SeedCodec.FormatOrdinal(source.Start),
            nextSeed = Beta110SeedCodec.FormatOrdinal(source.SafeNext), stored = 1000, diskQuota = 30 });
    }

    private static async Task CheckUncountedRawQueue(string root)
    {
        using var source = new FixtureSession(64) { ReadinessGate = Signal() };
        await source.Publish(0, counted: false); source.SafeNext = source.Start + 1;
        var store = PrepareStore(root, source);
        var receiver = Receiver(store);
        Check(source.PendingCandidateCount == 1 && source.GetProgress().MatchCount == 0 && receiver.BufferedCount == 0,
            "Raw publication-lag fixture was not isolated.");
        store.ObserveSessionCheckpoint(source, Fingerprint); store.FlushCursor();
        Check(store.CurrentNextCursorOrdinal == source.Start, "Raw uncounted publication escaped checkpoint guard.");
        source.SetPublished(1); source.Finish(); source.ReadinessGate.TrySetResult();
        await receiver.ReceptionCompletion.WaitAsync(Deadline);
        Check(store.DrainReceivedResults(source, Fingerprint) == 1 && store.TryCompleteResultReception(source, Fingerprint),
            "Raw-lag fixture could not recover/commit.");
        Report("rawQueuePublicationLag", new { result = "PASS" });
    }

    private static async Task CheckUncountedInFlight(string root)
    {
        using var source = new FixtureSession(64) { PauseAfterRemoval = true };
        await source.Publish(0, counted: false); source.SafeNext = source.Start + 1;
        var store = PrepareStore(root, source);
        var receiver = Receiver(store);
        try
        {
            await source.Removed.Task.WaitAsync(Deadline);
            Check(source.PendingCandidateCount == 0 && source.GetProgress().MatchCount == 0 && receiver.BufferedCount == 0 && receiver.TransferInFlight,
                "In-flight publication-lag fixture was not isolated.");
            store.ObserveSessionCheckpoint(source, Fingerprint); store.FlushCursor();
            Check(store.CurrentNextCursorOrdinal == source.Start, "Moving uncounted candidate escaped checkpoint generation guard.");
        }
        finally { source.AllowRemoval.Set(); }
        source.SetPublished(1); source.Finish();
        await receiver.ReceptionCompletion.WaitAsync(Deadline);
        Check(store.DrainReceivedResults(source, Fingerprint) == 1 && store.TryCompleteResultReception(source, Fingerprint),
            "In-flight fixture could not recover/commit.");
        Report("inflightPublicationLag", new { result = "PASS" });
    }

    private static async Task CheckFlushFailure(string root)
    {
        using var source = new FixtureSession(64);
        var store = PrepareStore(root, source);
        Check(store.TryFlushAll(), "Fixture initial save failed.");
        byte[] cursorBefore = File.ReadAllBytes(Cursor(root));
        await Task.WhenAll(source.Produce(10), Receiver(store).ReceptionCompletion).WaitAsync(Deadline);
        Check(store.DrainReceivedResults(source, Fingerprint) == 10, "Flush fixture lost result tail.");
        string workspace = Path.Combine(root, "RolltheSpire2", "state", "search_workspace.json");
        File.Delete(workspace); Directory.CreateDirectory(workspace); // Isolated temporary fixture only.
        Check(!store.TryCompleteResultReception(source, Fingerprint), "Failed result write incorrectly released completion.");
        Check(store.HasSearchInFlight && cursorBefore.SequenceEqual(File.ReadAllBytes(Cursor(root))),
            "Failed result write released owner or advanced durable cursor.");
        Throws<InvalidOperationException>(() => store.BeginSearchCheckpoint("anotherQuery", source.SafeNext),
            "New search overwrote a failed-save owner.");
        Check(!store.TrySetPersistentOrigin("000000000000", out _, out _), "Origin change bypassed failed-save ownership.");
        Directory.Delete(workspace); // Verified exact empty blocker created above, never recursive.
        Check(store.TryCompleteResultReception(source, Fingerprint) && !store.HasSearchInFlight, "Save retry did not release owner.");
        var reloaded = Open(root);
        Check(reloaded.CurrentNextCursorOrdinal == source.SafeNext && reloaded.GetPersistedResults().Count == 10,
            "Successful retry did not persist both results and safe cursor.");
        Report("flushFailureAndRetry", new { result = "PASS", cursorHeldOnFailure = true, retrySaved = 10 });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SearchWorkspacePersistence Open(string root) => new(root, RuntimeProfileId.Beta111, initializeSearchCursor: false);
    private static string Cursor(string root) => Path.Combine(root, "RolltheSpire2", "state", "search_cursor.json");
    private static SearchWorkspacePersistence PrepareStore(string root, FixtureSession source)
    {
        var store = Open(root);
        Check(store.TrySetPersistentOrigin(Beta110SeedCodec.FormatOrdinal(source.Start), out _, out _), "Fixture origin rejected.");
        store.ResetWorkspaceForSuccessfulStart(Fingerprint);
        store.BeginSearchCheckpoint(Fingerprint, source.Start);
        store.BeginResultReception(source, 1000); store.TrackSearch(source);
        return store;
    }
    private static SearchResultReceiver Receiver(SearchWorkspacePersistence store) =>
        (SearchResultReceiver)typeof(SearchWorkspacePersistence).GetField("_resultReceiver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    private static async Task UntilReceived(SearchResultReceiver receiver, long count)
    {
        // Signal readiness of already buffered data, then use a finite thread yield
        // only to reach an exact deterministic publication gate (never a timer).
        await receiver.WaitToReadCandidateAsync().AsTask().WaitAsync(Deadline);
        await Task.Run(() => { if (!SpinWait.SpinUntil(() => receiver.ReceivedCount >= count, Deadline)) throw new TimeoutException("Receiver did not reach publication gate."); });
    }
    private static List<SearchCandidate> Drain(SearchResultReceiver receiver)
    {
        var all = new List<SearchCandidate>();
        while (receiver.TryReadCandidate(out var candidate)) { Check(candidate is not null, "Null candidate."); all.Add(candidate!); }
        return all;
    }
    private static void AssertSeeds(IReadOnlyList<SearchCandidate> all, int count)
    {
        Check(all.Count == count, "Candidate count mismatch.");
        ulong start = FixtureSession.StartOrdinal;
        for (int i = 0; i < count; i++) Check(all[i].Seed == Beta110SeedCodec.FormatOrdinal(start + (ulong)i), "Candidate lost, duplicated or out of FIFO order at" + i);
    }
    private static void Throws<T>(Action action, string issue) where T : Exception
    {
        try { action(); } catch (T) { _assertions++; return; }
        throw new InvalidDataException(issue);
    }

    private sealed class FixtureSession : IProductionSearchSession, IDisposable
    {
        internal static readonly ulong StartOrdinal = ParseStart();
        private readonly Channel<SearchCandidate> _channel;
        private readonly TaskCompletionSource _completion = Signal();
        private readonly CancellationTokenSource _cancel = new();
        private int _published, _activeReaders, _maxReaders, _finished, _cancelCount;
        internal FixtureSession(int capacity) => _channel = Channel.CreateBounded<SearchCandidate>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        internal ulong Start => StartOrdinal;
        internal ulong SafeNext { get; set; } = StartOrdinal;
        internal int MaxReaders => Volatile.Read(ref _maxReaders);
        internal int CancelCount => Volatile.Read(ref _cancelCount);
        internal Action<int>? AtPublished { get; set; }
        internal TaskCompletionSource? ReadinessGate { get; init; }
        internal bool FailNextReadiness { get; set; }
        internal bool PauseAfterRemoval { get; init; }
        internal TaskCompletionSource Removed { get; } = Signal();
        internal ManualResetEventSlim AllowRemoval { get; } = new();
        public Task Completion => _completion.Task;
        public int PendingCandidateCount => _channel.Reader.Count;
        public void Cancel() { Interlocked.Increment(ref _cancelCount); _cancel.Cancel(); }
        internal void SetPublished(int count) => Volatile.Write(ref _published, count);
        internal async Task Publish(int index, bool counted = true, CancellationToken token = default)
        {
            await _channel.Writer.WriteAsync(new SearchCandidate(Beta110SeedCodec.FormatOrdinal(Start + (ulong)index),
                RuntimeProfileId.Beta111, default, 10, Fingerprint, null!, null, null!, [], [], []), token);
            if (counted) { int count = Interlocked.Increment(ref _published); AtPublished?.Invoke(count); }
        }
        internal async Task Produce(int count)
        {
            try { for (int i = 0; i < count; i++) await Publish(i, token: _cancel.Token); }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            finally { SafeNext = Start + (ulong)Volatile.Read(ref _published); Finish(); }
        }
        internal async Task ProduceUntilCancelled(int count, TaskCompletionSource ready)
        {
            try
            {
                for (int i = 0; i < count; i++) await Publish(i, token: _cancel.Token);
                SafeNext = Start + (ulong)count; ready.TrySetResult();
                await Task.Delay(Timeout.Infinite, _cancel.Token);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            finally { Finish(); }
        }
        internal void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _channel.Writer.TryComplete(); _completion.TrySetResult();
        }
        public async ValueTask<bool> WaitToReadCandidateAsync(CancellationToken token = default)
        {
            if (FailNextReadiness) { FailNextReadiness = false; throw new IOException("Synthetic readiness failure"); }
            if (ReadinessGate is not null) await ReadinessGate.Task.WaitAsync(token);
            return await _channel.Reader.WaitToReadAsync(token);
        }
        public bool TryReadCandidate(out SearchCandidate? candidate)
        {
            int active = Interlocked.Increment(ref _activeReaders);
            int previous;
            while (active > (previous = Volatile.Read(ref _maxReaders)) && Interlocked.CompareExchange(ref _maxReaders, active, previous) != previous) { }
            try
            {
                bool read = _channel.Reader.TryRead(out candidate);
                if (read && PauseAfterRemoval)
                { Removed.TrySetResult(); if (!AllowRemoval.Wait(Deadline)) throw new TimeoutException("In-flight test gate not released."); }
                return read;
            }
            finally { Interlocked.Decrement(ref _activeReaders); }
        }
        public SearchProgressSnapshot GetProgress() => new(Completion.IsCompleted ? (_cancel.IsCancellationRequested ? SearchRunState.Cancelled : SearchRunState.Completed) : SearchRunState.Running,
            Volatile.Read(ref _published), Volatile.Read(ref _published), 1000, 1000, 1, 1000, SearchDisposition.Match, "", _cancel.IsCancellationRequested);
        public SearchDiagnosticSummarySnapshot GetDiagnosticSummary() => throw new NotSupportedException();
        public ulong GetSafeNextOrdinal() => SafeNext;
        public bool TryGetSafeNextOrdinal(out ulong nextOrdinal) { nextOrdinal = SafeNext; return true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void Dispose() { AllowRemoval.Set(); Finish(); _cancel.Dispose(); AllowRemoval.Dispose(); }
        private static ulong ParseStart()
        {
            if (!Beta110SeedCodec.TryParseOrdinal("12345678ZZFZ", out ulong value, out _, out _)) throw new InvalidDataException("Invalid start.");
            return value;
        }
    }
}
