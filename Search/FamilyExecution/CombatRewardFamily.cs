using RolltheSpire2.Bootstrap;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.FamilyExecution;

// One continuous ordinary-combat observation, independently replayed from root.
// Authored Neow mechanics are prerequisites, never N Coverage or opening truth.
// Capsule latent obtains follow the authored-only policy. Multiplayer Bones
// recovers unspecified companions from the real root-local offered-pair shuffle.
internal sealed partial class CombatRewardFamily : IFamilyInvocation
{
    private readonly GpuCostSamples _costSamples = new();
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks => _costSamples.Peaks;
    void IFamilyInvocation.LogExecutionEvidence() => _costSamples.WriteSummary();
    private readonly FamilyCpuExecution _cpu;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    private readonly ExactSearchExecutionRequest _request;
    private readonly CombatRewardGpuPlan? _gpuPlan;
    internal CombatRewardGpuPlan? GpuPlan => _gpuPlan;
    private readonly CombatRewardFamilyModels _models;
    private static readonly IReadOnlySet<string> EmptyCoverage = new HashSet<string>();
    private CombatRewardGpuExecutor? _gpu;
    private bool _compact, _parity, _faulted;
    private int _batches;
    private long _inputs, _outputs, _liveInputs;
    private double _canonicalMs, _liveMs, _peak;
    internal CombatRewardReplay Replay { get; }

