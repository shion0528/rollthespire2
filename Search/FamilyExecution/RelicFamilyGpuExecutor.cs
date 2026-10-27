using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Godot;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

internal readonly record struct RelicFamilyGpuBatchMetrics(
    int InputCandidates,
    int ProcessedCandidates,
    int Survivors,
    uint DispatchGroups,
    double MetadataUploadMs,
    double CompactPackAllocationMs,
    double CompactPackConvertValidateMs,
    long CompactPackAllocatedBytes,
    double CompactUploadStagingMs,
    long CompactUploadStagingAllocatedBytes,
    double CompactBufferUpdateMs,
    double CommandMs,
    double SubmitMs,
    double SyncMs,
    double ReadbackMs,
    long ReadbackBytes,
    double CanonicalAbi1ReadyMs)
{
    public double CompactPackMs => CompactPackAllocationMs + CompactPackConvertValidateMs;
    public double CompactUploadMs => CompactUploadStagingMs + CompactBufferUpdateMs;
    public double UploadMs => MetadataUploadMs + CompactPackMs + CompactUploadMs;
    public double PhysicalMs => UploadMs + CommandMs + SubmitMs + SyncMs + ReadbackMs;
    public double CandidatesPerSecond => PhysicalMs <= 0d ? 0d : InputCandidates * 1000d / PhysicalMs;
}

/// <summary>
/// Family-local R physical implementation. Resources are prepared once and reused;
/// every method is invoked synchronously by the session GPU owner.
/// </summary>
internal sealed partial class RelicFamilyGpuExecutor : IDisposable
{
    internal const int WorkgroupSize = 64;
    internal const int SeedsPerInvocation = 8;
    internal const int Capacity = FamilyPhysicalExecutionDefaults.DefaultCandidateBatchSize;
    internal const int CompactInputCapacity = 1 << 22;
    internal const int OutputCapacity = Capacity;
    private const long MaximumWorkspaceBytes = 640L * 1024L * 1024L;
    private const uint HeaderMagic = 0x52464D52u; // "RFMR"
    private const uint HeaderAbi = 1u;
    private const int HeaderUIntCount = 8;
    private const string ShaderSuffix = "RelicFamily.comp.glsl";
    private const string RfullShaderSuffix = "RelicFullCapsule.glsl";
    private const string RfullBindingMarker = "/*__RT2_RFULL_BINDING__*/";
    private const string RfullFunctionsMarker = "/*__RT2_RFULL_FUNCTIONS__*/";
    private const string RfullFilterMarker = "/*__RT2_RFULL_FILTER__*/";

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
    private readonly uint _planTag;
    private readonly int _ownerThreadId;
    private bool _disposed;
    internal uint LastRouteA { get; private set; }
    internal uint LastRouteB { get; private set; }
    internal uint LastEarlyHeavy { get; private set; }
    internal uint LastLateHeavy { get; private set; }
    private PrivateOrdinalBuffer? _privateInput, _privateOutput;
    private SharedVisibleSeedGpuCache? _prefixCache;
    private bool _bonesKBoundary;
    internal uint LastEarlyPass { get; private set; }
    internal uint LastLatePass { get; private set; }
    private int ActiveHeaderUIntCount => _bonesKBoundary ? 12 : HeaderUIntCount;

    private RelicFamilyGpuExecutor(
        RenderingDevice rd,
        Rid pipeline,
        Rid batchMeta,
        Rid inputOrdinals,
        Rid outputHeader,
        Rid outputOrdinals,
        Rid uniformSet,
        uint planTag,
        int ownerThreadId,
        double setupMs,
        string deviceName,
        long workspaceBytes)
    {
        _rd = rd;
        _headerReadback = new(rd, FamilyGpuHeaderReadback.Requested);
        _pipeline = pipeline;
        _batchMeta = batchMeta;
        _inputOrdinals = inputOrdinals;
        _outputHeader = outputHeader;
        _outputOrdinals = outputOrdinals;
        _uniformSet = uniformSet;
        _planTag = planTag;
        _ownerThreadId = ownerThreadId;
        SetupMs = setupMs;
        DeviceName = deviceName;
        WorkspaceBytes = workspaceBytes;
    }

