using Godot;
using RolltheSpire2.Bootstrap;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Owner-thread transport for small status buffers. A local RD has one frame;
/// its Sync delivers a download registered before Submit. The callback only
/// captures managed data and never calls back into the RenderingDevice.
/// </summary>
internal sealed class FamilyGpuHeaderReadback
{
    internal const string QueuedRevision = "PreSubmitHeaderDownload.20261021.v1";
    internal const string SynchronousRevision = "PostSyncHeaderControl.20261021.v1";
    internal const string FallbackRevision = "PostSyncHeaderRegistrationFallback.20261021.v1";
    internal const string EmptyRevision = "EmptyInputSynchronousHeader.20261021.v1";
    internal static bool Requested => SearchOptimizationScope.Current.AsyncHeaderReadback &&
        System.Environment.GetEnvironmentVariable("RT2_NEOW_SYNC_HEADER") != "1";
    internal static string RequestedRevision => Requested ? QueuedRevision : SynchronousRevision;

    // These adapters also let the smoke host exercise the production state
    // machine without provoking driver errors or submitting an invalid kernel.
    internal delegate Error RegisterDownload(Rid source, Callable callback, uint sizeBytes);
    internal delegate byte[] ReadSynchronously(Rid source, uint sizeBytes);

    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private readonly bool _requested;
    private readonly RegisterDownload _register;
    private readonly Action<string> _submitAndSync;
    private readonly ReadSynchronously _readSynchronously;
    private long _requestId, _activeRequestId;
    private string? _callbackFault;
    private bool _disabled, _faulted;
    internal string Revision { get; private set; }
    internal double LastSubmitMs { get; private set; }
    internal double LastSyncMs { get; private set; }

    internal FamilyGpuHeaderReadback(RenderingDevice rd, bool requested,
        RegisterDownload? register = null, Action<string>? submitAndSync = null,
        ReadSynchronously? readSynchronously = null)
    {
        _requested = requested;
        Revision = requested ? QueuedRevision : SynchronousRevision;
        _register = register ?? ((source, callback, size) => rd.BufferGetDataAsync(source, callback, 0, size));
        _submitAndSync = submitAndSync ?? (family =>
        {
            var times = FamilyGpuComputeUtility.SubmitAndSync(rd, family);
            LastSubmitMs = times.SubmitMs; LastSyncMs = times.SyncMs;
        });
        _readSynchronously = readSynchronously ?? ((source, size) => rd.BufferGetData(source, 0, size));
    }

