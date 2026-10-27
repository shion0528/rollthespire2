using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Core.Seed;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed class TransformationAggregateFamily : IFamilyInvocation
{
    internal const string Id="T.TransformationAggregate";
    internal const string CpuRevision="T.Cpu.InitialBasicsAggregate.20260922.v2";
    private readonly ExactSearchExecutionRequest _request;
    internal TransformationAggregateNumericalPlan Plan { get; }
    internal TransformationAggregateGpuPlan GpuPlan { get; }
    internal TransformationAggregateProbability Probability { get; }
    private readonly double? _givenNeow;
    private readonly FamilyCpuExecution _cpu;
    private readonly FamilyHeaderTransportPerformance _headerTransport;
    private TransformationAggregateGpuExecutor? _gpu;
    private bool _parity,_faulted,_compact;
    private readonly GpuCostSamples _costSamples=new();
    private int _batches,_comparable; private long _inputs,_outputs,_liveInputs;
    private double _canonical,_liveMs,_peak,_validPeak,_dispatch,_readback;
    internal TransformationAggregateFamily(ExactSearchExecutionRequest request)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        _headerTransport = new(request.RunOptions.Optimizations);
        _request=request;Plan=new(request);GpuPlan=new(Plan);Probability=TransformationAggregateProbability.Build(Plan);
        if (Plan.Neow is not null && HasConditionalProjections)
            _givenNeow = TransformationAggregateProbability.Conditional(
                TransformationAggregateProbability.SharedNeowJoint(request, Plan),
                new NeowFamilyProjections(request, NeowReplayPlan.Compile(request, false), false, null).Survival(new HashSet<string>()).SurvivalProbability);
        _cpu=new(request,this,CpuRevision,Plan.Matches,1,g=>TransformationAggregatePricing.Cpu(Plan,Probability,g));
    }
    public string FamilyId=>Id;
    public FamilySurvivalProjection Survival=>Probability.StageSurvival is { } p
        ? FamilySurvivalProjection.Resolved(Id,p,Probability.Evidence):FamilySurvivalProjection.Unresolved(Id,Probability.Evidence);
    public bool HasConditionalProjections => Search.Semantics.TransformationAggregateCondition.HasSharedNeow(_request.Evaluation);
    public FamilySurvivalProjection ResolveSurvival(IReadOnlySet<string> passed) => !HasConditionalProjections || !passed.Contains("N.Neow") ? Survival :
        Search.Semantics.TransformationAggregateCondition.SharedNeowIdentityOnly(_request.Evaluation) && Probability.StageSurvival is { } p
            ? FamilySurvivalProjection.Resolved(Id, Probability.IdentityProbability > 0 ? p / Probability.IdentityProbability : 0, "T.GivenSharedNIdentity;CountOpeningOnce")
            : _givenNeow is { } joint ? FamilySurvivalProjection.Resolved(Id, joint, "T.GivenN;ExistingSameDrawJointOverNMarginal")
            : FamilySurvivalProjection.Unresolved(Id, "T+N.AdditionalPredicateJointUnknown");
    public FamilyAnalyticalCostProjection AnalyticalCost=>new(Id,GpuPlan.Capacity,[],"T.OwnedNumericalWork");
    public FamilyPhysicalQuote? QuotePhysicalWork(FamilyPhysicalQuoteRequest g)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return GpuPlan.Supported ? TransformationAggregatePricing.Gpu(Plan,Probability,GpuPlan,g):TransformationAggregatePricing.Cpu(Plan,Probability,g);
    }
    public FamilyConditionPerformanceProjection ConditionPerformance=>ResolveConditionPerformance(false);
    public FamilyConditionPerformanceProjection ResolveConditionPerformance(bool compact)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        return !GpuPlan.Supported?_cpu.Condition(compact):_headerTransport.Apply(
            new(Id,(GpuPlan.BonesOptimized ? "T.Gpu.BonesTracked."+(GpuPlan.FullTarget?"TransformFirst.Carry8.AllRequired":"OpeningFirst.Generic")+".20261001.v1" :
                GpuPlan.FullTarget?TransformationAggregateGpuPlan.FullTargetRevision:TransformationAggregateGpuPlan.GenericRevision)+"."+(compact?"CompactAbi1":"Dense")+".CanonicalAbi1Ready"+SharedVisibleSeedGpuCache.RevisionSuffix,
                "T.Gpu.Neutral.20260914.v1",1,"FamilyOwned;LocalAggregate;NoLeafPredicate",usesGpu:true),_gpu?.HeaderReadbackRevision);
    }
    IEnumerable<IFamilyInvocation> IFamilyInvocation.CpuRealizations=>[_cpu];
    IEnumerable<GpuLocalPeak> IFamilyInvocation.CostPeaks=>_costSamples.Peaks;
    bool IFamilyInvocation.CanBindPrivateSerial=>GpuPlan.Supported;
    FamilyPrivateGpuExecution IFamilyInvocation.BindPrivateSerial(Godot.RenderingDevice rd,PrivateOrdinalBuffer? input,PrivateOrdinalBuffer? output)
    {
        if(!GpuPlan.Supported)throw new InvalidOperationException("T.PrivateNotAdmitted");
        var gpu=new TransformationAggregateGpuExecutor(rd,GpuPlan,input,output);
        _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
        return new(gpu,gpu.Device,gpu.SetupMs,input,output,(batch,count,token)=>
        {
            FamilyCandidateSet? result=null;TransformationAggregateGpuMetrics m;
            if(output is not null)gpu.ExecutePrivateStage(batch,count,token,out m);
            else result=input is null?gpu.Execute(FamilyCandidateSet.Dense(batch),token,out m):gpu.ExecutePrivate(batch,count,token,out m);
            _headerTransport.RevisionFor(gpu.HeaderReadbackRevision);
            return new(m.Output,result,m.DispatchSyncMs,m.ReadbackMs,m.ReadbackBytes,m.CanonicalMs,m.Dispatches);
        });
    }
    public FamilyPerformanceObservation CapturePerformanceObservation()=>!GpuPlan.Supported?_cpu.Observation():
        new(ResolveConditionPerformance(_compact),_gpu?.Device??"",_gpu?.SetupMs??0,_peak,_validPeak,_batches,_comparable,
            _inputs,_outputs,_canonical,_parity&&!_faulted&&_comparable>0,"T.CpuParity="+_parity+";Fault="+_faulted);
    public FamilyLivePerformanceSnapshot? CaptureLivePerformanceSnapshot()=>!GpuPlan.Supported?_cpu.Live():
        _faulted||_batches<2?null:new(Id,ResolveConditionPerformance(_compact).PhysicalImplementationRevision,_liveInputs,_liveMs,_batches-1);
    public async ValueTask<FamilyCandidateSet> InvokeAsync(FamilyExecutionContext context,FamilyObservationWindow window,FamilyCandidateSet input,CancellationToken token)
    {
        if(!GpuPlan.Supported)return _cpu.Execute(window,input,token,Plan.Matches);
        token.ThrowIfCancellationRequested(); if(_faulted)throw new InvalidOperationException("T.InvocationFaulted");
        if(window.ExactRequest.SnapshotFingerprint!=_request.SnapshotFingerprint)throw new InvalidOperationException("T.ContextMismatch");
        if(input.Count==0)return input;_compact=!input.IsDense;
        try
        {
            _gpu??=await context.ExecuteGpuAsync(rd=>new TransformationAggregateGpuExecutor(rd,GpuPlan),token).ConfigureAwait(false);
            if(!_parity)
            {
                var sample=FamilyCandidateSet.FromSortedAbi1(input.Batch,input.EnumerateLogicalOrdinals().Take(2048).ToArray());
                var actual=await context.ExecuteGpuAsync(rd=>_gpu.Execute(sample,token,out _),token).ConfigureAwait(false);
                var expected=sample.EnumerateLogicalOrdinals().Where(i=>Plan.Matches(XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(input.Batch.GlobalCandidate(i)),0)));
                if(!actual.EnumerateLogicalOrdinals().SequenceEqual(expected))throw new InvalidDataException("T.GpuCpuParityMismatch");
                _parity=true;
            }
            var value=await context.ExecuteGpuAsync(_=>{var r=_gpu.Execute(input,token,out var m);return(Result:r,Metrics:m);},token).ConfigureAwait(false);
            var m=value.Metrics;_batches++;_inputs+=m.Input;_outputs+=m.Output;_canonical+=m.CanonicalMs;_dispatch+=m.DispatchSyncMs;_readback+=m.ReadbackMs;
            if(_batches>1){_liveInputs+=m.Input;_liveMs+=m.CanonicalMs;double rate=m.Input*1000d/m.CanonicalMs;_peak=Math.Max(_peak,rate);
                if(m.Input>=GpuPlan.Capacity){_comparable++;_validPeak=Math.Max(_validPeak,rate);}}
            _costSamples.Observe(this,new(_compact,false,m.Input),m.Input,m.DispatchSyncMs,outputs:m.Output,canonicalMs:m.CanonicalMs,readbackMs:m.ReadbackMs,readbackBytes:m.ReadbackBytes);
            return value.Result;
        }
        catch(OperationCanceledException){throw;}
        catch{_faulted=true;throw;}
    }
    public void LogExecutionEvidence()=>_costSamples.WriteSummary();
    public async ValueTask DisposeAsync(FamilyExecutionContext context)
    {
        if(!GpuPlan.Supported){_cpu.WriteSummary();return;}
        if(_gpu is not null)try{await context.ExecuteGpuAsync(_=>{_gpu.Dispose();return true;},CancellationToken.None).ConfigureAwait(false);}catch{_faulted=true;throw;}
        RuntimeLog.TryBackgroundInfo($"transformationFamilySummary=true;family={Id};batches={_batches};input={_inputs};output={_outputs};setupMs={_gpu?.SetupMs??0};dispatchSyncMs={_dispatch};readbackMs={_readback};canonicalMs={_canonical};parity={_parity};faulted={_faulted};recovery=None");
    }
}