    public double SetupMs { get; }
    public string DeviceName { get; }
    internal FamilyPrivateGpuExecution BindPrivateSerial() =>
        new(this, DeviceName, SetupMs, _privateInput, _privateOutput, (batch, count, token) =>
        {
            FamilyCandidateSet? result = null; RelicFamilyGpuBatchMetrics m;
            if (_privateOutput is not null) ExecutePrivateStage(batch, count, token, out m);
            else result = _privateInput is null ? Execute(FamilyCandidateSet.Dense(batch), token, out m) : ExecutePrivate(batch, count, token, out m);
            return new(m.Survivors, result, m.CommandMs + m.SubmitMs + m.SyncMs,
                m.ReadbackMs, m.ReadbackBytes, m.CanonicalAbi1ReadyMs, count == 0 ? 0 : 1);
        });
    public long WorkspaceBytes { get; }
    public static long InputWorkspaceBytes => (long)CompactInputCapacity * sizeof(uint);
    public static long OutputWorkspaceBytes => (long)OutputCapacity * sizeof(uint);
    public static long ScratchWorkspaceBytes => 0;
    public static long CaptureWorkspaceBytes => 0;

    public static RelicFamilyGpuExecutor Create(RenderingDevice rd, ExactSearchExecutionRequest request, RelicFamilyPlan plan)
        => Create(rd, request, plan, privateInput: null, privateOutput: null);
    internal static RelicFamilyGpuExecutor Create(RenderingDevice rd, ExactSearchExecutionRequest request, RelicFamilyPlan plan,
        PrivateOrdinalBuffer? privateInput, PrivateOrdinalBuffer? privateOutput)
        => Create(rd, request, plan, fullPlan: null, privateInput: privateInput, privateOutput: privateOutput);

    internal static RelicFamilyGpuExecutor Create(
        RenderingDevice rd,
        ExactSearchExecutionRequest request,
        RelicFullGpuPlan fullPlan, PrivateOrdinalBuffer? privateInput = null, bool bonesKRouteMask = false) =>
        Create(rd, request, fullPlan, privateInput, bonesKRouteMask, null);

    internal static RelicFamilyGpuExecutor Create(RenderingDevice rd, ExactSearchExecutionRequest request,
        RelicFullGpuPlan fullPlan, PrivateOrdinalBuffer? privateInput, bool bonesKRouteMask, PrivateOrdinalBuffer? privateOutput) =>
        Create(rd, request, fullPlan.SequencePlan, fullPlan, privateInput, bonesKRouteMask, privateOutput);

