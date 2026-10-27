using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed record CombatRewardGpuPlan(uint[] Meta, uint[] Pools, uint[] Ids, uint[] Predicates,
    uint[] Targets, Beta110GpuCombatRewardHotLoopPlan HotLoop, int Routes)
{
    private readonly string _sharedPrefixRevision = SharedVisibleSeedGpuCache.RevisionSuffix;
    internal bool UsesHotLoop => HotLoop.Enabled;
    internal string Revision(bool compact) => "C.CombatReward.Gpu." + (UsesHotLoop ? "NeutralHotLoop" : "GenericStreaming") +
        (compact ? ".CompactAbi1" : ".Dense") + ".CanonicalAbi1Ready.20260907.v1" + _sharedPrefixRevision;

    internal static CombatRewardGpuPlan? TryCreate(ExactSearchExecutionRequest request, CombatRewardReplay replay, out string issue)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        issue = "";
        var reward = replay.Plan;
        // Physical bounds of the retained donor, not Search admission gates.
        if (!RuntimeProfilePolicies.UsesBeta110SharedAlgorithms(request.ProfileId)) issue = "RootCodecOutsideDonor";
        else if (reward.PredicateCount > 64) issue = "PredicateMaskCapacity";
        else if (reward.OpeningConsumption.OrderedRelicIds.Contains(Beta110FastRelicCatalog.Kaleidoscope) &&
            !replay.KaleidoscopeCountOnly && replay.Catalog.OtherCharacterPools.Length > 15) issue = "KaleidoscopePermutationCapacity";
        if (issue.Length != 0) return null;
        var catalog = replay.CapsuleHeldReplay ? replay.Catalog :
            replay.Catalog with { OrdinaryRelics = [], PlayerRelicBuckets = [], SharedRelicConsumeShuffleLengths = [] };
        var policy = Beta110GpuCombatRewardRoutePolicy.UnpinnedAssumeUnperturbed;
        var hot = Beta110GpuCombatRewardHotLoopCompiler.Compile(request.Ascension, reward, catalog, policy);
        if (reward.HasDistinctBattleAssignments) hot = Beta110GpuCombatRewardHotLoopPlan.Disabled("PartyDistinctBattleAssignmentUsesGenericStreaming");
        uint[] meta = CombatRewardGpuPacking.BuildPlanMeta(request.Authority.PlayerSlotIndex,
            request.Authority.PlayersCount, request.Ascension, request.Authority.AllCharacterCardPoolsUnlocked == true,
            request.Authority.IsScrollBoxesAllowed == true, request.CharacterKey == BaseGameModelKeys.Characters.Defect,
            request.Authority.EffectAuthority?.BonesEligibleRelics?.Count(k => k != BaseGameModelKeys.Relics.NeowsBones) ?? 0,
            reward, 0x43463031, catalog, policy, 255, 255, 255, hot, replay.PrecedingNicheDraws,
            replay.CapsuleNicheAdvances, replay.KaleidoscopeCountOnly, replay.ConservativelyKeeps,
            replay.ReplayActualBonesPair, replay.BonesPool, replay.DynamicCapsuleNicheUnknown, replay.CapsuleHeldReplay);
        uint[] pools = CombatRewardGpuPacking.BuildPoolMeta(catalog, out uint[] ids, replay.ExplicitHeldIds);
        uint[] predicates = CombatRewardGpuPacking.BuildPredicates(reward, out uint[] targets);
        return new(meta, pools, ids, predicates, targets, hot, replay.RouteCount);
    }

    internal string ShaderSource(bool forceGeneric = false) => FamilyGpuComputeUtility.LoadEmbeddedShader("CombatRewardFamily.comp.glsl")
        .Replace("/*__C_CAPSULE_DEFINES__*/", "#define RT2_C_MULTIPLAYER " + (Meta[3] > 1 ? "1" : "0") +
            "\n#define RT2_C_CAPSULE_HELD " + (Meta[54] != 0 ? "1" : "0"), StringComparison.Ordinal)
        .Replace("/*__C_HOT_DEFINES__*/", Beta110GpuCombatRewardHotLoopCompiler.BuildShaderDefines(HotLoop,
            Beta110GpuCombatRewardRoutePolicy.UnpinnedAssumeUnperturbed, forceGeneric ? Beta110GpuCombatRewardHotLoopVariant.Disabled :
            Beta110GpuCombatRewardHotLoopVariant.OptimizedSingleRoute), StringComparison.Ordinal)
        .Replace("/*__C_COMMON__*/", FamilyGpuComputeUtility.LoadEmbeddedShader("Beta110GpuCombatRewardCommon.glsl"), StringComparison.Ordinal)
        .Replace("/*__C_STREAMING__*/", FamilyGpuComputeUtility.LoadEmbeddedShader("Beta110GpuCombatRewardStreamingP10A.glsl"), StringComparison.Ordinal);
}
