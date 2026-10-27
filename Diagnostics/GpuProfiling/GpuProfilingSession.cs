using System.Diagnostics;
using System.Globalization;
using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.FamilyExecution;

namespace RolltheSpire2.Diagnostics.GpuProfiling;

// Included only by EnableGpuProfiling=true. Never supplies prices, production
// timing observations or search results. All storage is bounded per executor.
internal sealed class GpuProfilingSession : IDisposable
{
    internal const int DefaultSampleEvery = 256;
    private const int MaxCapturedMarkers = 4096;
    [ThreadStatic] private static GpuProfilingSession? _current;
    private static long _nextSession;
    internal static GpuProfilingSession? Current => _current;

    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private readonly long _session = Interlocked.Increment(ref _nextSession);
    private readonly int _sampleEvery;
    private readonly Action<string> _capture, _log;
    private readonly Func<uint> _capturedCount;
    private readonly Func<uint, string> _capturedName;
    private readonly Func<uint, ulong> _capturedGpuTime;
    private long _batches, _samples, _discarded, _roots, _survivors, _submissions, _windows;
    private int _depth, _sampleWindows, _sampleSubmissions;
    private bool _sampled, _disabled, _disposed, _completed;
    private string? _beginMarker, _endMarker;
    private long _windowRecordStarted, _submitStarted;
    private double _sampleGpuMs, _sampleRecordMs, _sampleSubmitWaitMs;
    private double _gpuMs, _recordMs, _submitWaitMs, _hostMs, _readbackMs, _canonicalizeMs;
    private NeowFamilyGpuMetrics _metrics;

    // Raw delegates permit meaningful state-machine tests using the production
    // collector. The default adapter calls only the existing owner-thread RD.
    internal GpuProfilingSession(RenderingDevice rd, int sampleEvery = DefaultSampleEvery,
        Action<string>? capture = null, Func<uint>? capturedCount = null,
        Func<uint, string>? capturedName = null, Func<uint, ulong>? capturedGpuTime = null,
        Action<string>? log = null)
    {
        if (sampleEvery < 1) throw new ArgumentOutOfRangeException(nameof(sampleEvery));
        _sampleEvery = sampleEvery;
        _capture = capture ?? (name => rd.CaptureTimestamp(name));
        _capturedCount = capturedCount ?? (() => rd.GetCapturedTimestampsCount());
        _capturedName = capturedName ?? (index => rd.GetCapturedTimestampName(index));
        _capturedGpuTime = capturedGpuTime ?? (index => rd.GetCapturedTimestampGpuTime(index));
        _log = log ?? (line => RuntimeLog.TryBackgroundInfo(line));
    }

    internal void BeginBatch(int inputCount) => Guard(() =>
    {
        AssertOwner();
        if (_current is { } parent)
        {
            parent._depth = checked(parent._depth + 1);
            return;
        }
        if (_disposed) return;
        _current = this;
        _depth = 1;
        _batches = checked(_batches + 1);
        _sampled = !_disabled && (_batches == 1 || _batches % _sampleEvery == 0);
        _completed = false;
        _sampleWindows = _sampleSubmissions = 0;
        _sampleGpuMs = _sampleRecordMs = _sampleSubmitWaitMs = 0;
        _windowRecordStarted = _submitStarted = 0;
        _beginMarker = _endMarker = null;
        _metrics = default;
        if (inputCount < 0) throw new InvalidDataException("GpuProfiling.InputCountInvalid");
    });

    internal void WindowBegin() => Guard(() =>
    {
        AssertOwner();
        if (!Active) return;
        if (_windowRecordStarted != 0) throw new InvalidOperationException("GpuProfiling.WindowAlreadyOpen");
        if (_beginMarker is null)
        {
            // Timestamp nodes constrain graph ordering. Place one pair around
            // the whole recorded submission, never between independent windows.
            _beginMarker = $"RT2GpuProfiling.{_session}.{_batches}.{_sampleSubmissions}.Begin";
            _capture(_beginMarker);
        }
        _windowRecordStarted = Stopwatch.GetTimestamp();
        _sampleWindows = checked(_sampleWindows + 1);
    });