    private static RelicFamilyGpuExecutor Create(
        RenderingDevice rd,
        ExactSearchExecutionRequest request,
        RelicFamilyPlan plan,
        RelicFullGpuPlan? fullPlan, PrivateOrdinalBuffer? privateInput = null, bool bonesKRouteMask = false, PrivateOrdinalBuffer? privateOutput = null)
    {
        if (privateInput is not null && ReferenceEquals(privateInput, privateOutput)) throw new ArgumentException("R.PrivateBuffersAlias");
        if (bonesKRouteMask && (privateInput is null || fullPlan?.UsesBonesKBoundary != true)) throw new InvalidOperationException("BonesK.MaskConsumerScope");
        ArgumentNullException.ThrowIfNull(rd);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        var timer = Stopwatch.StartNew();
        var owned = new List<Rid>();
        try
        {
            Rid Add(Rid rid) { owned.Add(rid); return rid; }
            uint planTag = StableTag(
                request.SnapshotFingerprint + "|" +
                (fullPlan is null ? FamilyPhysicalImplementationRevisions.Relic : RelicFullGpuPlan.AlgorithmRevision));
            uint[] planMeta = PackPlanMeta(plan);
            uint[] bucketMeta = PackBuckets(plan.Pool);
            uint[] predicates = PackPredicates(plan.Predicates);
            uint[] shopPredicates = PackShopPredicates(plan.ShopPredicates, out uint[] shopTargets);
            long inputBytes = InputWorkspaceBytes;
            long outputBytes = OutputWorkspaceBytes;
            long workspaceBytes = checked(inputBytes + outputBytes);
            if (inputBytes > int.MaxValue || outputBytes > int.MaxValue || workspaceBytes > MaximumWorkspaceBytes)
                throw new InvalidOperationException(
                    $"RFamilyWorkspaceUnsupported:bytes={workspaceBytes};max={MaximumWorkspaceBytes};" +
                    $"bucket={plan.Pool.MaxBucketLength};localState={RelicFamilyPlanCompiler.MaximumShaderLocalState}");

            Rid batchMeta = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 6 * sizeof(uint)));
            Rid inputOrdinals = privateInput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, checked((int)inputBytes)));
            Rid poolIds = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytes(plan.Pool.DenseRelicIds.Select(value => (uint)value).ToArray())));
            Rid entryFlags = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytes(plan.Pool.EntryFlags.Select(value => (uint)value).ToArray())));
            Rid buckets = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(bucketMeta)));
            Rid planBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(planMeta)));
            Rid predicateBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytesNonEmpty(predicates)));
            Rid predicateIds = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytesNonEmpty(plan.PredicateTargetIndexes.Select(value => (uint)value).ToArray())));
            Rid trackedInitialPositions = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytesNonEmpty(plan.TrackedInitialPositions.Select(value => (uint)value).ToArray())));
            Rid outputHeader = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytes(CreateHeader(planTag, fullPlan?.UsesBonesKBoundary == true))));
            Rid outputOrdinals = privateOutput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, checked((int)outputBytes)));
            Rid shopPredicateBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytesNonEmpty(shopPredicates)));
            Rid shopTargetBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd,
                FamilyGpuComputeUtility.ToBytesNonEmpty(shopTargets)));

            var buffers = new List<Rid>
            {
                batchMeta, inputOrdinals, poolIds, entryFlags, buckets, planBuffer, predicateBuffer,
                predicateIds, trackedInitialPositions, outputHeader, outputOrdinals, shopPredicateBuffer, shopTargetBuffer
            };
            if (fullPlan is not null)
                buffers.Add(Add(FamilyGpuComputeUtility.CreateStorageBuffer(
                    rd, FamilyGpuComputeUtility.ToBytesNonEmpty(fullPlan.CapsuleMetadata))));

            string source = ShaderSource(fullPlan is not null, fullPlan?.UsesBonesKBoundary == true, bonesKRouteMask,
                fullPlan?.GenericReplay == true, plan.LocalStateCapacity);
            if (privateInput is not null)
            {
                const string bounds = "if (logical >= batch_meta.values[2u]) return;";
                if (!source.Contains(bounds, StringComparison.Ordinal)) throw new InvalidOperationException("R.PrivateInputSeamChanged");
                // An impossible predicate still must not hide a corrupt borrowed ordinal.
                const string reject = "if (plan_meta.values[2u] != 0u) return;";
                if (!source.Contains(reject, StringComparison.Ordinal)) throw new InvalidOperationException("R.PrivateRejectSeamChanged");
                source = source.Replace(reject, string.Empty, StringComparison.Ordinal)
                    .Replace(bounds, "if (logical >= batch_meta.values[2u]) { atomicExchange(output_header.values[3u],2u); return; } " + reject, StringComparison.Ordinal);
            }
            var prefixCache = SharedVisibleSeedGpuCache.Attach(rd, owned, ref source, buffers,
                fullPlan is null ? "R.Relic" : "R.RelicFull");
            if (prefixCache is not null) workspaceBytes = checked(workspaceBytes + SharedVisibleSeedGpuCache.Bytes);
            Rid shader = Add(FamilyGpuComputeUtility.CompileShader(rd, source, fullPlan is null ? "RelicFamilyR" : "RelicFamilyRfull"));
            Rid pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, fullPlan is null ? "R.Relic" : "R.RelicFull"));
            if (!rd.ComputePipelineIsValid(pipeline)) throw new InvalidOperationException("RFamilyGpuPipelineInvalid");
            Rid uniformSet = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers));

            timer.Stop();
            var result = new RelicFamilyGpuExecutor(
                rd, pipeline, batchMeta, inputOrdinals, outputHeader, outputOrdinals, uniformSet,
                planTag, Thread.CurrentThread.ManagedThreadId, timer.Elapsed.TotalMilliseconds,
                rd.GetDeviceName()?.Trim() ?? "unknown", workspaceBytes);
            result._privateInput = privateInput; result._privateOutput = privateOutput;
            result._prefixCache = prefixCache;
            result._bonesKBoundary = fullPlan?.UsesBonesKBoundary == true;
            result._owned.AddRange(owned);
            owned.Clear();
            return result;
        }
        catch (Exception ex)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(ex,
                () => FamilyGpuComputeUtility.FreeAll(rd, owned), "R.Initialize");
            throw;
        }
    }

    internal static string ShaderSource(bool rfull, bool bonesKBoundary = false, bool bonesKRouteMask = false,
        bool genericReplay = false, int localStateCapacity = 64)
    {
        string source = FamilyGpuComputeUtility.LoadFamilyShaderWithVisibleSeedRootHash(ShaderSuffix);
        source = source.Replace("const int MAX_LOCAL_STATE = 64;", $"const int MAX_LOCAL_STATE = {localStateCapacity};", StringComparison.Ordinal);
        if (bonesKRouteMask) source = source.Replace(
            "uint logical = batch_meta.values[4u] == 0u ? invocation : input_ordinals.values[invocation];",
            "uint packed=input_ordinals.values[invocation]; nr_route_mask=packed>>24u; uint logical=packed&0xffffffu; if(nr_route_mask==0u || nr_route_mask>3u || logical>=batch_meta.values[2u]) { atomicExchange(output_header.values[3u],1u); return; }", StringComparison.Ordinal);
        string binding = rfull
            ? "layout(set = 0, binding = 13, std430) readonly restrict buffer RfullCapsuleMeta { uint values[]; } rfull_capsule;"
            : string.Empty;
        string functions = genericReplay ? RelicFullGpuPlan.GenericShaderFunctions() : rfull ? FamilyGpuComputeUtility.LoadEmbeddedShader(RfullShaderSuffix) : string.Empty;
        if (genericReplay) source = source.Replace("#version 450", "#version 450\n#extension GL_EXT_shader_explicit_arithmetic_types_float64 : require", StringComparison.Ordinal);
        if (bonesKBoundary) functions = "#define RFULL_TRACK_HEAVY_ENTRY\n#define BONES_K_MASK " + (bonesKRouteMask ? "1" : "0") + "\nuint nr_heavy_entries; uint nr_route_mask;\n" + functions.Replace(
            "return rfull_prepare_rewards(root) && rfull_capsule_after_rewards(root);",
            FamilyGpuComputeUtility.LoadEmbeddedShader("RelicFullBonesKBoundary.glsl"), StringComparison.Ordinal);
        string filter = rfull ? "if (!rfull_capsule_matches(root)) return;" : string.Empty;
        return source.Replace(RfullBindingMarker, binding, StringComparison.Ordinal)
            .Replace(RfullFunctionsMarker, functions, StringComparison.Ordinal)
            .Replace("/*__RT2_RFULL_SEQUENCE_BEGIN__*/", rfull
                ? "if (plan_meta.values[3u] != 0u || plan_meta.values[4u] != 0u) {" : string.Empty, StringComparison.Ordinal)
            .Replace("/*__RT2_RFULL_SEQUENCE_END__*/", rfull ? "}" : string.Empty, StringComparison.Ordinal)
            .Replace(RfullFilterMarker, filter, StringComparison.Ordinal);
    }

    internal static uint[][] PackRfullFixtureBuffers(RelicFullGpuPlan fullPlan)
    {
        RelicFamilyPlan plan = fullPlan.SequencePlan;
        uint[] shopPredicates = PackShopPredicates(plan.ShopPredicates, out uint[] shopTargets);
        return
        [
            plan.Pool.DenseRelicIds.Select(value => (uint)value).ToArray(),
            plan.Pool.EntryFlags.Select(value => (uint)value).ToArray(),
            PackBuckets(plan.Pool),
            PackPlanMeta(plan),
            PackPredicates(plan.Predicates),
            plan.PredicateTargetIndexes.Select(value => (uint)value).ToArray(),
            plan.TrackedInitialPositions.Select(value => (uint)value).ToArray(),
            shopPredicates,
            shopTargets,
            fullPlan.CapsuleMetadata
        ];
    }

    public FamilyCandidateSet Execute(
        FamilyCandidateSet input,
        CancellationToken cancellationToken,
        out RelicFamilyGpuBatchMetrics metrics)
    {
        if (_privateInput is not null || _privateOutput is not null) throw new InvalidOperationException("R.PrivateTransportRequiresPrivateEntry");
        double prefixUploadMs = RefreshPrefixCache(input.Batch, input.Count, cancellationToken);
        FamilyCandidateSet result = !input.IsDense && input.Count > CompactInputCapacity && input.Count <= Capacity
            ? ExecutePublicCompactWindows(input, cancellationToken, out metrics)
            : ExecuteCore(input, input.Count, cancellationToken, out metrics)!;
        metrics = IncludePrefixUpload(metrics, prefixUploadMs);
        return result;
    }

    internal FamilyCandidateSet ExecutePrivate(SearchBatch batch, int count, CancellationToken token,
        out RelicFamilyGpuBatchMetrics metrics)
    {
        if (_privateInput is null || _privateOutput is not null) throw new InvalidOperationException("R.PrivateTerminalPortsRequired");
        _privateInput.CheckPopulation(batch, count);
        double prefixUploadMs = RefreshPrefixCache(batch, count, token);
        FamilyCandidateSet result = ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics)!;
        metrics = IncludePrefixUpload(metrics, prefixUploadMs);
        return result;
    }

    internal int ExecutePrivateStage(SearchBatch batch, int count, CancellationToken token, out RelicFamilyGpuBatchMetrics metrics)
    {
        if (_privateOutput is null || (_privateInput is null && count != batch.BatchCandidateCount)) throw new InvalidOperationException("R.PrivateStagePortsRequired");
        _privateOutput.CheckPopulation(batch, count);
        _privateInput?.CheckPopulation(batch, count);
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
        var timer = Stopwatch.StartNew();
        _prefixCache.Refresh(batch);
        return timer.Elapsed.TotalMilliseconds;
    }

    private static RelicFamilyGpuBatchMetrics IncludePrefixUpload(RelicFamilyGpuBatchMetrics metrics, double elapsedMs) =>
        metrics with
        {
            MetadataUploadMs = metrics.MetadataUploadMs + elapsedMs,
            CanonicalAbi1ReadyMs = metrics.CanonicalAbi1ReadyMs + elapsedMs
        };

    private FamilyCandidateSet? ExecuteCore(FamilyCandidateSet input, int inputCount,
        CancellationToken cancellationToken, out RelicFamilyGpuBatchMetrics metrics)
    {
        AssertOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        LastRouteA = LastRouteB = LastEarlyHeavy = LastLateHeavy = LastEarlyPass = LastLatePass = 0;
        if (inputCount == 0)
        {
            metrics = new RelicFamilyGpuBatchMetrics();
            return FamilyCandidateSet.FromSortedAbi1(input.Batch, []);
        }
        if (inputCount > Capacity) throw new InvalidOperationException("RFamilyGpuInputExceedsCapacity");
        if (!input.IsDense && inputCount > CompactInputCapacity)
            throw new InvalidOperationException("RFamilyCompactInputExceedsTransferCapacity");

        var canonicalTimer = Stopwatch.StartNew();
        var timer = Stopwatch.StartNew();
        uint[] meta =
        [
            unchecked((uint)input.Batch.BatchBase), unchecked((uint)(input.Batch.BatchBase >> 32)),
            checked((uint)input.Batch.BatchCandidateCount), checked((uint)inputCount),
            input.IsDense && _privateInput is null ? 0u : 1u, OutputCapacity
        ];
        FamilyGpuComputeUtility.Update(_rd, _batchMeta, meta, "RFamilyBatchMeta");
        FamilyGpuComputeUtility.Update(_rd, _outputHeader, CreateHeader(_planTag, _bonesKBoundary), "RFamilyOutputHeader");
        timer.Stop();
        double metadataUploadMs = timer.Elapsed.TotalMilliseconds;
        double compactPackAllocationMs = 0d;
        double compactPackConvertValidateMs = 0d;
        long compactPackAllocatedBytes = 0L;
        double compactUploadStagingMs = 0d;
        long compactUploadStagingAllocatedBytes = 0L;
        double compactBufferUpdateMs = 0d;
        if (!input.IsDense && _privateInput is null)
        {
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            timer.Restart();
            ReadOnlySpan<ulong> ordinals = input.ExportAbi1().Span;
            var packed = new uint[ordinals.Length];
            timer.Stop();
            compactPackAllocationMs = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            for (int index = 0; index < ordinals.Length; index++)
                packed[index] = checked((uint)ordinals[index]);
            timer.Stop();
            compactPackConvertValidateMs = timer.Elapsed.TotalMilliseconds;
            compactPackAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

            allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            timer.Restart();
            byte[] packedBytes = FamilyGpuComputeUtility.ToBytes(packed);
            timer.Stop();
            compactUploadStagingMs = timer.Elapsed.TotalMilliseconds;
            compactUploadStagingAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

            timer.Restart();
            FamilyGpuComputeUtility.UpdateBytes(_rd, _inputOrdinals, packedBytes, "RFamilyCompactInput");
            timer.Stop();
            compactBufferUpdateMs = timer.Elapsed.TotalMilliseconds;
        }

        cancellationToken.ThrowIfCancellationRequested();
        timer.Restart();
        long list = _rd.ComputeListBegin();
        int candidatesPerGroup = WorkgroupSize * SeedsPerInvocation;
        uint groups = checked((uint)((inputCount + candidatesPerGroup - 1) / candidatesPerGroup));
        try
        {
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, _uniformSet, 0);
            _rd.ComputeListDispatch(list, groups, 1u, 1u);
        }
        finally { _rd.ComputeListEnd(); }
        timer.Stop();
        double commandMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        uint[] header = _headerReadback.CompleteRecorded(_outputHeader, checked((uint)ActiveHeaderUIntCount),
            true, "R.Relic", static () => { });
        timer.Stop();
        double submitMs = _headerReadback.LastSubmitMs, syncMs = _headerReadback.LastSyncMs;
        double headerTransportMs = Math.Max(0, timer.Elapsed.TotalMilliseconds - submitMs - syncMs);
        cancellationToken.ThrowIfCancellationRequested();

        timer.Restart();
        long headerReadBytes = (long)header.Length * sizeof(uint);
        int processedCount = ValidateHeader(header, inputCount);
        LastEarlyHeavy = header[6]; LastLateHeavy = header[7];
        if (_bonesKBoundary) { LastEarlyPass = header[8]; LastLatePass = header[9]; LastRouteA=header[10]; LastRouteB=header[11]; }
        if (_bonesKBoundary && (header[10] > inputCount || header[11] > inputCount || header[6] > header[11] || header[7] > header[10] || header[6] > inputCount || header[7] > inputCount ||
            header[8] > header[6] || header[9] > header[7] || header[2] < Math.Max(header[8], header[9]) ||
            header[2] > (ulong)header[8] + header[9])) throw new InvalidDataException("R.BonesBoundaryRouteCountersInvalid");
        int survivorCount = checked((int)header[2]);
        byte[] rawOrdinals = _privateOutput is not null || survivorCount == 0 ? [] : _rd.BufferGetData(
            _outputOrdinals, 0u, checked((uint)(survivorCount * sizeof(uint))));
        timer.Stop();
        FamilyCandidateSet? result = null;
        if (_privateOutput is null)
        {
            if (rawOrdinals.Length != survivorCount * sizeof(uint)) throw new InvalidDataException("R.PayloadLengthMismatch");
            var survivors = FamilyGpuComputeUtility.FromUInt32Bytes(rawOrdinals).Select(x => (ulong)x).ToArray();
            if (survivors.Length > 1) Array.Sort(survivors);
            result = FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors);
        }
        canonicalTimer.Stop();
        metrics = new RelicFamilyGpuBatchMetrics(
            inputCount, processedCount, survivorCount, groups,
            metadataUploadMs,
            compactPackAllocationMs, compactPackConvertValidateMs, compactPackAllocatedBytes,
            compactUploadStagingMs, compactUploadStagingAllocatedBytes, compactBufferUpdateMs,
            commandMs, submitMs, syncMs,
            headerTransportMs + timer.Elapsed.TotalMilliseconds, headerReadBytes + rawOrdinals.LongLength,
            canonicalTimer.Elapsed.TotalMilliseconds);
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
            throw new InvalidOperationException("RFamilyGpuOwnerThreadMismatch");
    }

    private int ValidateHeader(uint[] header, int inputCount)
    {
        if (header.Length != ActiveHeaderUIntCount || header[0] != HeaderMagic || header[1] != HeaderAbi || header[5] != _planTag)
            throw new InvalidDataException("RFamilyGpuHeaderIdentityMismatch");
        int survivors = checked((int)header[2]);
        int overflow = checked((int)header[3]);
        int processed = checked((int)header[4]);
        if (overflow != 0 || survivors < 0 || survivors > processed || processed != inputCount || survivors > OutputCapacity)
            throw new InvalidDataException(
                $"RFamilyGpuHeaderInvalid:survivors={survivors};processed={processed};input={inputCount};overflow={overflow}");
        return processed;
    }

    private static uint[] CreateHeader(uint planTag, bool bonesKBoundary = false) => bonesKBoundary
        ? [HeaderMagic, HeaderAbi, 0u, 0u, 0u, planTag, 0u, 0u, 0u, 0u, 0u, 0u]
        : [HeaderMagic, HeaderAbi, 0u, 0u, 0u, planTag, 0u, 0u];

    private static uint[] PackPlanMeta(RelicFamilyPlan plan) =>
    [
        checked((uint)plan.Pool.BucketCount), checked((uint)plan.LastRequiredBucket),
        plan.AlwaysReject ? 1u : 0u, checked((uint)plan.Predicates.Length),
        checked((uint)plan.ShopPredicates.Length), OutputCapacity,
        checked((uint)plan.LocalStateCapacity), SeedsPerInvocation,
        .. plan.PositiveDepthByLane.Select(value => (uint)value),
        .. plan.ExclusionDepthByLane.Select(value => (uint)value),
        .. plan.TrackedOffsetsByLane.Select(value => (uint)value),
        .. plan.TrackedCountsByLane.Select(value => (uint)value)
    ];

    private static uint[] PackBuckets(RelicFamilyPool pool)
    {
        var output = new uint[checked(pool.BucketCount * 4)];
        for (int index = 0; index < pool.BucketCount; index++)
        {
            int offset = index * 4;
            output[offset] = checked((uint)pool.BucketOffsets[index]);
            output[offset + 1] = checked((uint)pool.BucketLengths[index]);
            output[offset + 2] = pool.BucketScopes[index] |
                                 ((uint)pool.BucketKinds[index] << 8) |
                                 ((uint)pool.DrawDirections[index] << 16);
        }
        return output;
    }

    private static uint[] PackPredicates(RelicFamilyPredicate[] predicates)
    {
        var output = new uint[checked(predicates.Length * 9)];
        for (int index = 0; index < predicates.Length; index++)
        {
            RelicFamilyPredicate p = predicates[index];
            int o = index * 9;
            output[o] = p.Lane; output[o + 1] = p.RangeMode; output[o + 2] = p.RangeValue;
            output[o + 3] = checked((uint)p.AnyOffset); output[o + 4] = p.AnyCount;
            output[o + 5] = checked((uint)p.AllOffset); output[o + 6] = p.AllCount;
            output[o + 7] = checked((uint)p.BanOffset); output[o + 8] = p.BanCount;
        }
        return output;
    }

    private static uint[] PackShopPredicates(RelicFamilyShopPredicate[] predicates, out uint[] targets)
    {
        var output = new uint[checked(predicates.Length * 4)];
        var allTargets = new List<uint>();
        for (int index = 0; index < predicates.Length; index++)
        {
            RelicFamilyShopPredicate p = predicates[index];
            int o = index * 4;
            output[o] = p.Count; output[o + 1] = p.OrderMode;
            output[o + 2] = checked((uint)allTargets.Count); output[o + 3] = checked((uint)p.TargetIds.Length);
            allTargets.AddRange(p.TargetIds.Select(value => (uint)value));
        }
        targets = allTargets.ToArray();
        return output;
    }

    private static uint StableTag(string value) =>
        BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);
}
