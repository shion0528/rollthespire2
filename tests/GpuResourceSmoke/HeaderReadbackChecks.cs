using Godot;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Called by the main smoke host after it creates a real local RD.
    private static void TestHeaderReadbackChecks(RenderingDevice rd)
    {
        TestHeaderReadbackStateMachine(rd);
        TestHeaderReadbackDevice(rd);
        TestHeaderReadbackExecutor(rd);
    }

    private static void TestHeaderReadbackStateMachine(RenderingDevice rd)
    {
        uint[] words = [0x4e464d52, 1, 3, 0, 513, 42, 20, 4];
        byte[] bytes = FamilyGpuComputeUtility.ToBytes(words);
        Callable callback = default;
        int registrations = 0, submissions = 0, syncReads = 0, completions = 0;
        var transport = new FamilyGpuHeaderReadback(rd, true,
            (_, ready, size) => { Require(size == 32, "Wrong queued header size."); registrations++; callback = ready; return Error.Ok; },
            _ =>
            {
                submissions++;
                callback.Call(bytes);
                // Saved data must remain independent of a reused callback array.
                Array.Fill(bytes, (byte)0);
            },
            (_, _) => { syncReads++; throw new InvalidDataException("Accepted request unexpectedly used synchronous readback."); });
        var actual = transport.CompleteRecorded(default, 8, true, "Smoke.HeaderState", () => completions++);
        Require(actual.SequenceEqual(words) && registrations == 1 && submissions == 1 && syncReads == 0 && completions == 1 &&
            transport.Revision == FamilyGpuHeaderReadback.QueuedRevision, "Queued state transport changed the header or submitted twice.");

        bytes = FamilyGpuComputeUtility.ToBytes(words);
        registrations = submissions = syncReads = 0;
        var fallback = new FamilyGpuHeaderReadback(rd, true,
            (_, _, _) => { registrations++; return Error.Unavailable; },
            _ => submissions++, (_, _) => { syncReads++; return bytes; });
        for (int i = 0; i < 2; i++)
            Require(fallback.CompleteRecorded(default, 8, true, "Smoke.HeaderRejected", () => { }).SequenceEqual(words),
                "Registration fallback changed data.");
        Require(registrations == 1 && submissions == 2 && syncReads == 2 && fallback.Revision == FamilyGpuHeaderReadback.FallbackRevision,
            "Rejected registration retried allocation or re-ran an already submitted batch.");

        registrations = submissions = syncReads = 0;
        var unsupported = new FamilyGpuHeaderReadback(rd, true,
            (_, _, _) => { registrations++; throw new MissingMethodException("Smoke unsupported binding."); },
            _ => submissions++, (_, _) => { syncReads++; return bytes; });
        Require(unsupported.CompleteRecorded(default, 8, true, "Smoke.HeaderUnsupported", () => { }).SequenceEqual(words) &&
            registrations == 1 && submissions == 1 && syncReads == 1 && unsupported.Revision == FamilyGpuHeaderReadback.FallbackRevision,
            "Unsupported API did not use the single-submission fallback.");

        registrations = submissions = syncReads = 0;
        var sync = new FamilyGpuHeaderReadback(rd, false,
            (_, _, _) => { registrations++; throw new InvalidOperationException("Sync control registered an async request."); },
            _ => submissions++, (_, _) => { syncReads++; return bytes; });
        Require(sync.CompleteRecorded(default, 8, true, "Smoke.HeaderSync", () => { }).SequenceEqual(words) &&
            sync.Revision == FamilyGpuHeaderReadback.SynchronousRevision, "Synchronous A/B control changed data or revision.");
        Require(sync.CompleteRecorded(default, 8, false, "Smoke.HeaderEmpty", () => { }).SequenceEqual(words) &&
            registrations == 0 && submissions == 1 && syncReads == 2 && sync.Revision == FamilyGpuHeaderReadback.EmptyRevision,
            "An empty window submitted a fabricated compute batch.");

        int rejected = 0;
        void RejectCallback(Action<Callable> deliver, bool duringRegistration = false)
        {
            Callable ready = default;
            int submitted = 0, read = 0;
            var invalid = new FamilyGpuHeaderReadback(rd, true,
                (_, value, _) => { ready = value; if (duringRegistration) deliver(ready); return Error.Ok; },
                _ => { submitted++; if (!duringRegistration) deliver(ready); },
                (_, _) => { read++; return bytes; });
            Expect<InvalidDataException>(() => invalid.CompleteRecorded(default, 8, true, "Smoke.HeaderInvalid", () => { }));
            Require(submitted == 1 && read == 0, "Invalid callback replayed compute or silently changed transport.");
            Expect<InvalidOperationException>(() => invalid.CompleteRecorded(default, 8, true, "Smoke.HeaderFaulted", () => { }));
            Require(submitted == 1, "Faulted readback submitted another batch.");
            rejected++;
        }
        RejectCallback(_ => { });
        RejectCallback(ready => { ready.Call(bytes); ready.Call(bytes); });
        RejectCallback(ready => ready.Call(Array.Empty<byte>()));
        RejectCallback(ready => ready.Call(new byte[28]));
        RejectCallback(ready => ready.Call(new byte[36]));
        RejectCallback(ready => ready.Call(bytes), duringRegistration: true);
        RejectCallback(ready => Task.Run(() => ready.Call(bytes)).GetAwaiter().GetResult());

        // A callback retained from a completed request must never be accepted as
        // the status of the next request, even if its byte length is identical.
        Callable previous = default, current = default;
        int generation = 0;
        var late = new FamilyGpuHeaderReadback(rd, true,
            (_, ready, _) => { current = ready; return Error.Ok; },
            _ =>
            {
                if (++generation == 2) previous.Call(bytes);
                current.Call(bytes);
                previous = current;
            }, (_, _) => throw new InvalidDataException("Late callback used sync fallback."));
        late.CompleteRecorded(default, 8, true, "Smoke.HeaderGeneration1", () => { });
        Expect<InvalidDataException>(() => late.CompleteRecorded(default, 8, true, "Smoke.HeaderGeneration2", () => { }));
        Require(generation == 2, "Late callback caused a replay.");
        rejected++;

        submissions = syncReads = 0;
        var unexpected = new FamilyGpuHeaderReadback(rd, true,
            (_, _, _) => throw new InvalidOperationException("Smoke unexpected registration failure."),
            _ => submissions++, (_, _) => { syncReads++; return bytes; });
        Expect<InvalidOperationException>(() => unexpected.CompleteRecorded(default, 8, true, "Smoke.HeaderRegistrationFault", () => { }));
        Require(submissions == 1 && syncReads == 0, "Unexpected registration exception skipped synchronization or silently fell back.");
        foreach (byte[] malformed in new[] { Array.Empty<byte>(), new byte[28], new byte[36] })
        {
            var shortRead = new FamilyGpuHeaderReadback(rd, false, submitAndSync: _ => { },
                readSynchronously: (_, _) => malformed);
            Expect<InvalidDataException>(() => shortRead.CompleteRecorded(default, 8, true, "Smoke.HeaderSyncLength", () => { }));
            rejected++;
        }

        using var cancellation = new CancellationTokenSource();
        Callable cancellableCallback = default;
        int cancelledSubmissions = 0;
        var cancellable = new FamilyGpuHeaderReadback(rd, true,
            (_, ready, _) => { cancellableCallback = ready; return Error.Ok; },
            _ =>
            {
                if (++cancelledSubmissions == 1) cancellation.Cancel();
                cancellableCallback.Call(bytes);
            }, (_, _) => throw new InvalidDataException("Cancellation changed header transport."));
        Expect<OperationCanceledException>(() =>
        {
            cancellable.CompleteRecorded(default, 8, true, "Smoke.HeaderCancelDuringSync", () => { });
            // This is the production executor's cancellation boundary: the
            // local device and callback complete before observing cancellation.
            cancellation.Token.ThrowIfCancellationRequested();
        });
        Require(cancellable.CompleteRecorded(default, 8, true, "Smoke.HeaderReuseAfterCancel", () => { }).SequenceEqual(words) &&
            cancelledSubmissions == 2, "Cancellation during Sync broke later callback identity or workspace reuse.");
        Emit(new { phase = "headerReadbackStateMachine", result = "PASS", invalidCallbacksRejected = rejected,
            registrationRejectedOnce = true, exactSingleSubmission = true, synchronousControlAndEmpty = true,
            snapshotIndependent = true, cancellationDuringSyncAndReuse = true, productionHelper = true });
    }

    private static void TestHeaderReadbackDevice(RenderingDevice rd)
    {
        Rid buffer = FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 64, "Smoke.HeaderDownload");
        try
        {
            foreach (bool queued in new[] { false, true })
            {
                int submissions = 0, registrations = 0;
                var transport = new FamilyGpuHeaderReadback(rd, queued,
                    (source, callback, size) => { registrations++; return rd.BufferGetDataAsync(source, callback, 0, size); },
                    family => { submissions++; FamilyGpuComputeUtility.SubmitAndSync(rd, family); });
                foreach (int count in new[] { 8, 12, 16, 8 })
                {
                    uint[] expected = Enumerable.Range(0, 16).Select(index => checked((uint)(0x4e460000 + index + count))).ToArray();
                    FamilyGpuComputeUtility.Update(rd, buffer, expected, "Smoke.HeaderDownloadUpdate");
                    long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
                    var result = transport.CompleteRecorded(buffer, checked((uint)count), true, "Smoke.HeaderDownload", () => { });
                    Require(result.SequenceEqual(expected.Take(count)) && FamilyGpuComputeUtility.CompletedSubmissionTicks > before,
                        "Real local-RD download was stale, incomplete, or omitted submitted-work accounting.");
                }
                Require(submissions == 4 && registrations == (queued ? 4 : 0) &&
                    transport.Revision == (queued ? FamilyGpuHeaderReadback.QueuedRevision : FamilyGpuHeaderReadback.SynchronousRevision),
                    "Real queued download fell back or changed submission count.");
            }
        }
        finally { rd.FreeRid(buffer); }
        Emit(new { phase = "headerReadbackDevice", result = "PASS", modes = 2, sizesBytes = new[] { 32, 48, 64 },
            ownerThreadCallback = true, completesInExistingSync = true, repeatedReuse = true });
    }

    private static void TestHeaderReadbackExecutor(RenderingDevice rd)
    {
        var plan = Plan(true);
        plan.Meta[58] = plan.Meta[59] = 1;
        using var sync = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false, usePreSubmitHeaderReadback: false);
        using var queued = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false, usePreSubmitHeaderReadback: true);
        int allocationAttempts = 0;
        using var fallback = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false,
            beforeDoubleAllocation: () => { allocationAttempts++; throw new OutOfMemoryException("Smoke optional workspace."); },
            usePreSubmitHeaderReadback: true);
        int wideRightRepetitions = 0;
        // Repeat paired windows after the bounded right-hand output has armed
        // its independent lease; the left half can remain dense/dormant.
        foreach (int count in new[] { 0, 513, NeowFamilyGpuExecutor.Capacity + 513,
            NeowFamilyGpuExecutor.Capacity + 65536, NeowFamilyGpuExecutor.Capacity + 65536 })
        {
            var input = FamilyCandidateSet.Dense(new SearchBatch(0x100000003, count));
            var a = sync.ExecuteDenseWindows(input, CancellationToken.None, out var am);
            var b = queued.ExecuteDenseWindows(input, CancellationToken.None, out var bm);
            var c = fallback.ExecuteDenseWindows(input, CancellationToken.None, out var cm);
            Require(a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span) && a.ExportAbi1().Span.SequenceEqual(c.ExportAbi1().Span) &&
                am.Processed == bm.Processed && am.Processed == cm.Processed && am.CurseSurvivors == bm.CurseSurvivors &&
                am.CurseSurvivors == cm.CurseSurvivors && am.PairSurvivors == bm.PairSurvivors && am.PairSurvivors == cm.PairSurvivors &&
                am.Dispatches == bm.Dispatches && am.Dispatches == cm.Dispatches &&
                am.ReadbackBytes <= bm.ReadbackBytes && am.ReadbackBytes <= cm.ReadbackBytes,
                "Queued header transport changed single/paired/sequential output or counters.");
            if (count == NeowFamilyGpuExecutor.Capacity + 65536 && ++wideRightRepetitions == 2)
                Require(b.ExportAbi1().ToArray().Any(i => i >= (ulong)NeowFamilyGpuExecutor.Capacity) &&
                    bm.ReadbackBytes > am.ReadbackBytes,
                    "The repeated paired fixture never exercised its independently armed sparse payload lease.");
        }
        Require(allocationAttempts == 1 && queued.HeaderReadbackRevision == FamilyGpuHeaderReadback.QueuedRevision + "." + NeowSparsePayloadReadback.QueuedRevision &&
            fallback.HeaderReadbackRevision == FamilyGpuHeaderReadback.QueuedRevision + "." + NeowSparsePayloadReadback.QueuedRevision &&
            sync.HeaderReadbackRevision == FamilyGpuHeaderReadback.SynchronousRevision + "." + NeowSparsePayloadReadback.SynchronousRevision,
            "Transport revision or optional-allocation fallback was not preserved.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long beforeCancel = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        var tail = FamilyCandidateSet.Dense(new SearchBatch(0, 513));
        Expect<OperationCanceledException>(() => queued.ExecuteDenseWindows(tail, cancelled.Token, out _));
        Require(FamilyGpuComputeUtility.CompletedSubmissionTicks == beforeCancel, "Pre-cancelled download submitted work.");
        var expectedTail = sync.ExecuteDenseWindows(tail, CancellationToken.None, out _);
        var actualTail = queued.ExecuteDenseWindows(tail, CancellationToken.None, out _);
        Require(expectedTail.ExportAbi1().Span.SequenceEqual(actualTail.ExportAbi1().Span), "Cancellation broke safe workspace reuse.");
        Emit(new { phase = "headerReadbackExecutor", result = "PASS", synchronousParity = true,
            singlePairedSequentialAndEmpty = true, cancelledBeforeSubmission = true, safeReuse = true });
    }
}
