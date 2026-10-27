using Godot;
using RolltheSpire2.Bootstrap;

namespace RolltheSpire2.Search.FamilyExecution;

// A bounded speculative download joins the existing header's single submit.
// Only validated header counts decide which words are materialized. Dense
// outputs retain a complete synchronous fallback; private outputs are untouched.
internal sealed class NeowSparsePayloadReadback
{
    internal const int PrefixWords = 1024;
    internal const int SparseLeaseWindows = 64;
    // This is the policy identity, including its dormant state. A history
    // transition never changes the physical cost key into another algorithm.
    internal const string QueuedRevision = "AdaptiveSparsePayload4KiB.Lease64.FastDormant.20261022.v3";
    internal const string SynchronousRevision = "PostSyncPayloadControl.20261022.v1";
    internal const string FallbackRevision = "PostSyncPayloadRegistrationFallback.20261022.v1";
    internal const string PrivateRevision = "PrivateOutputNoPayloadDownload.20261022.v1";
    internal const string EmptyRevision = "EmptyInputNoPayloadDownload.20261022.v1";
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private readonly bool _requested, _publicOutput;
    private readonly FamilyGpuHeaderReadback.RegisterDownload _register;
    private long _requestId, _activeRequestId;
    private string? _callbackFault;
    private bool _disabled, _faulted;
    private int _sparseLease;
    private Request? _active;
    internal string Revision { get; private set; }
    internal int ActiveSparseLease => _sparseLease;
    internal long LastPrefetchBytes { get; private set; }

    internal NeowSparsePayloadReadback(RenderingDevice rd, bool requested, bool publicOutput = true,
        FamilyGpuHeaderReadback.RegisterDownload? register = null)
    {
        _requested = requested; _publicOutput = publicOutput;
        _register = register ?? ((source, callback, size) => rd.BufferGetDataAsync(source, callback, 0, size));
        Revision = !publicOutput ? PrivateRevision : requested ? QueuedRevision : SynchronousRevision;
    }

    internal void AssertReady()
    {
        AssertOwner();
        if (_faulted || _callbackFault is not null || _active is not null)
            throw new InvalidOperationException("N.PayloadReadbackFaultedOrPending");
    }

    internal Request? RegisterRecorded(Rid source, int inputCount, bool submit)
    {
        AssertReady();
        if (inputCount < 0 || inputCount > NeowFamilyGpuExecutor.Capacity)
            throw new ArgumentOutOfRangeException(nameof(inputCount));
        bool queue = _requested && _publicOutput && !_disabled && _sparseLease > 0 && submit && inputCount > 0;
        LastPrefetchBytes = 0;
        if (!queue)
        {
            Revision = !_publicOutput ? PrivateRevision : !submit || inputCount == 0 ? EmptyRevision :
                _disabled ? FallbackRevision : _requested ? QueuedRevision : SynchronousRevision;
            // No callback or native request exists in these modes. Preserve
            // the original header/synchronous-payload path without allocating
            // a managed capture or performing request lifetime atomics.
            return null;
        }
        uint words = checked((uint)Math.Min(PrefixWords, inputCount));
        var request = new Request(this, checked(++_requestId), words);
        _active = request;
        Volatile.Write(ref _activeRequestId, _requestId);
        Error registration;
        try { registration = _register(source, request.Callback, checked(words * sizeof(uint))); }
        catch (Exception ex) when (ex is MissingMethodException or EntryPointNotFoundException or NotSupportedException)
        { registration = Error.Unavailable; }
        catch (Exception ex)
        {
            // Native registration may have succeeded before its binding threw.
            // Keep the capture alive, finish the recorded work once, then fail.
            registration = Error.Failed; request.RegistrationFailure = ex;
        }
        request.Accepted = registration == Error.Ok && request.RegistrationFailure is null;
        if (request.Accepted) { Revision = QueuedRevision; LastPrefetchBytes = request.TransferredBytes; }
        else if (request.RegistrationFailure is null)
        {
            _disabled = true; Revision = FallbackRevision;
            RuntimeLog.TryBackgroundWarning($"nSparsePayloadFallback=true;revision={Revision};registration={registration};scope=BeforeExplicitSubmitOnly;submittedWorkRetried=false");
        }
        return request;
    }

