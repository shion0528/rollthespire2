using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed class EwPrivateInvocation(ExactSearchExecutionRequest request, EventResultFamilyPlan e, WorldFamilyGpuPlan w, string mode) : IFamilyInvocation
{
    private readonly FamilyHeaderTransportPerformance _headerTransport = new(request.RunOptions.Optimizations);
    private EwPrivateGpuExecutor? _gpu;
    private long _input, _output;
    private int _batches;
    private double _ms;
    private bool _faulted;
    public string FamilyId => "E.EventResult";
    public IReadOnlyList<string> Coverage => ["E.EventResult","W.World"];
    public FamilyAnalyticalCostProjection AnalyticalCost => new(FamilyId,EwPrivateGpuExecutor.MaxRoots,[],"EW.UnpricedEquivalentCoverage");
    public FamilySurvivalProjection Survival => FamilySurvivalProjection.Unresolved(FamilyId,"EW.JointUnpriced");
    public FamilyConditionPerformanceProjection ConditionPerformance
    {
        get
        {
            using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
            return _headerTransport.Apply(new(FamilyId,
                "E.EventResult.Composite.W.World.PrivateOrdinal.CanonicalAbi1Ready.20260908.v2" + SharedVisibleSeedGpuCache.RevisionSuffix,
                "EW.Neutral",1,"EW.UnpricedCandidate",usesGpu:true), _gpu?.HeaderReadbackRevision);
        }
    }
    public FamilyPerformanceObservation CapturePerformanceObservation() => new(ConditionPerformance,_gpu?.Device??"",_gpu?.SetupMs??0,
        0,0,_batches,0,_input,_output,_ms,false,_faulted?"PhysicalFailureOrRecovery":"EW.CandidateNoCalibration");
    public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow observation, FamilyCandidateSet input, CancellationToken token)
    {
        if (_faulted || request.SnapshotFingerprint != observation.ExactRequest.SnapshotFingerprint) throw new InvalidOperationException("EW.InvocationInvariant");
        try
        {
            _gpu ??= await context.ExecuteGpuAsync(rd=>new EwPrivateGpuExecutor(rd,e,w,mode.StartsWith("verify-",StringComparison.Ordinal)),token).ConfigureAwait(false);
            var result = await context.ExecuteGpuAsync(_=> { var output=_gpu.Execute(input,token,out var ms); return (output,ms); },token).ConfigureAwait(false);
            _batches++; _input+=input.Count; _output+=result.output.Count; _ms+=result.ms;
            return result.output;
        }
        catch (OperationCanceledException) { throw; }
        catch { _faulted=true; throw; }
    }
    public async ValueTask DisposeAsync(FamilyExecutionContext context)
    {
        if (_gpu is not null) await context.ExecuteGpuAsync(_=>{_gpu.Dispose();return true;},CancellationToken.None).ConfigureAwait(false);
        Bootstrap.RuntimeLog.TryBackgroundInfo($"ewPrivateSummary=true;mode={mode};batches={_batches};input={_input};output={_output};canonicalMs={_ms};faulted={_faulted};recovery=None");
    }
}
