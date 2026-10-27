using System.Diagnostics;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Relics;
using RolltheSpire2.Core.World;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Family R: one independent replay of the initial up_front Relic progression,
/// evaluating ordinary lane and Shop Relic predicates in the same traversal.
/// </summary>
internal sealed partial class RelicFamily : IFamilyInvocation
{
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    bool IFamilyInvocation.CanBindPrivateSerial => !_gpuDisabled;
    FamilyPhysicalQuote? IFamilyInvocation.QuoteCommonPrivateWorkCancellation(FamilyPhysicalQuoteRequest g) =>
        _gpuDisabled || !FamilyPhysicalQuote.AdmittedRequest(g) || !FamilyPhysicalQuote.HasReferenceBackend() ? null :
        new(FamilyId, "R.SameCompiledTrackedShopBody", 0, RelicFamilyGpuExecutor.Capacity,
            390,
            "ComparisonOnly;UnknownNumericalCancelled;SamePlanAndAppend32;SameBodyReferenceSetup;PublicReachAppliedByAllocation");
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        if (_gpuDisabled) throw new InvalidOperationException("R.PrivateSerialFaulted");
        var gpu = RelicFamilyGpuExecutor.Create(rd, _request, _plan, input, output);
        return _headerTransport.Bind(gpu.BindPrivateSerial(), input, output, () => gpu.HeaderReadbackRevision);
    }
    private const int ReferenceSampleCapacity = 256;
    private const int PerformanceSamplePeriod = 256;
    private const int InitialPerformanceSampleCount = 3;
    private readonly ExactSearchExecutionRequest _request;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    private readonly IRuntimeProfile _profile;
    private readonly RelicFamilyPlan _plan;
    private readonly FamilyAnalyticalCostProjection _analyticalCost;
    private readonly FamilySurvivalProjection _survival;
    private readonly FamilyExpectedFilteringCostProjection _expectedFilteringCost;
    private readonly FamilyConditionPerformanceProjection _denseConditionPerformance;
    private readonly FamilyConditionPerformanceProjection _compactConditionPerformance;
    private RelicFamilyGpuExecutor? _gpu;
    private bool _gpuDisabled;
    private bool _referenceMeasured;
    private bool _referenceParityPassed;
    private bool _disposed;
    private string _failure = string.Empty;
    private string _device = "not-created";
    private int _ownerThreadId;
    private int _invocations;
    private int _denseInputs;
    private int _compactInputs;
    private int _emptyInputs;
    private int _gpuBatches;
    private int _performanceSamples;
    private long _inputCandidates;
    private long _processedCandidates;
    private long _survivors;
    private long _readbackBytes;
    private long _workspaceBytes;
    private long _dispatchGroups;
    private double _setupMs;
    private double _physicalMs;
    private double _wallMs;
    private double _canonicalAbi1ReadyMs;
    private double _currentSearchPeakCandidatesPerSecond;
    private double _validSearchPeakCandidatesPerSecond;
    private int _comparableSteadyBatches;
    private int _liveSteadyBatches;
    private long _liveSteadyInputCandidates;
    private double _liveSteadyCanonicalMs;
    private bool? _observedCompactAbi1Input;

    private RelicFamily(ExactSearchExecutionRequest request, IRuntimeProfile profile, RelicFamilyPlan plan)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _headerTransport = new(request.RunOptions.Optimizations);
        _request = request;
        _profile = profile;
        _plan = plan;
        _analyticalCost = RelicFamilyAnalyticalCost.Project(plan);
        _survival = RelicFamilySurvival.Project(request);
        _expectedFilteringCost = RelicExpectedFilteringCost.Project(request, plan, _analyticalCost, _survival);
        string prefixRevision = SharedVisibleSeedGpuCache.RevisionSuffix;
        _denseConditionPerformance = RelicConditionPerformance.Project(
            plan, FamilyPhysicalImplementationRevisions.RelicDensePerformance + prefixRevision);
        _compactConditionPerformance = RelicConditionPerformance.Project(
            plan, FamilyPhysicalImplementationRevisions.RelicCompactPerformance + prefixRevision);
    }

    public string FamilyId => "R.Relic";
    internal string PricingSignature => $"last={_plan.LastRequiredBucket};pool=" + string.Join(',', _plan.Pool.BucketLengths) +
        ";tracked=" + string.Join(',', _plan.TrackedCountsByLane) + ";depth=" + string.Join(',', _plan.PositiveDepthByLane);
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest request)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return RelicPhysicalPricing.Quote(_request, _plan, request, _expectedFilteringCost);
    }
    public FamilyAnalyticalCostProjection AnalyticalCost => _analyticalCost;
    public FamilySurvivalProjection Survival => _survival;
    public FamilyExpectedFilteringCostProjection ExpectedFilteringCost => _expectedFilteringCost;
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compactAbi1Input) =>
        _headerTransport.Apply(compactAbi1Input ? _compactConditionPerformance : _denseConditionPerformance,
            _gpu?.HeaderReadbackRevision);
    internal int PreferredExecutionWindowSize => RelicFamilyGpuExecutor.Capacity;

    public static bool TryCreate(ExactSearchExecutionRequest request, out IFamilyInvocation? family)
    {
        ArgumentNullException.ThrowIfNull(request);
        family = null;
        if (!request.Evaluation.RequiresRelicSequenceDomain) return false;
        IRuntimeProfile profile = RuntimeProfileRegistry.Select(request.Detection);
        if (!RelicFamilyPlanCompiler.TryCompile(request, out RelicFamilyPlan? plan, out string issue) || plan is null)
        {
            RuntimeLog.TryBackgroundWarning(
                $"rFamilyUnavailable=true;issue={Sanitize(issue)};recovery=ExactOnly;legacyFallback=false");
            return false;
        }
        family = new RelicFamily(request, profile, plan);
        return true;
    }

    public async ValueTask<FamilyCandidateSet> InvokeAsync(
        FamilyExecutionContext executionContext,
        FamilyObservationWindow observationWindow,
        FamilyCandidateSet input,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(observationWindow.ExactRequest.SnapshotFingerprint,
                _request.SnapshotFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("RFamilyObservationContextMismatch");

        cancellationToken.ThrowIfCancellationRequested();
        ObserveInputMode(!input.IsDense);
        _invocations++;
        if (input.IsDense) _denseInputs++; else _compactInputs++;
        if (input.Count == 0)
        {
            _emptyInputs++;
            executionContext.CompletePipelineBoundaryTarget(
                input.Batch, default, Stopwatch.GetTimestamp(), targetDispatched: false);
            return FamilyCandidateSet.FromSortedAbi1(input.Batch, []);
        }
        if (_gpuDisabled) throw new InvalidOperationException("R.SelectedInvocationFaulted");

        try
        {
            await EnsureGpuAsync(executionContext, cancellationToken).ConfigureAwait(false);
            var wall = Stopwatch.StartNew();
            (FamilyCandidateSet survivors, RelicFamilyGpuBatchMetrics metrics, long ownerStartTimestamp) =
                await executionContext.ExecuteGpuAsync(
                    _ =>
                    {
                        long targetStartTimestamp = Stopwatch.GetTimestamp();
                        FamilyCandidateSet result = _gpu!.Execute(input, cancellationToken, out RelicFamilyGpuBatchMetrics batch);
                        return (result, batch, targetStartTimestamp);
                    },
                    cancellationToken).ConfigureAwait(false);
            wall.Stop();

            if (!_referenceMeasured && !MeasureReferenceAndParity(input, survivors, out string parityIssue))
            {
                throw new InvalidDataException("ReferenceParityMismatch:" + parityIssue);
            }

            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, metrics.CommandMs + metrics.SubmitMs + metrics.SyncMs, outputs: metrics.Survivors, canonicalMs: metrics.CanonicalAbi1ReadyMs, readbackMs: metrics.ReadbackMs, readbackBytes: metrics.ReadbackBytes);
            int batchNumber = ++_gpuBatches;
            _inputCandidates += metrics.InputCandidates;
            _processedCandidates += metrics.ProcessedCandidates;
            _survivors += metrics.Survivors;
            _physicalMs += metrics.PhysicalMs;
            _wallMs += wall.Elapsed.TotalMilliseconds;
            _canonicalAbi1ReadyMs += metrics.CanonicalAbi1ReadyMs;
            _readbackBytes += metrics.ReadbackBytes;
            _dispatchGroups += metrics.DispatchGroups;
            double canonicalCandidatesPerSecond =
                CandidatesPerSecond(metrics.InputCandidates, metrics.CanonicalAbi1ReadyMs);
            UpdateMaximum(ref _currentSearchPeakCandidatesPerSecond, canonicalCandidatesPerSecond);
            bool liveSteadyInput = FamilyPerformanceEvidenceEligibility.IsLiveSteadyInput(
                input.IsDense, metrics.InputCandidates, RelicFamilyGpuExecutor.Capacity);
            bool durableComparableInput = FamilyPerformanceEvidenceEligibility.IsDurableComparableInput(
                input.IsDense, metrics.InputCandidates,
                RelicFamilyGpuExecutor.Capacity,
                RelicFamilyGpuExecutor.CompactInputCapacity);
            if (batchNumber > 1 && liveSteadyInput)
            {
                _liveSteadyBatches++;
                _liveSteadyInputCandidates += metrics.InputCandidates;
                _liveSteadyCanonicalMs += metrics.CanonicalAbi1ReadyMs;
            }
            if (batchNumber > 1 && durableComparableInput)
            {
                _comparableSteadyBatches++;
                UpdateMaximum(ref _validSearchPeakCandidatesPerSecond, canonicalCandidatesPerSecond);
            }
            if (batchNumber <= InitialPerformanceSampleCount || batchNumber % PerformanceSamplePeriod == 0)
            {
                _performanceSamples++;
                RuntimeLog.TryBackgroundDetail(
                    $"rFamilyPerformanceBatch=true;family={FamilyId};batchNumber={batchNumber};" +
                    $"inputCandidates={metrics.InputCandidates};processedCandidates={metrics.ProcessedCandidates};" +
                    $"survivors={metrics.Survivors};" +
                    $"liveEtaEligible={(batchNumber > 1 && liveSteadyInput).ToString().ToLowerInvariant()};" +
                    $"durablePerformanceEligible={(batchNumber > 1 && durableComparableInput).ToString().ToLowerInvariant()};" +
                    $"minimumDurableCompactInput={FamilyPerformanceEvidenceEligibility.MinimumDurableCompactInput(RelicFamilyGpuExecutor.CompactInputCapacity)};" +
                    $"physicalMs={F(metrics.PhysicalMs)};wallMs={F(wall.Elapsed.TotalMilliseconds)};" +
                    $"canonicalAbi1ReadyMs={F(metrics.CanonicalAbi1ReadyMs)};" +
                    $"physicalCandidatesPerSecond={F(metrics.CandidatesPerSecond)};" +
                    $"canonicalAbi1ReadyCandidatesPerSecond={F(canonicalCandidatesPerSecond)};" +
                    $"wallCandidatesPerSecond={F(CandidatesPerSecond(metrics.InputCandidates, wall.Elapsed.TotalMilliseconds))};" +
                    $"dispatchGroups={metrics.DispatchGroups};readbackBytes={metrics.ReadbackBytes};sampled=true");
            }
            executionContext.CompletePipelineBoundaryTarget(
                input.Batch, metrics, ownerStartTimestamp, targetDispatched: true);
            return survivors;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await DisableGpuAsync(executionContext, ex.GetType().Name + ":" + ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    public FamilyPerformanceObservation CapturePerformanceObservation()
    {
        bool compact = _observedCompactAbi1Input == true;
        FamilyConditionPerformanceProjection condition = ResolveConditionPerformance(compact);
        bool eligible = !_gpuDisabled && _referenceParityPassed &&
                        string.IsNullOrWhiteSpace(_failure) &&
                        _comparableSteadyBatches > 0 &&
                        _validSearchPeakCandidatesPerSecond > 0d &&
                        double.IsFinite(_validSearchPeakCandidatesPerSecond);
        string evidence = eligible
            ? $"Clean{(compact ? "CompactAbi1" : "FullDense")}CanonicalSteadyBatches;" +
              "timingBoundary=CanonicalAbi1Ready;setupExcluded=true;coldFirstBatchExcluded=true;recovery=false;parity=Pass"
            : _gpuDisabled ? "PhysicalFailureOrRecovery"
            : !_referenceParityPassed ? "ReferenceParityUnavailable"
            : compact && _liveSteadyBatches > 0
                ? $"CompactUnderfillNotDurableComparable:minimumInput={FamilyPerformanceEvidenceEligibility.MinimumDurableCompactInput(RelicFamilyGpuExecutor.CompactInputCapacity)}"
            : compact ? "NoCompactAbi1SteadyBatch"
            : "NoFullDenseSteadyBatch";
        return new FamilyPerformanceObservation(
            condition,
            _device,
            _setupMs,
            _currentSearchPeakCandidatesPerSecond,
            _validSearchPeakCandidatesPerSecond,
            _gpuBatches,
            _comparableSteadyBatches,
            _inputCandidates,
            _survivors,
            _canonicalAbi1ReadyMs,
            eligible,
            evidence);
    }

    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot()
    {
        long inputs = Interlocked.Read(ref _liveSteadyInputCandidates);
        double milliseconds = Volatile.Read(ref _liveSteadyCanonicalMs);
        int batches = Volatile.Read(ref _liveSteadyBatches);
        if (_gpuDisabled || !_referenceParityPassed || !string.IsNullOrWhiteSpace(_failure) ||
            inputs <= 0 || !(milliseconds > 0d) || batches <= 0)
            return null;
        bool compact = _observedCompactAbi1Input == true;
        return new FamilyLivePerformanceSnapshot(
            FamilyId,
            ResolveConditionPerformance(compact).PhysicalImplementationRevision,
            inputs,
            milliseconds,
            batches);
    }

    public async ValueTask DisposeAsync(FamilyExecutionContext executionContext)
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_gpu is not null)
            {
                await executionContext.ExecuteGpuAsync(_ => { _gpu.Dispose(); return true; }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch { _gpuDisabled = true; throw; }
        _gpu = null;

        RuntimeLog.TryBackgroundInfo(
            $"rFamilyPerformanceSummary=true;family={FamilyId};" +
            $"implementation={(_gpuDisabled ? "Faulted" : "GpuPrimary")};device={Sanitize(_device)};" +
            "gpuNextIntImplementation=HistoricalShortcut;gpuNextIntExact=false;ownerApprovedApproximation=true;" +
            "candidateDecode=DensePackedBase34Carry8_CompactFullDecode;" +
            $"predicateCount={_plan.PredicateCount};ordinaryPredicateCount={_plan.Predicates.Length};" +
            $"shopRelicPredicateCount={_plan.ShopPredicates.Length};invocations={_invocations};" +
            $"denseInputs={_denseInputs};compactAbi1Inputs={_compactInputs};emptyInputs={_emptyInputs};" +
            $"gpuBatches={_gpuBatches};inputCandidates={_inputCandidates};processedCandidates={_processedCandidates};" +
            $"liveEtaSteadyBatches={_liveSteadyBatches};durableComparableSteadyBatches={_comparableSteadyBatches};" +
            $"minimumDurableCompactInput={FamilyPerformanceEvidenceEligibility.MinimumDurableCompactInput(RelicFamilyGpuExecutor.CompactInputCapacity)};" +
            $"survivors={_survivors};" +
            $"physicalMs={F(_physicalMs)};wallMs={F(_wallMs)};" +
            $"canonicalAbi1ReadyMs={F(_canonicalAbi1ReadyMs)};" +
            $"physicalCandidatesPerSecond={F(CandidatesPerSecond(_inputCandidates, _physicalMs))};" +
            $"wallCandidatesPerSecond={F(CandidatesPerSecond(_inputCandidates, _wallMs))};" +
            $"canonicalAbi1ReadyCandidatesPerSecond={F(CandidatesPerSecond(_inputCandidates, _canonicalAbi1ReadyMs))};" +
            $"performanceDenseRevision={_denseConditionPerformance.PhysicalImplementationRevision};" +
            $"performanceCompactRevision={_compactConditionPerformance.PhysicalImplementationRevision};" +
            $"performanceTimingBoundary=CanonicalAbi1Ready;observedInputMode={(_observedCompactAbi1Input == true ? "CompactAbi1" : "Dense")};" +
            $"performanceBatchSamples={_performanceSamples};performanceSampling=First3ThenEvery256;" +
            $"referenceParity={(_referenceParityPassed ? "Pass" : _referenceMeasured ? "Fail" : "NotRun")};" +
            $"batchCapacity={RelicFamilyGpuExecutor.Capacity};compactInputCapacity={RelicFamilyGpuExecutor.CompactInputCapacity};" +
            $"outputCapacity={RelicFamilyGpuExecutor.OutputCapacity};workgroupSize={RelicFamilyGpuExecutor.WorkgroupSize};" +
            $"seedsPerInvocation={RelicFamilyGpuExecutor.SeedsPerInvocation};dispatchGroups={_dispatchGroups};" +
            "processedCounting=GpuWorkgroupAggregate512;" +
            $"workspaceBytes={_workspaceBytes};workspaceBytesPerDenseCandidate={F((double)_workspaceBytes / RelicFamilyGpuExecutor.Capacity)};" +
            $"inputWorkspaceBytes={RelicFamilyGpuExecutor.InputWorkspaceBytes};outputWorkspaceBytes={RelicFamilyGpuExecutor.OutputWorkspaceBytes};" +
            $"scratchWorkspaceBytes=0;captureWorkspaceBytes=0;localTrackedStateCapacity={RelicFamilyPlanCompiler.MaximumShaderLocalState};" +
            $"maxRuntimeBucketLength={_plan.Pool.MaxBucketLength};trackedCounts={string.Join("|", _plan.TrackedCountsByLane)};" +
            $"resourceLifetime=Session;readbackBytes={_readbackBytes};physicalAlgorithm=TrackedPositionsStreaming;" +
            $"gpuOwnerThreadId={_ownerThreadId};gpuThreadOwnership=SessionDedicated;" +
            $"abi=ABI1-BatchLocalLogicalOrdinal;legacyFallback=false;physicalFailure={Sanitize(_failure)}");
    }

    private void ObserveInputMode(bool compactAbi1Input)
    {
        if (_observedCompactAbi1Input.HasValue && _observedCompactAbi1Input.Value != compactAbi1Input)
            throw new InvalidOperationException("RFamilyInputModeChangedWithinSession");
        _observedCompactAbi1Input = compactAbi1Input;
    }

    private async ValueTask EnsureGpuAsync(FamilyExecutionContext context, CancellationToken cancellationToken)
    {
        if (_gpu is not null) return;
        _gpu = await context.ExecuteGpuAsync(
            rd => RelicFamilyGpuExecutor.Create(rd, _request, _plan), cancellationToken).ConfigureAwait(false);
        _setupMs = _gpu.SetupMs;
        _device = _gpu.DeviceName;
        _workspaceBytes = _gpu.WorkspaceBytes;
        _ownerThreadId = context.GpuOwnerThreadId;
        RuntimeLog.TryBackgroundInfo(
            $"rFamilyPhysicalReady=true;family={FamilyId};implementation=GpuPrimary;device={Sanitize(_device)};" +
            "gpuNextIntImplementation=HistoricalShortcut;gpuNextIntExact=false;ownerApprovedApproximation=true;" +
            "candidateDecode=DensePackedBase34Carry8_CompactFullDecode;" +
            $"setupMs={F(_setupMs)};predicateCount={_plan.PredicateCount};" +
            $"ordinaryPredicateCount={_plan.Predicates.Length};shopRelicPredicateCount={_plan.ShopPredicates.Length};" +
            $"batchCapacity={RelicFamilyGpuExecutor.Capacity};compactInputCapacity={RelicFamilyGpuExecutor.CompactInputCapacity};" +
            $"outputCapacity={RelicFamilyGpuExecutor.OutputCapacity};workspaceBytes={_gpu.WorkspaceBytes};" +
            $"workspaceBytesPerDenseCandidate={F((double)_gpu.WorkspaceBytes / RelicFamilyGpuExecutor.Capacity)};" +
            $"inputWorkspaceBytes={RelicFamilyGpuExecutor.InputWorkspaceBytes};outputWorkspaceBytes={RelicFamilyGpuExecutor.OutputWorkspaceBytes};" +
            $"scratchWorkspaceBytes=0;captureWorkspaceBytes=0;localTrackedStateCapacity={RelicFamilyPlanCompiler.MaximumShaderLocalState};" +
            $"maxRuntimeBucketLength={_plan.Pool.MaxBucketLength};trackedCounts={string.Join("|", _plan.TrackedCountsByLane)};" +
            $"seedsPerInvocation={RelicFamilyGpuExecutor.SeedsPerInvocation};physicalAlgorithm=TrackedPositionsStreaming;" +
            "processedCounting=GpuWorkgroupAggregate512;" +
            $"gpuOwnerThreadId={_ownerThreadId};gpuThreadOwnership=SessionDedicated;resourceLifetime=Session;" +
            "denseInput=true;compactAbi1Input=true;legacyBackendUsed=false;legacyFastPlanUsed=false");
    }

    private bool MeasureReferenceAndParity(FamilyCandidateSet input, FamilyCandidateSet gpuOutput, out string issue)
    {
        _referenceMeasured = true;
        issue = string.Empty;
        ulong[] sample = input.EnumerateLogicalOrdinals().Take(ReferenceSampleCapacity).ToArray();
        var expected = new HashSet<ulong>();
        int preview = Math.Max(
            _request.Evaluation.RelicSequenceConditions.Select(condition => condition.RangeValue).DefaultIfEmpty(1).Max(),
            _request.Evaluation.RelicShopSequenceConditions.Select(condition => condition.Count).DefaultIfEmpty(1).Max());
        foreach (ulong logicalOrdinal in sample)
        {
            ulong global = input.Batch.GlobalCandidate(logicalOrdinal);
            string seed = VisibleSeedCandidateCodec.FormatOrdinal(_profile, global);
            ulong root = _profile.ComputeRootSeed(seed);
            RelicSequencePredictionResult prediction = RelicSequencePredictor.PredictFromRootHash(
                _profile, root, seed, _request.Authority.WorldAuthority, preview);
            if (prediction.Status != SeedDomainEvaluationStatus.Evaluated ||
                prediction.Lanes.Any(lane => lane.Precision != PredictionPrecision.Exact))
            {
                issue = "ReferencePredictionNotExact";
                return false;
            }
            if (MatchesReference(prediction)) expected.Add(logicalOrdinal);
        }
        ReadOnlySpan<ulong> actual = gpuOutput.ExportAbi1().Span;
        foreach (ulong logicalOrdinal in sample)
        {
            bool cpu = expected.Contains(logicalOrdinal);
            bool gpu = actual.BinarySearch(logicalOrdinal) >= 0;
            if (cpu == gpu) continue;
            issue = $"logicalOrdinal={logicalOrdinal};cpu={cpu};gpu={gpu}";
            RuntimeLog.TryBackgroundWarning(
                $"rFamilyReferenceParity=false;family={FamilyId};sampleCandidates={sample.Length};" +
                $"failure={Sanitize(issue)};recovery=ExactOnly;legacyFallback=false");
            return false;
        }
        _referenceParityPassed = true;
        RuntimeLog.TryBackgroundInfo(
            $"rFamilyReferenceParity=true;family={FamilyId};sampleCandidates={sample.Length};" +
            "referenceImplementation=CoreRelicSequencePredictor;legacyFallback=false");
        return true;
    }

    internal bool MatchesCpuReference(ulong root)
    {
        int preview = Math.Max(_request.Evaluation.RelicSequenceConditions.Select(c => c.RangeValue).DefaultIfEmpty(1).Max(),
            _request.Evaluation.RelicShopSequenceConditions.Select(c => c.Count).DefaultIfEmpty(1).Max());
        var prediction = RelicSequencePredictor.PredictFromRootHash(_profile, root, "CpuReference", _request.Authority.WorldAuthority, preview);
        if (prediction.Status != SeedDomainEvaluationStatus.Evaluated || prediction.Lanes.Any(l => l.Precision != PredictionPrecision.Exact))
            throw new InvalidOperationException("R.CpuReferenceNotExact");
        return MatchesReference(prediction);
    }

    private bool MatchesReference(RelicSequencePredictionResult prediction)
    {
        foreach (RelicSequenceSearchCondition condition in _request.Evaluation.RelicSequenceConditions.Where(c => !c.IsEmpty))
        {
            RelicSequenceLaneResult? lane = prediction.Lanes.FirstOrDefault(item => item.Kind == condition.Lane);
            if (lane is null) return false;
            var range = condition.RangeMode == SearchSequenceRangeMode.FirstN
                ? lane.Entries.Where(item => item.Position <= condition.RangeValue).Select(item => item.RelicKey).ToArray()
                : lane.Entries.Where(item => item.Position == condition.RangeValue).Select(item => item.RelicKey).ToArray();
            if (condition.RangeMode == SearchSequenceRangeMode.ExactSlot && range.Length != 1) return false;
            if (!MatchesKeys(range, condition.Keys)) return false;
        }
        RelicSequenceLaneResult? shop = prediction.Lanes.FirstOrDefault(item => item.Kind == RelicSequenceKind.Shop);
        foreach (RelicShopSequenceSearchCondition condition in _request.Evaluation.RelicShopSequenceConditions.Where(c => !c.IsEmpty))
        {
            if (shop is null) return false;
            var actual = shop.Entries.Where(item => item.Position <= condition.Count)
                .OrderBy(item => item.Position).Select(item => item.RelicKey).ToArray();
            if (actual.Length < condition.Count) return false;
            if (!ShopSequenceSemantics.Matches(actual, condition.Count, condition.OrderMode, condition.Slots)) return false;
        }
        return true;
    }

    private static bool MatchesKeys(IReadOnlyList<ModelKey> actual, ModelKeySetFilter keys) =>
        (keys.Any.Count == 0 || keys.Any.Any(actual.Contains)) &&
        keys.All.All(actual.Contains) &&
        !keys.Ban.Any(actual.Contains);

    private async ValueTask DisableGpuAsync(FamilyExecutionContext context, string failure)
    {
        if (_gpuDisabled) return;
        _gpuDisabled = true;
        _failure = failure;
        try
        {
            if (_gpu is not null)
                await context.ExecuteGpuAsync(_ => { _gpu.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _gpu = null;
        RuntimeLog.TryBackgroundWarning(
            $"rFamilyPhysicalFault=true;family={FamilyId};failure={Sanitize(failure)};" +
            "recovery=None;selectedExecutionFault=true;legacyFallback=false");
    }

    private static double CandidatesPerSecond(long candidates, double milliseconds) =>
        milliseconds <= 0d ? 0d : candidates * 1000d / milliseconds;

    private static void UpdateMaximum(ref double target, double value)
    {
        if (value > target && double.IsFinite(value)) target = value;
    }

    private static string F(double value) =>
        value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    private static string Sanitize(string value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
