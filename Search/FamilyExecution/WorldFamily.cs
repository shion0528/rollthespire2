using RolltheSpire2.Bootstrap;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.FamilyExecution;

// Independent World observation. Encounter draws are prerequisites; Ancient
// options and Event results remain outside W. Selected physical faults propagate.
internal sealed partial class WorldFamily : IFamilyInvocation
{
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    private readonly FamilyCpuExecution _cpu;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    private readonly ExactSearchExecutionRequest _request;
    private readonly WorldFamilyGpuPlan? _gpuPlan;

    private WorldFamilyGpuExecutor? _gpu;
    private bool _compact, _parity, _faulted;
    private int _batches;
    private long _inputs, _outputs;
    private double _canonicalMs;
    private long _windows, _groups, _headerCalls, _headerBytes, _payloadCalls, _payloadBytes;
    private double _dispatchSyncMs, _headerMs;
    // Separate physical evidence; the interface publishes the latest input mode only.
    private readonly GpuObservation[] _observations = [new(), new()];
    private GpuObservation Observation => _observations[_compact ? 1 : 0];
    private sealed class GpuObservation
    {
        internal int Batches, Comparable;
        internal long Inputs, Outputs, LiveInputs;
        internal double CanonicalMs, LiveMs, Peak, ValidPeak;
        internal void Record(WorldFamilyGpuMetrics m, int fullGroupInput)
        {
            Batches++; Inputs += m.Input; Outputs += m.Output; CanonicalMs += m.CanonicalMs;
            if (Batches == 1) return; // cold first invocation of this physical mode
            Interlocked.Add(ref LiveInputs, m.Input); LiveMs += m.CanonicalMs;
            double rate = m.Input * 1000d / m.CanonicalMs;
            Peak = Math.Max(Peak, rate);
            // A complete bounded submission group is a minimum geometry check,
            // not a claim that arbitrary Compact populations saturate the device.
            if (m.Input >= fullGroupInput && double.IsFinite(rate) && rate > 0)
            { Comparable++; ValidPeak = Math.Max(ValidPeak, rate); }
        }
    }
    internal WorldFamilyReplay Replay { get; }
    private readonly FamilySurvivalProjection _survival;

