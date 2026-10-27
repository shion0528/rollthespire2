using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

// R owns one initial World Relic Bag for Capsule and sequence observations.
// Tracked-rank Rfull handles common shapes; multiplayer general arrivals reuse
// the opening draw donor and full local bag. Coverage and ABI1 remain unchanged.
internal sealed partial class CapsuleRelicFamily : IFamilyInvocation
{
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    bool IFamilyInvocation.CanBindPrivateSerial => _gpuPlan is not null && !_gpuDisabled;
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest geometry)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return RelicPhysicalPricing.QuoteCapsule(_request, _gpuPlan, geometry);
    }
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        if (_gpuPlan is null || _gpuDisabled) throw new InvalidOperationException("Rfull.PrivateSerialNotAdmitted");
        var gpu = RelicFamilyGpuExecutor.Create(rd, _request, _gpuPlan, input, false, output);
        return _headerTransport.Bind(gpu.BindPrivateSerial(), input, output, () => gpu.HeaderReadbackRevision);
    }
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    private const int ReferenceSampleCapacity = 256;
    private readonly ExactSearchExecutionRequest _request;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    private readonly CapsuleRelicReplay _replay;
    private readonly NeowFamilyProjections _models;
    private readonly FamilyCpuExecution _cpu;
    private readonly RelicFullGpuPlan? _gpuPlan;
    private RelicFamilyGpuExecutor? _gpu;
    private bool _gpuDisabled;
    private bool _referenceParityPassed;
    private bool _compactInput;
    private string _failure = string.Empty;
    private int _gpuBatches;
    private int _steadyBatches;
    private long _inputs;
    private long _outputs;
    private long _liveInputs;
    private double _canonicalMs;
    private double _liveMs;
    private double _peak;

    private CapsuleRelicFamily(ExactSearchExecutionRequest request, NeowReplayPlan route)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _headerTransport = new(request.RunOptions.Optimizations);
        _request = request;
        _replay = new(request, route);
        _models = new(request, route, true, _replay.Pool);
        _cpu = new(request, FamilyId, "R.Relic.Cpu.CapsuleAndSequenceReplay.20260905.v1");
        RelicFullGpuPlan.TryCreate(request, route, out _gpuPlan, out string issue);
        RuntimeLog.TryBackgroundInfo(
            $"rCapsuleFamilyReady=true;physical={(_gpuPlan is null ? "CpuCapsuleAndSequenceReplay" : "GpuRfull")};" +
            $"physicalIssue={issue};exactOnly={string.Join(',', route.ExactOnly)}");
    }

    internal bool MatchesReference(ulong root) => _replay.Matches(root);

    public string FamilyId => "R.Relic";
    public FamilyAnalyticalCostProjection AnalyticalCost => _models.Full;
    public FamilySurvivalProjection Survival => ResolveSurvival(Empty);
    public bool HasConditionalProjections => true;
    public FamilySurvivalProjection ResolveSurvival(IReadOnlySet<string> passedCoverage) => _models.Survival(passedCoverage);
    public FamilyExpectedFilteringCostProjection ExpectedFilteringCost => ResolveExpectedFilteringCost(Empty);
    public FamilyExpectedFilteringCostProjection ResolveExpectedFilteringCost(IReadOnlySet<string> passedCoverage) => _models.Expected(passedCoverage);
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compactAbi1Input) => _gpuPlan is null
        ? _cpu.Condition(compactAbi1Input)
        : _headerTransport.Apply(new FamilyConditionPerformanceProjection(
            FamilyId,
            _gpuPlan.Revision(compactAbi1Input),
            "Rfull.ConditionPerformance.Neutral.CanonicalBoundary.20260906.v4",
            1d,
            "NoAcceptedWithinPathCurve;timingBoundary=CanonicalAbi1Ready;independentRfullIdentity=true"),
            _gpu?.HeaderReadbackRevision);

    public FamilyPerformanceObservation CapturePerformanceObservation() => _gpuPlan is null
        ? _cpu.Observation()
        : new FamilyPerformanceObservation(
            ResolveConditionPerformance(_compactInput), _gpu?.DeviceName ?? "not-created", _gpu?.SetupMs ?? 0d,
            _peak, _peak, _gpuBatches, _steadyBatches, _inputs, _outputs, _canonicalMs,
            !_gpuDisabled && _referenceParityPassed && _steadyBatches > 0,
            _gpuDisabled ? "PhysicalFailureOrRecovery" :
            "RfullCanonicalAbi1Ready;FirstBatchExcluded;CpuCapsuleAndSequenceReplayParity=" + _referenceParityPassed);

    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot() =>
        _gpuPlan is null || _gpuDisabled || !_referenceParityPassed || _gpuBatches < 2 || _liveInputs <= 0 || _liveMs <= 0d
            ? null
            : new FamilyLivePerformanceSnapshot(
                FamilyId, ResolveConditionPerformance(_compactInput).PhysicalImplementationRevision,
                Interlocked.Read(ref _liveInputs), Volatile.Read(ref _liveMs), _gpuBatches - 1);

    public async ValueTask<FamilyCandidateSet> InvokeAsync(
        FamilyExecutionContext executionContext,
        FamilyObservationWindow observationWindow,
        FamilyCandidateSet input,
        CancellationToken cancellationToken)
    {
        if (observationWindow.ExactRequest.SnapshotFingerprint != _request.SnapshotFingerprint)
            throw new InvalidOperationException("RfullObservationContextMismatch");
        cancellationToken.ThrowIfCancellationRequested();
        if (_gpuPlan is null)
            return _cpu.Execute(observationWindow, input, cancellationToken, _replay.Matches);
        if (_gpuDisabled) throw new InvalidOperationException("Rfull.SelectedInvocationFaulted");
        if (input.Count == 0) return input;

        _compactInput = !input.IsDense;
        try
        {
            if (_gpu is null)
                _gpu = await executionContext.ExecuteGpuAsync(
                    rd => RelicFamilyGpuExecutor.Create(rd, _request, _gpuPlan), cancellationToken).ConfigureAwait(false);
            (FamilyCandidateSet survivors, RelicFamilyGpuBatchMetrics metrics) =
                await executionContext.ExecuteGpuAsync(
                    _ =>
                    {
                        FamilyCandidateSet result = _gpu.Execute(input, cancellationToken, out RelicFamilyGpuBatchMetrics batch);
                        return (result, batch);
                    }, cancellationToken).ConfigureAwait(false);

            if (!_referenceParityPassed) ValidateReferenceParity(input, survivors);

            _gpuBatches++;
            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, metrics.CommandMs + metrics.SubmitMs + metrics.SyncMs, outputs: metrics.Survivors, canonicalMs: metrics.CanonicalAbi1ReadyMs, readbackMs: metrics.ReadbackMs, readbackBytes: metrics.ReadbackBytes);
            _inputs += metrics.InputCandidates;
            _outputs += metrics.Survivors;
            _canonicalMs += metrics.CanonicalAbi1ReadyMs;
            double rate = metrics.CanonicalAbi1ReadyMs <= 0d
                ? 0d : metrics.InputCandidates * 1000d / metrics.CanonicalAbi1ReadyMs;
            if (_gpuBatches > 1)
            {
                Interlocked.Add(ref _liveInputs, metrics.InputCandidates);
                _liveMs += metrics.CanonicalAbi1ReadyMs;
                int steadyMinimum = _compactInput ? RelicFamilyGpuExecutor.CompactInputCapacity / 4 : RelicFamilyGpuExecutor.Capacity;
                if (metrics.InputCandidates >= steadyMinimum)
                {
                    _steadyBatches++;
                    _peak = Math.Max(_peak, rate);
                }
            }
            if (_gpuBatches <= 3 || _gpuBatches % 256 == 0)
                RuntimeLog.TryBackgroundInfo(
                    $"rfullFamilyBatch=true;physicalRevision={ResolveConditionPerformance(_compactInput).PhysicalImplementationRevision};" +
                    $"arrival={ArrivalName(_gpuPlan.CapsuleMetadata[0])};" +
                    $"batchBase={input.Batch.BatchBase};input={metrics.InputCandidates};processed={metrics.ProcessedCandidates};" +
                    $"survivors={metrics.Survivors};groups={metrics.DispatchGroups};readbackBytes={metrics.ReadbackBytes};" +
                    $"canonicalAbi1ReadyMs={metrics.CanonicalAbi1ReadyMs:F4};parity={_referenceParityPassed};" +
                    "bagSemantics=InitialWorldBag;capsuleProjection=RarityTrackedRank;legacyFallback=false");
            return survivors;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _gpuDisabled = true;
            _failure = ex.GetType().Name + ":" + ex.Message;
            try
            {
                if (_gpu is not null)
                    await executionContext.ExecuteGpuAsync(_ => { _gpu.Dispose(); return true; }, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch { }
            _gpu = null;
            RuntimeLog.TryBackgroundWarning(
                $"rfullFamilyPhysicalFault=true;failure={Sanitize(_failure)};recovery=None;" +
                "selectedExecutionFault=true;legacyFallback=false");
            throw;
        }
    }

    private void ValidateReferenceParity(FamilyCandidateSet input, FamilyCandidateSet gpuOutput)
    {
        IRuntimeProfile profile = RuntimeProfileRegistry.Select(_request.Detection);
        ReadOnlySpan<ulong> actual = gpuOutput.ExportAbi1().Span;
        foreach (ulong ordinal in input.EnumerateLogicalOrdinals().Take(ReferenceSampleCapacity))
        {
            ulong global = input.Batch.GlobalCandidate(ordinal);
            ulong root = profile.ComputeRootSeed(VisibleSeedCandidateCodec.FormatOrdinal(profile, global));
            bool cpu = _replay.Matches(root);
            bool gpu = actual.BinarySearch(ordinal) >= 0;
            if (cpu != gpu)
                throw new InvalidDataException(
                    $"RfullCpuReferenceParityMismatch:logicalOrdinal={ordinal};cpu={cpu};gpu={gpu}");
        }
        _referenceParityPassed = true;
        RuntimeLog.TryBackgroundInfo(
            $"rfullFamilyReferenceParity=true;sampleCandidates={Math.Min(input.Count, ReferenceSampleCapacity)};" +
            "reference=R.Relic.Cpu.CapsuleAndSequenceReplay.20260905.v1");
    }

    private static string ArrivalName(uint arrival) => arrival switch
    {
        RelicFullGpuPlan.DirectArrival => "Direct",
        RelicFullGpuPlan.FixedBonesArrival => "FixedBones",
        RelicFullGpuPlan.GroupedBonesArrival => "GroupedBones",
        RelicFullGpuPlan.SourceConstrainedBonesArrival => "SourceConstrainedSmallLarge",
        _ => "Unknown"
    };

    public async ValueTask DisposeAsync(FamilyExecutionContext executionContext)
    {
        if (_gpuPlan is null)
        {
            _cpu.WriteSummary();
            return;
        }
        if (_gpu is not null)
        {
            try
            {
                await executionContext.ExecuteGpuAsync(_ => { _gpu.Dispose(); return true; }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { _gpuDisabled = true; throw; }
            _gpu = null;
        }
        RuntimeLog.TryBackgroundInfo(
            $"rfullFamilySummary=true;physicalDense={_gpuPlan?.Revision(false)};physicalCompact={_gpuPlan?.Revision(true)};" +
            $"batches={_gpuBatches};input={_inputs};survivors={_outputs};canonicalAbi1ReadyMs={_canonicalMs:F3};" +
            $"parity={_referenceParityPassed};recovery=False;faulted={_gpuDisabled};failure={Sanitize(_failure)};" +
            "unsupportedShapeFallback=CpuCapsuleAndSequenceReplay;legacyFallback=false");
    }

    internal static bool TryCreate(ExactSearchExecutionRequest request, out IFamilyInvocation? family)
    {
        family = null;
        if (!NeowReplayPlan.HasCapsule(request)) return false;
        try
        {
            var route = NeowReplayPlan.Compile(request, true);
            family = new CapsuleRelicFamily(request, route);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.TryBackgroundWarning("rCapsuleFamilyUnavailable=true;recovery=None;legacyFallback=false;reason=" + ex.Message);
            return false;
        }
    }

    private static string Sanitize(string value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
