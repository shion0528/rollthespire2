using System.Diagnostics;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Merchant;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Family S: one independent replay of the pristine Normal Merchant Shops stream,
/// evaluating every active uncommon/rare Colorless predicate in that one traversal.
/// Its primary physical implementation is one session-resident GPU program; any
/// physical failure is explicit and fails open to the independent Exact terminal.
/// </summary>
internal sealed partial class MerchantShopColorlessFamily : IFamilyInvocation
{
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    private const int CpuReferenceSampleCapacity = 4096;
    private const int PerformanceSamplePeriod = 256;
    private const int InitialPerformanceSampleCount = 3;
    private readonly ExactSearchExecutionRequest _plan;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    private readonly IRuntimeProfile _profile;
    private readonly FamilyAnalyticalCostProjection _analyticalCost;
    private readonly FamilySurvivalProjection _survival;
    private readonly FamilyExpectedFilteringCostProjection _expectedFilteringCost;
    private readonly FamilyExpectedFilteringCostProjection _gpuExpectedFilteringCost;
    private readonly MerchantOrderedSequenceSlotsPlan _orderedSequenceSlots;
    private readonly FamilyConditionPerformanceProjection _denseConditionPerformance;
    private readonly FamilyConditionPerformanceProjection _compactConditionPerformance;
    private readonly MerchantShopColorlessCompactionDecision _compactionDecision;
    private MerchantShopColorlessGpuExecutor? _gpu;
    private bool _gpuDisabled;
    private bool _referenceMeasured;
    private bool _referenceParityPassed;
    private string _physicalFailure = string.Empty;
    private string _deviceName = "not-created";
    private int _gpuOwnerThreadId;
    private double _setupMs;
    private double _gpuPhysicalMs;
    private double _gpuWallMs;
    private double _cpuReferenceMs;
    private long _gpuInputCandidates;
    private long _gpuProcessedCandidates;
    private long _gpuSurvivors;
    private long _readbackBytes;
    private long _cpuReferenceCandidates;
    private long _lastGpuCompletionTimestamp;
    private long _gpuInterBatchGapTicks;
    private int _gpuBatches;
    private int _performanceBatchSamples;
    private int _gpuInterBatchGapSamples;
    private int _comparableSteadyBatches;
    private int _liveSteadyBatches;
    private long _liveSteadyInputCandidates;
    private double _liveSteadyCanonicalMs;
    private double _currentSearchPeakCandidatesPerSecond;
    private double _validSearchPeakCandidatesPerSecond;
    private bool _stableCompactionUsed;
    private double _stableCountsReadbackMs;
    private double _stableHostPrefixMs;
    private double _stableOffsetsUploadMs;
    private double _stablePhase1Ms;
    private double _stableScatterMs;
    private double _canonicalAbi1ReadyMs;
    private bool? _observedCompactAbi1Input;
    private bool _disposed;

