using System.Diagnostics;
using Godot;

namespace RolltheSpire2.Search.FamilyExecution;

internal readonly record struct WorldFamilyGpuMetrics(int Input, int Output, int Dispatches,
    int SubmissionGroups, int HeaderReadCalls, long HeaderReadBytes, int PayloadReadCalls, long PayloadReadBytes,
    double DispatchSyncMs, double HeaderReadbackMs, double CanonicalMs);
// Bounded windows within one SearchBatch. No numerical work or public state is changed by grouping.
internal sealed class WorldFamilyGpuExecutor : IDisposable
{
    internal int Capacity { get; }
    private readonly RenderingDevice _rd;
    private readonly FamilyGpuHeaderReadback _headerReadback;
    internal string HeaderReadbackRevision => _headerReadback.Revision;
    private readonly List<Rid> _owned = new();
    private readonly Rid[] _batch, _header, _input, _output, _uniforms;
    private readonly Rid _pipeline, _headerBank;
    private readonly SharedVisibleSeedGpuCache? _seedCache;
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private bool _disposed;
    private readonly bool _tracePrivate = EwPhysicalExperiment.Mode.Length != 0 || PrivateOrdinalSelection.IsRequested || NwaePhysicalExperiment.Mode.Length != 0;
    private readonly bool _privateInput;
    private readonly PrivateOrdinalBuffer? _privateOutput;
    internal int GroupingK { get; }
    internal string Device { get; }
    internal double SetupMs { get; }

