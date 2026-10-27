using Godot;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class NeowFamilyGpuExecutor
{
    internal const string InternalFilterOrderRevision = "InternalFilterOrder.FinalCurseBeforeIdentity.DenseCompaction.20261022.v1";
    private bool _requestInternalFilterOrder, _internalFilterOrderEnabled;
    private NeowInternalFilterOrderPlan _internalFilterOrderPlan = null!;
    private string _internalFilterOrderDescription = "NotInitialized";
    private Rid _internalFilterOrderPipeline, _internalFilterOrderUniforms;

    // Only diagnostic builds include the same-binary benchmark switch. Normal
    // releases always let the dependency and cost planner choose the order.
    internal static bool RequestInternalFilterOrder
    {
        get
        {
#if RT2_GPU_PROFILING
            return SearchOptimizationScope.Current.CostOrderedFilters &&
                System.Environment.GetEnvironmentVariable("RT2_N_INTERNAL_ORDER") != "0";
#else
            return SearchOptimizationScope.Current.CostOrderedFilters;
#endif
        }
    }

    internal bool InternalFilterOrderEnabled => _internalFilterOrderEnabled;
    internal string InternalFilterOrderDescription => _internalFilterOrderDescription;
    private string InternalFilterOrderCostEvidence
    {
        get
        {
            static string Number(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string evidence = "internalFilterOrderAcceptedFraction=" + Number(_internalFilterOrderPlan.AcceptedFraction);
            if (_internalFilterOrderPlan.CostModel is not { } cost) return evidence;
            return evidence + ";internalFilterOrderModel=" + cost.Model +
                ";internalFilterOrderGateWork=" + Number(cost.GateWorkUnits) +
                ";internalFilterOrderPairWork=" + Number(cost.PairWorkUnits) +
                ";internalFilterOrderRejectionLowerBound=" + Number(cost.RejectionLowerBound) +
                ";internalFilterOrderModeledGain=" + Number(cost.EstimatedGainFraction) +
                ";internalFilterOrderGateCostPerRejected=" + Number(cost.GateCostPerRejected) +
                ";internalFilterOrderPairCostPerRejected=" + Number(cost.PairCostPerRejected);
        }
    }
    internal string? InternalFilterOrderRevisionFor(bool compactAbi1Input) =>
        !compactAbi1Input && InternalFilterOrderEnabled ? InternalFilterOrderRevision : null;

    internal static string? RequestedInternalFilterOrderRevisionFor(NeowFamilyGpuPlan plan, bool compactAbi1Input) =>
        !compactAbi1Input && RequestInternalFilterOrder && !RequestIndirectStaging &&
        NeowInternalFilterOrder.Compile(plan).Enabled ? InternalFilterOrderRevision + ".Requested" : null;

    private void InitializeInternalFilterOrder(bool? requested)
    {
        _requestInternalFilterOrder = requested ?? RequestInternalFilterOrder;
        _internalFilterOrderPlan = NeowInternalFilterOrder.Compile(_plan);
        // Compact/fused modules retain the canonical matcher. A private output
        // is safe: its buffer is scratch only until stage2 overwrites it, before
        // any downstream executor is submitted. Private input is never scratch.
        string? transportIssue = _privateInput ? "PrivateInputUsesFusedMatcher" :
            _composite is not null || _bonesKMode != 0 || _capsulePhysicalMode != 0 ? "CompositeUsesCanonicalOrder" :
            _plan.JointTransformMetaOffset >= 0 ? "JointTransformUsesCanonicalOrder" :
            _partySource is not null || _plan.IsMultiplayer ? "MultiplayerUsesCanonicalOrder" :
            _requestIndirectStaging ? "IndirectDispatchUsesCanonicalOrder" : null;
        _internalFilterOrderEnabled = _requestInternalFilterOrder && transportIssue is null && _internalFilterOrderPlan.Enabled;
        string steps = _internalFilterOrderEnabled
            ? "InitialCurse>FinalCurseProjection>CompactedPairIdentity>CanonicalLocal"
            : "InitialCurse>PairIdentity>CanonicalLocal";
        string reason = !_requestInternalFilterOrder ? "ExplicitDiagnosticOrTestControl" :
            transportIssue ?? _internalFilterOrderPlan.Reason;
        _internalFilterOrderDescription = steps + "|" + reason.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
    }

    private void CreateInternalFilterOrderModule(Rid[] stageBuffers)
    {
        const string name = "NeowFamilyInternalFinalCurse";
        // _output has Capacity uints. Scratch contains one counter followed by
        // three uints per possible curse survivor. It does not alter header ABI.
        if (1L + 3L * NeowFamilyGpuPlan.CurseCapacity > Capacity)
            throw new InvalidOperationException("N.InternalFilterScratchCapacity");
        Rid shader = Add(FamilyGpuComputeUtility.CompileShader(_rd, ModuleSource(4), name));
        Rid pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(_rd, shader, name));
        if (!_rd.ComputePipelineIsValid(pipeline))
            throw new InvalidOperationException("N.InternalFilterPipelineInvalid");
        _internalFilterOrderPipeline = pipeline;
        _internalFilterOrderUniforms = Add(FamilyGpuComputeUtility.CreateUniformSet(_rd, shader, stageBuffers, name));
    }

    private int WindowDispatchCount(bool staged) => !staged ? 1 :
        UsesIndirectStaging ? 5 : InternalFilterOrderEnabled ? 4 : 3;
}