    private WorldFamily(ExactSearchExecutionRequest request, WorldFamilyReplay replay)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _headerTransport = new(request.RunOptions.Optimizations);
        _request = request;
        Replay = replay;
        _survival = WorldPhysicalPricing.ResolveSurvival(request);
        _gpuPlan = WorldFamilyGpuPlan.Supports(Replay) ? new WorldFamilyGpuPlan(Replay,
            request.Authority.PlayersCount == 1 && request.Authority.WorldAuthority?.Beta109Generation is { IsMultiplayer: false }) : null;
        _cpu = new(request, FamilyId, "W.World.Cpu.Progression.20260907.v1");
        RuntimeLog.TryBackgroundInfo($"worldFamilyReady=true;family={FamilyId};physicalRevision={ConditionPerformance.PhysicalImplementationRevision};" +
            $"actHorizon={Replay.Plan.MaxRequiredAct};finalHorizon={Replay.Plan.RequiredFarthestStage};" +
            $"groupingK={_gpuPlan?.GroupingK ?? 1};groupingWorkspaceBytes={_gpuPlan?.GroupingWorkspaceBytes ?? 0};capacity={_gpuPlan?.Capacity ?? FamilyCpuExecution.Capacity};workspaceBytes={_gpuPlan?.WorkspaceBytes ?? 0};scratchBytes={_gpuPlan?.ScratchBytes ?? 0};recovery=None");
    }

    public string FamilyId => "W.World";
    bool IFamilyInvocation.CanBindPrivateSerial => _gpuPlan is not null;
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        if (_gpuPlan is null) throw new InvalidOperationException("W.PrivateSerialNotAdmitted");
        var gpu = new WorldFamilyGpuExecutor(rd, _gpuPlan, input?.Buffer, output);
        _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
        return new(gpu, gpu.Device, gpu.SetupMs, input, output, (batch, count, token) =>
        {
            FamilyCandidateSet? result = null; WorldFamilyGpuMetrics m;
            if (output is not null) gpu.ExecutePrivateStage(batch, count, token, out m);
            else result = input is null ? gpu.Execute(FamilyCandidateSet.Dense(batch), token, out m) : gpu.ExecutePrivate(batch, count, token, out m);
            // W currently reports header timing separately, not full payload time.
            _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
            return new(m.Output, result, m.DispatchSyncMs, m.HeaderReadbackMs,
                m.HeaderReadBytes + m.PayloadReadBytes, m.CanonicalMs, m.Dispatches);
        });
    }
    public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId,_gpuPlan?.Capacity??FamilyCpuExecution.Capacity,[],"W.ProgressionCostUnresolved");
    // No accepted World joint cost model: null is unavailable, never a zero-cost quote.
    public FamilyExpectedFilteringCostProjection? ExpectedFilteringCost => null;
    public FamilySurvivalProjection Survival => _survival;
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest geometry)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return _request.ProfileId == Compatibility.RuntimeProfileId.Beta111 && _request.Authority.CanUseCurrentModel &&
            _request.Authority.PlayersCount == 1 && _request.Authority.AllCharacterCardPoolsUnlocked == true
                ? WorldPhysicalPricing.Quote(Replay, _gpuPlan, geometry) : null;
    }
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compactAbi1Input)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return _gpuPlan is null ? _cpu.Condition(compactAbi1Input) : _headerTransport.Apply(
            new(FamilyId,_gpuPlan.Revision(compactAbi1Input), "W.Neutral.20260907.v1",1,
                "NoAcceptedWithinPathCurve;CanonicalAbi1Ready",usesGpu:true),_gpu?.HeaderReadbackRevision);
    }
    public FamilyPerformanceObservation CapturePerformanceObservation()
    {
        if (_gpuPlan is null) return _cpu.Observation();
        var o = Observation;
        bool eligible = _parity && !_faulted && o.Comparable > 0 && o.ValidPeak > 0;
        return new(ResolveConditionPerformance(_compact), _gpu?.Device ?? "", _gpu?.SetupMs ?? 0,
            o.Peak, o.ValidPeak, o.Batches, o.Comparable, o.Inputs, o.Outputs, o.CanonicalMs, eligible,
            _faulted ? "PhysicalFailureOrRecovery" :
            $"CanonicalAbi1Ready;FirstModeBatchExcluded;CpuReferenceParity={_parity};minimumDurableInput={_gpuPlan.Capacity * _gpuPlan.GroupingK};underfilledEvidenceExcluded=True");
    }
    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot()
    {
        if (_gpuPlan is null) return _cpu.Live();
        var o = Observation;
        return _faulted || !_parity || o.Batches < 2 ? null :
            new(FamilyId, ResolveConditionPerformance(_compact).PhysicalImplementationRevision, Interlocked.Read(ref o.LiveInputs), Volatile.Read(ref o.LiveMs), o.Batches - 1);
    }
    public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext executionContext,
        FamilyObservationWindow observationWindow, FamilyCandidateSet input, CancellationToken cancellationToken)
    {
        if(_gpuPlan is null)return _cpu.Execute(observationWindow,input,cancellationToken,Replay.Matches);
        cancellationToken.ThrowIfCancellationRequested();
        if(_faulted)throw new InvalidOperationException("W.GpuInvocationFaulted");
        if(observationWindow.ExactRequest.SnapshotFingerprint!=_request.SnapshotFingerprint)throw new InvalidOperationException("W.ObservationMismatch");
        if(input.Count==0)return input;
        _compact=!input.IsDense;
        long cancellationRequestedAt = 0;
        using var cancellationRegistration = cancellationToken.Register(() =>
            Interlocked.CompareExchange(ref cancellationRequestedAt, System.Diagnostics.Stopwatch.GetTimestamp(), 0));
        try
        {
            _gpu??=await executionContext.ExecuteGpuAsync(rd=>new WorldFamilyGpuExecutor(rd,_gpuPlan),cancellationToken).ConfigureAwait(false);
            if(!_parity)
            {
                // Separate tiny GPU dispatch, compared in full before admitting the
                // large batch. Parity cost is not physical performance evidence.
                var sample=FamilyCandidateSet.FromSortedAbi1(input.Batch,input.EnumerateLogicalOrdinals().Take(4096).ToArray());
                var actual=await executionContext.ExecuteGpuAsync(rd=>_gpu.Execute(sample,cancellationToken,out _),cancellationToken).ConfigureAwait(false);
                var profile=RolltheSpire2.Compatibility.RuntimeProfileRegistry.Select(_request.Detection);
                var expected=sample.EnumerateLogicalOrdinals().Where(i=>Replay.Matches(profile.ComputeRootSeed(
                    VisibleSeedCandidateCodec.FormatOrdinal(profile,input.Batch.GlobalCandidate(i)))));
                if(!actual.EnumerateLogicalOrdinals().SequenceEqual(expected))throw new InvalidDataException("W.GpuCpuParityMismatch");
                _parity=true;
            }
            var result=await executionContext.ExecuteGpuAsync(_=>{
                var output=_gpu.Execute(input,cancellationToken,out var metrics);return (output,metrics);
            },cancellationToken).ConfigureAwait(false);
            var m=result.metrics;
            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, m.DispatchSyncMs, outputs: result.output.Count, canonicalMs: m.CanonicalMs, readbackMs: m.HeaderReadbackMs, readbackBytes: m.HeaderReadBytes + m.PayloadReadBytes);
            _windows+=m.Dispatches;_groups+=m.SubmissionGroups;_headerCalls+=m.HeaderReadCalls;_headerBytes+=m.HeaderReadBytes;
            _payloadCalls+=m.PayloadReadCalls;_payloadBytes+=m.PayloadReadBytes;
            _dispatchSyncMs+=m.DispatchSyncMs;_headerMs+=m.HeaderReadbackMs;
            _batches++;_inputs+=m.Input;_outputs+=m.Output;_canonicalMs+=m.CanonicalMs;
            Observation.Record(m, checked(_gpuPlan.Capacity * _gpuPlan.GroupingK));
            if (RolltheSpire2.Bootstrap.RuntimeLog.DetailEnabled)RuntimeLog.TryBackgroundDetail($"worldFamilyGpuBatch=true;family={FamilyId};physicalRevision={_gpuPlan.Revision(_compact)};" +
                $"groupingK={_gpuPlan.GroupingK};input={m.Input};output={m.Output};privateCapacity={_gpuPlan.Capacity};privateWindows={m.Dispatches};submissionGroups={m.SubmissionGroups};dispatches={m.Dispatches};submits={m.SubmissionGroups};syncs={m.SubmissionGroups};" +
                $"headerReadCalls={m.HeaderReadCalls};headerReadBytes={m.HeaderReadBytes};payloadReadCalls={m.PayloadReadCalls};payloadReadBytes={m.PayloadReadBytes};dispatchSyncMs={m.DispatchSyncMs};headerReadbackMs={m.HeaderReadbackMs};canonicalAbi1ReadyMs={m.CanonicalMs};" +
                $"workspaceBytes={_gpuPlan.WorkspaceBytes};groupingWorkspaceBytes={_gpuPlan.GroupingWorkspaceBytes};scratchBytes={_gpuPlan.ScratchBytes};parity={_parity};faulted=False;recovery=None");
            return result.output;
        }
        catch(OperationCanceledException)
        {
            long requested = Volatile.Read(ref cancellationRequestedAt);
            string latency = requested == 0 ? "Unknown" : System.Diagnostics.Stopwatch.GetElapsedTime(requested).TotalMilliseconds.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            RuntimeLog.TryBackgroundInfo($"worldFamilyGpuCancelled=true;physicalRevision={_gpuPlan.Revision(_compact)};groupingK={_gpuPlan.GroupingK};cancelLatencyMs={latency};resultPublished=False;faulted=False;recovery=None");
            throw;
        }
        catch(Exception ex){_faulted=true;RuntimeLog.TryBackgroundWarning("worldFamilyGpuFault=true;recovery=None;error="+ex.Message);throw;}
    }
    public async ValueTask DisposeAsync(FamilyExecutionContext executionContext)
    {
        if(_gpuPlan is null){_cpu.WriteSummary();return;}
        RuntimeLog.TryBackgroundInfo($"worldFamilyGpuSummary=true;family={FamilyId};physicalRevision={_gpuPlan.Revision(_compact)};groupingK={_gpuPlan.GroupingK};batches={_batches};input={_inputs};output={_outputs};canonicalAbi1ReadyMs={_canonicalMs};privateCapacity={_gpuPlan.Capacity};privateWindows={_windows};submissionGroups={_groups};dispatches={_windows};submits={_groups};syncs={_groups};" +
            $"headerReadCalls={_headerCalls};headerReadBytes={_headerBytes};payloadReadCalls={_payloadCalls};payloadReadBytes={_payloadBytes};dispatchSyncMs={_dispatchSyncMs};headerReadbackMs={_headerMs};workspaceBytes={_gpuPlan.WorkspaceBytes};groupingWorkspaceBytes={_gpuPlan.GroupingWorkspaceBytes};parity={_parity};faulted={_faulted};recovery=None");
        if(_gpu is not null)
        {
            try { await executionContext.ExecuteGpuAsync(_=>{_gpu.Dispose();return true;},CancellationToken.None).ConfigureAwait(false); }
            catch { _faulted = true; throw; }
        }
    }

    internal static bool TryCreate(ExactSearchExecutionRequest request, out IFamilyInvocation? family)
    {
        family = null;
        if (!WorldFamilyReplay.HasConditions(request)) return false;
        var plan = WorldFamilyReplay.Compile(request);
        if (!plan.Enabled && plan.DisableReason == "WorldNumericPlanCapacityExceeded")
        {
            WorldFamilyReplay.ValidateRelicAuthority(request);
            RuntimeLog.TryBackgroundInfo("worldFamilyInapplicable=true;reason=WorldNumericPlanCapacityExceeded;selectionTime=true;predicatesRetainedByExact=true;recovery=None");
            return false;
        }
        family = new WorldFamily(request, new WorldFamilyReplay(request, plan));
        return true;
    }
}
