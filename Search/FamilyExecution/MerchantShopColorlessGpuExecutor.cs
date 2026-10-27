using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Godot;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Merchant;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

internal readonly record struct MerchantShopColorlessGpuBatchMetrics(
    MerchantShopColorlessCompactionPath CompactionPath,
    int InputCandidates,
    int ProcessedCandidates,
    int Survivors,
    double UploadMs,
    double CommandMs,
    double SubmitMs,
    double SyncMs,
    double HeaderReadbackMs,
    double PayloadReadbackMs,
    double TypedAllocationMs,
    double DecodePayloadMs,
    double SortMs,
    double ValidationCandidateSetMs,
    long PayloadReadbackAllocatedBytes,
    long HostAllocatedBytes,
    long HeaderReadbackBytes,
    long PayloadReadbackBytes,
    double StablePhase1CommandMs,
    double StablePhase1SubmitMs,
    double StablePhase1SyncMs,
    double StableCountsReadbackMs,
    double StableHostPrefixMs,
    double StableOffsetsUploadMs,
    double StableScatterCommandMs,
    double StableScatterSubmitMs,
    double StableScatterSyncMs,
    long StableCountsReadbackBytes,
    double CanonicalAbi1ReadyMs,
    int Dispatches = 0)
{
    public double ReadbackMs => HeaderReadbackMs + PayloadReadbackMs + StableCountsReadbackMs;
    public double HostMaterializeMs => TypedAllocationMs + DecodePayloadMs + SortMs + ValidationCandidateSetMs;
    public long ReadbackBytes => HeaderReadbackBytes + PayloadReadbackBytes + StableCountsReadbackBytes;
    public double PhysicalMs => UploadMs + CommandMs + SubmitMs + SyncMs + ReadbackMs + StableHostPrefixMs + StableOffsetsUploadMs;
    public double CandidatesPerSecond => PhysicalMs <= 0d ? 0d : InputCandidates * 1000d / PhysicalMs;
}

/// <summary>
/// Family-specific GPU resources and synchronous batch execution. The unified
/// FamilyExecutionContext guarantees that every method is called on its single
/// session GPU owner thread; this class creates no thread, Task, or queue.
/// </summary>
internal sealed partial class MerchantShopColorlessGpuExecutor : IDisposable
{
    internal const int WorkgroupSize = 64;
    internal const int SeedsPerInvocation = 8;
    internal const int Capacity = FamilyPhysicalExecutionDefaults.DefaultCandidateBatchSize;
    internal const int CompactInputCapacity = 1 << 22;
    internal const int SurvivorCapacity = 1 << 22;
    internal const int MaximumInvocationCount = Capacity / SeedsPerInvocation;
    internal const int MaximumWorkgroupCount = Capacity / (WorkgroupSize * SeedsPerInvocation);
    internal const int StableKeepMaskBytes = MaximumInvocationCount * sizeof(uint);
    internal const int StableBlockCountBytes = MaximumWorkgroupCount * sizeof(uint);
    internal const int StableBlockOffsetBytes = MaximumWorkgroupCount * sizeof(uint);

    private const uint HeaderMagic = 0x53464D52u; // "SFMR"
    private const uint HeaderAbi = 1u;
    private const int HeaderUIntCount = 8;
    private const string ShaderSuffix = "MerchantShopColorless.comp.glsl";
    private const string StableScatterShaderSuffix = "MerchantShopColorlessStableScatter.comp.glsl";

    private readonly RenderingDevice _rd;
    private readonly FamilyGpuHeaderReadback _headerReadback;
    internal string HeaderReadbackRevision => _headerReadback.Revision;
    private readonly List<Rid> _owned = new();
    private readonly Rid _pipeline;
    private readonly Rid _batchMeta;
    private readonly Rid _inputOrdinals;
    private readonly Rid _outputHeader;
    private readonly Rid _outputOrdinals;
    private readonly Rid _uniformSet;
    private readonly bool _stableEnabled;
    private readonly Rid _stablePhase1Pipeline;
    private readonly Rid _stablePhase1UniformSet;
    private readonly Rid _stableScatterPipeline;
    private readonly Rid _stableScatterUniformSet;
    private readonly Rid _stableKeepMasks;
    private readonly Rid _stableBlockCounts;
    private readonly Rid _stableBlockOffsets;
    private readonly uint _planTag;
    private readonly MerchantOrderedSequenceSlotsPlan _orderedSequenceSlots;
    private readonly int _ownerThreadId;
    private bool _disposed;
    private PrivateOrdinalBuffer? _privateInput, _privateOutput;
    private SharedVisibleSeedGpuCache? _prefixCache;
    private int OutputCapacity => _privateInput is not null || _privateOutput is not null ? Capacity : SurvivorCapacity;