    private MerchantShopColorlessFamily(ExactSearchExecutionRequest plan, IRuntimeProfile profile)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(plan.RunOptions.Optimizations);
        _headerTransport = new(plan.RunOptions.Optimizations);
        _plan = plan;
        _profile = profile;
        _orderedSequenceSlots = MerchantShopColorlessOrderedSequencePolicy.Compile(plan);
        _analyticalCost = MerchantShopColorlessAnalyticalCost.Project(plan.Evaluation);
        _survival = MerchantShopColorlessSurvival.Project(plan);
        _compactionDecision = MerchantShopColorlessCompactionPolicy.Select(_survival);
        _expectedFilteringCost = MerchantShopColorlessExpectedFilteringCost.Project(
            plan, _analyticalCost, _survival);
        _gpuExpectedFilteringCost = !_orderedSequenceSlots.Applied ? _expectedFilteringCost :
            MerchantShopColorlessExpectedFilteringCost.ProjectPhysical(plan, _orderedSequenceSlots.Evaluation,
                MerchantShopColorlessAnalyticalCost.Project(_orderedSequenceSlots.Evaluation), _survival);
        string densePerformanceRevision = _compactionDecision.Path == MerchantShopColorlessCompactionPath.StableOrderedCompaction
            ? FamilyPhysicalImplementationRevisions.MerchantShopColorlessStableDensePerformance
            : FamilyPhysicalImplementationRevisions.MerchantShopColorlessAtomicDensePerformance;
        string prefixRevision = SharedVisibleSeedGpuCache.RevisionSuffix;
        _denseConditionPerformance = MerchantShopColorlessConditionPerformance.Project(
            _orderedSequenceSlots.Evaluation, densePerformanceRevision + prefixRevision + _orderedSequenceSlots.RevisionSuffix);
        _compactConditionPerformance = MerchantShopColorlessConditionPerformance.Project(
            _orderedSequenceSlots.Evaluation, FamilyPhysicalImplementationRevisions.MerchantShopColorlessCompactPerformance +
                prefixRevision + _orderedSequenceSlots.RevisionSuffix);
    }

    public string FamilyId => "S.MerchantShopColorless";
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest request)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_plan.RunOptions.Optimizations);
        return MerchantShopColorlessPhysicalPricing.Quote(_plan, _compactionDecision, request,
            _gpuExpectedFilteringCost, _orderedSequenceSlots);
    }
    bool IFamilyInvocation.CanBindPrivateSerial => !_gpuDisabled;
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        if (_gpuDisabled) throw new InvalidOperationException("S.PrivateSerialFaulted");
        var path = input is null && output is null ? _compactionDecision.Path : MerchantShopColorlessCompactionPath.AtomicAppendHostSort;
        var gpu = MerchantShopColorlessGpuExecutor.Create(rd, _plan, path, input, output,
            useOrderedSequenceSlots: _orderedSequenceSlots.Requested);
        _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
        return new(gpu, gpu.DeviceName, gpu.SetupMs, input, output, (batch, count, token) =>
        {
            FamilyCandidateSet? result = null; MerchantShopColorlessGpuBatchMetrics m;
            if (output is not null) gpu.ExecutePrivateStage(batch, count, token, out m);
            else result = input is null ? gpu.Execute(FamilyCandidateSet.Dense(batch), token, out m) : gpu.ExecutePrivate(batch, count, token, out m);
            _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
            return new(m.Survivors, result, m.CommandMs + m.SubmitMs + m.SyncMs,
                m.ReadbackMs, m.ReadbackBytes, m.CanonicalAbi1ReadyMs,
                m.Dispatches);
        });
    }
    public FamilyAnalyticalCostProjection AnalyticalCost => _analyticalCost;
    public FamilySurvivalProjection Survival => _survival;
    public FamilyExpectedFilteringCostProjection ExpectedFilteringCost => _expectedFilteringCost;
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compactAbi1Input) =>
        _headerTransport.Apply(compactAbi1Input ? _compactConditionPerformance : _denseConditionPerformance,
            _gpu?.HeaderReadbackRevision);
    internal int PreferredExecutionWindowSize => MerchantShopColorlessGpuExecutor.Capacity;

    public static bool TryCreate(
        ExactSearchExecutionRequest plan,
        out IFamilyInvocation? family)
    {
        ArgumentNullException.ThrowIfNull(plan);
        family = null;
        if (!plan.Evaluation.RequiresMerchantColorlessDomain)
            return false;

        IRuntimeProfile profile = RuntimeProfileRegistry.Select(plan.Detection);
        Beta111MerchantColorlessAuthority authority = Beta111MerchantColorlessAuthority.From(plan.Authority);
        if (profile.ProfileId != RuntimeProfileId.Beta111 || !authority.HasExactV1Inputs || authority.PlayersCount > 4)
            return false;

        int maxOrdinal = plan.Evaluation.MerchantColorlessConditions.Count == 0
            ? 0
            : plan.Evaluation.MerchantColorlessConditions.Max(condition => condition.MerchantOrdinal);
        if (plan.Evaluation.MerchantColorlessSequenceConditions.Count > 0)
            maxOrdinal = Math.Max(maxOrdinal, plan.Evaluation.MerchantColorlessSequenceConditions.Max(condition => condition.Count));
        if (maxOrdinal is < 1 or > Beta111NormalMerchantColorlessSequenceProjector.MerchantCount)
            return false;

        family = new MerchantShopColorlessFamily(plan, profile);
        return true;
    }

    public async ValueTask<FamilyCandidateSet> InvokeAsync(
        FamilyExecutionContext executionContext,
        FamilyObservationWindow observationWindow,
        FamilyCandidateSet input,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(
                observationWindow.ExactRequest.SnapshotFingerprint,
                _plan.SnapshotFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SFamilyObservationContextMismatch");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObserveInputMode(!input.IsDense);
        if (_gpuDisabled) throw new InvalidOperationException("S.SelectedInvocationFaulted");

        try
        {
            await EnsureGpuAsync(executionContext, cancellationToken).ConfigureAwait(false);
            var wall = Stopwatch.StartNew();
            (FamilyCandidateSet survivors, MerchantShopColorlessGpuBatchMetrics metrics, long ownerCompletionTimestamp) =
                await executionContext.ExecuteGpuAsync(
                    _ =>
                    {
                        long gpuStartTimestamp = Stopwatch.GetTimestamp();
                        if (_lastGpuCompletionTimestamp != 0 && Volatile.Read(ref _gpuBatches) > 1)
                        {
                            _gpuInterBatchGapTicks += gpuStartTimestamp - _lastGpuCompletionTimestamp;
                            _gpuInterBatchGapSamples++;
                        }
                        FamilyCandidateSet result = _gpu!.Execute(
                            input, cancellationToken, out MerchantShopColorlessGpuBatchMetrics batchMetrics);
                        _lastGpuCompletionTimestamp = Stopwatch.GetTimestamp();
                        return (result, batchMetrics, _lastGpuCompletionTimestamp);
                    },
                    cancellationToken).ConfigureAwait(false);
            wall.Stop();

            if (!_referenceMeasured && !MeasureReferenceAndParity(input, survivors, out string parityFailure))
            {
                throw new InvalidDataException("ReferenceParityMismatch:" + parityFailure);
            }

            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, metrics.CommandMs + metrics.SubmitMs + metrics.SyncMs, outputs: metrics.Survivors, canonicalMs: metrics.CanonicalAbi1ReadyMs, readbackMs: metrics.ReadbackMs, readbackBytes: metrics.ReadbackBytes);
            int batchNumber = ++_gpuBatches;
            _gpuInputCandidates += metrics.InputCandidates;
            _gpuProcessedCandidates += metrics.ProcessedCandidates;
            _gpuSurvivors += metrics.Survivors;
            _gpuPhysicalMs += metrics.PhysicalMs;
            _gpuWallMs += wall.Elapsed.TotalMilliseconds;
            _readbackBytes += metrics.ReadbackBytes;
            _canonicalAbi1ReadyMs += metrics.CanonicalAbi1ReadyMs;
            if (metrics.CompactionPath == MerchantShopColorlessCompactionPath.StableOrderedCompaction)
            {
                _stableCompactionUsed = true;
                _stableCountsReadbackMs += metrics.StableCountsReadbackMs;
                _stableHostPrefixMs += metrics.StableHostPrefixMs;
                _stableOffsetsUploadMs += metrics.StableOffsetsUploadMs;
                _stablePhase1Ms += metrics.StablePhase1CommandMs + metrics.StablePhase1SubmitMs + metrics.StablePhase1SyncMs;
                _stableScatterMs += metrics.StableScatterCommandMs + metrics.StableScatterSubmitMs + metrics.StableScatterSyncMs;
            }
            double canonicalCandidatesPerSecond =
                CandidatesPerSecond(metrics.InputCandidates, metrics.CanonicalAbi1ReadyMs);
            UpdateMaximum(ref _currentSearchPeakCandidatesPerSecond, canonicalCandidatesPerSecond);
            bool liveSteadyInput = FamilyPerformanceEvidenceEligibility.IsLiveSteadyInput(
                input.IsDense, metrics.InputCandidates, MerchantShopColorlessGpuExecutor.Capacity);
            bool durableComparableInput = FamilyPerformanceEvidenceEligibility.IsDurableComparableInput(
                input.IsDense, metrics.InputCandidates,
                MerchantShopColorlessGpuExecutor.Capacity,
                MerchantShopColorlessGpuExecutor.CompactInputCapacity);
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
            if (ShouldSamplePerformance(batchNumber))
            {
                _performanceBatchSamples++;
                RuntimeLog.TryBackgroundDetail(
                    $"sFamilyPerformanceBatch=true;family={FamilyId};batchNumber={batchNumber};" +
                    $"predicateCount={PredicateCount};maximumMerchantOrdinal={MaximumMerchantOrdinal};" +
                    $"inputCandidates={metrics.InputCandidates};" +
                    $"processedCandidates={metrics.ProcessedCandidates};survivors={metrics.Survivors};" +
                    $"liveEtaEligible={(batchNumber > 1 && liveSteadyInput).ToString().ToLowerInvariant()};" +
                    $"durablePerformanceEligible={(batchNumber > 1 && durableComparableInput).ToString().ToLowerInvariant()};" +
                    $"minimumDurableCompactInput={FamilyPerformanceEvidenceEligibility.MinimumDurableCompactInput(MerchantShopColorlessGpuExecutor.CompactInputCapacity)};" +
                    $"compactionPath={metrics.CompactionPath};" +
                    $"physicalMs={F(metrics.PhysicalMs)};wallMs={F(wall.Elapsed.TotalMilliseconds)};" +
                    $"canonicalAbi1ReadyMs={F(metrics.CanonicalAbi1ReadyMs)};" +
                    $"physicalCandidatesPerSecond={F(metrics.CandidatesPerSecond)};" +
                    $"wallCandidatesPerSecond={F(CandidatesPerSecond(metrics.InputCandidates, wall.Elapsed.TotalMilliseconds))};" +
                    $"phase1GpuBoundaryMs={F(metrics.StablePhase1CommandMs + metrics.StablePhase1SubmitMs + metrics.StablePhase1SyncMs)};" +
                    $"stableCountsReadbackMs={F(metrics.StableCountsReadbackMs)};stableHostPrefixMs={F(metrics.StableHostPrefixMs)};" +
                    $"stableOffsetsUploadMs={F(metrics.StableOffsetsUploadMs)};" +
                    $"stableScatterGpuBoundaryMs={F(metrics.StableScatterCommandMs + metrics.StableScatterSubmitMs + metrics.StableScatterSyncMs)};" +
                    $"payloadReadbackMs={F(metrics.PayloadReadbackMs)};hostDecodeMs={F(metrics.DecodePayloadMs)};" +
                    $"hostSortMs={F(metrics.SortMs)};hostValidationMs={F(metrics.ValidationCandidateSetMs)};" +
                    $"syncMs={F(metrics.SyncMs)};readbackBytes={metrics.ReadbackBytes};sampled=true");
            }
            executionContext.RecordPipelineBoundarySource(
                input.Batch, metrics, ownerCompletionTimestamp, batchNumber);
            return survivors;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
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
                        string.IsNullOrWhiteSpace(_physicalFailure) &&
                        _comparableSteadyBatches > 0 &&
                        _validSearchPeakCandidatesPerSecond > 0d &&
                        double.IsFinite(_validSearchPeakCandidatesPerSecond);
        string evidence = eligible
            ? $"Clean{(compact ? "CompactAbi1" : "FullDense")}CanonicalSteadyBatches;" +
              "timingBoundary=CanonicalAbi1Ready;setupExcluded=true;coldFirstBatchExcluded=true;recovery=false;parity=Pass"
            : _gpuDisabled ? "PhysicalFailureOrRecovery"
            : !_referenceParityPassed ? "ReferenceParityUnavailable"
            : compact && _liveSteadyBatches > 0
                ? $"CompactUnderfillNotDurableComparable:minimumInput={FamilyPerformanceEvidenceEligibility.MinimumDurableCompactInput(MerchantShopColorlessGpuExecutor.CompactInputCapacity)}"
            : compact ? "NoCompactAbi1SteadyBatch"
            : "NoFullDenseSteadyBatch";
        return new FamilyPerformanceObservation(
            condition,
            _deviceName,
            _setupMs,
            _currentSearchPeakCandidatesPerSecond,
            _validSearchPeakCandidatesPerSecond,
            _gpuBatches,
            _comparableSteadyBatches,
            _gpuInputCandidates,
            _gpuSurvivors,
            _canonicalAbi1ReadyMs,
            eligible,
            evidence);
    }

    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot()
    {
        long inputs = Interlocked.Read(ref _liveSteadyInputCandidates);
        double milliseconds = Volatile.Read(ref _liveSteadyCanonicalMs);
        int batches = Volatile.Read(ref _liveSteadyBatches);
        if (_gpuDisabled || !_referenceParityPassed || !string.IsNullOrWhiteSpace(_physicalFailure) ||
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
                await executionContext.ExecuteGpuAsync(
                    _ =>
                    {
                        _gpu.Dispose();
                        return true;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { _gpuDisabled = true; throw; }
        _gpu = null;

        double gpuCps = _gpuPhysicalMs <= 0d ? 0d : _gpuInputCandidates * 1000d / _gpuPhysicalMs;
        double gpuWallCps = CandidatesPerSecond(_gpuInputCandidates, _gpuWallMs);
        double cpuCps = _cpuReferenceMs <= 0d ? 0d : _cpuReferenceCandidates * 1000d / _cpuReferenceMs;
        RuntimeLog.TryBackgroundInfo(
            $"sFamilyPerformanceSummary=true;family={FamilyId};" +
            $"implementation={(_gpuDisabled ? "Faulted" : "GpuPrimary")};device={Sanitize(_deviceName)};" +
            $"compactionPath={_compactionDecision.Path};compactionPolicyMode={_compactionDecision.Mode};" +
            $"analyticalFinalSurvival={Maybe(_compactionDecision.AnalyticalFinalSurvival)};" +
            $"stableThreshold={F(_compactionDecision.StableThreshold)};compactionSelectionReason={_compactionDecision.Reason};" +
            $"physicalImplementationRevision={_compactionDecision.PhysicalImplementationRevision + _orderedSequenceSlots.RevisionSuffix};" +
            $"performanceDenseRevision={_denseConditionPerformance.PhysicalImplementationRevision};" +
            $"performanceCompactRevision={_compactConditionPerformance.PhysicalImplementationRevision};" +
            $"performanceTimingBoundary=CanonicalAbi1Ready;observedInputMode={(_observedCompactAbi1Input == true ? "CompactAbi1" : "Dense")};" +
            "gpuNextIntImplementation=HistoricalShortcut;gpuNextIntExact=false;ownerApprovedApproximation=true;" +
            "rngBurnImplementation=StateAdvanceOnly26PerMerchant;" +
            "candidateDecode=DensePackedBase34Carry8_CompactFullDecode;" +
            $"predicateCount={PredicateCount};maximumMerchantOrdinal={MaximumMerchantOrdinal};" +
            $"slotPredicateCount={_plan.Evaluation.MerchantColorlessConditions.Count};" +
            $"sequencePredicateCount={_plan.Evaluation.MerchantColorlessSequenceConditions.Count};" +
            $"orderedSequenceSlots={_orderedSequenceSlots.Applied};orderedSequenceSlotsRevision={_orderedSequenceSlots.RevisionSuffix};" +
            $"packedSlotPredicateCount={_orderedSequenceSlots.Evaluation.MerchantColorlessConditions.Count};" +
            $"packedSequencePredicateCount={_orderedSequenceSlots.Evaluation.MerchantColorlessSequenceConditions.Count};" +
            $"packedMaximumMerchantOrdinal={_orderedSequenceSlots.PackedMaximumOrdinal};" +
            $"gpuInputCandidates={_gpuInputCandidates};gpuProcessedCandidates={_gpuProcessedCandidates};" +
            $"gpuSurvivors={_gpuSurvivors};gpuBatches={_gpuBatches};" +
            $"liveEtaSteadyBatches={_liveSteadyBatches};durableComparableSteadyBatches={_comparableSteadyBatches};" +
            $"minimumDurableCompactInput={FamilyPerformanceEvidenceEligibility.MinimumDurableCompactInput(MerchantShopColorlessGpuExecutor.CompactInputCapacity)};" +
            $"performanceBatchSamples={_performanceBatchSamples};performanceSampling=First3ThenEvery256;" +
            $"gpuInterBatchGapSamples={_gpuInterBatchGapSamples};" +
            $"gpuInterBatchGapMs={F(ElapsedMilliseconds(_gpuInterBatchGapTicks))};" +
            $"gpuInterBatchGapAverageMs={F(AverageElapsedMilliseconds(_gpuInterBatchGapTicks, _gpuInterBatchGapSamples))};" +
            $"setupMs={F(_setupMs)};gpuPhysicalMs={F(_gpuPhysicalMs)};gpuWallMs={F(_gpuWallMs)};" +
            $"canonicalAbi1ReadyMs={F(_canonicalAbi1ReadyMs)};stablePhase1GpuBoundaryMs={F(_stablePhase1Ms)};" +
            $"canonicalAbi1ReadyCandidatesPerSecond={F(CandidatesPerSecond(_gpuInputCandidates, _canonicalAbi1ReadyMs))};" +
            $"stableCountsReadbackMs={F(_stableCountsReadbackMs)};stableHostPrefixMs={F(_stableHostPrefixMs)};" +
            $"stableOffsetsUploadMs={F(_stableOffsetsUploadMs)};stableScatterGpuBoundaryMs={F(_stableScatterMs)};" +
            $"gpuPhysicalCandidatesPerSecond={F(gpuCps)};gpuWallCandidatesPerSecond={F(gpuWallCps)};" +
            $"cpuReferenceCandidates={_cpuReferenceCandidates};" +
            $"cpuReferenceMs={F(_cpuReferenceMs)};cpuReferenceCandidatesPerSecond={F(cpuCps)};" +
            $"referenceParity={(_referenceParityPassed ? "Pass" : _referenceMeasured ? "Fail" : "NotRun")};" +
            $"workgroupSize={MerchantShopColorlessGpuExecutor.WorkgroupSize};" +
            $"seedsPerInvocation={MerchantShopColorlessGpuExecutor.SeedsPerInvocation};" +
            "processedCounting=GpuWorkgroupAggregate512;" +
            $"batchCapacity={MerchantShopColorlessGpuExecutor.Capacity};resourceLifetime=Session;" +
            $"compactInputCapacity={MerchantShopColorlessGpuExecutor.CompactInputCapacity};" +
            $"survivorCapacity={MerchantShopColorlessGpuExecutor.SurvivorCapacity};" +
            $"gpuOwnerThreadId={_gpuOwnerThreadId};gpuThreadOwnership=SessionDedicated;" +
            $"dispatchesPerBatch={(_stableCompactionUsed ? 2 : 1)};syncsPerBatch={(_stableCompactionUsed ? 2 : 1)};" +
            $"headerReadbacksPerBatch={(_stableCompactionUsed ? 2 : 1)};payloadReadbacksPerBatch=1;" +
            "hardwarePerformanceEvidenceIsolation=FamilyAndPhysicalImplementationRevision;" +
            $"readbackBytes={_readbackBytes};abi=ABI1-BatchLocalLogicalOrdinal;legacyFallback=false;" +
            $"physicalFailure={Sanitize(_physicalFailure)}");
    }

    private void ObserveInputMode(bool compactAbi1Input)
    {
        if (_observedCompactAbi1Input.HasValue && _observedCompactAbi1Input.Value != compactAbi1Input)
            throw new InvalidOperationException("SFamilyInputModeChangedWithinSession");
        _observedCompactAbi1Input = compactAbi1Input;
    }

    private int PredicateCount =>
        _plan.Evaluation.MerchantColorlessConditions.Count +
        _plan.Evaluation.MerchantColorlessSequenceConditions.Count;

    private int MaximumMerchantOrdinal =>
        _plan.Evaluation.MerchantColorlessConditions.Select(item => item.MerchantOrdinal)
            .Concat(_plan.Evaluation.MerchantColorlessSequenceConditions.Select(item => item.Count))
            .DefaultIfEmpty(0)
            .Max();

    private async ValueTask EnsureGpuAsync(
        FamilyExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (_gpu is not null) return;
        _gpu = await executionContext.ExecuteGpuAsync(
            rd => MerchantShopColorlessGpuExecutor.Create(rd, _plan, _compactionDecision.Path,
                useOrderedSequenceSlots: _orderedSequenceSlots.Requested),
            cancellationToken).ConfigureAwait(false);
        _setupMs = _gpu.SetupMs;
        _deviceName = _gpu.DeviceName;
        _gpuOwnerThreadId = executionContext.GpuOwnerThreadId;
        RuntimeLog.TryBackgroundInfo(
            $"sFamilyPhysicalReady=true;family={FamilyId};implementation=GpuPrimary;device={Sanitize(_deviceName)};" +
            $"compactionPath={(_gpu.StableOrderedCompactionEnabled ? MerchantShopColorlessCompactionPath.StableOrderedCompaction : MerchantShopColorlessCompactionPath.AtomicAppendHostSort)};" +
            $"compactionPolicyMode={_compactionDecision.Mode};analyticalFinalSurvival={Maybe(_compactionDecision.AnalyticalFinalSurvival)};" +
            $"stableThreshold={F(_compactionDecision.StableThreshold)};compactionSelectionReason={_compactionDecision.Reason};" +
            $"physicalImplementationRevision={_gpu.PhysicalImplementationRevision};" +
            "gpuNextIntImplementation=HistoricalShortcut;gpuNextIntExact=false;ownerApprovedApproximation=true;" +
            "rngBurnImplementation=StateAdvanceOnly26PerMerchant;" +
            "candidateDecode=DensePackedBase34Carry8_CompactFullDecode;" +
            $"maximumMerchantOrdinal={MaximumMerchantOrdinal};" +
            $"setupMs={F(_setupMs)};resourceLifetime=Session;batchCapacity={MerchantShopColorlessGpuExecutor.Capacity};" +
            $"compactInputCapacity={MerchantShopColorlessGpuExecutor.CompactInputCapacity};" +
            $"survivorCapacity={MerchantShopColorlessGpuExecutor.SurvivorCapacity};" +
            $"stableKeepMaskBytes={(_gpu.StableOrderedCompactionEnabled ? MerchantShopColorlessGpuExecutor.StableKeepMaskBytes : 0)};" +
            $"stableBlockCountBytes={(_gpu.StableOrderedCompactionEnabled ? MerchantShopColorlessGpuExecutor.StableBlockCountBytes : 0)};" +
            $"stableBlockOffsetBytes={(_gpu.StableOrderedCompactionEnabled ? MerchantShopColorlessGpuExecutor.StableBlockOffsetBytes : 0)};" +
            "hardwarePerformanceEvidenceIsolation=FamilyAndPhysicalImplementationRevision;" +
            "processedCounting=GpuWorkgroupAggregate512;" +
            $"gpuOwnerThreadId={_gpuOwnerThreadId};gpuThreadOwnership=SessionDedicated;" +
            "performanceSampling=First3ThenEvery256;legacyCapabilityRegistryUsed=false;" +
            "legacyBackendUsed=false;legacyFastPlanUsed=false");
    }

    private bool MeasureReferenceAndParity(
        FamilyCandidateSet input,
        FamilyCandidateSet gpuOutput,
        out string failure)
    {
        _referenceMeasured = true;
        failure = string.Empty;
        ulong[] sample = input.EnumerateLogicalOrdinals().Take(CpuReferenceSampleCapacity).ToArray();
        var expected = new HashSet<ulong>();
        var timer = Stopwatch.StartNew();
        foreach (ulong logicalOrdinal in sample)
        {
            ulong globalCandidate = input.Batch.GlobalCandidate(logicalOrdinal);
            string seed = VisibleSeedCandidateCodec.FormatOrdinal(_profile, globalCandidate);
            ulong rootHash = _profile.ComputeRootSeed(seed);
            SearchQueryEvaluation result = MerchantShopColorlessQueryEvaluator.Evaluate(
                _plan.Evaluation,
                rootHash,
                _plan.Authority);
            if (result.Disposition != SearchDisposition.NoMatch)
                expected.Add(logicalOrdinal);
        }
        timer.Stop();
        _cpuReferenceCandidates = sample.LongLength;
        _cpuReferenceMs = timer.Elapsed.TotalMilliseconds;

        ReadOnlySpan<ulong> actual = gpuOutput.ExportAbi1().Span;
        foreach (ulong logicalOrdinal in sample)
        {
            bool cpuSurvives = expected.Contains(logicalOrdinal);
            bool gpuSurvives = actual.BinarySearch(logicalOrdinal) >= 0;
            if (cpuSurvives == gpuSurvives) continue;
            failure = $"logicalOrdinal={logicalOrdinal};cpu={cpuSurvives};gpu={gpuSurvives}";
            RuntimeLog.TryBackgroundWarning(
                $"sFamilyReferenceParity=false;family={FamilyId};sampleCandidates={sample.Length};failure={Sanitize(failure)};" +
                "recovery=ExactOnly;legacyFallback=false");
            return false;
        }

        _referenceParityPassed = true;
        double cpuCps = _cpuReferenceMs <= 0d ? 0d : _cpuReferenceCandidates * 1000d / _cpuReferenceMs;
        RuntimeLog.TryBackgroundInfo(
            $"sFamilyReferenceParity=true;family={FamilyId};sampleCandidates={sample.Length};" +
            $"cpuReferenceMs={F(_cpuReferenceMs)};cpuReferenceCandidatesPerSecond={F(cpuCps)};" +
            "referenceImplementation=PreviousPerCandidateCpuReplay;legacyFallback=false");
        return true;
    }

    private async ValueTask DisableGpuAsync(
        FamilyExecutionContext executionContext,
        string failure)
    {
        if (_gpuDisabled) return;
        _gpuDisabled = true;
        _physicalFailure = failure;
        try
        {
            if (_gpu is not null)
            {
                await executionContext.ExecuteGpuAsync(
                    _ =>
                    {
                        _gpu.Dispose();
                        return true;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { }
        _gpu = null;
        RuntimeLog.TryBackgroundWarning(
            $"sFamilyPhysicalFault=true;family={FamilyId};failure={Sanitize(failure)};" +
            "recovery=None;selectedExecutionFault=true;legacyFallback=false");
    }

    private static string F(double value) => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    private static string Maybe(double? value) => value.HasValue
        ? value.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)
        : "unknown";

    private static double CandidatesPerSecond(long candidates, double milliseconds) =>
        milliseconds <= 0d ? 0d : candidates * 1000d / milliseconds;

    private static double ElapsedMilliseconds(long stopwatchTicks) =>
        stopwatchTicks * 1000d / Stopwatch.Frequency;

    private static double AverageElapsedMilliseconds(long stopwatchTicks, int samples) =>
        samples <= 0 ? 0d : ElapsedMilliseconds(stopwatchTicks) / samples;

    private static bool ShouldSamplePerformance(int batchNumber) =>
        batchNumber <= InitialPerformanceSampleCount || batchNumber % PerformanceSamplePeriod == 0;

    private static void UpdateMaximum(ref double target, double value)
    {
        if (value > target && double.IsFinite(value)) target = value;
    }

    private static string Sanitize(string value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
