using RolltheSpire2.Bootstrap;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class AncientOptionFamily : IFamilyInvocation
{
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    private readonly ExactSearchExecutionRequest _request;
    private readonly FamilyCpuExecution _cpu;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    internal AncientOptionFamilyPlan Plan { get; }
    private AncientOptionFamilyGpuExecutor? _gpu;
    private bool _parity, _faulted, _compact;
    private readonly Sample[] _samples = [new(), new()];
    private Sample Current => _samples[_compact ? 1 : 0];
    private sealed class Sample
    {
        internal int Batches, Comparable; internal long Input, Output, LiveInput; internal double Ms, LiveMs, Peak, ValidPeak;
    }
    private AncientOptionFamily(ExactSearchExecutionRequest request)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _headerTransport = new(request.RunOptions.Optimizations);
        _request = request; Plan = new(request); _cpu = new(request, FamilyId, "A.AncientOption.Cpu.EventLocal.20260907.v1");
        RuntimeLog.TryBackgroundInfo($"ancientOptionFamilyReady=true;family={FamilyId};physicalRevision={ConditionPerformance.PhysicalImplementationRevision};gpu={Plan.GpuSupported};recovery=None");
    }
    public string FamilyId => "A.AncientOption";
    bool IFamilyInvocation.CanBindPrivateSerial => Plan.GpuSupported;
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        if (!Plan.GpuSupported) throw new InvalidOperationException("A.PrivateSerialNotAdmitted");
        var gpu = new AncientOptionFamilyGpuExecutor(rd, Plan, input, output);
        _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
        return new(gpu, gpu.Device, gpu.SetupMs, input, output, (batch, count, token) =>
        {
            FamilyCandidateSet? result = null; AncientOptionFamilyGpuMetrics m;
            if (output is not null) gpu.ExecutePrivateStage(batch, count, token, out m);
            else result = input is null ? gpu.Execute(FamilyCandidateSet.Dense(batch), token, out m) : gpu.ExecutePrivate(batch, count, token, out m);
            _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
            return new(m.Output, result, m.DispatchSyncMs, m.ReadbackMs, m.ReadbackBytes, m.CanonicalMs, m.Dispatches);
        });
    }
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest geometry)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return AncientOptionPhysicalPricing.Quote(_request, Plan, geometry);
    }
    public FamilySurvivalProjection Survival => Plan.Survival;
    public FamilyExpectedFilteringCostProjection? ExpectedFilteringCost => null;
    public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId, Plan.Capacity, [], "EventLocalExpectedWorkUnavailable");
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compact)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return !Plan.GpuSupported ? _cpu.Condition(compact) : _headerTransport.Apply(
            new(FamilyId, $"A.AncientOption.Gpu.EventLocal.{(compact ? "CompactAbi1" : "Dense")}.CanonicalAbi1Ready.20260922.v2" + SharedVisibleSeedGpuCache.RevisionSuffix,
                "A.AncientOption.Neutral.20260907.v1", 1, "NoAcceptedWithinPathCurve;CanonicalAbi1Ready", usesGpu: true),
            _gpu?.HeaderReadbackRevision);
    }
    public FamilyPerformanceObservation CapturePerformanceObservation()
    {
        if (!Plan.GpuSupported) return _cpu.Observation(); var s = Current;
        return new(ResolveConditionPerformance(_compact), _gpu?.Device ?? "", _gpu?.SetupMs ?? 0, s.Peak, s.ValidPeak, s.Batches, s.Comparable,
            s.Input, s.Output, s.Ms, _parity && !_faulted && s.Comparable > 0,
            _faulted ? "PhysicalFailureOrRecovery" : "CanonicalAbi1Ready;ColdModeExcluded;FullPrivateWindowRequired;CpuReferenceParity=" + _parity);
    }
    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot()
    {
        if (!Plan.GpuSupported) return _cpu.Live(); var s = Current;
        return _faulted || !_parity || s.Batches < 2 ? null : new(FamilyId, ResolveConditionPerformance(_compact).PhysicalImplementationRevision, s.LiveInput, s.LiveMs, s.Batches - 1);
    }
    public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow observation,
        FamilyCandidateSet input, CancellationToken token)
    {
        if (!Plan.GpuSupported) return _cpu.Execute(observation, input, token, Plan.Matches);
        token.ThrowIfCancellationRequested();
        if (_faulted) throw new InvalidOperationException("A.AncientOption.InvocationFaulted");
        if (observation.ExactRequest.SnapshotFingerprint != _request.SnapshotFingerprint) throw new InvalidOperationException("A.AncientOption.ObservationMismatch");
        if (input.Count == 0) return input;
        _compact = !input.IsDense;
        try
        {
            _gpu ??= await context.ExecuteGpuAsync(rd => new AncientOptionFamilyGpuExecutor(rd, Plan), token).ConfigureAwait(false);
            if (!_parity)
            {
                var sample = FamilyCandidateSet.FromSortedAbi1(input.Batch, input.EnumerateLogicalOrdinals().Take(2048).ToArray());
                var actual = await context.ExecuteGpuAsync(rd => _gpu.Execute(sample, token, out _), token).ConfigureAwait(false);
                var profile = RolltheSpire2.Compatibility.RuntimeProfileRegistry.Select(_request.Detection);
                var expected = sample.EnumerateLogicalOrdinals().Where(i => Plan.Reference(profile.ComputeRootSeed(
                    VisibleSeedCandidateCodec.FormatOrdinal(profile, input.Batch.GlobalCandidate(i)))));
                if (!actual.EnumerateLogicalOrdinals().SequenceEqual(expected)) throw new InvalidDataException("A.AncientOption.CanonicalReferenceParityMismatch");
                _parity = true;
            }
            var result = await context.ExecuteGpuAsync(rd => { var output = _gpu.Execute(input, token, out var m); return (output, m); }, token).ConfigureAwait(false);
            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, result.m.DispatchSyncMs, outputs: result.output.Count, canonicalMs: result.m.CanonicalMs, readbackMs: result.m.ReadbackMs, readbackBytes: result.m.ReadbackBytes);
            var metrics = result.m; var s = Current; s.Batches++; s.Input += metrics.Input; s.Output += metrics.Output; s.Ms += metrics.CanonicalMs;
            if (s.Batches > 1)
            {
                s.LiveInput += metrics.Input; s.LiveMs += metrics.CanonicalMs; double rate = metrics.Input * 1000d / metrics.CanonicalMs; s.Peak = Math.Max(s.Peak, rate);
                if (metrics.Input >= Plan.Capacity && double.IsFinite(rate) && rate > 0) { s.Comparable++; s.ValidPeak = Math.Max(s.ValidPeak, rate); }
            }
            if (RolltheSpire2.Bootstrap.RuntimeLog.DetailEnabled) RuntimeLog.TryBackgroundDetail($"ancientOptionFamilyGpuBatch=true;family={FamilyId};physicalRevision={ResolveConditionPerformance(_compact).PhysicalImplementationRevision};input={metrics.Input};output={metrics.Output};observedSurvivalDiagnostic={(double)metrics.Output / metrics.Input};privateCapacity={Plan.Capacity};dispatches={metrics.Dispatches};readbackBytes={metrics.ReadbackBytes};readbackMs={metrics.ReadbackMs};sortMs={metrics.SortMs};canonicalAbi1ReadyMs={metrics.CanonicalMs};workspaceBytes={_gpu.WorkspaceBytes};parity={_parity};faulted=False;recovery=None");
            return result.output;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _faulted = true; RuntimeLog.TryBackgroundWarning("ancientOptionFamilyFault=true;recovery=None;error=" + ex.Message); throw; }
    }
    public async ValueTask DisposeAsync(FamilyExecutionContext context)
    {
        if (!Plan.GpuSupported) { _cpu.WriteSummary(); return; }
        if (_gpu != null) try { await context.ExecuteGpuAsync(_ => { _gpu.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false); }
            catch { _faulted = true; throw; }
        var s = Current;
        RuntimeLog.TryBackgroundInfo($"ancientOptionFamilySummary=true;family={FamilyId};physicalRevision={ResolveConditionPerformance(_compact).PhysicalImplementationRevision};batches={s.Batches};input={s.Input};output={s.Output};canonicalAbi1ReadyMs={s.Ms};parity={_parity};faulted={_faulted};recovery=None");
    }
    internal static bool TryCreate(ExactSearchExecutionRequest request, out IFamilyInvocation? family)
    {
        family = null; if (!(request.Evaluation.AncientBranchConditions.Any(b => b.OptionAny.Count > 0 || b.SeaGlassTargetAny.Count > 0) || request.Evaluation.AncientOptionFilters.Any(f => !f.IsEmpty) || request.Evaluation.AncientSeaGlassTargetFilters.Any(f => !f.IsEmpty))) return false; family = new AncientOptionFamily(request); return true;
    }
}