    internal void WindowEnd() => Guard(() =>
    {
        AssertOwner();
        if (!Active) return;
        if (_windowRecordStarted == 0) throw new InvalidOperationException("GpuProfiling.WindowNotOpen");
        _sampleRecordMs += Stopwatch.GetElapsedTime(_windowRecordStarted).TotalMilliseconds;
        _windowRecordStarted = 0;
    });

    internal void BeforeSubmit() => Guard(() =>
    {
        AssertOwner();
        if (!Active || _beginMarker is null) return; // Empty input has no dispatch.
        if (_windowRecordStarted != 0 || _endMarker is not null || _submitStarted != 0)
            throw new InvalidOperationException("GpuProfiling.SubmissionBoundaryInvalid");
        _endMarker = $"RT2GpuProfiling.{_session}.{_batches}.{_sampleSubmissions}.End";
        _capture(_endMarker);
        _submitStarted = Stopwatch.GetTimestamp();
    });

    internal void AfterSync() => Guard(() =>
    {
        AssertOwner();
        if (!Active || _beginMarker is null) return;
        if (_submitStarted == 0 || _endMarker is null) throw new InvalidOperationException("GpuProfiling.SyncWithoutSubmission");
        double submittedScopeMs = Stopwatch.GetElapsedTime(_submitStarted).TotalMilliseconds;
        _sampleSubmitWaitMs += submittedScopeMs;
        _submitStarted = 0;
        uint count = _capturedCount();
        if (count > MaxCapturedMarkers) throw new InvalidDataException("GpuProfiling.TimestampCountInvalid");
        int begins = 0, ends = 0;
        ulong begin = 0, end = 0;
        for (uint i = 0; i < count; i++)
        {
            string name = _capturedName(i);
            if (name == _beginMarker) { begins++; begin = _capturedGpuTime(i); }
            else if (name == _endMarker) { ends++; end = _capturedGpuTime(i); }
        }
        if (begins != 1 || ends != 1)
            throw new InvalidDataException("GpuProfiling.TimestampPairInvalid");
        _sampleGpuMs += GpuIntervalMilliseconds(begin, end, submittedScopeMs);
        _sampleSubmissions = checked(_sampleSubmissions + 1);
        _beginMarker = _endMarker = null;
    });

    internal void BatchCompleted(NeowFamilyGpuMetrics metrics) => Guard(() =>
    {
        AssertOwner();
        if (!Active || _depth != 1) return; // Nested sequential subwindows are inclusive.
        _metrics = metrics;
        _completed = true;
    });

    internal void EndBatch()
    {
        // This also closes failed or cancelled scopes. No RD operations occur
        // while unwinding and no diagnostic exception can escape to search.
        bool close = false;
        try
        {
            AssertOwner();
            if (!ReferenceEquals(_current, this)) return;
            if (_depth > 1) { _depth--; return; }
            close = true;
            if (_sampled)
            {
                if (_completed && _beginMarker is null && _windowRecordStarted == 0 && _submitStarted == 0)
                {
                    _samples = checked(_samples + 1);
                    _roots = checked(_roots + _metrics.Processed);
                    _survivors = checked(_survivors + _metrics.Survivors);
                    _submissions = checked(_submissions + _sampleSubmissions);
                    _windows = checked(_windows + _sampleWindows);
                    _gpuMs += _sampleGpuMs;
                    _recordMs += _sampleRecordMs;
                    _submitWaitMs += _sampleSubmitWaitMs;
                    _hostMs += _metrics.CanonicalMs;
                    _readbackMs += _metrics.ReadbackMs;
                    _canonicalizeMs += _metrics.CanonicalizeMs;
                    if (_samples == 1 || _samples % 16 == 0) WriteSummary("Sampled");
                }
                else _discarded = checked(_discarded + 1);
            }
        }
        catch (Exception ex) { Disable(ex); }
        finally
        {
            if (close && ReferenceEquals(_current, this))
            {
                _depth = 0; _sampled = false;
                _beginMarker = _endMarker = null;
                _windowRecordStarted = _submitStarted = 0;
                _current = null;
            }
        }
    }

