using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Compatibility;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class NeowFamily : IFamilyInvocation
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    private readonly NeowReplayPlan _plan;
    private readonly Func<ulong, bool> _cpuMatcher;
    private readonly NeowFamilyProjections _models;
    private readonly FamilyCpuExecution _physical;
    private readonly bool _conditional;
    private readonly ExactSearchExecutionRequest _request;
    private readonly NeowTransformationComposite? _transformations;
    private readonly double? _transformationJoint, _givenTransformations;
    private readonly NeowFamilyGpuPlan? _gpuPlan;
    private string? _boundStagedDispatchRevision;
    private string? _boundHeaderReadbackRevision, _boundLeafyGateRevision, _boundPrefixHashRevision;
    private string? _boundSharedPrefixHashRevision;
    private bool _boundSharedPrefixHashCompactInput;
    private string? _boundStaticDrawGateRevision;
    private string? _boundInternalFilterOrderRevision;
    private bool _boundInternalFilterOrderObserved;
    private string? _boundGroupOutputRevision;
    private bool _boundGroupOutputObserved;
    private readonly bool _requestGroupOutput;
    private readonly NeowStructuralTrialOptions _structuralTrials;
    private string? _boundStructuralRevision;
    private bool _boundStructuralObserved;
    private string HeaderReadbackRevision => _gpu?.HeaderReadbackRevision ?? _boundHeaderReadbackRevision ??
        NeowFamilyGpuExecutor.RequestedHeaderReadbackRevision;
    private string LeafyGateRevision => _gpu?.LeafyGateRevision ?? _boundLeafyGateRevision ??
        NeowFamilyGpuExecutor.RequestedLeafyGateRevision;
    private string PrefixHashRevision => _gpu?.PrefixHashRevision ??
        (!_boundSharedPrefixHashCompactInput ? _boundPrefixHashRevision : null) ??
        NeowFamilyGpuExecutor.RequestedPrefixHashRevision;
    private string StagedDispatchRevision => _gpu?.StagedDispatchRevision ?? _boundStagedDispatchRevision ??
        NeowFamilyGpuExecutor.RequestedStagedDispatchRevision;
    private string WindowTransportRevision => _gpu?.WindowTransportRevision ?? (_boundStagedDispatchRevision is not null
        ? NeowFamilyGpuExecutor.SingleWindowRevision : SupportsDenseWindowBatching
            ? NeowFamilyGpuExecutor.DoubleWindowRevision : NeowFamilyGpuExecutor.SingleWindowRevision);
    internal NeowFamilyGpuPlan? GpuPlan => _composite is null && _transformations is null ? _gpuPlan : null;
    internal NeowReplayPlan PricingReplayPlan => _plan;
    internal NeowCapsuleComposite? PricingComposite => _composite;
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest request)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        bool pairedGeometry = SupportsDenseWindowBatching && !request.CompactInput && !request.PrivateInput && !request.PrivateOutput &&
            request.MeanInputPopulation > NeowFamilyGpuExecutor.Capacity && request.MeanInputPopulation <= NeowFamilyGpuExecutor.DenseWindowBatchCapacity;
        var physicalRequest = pairedGeometry ? request with { MeanInputPopulation = NeowFamilyGpuExecutor.Capacity } : request;
        var quote = _transformations is null
            ? NeowPhysicalPricing.Quote(this, physicalRequest) : NeowTransformationPricing.Joint(_transformations, physicalRequest);
        if (quote is not null && !request.CompactInput && _gpuPlan?.UsesStagedDense == true)
            quote = quote with { WindowCapacity = SupportsDenseWindowBatching && !request.PrivateOutput &&
                    WindowTransportRevision == NeowFamilyGpuExecutor.DoubleWindowRevision ? PreferredDenseWindowSize : quote.WindowCapacity,
                Evidence = quote.Evidence + ";StagedDispatch=" + StagedDispatchRevision + ";WindowTransport=" + WindowTransportRevision +
                ";DispatchRevisionMeasured=false;DispatchReference=LegacyEstimate;NoInheritedAtlasMeasurement" +
                (WindowTransportRevision == NeowFamilyGpuExecutor.SequentialWindowFallbackRevision
                    ? ";OptionalSecondWorkspaceUnavailable;SingleWorkspaceSequential=true" : "") +
                (pairedGeometry ? ";PhysicalWindowPopulation=16777216;LogicalWindowPopulation<=33554432" : "") };
        return quote;
    }
    bool IFamilyInvocation.CanBindPrivateSerial => _gpuPlan is not null && _composite is null && _transformations is null;
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        if (_gpuPlan is null || _composite is not null || _transformations is not null) throw new InvalidOperationException("N.PrivateSerialNotAdmitted");
        var gpu = new NeowFamilyGpuExecutor(rd, _gpuPlan, 42, privateInput: input, privateOutput: output,
            useGroupOutput: _requestGroupOutput, structuralTrials: _structuralTrials);
        _boundHeaderReadbackRevision = gpu.HeaderReadbackRevision;
        _boundLeafyGateRevision = gpu.LeafyGateRevision;
        _boundPrefixHashRevision = gpu.PrefixHashRevision;
        _boundSharedPrefixHashRevision = gpu.SharedPrefixHashRevisionFor(input is not null);
        _boundSharedPrefixHashCompactInput = input is not null;
        _boundStaticDrawGateRevision = gpu.StaticDrawGateRevisionFor(input is not null);
        _boundInternalFilterOrderRevision = gpu.InternalFilterOrderRevisionFor(input is not null);
        _boundInternalFilterOrderObserved = true;
        _boundGroupOutputRevision = gpu.GroupOutputRevisionFor(input is not null);
        _boundGroupOutputObserved = gpu.GroupOutputObservedFor(input is not null);
        _boundStructuralRevision = gpu.StructuralRevisionFor(input is not null);
        _boundStructuralObserved = gpu.StructuralObservedFor(input is not null);
        if (input is null && _gpuPlan.UsesStagedDense) _boundStagedDispatchRevision = gpu.StagedDispatchRevision;
        return new(gpu, gpu.Device, gpu.SetupMs, input, output, (batch, count, token) =>
        {
            FamilyCandidateSet? result = null; NeowFamilyGpuMetrics m;
            if (output is not null) gpu.ExecutePrivateStage(batch, count, token, out m);
            else result = input is null ? gpu.Execute(FamilyCandidateSet.Dense(batch), token, out m) : gpu.ExecutePrivate(batch, count, token, out m);
            _boundHeaderReadbackRevision = gpu.HeaderReadbackRevision;
            _boundLeafyGateRevision = gpu.LeafyGateRevision;
            _boundPrefixHashRevision = gpu.PrefixHashRevision;
            _boundSharedPrefixHashRevision = gpu.SharedPrefixHashRevisionFor(input is not null);
            _boundSharedPrefixHashCompactInput = input is not null;
            _boundStaticDrawGateRevision = gpu.StaticDrawGateRevisionFor(input is not null);
            _boundInternalFilterOrderRevision = gpu.InternalFilterOrderRevisionFor(input is not null);
            _boundInternalFilterOrderObserved = true;
            _boundGroupOutputRevision = gpu.GroupOutputRevisionFor(input is not null);
            _boundGroupOutputObserved = gpu.GroupOutputObservedFor(input is not null);
            _boundStructuralRevision = gpu.StructuralRevisionFor(input is not null);
            _boundStructuralObserved = gpu.StructuralObservedFor(input is not null);
            return new(m.Survivors, result, m.DispatchSyncMs, m.ReadbackMs, m.ReadbackBytes, m.CanonicalMs, m.Dispatches);
        });
    }
    private readonly NeowCapsuleComposite? _composite;
    private NeowFamilyGpuExecutor? _gpu;
    private bool _disabled, _compact, _parity;
    private int _batches, _steady;
    private long _inputs, _outputs, _liveInputs;
    private double _ms, _liveMs, _peak;
    private double _uploadMs, _dispatchMs, _readbackMs, _canonicalizeMs;
    private long _fullBatchInputs, _fullBatchOutputs;
    private long _bonesEntryPass, _bonesPairPass;
    private double _fullBatchDispatchMs, _fullBatchCanonicalMs;
    private long _nrPairPass, _arcanePass, _rarityPass, _heavyEntered;
    private int _zeroSurvivorBatches, _payloadReadbackCount;
    private double _payloadReadbackMs;
    internal NeowFamily(ExactSearchExecutionRequest request, NeowReplayPlan plan, bool preferGpu = true, NeowCapsuleComposite? composite = null,
        NeowTransformationComposite? transformations = null)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _request = request; _plan = plan; _transformations = transformations;
        _requestGroupOutput = NeowFamilyGpuExecutor.RequestGroupOutput;
        _structuralTrials = NeowStructuralTrialOptions.Capture();
        var neowMatcher = NeowFamilyReplay.Bind(plan);
        _cpuMatcher = transformations is null ? neowMatcher : root => neowMatcher(root) && transformations.Numerical.Matches(root);
        string physicalIssue = "CpuReferenceSelected";
        if (preferGpu) NeowFamilyGpuPlan.TryCreate(plan, out _gpuPlan, out physicalIssue);
        if (transformations is not null)
        {
            if (!preferGpu || composite is not null) throw new InvalidOperationException("NJointTransformPhysicalConflict");
            _gpuPlan = transformations.Gpu;
        }
        if (composite is not null && _gpuPlan is null) throw new InvalidOperationException("NrRequiresGpuPhysical");
        _composite = composite;
        _models = new(request, plan, false, null, _gpuPlan is null ? FamilyCpuExecution.Capacity : NeowFamilyGpuExecutor.Capacity);
        if (Search.Semantics.TransformationAggregateCondition.HasSharedNeow(request.Evaluation))
        {
            var numerical = transformations?.Numerical ?? new TransformationAggregateNumericalPlan(request);
            _transformationJoint = plan.ExactOnly.Length == 0 ? TransformationAggregateProbability.SharedNeowJoint(request, numerical) : null;
            _givenTransformations = TransformationAggregateProbability.Conditional(_transformationJoint,
                TransformationAggregateProbability.Build(numerical).StageSurvival);
        }
        _physical = new(request, FamilyId, "N.Neow.Cpu.LocalReplay.20260905.v1");
        _conditional = NeowReplayPlan.HasCapsule(request);
        if (plan.AuthoredUpgrades is { } upgrades)
            RuntimeLog.TryBackgroundInfo($"nAuthoredUpgradeContinuation=true;scope=AuthoredOnly;hiddenWwpReplay=false;constants={string.Join(',', upgrades.Advances)};unknownConstant=-1;unknownRoute={(plan.CapsuleUpgradeUpperBound >= 0 ? "BoundedDrawUnion" : "IdentityOnlyExactRemainder")};choicePolicy=AuthoredTakeOtherwiseSkip");
        RuntimeLog.TryBackgroundInfo($"nFamilyReady=true;coverage={string.Join(',', Coverage)};physical={(_composite is not null ? "NrTargetRankComposite" : _gpuPlan is null ? "CpuLocalReplay" : "GpuLocalDonor")};physicalIssue={physicalIssue};capsuleBagScratch=false;capsuleSemanticOwner=R;boundedBonesNr={_composite?.UsesBonesCheckpoint == true};exactOnly={string.Join(',', plan.ExactOnly)}");
    }
    public string FamilyId => "N.Neow";
    public IReadOnlyList<string> Coverage => _transformations is not null ? [FamilyId, TransformationAggregateFamily.Id] :
        _composite is null ? [FamilyId] : [FamilyId, "R.Relic"];
    public FamilyAnalyticalCostProjection AnalyticalCost => _composite is null ? _models.Full : new(FamilyId,
        NeowFamilyGpuExecutor.Capacity, _models.Full.Terms.Concat(_composite.Projection.Full.Terms),
        "CompositeConservativeFullEnvelope_NPlusR;ExpectedTargetRankReachUnpriced");
    public FamilySurvivalProjection Survival => ResolveSurvival(Empty);
    public bool HasConditionalProjections => _conditional || Search.Semantics.TransformationAggregateCondition.HasSharedNeow(_request.Evaluation);
    public FamilySurvivalProjection ResolveSurvival(IReadOnlySet<string> passedCoverage)
    {
        if (_transformations is not null) return _transformationJoint is { } joint
            ? FamilySurvivalProjection.Resolved(FamilyId, joint, "NJointAggregate;ExistingSameDrawNAndTModel;IdentityOnce")
            : FamilySurvivalProjection.Unresolved(FamilyId, "NJointAggregate.JointModelUnavailable");
        if (Search.Semantics.TransformationAggregateCondition.HasSharedNeow(_request.Evaluation) && passedCoverage.Contains(TransformationAggregateFamily.Id))
            return Search.Semantics.TransformationAggregateCondition.SharedNeowIdentityOnly(_request.Evaluation)
                ? FamilySurvivalProjection.Resolved(FamilyId, 1, "SharedNIdentityAlreadyPassedByT")
                : _givenTransformations is { } conditional
                    ? FamilySurvivalProjection.Resolved(FamilyId, conditional, "N.GivenT;ExistingSameDrawJointOverTMarginal")
                    : FamilySurvivalProjection.Unresolved(FamilyId, "T+N.AdditionalPredicateJointUnknown");
        if (_composite is null) return _models.Survival(passedCoverage);
        double? n = _models.Survival(Empty).SurvivalProbability;
        double? r = _composite.Projection.Survival(new HashSet<string> { FamilyId }).SurvivalProbability;
        return n.HasValue && r.HasValue ? FamilySurvivalProjection.Resolved(FamilyId, n.Value*r.Value, "CompositeCoverage=N.Neow,R.Relic;RConditionalOnN")
            : FamilySurvivalProjection.Unresolved(FamilyId, "CompositeJointSurvivalUnavailable");
    }
    public FamilyExpectedFilteringCostProjection ExpectedFilteringCost => ResolveExpectedFilteringCost(Empty);
    public FamilyExpectedFilteringCostProjection ResolveExpectedFilteringCost(IReadOnlySet<string> passedCoverage) => _transformations is not null
        ? new(FamilyId, _models.Full.WorkUnitsPerInput, null, _transformationJoint, 1, [], "NJointAggregate.NoPrimitiveLedger;UseOwnedPhysicalQuote") : _composite is null
        ? _models.Expected(passedCoverage) : new(FamilyId, _models.Full.WorkUnitsPerInput + _composite.Projection.Full.WorkUnitsPerInput,
            null, ResolveSurvival(passedCoverage).SurvivalProbability, 1, [], "CompositeTargetRankReachLedgerNotYetPriced");
    internal int PreferredExecutionWindowSize => _gpuPlan is null ? FamilyCpuExecution.Capacity : NeowFamilyGpuExecutor.Capacity;
    internal bool SupportsDenseWindowBatching => _gpuPlan is { IsMultiplayer: false, UsesStagedDense: true, UsesLeafyPreGate: true } &&
        _composite is null && _transformations is null && (_request.RunOptions.Resources?.GpuPercent ?? 100) == 100 &&
        _request.RunOptions.Optimizations.DoubleWindowBatching &&
        System.Environment.GetEnvironmentVariable("RT2_NEOW_SINGLE_WINDOW") != "1";
    internal int PreferredDenseWindowSize => SupportsDenseWindowBatching
        ? NeowFamilyGpuExecutor.DenseWindowBatchCapacity : PreferredExecutionWindowSize;
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compactAbi1Input)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        if (_gpuPlan is null) return _physical.Condition(compactAbi1Input);
        string revision = _transformations is not null
            ? NeowTransformationComposite.Revision + (compactAbi1Input ? ".Compact" : _gpuPlan.UsesStagedDense ? ".StagedDense" : ".Dense")
            : _composite is not null
            ? _composite.Revision(compactAbi1Input, _gpuPlan.UsesStagedDense)
            : _gpuPlan.DirectNestedMode != 0 ? _gpuPlan.DirectNestedRevision(compactAbi1Input)
            : _gpuPlan.UsesLeafyPreGate
                ? compactAbi1Input ? NeowFamilyGpuPlan.LeafyCompactRevision :
                    _gpuPlan.UsesStagedDense ? NeowFamilyGpuPlan.LeafyStagedDenseRevision : NeowFamilyGpuPlan.LeafyDenseRevision
                : compactAbi1Input ? NeowFamilyGpuPlan.CompactRevision :
                    _gpuPlan.UsesStagedDense ? NeowFamilyGpuPlan.StagedDenseRevision : NeowFamilyGpuPlan.DenseRevision;
        if (!_gpuPlan.IsMultiplayer) revision += ".PackedPublicOrdinals.20260930.v1";
        if (!_gpuPlan.IsMultiplayer && (_gpuPlan.Meta[82] & 9u) != 0)
            revision += ".ZeroUsedDirectIndex.20260930.v1";
        if (!_gpuPlan.IsMultiplayer && _plan.StructuredConditions.Any(c => c.SourceRelicId == Beta110FastRelicCatalog.Kaleidoscope))
            revision += ".KaleidoscopeFirstGroupReject.20260930.v1";
        if (!compactAbi1Input && _gpuPlan.UsesStagedDense)
            revision += "." + StagedDispatchRevision + "." + WindowTransportRevision;
        revision += "." + HeaderReadbackRevision;
        if (!_gpuPlan.IsMultiplayer && _gpuPlan.UsesLeafyPreGate) revision += "." + LeafyGateRevision;
        if (!compactAbi1Input && !_gpuPlan.IsMultiplayer && _gpuPlan.UsesStagedDense && _composite is null)
            revision += "." + PrefixHashRevision;
        // Original Dense and shared fallback/Fused tables have distinct identities.
        string? sharedPrefixRevision = _gpu is not null
            ? _gpu.SharedPrefixHashRevisionFor(compactAbi1Input)
            : _boundSharedPrefixHashCompactInput == compactAbi1Input ? _boundSharedPrefixHashRevision : null;
        if (sharedPrefixRevision is { } sharedRevision)
            revision += "." + sharedRevision;
        revision += "." + (_gpu?.StaticDrawGateRevisionFor(compactAbi1Input) ??
            (_boundSharedPrefixHashCompactInput == compactAbi1Input ? _boundStaticDrawGateRevision : null) ??
            NeowFamilyGpuExecutor.RequestedStaticDrawGateRevision);
        if (!compactAbi1Input && _composite is null && _transformations is null)
        {
            string? internalOrderRevision = _gpu is not null ? _gpu.InternalFilterOrderRevisionFor(false) :
                _boundInternalFilterOrderObserved && !_boundSharedPrefixHashCompactInput ? _boundInternalFilterOrderRevision :
                NeowFamilyGpuExecutor.RequestedInternalFilterOrderRevisionFor(_gpuPlan, false);
            if (internalOrderRevision is not null) revision += "." + internalOrderRevision;
        }
        if (!compactAbi1Input && _composite is null && _transformations is null)
        {
            string? groupOutputRevision = _gpu is not null && _gpu.GroupOutputObservedFor(false)
                ? _gpu.GroupOutputRevisionFor(false)
                : _boundGroupOutputObserved && !_boundSharedPrefixHashCompactInput ? _boundGroupOutputRevision
                : NeowFamilyGpuExecutor.RequestedGroupOutputRevisionFor(_gpuPlan, false, _requestGroupOutput);
            if (groupOutputRevision is not null) revision += "." + groupOutputRevision;
        }
        if (_composite is null && _transformations is null)
        {
            string? structuralRevision = _gpu is not null && _gpu.StructuralObservedFor(compactAbi1Input)
                ? _gpu.StructuralRevisionFor(compactAbi1Input)
                : _boundStructuralObserved && _boundSharedPrefixHashCompactInput == compactAbi1Input
                    ? _boundStructuralRevision
                    : NeowFamilyGpuExecutor.RequestedStructuralRevisionFor(_gpuPlan, compactAbi1Input, _structuralTrials);
            if (structuralRevision is not null) revision += "." + structuralRevision;
        }
        return new(FamilyId, revision, "N.Neutral.20260905.v1", 1, "NoAcceptedWithinPathCurve");
    }
    public FamilyPerformanceObservation CapturePerformanceObservation() => _gpuPlan is null ? _physical.Observation() :
        new(ResolveConditionPerformance(_compact), _gpu?.Device ?? "not-created", _gpu?.SetupMs ?? 0, _peak, _peak,
            _batches, _steady, _inputs, _outputs, _ms, !_disabled && _parity && _steady > 0,
            _disabled ? "PhysicalFailureOrRecovery" : "CanonicalAbi1Ready;FirstBatchExcluded;CpuReferenceParity=" + _parity);
    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot() => _gpuPlan is null ? _physical.Live() :
        _disabled || _batches < 2 ? null : new(FamilyId, ResolveConditionPerformance(_compact).PhysicalImplementationRevision,
            Interlocked.Read(ref _liveInputs), Volatile.Read(ref _liveMs), _batches - 1);
    public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext executionContext, FamilyObservationWindow observationWindow,
        FamilyCandidateSet input, CancellationToken cancellationToken)
    {
        if (observationWindow.ExactRequest.SnapshotFingerprint != _request.SnapshotFingerprint)
            throw new InvalidOperationException("NFamilyObservationContextMismatch");
        cancellationToken.ThrowIfCancellationRequested();
        if (_gpuPlan is null) return _physical.Execute(observationWindow, input, cancellationToken, _cpuMatcher);
        if (_disabled && _composite?.UsesBonesCheckpoint == true)
            throw new InvalidOperationException("NrBonesPhysicalFaulted");
        if (_disabled) throw new InvalidOperationException("N.SelectedInvocationFaulted");
        if (input.Count == 0) return input;
        _compact = !input.IsDense;
        try
        {
            if (_gpu is null) _gpu = await executionContext.ExecuteGpuAsync(rd => new NeowFamilyGpuExecutor(rd, _gpuPlan,
                (uint)RolltheSpire2.Core.Seed.XxHash64.HashUtf8(_request.SnapshotFingerprint, 0), _composite,
                useGroupOutput: _requestGroupOutput, structuralTrials: _structuralTrials), cancellationToken).ConfigureAwait(false);
            var result = await executionContext.ExecuteGpuAsync(_ => {
                NeowFamilyGpuMetrics metrics;
                var survivors = SupportsDenseWindowBatching && input.IsDense && input.Count > NeowFamilyGpuExecutor.Capacity
                    ? _gpu.ExecuteDenseWindows(input, cancellationToken, out metrics)
                    : _gpu.Execute(input, cancellationToken, out metrics);
                return (survivors, metrics);
            }, cancellationToken).ConfigureAwait(false);
            if (!_parity)
            {
                var profile = RuntimeProfileRegistry.Select(_request.Detection);
                ulong[] survivors = result.survivors.ExportAbi1().ToArray();
                foreach (ulong ordinal in input.EnumerateLogicalOrdinals().Take(256))
                {
                    ulong root = profile.ComputeRootSeed(VisibleSeedCandidateCodec.FormatOrdinal(profile, input.Batch.GlobalCandidate(ordinal)));
                    if ((_cpuMatcher(root) && (_composite?.Reference.Matches(root) ?? true)) != (Array.BinarySearch(survivors, ordinal) >= 0))
                        throw new InvalidDataException("NFamilyCpuReferenceParityMismatch");
                }
                _parity = true;
            }
            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, result.metrics.DispatchSyncMs, outputs: result.survivors.Count, canonicalMs: result.metrics.CanonicalMs, readbackMs: result.metrics.ReadbackMs, readbackBytes: result.metrics.ReadbackBytes);
            _batches++; _inputs += input.Count; _outputs += result.survivors.Count; _ms += result.metrics.CanonicalMs;
            _bonesEntryPass += result.metrics.CurseSurvivors; _bonesPairPass += result.metrics.PairSurvivors;
            _uploadMs += result.metrics.UploadMs; _dispatchMs += result.metrics.DispatchSyncMs;
            _readbackMs += result.metrics.ReadbackMs; _canonicalizeMs += result.metrics.CanonicalizeMs;
            if (input.Count == (_compact ? NeowFamilyGpuExecutor.Capacity : PreferredDenseWindowSize))
            {
                _fullBatchInputs += input.Count; _fullBatchOutputs += result.survivors.Count;
                _fullBatchDispatchMs += result.metrics.DispatchSyncMs; _fullBatchCanonicalMs += result.metrics.CanonicalMs;
            }
            if (_composite?.UsesBonesArcane == true)
            {
                _nrPairPass += result.metrics.NrPairPass; _arcanePass += result.metrics.ArcanePass;
                _rarityPass += result.metrics.CapsuleRarityPass; _heavyEntered += result.metrics.HeavyEntered;
                if (result.survivors.Count == 0) _zeroSurvivorBatches++; else _payloadReadbackCount++;
                _payloadReadbackMs += result.metrics.PayloadReadbackMs;
                if (RolltheSpire2.Bootstrap.RuntimeLog.DetailEnabled)
                    RuntimeLog.TryBackgroundDetail($"boundedBonesCapsuleArcaneNr=True;batchBase={input.Batch.BatchBase};pairPass={result.metrics.NrPairPass};arcanePass={result.metrics.ArcanePass};capsuleRarityPass={result.metrics.CapsuleRarityPass};heavyEntered={result.metrics.HeavyEntered};nrOutput={result.survivors.Count};zeroSurvivorBatches={_zeroSurvivorBatches};payloadReadbackCount={_payloadReadbackCount};payloadReadbackMs={result.metrics.PayloadReadbackMs}");
            }
            if (_batches > 1)
            {
                Interlocked.Add(ref _liveInputs, input.Count); _liveMs += result.metrics.CanonicalMs;
                if (input.Count >= (_compact ? NeowFamilyGpuExecutor.Capacity / 4 : PreferredDenseWindowSize))
                { _steady++; _peak = Math.Max(_peak, input.Count * 1000d / result.metrics.CanonicalMs); }
            }
            if (RolltheSpire2.Bootstrap.RuntimeLog.DetailEnabled)
                RuntimeLog.TryBackgroundDetail($"nFamilyBatch=true;physicalRevision={ResolveConditionPerformance(_compact).PhysicalImplementationRevision};batchBase={input.Batch.BatchBase};input={input.Count};processed={result.metrics.Processed};survivors={result.survivors.Count};groups={result.metrics.Groups};dispatches={result.metrics.Dispatches};curseSurvivors={result.metrics.CurseSurvivors};pairSurvivors={result.metrics.PairSurvivors};uploadMs={result.metrics.UploadMs};dispatchSyncMs={result.metrics.DispatchSyncMs};readbackMs={result.metrics.ReadbackMs};readbackBytes={result.metrics.ReadbackBytes};canonicalizeMs={result.metrics.CanonicalizeMs};canonicalAbi1ReadyMs={result.metrics.CanonicalMs};firstDrawAcceptMask={_gpuPlan.Meta[68]};firstDrawRejectMask={_gpuPlan.Meta[69]};trackedBones={_gpuPlan.Meta[67]};boundedBonesNr={_composite?.UsesBonesCheckpoint == true};leafyPreGate={_gpuPlan.UsesLeafyPreGate};parity={_parity};workspaceBytes={_gpu.ResidentBytes};scratchBytes=0;ownerThread={executionContext.GpuOwnerThreadId}");
            return result.survivors;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _disabled = true;
            RuntimeLog.TryBackgroundWarning($"nFamilyPhysicalFault=true;reason={ex.Message};recovery=None");
            throw;
        }
    }
    public async ValueTask DisposeAsync(FamilyExecutionContext context)
    {
        if (_gpuPlan is null) { _physical.WriteSummary(); return; }
        if (_gpu is not null) await context.ExecuteGpuAsync(_ => { _gpu.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false);
        if (_composite?.UsesBonesArcane == true)
            RuntimeLog.TryBackgroundInfo($"boundedBonesCapsuleArcaneNr=True;summary=True;batches={_batches};pairPass={_nrPairPass};arcanePass={_arcanePass};capsuleRarityPass={_rarityPass};heavyEntered={_heavyEntered};nrOutput={_outputs};zeroSurvivorBatches={_zeroSurvivorBatches};payloadReadbackCount={_payloadReadbackCount};payloadReadbackMs={_payloadReadbackMs}");
        RuntimeLog.TryBackgroundInfo($"nFamilySummary=true;gpu={_gpuPlan is not null};batches={_batches};input={_inputs};survivors={_outputs};canonicalMs={_ms};parity={_parity};recovery=False;faulted={_disabled};" +
            $"uploadMs={_uploadMs};dispatchSyncMs={_dispatchMs};readbackMs={_readbackMs};canonicalizeMs={_canonicalizeMs};" +
            $"fullBatchInputs={_fullBatchInputs};fullBatchOutputs={_fullBatchOutputs};fullBatchDispatchSyncMs={_fullBatchDispatchMs};fullBatchCanonicalMs={_fullBatchCanonicalMs};partialBatchInputs={_inputs-_fullBatchInputs};constructorSetupIncluded=false;firstBatchIncluded=true;" +
            $"bonesEntryPass={_bonesEntryPass};bonesPairPass={_bonesPairPass};bonesCheckpointCounters=StagedOnly");
    }
    internal static bool TryCreate(ExactSearchExecutionRequest request, out IFamilyInvocation? family)
    {
        family = null;
        if (!request.Evaluation.HasNeowConstraints) return false;
        try
        {
            var plan = NeowReplayPlan.Compile(request, false);
            family = new NeowFamily(request, plan);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.TryBackgroundWarning("nFamilyUnavailable=true;recovery=ExactOnly;legacyFallback=false;reason=" + ex.Message);
            return false;
        }
    }
}