    // The executor calls this only after the complete header and its composite
    // counters have passed their existing validators. This is a transport hint:
    // no candidate is accepted/rejected, and a wrong hint only costs a fallback.
    internal void ObserveValidatedCount(uint count, bool hasPhysicalInput = true)
    {
        AssertOwner();
        if (_faulted || _callbackFault is not null || _active is { IsCompleted: false })
            throw new InvalidOperationException("N.PayloadObservationBeforeValidatedCompletion");
        if (!_requested || !_publicOutput || _disabled || !hasPhysicalInput) return;
        if (count > 0 && count <= PrefixWords) _sparseLease = SparseLeaseWindows;
        else if (count > PrefixWords) _sparseLease = 0;
        else if (_sparseLease > 0) _sparseLease--;
    }

    internal void BeginSubmission() => _active?.BeginSubmission();
    internal long TransferredBytes => _active?.TransferredBytes ?? 0;
    internal bool TryGetValidatedPrefix(uint count, out uint[] packed)
    {
        if (_active is not null) return _active.TryGetValidatedPrefix(count, out packed);
        packed = [];
        return count == 0;
    }
    internal void MarkFaulted() => _faulted = true;
    private void AssertOwner()
    {
        if (_owner != System.Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("N.PayloadReadbackOwnerMismatch");
    }
    private void CallbackFault(string detail) => Interlocked.CompareExchange(ref _callbackFault, detail, null);

    internal sealed class Request : IDisposable
    {
        private readonly NeowSparsePayloadReadback _transport;
        private readonly long _id;
        private readonly uint _words;
        private int _phase, _calls;
        private byte[]? _bytes;
        private bool _completed;
        internal bool IsCompleted => _completed;
        internal Callable Callback { get; }
        internal bool Accepted { get; set; }
        internal Exception? RegistrationFailure { get; set; }
        internal long TransferredBytes => Accepted ? checked((long)_words * sizeof(uint)) : 0;

        internal Request(NeowSparsePayloadReadback transport, long id, uint words)
        {
            _transport = transport; _id = id; _words = words;
            Callback = words == 0 ? default : Callable.From<byte[]>(Save);
        }
        internal void BeginSubmission() => Volatile.Write(ref _phase, 1);
        private void Save(byte[] bytes)
        {
            // No exception may cross a native callback. A stale, repeated,
            // foreign-thread or early callback poisons the transport instead.
            if (Interlocked.Increment(ref _calls) != 1 || _id != Volatile.Read(ref _transport._activeRequestId) ||
                Volatile.Read(ref _phase) != 1 || System.Environment.CurrentManagedThreadId != _transport._owner)
            { _transport.CallbackFault("N.PayloadCallbackIdentityOrLifetimeInvalid"); return; }
            if (bytes is null || (ulong)bytes.LongLength != (ulong)_words * sizeof(uint))
            { _transport.CallbackFault("N.PayloadCallbackLengthInvalid"); return; }
            try { _bytes = (byte[])bytes.Clone(); }
            catch (Exception) { _transport.CallbackFault("N.PayloadCallbackCaptureFailed"); }
        }
        internal void CompleteAfterSync()
        {
            _transport.AssertOwner();
            try
            {
                if (RegistrationFailure is not null)
                    throw new InvalidOperationException("N.PayloadRegistrationFailed", RegistrationFailure);
                if (_transport._callbackFault is not null) throw new InvalidDataException(_transport._callbackFault);
                if (Accepted && (Volatile.Read(ref _calls) != 1 || _bytes is null))
                    throw new InvalidDataException("N.PayloadCallbackMissingOrRepeated");
                if (!Accepted && Volatile.Read(ref _calls) != 0)
                    throw new InvalidDataException("N.UnacceptedPayloadCallbackDelivered");
                _completed = true;
            }
            catch { _transport._faulted = true; throw; }
        }
        internal bool TryGetValidatedPrefix(uint count, out uint[] packed)
        {
            _transport.AssertOwner();
            if (!_completed || Volatile.Read(ref _phase) == 2)
                throw new InvalidOperationException("N.PayloadReadBeforeCompletionOrAfterClose");
            if (_transport._callbackFault is not null || Accepted && Volatile.Read(ref _calls) != 1)
                throw new InvalidDataException("N.PayloadCallbackChangedAfterSync");
            if (count == 0) { packed = []; return true; }
            if (!Accepted || count > _words) { packed = []; return false; }
            // Never inspect speculative tail words beyond the validated count.
            packed = FamilyGpuComputeUtility.FromUInt32Bytes(_bytes!.AsSpan(0, checked((int)count * sizeof(uint))).ToArray(), count);
            return true;
        }
        public void Dispose()
        {
            _transport.AssertOwner();
            Volatile.Write(ref _phase, 2);
            Interlocked.CompareExchange(ref _transport._activeRequestId, 0, _id);
            if (ReferenceEquals(_transport._active, this)) _transport._active = null;
            GC.KeepAlive(this);
        }
    }
}