    private MerchantShopColorlessGpuExecutor(
        RenderingDevice rd,
        Rid pipeline,
        Rid batchMeta,
        Rid inputOrdinals,
        Rid outputHeader,
        Rid outputOrdinals,
        Rid uniformSet,
        bool stableEnabled,
        Rid stablePhase1Pipeline,
        Rid stablePhase1UniformSet,
        Rid stableScatterPipeline,
        Rid stableScatterUniformSet,
        Rid stableKeepMasks,
        Rid stableBlockCounts,
        Rid stableBlockOffsets,
        uint planTag,
        int ownerThreadId,
        double setupMs,
        string deviceName,
        MerchantOrderedSequenceSlotsPlan orderedSequenceSlots)
    {
        _rd = rd;
        _headerReadback = new(rd, FamilyGpuHeaderReadback.Requested);
        _pipeline = pipeline;
        _batchMeta = batchMeta;
        _inputOrdinals = inputOrdinals;
        _outputHeader = outputHeader;
        _outputOrdinals = outputOrdinals;
        _uniformSet = uniformSet;
        _stableEnabled = stableEnabled;
        _stablePhase1Pipeline = stablePhase1Pipeline;
        _stablePhase1UniformSet = stablePhase1UniformSet;
        _stableScatterPipeline = stableScatterPipeline;
        _stableScatterUniformSet = stableScatterUniformSet;
        _stableKeepMasks = stableKeepMasks;
        _stableBlockCounts = stableBlockCounts;
        _stableBlockOffsets = stableBlockOffsets;
        _planTag = planTag;
        _ownerThreadId = ownerThreadId;
        SetupMs = setupMs;
        DeviceName = deviceName;
        _orderedSequenceSlots = orderedSequenceSlots;
    }

    public double SetupMs { get; }
    public string DeviceName { get; }
    public bool StableOrderedCompactionEnabled => _stableEnabled;
    internal bool OrderedSequenceSlotsEnabled => _orderedSequenceSlots.Applied;
    internal MerchantOrderedSequenceSlotsPlan OrderedSequenceSlotsPlan => _orderedSequenceSlots;
    public string PhysicalImplementationRevision => (_stableEnabled
        ? FamilyPhysicalImplementationRevisions.MerchantShopColorlessStableOrderedCompaction
        : FamilyPhysicalImplementationRevisions.MerchantShopColorlessAtomicAppendHostSort) +
        (_prefixCache is null ? "" : "." + SharedVisibleSeedGpuCache.Revision) +
        _orderedSequenceSlots.RevisionSuffix + "." + _headerReadback.Revision;