    internal GpuProfilingSnapshot Snapshot() => new(_batches, _samples, _discarded, _roots, _survivors,
        _submissions, _windows, _disabled, _beginMarker is null ? 0 : _endMarker is null ? 1 : 2,
        _gpuMs, _recordMs, _submitWaitMs, _hostMs);

    internal static double GpuIntervalMilliseconds(ulong begin, ulong end, double submittedScopeMs)
    {
        if (end == 0 || end < begin || !double.IsFinite(submittedScopeMs) || submittedScopeMs < 0)
            throw new InvalidDataException("GpuProfiling.TimestampPairInvalid");
        // Godot 4.5.1's D3D12 and Vulkan drivers return nanoseconds, despite
        // the C# XML documentation describing GPU timestamps as microseconds.
        // Subtract before converting to double to preserve large-epoch deltas.
        double elapsedMs = (end - begin) / 1_000_000d;
        // The entire interval was recorded before this existing Submit/Sync.
        // Compare this ONE outer GPU span with its own completed CPU scope;
        // do not sum overlapping windows or compare unrelated aggregate clocks.
        // Allow 1 ms or 5% for timestamp quantization and cross-clock precision.
        double toleranceMs = Math.Max(1d, submittedScopeMs * .05d);
        if (elapsedMs > submittedScopeMs + toleranceMs)
            throw new InvalidDataException("GpuProfiling.GpuSpanExceedsSubmittedScope");
        return elapsedMs;
    }

    private bool Active => _sampled && !_disabled && ReferenceEquals(_current, this);
    private void AssertOwner()
    {
        if (System.Environment.CurrentManagedThreadId != _owner) throw new InvalidOperationException("GpuProfiling.OwnerMismatch");
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { Disable(ex); }
    }
    private void Disable(Exception failure)
    {
        bool first = !_disabled;
        _disabled = true;
        if (_sampled) _discarded++;
        _sampled = false;
        _beginMarker = _endMarker = null;
        _windowRecordStarted = _submitStarted = 0;
        if (!first) return;
        try { _log($"gpuProfiling=true;phase=Disabled;session={_session};reason={failure.GetType().Name};searchUnaffected=true;calibration=false"); }
        catch (Exception) { /* Diagnostics must not affect search or cleanup. */ }
    }
    private void WriteSummary(string phase)
    {
        static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
        _log($"gpuProfiling=true;phase={phase};session={_session};sampleEvery={_sampleEvery};batchesSeen={_batches};samples={_samples};discarded={_discarded};roots={_roots};survivors={_survivors};submissions={_submissions};physicalWindows={_windows};gpuRecordedSpanMs={F(_gpuMs)};gpuTimestampUnit=Nanoseconds;nanosecondsPerMillisecond=1000000;submittedScopePlausibilityCheck=true;cpuComputeRecordMs={F(_recordMs)};cpuRegistrationSubmitWaitMs={F(_submitWaitMs)};hostCanonicalMs={F(_hostMs)};hostReadbackMs={F(_readbackMs)};hostCanonicalizeMs={F(_canonicalizeMs)};gpuScope=RecordedWindowsAndInterWindowTransfers;headerDownloadOutsideGpuSpan=true;stageBreakdownUnavailable=true;markersBetweenIndependentWindows=false;instrumentationMayAffectOrdering=true;timingCategoriesOverlap=true;calibration=false;diagnosticOnly=true");
    }
    public void Dispose()
    {
        try
        {
            AssertOwner();
            if (_disposed) return;
            if (ReferenceEquals(_current, this))
            {
                if (_sampled) _discarded++;
                _current = null;
            }
            _depth = 0; _sampled = false;
            _beginMarker = _endMarker = null;
            _windowRecordStarted = _submitStarted = 0;
            WriteSummary("Disposed");
        }
        catch (Exception ex) { Disable(ex); }
        finally { _disposed = true; }
    }
}

internal readonly record struct GpuProfilingSnapshot(long BatchesSeen, long Samples, long Discarded,
    long Roots, long Survivors, long Submissions, long PhysicalWindows, bool Disabled, int PendingMarkers,
    double GpuRecordedSpanMs, double CpuComputeRecordMs, double CpuRegistrationSubmitWaitMs, double HostCanonicalMs);
