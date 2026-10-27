using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static void TestSparsePayloadReadback(RenderingDevice rd)
    {
        TestSparsePayloadState(rd);
        TestSparsePayloadEligibility(rd);
        TestSparsePayloadDevice(rd);
        TestSparsePayloadExecutor(rd);
    }

    private static void TestSparsePayloadState(RenderingDevice rd)
    {
        uint[] expected = Enumerable.Range(0, NeowSparsePayloadReadback.PrefixWords).Select(i => (uint)(i * 17 + 7)).ToArray();
        byte[] bytes = FamilyGpuComputeUtility.ToBytes(expected);
        Callable ready = default;
        int registrations = 0;
        var transport = new NeowSparsePayloadReadback(rd, true, register: (_, callback, size) =>
        {
            registrations++; ready = callback;
            Require(size == expected.Length * sizeof(uint), "Unbounded speculative payload registration.");
            return Error.Ok;
        });
        transport.ObserveValidatedCount(3);
        using (var request = transport.RegisterRecorded(default, 4096, true) ?? throw new InvalidDataException("Armed payload did not create a capture."))
        {
            Expect<InvalidOperationException>(() => request.TryGetValidatedPrefix(1, out _));
            transport.BeginSubmission(); ready.Call(bytes); Array.Fill(bytes, (byte)0);
            request.CompleteAfterSync();
            Require(request.TryGetValidatedPrefix(3, out var actual) && actual.SequenceEqual(expected.Take(3)) &&
                request.TryGetValidatedPrefix(0, out var empty) && empty.Length == 0 &&
                request.TryGetValidatedPrefix(1024, out var full) && full.SequenceEqual(expected) &&
                !request.TryGetValidatedPrefix(1025, out _) && request.TransferredBytes == 4096,
                "Speculative payload changed data, exposed a tail, or truncated a dense output.");
        }
        transport.AssertReady();
        Require(registrations == 1, "Payload registration submitted or downloaded twice.");

        int noRegistrations = 0;
        foreach (var mode in new (bool Requested, bool Public, int Count, bool Submit)[]
            { (false, true, 17, true), (true, false, 17, true), (true, true, 0, false) })
        {
            var control = new NeowSparsePayloadReadback(rd, mode.Requested, mode.Public,
                (_, _, _) => { noRegistrations++; throw new InvalidOperationException("Disabled payload registered a download."); });
            using var request = control.RegisterRecorded(default, mode.Count, mode.Submit);
            if (mode.Submit) control.BeginSubmission();
            Require(request is null && control.LastPrefetchBytes == 0 && control.TryGetValidatedPrefix(0, out _) &&
                !control.TryGetValidatedPrefix(1, out _), "Control/private/empty path allocated a capture or downloaded payload.");
        }
        Require(noRegistrations == 0, "Private or disabled transport created a payload download.");

        foreach (bool unavailable in new[] { false, true })
        {
            int rejectedRegistrations = 0;
            var fallback = new NeowSparsePayloadReadback(rd, true, register: (_, _, _) =>
            {
                rejectedRegistrations++;
                if (unavailable) throw new MissingMethodException("Synthetic unsupported binding.");
                return Error.Unavailable;
            });
            fallback.ObserveValidatedCount(7);
            for (int i = 0; i < 2; i++)
            {
                using var request = fallback.RegisterRecorded(default, 17, true);
                fallback.BeginSubmission(); request?.CompleteAfterSync();
                Require((request is not null) == (i == 0) && !fallback.TryGetValidatedPrefix(7, out _) && fallback.LastPrefetchBytes == 0 &&
                    fallback.Revision == NeowSparsePayloadReadback.FallbackRevision,
                    "Registration fallback used speculative data or changed revision.");
            }
            Require(rejectedRegistrations == 1, "Rejected registration was retried for every batch.");
        }

        int rejected = 0;
        void Reject(Action<Callable> deliver, bool early = false)
        {
            Callable callback = default;
            var invalid = new NeowSparsePayloadReadback(rd, true, register: (_, value, _) =>
            { callback = value; if (early) deliver(value); return Error.Ok; });
            invalid.ObserveValidatedCount(4);
            using (var request = invalid.RegisterRecorded(default, 4, true) ?? throw new InvalidDataException("Invalid-callback fixture was dormant."))
            {
                invalid.BeginSubmission(); if (!early) deliver(callback);
                Expect<InvalidDataException>(request.CompleteAfterSync);
            }
            Expect<InvalidOperationException>(invalid.AssertReady);
            rejected++;
        }
        byte[] valid = FamilyGpuComputeUtility.ToBytes(new uint[] { 7, 3, 1, uint.MaxValue });
        Reject(_ => { });
        Reject(callback => { callback.Call(valid); callback.Call(valid); });
        Reject(callback => callback.Call(Array.Empty<byte>()));
        Reject(callback => callback.Call(new byte[12]));
        Reject(callback => callback.Call(new byte[20]));
        Reject(callback => callback.Call(valid), early: true);
        Reject(callback => Task.Run(() => callback.Call(valid)).GetAwaiter().GetResult());

        Callable stale = default, current = default;
        var late = new NeowSparsePayloadReadback(rd, true, register: (_, callback, _) => { current = callback; return Error.Ok; });
        late.ObserveValidatedCount(4);
        using (var first = late.RegisterRecorded(default, 4, true) ?? throw new InvalidDataException("First late-callback fixture was dormant."))
        {
            late.BeginSubmission(); current.Call(valid); first.CompleteAfterSync(); stale = current;
        }
        using (var second = late.RegisterRecorded(default, 4, true) ?? throw new InvalidDataException("Second late-callback fixture was dormant."))
        {
            late.BeginSubmission(); stale.Call(valid); current.Call(valid);
            Expect<InvalidDataException>(second.CompleteAfterSync);
        }
        rejected++;

        var unexpected = new NeowSparsePayloadReadback(rd, true, register: (_, _, _) =>
            throw new InvalidOperationException("Synthetic uncertain native registration."));
        unexpected.ObserveValidatedCount(4);
        using (var request = unexpected.RegisterRecorded(default, 4, true) ?? throw new InvalidDataException("Unexpected-registration fixture was dormant."))
        {
            unexpected.BeginSubmission();
            Expect<InvalidOperationException>(request.CompleteAfterSync);
        }
        Expect<InvalidOperationException>(unexpected.AssertReady);

        using var cancellation = new CancellationTokenSource();
        Callable cancelCallback = default;
        var cancellable = new NeowSparsePayloadReadback(rd, true, register: (_, callback, _) => { cancelCallback = callback; return Error.Ok; });
        cancellable.ObserveValidatedCount(4);
        for (int i = 0; i < 2; i++)
        {
            using var request = cancellable.RegisterRecorded(default, 4, true) ?? throw new InvalidDataException("Cancellation fixture was dormant.");
            cancellable.BeginSubmission();
            if (i == 0) cancellation.Cancel();
            cancelCallback.Call(valid); request.CompleteAfterSync();
            if (i == 0) Expect<OperationCanceledException>(() => cancellation.Token.ThrowIfCancellationRequested());
            Require(request.TryGetValidatedPrefix(3, out var payload) && payload.SequenceEqual(new uint[] { 7, 3, 1 }),
                "Cancellation before delivery broke the synchronized payload or later reuse.");
        }
        Emit(new { phase = "sparsePayloadState", result = "PASS", invalidCallbacksRejected = rejected,
            independentSnapshot = true, zeroDoesNotInspectTail = true, denseFallbackNotTruncated = true,
            privateAndControlNoDownload = true, registrationFallbackOnce = true, cancellationAndReuse = true });
    }

    private static void TestSparsePayloadEligibility(RenderingDevice rd)
    {
        int registrations = 0;
        Callable callback = default;
        var transport = new NeowSparsePayloadReadback(rd, true, register: (_, ready, size) =>
        { registrations++; callback = ready; Require(size == 4096, "Adaptive prefix exceeded its bound."); return Error.Ok; });
        byte[] bytes = new byte[4096];
        void Run(uint count, int inputCount = 2048, bool submit = true, bool cancelAfterSync = false)
        {
            int before = registrations;
            bool expectRequest = transport.ActiveSparseLease > 0 && submit && inputCount > 0;
            using var request = transport.RegisterRecorded(default, inputCount, submit);
            Require((request is not null) == expectRequest, "Dormant/empty windows allocated a capture or active windows skipped one.");
            if (request is not null) Expect<InvalidOperationException>(() => transport.ObserveValidatedCount(count));
            if (submit) transport.BeginSubmission();
            if (registrations > before) callback.Call(bytes);
            request?.CompleteAfterSync();
            if (cancelAfterSync)
            {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                Expect<OperationCanceledException>(() => cancelled.Token.ThrowIfCancellationRequested());
                // Mirrors production: post-Sync cancellation prevents header
                // completion/materialization from updating its hint history.
            }
            else transport.ObserveValidatedCount(count, submit && inputCount > 0);
            Require(transport.TryGetValidatedPrefix(0, out var empty) && empty.Length == 0 &&
                transport.LastPrefetchBytes == (expectRequest ? 4096 : 0),
                "Adaptive zero result inspected speculative tail data.");
            if (!expectRequest)
                Require(!transport.TryGetValidatedPrefix(1, out _), "Dormant transport exposed a stale speculative snapshot.");
        }
        Run(0);
        Require(registrations == 0 && transport.ActiveSparseLease == 0 &&
            transport.Revision == NeowSparsePayloadReadback.QueuedRevision, "Adaptive transport did not begin dormant.");
        Run(1024);
        Require(registrations == 0 && transport.ActiveSparseLease == 64, "Boundary sparse count did not arm the next physical window.");
        Run(0, inputCount: 0, submit: false);
        Require(registrations == 0 && transport.ActiveSparseLease == 64, "Empty input consumed or activated a physical lease.");
        for (int i = 0; i < 64; i++) Run(0);
        Require(registrations == 64 && transport.ActiveSparseLease == 0, "The sparse lease did not expire after exactly 64 empty windows.");
        Run(0);
        Require(registrations == 64 && transport.Revision == NeowSparsePayloadReadback.QueuedRevision,
            "Dormant empty windows registered work or changed the algorithm identity.");
        Run(1); Run(1025);
        Require(registrations == 65 && transport.ActiveSparseLease == 0, "A dense result did not disable subsequent speculative downloads.");
        Run(1024);
        Require(registrations == 65 && transport.ActiveSparseLease == 64, "A later real sparse result did not reactivate the dormant transport.");
        Run(0, cancelAfterSync: true);
        Require(registrations == 66 && transport.ActiveSparseLease == 64, "Post-Sync cancellation changed validated history.");
        Run(0);
        Require(registrations == 67 && transport.ActiveSparseLease == 63 && transport.LastPrefetchBytes == 4096,
            "Cancellation broke request reuse or actual byte accounting.");
        transport.AssertReady();
        var privateTransport = new NeowSparsePayloadReadback(rd, true, publicOutput: false,
            register: (_, _, _) => throw new InvalidOperationException("Private history registered a payload."));
        privateTransport.ObserveValidatedCount(1);
        using (var request = privateTransport.RegisterRecorded(default, 2048, true))
        {
            privateTransport.BeginSubmission(); privateTransport.ObserveValidatedCount(1);
            Require(request is null && privateTransport.ActiveSparseLease == 0 && privateTransport.LastPrefetchBytes == 0,
                "Private output acquired an adaptive payload lease.");
        }
        Emit(new { phase = "sparsePayloadEligibility", result = "PASS", dormantByDefault = true,
            leasePhysicalWindows = 64, sparseBoundary = 1024, denseBoundary = 1025,
            expiresAndRearms = true, emptyAndPrivateDoNotArm = true, cancellationAndReuse = true,
            identityStableAcrossLeaseTransitions = true, dormantCaptureAllocated = false, dormantBytesReset = true });
    }

    private static void TestSparsePayloadDevice(RenderingDevice rd)
    {
        Rid source = FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 8192, "Smoke.SparsePayload");
        Rid headerBuffer = FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 32, "Smoke.SparseHeader");
        try
        {
            var payload = new NeowSparsePayloadReadback(rd, true);
            int submissions = 0;
            var header = new FamilyGpuHeaderReadback(rd, true, submitAndSync: family =>
            { payload.BeginSubmission(); submissions++; FamilyGpuComputeUtility.SubmitAndSync(rd, family); });
            foreach (int count in new[] { 0, 1, 17, 1024, 1025, 2048, 0, 3, 4 })
            {
                uint[] expected = Enumerable.Range(0, 2048).Select(i => checked((uint)(i + count * 3))).ToArray();
                FamilyGpuComputeUtility.Update(rd, source, expected, "Smoke.SparsePayloadUpdate");
                uint[] status = [0x4e464d52, 1, (uint)count, 0, 2048, 42, 0, 0];
                FamilyGpuComputeUtility.Update(rd, headerBuffer, status, "Smoke.SparseHeaderUpdate");
                bool expectedQueued = payload.ActiveSparseLease > 0;
                using var request = payload.RegisterRecorded(source, 2048, true);
                Require((request is not null) == expectedQueued, "The device path allocated a dormant capture or omitted an armed one.");
                Require(header.CompleteRecorded(headerBuffer, 8, true, "Smoke.SparsePayload", () => { }).SequenceEqual(status),
                    "Combined header+payload submission returned a stale header.");
                request?.CompleteAfterSync();
                payload.ObserveValidatedCount((uint)count);
                bool prefetched = payload.TryGetValidatedPrefix((uint)count, out var actual);
                Require(prefetched == (count == 0 || expectedQueued && count <= NeowSparsePayloadReadback.PrefixWords), "Unexpected adaptive sparse payload coverage.");
                if (!prefetched) actual = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(source, 0, (uint)count * 4), (uint)count);
                Require(actual.SequenceEqual(expected.Take(count)) && payload.LastPrefetchBytes == (expectedQueued ? 4096 : 0),
                    "Real device payload was stale, partial, or reused from an earlier batch.");
            }
            Require(submissions == 9 && payload.Revision == NeowSparsePayloadReadback.QueuedRevision,
                "Combined readback used an extra submission or unexpectedly fell back.");
        }
        finally { rd.FreeRid(headerBuffer); rd.FreeRid(source); }
        Emit(new { phase = "sparsePayloadDevice", result = "PASS", sameSubmissionAsHeader = true,
            counts = new[] { 0, 1, 17, 1024, 1025, 2048, 0, 3, 4 }, fullDenseFallback = true, repeatedReuse = true });
    }

    private static void TestSparsePayloadExecutor(RenderingDevice rd)
    {
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        var plan = PrefixScopePlan(false);
        using var sync = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false, usePreSubmitHeaderReadback: false);
        using var queued = new NeowFamilyGpuExecutor(rd, plan, 42, useIndirectStaging: false, usePreSubmitHeaderReadback: true);
        using var privateOutput = new PrivateOrdinalBuffer(rd);
        using var privateExecutor = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: privateOutput,
            useIndirectStaging: false, usePreSubmitHeaderReadback: true);
        int nonzero = 0, denseFallbacks = 0;
        foreach (var batch in new[] { new SearchBatch(0, 0), new SearchBatch(0, 1), new SearchBatch(0, 65536),
            new SearchBatch(zzfz, 1031), new SearchBatch(PrefixHashCache.SpaceSize - 513, 513) })
        {
            var dense = FamilyCandidateSet.Dense(batch);
            foreach (var input in new[] { dense, FamilyCandidateSet.FromSortedAbi1(batch,
                Enumerable.Range(0, batch.BatchCandidateCount).Where(i => (i & 3) == 1).Select(i => (ulong)i).ToArray()) })
            {
                var a = sync.Execute(input, CancellationToken.None, out var am);
                var b = queued.Execute(input, CancellationToken.None, out var bm);
                ulong[] expected = PrefixScopeExpected(plan, batch, input.EnumerateLogicalOrdinals().ToArray());
                Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected) &&
                    am.Dispatches == bm.Dispatches && am.Survivors == bm.Survivors,
                    "Sparse prefetch changed a Dense/Compact population, ordinal offset, or dispatch count.");
                long prefixBytes = queued.LastSparsePayloadPrefetchBytes;
                long expectedBytes = 32 + prefixBytes + ((long)b.Count * sizeof(uint) > prefixBytes ? (long)b.Count * sizeof(uint) : 0);
                Require(bm.ReadbackBytes == expectedBytes, "Speculative payload metrics omitted empty-tail or fallback traffic.");
                if (b.Count > 0) nonzero++;
                if (b.Count > NeowSparsePayloadReadback.PrefixWords) denseFallbacks++;
            }
            int count = privateExecutor.ExecutePrivateOutput(dense, CancellationToken.None, out var pm);
            Require(privateOutput.ReadForVerification(batch, count).ExportAbi1().Span.SequenceEqual(PrefixScopeExpected(plan, batch)) &&
                pm.ReadbackBytes == 32 && privateExecutor.HeaderReadbackRevision.EndsWith("." + NeowSparsePayloadReadback.PrivateRevision),
                "Private output gained a speculative payload download.");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var tail = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 1031));
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => queued.Execute(tail, cancelled.Token, out _));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks &&
            sync.Execute(tail, CancellationToken.None, out _).ExportAbi1().Span.SequenceEqual(
                queued.Execute(tail, CancellationToken.None, out _).ExportAbi1().Span) && nonzero > 0 && denseFallbacks > 0,
            "Pre-cancellation submitted work, poisoned reuse, or fixtures never exercised nonzero/dense fallback.");
        Emit(new { phase = "sparsePayloadExecutor", result = "PASS", denseCompactAndPrivate = true,
            originalCpuOracle = true, nonzero, denseFallbacks, offsetsAndPrefixCrossing = true,
            emptyAndSpaceEnd = true, cancellationAndReuse = true, windowAdmissionUnchanged = true });
    }
}