    internal uint[] CompleteRecorded(Rid source, uint expectedWords, bool submit,
        string family, Action synchronized)
    {
        AssertOwner();
        LastSubmitMs = LastSyncMs = 0;
        if (_faulted || _callbackFault is not null) throw new InvalidOperationException("FamilyGpu.HeaderReadbackFaulted");
        if (expectedWords == 0) throw new ArgumentOutOfRangeException(nameof(expectedWords));
        uint expectedBytes = checked(expectedWords * sizeof(uint));
        Capture? capture = null;
        bool accepted = false;
        Exception? registrationFailure = null;
        if (_requested && !_disabled && submit)
        {
            capture = new Capture(this, checked(++_requestId), expectedBytes);
            Volatile.Write(ref _activeRequestId, _requestId);
            Error registration;
            string? unsupported = null;
            try { registration = _register(source, capture.Callback, expectedBytes); }
            // Missing API support is an initialization/registration failure.
            // Other exceptions retain fail-closed behavior; no submitted work
            // or invalid downloaded result is retried with another transport.
            catch (Exception ex) when (ex is MissingMethodException or EntryPointNotFoundException or NotSupportedException)
            {
                registration = Error.Unavailable;
                unsupported = ex.GetType().Name;
            }
            catch (Exception ex)
            {
                // A binding failure may occur after native registration. Keep
                // the callback alive and finish this command graph once, then
                // fail rather than guessing that replay or fallback is safe.
                registration = Error.Failed;
                registrationFailure = ex;
            }
            accepted = registration == Error.Ok && registrationFailure is null;
            if (accepted) Revision = QueuedRevision;
            else if (registrationFailure is null)
            {
                _disabled = true;
                Revision = FallbackRevision;
                RuntimeLog.TryBackgroundWarning($"nHeaderReadbackFallback=true;revision={Revision};registration={registration};reason={unsupported ?? "Rejected"};scope=BeforeExplicitSubmitOnly;submittedWorkRetried=false");
            }
        }
        else Revision = !submit ? EmptyRevision : _disabled ? FallbackRevision : SynchronousRevision;

        try
        {
            if (submit)
            {
                capture?.BeginSubmission();
                // This is the same submission in both modes. In particular a
                // rejected registration never re-records or re-runs compute.
                _submitAndSync(family);
            }
            synchronized();
            if (registrationFailure is not null)
                throw new InvalidOperationException("FamilyGpu.HeaderRegistrationFailed", registrationFailure);
            if (_callbackFault is not null) throw new InvalidDataException(_callbackFault);
            byte[] bytes = accepted ? capture!.CompleteAfterSync() : _readSynchronously(source, expectedBytes);
            return FamilyGpuComputeUtility.FromUInt32Bytes(bytes, expectedWords);
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            // Source buffers remain executor-owned and the callable/capture
            // remain strongly reachable through the entire native Sync.
            capture?.Close();
            GC.KeepAlive(capture);
        }
    }

    private void AssertOwner()
    {
        if (_owner != System.Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("FamilyGpu.HeaderReadbackOwnerMismatch");
    }

    private void CallbackFault(string detail) => Interlocked.CompareExchange(ref _callbackFault, detail, null);

    private sealed class Capture
    {
        private readonly FamilyGpuHeaderReadback _transport;
        private readonly long _id;
        private readonly uint _expectedBytes;
        private int _phase, _calls;
        private byte[]? _bytes;
        internal Callable Callback { get; }

        internal Capture(FamilyGpuHeaderReadback transport, long id, uint expectedBytes)
        {
            _transport = transport; _id = id; _expectedBytes = expectedBytes;
            Callback = Callable.From<byte[]>(bytes => Save(_id, bytes));
        }

        internal void BeginSubmission() => Volatile.Write(ref _phase, 1);
        internal void Close()
        {
            Volatile.Write(ref _phase, 2);
            Interlocked.CompareExchange(ref _transport._activeRequestId, 0, _id);
        }

        private void Save(long requestId, byte[] bytes)
        {
            // Never throw through a native callback. Report all errors after
            // Sync, once the device no longer owns in-flight work.
            if (Interlocked.Increment(ref _calls) != 1 || requestId != _id ||
                requestId != Volatile.Read(ref _transport._activeRequestId) ||
                Volatile.Read(ref _phase) != 1 ||
                System.Environment.CurrentManagedThreadId != _transport._owner)
            {
                _transport.CallbackFault("FamilyGpu.HeaderCallbackIdentityOrLifetimeInvalid");
                return;
            }
            if (bytes is null || (ulong)bytes.LongLength != _expectedBytes)
            {
                _transport.CallbackFault("FamilyGpu.HeaderCallbackLengthInvalid");
                return;
            }
            // Godot reuses its native PackedByteArray for later callbacks.
            // Retain an independent managed snapshot until completion checks.
            try { _bytes = (byte[])bytes.Clone(); }
            catch (Exception) { _transport.CallbackFault("FamilyGpu.HeaderCallbackCaptureFailed"); }
        }

        internal byte[] CompleteAfterSync()
        {
            if (Volatile.Read(ref _calls) != 1 || _bytes is null)
                throw new InvalidDataException("FamilyGpu.HeaderCallbackMissingOrRepeated");
            return _bytes;
        }
    }
}