    private CombatRewardFamily(ExactSearchExecutionRequest request, int? precedingNicheDraws = 0)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _headerTransport = new(request.RunOptions.Optimizations);
        _request = request;
        Replay = new(request, precedingNicheDraws);
        _gpuPlan = CombatRewardGpuPlan.TryCreate(request, Replay, out string gpuIssue);
        _models = new(request, Replay, _gpuPlan is null ? FamilyCpuExecution.Capacity : CombatRewardGpuExecutor.Capacity);
        _cpu = new(request, FamilyId, "C.CombatReward.Cpu.AuthoredPrefix.20260907.v1");
        RuntimeLog.TryBackgroundInfo($"combatRewardFamilyReady=true;family={FamilyId};" +
            $"bonesPrefix={Replay.Plan.OpeningConsumption.ReplayBonesOffer};authoredChildren={Replay.Plan.OpeningConsumption.OrderedRelicIds.Length};" +
            $"prefixRoutes={Replay.RouteCount};precedingNicheDraws={Replay.PrecedingNicheDraws};maximumBattle={Replay.Plan.MaximumBattleOrdinal};" +
            $"continuationIssue={Replay.ConservativeOpeningReason};kaleidoscopeCountOnly={Replay.KaleidoscopeCountOnly};" +
            $"effectiveSieve={!Replay.ConservativelyKeeps};exactRemainder={Replay.ConservativelyKeeps};" +
            $"actualBonesPair={Replay.ReplayActualBonesPair};" +
            $"actualCapsuleHeld={Replay.CapsuleHeldReplay};unhandledCapsuleHeld=PerRootConservativeKeep;" +
            $"explicitImpacts={string.Join(',', Replay.Plan.ExplicitContext.SourceRelicKeys)};" +
            $"physical={(_gpuPlan is null ? "CPU" : _gpuPlan.UsesHotLoop ? "GpuNeutralHotLoop" : "GpuGenericStreaming")};physicalIssue={gpuIssue};" +
            $"model={(request.Authority.PlayersCount > 1 ? "PartyAuthoredOrNeutralContinuation" : "AuthoredPlusNeutralBestEffort")};unordered=IndependentExistential;survival={(Survival.IsResolved ? "Available" : "Unknown")};expectedCost=FullEnvelope;recovery=None");
    }

    public string FamilyId => "C.CombatReward";
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest request)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return CombatRewardPhysicalPricing.Quote(_request, Replay, _gpuPlan, request);
    }
    bool IFamilyInvocation.CanBindPrivateSerial => _gpuPlan is not null;
    FamilyPhysicalQuote? IFamilyInvocation.QuoteCommonPrivateWorkCancellation(FamilyPhysicalQuoteRequest g) =>
        _gpuPlan is null || !FamilyPhysicalQuote.AdmittedRequest(g) || !FamilyPhysicalQuote.HasReferenceBackend() ? null :
        new(FamilyId, "C.SameCompiled" + (_gpuPlan.UsesHotLoop ? "HotLoop" : "GenericStreaming"), 0,
            CombatRewardGpuExecutor.Capacity, _gpuPlan.UsesHotLoop ? 120 : 1000,
            "ComparisonOnly;UnknownNumericalCancelled;SameRoutesMetaAndAppend32;SameBodyReferenceSetup;PublicReachAppliedByAllocation");
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output)
    {
        if (_gpuPlan is null) throw new InvalidOperationException("C.PrivateSerialNotAdmitted");
        var gpu = new CombatRewardGpuExecutor(rd, _gpuPlan, privateInput: input, privateOutput: output);
        _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
        return new(gpu, gpu.Device, gpu.SetupMs, input, output, (batch, count, token) =>
        {
            FamilyCandidateSet? result = null; CombatRewardGpuMetrics m;
            if (output is not null)
            {
                if (input is null) gpu.ExecutePrivate(FamilyCandidateSet.Dense(batch), token, out m);
                else gpu.ExecutePrivateStage(batch, count, token, out m);
            }
            else result = input is null ? gpu.Execute(FamilyCandidateSet.Dense(batch), token, out m) : gpu.ExecutePrivateInput(batch, count, token, out m);
            _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
            return new(m.Output, result, m.DispatchSyncMs, m.ReadbackMs, m.ReadbackBytes, m.CanonicalMs, m.Dispatches);
        });
    }
    public FamilyAnalyticalCostProjection AnalyticalCost => _models.Analytical;
    public FamilySurvivalProjection Survival => _models.Survival;
    public bool HasConditionalProjections => true;
    public FamilySurvivalProjection ResolveSurvival(IReadOnlySet<string> passedCoverage) => _models.ResolveSurvival(passedCoverage);
    public FamilyExpectedFilteringCostProjection ExpectedFilteringCost => _models.Expected(EmptyCoverage);
    public FamilyExpectedFilteringCostProjection ResolveExpectedFilteringCost(IReadOnlySet<string> passedCoverage) => _models.Expected(passedCoverage);
    public FamilyConditionPerformanceProjection ConditionPerformance => ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compactAbi1Input)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return _gpuPlan is null ? _cpu.Condition(compactAbi1Input) : _headerTransport.Apply(
            new(FamilyId,_gpuPlan.Revision(compactAbi1Input), "C.Neutral.20260907.v1",1,
                "NoAcceptedWithinPathCurve;CanonicalAbi1Ready",usesGpu:true), _gpu?.HeaderReadbackRevision);
    }
    public FamilyPerformanceObservation CapturePerformanceObservation() => _gpuPlan is null ? _cpu.Observation() :
        new(ResolveConditionPerformance(_compact),_gpu?.Device??"",_gpu?.SetupMs??0,_peak,_peak,_batches,Math.Max(0,_batches-1),
            _inputs,_outputs,_canonicalMs,_parity&&!_faulted&&_batches>1,"CanonicalAbi1Ready;FirstBatchExcluded;CpuReferenceParity="+_parity);
    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot() => _gpuPlan is null ? _cpu.Live() :
        _faulted||!_parity||_batches<2?null:new(FamilyId,ResolveConditionPerformance(_compact).PhysicalImplementationRevision,Interlocked.Read(ref _liveInputs),Volatile.Read(ref _liveMs),_batches-1);
    public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext executionContext,
        FamilyObservationWindow observationWindow, FamilyCandidateSet input, CancellationToken cancellationToken)
    {
        if(_gpuPlan is null)return _cpu.Execute(observationWindow,input,cancellationToken,Replay.Matches);
        cancellationToken.ThrowIfCancellationRequested();
        if(_faulted)throw new InvalidOperationException("C.GpuInvocationFaulted");
        if(observationWindow.ExactRequest.SnapshotFingerprint!=_request.SnapshotFingerprint)throw new InvalidOperationException("C.ObservationMismatch");
        if(input.Count==0)return input;
        _compact=!input.IsDense;
        try
        {
            _gpu??=await executionContext.ExecuteGpuAsync(rd=>new CombatRewardGpuExecutor(rd,_gpuPlan),cancellationToken).ConfigureAwait(false);
            if(!_parity)
            {
                // Separate tiny GPU dispatch, compared in full before admitting the
                // large batch. Parity cost is not physical performance evidence.
                var sample=FamilyCandidateSet.FromSortedAbi1(input.Batch,input.EnumerateLogicalOrdinals().Take(4096).ToArray());
                var actual=await executionContext.ExecuteGpuAsync(rd=>_gpu.Execute(sample,cancellationToken,out _),cancellationToken).ConfigureAwait(false);
                var profile=RolltheSpire2.Compatibility.RuntimeProfileRegistry.Select(_request.Detection);
                var expected=sample.EnumerateLogicalOrdinals().Where(i=>Replay.Matches(profile.ComputeRootSeed(
                    VisibleSeedCandidateCodec.FormatOrdinal(profile,input.Batch.GlobalCandidate(i))))).ToArray();
                var actualOrdinals = actual.EnumerateLogicalOrdinals().ToArray();
                if(!actualOrdinals.SequenceEqual(expected))throw new InvalidDataException(
                    $"C.GpuCpuParityMismatch:batchBase={input.Batch.BatchBase};slot={_request.Authority.PlayerSlotIndex};" +
                    $"cpu={expected.Length};gpu={actualOrdinals.Length};cpuOnly={string.Join(',',expected.Except(actualOrdinals).Take(8))};" +
                    $"gpuOnly={string.Join(',',actualOrdinals.Except(expected).Take(8))}");
                _parity=true;
            }
            var result=await executionContext.ExecuteGpuAsync(_=>{
                var output=_gpu.Execute(input,cancellationToken,out var metrics);return (output,metrics);
            },cancellationToken).ConfigureAwait(false);
            _costSamples.Observe(this, new(!input.IsDense, false, input.Count), input.Count, result.metrics.DispatchSyncMs, outputs: result.output.Count, canonicalMs: result.metrics.CanonicalMs, readbackMs: result.metrics.ReadbackMs, readbackBytes: result.metrics.ReadbackBytes);
            var m=result.metrics;_batches++;_inputs+=m.Input;_outputs+=m.Output;_canonicalMs+=m.CanonicalMs;
            if(_batches>1){Interlocked.Add(ref _liveInputs,m.Input);_liveMs+=m.CanonicalMs;_peak=Math.Max(_peak,m.Input*1000d/m.CanonicalMs);}
            if (RolltheSpire2.Bootstrap.RuntimeLog.DetailEnabled)RuntimeLog.TryBackgroundDetail($"combatRewardGpuBatch=true;physicalRevision={_gpuPlan.Revision(_compact)};"+
                $"input={m.Input};output={m.Output};dispatches={m.Dispatches};uploadMs={m.UploadMs};dispatchSyncMs={m.DispatchSyncMs};readbackMs={m.ReadbackMs};"+
                $"readbackBytes={m.ReadbackBytes};canonicalAbi1ReadyMs={m.CanonicalMs};parity={_parity};recovery=False");
            return result.output;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex){_faulted=true;RuntimeLog.TryBackgroundWarning("combatRewardGpuFault=true;recovery=None;error="+ex.Message);throw;}
    }
    public async ValueTask DisposeAsync(FamilyExecutionContext executionContext)
    {
        if(_gpuPlan is null){_cpu.WriteSummary();return;}
        RuntimeLog.TryBackgroundInfo($"combatRewardGpuSummary=true;batches={_batches};input={_inputs};output={_outputs};canonicalMs={_canonicalMs};parity={_parity};faulted={_faulted};recovery=False");
        if(_gpu is not null)await executionContext.ExecuteGpuAsync(_=>{_gpu.Dispose();return true;},CancellationToken.None).ConfigureAwait(false);
    }

    internal static bool TryCreate(ExactSearchExecutionRequest request, out IFamilyInvocation? family)
    {
        family = null;
        // A personal multiplayer request must carry its shared arrival explicitly.
        if (request.Authority.PlayersCount != 1) return false;
        if (!request.Evaluation.NormalCombatRewardConditions.Any(c => !c.IsEmpty)) return false;
        family = new CombatRewardFamily(request);
        return true;
    }

    internal static bool TryCreateParty(ExactSearchExecutionRequest request,
        int? precedingNicheDraws, int? precedingPotionDraws, out IFamilyInvocation? family)
    {
        family = null;
        if (request.ProfileId != Compatibility.RuntimeProfileId.Beta111 ||
            !request.Authority.CanUseCurrentModel || request.Authority.PlayersCount is < 2 or > 4 ||
            request.Authority.PlayerSlotIndex < 0 || request.Authority.PlayerSlotIndex >= request.Authority.PlayersCount ||
            !request.Evaluation.NormalCombatRewardConditions.Any(c => !c.IsEmpty))
            return false;
        if (request.CompiledSearch.NormalizedQuery.OpeningRoute is { } route &&
            (!route.IsValid || !Beta110FastRelicCatalog.TryGetId(route.RouteRelicKey, out _))) return false;
        // Combat potion rewards use personal Rewards, never the shared opening
        // CombatPotionGeneration stream. Unknown Niche remains explicitly unknown.
        family = new CombatRewardFamily(request, precedingNicheDraws);
        return true;
    }
}
