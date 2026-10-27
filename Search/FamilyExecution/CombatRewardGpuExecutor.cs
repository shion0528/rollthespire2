using System.Diagnostics;
using Godot;

namespace RolltheSpire2.Search.FamilyExecution;

internal readonly record struct CombatRewardGpuMetrics(int Input, int Output, int Dispatches, long ReadbackBytes,
    double UploadMs, double DispatchSyncMs, double ReadbackMs, double CanonicalMs);

// Bounded private dispatch windows, never a change to the root SearchBatch/ABI.
internal sealed class CombatRewardGpuExecutor : IDisposable
{
    internal const int Capacity = 1 << 20;
    private readonly RenderingDevice _rd;
    private readonly FamilyGpuHeaderReadback _headerReadback;
    internal string HeaderReadbackRevision => _headerReadback.Revision;
    private readonly List<Rid> _owned = new();
    private readonly Rid _batch, _header, _input, _output, _pipeline, _uniforms;
    private readonly SharedVisibleSeedGpuCache? _visibleSeedCache;
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private readonly int _routes;
    private readonly PrivateOrdinalBuffer? _privateOutput;
    private readonly bool _privateInput;
    private readonly PrivateOrdinalBuffer? _privateInputBuffer;
    private bool _disposed;
    private readonly bool _tracePrivate = NcPhysicalExperiment.Mode.Length != 0 || PrivateOrdinalSelection.IsRequested;
    internal string Device { get; }
    internal double SetupMs { get; }
    internal CombatRewardGpuExecutor(RenderingDevice rd, CombatRewardGpuPlan plan, bool forceGeneric = false, PrivateOrdinalBuffer? privateOutput = null, PrivateOrdinalBuffer? privateInput = null)
    {
        _rd = rd; _routes = plan.Routes; _privateOutput = privateOutput;
        _headerReadback = new(rd, FamilyGpuHeaderReadback.Requested);
        _privateInput=privateInput is not null; _privateInputBuffer=privateInput;
        if (_privateInput && ReferenceEquals(privateInput,privateOutput)) throw new ArgumentException("C.PrivateBuffersAlias");
        var watch = Stopwatch.StartNew();
        Rid Add(Rid rid) { _owned.Add(rid); return rid; }
        Rid Buffer(uint[] values) => Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(values)));
        try
        {
            _batch=Buffer(new uint[8]); _header=Buffer(Header());
            _input=privateInput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity*4));
            _output=privateOutput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity*4));
            Rid[] buffers=[_batch,Buffer(plan.Meta),Buffer([0]),Buffer(plan.Pools),Buffer(plan.Ids),Buffer([0]),
                Buffer(plan.Predicates),Buffer(plan.Targets),_header,_output,_input];
            string source=plan.ShaderSource(forceGeneric);
            if (_privateInput)
            {
                const string read="input_ordinals.values[index]";
                if(!source.Contains(read,StringComparison.Ordinal))throw new InvalidOperationException("C.PrivateInputSeamChanged");
                source=source.Replace(read,"input_ordinals.values[batch_meta.values[6]+index]",StringComparison.Ordinal);
            }
            _visibleSeedCache = SharedVisibleSeedGpuCache.Attach(rd, _owned, ref source, ref buffers, "C.CombatReward");
            Rid shader=Add(FamilyGpuComputeUtility.CompileShader(rd,source,"CombatRewardFamily"));
            _pipeline=Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, "C.CombatReward"));
            if(!rd.ComputePipelineIsValid(_pipeline))throw new InvalidOperationException("C.GpuPipelineInvalid");
            _uniforms=Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers, "C.CombatReward"));
            Device=rd.GetDeviceName(); SetupMs=watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception failure)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(failure, () => FamilyGpuComputeUtility.FreeAll(rd, _owned), "C.CombatReward");
            throw;
        }
    }
    private static uint[] Header() => [0x43464d52,1,0,0,0,0x43463031,0,0];
    private void AssertOwner()
    {
        if(_owner!=System.Environment.CurrentManagedThreadId)throw new InvalidOperationException("C.GpuOwnerMismatch");
        ObjectDisposedException.ThrowIf(_disposed,this);
    }
    internal FamilyCandidateSet Execute(FamilyCandidateSet input,CancellationToken token,out CombatRewardGpuMetrics metrics)
    {
        if (_privateOutput is not null || _privateInput) throw new InvalidOperationException("C.PrivateTransportRequiresPrivateEntry");
        return ExecuteCore(input, token, out metrics, out _)!;
    }
    internal FamilyCandidateSet ExecutePrivateInput(SearchBatch batch,int count,CancellationToken token,out CombatRewardGpuMetrics metrics)
    {
        if(!_privateInput || _privateOutput is not null || count<0 || count>batch.BatchCandidateCount || count>PrivateOrdinalBuffer.Capacity)throw new InvalidDataException("C.PrivateInputCountInvalid");
        _privateInputBuffer!.CheckPopulation(batch,count);
        return ExecuteCore(FamilyCandidateSet.Dense(batch),token,out metrics,out _,count)!;
    }

    internal int ExecutePrivate(FamilyCandidateSet input,CancellationToken token,out CombatRewardGpuMetrics metrics)
    {
        if (_privateOutput is null || _privateInput || !input.IsDense) throw new InvalidOperationException("C.PrivateDenseOnly");
        _privateOutput.CheckPopulation(input.Batch,input.Count);
        ExecuteCore(input, token, out metrics, out int count);
        return count;
    }
    // Both endpoints borrow distinct buffers; ordinal order is private and arbitrary.
    internal int ExecutePrivateStage(SearchBatch batch,int count,CancellationToken token,out CombatRewardGpuMetrics metrics)
    {
        if(!_privateInput || _privateOutput is null) throw new InvalidOperationException("C.PrivateMiddlePortsRequired");
        _privateInputBuffer!.CheckPopulation(batch,count);
        _privateOutput.CheckPopulation(batch,count);
        ExecuteCore(FamilyCandidateSet.Dense(batch),token,out metrics,out int output,count);
        return output;
    }
    private FamilyCandidateSet? ExecuteCore(FamilyCandidateSet input,CancellationToken token,out CombatRewardGpuMetrics metrics, out int privateCount,int? privateInputCount=null)
    {
        AssertOwner(); token.ThrowIfCancellationRequested();
        privateCount = 0;
        bool resident = _privateOutput is not null;
        int inputCount=privateInputCount??input.Count;
        bool dense=input.IsDense&&!_privateInput;
        if (resident && !input.IsDense) throw new InvalidOperationException("C.PrivateDenseOnly");
        var watch=Stopwatch.StartNew(); var survivors=new List<ulong>();
        double upload=0,sync=0,read=0,widen=0; long bytes=0; int dispatches=0;
        if (_visibleSeedCache is not null)
        {
            _visibleSeedCache.Refresh(input.Batch);
            upload += watch.Elapsed.TotalMilliseconds;
        }
        if (resident) FamilyGpuComputeUtility.Update(_rd,_header,Header(),"C.PrivateHeader");
        for(int offset=0;offset<inputCount;offset+=Capacity)
        {
            token.ThrowIfCancellationRequested();
            int count=Math.Min(Capacity,inputCount-offset);
            double start=watch.Elapsed.TotalMilliseconds;
            FamilyGpuComputeUtility.Update(_rd,_batch,[(uint)input.Batch.BatchBase,(uint)(input.Batch.BatchBase>>32),
                (uint)input.Batch.BatchCandidateCount,(uint)count,dense?0u:1u,(uint)(resident?PrivateOrdinalBuffer.Capacity:Capacity),(uint)offset,(uint)_routes],"C.Batch");
            if (!resident) FamilyGpuComputeUtility.Update(_rd,_header,Header(),"C.Header");
            if(!input.IsDense)
            {
                var ordinals=input.ExportAbi1().Span.Slice(offset,count);
                var packed=new uint[count];
                for(int i=0;i<count;i++)packed[i]=checked((uint)ordinals[i]);
                FamilyGpuComputeUtility.Update(_rd,_input,packed,"C.CompactInput");
            }
            upload+=watch.Elapsed.TotalMilliseconds-start;
            start=watch.Elapsed.TotalMilliseconds;
            long list=_rd.ComputeListBegin();
            try {
                _rd.ComputeListBindComputePipeline(list,_pipeline);_rd.ComputeListBindUniformSet(list,_uniforms,0);
                _rd.ComputeListDispatch(list,(uint)((count+63)/64),1,1);
            } finally { _rd.ComputeListEnd(); }
            bool needsHeader = !resident || offset + count == inputCount;
            double completionStart = watch.Elapsed.TotalMilliseconds;
            uint[]? header = needsHeader
                ? _headerReadback.CompleteRecorded(_header, 8, true, "C.CombatReward", static () => { }) : null;
            if (!needsHeader) FamilyGpuComputeUtility.SubmitAndSync(_rd, "C.CombatReward");
            double headerTransportMs = needsHeader ? Math.Max(0, watch.Elapsed.TotalMilliseconds - completionStart -
                _headerReadback.LastSubmitMs - _headerReadback.LastSyncMs) : 0;
            dispatches++;
            sync+=watch.Elapsed.TotalMilliseconds-start;
            sync -= headerTransportMs; read += headerTransportMs;
            if (!needsHeader) continue;
            token.ThrowIfCancellationRequested();start=watch.Elapsed.TotalMilliseconds;
            if (header is null) throw new InvalidDataException("C.MissingCompletedHeader");
            if(header.Length!=8||header[0]!=0x43464d52||header[1]!=1||header[5]!=0x43463031||
                header[3]!=0||header[4]!=(resident?inputCount:count)||header[2]>(resident?inputCount:count))
                throw new InvalidDataException("C.GpuHeaderOrNumericFailure:"+string.Join(',',header));
            if (resident)
            {
                privateCount=checked((int)header[2]); bytes+=32; read+=watch.Elapsed.TotalMilliseconds-start;
                metrics=new(inputCount,privateCount,dispatches,bytes,upload,sync,read,watch.Elapsed.TotalMilliseconds);
                return null;
            }
            uint n=header[2];bytes+=32+n*4L;
            uint[] packedOutput=n==0?[]:FamilyGpuComputeUtility.FromUInt32Bytes(_rd.BufferGetData(_output,0,n*4));
            if(packedOutput.Length!=n)throw new InvalidDataException("C.GpuPayloadCountMismatch");
            read+=watch.Elapsed.TotalMilliseconds-start;
            start=watch.Elapsed.TotalMilliseconds;
            foreach(uint ordinal in packedOutput)survivors.Add(ordinal);
            widen+=watch.Elapsed.TotalMilliseconds-start;
        }
        double sortStart=watch.Elapsed.TotalMilliseconds;
        survivors.Sort();
        double sort=watch.Elapsed.TotalMilliseconds-sortStart,validationStart=watch.Elapsed.TotalMilliseconds;
        var result=FamilyCandidateSet.FromSortedAbi1(input.Batch,survivors.ToArray());
        if(!input.IsDense)
        {
            var source=input.ExportAbi1().Span;int index=0;
            foreach(ulong ordinal in survivors)
            {
                while(index<source.Length&&source[index]<ordinal)index++;
                if(index==source.Length||source[index]!=ordinal)throw new InvalidDataException("C.GpuOutputNotInputSubset");
            }
        }
        token.ThrowIfCancellationRequested();
        metrics=new(inputCount,result.Count,dispatches,bytes,upload,sync,read,watch.Elapsed.TotalMilliseconds);
        if (_tracePrivate)
            Bootstrap.RuntimeLog.TryBackgroundDetail($"ncStageTiming=true;stage=C;input={inputCount};output={result.Count};dispatches={dispatches};uploadMs={upload};dispatchSyncMs={sync};readbackMs={read};readbackBytes={bytes};widenMs={widen};sortMs={sort};validationMs={watch.Elapsed.TotalMilliseconds-validationStart};canonicalMs={metrics.CanonicalMs}");
        return result;
    }
    public void Dispose(){AssertOwner();_disposed=true;FamilyGpuComputeUtility.FreeAll(_rd, _owned);}
}
