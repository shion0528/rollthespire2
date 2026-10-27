namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class NeowFamilyGpuExecutor
{
    internal const string GroupOutputRevision = "GroupOutput.Shared512.20261022.v1";
    private readonly bool _useGroupOutput;

    // The accepted production path defaults on. Only diagnostic builds retain
    // the same-binary off switch; releases contain no environment lookup.
    internal static bool RequestGroupOutput
    {
        get
        {
#if RT2_GPU_PROFILING
            return SearchOptimizationScope.Current.GroupedOutput &&
                System.Environment.GetEnvironmentVariable("RT2_N_GROUP_OUTPUT") != "0";
#else
            return SearchOptimizationScope.Current.GroupedOutput;
#endif
        }
    }

    private static bool SupportsGroupOutput(NeowFamilyGpuPlan plan, bool compactAbi1Input,
        bool privateInput, bool capsuleComposite, bool partyPacking, int bonesKMode, int capsulePhysicalMode) =>
        !compactAbi1Input && !privateInput && plan.UsesStagedDense && !plan.IsMultiplayer &&
        !capsuleComposite && !partyPacking && bonesKMode == 0 && capsulePhysicalMode == 0 &&
        plan.JointTransformMetaOffset < 0;

    internal bool GroupOutputEnabledFor(bool compactAbi1Input) => _useGroupOutput &&
        SupportsGroupOutput(_plan, compactAbi1Input, _privateInput, _composite is not null,
            _partySource is not null, _bonesKMode, _capsulePhysicalMode);

    internal bool GroupOutputObservedFor(bool compactAbi1Input) =>
        compactAbi1Input || _privateInput || !_plan.UsesStagedDense ? _pipeline.IsValid : _stagedModulesReady;

    internal string? GroupOutputRevisionFor(bool compactAbi1Input) =>
        GroupOutputObservedFor(compactAbi1Input) && GroupOutputEnabledFor(compactAbi1Input)
            ? GroupOutputRevision : null;

    // Before shader creation, requested work has a separate performance identity.
    // After initialization the observed result, including null, replaces it.
    internal static string? RequestedGroupOutputRevisionFor(NeowFamilyGpuPlan plan, bool compactAbi1Input,
        bool useGroupOutput, bool capsuleComposite = false, bool privateInput = false,
        bool partyPacking = false, int bonesKMode = 0, int capsulePhysicalMode = 0) =>
        useGroupOutput && SupportsGroupOutput(plan, compactAbi1Input, privateInput, capsuleComposite,
            partyPacking, bonesKMode, capsulePhysicalMode) ? GroupOutputRevision + ".Requested" : null;

    internal static string ApplyGroupOutput(string source, int stage, bool enabled)
    {
        if (!enabled || stage is not (0 or 4)) return source;
        const string seam = "#define RT2_N_GROUP_OUTPUT 0";
        int at = source.IndexOf(seam, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(seam, at + seam.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("N.GroupOutputSourceSeamChanged");
        return source.Replace(seam, "#define RT2_N_GROUP_OUTPUT 1", StringComparison.Ordinal);
    }
}
