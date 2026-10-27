using System.Diagnostics;
using Godot;

namespace RolltheSpire2.Search.FamilyExecution;

// Bounded E output allocation, then the original W K4 executor.
// This retains the E 1M dispatch windows and W scratch/window geometry.
internal sealed class EwPrivateGpuExecutor : IDisposable
{
    private readonly GpuCostSamples _logTotals = new();

    internal const int MaxRoots = PrivateOrdinalBuffer.Capacity;
    private readonly RenderingDevice _rd;
    private readonly FamilyGpuHeaderReadback _headerReadback;
    internal string HeaderReadbackRevision => CombineHeaderReadbackRevisions(
        _headerReadback.Revision, _world.HeaderReadbackRevision);
    internal static string CombineHeaderReadbackRevisions(string eventRevision, string worldRevision) =>
        eventRevision == worldRevision ? eventRevision : "E." + eventRevision + ".W." + worldRevision;
    private readonly List<Rid> _owned = [];
    private readonly Rid _batch, _header, _output, _pipeline, _uniforms;
    private readonly SharedVisibleSeedGpuCache? _seedCache;
    private readonly WorldFamilyGpuExecutor _world;
    private readonly PrivateOrdinalBuffer _ordinals;
    private readonly EventResultFamilyGpuExecutor? _referenceE;
    private readonly WorldFamilyGpuExecutor? _referenceW;
    private readonly int _capacity;
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private bool _disposed;
    internal double SetupMs { get; }
    internal string Device { get; }
    internal long RetainedBytes => MaxRoots * 4L;

