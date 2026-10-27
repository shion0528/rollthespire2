using System.Diagnostics;
using Godot;
namespace RolltheSpire2.Search.FamilyExecution;

internal readonly record struct AncientOptionFamilyGpuMetrics(int Input, int Output, int Dispatches, long ReadbackBytes,
    double ReadbackMs, double SortMs, double CanonicalMs, double DispatchSyncMs = 0);
internal sealed class AncientOptionFamilyGpuExecutor : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly FamilyGpuHeaderReadback _headerReadback;
    internal string HeaderReadbackRevision => _headerReadback.Revision;
    private readonly List<Rid> _owned = [];
    private readonly Rid _batch, _input, _header, _output, _pipeline, _uniforms;
    private readonly SharedVisibleSeedGpuCache? _visibleSeedCache;
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private bool _disposed;
    private readonly bool _privateInput, _privateOutput;
    private readonly bool _traceNwae = NwaePhysicalExperiment.Mode.Length != 0;
    internal int Capacity { get; }
    internal string Device { get; }
    internal double SetupMs { get; }
    internal long WorkspaceBytes { get; }
    internal AncientOptionFamilyGpuExecutor(RenderingDevice rd, AncientOptionFamilyPlan plan,
        PrivateOrdinalBuffer? privateInput = null, PrivateOrdinalBuffer? privateOutput = null)
    {
        _privateInput = privateInput is not null; _privateOutput = privateOutput is not null;
        if (_privateInput && ReferenceEquals(privateInput, privateOutput)) throw new ArgumentException("A.PrivateBuffersAlias");
        _rd = rd; Capacity = plan.Capacity; var watch = Stopwatch.StartNew();
        _headerReadback = new(rd, FamilyGpuHeaderReadback.Requested);
        Rid Add(Rid r) { _owned.Add(r); return r; }
        Rid Data(uint[] data) => Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(data)));
        try
        {
            var buffers = new Rid[13];
            buffers[0] = _batch = Data(new uint[8]);
            for (int i = 0; i < plan.Buffers.Length; i++) buffers[i + 1] = Data(plan.Buffers[i]);
            buffers[9] = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, checked(Capacity * plan.ScratchWords * 4)));
            buffers[10] = _input = privateInput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity * 4));
            buffers[10 + 1] = _header = Data(Header());
            buffers[10 + 2] = _output = privateOutput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity * 4));
            string source = plan.ShaderSource();
            if (_privateInput)
            {
                const string address = "input_ids.v[i]";
                if (source.IndexOf(address, StringComparison.Ordinal) < 0 ||
                    source.IndexOf(address, StringComparison.Ordinal) != source.LastIndexOf(address, StringComparison.Ordinal))
                    throw new InvalidOperationException("A.PrivateInputShaderSeam");
                source = source.Replace(address, "input_ids.v[batch.v[6]+i]", StringComparison.Ordinal);
            }
            _visibleSeedCache = SharedVisibleSeedGpuCache.Attach(rd, _owned, ref source, ref buffers, "A.AncientOption");
            Rid shader = Add(FamilyGpuComputeUtility.CompileShader(rd, source, "AncientOptionFamily"));
            _pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, "A.AncientOption"));
            if (!rd.ComputePipelineIsValid(_pipeline)) throw new InvalidOperationException("A.AncientOption.PipelineInvalid");
            _uniforms = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers, "A.AncientOption"));
            WorkspaceBytes = 64 + Capacity * 8L + plan.Buffers.Sum(b => Math.Max(4, b.Length * 4L)) + Math.Max(4, Capacity * plan.ScratchWords * 4L) +
                (_visibleSeedCache is null ? 0 : SharedVisibleSeedGpuCache.Bytes);
            Device = rd.GetDeviceName(); SetupMs = watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception failure)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(failure, () => FamilyGpuComputeUtility.FreeAll(rd, _owned), "A.AncientOption");
            throw;
        }
    }
    private static uint[] Header() => [0x414f4652u, 1, 0, 0, 0, 0x414f4652u, 0, 0];
    private void Check() { if (_owner != System.Environment.CurrentManagedThreadId) throw new InvalidOperationException("A.AncientOption.OwnerMismatch"); ObjectDisposedException.ThrowIf(_disposed, this); }
    internal FamilyCandidateSet Execute(FamilyCandidateSet input, CancellationToken token, out AncientOptionFamilyGpuMetrics metrics)
    {
        if (_privateInput || _privateOutput) throw new InvalidOperationException("A.PrivateEntryRequired");
        return ExecuteCore(input, input.Count, token, out metrics)!;
    }
    internal FamilyCandidateSet ExecutePrivate(SearchBatch batch, int count, CancellationToken token, out AncientOptionFamilyGpuMetrics metrics)
    {
        if (!_privateInput || _privateOutput || count < 0 || count > batch.BatchCandidateCount || batch.BatchCandidateCount > PrivateOrdinalBuffer.Capacity)
            throw new InvalidDataException("A.PrivateTerminalBounds");
        return ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics)!;
    }
    internal int ExecutePrivateStage(SearchBatch batch, int count, CancellationToken token, out AncientOptionFamilyGpuMetrics metrics)
    {
        if (!_privateOutput || (!_privateInput && count != batch.BatchCandidateCount) || count < 0 || count > batch.BatchCandidateCount || batch.BatchCandidateCount > PrivateOrdinalBuffer.Capacity)
            throw new InvalidDataException("A.PrivateStageBounds");
        ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics);
        return metrics.Output;
    }
    private FamilyCandidateSet? ExecuteCore(FamilyCandidateSet input, int inputCount, CancellationToken token, out AncientOptionFamilyGpuMetrics metrics)
    {
        Check(); token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew(); var survivors = new List<ulong>(); int dispatches = 0; long bytes = 0; double read = 0;
        _visibleSeedCache?.Refresh(input.Batch);
        double dispatchMs = 0; int privateCount = 0;
        if (_privateOutput) FamilyGpuComputeUtility.Update(_rd, _header, Header(), "A.PrivateHeader");
        for (int offset = 0; offset < inputCount; offset += Capacity)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(Capacity, inputCount - offset);
            FamilyGpuComputeUtility.Update(_rd, _batch, [(uint)input.Batch.BatchBase,(uint)(input.Batch.BatchBase>>32),
                (uint)input.Batch.BatchCandidateCount,(uint)count,input.IsDense && !_privateInput?0u:1u,
                (uint)(_privateOutput ? PrivateOrdinalBuffer.Capacity : Capacity),(uint)offset,0], "A.AncientOption.Batch");
            if (!_privateOutput) FamilyGpuComputeUtility.Update(_rd, _header, Header(), "A.AncientOption.Header");
            if (!input.IsDense && !_privateInput)
            {
                var source = input.ExportAbi1().Span.Slice(offset, count); var packed = new uint[count];
                for (int i = 0; i < count; i++) packed[i] = checked((uint)source[i]);
                FamilyGpuComputeUtility.Update(_rd, _input, packed, "A.AncientOption.Input");
            }
            token.ThrowIfCancellationRequested();
            double dispatchStart = watch.Elapsed.TotalMilliseconds;
            long list = _rd.ComputeListBegin();
            try
            {
                _rd.ComputeListBindComputePipeline(list, _pipeline); _rd.ComputeListBindUniformSet(list, _uniforms, 0);
                _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
            }
            finally { _rd.ComputeListEnd(); }
            bool needsHeader = !_privateOutput || offset + count == inputCount;
            double completionStart = watch.Elapsed.TotalMilliseconds;
            uint[]? h = needsHeader
                ? _headerReadback.CompleteRecorded(_header, 8, true, "A.AncientOption", static () => { }) : null;
            if (!needsHeader) FamilyGpuComputeUtility.SubmitAndSync(_rd, "A.AncientOption");
            double headerTransportMs = needsHeader ? Math.Max(0, watch.Elapsed.TotalMilliseconds - completionStart -
                _headerReadback.LastSubmitMs - _headerReadback.LastSyncMs) : 0;
            dispatches++;
            dispatchMs += watch.Elapsed.TotalMilliseconds - dispatchStart;
            dispatchMs -= headerTransportMs; read += headerTransportMs;
            if (!needsHeader) continue;
            token.ThrowIfCancellationRequested(); double start = watch.Elapsed.TotalMilliseconds;
            if (h is null) throw new InvalidDataException("A.AncientOption.MissingCompletedHeader");
            int processed = _privateOutput ? offset + count : count;
            if (h.Length != 8 || h[0] != 0x414f4652u || h[1] != 1 || h[5] != 0x414f4652u || h[3] != 0 || h[4] != processed || h[2] > processed)
                throw new InvalidDataException($"A.AncientOption.HeaderFault:batch={input.Batch.BatchBase};offset={offset};header={string.Join(',', h)}");
            if (_privateOutput) { privateCount = checked((int)h[2]); bytes += 32; read += watch.Elapsed.TotalMilliseconds-start; continue; }
            uint n = h[2]; bytes += 32 + n * 4L;
            var output = n == 0 ? [] : FamilyGpuComputeUtility.FromUInt32Bytes(_rd.BufferGetData(_output, 0, n * 4));
            if (output.Length != n) throw new InvalidDataException("A.AncientOption.PayloadMismatch");
            read += watch.Elapsed.TotalMilliseconds - start;
            foreach (uint ordinal in output) survivors.Add(ordinal);
        }
        if (_privateOutput)
        {
            token.ThrowIfCancellationRequested();
            metrics = new(inputCount, privateCount, dispatches, bytes, read, 0, watch.Elapsed.TotalMilliseconds, dispatchMs);
            return null;
        }
        token.ThrowIfCancellationRequested(); double sortStart = watch.Elapsed.TotalMilliseconds;
        survivors.Sort(); double sort = watch.Elapsed.TotalMilliseconds - sortStart;
        var result = FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors.ToArray());
        if (!input.IsDense)
        {
            var source = input.ExportAbi1().Span; int i = 0;
            foreach (ulong ordinal in survivors)
            {
                while (i < source.Length && source[i] < ordinal) i++;
                if (i == source.Length || source[i] != ordinal) throw new InvalidDataException("A.AncientOption.OutputNotInputSubset");
            }
        }
        token.ThrowIfCancellationRequested();
        metrics = new(inputCount, result.Count, dispatches, bytes, read, sort, watch.Elapsed.TotalMilliseconds, dispatchMs);
        if (_traceNwae) Bootstrap.RuntimeLog.TryBackgroundDetail($"nwaeStageTiming=true;stage=A;input={input.Count};output={result.Count};dispatches={dispatches};dispatchSyncMs={dispatchMs};readbackBytes={bytes};readbackMs={read};sortMs={sort};canonicalMs={metrics.CanonicalMs}");
        return result;
    }
    public void Dispose() { if (_disposed) return; Check(); _disposed = true; FamilyGpuComputeUtility.FreeAll(_rd, _owned); }
}