    public static MerchantShopColorlessGpuExecutor Create(RenderingDevice rd, ExactSearchExecutionRequest request,
        MerchantShopColorlessCompactionPath compactionPath,
        bool? useOrderedSequenceSlots = null) => Create(rd, request, compactionPath, null, null, useOrderedSequenceSlots);
    internal static MerchantShopColorlessGpuExecutor Create(
        RenderingDevice rd,
        ExactSearchExecutionRequest request,
        MerchantShopColorlessCompactionPath compactionPath,
        PrivateOrdinalBuffer? privateInput, PrivateOrdinalBuffer? privateOutput,
        bool? useOrderedSequenceSlots = null)
    {
        if (privateInput is not null && ReferenceEquals(privateInput, privateOutput)) throw new ArgumentException("S.PrivateBuffersAlias");
        if ((privateInput is not null || privateOutput is not null) && compactionPath == MerchantShopColorlessCompactionPath.StableOrderedCompaction)
            throw new ArgumentException("S.PrivateAppendRequiresAtomicPath");
        ArgumentNullException.ThrowIfNull(rd);
        ArgumentNullException.ThrowIfNull(request);
        var timer = Stopwatch.StartNew();
        var owned = new List<Rid>();
        try
        {
            Rid Add(Rid rid) { owned.Add(rid); return rid; }
            bool stableEnabled = compactionPath == MerchantShopColorlessCompactionPath.StableOrderedCompaction;
            Beta111MerchantColorlessAuthority authority = Beta111MerchantColorlessAuthority.From(request.Authority);
            var orderedSequenceSlots = MerchantShopColorlessOrderedSequencePolicy.Compile(request, useOrderedSequenceSlots);
            uint[] plan = PackPlan(orderedSequenceSlots.Evaluation, authority, out uint[] targets);
            uint planTag = StableTag(
                request.SnapshotFingerprint + "|" + (stableEnabled
                    ? FamilyPhysicalImplementationRevisions.MerchantShopColorlessStableOrderedCompaction
                    : FamilyPhysicalImplementationRevisions.MerchantShopColorlessAtomicAppendHostSort) +
                orderedSequenceSlots.RevisionSuffix);

            Rid batchMeta = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 6 * sizeof(uint)));
            Rid inputOrdinals = privateInput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(
                rd, CompactInputCapacity * 2 * sizeof(uint)));
            Rid planBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(plan)));
            Rid targetBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(targets)));
            Rid outputHeader = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytes(CreateHeader(planTag))));
            Rid outputOrdinals = privateOutput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(
                rd, (privateInput is null ? SurvivorCapacity : Capacity) * 2 * sizeof(uint)));

            string source = FamilyGpuComputeUtility.LoadFamilyShaderWithVisibleSeedRootHash(ShaderSuffix);
            // Addressing/emission only; reuse the existing merchant predicate and RNG body.
            if (privateInput is not null)
            {
                const string read = "make_u64(compact_input.values[input_index * 2u], compact_input.values[input_index * 2u + 1u])";
                if (!source.Contains(read, StringComparison.Ordinal)) throw new InvalidOperationException("S.PrivateInputSeamChanged");
                source = source.Replace(read, "uint64_t(compact_input.values[input_index])", StringComparison.Ordinal)
                    .Replace("if (logical_ordinal >= uint64_t(batch_meta.values[2u])) return;",
                        "if (logical_ordinal >= uint64_t(batch_meta.values[2u])) { atomicExchange(output_header.values[3u],2u); return; }", StringComparison.Ordinal);
            }
            if (privateOutput is not null)
            {
                const string low = "compact_output.values[slot * 2u] = uint(logical_ordinal);";
                const string high = "compact_output.values[slot * 2u + 1u] = uint(logical_ordinal >> 32u);";
                if (!source.Contains(low, StringComparison.Ordinal) || !source.Contains(high, StringComparison.Ordinal)) throw new InvalidOperationException("S.PrivateOutputSeamChanged");
                source = source.Replace(low, "compact_output.values[slot] = uint(logical_ordinal);", StringComparison.Ordinal).Replace(high, "", StringComparison.Ordinal);
            }
            Rid[] buffers = [batchMeta, inputOrdinals, planBuffer, targetBuffer, outputHeader, outputOrdinals];
            var prefixCache = SharedVisibleSeedGpuCache.Attach(rd, owned, ref source, ref buffers, "S.MerchantShopColorless");
            Rid shader = Add(FamilyGpuComputeUtility.CompileShader(rd, source, "MerchantShopColorlessFamilyS"));
            Rid pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, "S.MerchantShopColorless"));
            if (!rd.ComputePipelineIsValid(pipeline))
                throw new InvalidOperationException("SFamilyGpuPipelineInvalid");
            Rid uniformSet = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers));

            Rid stablePhase1Pipeline = default;
            Rid stablePhase1UniformSet = default;
            Rid stableScatterPipeline = default;
            Rid stableScatterUniformSet = default;
            Rid stableKeepMasks = default;
            Rid stableBlockCounts = default;
            Rid stableBlockOffsets = default;
            if (stableEnabled)
            {
                stableKeepMasks = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, StableKeepMaskBytes));
                stableBlockCounts = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, StableBlockCountBytes));
                stableBlockOffsets = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, StableBlockOffsetBytes));

                string stablePhase1Source = EnableStableOrderedCompaction(source);
                if (prefixCache is not null)
                {
                    // Stable phase 1 already owns bindings 6/7. Only relocate
                    // the cache declaration; all phases borrow one owned table.
                    const string prefixBinding = "layout(set=0,binding=6,std430) readonly restrict buffer Rt2VisiblePrefix";
                    int bindingAt = stablePhase1Source.IndexOf(prefixBinding, StringComparison.Ordinal);
                    if (bindingAt < 0 || bindingAt != stablePhase1Source.LastIndexOf(prefixBinding, StringComparison.Ordinal))
                        throw new InvalidOperationException("S.SharedPrefixStableBindingSeamChanged");
                    stablePhase1Source = stablePhase1Source.Replace(prefixBinding,
                        "layout(set=0,binding=8,std430) readonly restrict buffer Rt2VisiblePrefix", StringComparison.Ordinal);
                }
                Rid stablePhase1Shader = Add(FamilyGpuComputeUtility.CompileShader(
                    rd, stablePhase1Source, "MerchantShopColorlessFamilySStablePhase1"));
                stablePhase1Pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, stablePhase1Shader, "S.StablePhase1"));
                if (!rd.ComputePipelineIsValid(stablePhase1Pipeline))
                    throw new InvalidOperationException("SFamilyStablePhase1PipelineInvalid");
                Rid[] stableBuffers =
                {
                    batchMeta, inputOrdinals, planBuffer, targetBuffer, outputHeader, outputOrdinals,
                    stableKeepMasks, stableBlockCounts
                };
                if (prefixCache is not null) stableBuffers = [.. stableBuffers, prefixCache.Buffer];
                stablePhase1UniformSet = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, stablePhase1Shader, stableBuffers));

                string scatterSource = FamilyGpuComputeUtility.LoadEmbeddedShader(StableScatterShaderSuffix);
                Rid scatterShader = Add(FamilyGpuComputeUtility.CompileShader(
                    rd, scatterSource, "MerchantShopColorlessFamilySStableScatter"));
                stableScatterPipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, scatterShader, "S.StableScatter"));
                if (!rd.ComputePipelineIsValid(stableScatterPipeline))
                    throw new InvalidOperationException("SFamilyStableScatterPipelineInvalid");
                stableScatterUniformSet = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, scatterShader, new[]
                {
                    batchMeta, stableKeepMasks, stableBlockOffsets, outputHeader, outputOrdinals
                }));
            }

            timer.Stop();
            var result = new MerchantShopColorlessGpuExecutor(
                rd, pipeline, batchMeta, inputOrdinals, outputHeader, outputOrdinals,
                uniformSet, stableEnabled, stablePhase1Pipeline, stablePhase1UniformSet,
                stableScatterPipeline, stableScatterUniformSet, stableKeepMasks, stableBlockCounts,
                stableBlockOffsets, planTag, Thread.CurrentThread.ManagedThreadId,
                timer.Elapsed.TotalMilliseconds, rd.GetDeviceName()?.Trim() ?? "unknown", orderedSequenceSlots);
            result._privateInput = privateInput; result._privateOutput = privateOutput;
            result._prefixCache = prefixCache;
            result._owned.AddRange(owned);
            owned.Clear();
            Bootstrap.RuntimeLog.TryBackgroundInfo($"sOrderedSequenceSlots=true;requested={orderedSequenceSlots.Requested};" +
                $"enabled={orderedSequenceSlots.Applied};revision={orderedSequenceSlots.RevisionSuffix};" +
                $"expandedSequences={orderedSequenceSlots.ExpandedSequences};addedSlots={orderedSequenceSlots.AddedSlots};" +
                $"originalMaximumOrdinal={orderedSequenceSlots.OriginalMaximumOrdinal};packedMaximumOrdinal={orderedSequenceSlots.PackedMaximumOrdinal};" +
                $"reason={orderedSequenceSlots.Reason};shaderPredicateAndRngBodyUnchanged=true;physicalRevision={result.PhysicalImplementationRevision}");
            return result;
        }
        catch (Exception ex)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(ex,
                () => FamilyGpuComputeUtility.FreeAll(rd, owned), "S.Initialize");
            throw;
        }
    }

    public FamilyCandidateSet Execute(FamilyCandidateSet input, CancellationToken token, out MerchantShopColorlessGpuBatchMetrics metrics)
    {
        if (_privateInput is not null || _privateOutput is not null) throw new InvalidOperationException("S.PrivateEntryRequired");
        double prefixUploadMs = RefreshPrefixCache(input.Batch, input.Count, token);
        // Public output has the same bounded capacity as compact upload. Dense
        // inputs also need windows: legal small/modded pools can pass every root.
        FamilyCandidateSet result = input.Count > SurvivorCapacity && input.Count <= Capacity
            ? ExecutePublicWindows(input, token, out metrics)
            : ExecuteCore(input, input.Count, token, out metrics)!;
        metrics = IncludePrefixUpload(metrics, prefixUploadMs);
        return result;
    }
    internal FamilyCandidateSet ExecutePrivate(SearchBatch batch, int count, CancellationToken token, out MerchantShopColorlessGpuBatchMetrics metrics)
    {
        if (_privateInput is null || _privateOutput is not null) throw new InvalidOperationException("S.PrivateTerminalPortsRequired");
        _privateInput.CheckPopulation(batch, count);
        double prefixUploadMs = RefreshPrefixCache(batch, count, token);
        FamilyCandidateSet result = ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics)!;
        metrics = IncludePrefixUpload(metrics, prefixUploadMs);
        return result;
    }
    internal int ExecutePrivateStage(SearchBatch batch, int count, CancellationToken token, out MerchantShopColorlessGpuBatchMetrics metrics)
    {
        if (_privateOutput is null || (_privateInput is null && count != batch.BatchCandidateCount)) throw new InvalidOperationException("S.PrivateStagePortsRequired");
        _privateOutput.CheckPopulation(batch, count); _privateInput?.CheckPopulation(batch, count);
        double prefixUploadMs = RefreshPrefixCache(batch, count, token);
        ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics);
        metrics = IncludePrefixUpload(metrics, prefixUploadMs);
        return metrics.Survivors;
    }

    private double RefreshPrefixCache(SearchBatch batch, int count, CancellationToken token)
    {
        AssertOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_prefixCache is null || count == 0) return 0d;
        // Dense public windows rebase their metadata, while this pure hash table
        // continues to cover the original batch's absolute ordinal range.
        var timer = Stopwatch.StartNew();
        _prefixCache.Refresh(batch);
        return timer.Elapsed.TotalMilliseconds;
    }

    private static MerchantShopColorlessGpuBatchMetrics IncludePrefixUpload(MerchantShopColorlessGpuBatchMetrics metrics, double elapsedMs) =>
        metrics with
        {
            UploadMs = metrics.UploadMs + elapsedMs,
            CanonicalAbi1ReadyMs = metrics.CanonicalAbi1ReadyMs + elapsedMs
        };
    private FamilyCandidateSet? ExecuteCore(FamilyCandidateSet input, int inputCount, CancellationToken cancellationToken, out MerchantShopColorlessGpuBatchMetrics metrics)
    {
        AssertOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (inputCount == 0)
        {
            metrics = new MerchantShopColorlessGpuBatchMetrics();
            return FamilyCandidateSet.FromSortedAbi1(input.Batch, Array.Empty<ulong>());
        }
        if (inputCount > Capacity)
            throw new InvalidOperationException("SFamilyGpuInputExceedsPhysicalBatch");
        if (!input.IsDense && inputCount > CompactInputCapacity)
            throw new InvalidOperationException("SFamilyCompactInputExceedsTransferCapacity");
        if (_stableEnabled && input.IsDense)
            return ExecuteStableOrdered(input, cancellationToken, out metrics);

        var canonicalTimer = Stopwatch.StartNew();
        var timer = Stopwatch.StartNew();
        uint[] meta =
        {
            unchecked((uint)input.Batch.BatchBase),
            unchecked((uint)(input.Batch.BatchBase >> 32)),
            checked((uint)input.Batch.BatchCandidateCount),
            checked((uint)inputCount),
            input.IsDense && _privateInput is null ? 0u : 1u,
            (uint)OutputCapacity
        };
        FamilyGpuComputeUtility.Update(_rd, _batchMeta, meta, "SFamilyBatchMeta");
        FamilyGpuComputeUtility.Update(_rd, _outputHeader, CreateHeader(_planTag), "SFamilyOutputHeader");
        if (!input.IsDense && _privateInput is null)
        {
            ReadOnlySpan<ulong> ordinals = input.ExportAbi1().Span;
            var packed = new uint[checked(ordinals.Length * 2)];
            for (int index = 0; index < ordinals.Length; index++)
            {
                packed[index * 2] = unchecked((uint)ordinals[index]);
                packed[index * 2 + 1] = unchecked((uint)(ordinals[index] >> 32));
            }
            FamilyGpuComputeUtility.Update(_rd, _inputOrdinals, packed, "SFamilyCompactInput");
        }
        timer.Stop();
        double uploadMs = timer.Elapsed.TotalMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();
        timer.Restart();
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, _uniformSet, 0);
        int candidatesPerGroup = WorkgroupSize * SeedsPerInvocation;
        uint groups = checked((uint)((inputCount + candidatesPerGroup - 1) / candidatesPerGroup));
        _rd.ComputeListDispatch(list, groups, 1u, 1u);
        _rd.ComputeListEnd();
        timer.Stop();
        double commandMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        uint[] header = _headerReadback.CompleteRecorded(_outputHeader, HeaderUIntCount,
            true, "S.MerchantShopColorless", static () => { });
        timer.Stop();
        double submitMs = _headerReadback.LastSubmitMs, syncMs = _headerReadback.LastSyncMs;
        double headerTransportMs = Math.Max(0, timer.Elapsed.TotalMilliseconds - submitMs - syncMs);
        cancellationToken.ThrowIfCancellationRequested();

        timer.Restart();
        long headerReadBytes = (long)header.Length * sizeof(uint);
        int processedCount = ValidateHeader(header, inputCount);
        int survivorCount = checked((int)header[2]);
        timer.Stop();
        double headerReadbackMs = headerTransportMs + timer.Elapsed.TotalMilliseconds;

        if (_privateOutput is not null)
        {
            metrics = new MerchantShopColorlessGpuBatchMetrics
            {
                CompactionPath = MerchantShopColorlessCompactionPath.AtomicAppendHostSort,
                InputCandidates = inputCount, ProcessedCandidates = processedCount, Survivors = survivorCount, Dispatches = 1,
                UploadMs = uploadMs, CommandMs = commandMs, SubmitMs = submitMs, SyncMs = syncMs,
                HeaderReadbackMs = headerReadbackMs, HeaderReadbackBytes = headerReadBytes,
                CanonicalAbi1ReadyMs = canonicalTimer.Elapsed.TotalMilliseconds
            };
            return null;
        }
        long payloadAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        byte[] rawOrdinals = survivorCount == 0
            ? Array.Empty<byte>()
            : _rd.BufferGetData(_outputOrdinals, 0u, checked((uint)(survivorCount * 2 * sizeof(uint))));
        timer.Stop();
        double payloadReadbackMs = timer.Elapsed.TotalMilliseconds;
        long payloadReadbackAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - payloadAllocationBefore;

        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        var survivors = new ulong[survivorCount];
        timer.Stop();
        double typedAllocationMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        if (rawOrdinals.Length > 0)
            Buffer.BlockCopy(rawOrdinals, 0, survivors, 0, rawOrdinals.Length);
        timer.Stop();
        double decodePayloadMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        if (survivors.Length > 1) Array.Sort(survivors);
        timer.Stop();
        double sortMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        FamilyCandidateSet result = FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors);
        timer.Stop();
        double validationCandidateSetMs = timer.Elapsed.TotalMilliseconds;
        long hostAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        canonicalTimer.Stop();
        metrics = new MerchantShopColorlessGpuBatchMetrics(
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort,
            inputCount, processedCount, survivors.Length,
            uploadMs, commandMs, submitMs, syncMs,
            headerReadbackMs, payloadReadbackMs,
            typedAllocationMs, decodePayloadMs, sortMs, validationCandidateSetMs,
            payloadReadbackAllocatedBytes, hostAllocatedBytes,
            headerReadBytes, rawOrdinals.LongLength,
            0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0L,
            canonicalTimer.Elapsed.TotalMilliseconds, 1);
        return result;
    }

    private FamilyCandidateSet ExecuteStableOrdered(
        FamilyCandidateSet input,
        CancellationToken cancellationToken,
        out MerchantShopColorlessGpuBatchMetrics metrics)
    {
        if (!input.IsDense) throw new InvalidOperationException("SFamilyStableCompactionRequiresDenseInput");
        var canonicalTimer = Stopwatch.StartNew();
        var timer = Stopwatch.StartNew();
        uint[] meta =
        {
            unchecked((uint)input.Batch.BatchBase),
            unchecked((uint)(input.Batch.BatchBase >> 32)),
            checked((uint)input.Batch.BatchCandidateCount),
            checked((uint)input.Count),
            0u,
            SurvivorCapacity
        };
        FamilyGpuComputeUtility.Update(_rd, _batchMeta, meta, "SFamilyStableBatchMeta");
        FamilyGpuComputeUtility.Update(_rd, _outputHeader, CreateHeader(_planTag), "SFamilyStableOutputHeader");
        timer.Stop();
        double uploadMs = timer.Elapsed.TotalMilliseconds;

        int candidatesPerGroup = WorkgroupSize * SeedsPerInvocation;
        uint groups = checked((uint)((input.Count + candidatesPerGroup - 1) / candidatesPerGroup));
        cancellationToken.ThrowIfCancellationRequested();
        timer.Restart();
        long phase1List = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(phase1List, _stablePhase1Pipeline);
        _rd.ComputeListBindUniformSet(phase1List, _stablePhase1UniformSet, 0);
        _rd.ComputeListDispatch(phase1List, groups, 1u, 1u);
        _rd.ComputeListEnd();
        timer.Stop();
        double phase1CommandMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        uint[] phase1Header = _headerReadback.CompleteRecorded(_outputHeader, HeaderUIntCount,
            true, "S.StablePhase1", static () => { });
        timer.Stop();
        double phase1SubmitMs = _headerReadback.LastSubmitMs, phase1SyncMs = _headerReadback.LastSyncMs;
        double phase1HeaderTransportMs = Math.Max(0, timer.Elapsed.TotalMilliseconds - phase1SubmitMs - phase1SyncMs);
        cancellationToken.ThrowIfCancellationRequested();

        timer.Restart();
        long phase1HeaderReadBytes = (long)phase1Header.Length * sizeof(uint);
        int processedCount = ValidateHeader(phase1Header, input.Count);
        int survivorCount = checked((int)phase1Header[2]);
        timer.Stop();
        double phase1HeaderReadbackMs = phase1HeaderTransportMs + timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        byte[] rawCounts = _rd.BufferGetData(_stableBlockCounts, 0u, checked(groups * sizeof(uint)));
        uint[] counts = FamilyGpuComputeUtility.FromUInt32Bytes(rawCounts);
        timer.Stop();
        double countsReadbackMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        var offsets = new uint[counts.Length];
        ulong total = 0UL;
        for (int index = 0; index < counts.Length; index++)
        {
            offsets[index] = checked((uint)total);
            total += counts[index];
            if (total > SurvivorCapacity)
                throw new InvalidDataException("SFamilyStablePrefixExceedsSurvivorCapacity");
        }
        if (total != checked((uint)survivorCount))
        {
            throw new InvalidDataException(
                $"SFamilyStablePrefixCountMismatch:prefix={total};header={survivorCount};blocks={counts.Length}");
        }
        timer.Stop();
        double hostPrefixMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        FamilyGpuComputeUtility.Update(_rd, _stableBlockOffsets, offsets, "SFamilyStableBlockOffsets");
        timer.Stop();
        double offsetsUploadMs = timer.Elapsed.TotalMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        timer.Restart();
        long scatterList = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(scatterList, _stableScatterPipeline);
        _rd.ComputeListBindUniformSet(scatterList, _stableScatterUniformSet, 0);
        _rd.ComputeListDispatch(scatterList, groups, 1u, 1u);
        _rd.ComputeListEnd();
        timer.Stop();
        double scatterCommandMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        uint[] finalHeader = _headerReadback.CompleteRecorded(_outputHeader, HeaderUIntCount,
            true, "S.StableScatter", static () => { });
        timer.Stop();
        double scatterSubmitMs = _headerReadback.LastSubmitMs, scatterSyncMs = _headerReadback.LastSyncMs;
        double finalHeaderTransportMs = Math.Max(0, timer.Elapsed.TotalMilliseconds - scatterSubmitMs - scatterSyncMs);
        cancellationToken.ThrowIfCancellationRequested();

        timer.Restart();
        long finalHeaderReadBytes = (long)finalHeader.Length * sizeof(uint);
        ValidateHeader(finalHeader, input.Count);
        if (checked((int)finalHeader[2]) != survivorCount)
            throw new InvalidDataException("SFamilyStableScatterChangedSurvivorCount");
        timer.Stop();
        double finalHeaderReadbackMs = finalHeaderTransportMs + timer.Elapsed.TotalMilliseconds;

        long payloadAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        byte[] rawOrdinals = survivorCount == 0
            ? Array.Empty<byte>()
            : _rd.BufferGetData(_outputOrdinals, 0u, checked((uint)(survivorCount * 2 * sizeof(uint))));
        timer.Stop();
        double payloadReadbackMs = timer.Elapsed.TotalMilliseconds;
        long payloadReadbackAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - payloadAllocationBefore;

        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        timer.Restart();
        var survivors = new ulong[survivorCount];
        timer.Stop();
        double typedAllocationMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        if (rawOrdinals.Length > 0)
            Buffer.BlockCopy(rawOrdinals, 0, survivors, 0, rawOrdinals.Length);
        timer.Stop();
        double decodePayloadMs = timer.Elapsed.TotalMilliseconds;

        // Stable scatter is constructive; the retained O(N) ABI1 validator below
        // remains the fail-closed range/order/duplicate authority for this experiment.
        timer.Restart();
        FamilyCandidateSet result = FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors);
        timer.Stop();
        double validationCandidateSetMs = timer.Elapsed.TotalMilliseconds;
        long hostAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        canonicalTimer.Stop();

        metrics = new MerchantShopColorlessGpuBatchMetrics(
            MerchantShopColorlessCompactionPath.StableOrderedCompaction,
            input.Count, processedCount, survivors.Length,
            uploadMs,
            phase1CommandMs + scatterCommandMs,
            phase1SubmitMs + scatterSubmitMs,
            phase1SyncMs + scatterSyncMs,
            phase1HeaderReadbackMs + finalHeaderReadbackMs,
            payloadReadbackMs,
            typedAllocationMs,
            decodePayloadMs,
            0d,
            validationCandidateSetMs,
            payloadReadbackAllocatedBytes,
            hostAllocatedBytes,
            phase1HeaderReadBytes + finalHeaderReadBytes,
            rawOrdinals.LongLength,
            phase1CommandMs,
            phase1SubmitMs,
            phase1SyncMs,
            countsReadbackMs,
            hostPrefixMs,
            offsetsUploadMs,
            scatterCommandMs,
            scatterSubmitMs,
            scatterSyncMs,
            rawCounts.LongLength,
            canonicalTimer.Elapsed.TotalMilliseconds, 2);
        return result;
    }

    public void Dispose()
    {
        AssertOwnerThread();
        if (_disposed) return;
        _disposed = true;
        // Execute completes each Submit/Sync pair before returning.
        FamilyGpuComputeUtility.FreeAll(_rd, _owned);
    }

    private void AssertOwnerThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("SFamilyGpuOwnerThreadMismatch");
    }

    private int ValidateHeader(uint[] header, int maximumProcessed)
    {
        if (header.Length < HeaderUIntCount || header[0] != HeaderMagic || header[1] != HeaderAbi || header[5] != _planTag)
            throw new InvalidDataException("SFamilyGpuHeaderIdentityMismatch");
        int output = checked((int)header[2]);
        int overflow = checked((int)header[3]);
        int processed = checked((int)header[4]);
        if (overflow != 0 || output < 0 || output > processed || processed != maximumProcessed || output > OutputCapacity)
        {
            throw new InvalidDataException(
                $"SFamilyGpuHeaderInvalid:output={output};processed={processed};max={maximumProcessed};" +
                $"overflow={overflow};survivorCapacity={SurvivorCapacity}");
        }
        return processed;
    }

    private static uint[] CreateHeader(uint planTag) =>
        [HeaderMagic, HeaderAbi, 0u, 0u, 0u, planTag, 0u, 0u];

    private static string EnableStableOrderedCompaction(string source)
    {
        const string version = "#version 450";
        if (!source.StartsWith(version, StringComparison.Ordinal))
            throw new InvalidOperationException("SFamilyStableShaderVersionMarkerMissing");
        return source.Insert(version.Length, "\n#define RT2_S_STABLE_ORDERED_COMPACTION 1");
    }

    private static uint[] PackPlan(
        ExactSearchEvaluationProjection evaluation,
        Beta111MerchantColorlessAuthority authority,
        out uint[] targets)
    {
        ulong shopsHash = XxHash64.Hash("shops"u8, 0UL);
        var output = new List<uint> { 0u, 0u, 0u, 0u, 0u, 0u, 0u };
        var targetValues = new List<uint>();
        foreach (MerchantColorlessSlotCondition condition in evaluation.MerchantColorlessConditions)
        {
            IReadOnlyList<ModelKey> pool = condition.Slot == MerchantColorlessSlot.Uncommon
                ? authority.UncommonPool
                : authority.RarePool;
            output.Add(checked((uint)condition.MerchantOrdinal));
            output.Add(condition.Slot == MerchantColorlessSlot.Uncommon ? 0u : 1u);
            int target = IndexOf(pool, condition.TargetCardKey);
            output.Add(target < 0 ? ushort.MaxValue : checked((uint)target));
        }
        foreach (MerchantColorlessSequenceSearchCondition condition in evaluation.MerchantColorlessSequenceConditions)
        {
            IReadOnlyList<ModelKey> pool = condition.Slot == MerchantColorlessSlot.Uncommon
                ? authority.UncommonPool
                : authority.RarePool;
            output.Add(condition.Slot == MerchantColorlessSlot.Uncommon ? 0u : 1u);
            output.Add(checked((uint)condition.Count));
            output.Add(condition.OrderMode == CombatRewardSequenceOrderMode.Ordered ? 0u : 1u);
            output.Add(checked((uint)targetValues.Count));
            output.Add(checked((uint)condition.Slots.Count));
            targetValues.AddRange(condition.Slots.Select(key =>
            {
                if (!key.HasValue) return (uint)ushort.MaxValue;
                int target = IndexOf(pool, key.Value);
                return target < 0 ? (uint)ushort.MaxValue : checked((uint)target);
            }));
        }
        output[0] = checked((uint)evaluation.MerchantColorlessConditions.Count);
        output[1] = checked((uint)evaluation.MerchantColorlessSequenceConditions.Count);
        output[2] = checked((uint)authority.UncommonPool.Count);
        output[3] = checked((uint)authority.RarePool.Count);
        output[4] = checked((uint)authority.PlayerSlotIndex);
        output[5] = unchecked((uint)shopsHash);
        output[6] = unchecked((uint)(shopsHash >> 32));
        targets = targetValues.ToArray();
        return output.ToArray();
    }

    private static int IndexOf(IReadOnlyList<ModelKey> values, ModelKey target)
    {
        for (int index = 0; index < values.Count; index++)
            if (values[index] == target) return index;
        return -1;
    }

    private static uint StableTag(string value) =>
        BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);
}
