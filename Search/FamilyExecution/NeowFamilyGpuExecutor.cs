using System.Diagnostics;
using Godot;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

internal readonly record struct NeowFamilyGpuMetrics(int Processed, int Survivors, uint Groups, long ReadbackBytes,
    double CanonicalMs, double UploadMs, double DispatchSyncMs, double ReadbackMs, double CanonicalizeMs,
    int Dispatches, uint CurseSurvivors, uint PairSurvivors,
    uint NrPairPass, uint ArcanePass, uint CapsuleRarityPass, uint HeavyEntered, double PayloadReadbackMs);

// Session-resident resources on the existing Family GPU owner. Host transport,
// header validation and canonicalization follow the accepted R executor donor.
internal sealed partial class NeowFamilyGpuExecutor : IDisposable
{
    internal const int Capacity = FamilyPhysicalExecutionDefaults.DefaultCandidateBatchSize;
    // Full-capacity Compact transport prevents high-survival upstream N/S from
    // becoming an accidental Exact-only gate. Both workspaces remain bounded.
    private readonly RenderingDevice _rd;
    private readonly List<Rid> _owned = new();
    private readonly Rid _meta, _header, _input, _output, _projectionMetadata;
    private Rid _pipeline, _uniforms;
    private Rid _dispatchArguments, _dispatchArgumentsPipeline, _dispatchArgumentsUniforms;
    private NeowFamilyGpuExecutor? _secondWindow;
    private FamilyGpuComputeUtility.ShaderReuse? _windowShaderReuse;
    private Rid _headerBank;
    private bool _lastDenseWindowWasPaired;
    private bool _lastDenseWindowWasSequentialFallback, _doubleWindowDisabled;
    private readonly Action? _beforeDoubleAllocation;
    private readonly bool _requestIndirectStaging;
    private readonly bool _requestDoubleWindowBatching;
    private readonly bool _legacyLeafyGate, _requestPreSubmitHeaderReadback;
    private readonly bool _usePrefixHashCache;
    private Rid _prefixHashTable;
    private readonly bool _requestSharedPrefixCache;
    private SharedVisibleSeedGpuCache? _sharedPrefixCache;
    private SharedVisibleSeedGpuCache? _sharedDensePrefixCache;
    private int _sharedDensePrefixBinding = -1;
    private int _sharedPrefixBinding = -1;
    private readonly FamilyGpuHeaderReadback _headerReadback;
    private readonly NeowSparsePayloadReadback _sparsePayloadReadback;
    private string _stagedDispatchRevision;
    private readonly Rid[] _buffers;
    private Rid[]? _stagedBuffers;
    private readonly NeowFamilyGpuPlan _plan;
    private readonly NeowCapsuleComposite? _composite;
    private readonly string? _partySource;
    private readonly int _capsulePhysicalMode;
    private readonly List<(Rid Pipeline, Rid Uniforms)> _stages = new();
    private bool _stagedModulesReady;
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private readonly uint _tag;
    private readonly bool _bonesArcane;
    private readonly int _bonesKMode;
    internal uint[] BonesKCounts { get; private set; } = [];
    private readonly bool _privateInput;
    private readonly bool _privateOutput;
    private bool _disposed, _initializationFaulted;
    private readonly bool _tracePrivate = NcPhysicalExperiment.Mode.Length != 0 || PrivateOrdinalSelection.IsRequested || NwaePhysicalExperiment.Mode.Length != 0;
    internal double SetupMs { get; private set; }
    internal string Device { get; }
    internal const string IndirectStagedRevision = "GpuCountedIndirect.20261021.v1";
    internal const string DirectStagedRevision = "CapacityDirectControl.20261021.v1";
    internal const string DirectFallbackStagedRevision = "CapacityDirectFallback.20261021.v1";
    // The measured try query favors cached prefixes with capacity dispatch.
    // Counted dispatch remains an explicit developer comparison, never default.
    internal static bool RequestIndirectStaging =>
        System.Environment.GetEnvironmentVariable("RT2_NEOW_INDIRECT_DISPATCH") == "1" &&
        System.Environment.GetEnvironmentVariable("RT2_NEOW_DIRECT_DISPATCH") != "1";
    internal static string RequestedStagedDispatchRevision => RequestIndirectStaging ? IndirectStagedRevision : DirectStagedRevision;
    internal static string RequestedHeaderReadbackRevision => FamilyGpuHeaderReadback.RequestedRevision + "." +
        (FamilyGpuHeaderReadback.Requested ? NeowSparsePayloadReadback.QueuedRevision : NeowSparsePayloadReadback.SynchronousRevision);
    internal string HeaderReadbackRevision => _headerReadback.Revision + "." +
        (_sparsePayloadReadback?.Revision ?? NeowSparsePayloadReadback.SynchronousRevision) +
        (_lastDenseWindowWasPaired && _secondWindow is not null &&
            _secondWindow._sparsePayloadReadback.Revision != _sparsePayloadReadback?.Revision
            ? ".SecondPayload." + _secondWindow._sparsePayloadReadback.Revision : "");
    internal long LastSparsePayloadPrefetchBytes => _sparsePayloadReadback.LastPrefetchBytes;
    internal const string LegacyLeafyGateRevision = "LeafyGateLegacy.20261021.v1";
    internal const string OptimizedLeafyGateRevision = "LeafyGateLazyFirstDraw.20261021.v1";
    internal static bool RequestLegacyLeafyGate => !SearchOptimizationScope.Current.LeafyLazyInitialization ||
        System.Environment.GetEnvironmentVariable("RT2_NEOW_LEGACY_LEAFY_GATE") == "1";
    internal static string RequestedLeafyGateRevision => RequestLegacyLeafyGate ? LegacyLeafyGateRevision : OptimizedLeafyGateRevision;
    internal string LeafyGateRevision => _legacyLeafyGate ? LegacyLeafyGateRevision : OptimizedLeafyGateRevision;
    internal const string PrefixHashCacheRevision = "PrefixHashBlock34Pow4.20261021.v1";
    internal const string LegacyPrefixHashRevision = "VisibleSeedHashLegacy.20261021.v1";
    internal static bool RequestPrefixHashCache => SearchOptimizationScope.Current.PrefixCache &&
        System.Environment.GetEnvironmentVariable("RT2_NEOW_LEGACY_PREFIX_HASH") != "1";
    internal static string RequestedPrefixHashRevision => RequestPrefixHashCache ? PrefixHashCacheRevision : LegacyPrefixHashRevision;
    internal bool UsesPrefixHashCache => _usePrefixHashCache;
    internal string PrefixHashRevision => _usePrefixHashCache ? PrefixHashCacheRevision : LegacyPrefixHashRevision;
    internal bool UsesSharedPrefixHashCache => _sharedPrefixCache is not null;
    internal int SharedPrefixHashBinding => _sharedPrefixBinding;
    internal string? SharedPrefixHashRevisionFor(bool compactAbi1Input) =>
        (compactAbi1Input || _privateInput || !_plan.UsesStagedDense
            ? _sharedPrefixCache : _sharedDensePrefixCache) is not null
            ? SharedVisibleSeedGpuCache.Revision : null;
    internal bool UsesSharedDensePrefixHashCache => _sharedDensePrefixCache is not null;
    internal int SharedDensePrefixHashBinding => _sharedDensePrefixBinding;
    internal string StagedDispatchRevision => _lastDenseWindowWasPaired && _secondWindow is not null &&
        _secondWindow._stagedDispatchRevision != _stagedDispatchRevision
            ? _stagedDispatchRevision + ".Second." + _secondWindow._stagedDispatchRevision : _stagedDispatchRevision;
    internal bool UsesIndirectStaging => _dispatchArguments.IsValid;
    internal const int DenseWindowBatchCapacity = Capacity * 2;
    internal const string SingleWindowRevision = "SingleWindowHeader.20261021.v1";
    internal const string DoubleWindowRevision = "DoubleWindowHeaderBank.20261021.v1";
    internal const string SequentialWindowFallbackRevision = "SequentialWindowAllocationFallback.20261021.v1";
    internal static bool RequestDoubleWindowBatching => SearchOptimizationScope.Current.DoubleWindowBatching &&
        System.Environment.GetEnvironmentVariable("RT2_NEOW_SINGLE_WINDOW") != "1";
    internal string WindowTransportRevision => _lastDenseWindowWasPaired ? DoubleWindowRevision :
        _lastDenseWindowWasSequentialFallback ? SequentialWindowFallbackRevision : SingleWindowRevision;
    internal bool SupportsDenseWindowBatching => _requestDoubleWindowBatching && !_privateInput && !_privateOutput && _composite is null &&
        _partySource is null && !_plan.IsMultiplayer && _bonesKMode == 0 && _capsulePhysicalMode == 0 &&
        _plan.UsesStagedDense && _plan.UsesLeafyPreGate;
    internal const long WorkspaceBytes = (long)Capacity * sizeof(uint) * 2;
    internal long ResidentBytes => WorkspaceBytes + (_stagedBuffers is null ? 0 :
        (long)NeowFamilyGpuPlan.CurseCapacity * 12 + (long)NeowFamilyGpuPlan.PairCapacity * 48) + (UsesIndirectStaging ? 24 : 0) +
        (_secondWindow?.ResidentBytes ?? 0) + (_headerBank.IsValid ? 64 : 0) + (_prefixHashTable.IsValid ? PrefixHashCache.MaximumTableWords * sizeof(uint) : 0) +
        (_sharedPrefixCache is null ? 0 : SharedVisibleSeedGpuCache.Bytes) +
        (_sharedDensePrefixCache is null ? 0 : SharedVisibleSeedGpuCache.Bytes);
    internal static string ShaderSource(int stage = 3, bool capsuleComposite = false, bool leafyPreGate = false,
        bool bonesCapsuleComposite = false, bool bonesArcaneComposite = false, int directNestedMode = 0, int capsulePhysicalMode = 0, int bonesKMode = 0, bool authoredUpgrades = false,
        bool localResults = true, bool multiplayer = false, int jointTransformMetaOffset = -1, bool legacyLeafyGate = false,
        bool prefixHashCache = false, bool internalFilterOrder = false, string internalFilterOrderSource = "") => FamilyGpuComputeUtility.LoadEmbeddedShader(multiplayer ? "NeowParty.comp.glsl" : "NeowSingleplayer.comp.glsl")
        .Replace("#define N_INTERNAL_ORDER 0", internalFilterOrder ? "#define N_INTERNAL_ORDER 1" : "#define N_INTERNAL_ORDER 0", StringComparison.Ordinal)
        .Replace("/*__RT2_N_INTERNAL_FILTER_ORDER__*/", internalFilterOrderSource, StringComparison.Ordinal)
        .Replace("#define RT2_LEAFY_GATE_LEGACY 0", legacyLeafyGate ? "#define RT2_LEAFY_GATE_LEGACY 1" : "#define RT2_LEAFY_GATE_LEGACY 0", StringComparison.Ordinal)
        .Replace("#define RT2_PREFIX_HASH_CACHE 0", prefixHashCache ? "#define RT2_PREFIX_HASH_CACHE 1" : "#define RT2_PREFIX_HASH_CACHE 0", StringComparison.Ordinal)
        .Replace("__RT2_N_TRANSFORM_META_OFFSET__", jointTransformMetaOffset.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("/*__RT2_N_JOINT_TRANSFORMS__*/", jointTransformMetaOffset < 0 ? "" : FamilyGpuComputeUtility.LoadEmbeddedShader("NeowJointTransforms.glsl"), StringComparison.Ordinal)
        .Replace("__RT2_LOCAL_RESULTS__", localResults ? "1" : "0", StringComparison.Ordinal)
        .Replace("__RT2_AUTHORED_UPGRADES__", authoredUpgrades ? "1" : "0", StringComparison.Ordinal)
        .Replace("__RT2_BONES_K_MODE__", bonesKMode.ToString(), StringComparison.Ordinal)
        .Replace("/*__RT2_BONES_K__*/", bonesKMode == 0 ? "" : FamilyGpuComputeUtility.LoadEmbeddedShader("BonesKSameRoute.glsl")
            .Replace("/*__RT2_RFULL_CHECKPOINT_MATCHER__*/", FamilyGpuComputeUtility.LoadEmbeddedShader("RelicFullCapsule.glsl"), StringComparison.Ordinal), StringComparison.Ordinal)
        .Replace("__RT2_CAPSULE_PHYSICAL__", capsulePhysicalMode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__RT2_N_DIRECT_NESTED__", directNestedMode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("__RT2_NR_CAPSULE__", bonesCapsuleComposite || bonesArcaneComposite ? "2" : capsuleComposite ? "1" : "0", StringComparison.Ordinal)
        .Replace("__RT2_N_LEAFY_PRE_GATE__", leafyPreGate ? "1" : "0", StringComparison.Ordinal)
        .Replace("/*__RT2_CAPSULE_COMPOSITE__*/", bonesArcaneComposite ? BonesCapsuleShader(true) : bonesCapsuleComposite ? BonesCapsuleShader() : capsuleComposite ? CapsuleShader() : "", StringComparison.Ordinal)
        .Replace("__RT2_N_STAGE__", stage.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("/*__RT2_NEOW_LOCAL_DONOR__*/", FamilyGpuComputeUtility.LoadEmbeddedShader(
            multiplayer ? "NeowPartyDonor.glsl" : "NeowSingleplayerDonor.glsl"), StringComparison.Ordinal);
    private static string BonesCapsuleShader(bool arcane = false) => FamilyGpuComputeUtility.LoadEmbeddedShader(
        arcane ? "NeowBonesArcaneCapsuleComposite.glsl" : "NeowBonesCapsuleComposite.glsl")
        .Replace("/*__RT2_RFULL_CHECKPOINT_MATCHER__*/", FamilyGpuComputeUtility.LoadEmbeddedShader("RelicFullCapsule.glsl"), StringComparison.Ordinal);
    private static string CapsuleShader()
    {
        ulong hash = RolltheSpire2.Core.Seed.XxHash64.Hash("up_front"u8, 0);
        return FamilyGpuComputeUtility.LoadEmbeddedShader("NeowCapsuleComposite.glsl")
            .Replace("__RT2_UP_FRONT_LOW__", ((uint)hash).ToString("x8"), StringComparison.Ordinal)
            .Replace("__RT2_UP_FRONT_HIGH__", ((uint)(hash >> 32)).ToString("x8"), StringComparison.Ordinal);
    }
    internal NeowFamilyGpuExecutor(RenderingDevice rd, NeowFamilyGpuPlan plan, uint tag, NeowCapsuleComposite? composite = null, PrivateOrdinalBuffer? privateInput = null, PrivateOrdinalBuffer? privateOutput = null, int capsulePhysicalMode = 0, int bonesKMode = 0, uint[]? bonesKMetadata = null, NeowFamilyGpuPlan[]? partyPlans = null, bool? useIndirectStaging = null, Action? beforeDoubleAllocation = null, bool? legacyLeafyGate = null, bool? usePreSubmitHeaderReadback = null, bool? usePrefixHashCache = null,
        bool? useStaticDrawGates = null,
        bool? useSharedPrefixCache = null, bool? useInternalFilterOrder = null, bool? useGroupOutput = null,
        bool? useDoubleWindowBatching = null, NeowStructuralTrialOptions? structuralTrials = null)
    {
        _requestDoubleWindowBatching = useDoubleWindowBatching ?? RequestDoubleWindowBatching;
        _useGroupOutput = useGroupOutput ?? RequestGroupOutput;
        _structuralTrials = (structuralTrials ?? NeowStructuralTrialOptions.Capture()).WithSettings();
        _beforeDoubleAllocation = beforeDoubleAllocation;
        _requestSharedPrefixCache = useSharedPrefixCache ?? SharedVisibleSeedGpuCache.Requested;
        _requestIndirectStaging = useIndirectStaging ?? RequestIndirectStaging;
        _useStaticDrawGates = useStaticDrawGates ?? RequestStaticDrawGates;
        _legacyLeafyGate = legacyLeafyGate ?? RequestLegacyLeafyGate;
        _requestPreSubmitHeaderReadback = usePreSubmitHeaderReadback ?? FamilyGpuHeaderReadback.Requested;
        _sparsePayloadReadback = new NeowSparsePayloadReadback(rd, _requestPreSubmitHeaderReadback, privateOutput is null);
        _headerReadback = new FamilyGpuHeaderReadback(rd, _requestPreSubmitHeaderReadback,
            submitAndSync: family =>
            {
                // Both optional prefix captures enter their delivery phase at
                // the same real submission as the header, never at registration.
                _sparsePayloadReadback.BeginSubmission();
                _secondWindow?._sparsePayloadReadback.BeginSubmission();
                FamilyGpuComputeUtility.SubmitAndSync(rd, family);
            });
        string? partySource = null;
        if (partyPlans is not null)
        {
            if (composite is not null || privateInput is not null || privateOutput is not null || bonesKMode != 0)
                throw new InvalidOperationException("PartyN.FusedTransportScope");
            (plan, partySource) = NeowPartyGpuPacking.Pack(partyPlans);
        }
        if (capsulePhysicalMode != 0 && (capsulePhysicalMode is < 1 or > 6 || composite is null || composite.UsesBonesCheckpoint || plan.UsesStagedDense))
            throw new InvalidOperationException("DirectCapsule.PhysicalScope");
        if (bonesKMode is < 0 or > 2 || (bonesKMode != 0 && (composite is not null || bonesKMode == 1 && privateOutput is null || bonesKMode == 2 && bonesKMetadata is null)))
            throw new InvalidOperationException("BonesK.SameRouteScope");
        _bonesKMode = bonesKMode;
        _rd = rd; _tag = tag; _bonesArcane = composite?.UsesBonesArcane == true; _privateInput = privateInput is not null;
        _privateOutput = privateOutput is not null;
        _plan = plan; _composite = composite; _partySource = partySource; _capsulePhysicalMode = capsulePhysicalMode;
        InitializeInternalFilterOrder(useInternalFilterOrder);
        InitializeStructuralTrials();
        _stagedDispatchRevision = _requestIndirectStaging ? IndirectStagedRevision : DirectStagedRevision;
        _usePrefixHashCache = (usePrefixHashCache ?? RequestPrefixHashCache) && !_privateInput &&
            composite is null && partySource is null && !plan.IsMultiplayer && plan.UsesStagedDense &&
            bonesKMode == 0 && capsulePhysicalMode == 0;
        if (_privateInput && ReferenceEquals(privateInput, privateOutput)) throw new ArgumentException("N.PrivateBuffersAlias");
        var timer = Stopwatch.StartNew();
        Rid Buffer(uint[] values, string name) => Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(values), name));
        try
        {
            _meta = Buffer(new uint[8], "N.Batch"); _header = Buffer(Header(), "N.Header");
            _input = privateInput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity * sizeof(uint), "N.Input"));
            _output = privateOutput?.Buffer ?? Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Capacity * sizeof(uint), "N.Output"));
            _buffers = [_meta, Buffer(plan.Meta, "N.Plan"), Buffer(plan.PoolMeta, "N.Pools"), Buffer(plan.Cards, "N.Cards"), Buffer(plan.Strike, "N.Strike"),
                Buffer(plan.Defend, "N.Defend"), _header, _output, Buffer(plan.Bones, "N.Bones"), Buffer(plan.Conditions, "N.Conditions"), _input];
            if (bonesKMode == 2 || composite is not null)
                _projectionMetadata = Buffer(bonesKMetadata ?? composite!.Metadata, "N.CapsuleProjection");
            Device = rd.GetDeviceName();
            // Private bindings have a fixed role. Build only that role while the
            // chain's shader-reuse scope is active. Public input is known at Execute.
            if (_privateInput || _privateOutput)
                EnsureModules(plan.UsesStagedDense && !_privateInput);
            SetupMs = timer.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(ex, () => FamilyGpuComputeUtility.FreeAll(rd, _owned), "N.Constructor");
            throw;
        }
    }
    private Rid Add(Rid rid) { _owned.Add(rid); return rid; }

    private string ModuleSource(int stage)
    {
        string source = stage == 3 && _partySource is not null ? _partySource : ShaderSource(stage,
            capsuleComposite: stage == 3 && _composite is not null,
            leafyPreGate: _plan.UsesLeafyPreGate, bonesCapsuleComposite: _composite?.UsesBonesCheckpoint == true,
            bonesArcaneComposite: _bonesArcane, directNestedMode: _composite is null ? _plan.DirectNestedMode : 0,
            capsulePhysicalMode: stage == 3 ? _capsulePhysicalMode : 0, bonesKMode: _bonesKMode,
            authoredUpgrades: _plan.HasAuthoredUpgrades,
            localResults: _plan.HasLocalResults || _composite is not null || _bonesKMode != 0, multiplayer: _plan.IsMultiplayer,
            jointTransformMetaOffset: _plan.JointTransformMetaOffset, legacyLeafyGate: _legacyLeafyGate,
            prefixHashCache: stage == 0 && _usePrefixHashCache,
            internalFilterOrder: (stage is 1 or 4) && InternalFilterOrderEnabled,
            internalFilterOrderSource: stage == 4 && InternalFilterOrderEnabled
                ? _structuralPhysical.InternalOrderSource ?? _internalFilterOrderPlan.Source : "");
        source = ApplyStaticDrawGates(source, stage, _plan, _useStaticDrawGates);
        source = ApplyGroupOutput(source, stage, GroupOutputEnabledFor(false));
        source = ApplyStructuralTrials(source, stage);
        if (_privateInput)
        {
            const string ordinalRead = "uint ordinal=batch.values[4u]==0u ? first+lane : input_ordinals.values[first+lane];";
            if (!source.Contains(ordinalRead, StringComparison.Ordinal)) throw new InvalidOperationException("N.PrivateInputSeamChanged");
            source = source.Replace(ordinalRead, ordinalRead + "\nif(ordinal>=batch.values[2u]) { atomicExchange(header.values[3u],1u); continue; }", StringComparison.Ordinal);
        }
        return source;
    }

    private void EnsureModules(bool staged)
    {
        if (_initializationFaulted) throw new InvalidOperationException("NFamilyGpuInitializationFaulted");
        if (staged ? _stagedModulesReady : _pipeline.IsValid) return;
        var timer = Stopwatch.StartNew();
        string name = staged ? "NeowFamilyPreBones0" : "NeowFamilyLocalDonor";
        bool checkpointMetadata = _composite?.UsesBonesCheckpoint == true || _bonesKMode == 2;
        try
        {
            if (staged)
            {
                // Allocate checkpoints only when this executor actually receives Dense.
                _stagedBuffers = [.._buffers,
                    Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(_rd, NeowFamilyGpuPlan.CurseCapacity * 12, "N.CurseSurvivors")),
                    Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(_rd, NeowFamilyGpuPlan.PairCapacity * 48, "N.PairSurvivors"))];
                if (checkpointMetadata) _stagedBuffers = [.._stagedBuffers, _projectionMetadata];
                if (_usePrefixHashCache)
                    _prefixHashTable = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(_rd,
                        PrefixHashCache.MaximumTableWords * sizeof(uint), "N.PrefixHashTable"));
                for (int stage = 0; stage < 3; stage++)
                {
                    name = "NeowFamilyPreBones" + stage;
                    string source = ModuleSource(stage);
                    Rid[] stageBuffers = stage == 0 && _usePrefixHashCache ? [.._stagedBuffers, _prefixHashTable] : _stagedBuffers;
                    if (stage == 0 && !_usePrefixHashCache && (_composite is not null || _partySource is not null || _plan.IsMultiplayer || _bonesKMode != 0 || _capsulePhysicalMode != 0))
                    {
                        int binding = stageBuffers.Length;
                        _sharedDensePrefixCache = SharedVisibleSeedGpuCache.Attach(_rd, _owned,
                            ref source, ref stageBuffers, "N.Neow.Dense", _requestSharedPrefixCache);
                        _sharedDensePrefixBinding = _sharedDensePrefixCache is null ? -1 : binding;
                    }
                    Rid shader = Add(FamilyGpuComputeUtility.CompileShader(_rd, source, name));
                    Rid pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(_rd, shader, name));
                    if (!_rd.ComputePipelineIsValid(pipeline)) throw new InvalidOperationException("NFamilyStagePipelineInvalid:" + name);
                    _stages.Add((pipeline, Add(FamilyGpuComputeUtility.CreateUniformSet(_rd, shader, stageBuffers, name))));
                }
                if (InternalFilterOrderEnabled) CreateInternalFilterOrderModule(_stagedBuffers);
                if (_requestIndirectStaging) TryCreateIndirectDispatchModules();
                _stagedModulesReady = true;
            }
            else
            {
                Rid[] buffers = _buffers;
                if (checkpointMetadata)
                    buffers = _stagedBuffers ?? [..buffers,
                        Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(_rd, 4, "N.UnusedCurseBinding")),
                        Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(_rd, 4, "N.UnusedPairBinding")), _projectionMetadata];
                else if (_composite is not null) buffers = [..buffers, _projectionMetadata];
                string source = ModuleSource(3);
                int sharedBinding = buffers.Length;
                _sharedPrefixCache = SharedVisibleSeedGpuCache.Attach(_rd, _owned, ref source, ref buffers,
                    "N.Neow.Fused", _requestSharedPrefixCache);
                _sharedPrefixBinding = _sharedPrefixCache is null ? -1 : sharedBinding;
                Rid shader = Add(FamilyGpuComputeUtility.CompileShader(_rd, source, name));
                Rid pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(_rd, shader, name));
                if (!_rd.ComputePipelineIsValid(pipeline)) throw new InvalidOperationException("NFamilyGpuPipelineInvalid:" + name);
                Rid uniforms = Add(FamilyGpuComputeUtility.CreateUniformSet(_rd, shader, buffers, name));
                _pipeline = pipeline; _uniforms = uniforms;
            }
            bool prefixActive = staged && _usePrefixHashCache;
            Bootstrap.RuntimeLog.TryBackgroundInfo($"nGpuModuleReady=true;path={(staged ? "DenseStaged" : "Fused")};prefixHashCache={prefixActive};prefixHashRevision={(prefixActive ? PrefixHashCacheRevision : LegacyPrefixHashRevision)};sharedPrefixHashCache={(staged ? _sharedDensePrefixCache is not null : _sharedPrefixCache is not null)};sharedPrefixBinding={(staged ? _sharedDensePrefixBinding : _sharedPrefixBinding)};stagedDispatchRevision={_stagedDispatchRevision};leafyPreGate={_plan.UsesLeafyPreGate};privateInput={_privateInput};privateOutput={_privateOutput};internalFilterOrder={staged && InternalFilterOrderEnabled};internalFilterOrderRevision={InternalFilterOrderRevisionFor(!staged)};internalFilterOrderDescription={(staged ? InternalFilterOrderDescription : "FusedCanonicalOrder")};internalFilterOrderNicheAdvances={string.Join(',', _internalFilterOrderPlan.NicheAdvances)};{InternalFilterOrderCostEvidence};groupOutput={GroupOutputEnabledFor(!staged)};groupOutputRevision={GroupOutputRevisionFor(!staged)}");
            Bootstrap.RuntimeLog.TryBackgroundInfo($"nStructuralTrials=true;path={(staged ? "DenseStaged" : "Fused")};poolGateRequested={_structuralTrials.KaleidoscopePoolGate};finalCurseIntervalsRequested={_structuralTrials.FinalCurseIntervals};bonesIdentityOnlyRequested={_structuralTrials.BonesIdentityOnly};poolGate={staged && _structuralPhysical.KaleidoscopePoolGate};finalCurseIntervals={staged && _structuralPhysical.FinalCurseIntervals};bonesIdentityOnly={!staged && _structuralPhysical.BonesIdentityOnly};target0PoolMask={_structuralPhysical.Target0Pools};target1PoolMask={_structuralPhysical.Target1Pools};physicalRevision={StructuralRevisionFor(!staged)};reason={_structuralPhysical.Reason};acceptanceAuthority=CanonicalMatcherAndExactUnchanged;densityIndependenceAssumed=false");
        }
        catch (Exception ex)
        {
            _initializationFaulted = true;
            var failure = new InvalidOperationException("NFamilyGpuModuleInitializationFailed:" + name + ":" + ex.Message, ex);
            FamilyGpuComputeUtility.CleanupAfterFailure(failure, () => FamilyGpuComputeUtility.FreeAll(_rd, _owned), "N.Module:" + name);
            throw failure;
        }
        finally { SetupMs += timer.Elapsed.TotalMilliseconds; }
    }
    private void TryCreateIndirectDispatchModules()
    {
        // Keep optional resources separate until all bindings are valid. Only
        // initialization may fall back; a submitted dispatch or invalid result
        // must retain the existing fail-closed execution behavior.
        var resources = new List<Rid>();
        Rid Own(Rid rid) { resources.Add(rid); return rid; }
        try
        {
            Rid arguments = _rd.StorageBufferCreate(24, Array.Empty<byte>(), RenderingDevice.StorageBufferUsage.Indirect);
            if (!arguments.IsValid) throw new InvalidOperationException("N.IndirectArgumentsBufferUnavailable");
            Own(arguments);
            Rid shader = Own(FamilyGpuComputeUtility.CompileShader(_rd,
                FamilyGpuComputeUtility.LoadEmbeddedShader("NeowDispatchArguments.comp.glsl"), "NeowDispatchArguments"));
            Rid pipeline = Own(FamilyGpuComputeUtility.CreateComputePipeline(_rd, shader, "NeowDispatchArguments"));
            Rid uniforms = Own(FamilyGpuComputeUtility.CreateUniformSet(_rd, shader, [_meta, _header, arguments], "NeowDispatchArguments"));
            _owned.AddRange(resources);
            _dispatchArguments = arguments; _dispatchArgumentsPipeline = pipeline; _dispatchArgumentsUniforms = uniforms;
        }
        catch (Exception ex)
        {
            FamilyGpuComputeUtility.CleanupAfterFailure(ex, () => FamilyGpuComputeUtility.FreeAll(_rd, resources), "N.IndirectDispatchInitialization");
            _stagedDispatchRevision = DirectFallbackStagedRevision;
            Bootstrap.RuntimeLog.TryBackgroundWarning($"nStagedDispatchFallback=true;revision={_stagedDispatchRevision};reason={ex.GetType().Name}:{ex.Message.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ')};scope=OptionalInitializationOnly;reference=UnmeasuredDispatchRevision");
        }
    }
    private uint[] Header() => _bonesKMode != 0 ? [0x4e464d52, 1, 0, 0, 0, _tag, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] : _bonesArcane ? [0x4e464d52, 1, 0, 0, 0, _tag, 0, 0, 0, 0, 0, 0]
        : [0x4e464d52, 1, 0, 0, 0, _tag, 0, 0];
    private void AssertOwner()
    {
        if (_owner != System.Environment.CurrentManagedThreadId) throw new InvalidOperationException("NFamilyGpuOwnerMismatch");
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    internal FamilyCandidateSet Execute(FamilyCandidateSet input, CancellationToken token, out NeowFamilyGpuMetrics metrics)
    {
        if (_privateInput || _privateOutput) throw new InvalidOperationException("N.PrivateTransportRequiresPrivateEntry");
        _lastDenseWindowWasPaired = false;
        _lastDenseWindowWasSequentialFallback = false;
        return ExecuteCore(input, input.Count, token, out metrics)!;
    }

    internal FamilyCandidateSet ExecutePrivate(SearchBatch batch, int count, CancellationToken token, out NeowFamilyGpuMetrics metrics)
    {
        if (!_privateInput || _privateOutput || count < 0 || count > batch.BatchCandidateCount) throw new InvalidDataException("N.PrivateCountInvalid");
        return ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics)!;
    }
    internal int ExecutePrivateOutput(FamilyCandidateSet input, CancellationToken token, out NeowFamilyGpuMetrics metrics)
    {
        if (!_privateOutput || _privateInput || !input.IsDense) throw new InvalidOperationException("N.PrivateOutputDenseOnly");
        ExecuteCore(input,input.Count,token,out metrics);
        return metrics.Survivors;
    }
    internal int ExecutePrivateStage(SearchBatch batch, int count, CancellationToken token, out NeowFamilyGpuMetrics metrics)
    {
        if (!_privateOutput || (!_privateInput && count != batch.BatchCandidateCount) || count < 0 || count > batch.BatchCandidateCount || batch.BatchCandidateCount > Capacity)
            throw new InvalidDataException("N.PrivateStageBounds");
        ExecuteCore(FamilyCandidateSet.Dense(batch), count, token, out metrics);
        return metrics.Survivors;
    }
    private FamilyCandidateSet? ExecuteCore(FamilyCandidateSet input, int inputCount, CancellationToken token, out NeowFamilyGpuMetrics metrics)
    {
        AssertOwner(); token.ThrowIfCancellationRequested();
        _sparsePayloadReadback.AssertReady();
        if (inputCount > Capacity) throw new InvalidOperationException("NFamilyGpuCapacityExceeded");
        bool dense = input.IsDense && !_privateInput;
        bool staged = dense && _plan.UsesStagedDense;
        if (_initializationFaulted) throw new InvalidOperationException("NFamilyGpuInitializationFaulted");
        // Setup is reported separately from batch upload/dispatch costs, including
        // a later transition between public Compact and Dense input.
        if (inputCount > 0) EnsureModules(staged);
        GpuProfilingBeginBatch(inputCount);
        try
        {
            var recorded = RecordWindow(input, inputCount, staged);
            using var payload = _sparsePayloadReadback.RegisterRecorded(_output, inputCount, recorded.Groups > 0);
            double syncedMs = 0;
            GpuProfilingBeforeSubmit();
            uint[] header;
            try
            {
                header = _headerReadback.CompleteRecorded(_header, _bonesKMode != 0 ? 16u : _bonesArcane ? 12u : 8u,
                    recorded.Groups > 0, "N.Neow", () => { syncedMs = recorded.Timer.Elapsed.TotalMilliseconds; GpuProfilingAfterSync(); });
                payload?.CompleteAfterSync();
            }
            catch { _sparsePayloadReadback.MarkFaulted(); throw; }
            token.ThrowIfCancellationRequested();
            var result = CompleteWindow(recorded, header, syncedMs, out metrics);
            GpuProfilingBatchCompleted(metrics);
            return result;
        }
        finally { GpuProfilingEndBatch(); }
    }
    private sealed record RecordedWindow(FamilyCandidateSet Input, int InputCount, bool Staged,
        uint Groups, double UploadedMs, Stopwatch Timer);
    private RecordedWindow RecordWindow(FamilyCandidateSet input, int inputCount, bool staged)
    {
        bool dense = input.IsDense && !_privateInput;
        var timer = Stopwatch.StartNew();
        FamilyGpuComputeUtility.Update(_rd, _meta, [(uint)input.Batch.BatchBase, (uint)(input.Batch.BatchBase >> 32),
            (uint)input.Batch.BatchCandidateCount, (uint)inputCount, dense ? 0u : 1u, Capacity,
            NeowFamilyGpuPlan.CurseCapacity, NeowFamilyGpuPlan.PairCapacity], "NFamilyBatch");
        FamilyGpuComputeUtility.Update(_rd, _header, Header(), "NFamilyHeader");
        if (staged && InternalFilterOrderEnabled)
            FamilyGpuComputeUtility.Update(_rd, _output, [0u], "N.InternalFilterCount");
        if (staged && dense && _usePrefixHashCache && inputCount > 0)
            FamilyGpuComputeUtility.Update(_rd, _prefixHashTable, PrefixHashCache.BuildTable(input.Batch.BatchBase, inputCount), "N.PrefixHashTable");
        if (staged && inputCount > 0) _sharedDensePrefixCache?.Refresh(input.Batch);
        if (!staged) _sharedPrefixCache?.Refresh(input.Batch);
        if (!input.IsDense && !_privateInput) FamilyGpuComputeUtility.Update(_rd, _input,
            input.ExportAbi1().ToArray().Select(x => checked((uint)x)).ToArray(), "NFamilyCompact");
        double uploadedMs = timer.Elapsed.TotalMilliseconds;
        uint groups = (uint)((inputCount + 511) / 512);
        if (groups > 0)
        {
            if (!staged && !_pipeline.IsValid) throw new InvalidOperationException("N.UnboundCompactModule");
            GpuProfilingWindowBegin();
            long list = _rd.ComputeListBegin();
            if (staged)
            {
                for (int stage = 0; stage < 3; stage++)
                {
                    if (stage > 0)
                    {
                        _rd.ComputeListAddBarrier(list);
                        if (stage == 1 && InternalFilterOrderEnabled)
                        {
                            _rd.ComputeListBindComputePipeline(list, _internalFilterOrderPipeline);
                            _rd.ComputeListBindUniformSet(list, _internalFilterOrderUniforms, 0);
                            _rd.ComputeListDispatch(list, (uint)(NeowFamilyGpuPlan.CurseCapacity / 512), 1, 1);
                            _rd.ComputeListAddBarrier(list);
                        }
                        if (UsesIndirectStaging)
                        {
                            _rd.ComputeListBindComputePipeline(list, _dispatchArgumentsPipeline);
                            _rd.ComputeListBindUniformSet(list, _dispatchArgumentsUniforms, 0);
                            _rd.ComputeListDispatch(list, 1, 1, 1);
                            _rd.ComputeListAddBarrier(list);
                        }
                    }
                    _rd.ComputeListBindComputePipeline(list, _stages[stage].Pipeline);
                    _rd.ComputeListBindUniformSet(list, _stages[stage].Uniforms, 0);
                    if (stage > 0 && UsesIndirectStaging)
                        _rd.ComputeListDispatchIndirect(list, _dispatchArguments, (uint)((stage - 1) * 12));
                    else
                        _rd.ComputeListDispatch(list, stage == 0 ? groups :
                            (uint)((stage == 1 ? NeowFamilyGpuPlan.CurseCapacity : NeowFamilyGpuPlan.PairCapacity) / 512), 1, 1);
                }
            }
            else
            {
                _rd.ComputeListBindComputePipeline(list, _pipeline); _rd.ComputeListBindUniformSet(list, _uniforms, 0);
                _rd.ComputeListDispatch(list, groups, 1, 1);
            }
            _rd.ComputeListEnd();
            GpuProfilingWindowEnd();
        }
        return new(input, inputCount, staged, groups, uploadedMs, timer);
    }
    private FamilyCandidateSet? CompleteWindow(RecordedWindow recorded, uint[] header, double syncedMs,
        out NeowFamilyGpuMetrics metrics)
    {
        var input = recorded.Input;
        int inputCount = recorded.InputCount;
        bool staged = recorded.Staged;
        uint groups = recorded.Groups;
        double uploadedMs = recorded.UploadedMs;
        var timer = recorded.Timer;
        if (header.Length != (_bonesKMode != 0 ? 16 : _bonesArcane ? 12 : 8) || header[0] != 0x4e464d52 || header[1] != 1 || header[5] != _tag ||
            header[3] != 0 || header[4] != inputCount || header[2] > header[4] ||
            (staged && (header[6] > NeowFamilyGpuPlan.CurseCapacity || header[7] > NeowFamilyGpuPlan.PairCapacity || header[7] > header[6])))
            throw new InvalidDataException("NFamilyGpuHeaderInvalid");
        if (_bonesKMode != 0) {
            BonesKCounts = header[8..16];
            ulong nPass=(ulong)header[8]+header[9]+header[10];
            if(nPass>(ulong)inputCount || (staged && nPass>header[7]) ||
                (_bonesKMode==1 && nPass!=header[2]) ||
                (_bonesKMode==2 && (header[11]>(ulong)header[8]+header[10] || header[12]>(ulong)header[9]+header[10] ||
                    header[13]>header[11] || header[14]>header[12] || header[15]!=header[2] || header[2]>(ulong)header[13]+header[14])))
                throw new InvalidDataException("BonesK.SameRouteCountersInvalid");
        }
        uint count = header[2];
        if (_privateOutput)
        {
            double readyMs=timer.Elapsed.TotalMilliseconds;
            metrics=new(inputCount,checked((int)count),groups,header.Length*4,readyMs,uploadedMs,syncedMs-uploadedMs,
                readyMs-syncedMs,0,groups==0?0:WindowDispatchCount(staged),header[6],header[7],0,0,0,0,0);
            return null;
        }
        if (_bonesArcane && (header[8] > input.Count || header[9] > header[8] || header[10] > header[9] ||
            header[11] < header[10] || header[11] > 2UL * header[10] || count > header[10]))
            throw new InvalidDataException("NrBonesArcaneCountersInvalid");
        _sparsePayloadReadback.ObserveValidatedCount(count, inputCount > 0 && groups > 0);
        double payloadStartMs = _bonesArcane ? timer.Elapsed.TotalMilliseconds : 0;
        long payloadReadbackBytes = _sparsePayloadReadback.TransferredBytes;
        if (!_sparsePayloadReadback.TryGetValidatedPrefix(count, out uint[] packed))
        {
            // A bounded speculative prefix never truncates a dense result.
            packed = FamilyGpuComputeUtility.FromUInt32Bytes(_rd.BufferGetData(_output, 0, checked(count * sizeof(uint))), count);
            payloadReadbackBytes += checked((long)count * sizeof(uint));
        }
        double readMs = timer.Elapsed.TotalMilliseconds;
        // Solo high-output public boundaries spend far more time comparison-sorting
        // widened ordinals than dispatching. Sort the complete packed permutation
        // first; the usual ABI1 validator still owns bounds and duplicate checks.
        // Party transport and small outputs retain their existing materialization.
        bool packedRadix = !_plan.IsMultiplayer && _partySource is null && packed.Length >= 65536;
        double sortMs = 0;
        if (packedRadix)
        {
            SortLargePackedOrdinals(packed);
            sortMs = timer.Elapsed.TotalMilliseconds - readMs;
        }
        double widenStart = timer.Elapsed.TotalMilliseconds;
        ulong[] ordinals;
        if (packedRadix)
        {
            ordinals = new ulong[packed.Length];
            for (int i = 0; i < packed.Length; i++) ordinals[i] = packed[i];
        }
        else ordinals = packed.Select(x => (ulong)x).ToArray();
        double widenMs = timer.Elapsed.TotalMilliseconds - widenStart;
        double sortStart = timer.Elapsed.TotalMilliseconds;
        if (!packedRadix && ordinals.Length > 1) Array.Sort(ordinals);
        sortMs += timer.Elapsed.TotalMilliseconds - sortStart;
        double validationStart = timer.Elapsed.TotalMilliseconds;
        var result = FamilyCandidateSet.FromSortedAbi1(input.Batch, ordinals);
        double canonicalMs = timer.Elapsed.TotalMilliseconds;
        metrics = new((int)header[4], ordinals.Length, groups, header.Length * sizeof(uint) + payloadReadbackBytes, canonicalMs,
            uploadedMs, syncedMs - uploadedMs, readMs - syncedMs, canonicalMs - readMs,
            groups == 0 ? 0 : WindowDispatchCount(staged), header[6], header[7], _bonesArcane ? header[8] : 0, _bonesArcane ? header[9] : 0,
            _bonesArcane ? header[10] : 0, _bonesArcane ? header[11] : 0, _bonesArcane && count > 0 ? readMs - payloadStartMs : 0);
        if (_tracePrivate)
            Bootstrap.RuntimeLog.TryBackgroundDetail($"ncStageTiming=true;stage=N;input={inputCount};output={result.Count};dispatches={metrics.Dispatches};uploadMs={uploadedMs};dispatchSyncMs={metrics.DispatchSyncMs};readbackMs={metrics.ReadbackMs};readbackBytes={metrics.ReadbackBytes};widenMs={widenMs};sortMs={sortMs};validationMs={canonicalMs-validationStart};canonicalMs={canonicalMs};packedRadix={packedRadix}");
        return result;
    }

    internal FamilyCandidateSet ExecuteDenseWindows(FamilyCandidateSet input, CancellationToken token,
        out NeowFamilyGpuMetrics metrics)
    {
        AssertOwner(); token.ThrowIfCancellationRequested();
        if (!SupportsDenseWindowBatching || !input.IsDense || input.Count > DenseWindowBatchCapacity)
            throw new InvalidOperationException("N.DoubleWindowScopeOrCapacity");
        if (input.Count <= Capacity) return Execute(input, token, out metrics);
        if (!TryPrepareDoubleWindowResources()) return ExecuteSequentialWindows(input, token, out metrics);
        var second = _secondWindow!;
        second.AssertOwner();
        _sparsePayloadReadback.AssertReady(); second._sparsePayloadReadback.AssertReady();
        _lastDenseWindowWasPaired = true;
        _lastDenseWindowWasSequentialFallback = false;
        var left = FamilyCandidateSet.Dense(new SearchBatch(input.Batch.BatchBase, Capacity));
        var right = FamilyCandidateSet.Dense(new SearchBatch(checked(input.Batch.BatchBase + (ulong)Capacity), input.Count - Capacity));
        // Both independent workspaces are fully initialized before recording.
        // Once recording starts, complete submission and synchronization before
        // observing cancellation or exposing any results to the caller.
        token.ThrowIfCancellationRequested();
        GpuProfilingBeginBatch(input.Count);
        try
        {
        var timer = Stopwatch.StartNew();
        var a = RecordWindow(left, left.Count, true);
        var b = second.RecordWindow(right, right.Count, true);
        if (_rd.BufferCopy(_header, _headerBank, 0, 0, 32) != Error.Ok ||
            _rd.BufferCopy(second._header, _headerBank, 0, 32, 32) != Error.Ok)
            throw new InvalidOperationException("N.DoubleWindowHeaderCopyFailed");
        using var payloadA = _sparsePayloadReadback.RegisterRecorded(_output, left.Count, true);
        using var payloadB = second._sparsePayloadReadback.RegisterRecorded(second._output, right.Count, true);
        double syncedMs = 0;
        GpuProfilingBeforeSubmit();
        uint[] bank;
        try
        {
            bank = _headerReadback.CompleteRecorded(_headerBank, 16, true,
                "N.Neow.DoubleWindow", () => { syncedMs = timer.Elapsed.TotalMilliseconds; GpuProfilingAfterSync(); });
            payloadA?.CompleteAfterSync(); payloadB?.CompleteAfterSync();
        }
        catch { _sparsePayloadReadback.MarkFaulted(); second._sparsePayloadReadback.MarkFaulted(); throw; }
        token.ThrowIfCancellationRequested();
        double bankReadMs = timer.Elapsed.TotalMilliseconds - syncedMs;
        if (bank.Length != 16) throw new InvalidDataException("N.DoubleWindowHeaderBankInvalid");
        // Completion retains the original header, payload, bounds and duplicate
        // validators for each physical window. These local timers only measure
        // completion work; the returned batch metrics use the shared wall time.
        var completedA = a with { UploadedMs = 0, Timer = Stopwatch.StartNew() };
        var resultA = CompleteWindow(completedA, bank[..8], 0, out var am)!;
        var completedB = b with { UploadedMs = 0, Timer = Stopwatch.StartNew() };
        var resultB = second.CompleteWindow(completedB, bank[8..], 0, out var bm)!;
        var result = MergeDenseWindows(input.Batch, resultA, resultB);
        double canonicalMs = timer.Elapsed.TotalMilliseconds;
        double readbackMs = bankReadMs + am.ReadbackMs + bm.ReadbackMs;
        double uploadMs = a.UploadedMs + b.UploadedMs;
        metrics = new(input.Count, result.Count, checked(a.Groups + b.Groups), am.ReadbackBytes + bm.ReadbackBytes,
            canonicalMs, uploadMs, syncedMs - uploadMs, readbackMs, Math.Max(0, canonicalMs - syncedMs - readbackMs),
            am.Dispatches + bm.Dispatches, checked(am.CurseSurvivors + bm.CurseSurvivors), checked(am.PairSurvivors + bm.PairSurvivors),
            0, 0, 0, 0, 0);
        GpuProfilingBatchCompleted(metrics);
        return result;
        }
        finally { GpuProfilingEndBatch(); }
    }
    private FamilyCandidateSet ExecuteSequentialWindows(FamilyCandidateSet input, CancellationToken token,
        out NeowFamilyGpuMetrics metrics)
    {
        GpuProfilingBeginBatch(input.Count);
        try
        {
        _lastDenseWindowWasPaired = false;
        _lastDenseWindowWasSequentialFallback = true;
        var left = FamilyCandidateSet.Dense(new SearchBatch(input.Batch.BatchBase, Capacity));
        var right = FamilyCandidateSet.Dense(new SearchBatch(checked(input.Batch.BatchBase + (ulong)Capacity), input.Count - Capacity));
        var timer = Stopwatch.StartNew();
        // The optional allocation failed before any command was recorded. Each
        // single-window call fully synchronizes before reusing this workspace.
        // Neither result escapes until both windows validate and merge.
        var resultA = ExecuteCore(left, left.Count, token, out var am)!;
        var resultB = ExecuteCore(right, right.Count, token, out var bm)!;
        var result = MergeDenseWindows(input.Batch, resultA, resultB);
        double canonicalMs = timer.Elapsed.TotalMilliseconds;
        double uploadMs = am.UploadMs + bm.UploadMs;
        double dispatchMs = am.DispatchSyncMs + bm.DispatchSyncMs;
        double readbackMs = am.ReadbackMs + bm.ReadbackMs;
        metrics = new(input.Count, result.Count, checked(am.Groups + bm.Groups), am.ReadbackBytes + bm.ReadbackBytes,
            canonicalMs, uploadMs, dispatchMs, readbackMs, Math.Max(0, canonicalMs - uploadMs - dispatchMs - readbackMs),
            am.Dispatches + bm.Dispatches, checked(am.CurseSurvivors + bm.CurseSurvivors), checked(am.PairSurvivors + bm.PairSurvivors),
            0, 0, 0, 0, 0);
        GpuProfilingBatchCompleted(metrics);
        return result;
        }
        finally { GpuProfilingEndBatch(); }
    }
    private static FamilyCandidateSet MergeDenseWindows(SearchBatch batch, FamilyCandidateSet left, FamilyCandidateSet right)
    {
        ulong[] merged = new ulong[checked(left.Count + right.Count)];
        left.ExportAbi1().Span.CopyTo(merged);
        var tail = right.ExportAbi1().Span;
        for (int i = 0; i < tail.Length; i++) merged[left.Count + i] = checked(tail[i] + (ulong)Capacity);
        return FamilyCandidateSet.FromSortedAbi1(batch, merged);
    }
    private bool TryPrepareDoubleWindowResources()
    {
        if (_initializationFaulted) throw new InvalidOperationException("NFamilyGpuInitializationFaulted");
        if (_secondWindow is not null && _headerBank.IsValid) return true;
        if (_doubleWindowDisabled) return false;
        double previousSetupMs = SetupMs;
        var timer = Stopwatch.StartNew();
        try
        {
            _windowShaderReuse ??= new FamilyGpuComputeUtility.ShaderReuse(_rd);
            using var scope = _windowShaderReuse.Activate();
            // A failed base initialization frees the base owner's RIDs and is
            // fatal. It must stay outside the optional-resource fallback catch.
            EnsureModules(true);
            NeowFamilyGpuExecutor? candidate = null;
            Rid bank = default;
            try
            {
                _beforeDoubleAllocation?.Invoke();
                candidate = new NeowFamilyGpuExecutor(_rd, _plan, _tag, useIndirectStaging: _requestIndirectStaging,
                    legacyLeafyGate: _legacyLeafyGate, usePreSubmitHeaderReadback: _requestPreSubmitHeaderReadback,
                    usePrefixHashCache: _usePrefixHashCache,
                    useStaticDrawGates: _useStaticDrawGates, useSharedPrefixCache: _requestSharedPrefixCache,
                    useInternalFilterOrder: _requestInternalFilterOrder, useGroupOutput: _useGroupOutput,
                    useDoubleWindowBatching: _requestDoubleWindowBatching, structuralTrials: _structuralTrials);
                candidate.EnsureModules(true);
                bank = FamilyGpuComputeUtility.CreateZeroedStorageBuffer(_rd, 64, "N.DoubleWindowHeaders");
                Add(bank);
                _secondWindow = candidate; _headerBank = bank;
                return true;
            }
            catch (Exception ex)
            {
                var cleanupFailures = new List<Exception>();
                if (bank.IsValid)
                    try { _rd.FreeRid(bank); } catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
                if (candidate is not null)
                    try { candidate.Dispose(); } catch (Exception cleanup) { cleanupFailures.Add(cleanup); }
                // If optional resources cannot be proven released, retain the
                // original fail-closed behavior instead of reusing the device.
                if (cleanupFailures.Count != 0 || FamilyGpuComputeUtility.HasInitializationCleanupFailure(ex))
                {
                    _initializationFaulted = true;
                    throw new AggregateException("N.DoubleWindowCleanupFailed", new[] { ex }.Concat(cleanupFailures));
                }
                _doubleWindowDisabled = true;
                Bootstrap.RuntimeLog.TryBackgroundWarning($"nDoubleWindowFallback=true;revision={SequentialWindowFallbackRevision};reason={ex.GetType().Name}:{ex.Message.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ')};scope=OptionalInitializationOnly;retryAllocation=false;submittedWorkRetried=false");
                return false;
            }
        }
        finally { SetupMs = previousSetupMs + timer.Elapsed.TotalMilliseconds; }
    }

    // Four stable byte passes preserve every uint, including malformed values;
    // no masking to the current root-window width, filtering, or deduplication.
    internal static void SortLargePackedOrdinals(uint[] packed)
    {
        var scratch = new uint[packed.Length];
        uint[] source = packed, destination = scratch;
        Span<int> offsets = stackalloc int[256];
        for (int shift = 0; shift < 32; shift += 8)
        {
            offsets.Clear();
            foreach (uint value in source) offsets[(int)((value >> shift) & 255)]++;
            int next = 0;
            for (int bucket = 0; bucket < offsets.Length; bucket++)
            {
                int length = offsets[bucket]; offsets[bucket] = next; next += length;
            }
            foreach (uint value in source) destination[offsets[(int)((value >> shift) & 255)]++] = value;
            (source, destination) = (destination, source);
        }
    }
    public void Dispose()
    {
        AssertOwner(); GpuProfilingDispose(); _disposed = true;
        var failures = new List<Exception>();
        try { _secondWindow?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        try { FamilyGpuComputeUtility.FreeAll(_rd, _owned); } catch (Exception ex) { failures.Add(ex); }
        try { _windowShaderReuse?.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        if (failures.Count != 0) throw new AggregateException("N.DisposeFailed", failures);
    }

    // Optional implementations live in Diagnostics/GpuProfiling. Without that
    // build input, C# removes these declarations and every call entirely.
    partial void GpuProfilingBeginBatch(int inputCount);
    partial void GpuProfilingWindowBegin();
    partial void GpuProfilingWindowEnd();
    partial void GpuProfilingBeforeSubmit();
    partial void GpuProfilingAfterSync();
    partial void GpuProfilingBatchCompleted(NeowFamilyGpuMetrics metrics);
    partial void GpuProfilingEndBatch();
    partial void GpuProfilingDispose();
}