    internal EwPrivateGpuExecutor(RenderingDevice rd, EventResultFamilyPlan e, WorldFamilyGpuPlan w, bool verify)
    {
        _rd = rd; _capacity = e.Capacity;
        _headerReadback = new(rd, FamilyGpuHeaderReadback.Requested);
        var timer = Stopwatch.StartNew();
        Rid Add(Rid r) { _owned.Add(r); return r; }
        Rid Data(uint[] a) => Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(a)));
        try
        {
            var buffers = new Rid[6];
            buffers[0] = _batch = Data(new uint[8]);
            buffers[1] = Data(e.Buffers[0]); buffers[2] = Data(e.Buffers[1]);
            buffers[3] = Data([0]); buffers[4] = _header = Data(Header());
            _ordinals = new PrivateOrdinalBuffer(rd);
            buffers[5] = _output = _ordinals.Buffer;
            string shaderSource = e.ShaderSource();
            _seedCache = SharedVisibleSeedGpuCache.Attach(rd, _owned, ref shaderSource, ref buffers, "EW.EventResult");
            Rid shader = Add(FamilyGpuComputeUtility.CompileShader(rd, shaderSource, "E.Y1Private"));
            _pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, "EW.EventResult"));
            if (!rd.ComputePipelineIsValid(_pipeline)) throw new InvalidOperationException("E.Y1PipelineInvalid");
            _uniforms = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers, "EW.EventResult"));
            _world = new WorldFamilyGpuExecutor(rd, w, _output);
            if (verify)
            {
                _referenceE = new EventResultFamilyGpuExecutor(rd, e);
                _referenceW = new WorldFamilyGpuExecutor(rd, w);
            }
            Device = rd.GetDeviceName(); SetupMs = timer.Elapsed.TotalMilliseconds;
        }
        catch (Exception failure)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(failure, Release, "EW");
            throw;
        }
    }
    private static uint[] Header() => [0x45524652u, 1, 0, 0, 0, 0x45524652u, 0, 0];

    internal FamilyCandidateSet Execute(FamilyCandidateSet input, CancellationToken token, out double canonicalMs)
    {
        if (System.Environment.CurrentManagedThreadId != _owner) throw new InvalidOperationException("EW.OwnerMismatch");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!input.IsDense || input.Count > MaxRoots) throw new InvalidOperationException("EW.DenseBatchBoundViolation");
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew(); double dispatchMs = 0;
        if (input.Count > 0) _seedCache?.Refresh(input.Batch);
        FamilyGpuComputeUtility.Update(_rd, _header, Header(), "EW.EHeader");
        int dispatches = 0; uint[]? h = null; double headerTransportMs = 0;
        for (int offset = 0; offset < input.Count; offset += _capacity)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(_capacity, input.Count-offset);
            FamilyGpuComputeUtility.Update(_rd, _batch, [(uint)input.Batch.BatchBase,(uint)(input.Batch.BatchBase>>32),
                (uint)input.Count,(uint)count,0,MaxRoots,(uint)offset,0], "EW.EBatch");
            double start = timer.Elapsed.TotalMilliseconds;
            long list = _rd.ComputeListBegin();
            try
            {
                _rd.ComputeListBindComputePipeline(list, _pipeline); _rd.ComputeListBindUniformSet(list, _uniforms, 0);
                _rd.ComputeListDispatch(list, (uint)((count+63)/64), 1, 1);
            }
            finally { _rd.ComputeListEnd(); }
            if (offset + count == input.Count)
            {
                double completionStart = timer.Elapsed.TotalMilliseconds;
                h = _headerReadback.CompleteRecorded(_header, 8, true, "EW.EventResult", static () => { });
                headerTransportMs += Math.Max(0, timer.Elapsed.TotalMilliseconds - completionStart -
                    _headerReadback.LastSubmitMs - _headerReadback.LastSyncMs);
            }
            else FamilyGpuComputeUtility.SubmitAndSync(_rd, "EW.EventResult");
            dispatches++;
            dispatchMs += timer.Elapsed.TotalMilliseconds-start;
        }
        dispatchMs -= headerTransportMs;
        token.ThrowIfCancellationRequested();
        double readStart = timer.Elapsed.TotalMilliseconds;
        h ??= _headerReadback.CompleteRecorded(_header, 8, false, "EW.EventResult.Empty", static () => { });
        if (h.Length != 8 || h[0] != 0x45524652u || h[1] != 1 || h[5] != 0x45524652u || h[3] != 0 || h[4] != input.Count || h[2] > input.Count)
            throw new InvalidDataException("EW.EHeaderFault:" + string.Join(',',h));
        int intermediate = checked((int)h[2]);
        _ordinals.CheckPopulation(input.Batch, intermediate);
        double readMs = headerTransportMs + timer.Elapsed.TotalMilliseconds-readStart;
        double eMs = timer.Elapsed.TotalMilliseconds;
        var final = _world.ExecutePrivate(input.Batch, intermediate, token, out var wm);
        canonicalMs = timer.Elapsed.TotalMilliseconds;
        // Validation mode is a distinct, unmeasured run. Compare complete first
        // and final populations, not merely the handful of Exact-accepted seeds.
        if (_referenceE is not null)
        {
            var expectedE = _referenceE.Execute(input, token, out _);
            _ordinals.VerifyPopulation(input.Batch, intermediate, expectedE);
            var expected = _referenceW!.Execute(expectedE, token, out _);
            if (!expected.EnumerateLogicalOrdinals().SequenceEqual(final.EnumerateLogicalOrdinals())) throw new InvalidDataException("EW.FullFinalPopulationParityMismatch");
        }
        _logTotals.Record("EW|E|PublicInput.PrivateOutput|identity=SelectedPlan",_capacity,input.Count,intermediate,dispatchMs,eMs,readMs,32);
        _logTotals.Record("EW|W|PrivateInput.PublicOutput|identity=SelectedPlan;capacityScope=PrivateBatch",PrivateOrdinalBuffer.Capacity,intermediate,final.Count,wm.DispatchSyncMs,wm.CanonicalMs,wm.HeaderReadbackMs,wm.HeaderReadBytes+wm.PayloadReadBytes);
        if(Bootstrap.RuntimeLog.DetailEnabled) Bootstrap.RuntimeLog.TryBackgroundDetail($"ewPrivateBatch=true;rootReuse=False;batchBase={input.Batch.BatchBase};input={input.Count};intermediate={intermediate};output={final.Count};eDispatches={dispatches};eDispatchSyncMs={dispatchMs};eHeaderMs={readMs};eCanonicalMs={eMs};wDispatches={wm.Dispatches};wGroups={wm.SubmissionGroups};wDispatchSyncMs={wm.DispatchSyncMs};wCanonicalMs={wm.CanonicalMs};intermediatePayloadBytes=0;retainedBytes={RetainedBytes};retainedWrittenBytes={intermediate*4L};canonicalMs={canonicalMs};fullPopulationParity={_referenceE is not null}");
        return final;
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (System.Environment.CurrentManagedThreadId != _owner) throw new InvalidOperationException("EW.DisposeOwnerMismatch");
        _disposed = true;
        _logTotals.WriteSummary(calibration:false);
        Release();
    }
    private void Release()
    {
        List<Exception> failures = [];
        try { PrivateOrdinalBuffer.DisposeAll(_referenceW, _referenceE, _world); }
        catch (Exception failure) { failures.Add(failure); }
        try { FamilyGpuComputeUtility.FreeAll(_rd, _owned); }
        catch (Exception failure) { failures.Add(failure); }
        try { _ordinals?.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count != 0) throw new AggregateException("EW.CleanupFailed", failures);
    }
}