    internal WorldFamilyGpuExecutor(RenderingDevice rd, WorldFamilyGpuPlan plan, Rid? privateInput = null, PrivateOrdinalBuffer? privateOutput = null)
    {
        _rd = rd; Capacity = plan.Capacity; GroupingK = plan.GroupingK;
        _headerReadback = new(rd, FamilyGpuHeaderReadback.Requested);
        _privateInput = privateInput.HasValue; _privateOutput = privateOutput;
        if (privateInput.HasValue && privateOutput is not null && privateInput.Value == privateOutput.Buffer)
            throw new ArgumentException("W.PrivateBuffersAlias");
        var watch = Stopwatch.StartNew();
        Rid Add(Rid rid) { _owned.Add(rid); return rid; }
        Rid Buffer(uint[] values) => Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(values)));
        _batch = new Rid[GroupingK]; _header = new Rid[GroupingK]; _input = new Rid[GroupingK];
        _output = new Rid[GroupingK]; _uniforms = new Rid[GroupingK];
        try
        {
            Rid[] buffers = new Rid[plan.Buffers.Length];
            for (int i = 0; i < plan.Buffers.Length; i++)
                if (i is not (0 or 12 or 16 or 17 or 18)) buffers[i] = Buffer(plan.Buffers[i]);
            buffers[12] = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, checked(Capacity * plan.ScratchWords * 4)));
            string source = plan.ShaderSource();
            if (_privateInput)
            {
                if (!source.Contains("input_ids.v[i]", StringComparison.Ordinal)) throw new InvalidOperationException("W.PrivateInputSeamChanged");
                source = source.Replace("input_ids.v[i]", "input_ids.v[batch.v[6]+i]", StringComparison.Ordinal);
            }
            if (privateOutput is not null)
            {
                // batch[5] also locates the event scratch after encounter scratch.
                // Keep that window geometry; only append capacity uses reserved batch[7].
                const string bound = "if(p>=batch.v[5])";
                if (!source.Contains(bound, StringComparison.Ordinal)) throw new InvalidOperationException("W.PrivateOutputSeamChanged");
                source = source.Replace(bound, "if(p>=batch.v[7])", StringComparison.Ordinal);
            }
            _seedCache = SharedVisibleSeedGpuCache.Attach(rd, _owned, ref source, ref buffers, "W.World");
            Rid shader = Add(FamilyGpuComputeUtility.CompileShader(rd, source, "WorldFamily"));
            _pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, "W.World"));
            if (!rd.ComputePipelineIsValid(_pipeline)) throw new InvalidOperationException("W.GpuPipelineInvalid");
            for (int slot = 0; slot < GroupingK; slot++)
            {
                buffers[0] = _batch[slot] = Buffer(new uint[8]);
                buffers[16] = _header[slot] = privateOutput is not null && slot > 0 ? _header[0] : Buffer(Header());
                buffers[18] = _input[slot] = privateInput ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity * 4));
                buffers[17] = _output[slot] = privateOutput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity * 4));
                _uniforms[slot] = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers, "W.World"));
            }
            // K1 retains its original direct header read and has no copy/bank.
            _headerBank = GroupingK == 1 || privateOutput is not null ? default : Buffer(new uint[GroupingK * 8]);
            Device = rd.GetDeviceName(); SetupMs = watch.Elapsed.TotalMilliseconds;
        }
        catch (Exception failure)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(failure, () => FamilyGpuComputeUtility.FreeAll(rd, _owned), "W.World");
            throw;
        }
    }

    private static uint[] Header() => [0x57464d52, 1, 0, 0, 0, 0x57463031, 0, 0];
    private void AssertOwner()
    {
        if (_owner != System.Environment.CurrentManagedThreadId) throw new InvalidOperationException("W.GpuOwnerMismatch");
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal FamilyCandidateSet Execute(FamilyCandidateSet input, CancellationToken token, out WorldFamilyGpuMetrics metrics)
        {
        if (_privateInput || _privateOutput is not null) throw new InvalidOperationException("W.PrivateEntryRequired");
        return ExecuteCore(input, token, out metrics, input.Count)!;
    }

    // Only the bounded EW owner can supply this count/buffer; no public ABI state.
    internal FamilyCandidateSet ExecutePrivate(SearchBatch batch, int count, CancellationToken token, out WorldFamilyGpuMetrics metrics)
    {
        if (!_privateInput || _privateOutput is not null || count < 0 || count > batch.BatchCandidateCount || batch.BatchCandidateCount > PrivateOrdinalBuffer.Capacity) throw new InvalidDataException("W.PrivateCountInvalid");
        return ExecuteCore(FamilyCandidateSet.Dense(batch), token, out metrics, count)!;
    }

    internal int ExecutePrivateStage(SearchBatch batch, int count, CancellationToken token, out WorldFamilyGpuMetrics metrics)
    {
        if (_privateOutput is null || (!_privateInput && count != batch.BatchCandidateCount)) throw new InvalidOperationException("W.PrivateStagePortsRequired");
        _privateOutput.CheckPopulation(batch, count);
        ExecuteCore(FamilyCandidateSet.Dense(batch), token, out metrics, count);
        return metrics.Output;
    }

    private FamilyCandidateSet? ExecuteCore(FamilyCandidateSet input, CancellationToken token, out WorldFamilyGpuMetrics metrics, int inputCount)
    {
        AssertOwner(); token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew(); var survivors = new List<ulong>();
        if (inputCount > 0) _seedCache?.Refresh(input.Batch);
        double dispatchSync = 0, headerRead = 0;
        double payloadRead = 0;
        int dispatches = 0, groups = 0, headerCalls = 0, payloadCalls = 0;
        long headerBytes = 0, payloadBytes = 0;
        int privateCount = 0;
        bool resident = _privateOutput is not null;
        if (resident) FamilyGpuComputeUtility.Update(_rd, _header[0], Header(), "W.PrivateHeader");
        for (int offset = 0; offset < inputCount;)
        {
            token.ThrowIfCancellationRequested();
            int slots = Math.Min(GroupingK, (inputCount - offset - 1) / Capacity + 1);
            double start;
            for (int slot = 0; slot < slots; slot++)
            {
                token.ThrowIfCancellationRequested();
                int windowOffset = offset + slot * Capacity, count = Math.Min(Capacity, inputCount - windowOffset);
                FamilyGpuComputeUtility.Update(_rd, _batch[slot], [(uint)input.Batch.BatchBase, (uint)(input.Batch.BatchBase >> 32),
                    (uint)input.Batch.BatchCandidateCount, (uint)count, input.IsDense && !_privateInput ? 0u : 1u, (uint)Capacity, (uint)windowOffset, resident ? (uint)PrivateOrdinalBuffer.Capacity : 0u], "W.Batch");
                if (!resident) FamilyGpuComputeUtility.Update(_rd, _header[slot], Header(), "W.Header");
                if (!input.IsDense && !_privateInput)
                {
                    var ordinals = input.ExportAbi1().Span.Slice(windowOffset, count);
                    var packed = new uint[count];
                    for (int i = 0; i < count; i++) packed[i] = checked((uint)ordinals[i]);
                    FamilyGpuComputeUtility.Update(_rd, _input[slot], packed, "W.CompactInput");
                }
            }
            token.ThrowIfCancellationRequested();
            start = watch.Elapsed.TotalMilliseconds;
            long list = _rd.ComputeListBegin();
            try
            {
                _rd.ComputeListBindComputePipeline(list, _pipeline);
                for (int slot = 0; slot < slots; slot++)
                {
                    // Once recording starts, finish/submit/sync even if cancellation arrives.
                    // This prevents leaving an active list or queued work using disposed/reused buffers.
                    if (slot > 0) _rd.ComputeListAddBarrier(list);
                    _rd.ComputeListBindUniformSet(list, _uniforms[slot], 0);
                    int count = Math.Min(Capacity, inputCount - offset - slot * Capacity);
                    _rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
                    dispatches++;
                }
            }
            finally { _rd.ComputeListEnd(); }
            if (GroupingK > 1 && !resident)
            {
                // BufferCopy is outside the active compute list. RD tracks compute-write -> transfer-read.
                for (int slot = 0; slot < slots; slot++)
                    if (_rd.BufferCopy(_header[slot], _headerBank, 0, (uint)(slot * 32), 32) != Error.Ok)
                        throw new InvalidOperationException($"W.HeaderBankCopyFailure:batch={input.Batch.BatchBase};offset={offset + slot * Capacity}");
            }
            int processedAfterGroup = offset + Math.Min(inputCount - offset, slots * Capacity);
            bool needsHeader = !resident || processedAfterGroup == inputCount;
            double completionStart = watch.Elapsed.TotalMilliseconds;
            uint[]? completedHeaders = needsHeader
                ? _headerReadback.CompleteRecorded(resident || GroupingK == 1 ? _header[0] : _headerBank,
                    checked((uint)(resident ? 8 : slots * 8)), true, "W.World", static () => { }) : null;
            if (!needsHeader) FamilyGpuComputeUtility.SubmitAndSync(_rd, "W.World");
            double headerTransportMs = needsHeader ? Math.Max(0, watch.Elapsed.TotalMilliseconds - completionStart -
                _headerReadback.LastSubmitMs - _headerReadback.LastSyncMs) : 0;
            dispatchSync += watch.Elapsed.TotalMilliseconds - start - headerTransportMs;
            headerRead += headerTransportMs; groups++;
            token.ThrowIfCancellationRequested();
            if (!needsHeader) { offset = processedAfterGroup; continue; }
            start = watch.Elapsed.TotalMilliseconds;
            if (resident)
            {
                uint[] h = completedHeaders ?? throw new InvalidDataException("W.MissingPrivateCompletedHeader");
                int processed = processedAfterGroup;
                if (h.Length != 8 || h[0] != 0x57464d52 || h[1] != 1 || h[5] != 0x57463031 ||
                    h[3] != 0 || h[4] != processed || h[2] > processed)
                    throw new InvalidDataException("W.PrivateHeaderFault:" + string.Join(',', h));
                privateCount = checked((int)h[2]);
                headerCalls++; headerBytes += 32; headerRead += watch.Elapsed.TotalMilliseconds - start;
                offset = processed;
                continue;
            }
            uint[] headers = completedHeaders ?? throw new InvalidDataException("W.MissingCompletedHeaderBank");
            if (headers.Length != slots * 8) throw new InvalidDataException($"W.GpuHeaderBankLength:offset={offset}");
            for (int slot = 0; slot < slots; slot++)
            {
                int h = slot * 8, count = Math.Min(Capacity, inputCount - offset - slot * Capacity);
                if (headers[h] != 0x57464d52 || headers[h + 1] != 1 || headers[h + 5] != 0x57463031 ||
                    headers[h + 3] != 0 || headers[h + 4] != count || headers[h + 2] > count)
                    throw new InvalidDataException($"W.GpuHeaderOrNumericFailure:batch={input.Batch.BatchBase};offset={offset + slot * Capacity};slot={slot};fault={headers[h + 3]};processed={headers[h + 4]};output={headers[h + 2]}");
            }
            headerCalls++; headerBytes += slots * 32L;
            headerRead += watch.Elapsed.TotalMilliseconds - start;
            token.ThrowIfCancellationRequested();
            for (int slot = 0; slot < slots; slot++)
            {
                uint n = headers[slot * 8 + 2];
                if (n == 0) continue;
                double payloadStart = watch.Elapsed.TotalMilliseconds;
                uint[] packed = FamilyGpuComputeUtility.FromUInt32Bytes(_rd.BufferGetData(_output[slot], 0, n * 4));
                if (packed.Length != n) throw new InvalidDataException($"W.GpuPayloadCountMismatch:offset={offset + slot * Capacity}");
                payloadCalls++; payloadBytes += n * 4L;
                foreach (uint ordinal in packed) survivors.Add(ordinal);
                payloadRead += watch.Elapsed.TotalMilliseconds - payloadStart;
            }
            token.ThrowIfCancellationRequested();
            offset += Math.Min(inputCount - offset, slots * Capacity);
        }
        if (resident)
        {
            token.ThrowIfCancellationRequested();
            metrics = new(inputCount, privateCount, dispatches, groups, headerCalls, headerBytes, 0, 0,
                dispatchSync, headerRead, watch.Elapsed.TotalMilliseconds);
            return null;
        }
        double sortStart = watch.Elapsed.TotalMilliseconds;
        survivors.Sort();
        double sortMs = watch.Elapsed.TotalMilliseconds - sortStart;
        double validationStart = watch.Elapsed.TotalMilliseconds;
        var result = FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors.ToArray());
        if (!input.IsDense && !_privateInput)
        {
            var source = input.ExportAbi1().Span; int index = 0;
            foreach (ulong ordinal in survivors)
            {
                while (index < source.Length && source[index] < ordinal) index++;
                if (index == source.Length || source[index] != ordinal) throw new InvalidDataException("W.GpuOutputNotInputSubset");
            }
        }
        token.ThrowIfCancellationRequested();
        metrics = new(inputCount, result.Count, dispatches, groups, headerCalls, headerBytes, payloadCalls, payloadBytes,
            dispatchSync, headerRead, watch.Elapsed.TotalMilliseconds);
        double validationMs = watch.Elapsed.TotalMilliseconds - validationStart;
        if (_tracePrivate)
            RolltheSpire2.Bootstrap.RuntimeLog.TryBackgroundDetail($"ewStageTiming=true;stage=W;input={inputCount};output={result.Count};dispatches={dispatches};groups={groups};dispatchSyncMs={dispatchSync};headerMs={headerRead};payloadMs={payloadRead};readbackBytes={headerBytes+payloadBytes};sortMs={sortMs};validationMs={validationMs};prepareOtherMs={metrics.CanonicalMs-dispatchSync-headerRead-payloadRead-sortMs-validationMs};canonicalMs={metrics.CanonicalMs}");
        return result;
    }
    public void Dispose() { AssertOwner(); _disposed = true; FamilyGpuComputeUtility.FreeAll(_rd, _owned); }
}
