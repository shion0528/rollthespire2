using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Events;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Relics;
using RolltheSpire2.Core.World;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.Contracts;

public enum SearchDisposition
{
    Match,
    NoMatch,
    Unknown,
    NotEvaluatedByPolicy,
    Unsupported,
    Cancelled
}

public enum SearchRunState
{
    Idle,
    Running,
    Completed,
    Cancelled,
    Faulted
}

public enum NeowSearchPreset
{
    None,
    BonesCapsuleAutomaticUpgradeAfterDeckChange
}

public enum SearchSequenceRangeMode
{
    FirstN,
    ExactSlot
}

public enum NormalCombatPotionRequirement
{
    Any,
    MustDrop,
    MustNotDrop
}

/// <summary>
/// Player-facing order semantics for Kaleidoscope's two independent reward groups.
/// This is a business semantic only; physical mirrored execution remains Planner-owned.
/// </summary>
public enum KaleidoscopeGroupOrderMode
{
    AnyOrder,
    ExactOrder
}

/// <summary>
/// Player-facing order semantics for the first N normal combat rewards in one reward
/// family. Card and potion families own independent order modes/permutations.
/// </summary>
public enum CombatRewardSequenceOrderMode
{
    Ordered,
    Unordered
}

public enum CombatPotionSlotRequirement
{
    NoDrop,
    DropAny,
    DropSpecific,
    Neutral
}

/// <summary>
/// Search-only structural semantics for one product-relevant Neow output. The
/// enum describes how to compare production Document groups. Product commitment
/// semantics additionally constrain optional picks; physical RNG scheduling and
/// acquisition-route execution remain downstream responsibilities.
/// </summary>
public enum NeowStructuredConditionKind
{
    ExactSingle,
    ExactUnorderedPair,
    ExactGroupedCapsuleMultiset,
    StructuredCardComposition,
    IndependentOfferGroupTargets,
    SpecialOffer
}

public enum NeowStructuredOutputKind
{
    Relic,
    Card,
    Potion,
    Curse
}

public enum NeowStructuredEffectScope
{
    ProductRelevantEffects,
    SelectableOfferGroups,
    NestedRelics,
    TransformResults,
    GeneratedPotions,
    BonesOfferedRelics,
    FinalCurse
}

public enum NeowSpecialOfferKind
{
    None,
    ScrollBoxesTripleClaw
}

/// <summary>
/// A single selected route relic. For Combat Reward Search, null is an explicit
/// product semantic: Fast assumes one synthetic unperturbed Reward continuation,
/// while Production Exact still reconstructs the real Neow world and admits only
/// provably Reward-neutral real routes. Rich single-seed analysis remains unchanged.
/// </summary>
public sealed record NeowRouteSearchCondition(ModelKey RouteRelicKey)
{
    public bool IsValid =>
        RouteRelicKey.IsValid &&
        RouteRelicKey.Category == BaseGameModelKeys.Categories.Relic;
}

/// <summary>
/// Route-scoped Search refinement over production effect groups. OutputKeys
/// preserve stable ModelKey identities only. Multi-output kinds may carry a
/// non-empty subset of the production outputs; filling every slot retains the
/// previous full-result comparison. Slot positions are normally presentation-only;
/// ordered Kaleidoscope is the explicit exception, where two canonical positional
/// slots carry result-group identity while OutputKeys remains the concrete Fast projection.
/// </summary>
public sealed record NeowStructuredEffectSearchCondition(
    ModelKey SourceRelicKey,
    NeowStructuredConditionKind Kind,
    NeowStructuredEffectScope Scope,
    NeowStructuredOutputKind OutputKind,
    IReadOnlyList<ModelKey> OutputKeys,
    NeowSpecialOfferKind SpecialOffer = NeowSpecialOfferKind.None,
    bool AllowDuplicateOutputs = false,
    KaleidoscopeGroupOrderMode KaleidoscopeGroupOrder = KaleidoscopeGroupOrderMode.AnyOrder)
{
    /// <summary>
    /// Canonical positional authority for ordered Kaleidoscope result groups.
    /// Exactly two positions are retained; null means wildcard / unconstrained.
    /// OutputKeys remains the concrete-target projection used by conservative Fast paths.
    /// </summary>
    public IReadOnlyList<ModelKey?> KaleidoscopePositionalSlots { get; init; } = Array.Empty<ModelKey?>();

    public bool IsEmpty =>
        !SourceRelicKey.IsValid ||
        (Kind != NeowStructuredConditionKind.SpecialOffer &&
         OutputKeys.Count == 0 &&
         !KaleidoscopePositionalSlots.Any(key => key.HasValue)) ||
        (Kind == NeowStructuredConditionKind.SpecialOffer && SpecialOffer == NeowSpecialOfferKind.None);
}

public sealed record ModelKeySetFilter(
    IReadOnlyList<ModelKey> Any,
    IReadOnlyList<ModelKey> All,
    IReadOnlyList<ModelKey> Ban)
{
    public static ModelKeySetFilter Empty { get; } = new(
        Array.Empty<ModelKey>(),
        Array.Empty<ModelKey>(),
        Array.Empty<ModelKey>());

    public bool IsEmpty => Any.Count == 0 && All.Count == 0 && Ban.Count == 0;
}

public sealed record ActModelKeySetFilter(
    int Act,
    ModelKeySetFilter Keys)
{
    public bool IsEmpty => Keys.IsEmpty;
}

/// <summary>
/// One active Ancient row. Ancient identities are production Event models
/// (EVENT:*), not a synthetic ANCIENT:* namespace. Multiple rows with the same
/// Act are OR branches. OptionAny is scoped to this exact Event identity, so an
/// identity from one row can never be combined with an option from another.
/// </summary>
public sealed record AncientSearchBranchCondition(
    int Act,
    ModelKey AncientKey,
    IReadOnlyList<ModelKey> OptionAny,
    IReadOnlyList<ModelKey> SeaGlassTargetAny)
{
    public bool IsValid =>
        Act is 2 or 3 &&
        AncientKey.IsValid &&
        AncientKey.Category == BaseGameModelKeys.Categories.Event;
}

public sealed record ActOrdinalModelKeySetFilter(
    int Act,
    int Ordinal,
    ModelKeySetFilter Keys)
{
    public bool IsEmpty => Keys.IsEmpty;
}

/// <summary>
/// Generic objective output filter. SourceRelicKey selects the production effect
/// group owner; OutputKeys are matched against typed effect TargetKey values.
/// Player decision targets are intentionally not part of this contract.
/// </summary>
public sealed record NeowEffectOutputSearchCondition(
    ModelKey SourceRelicKey,
    ModelKeySetFilter OutputKeys)
{
    public bool IsEmpty => !SourceRelicKey.IsValid || OutputKeys.IsEmpty;
}

public sealed record RelicSequenceSearchCondition(
    RelicSequenceKind Lane,
    SearchSequenceRangeMode RangeMode,
    int RangeValue,
    ModelKeySetFilter Keys)
{
    public bool IsEmpty => RangeValue <= 0 || Keys.IsEmpty;
}

public sealed record EventSequenceSearchCondition(
    int Act,
    EventPoolSourceKind? Source,
    SearchSequenceRangeMode RangeMode,
    int RangeValue,
    ModelKeySetFilter Keys)
{
    public bool IsEmpty => Act is < 1 or > 3 || RangeValue <= 0 || Keys.IsEmpty;
}

public enum EventResultConditionKind
{
    TrashHeapGrabCard,
    TrashHeapDiveRelic,
    ColorfulPhilosophersOfferedColor,
    FakeMerchantOfferedFakeRelic,
    MorphicGroveGroupInitialBasicsContains,
    SymbioteInitialBasicTransform,
    AromaOfChaosInitialBasicTransform,
    WhisperingHollowInitialBasicTransform,
    TrialNondescriptInitialBasicsContains,
    TrialCase,
    TinkerTimeTypeAndRider
}

public enum TrialCaseTarget { Merchant, Noble, Nondescript }
public enum TinkerCardTypeTarget { Attack, Skill, Power }
public enum TinkerRiderTarget { Sapping, Violence, Choking, Energized, Wisdom, Chaos, Expertise, Curious, Improvement }

public sealed record EventResultSearchCondition(
    EventResultConditionKind Kind,
    ModelKey TargetKey)
{
    // Authored/captured event-entry premise, never inferred from the starting deck.
    // Contains observes raw transformation identities, not final hook-modified cards.
    public RolltheSpire2.Core.Prediction.MorphicGroveScenario? MorphicGroveScenario { get; init; }
    // Optional second required output. Equal identities mean two copies, not one hit.
    public ModelKey? MorphicGroveSecondCard { get; init; }
    public TrialCaseTarget? TrialCase { get; init; }
    public TinkerCardTypeTarget? TinkerCardType { get; init; }
    public TinkerRiderTarget? TinkerRider { get; init; }

    public bool IsValid =>
        (TrialCase is null || Kind == EventResultConditionKind.TrialCase) &&
        (TinkerCardType is null && TinkerRider is null || Kind == EventResultConditionKind.TinkerTimeTypeAndRider) &&
        (MorphicGroveSecondCard is null || Kind is EventResultConditionKind.MorphicGroveGroupInitialBasicsContains or EventResultConditionKind.TrialNondescriptInitialBasicsContains) && (Kind switch
    {
        EventResultConditionKind.SymbioteInitialBasicTransform or EventResultConditionKind.AromaOfChaosInitialBasicTransform or
        EventResultConditionKind.WhisperingHollowInitialBasicTransform or EventResultConditionKind.TrialNondescriptInitialBasicsContains =>
            TargetKey.IsValid && TargetKey.Category == BaseGameModelKeys.Categories.Card &&
            (MorphicGroveSecondCard is null || MorphicGroveSecondCard is { IsValid: true, Category: BaseGameModelKeys.Categories.Card }) &&
            RolltheSpire2.Search.Semantics.EventResultTransformSemantics.IsInitialBasics(this),
        EventResultConditionKind.TrialCase => TargetKey == new ModelKey("EVENT", "TRIAL") && TrialCase is { } trial && Enum.IsDefined(trial),
        EventResultConditionKind.TinkerTimeTypeAndRider => TargetKey == new ModelKey("CARD", "MAD_SCIENCE") &&
            TinkerCardType is { } type && Enum.IsDefined(type) &&
            (TinkerRider is null || TinkerRider is { } rider && Enum.IsDefined(rider) && (int)rider / 3 == (int)type),
        EventResultConditionKind.MorphicGroveGroupInitialBasicsContains =>
            TargetKey.IsValid && TargetKey.Category == BaseGameModelKeys.Categories.Card &&
            (MorphicGroveSecondCard is null || MorphicGroveSecondCard is { IsValid: true, Category: BaseGameModelKeys.Categories.Card }) &&
            RolltheSpire2.Search.Semantics.MorphicGroveQuerySemantics.IsInitialBasics(MorphicGroveScenario),
        EventResultConditionKind.TrashHeapGrabCard =>
            TargetKey.IsValid && TargetKey.Category == BaseGameModelKeys.Categories.Card,
        EventResultConditionKind.TrashHeapDiveRelic or EventResultConditionKind.FakeMerchantOfferedFakeRelic =>
            TargetKey.IsValid && TargetKey.Category == BaseGameModelKeys.Categories.Relic,
        EventResultConditionKind.ColorfulPhilosophersOfferedColor =>
            TargetKey.IsValid && TargetKey.Category == BaseGameModelKeys.Categories.Character,
        _ => false
    });
}

public enum MerchantColorlessSlot
{
    Uncommon,
    Rare
}

public sealed record MerchantColorlessSlotCondition(
    int MerchantOrdinal,
    MerchantColorlessSlot Slot,
    ModelKey TargetCardKey)
{
    public bool IsValid =>
        MerchantOrdinal is >= 1 and <= 5 &&
        TargetCardKey.IsValid &&
        TargetCardKey.Category == BaseGameModelKeys.Categories.Card;
}

/// <summary>
/// Player-facing sequence semantics for the first N pristine Normal Merchant
/// U/R colorless slots. Ordered mode binds each non-empty slot to that exact
/// Merchant ordinal while null slots remain positional wildcards. Unordered mode
/// compares target multiplicities within the first N Merchants.
/// </summary>
public sealed record MerchantColorlessSequenceSearchCondition(
    int Count,
    CombatRewardSequenceOrderMode OrderMode,
    MerchantColorlessSlot Slot,
    IReadOnlyList<ModelKey?> Slots)
{
    public bool IsEmpty => Count <= 0 || !Slots.Take(Math.Max(0, Count)).Any(key => key.HasValue);
}

/// <summary>
/// Player-facing sequence semantics for the first N Shop Relic results. Ordered
/// mode binds each non-empty slot to that exact Merchant ordinal while null slots
/// remain positional wildcards. Unordered mode compares target multiplicities
/// within the first N Merchants.
/// </summary>
public sealed record RelicShopSequenceSearchCondition(
    int Count,
    CombatRewardSequenceOrderMode OrderMode,
    IReadOnlyList<ModelKey?> Slots)
{
    public bool IsEmpty => Count <= 0 || !Slots.Take(Math.Max(0, Count)).Any(key => key.HasValue);
}

public sealed record CombatCardRewardSequenceSearchCondition(
    int Count,
    CombatRewardSequenceOrderMode OrderMode,
    IReadOnlyList<ModelKey?> Slots)
{
    public bool IsEmpty => Count <= 0 || !Slots.Take(Math.Max(0, Count)).Any(key => key.HasValue);
}

public sealed record CombatPotionRewardSlotSearchCondition(
    CombatPotionSlotRequirement Requirement,
    ModelKey? PotionKey)
{
    public bool IsNeutral => Requirement == CombatPotionSlotRequirement.Neutral;

    public bool IsValid => Requirement switch
    {
        CombatPotionSlotRequirement.Neutral => !PotionKey.HasValue,
        CombatPotionSlotRequirement.NoDrop => !PotionKey.HasValue,
        CombatPotionSlotRequirement.DropAny => !PotionKey.HasValue,
        CombatPotionSlotRequirement.DropSpecific => PotionKey is { IsValid: true } key &&
            key.Category == BaseGameModelKeys.Categories.Potion,
        _ => false
    };
}

public sealed record CombatPotionRewardSequenceSearchCondition(
    int Count,
    CombatRewardSequenceOrderMode OrderMode,
    IReadOnlyList<CombatPotionRewardSlotSearchCondition> Slots)
{
    public bool IsEmpty => Count <= 0 || !Slots.Take(Math.Max(0, Count)).Any(slot => !slot.IsNeutral);
}

/// <summary>
/// One condition is evaluated against one battle. BattleOrdinal=0 means that a
/// single battle among the first three must satisfy every field in this record.
/// Multiple records are combined with All semantics on the same opening route.
/// </summary>
public enum CombatRewardFastRoutePolicy
{
    NotApplicable,
    PinnedRealRoute,
    UnpinnedAssumeUnperturbed
}

public enum CombatRewardExactRoutePolicy
{
    NotApplicable,
    PinnedRealRoute,
    UnpinnedVerifyNeutralRealRoute
}

public sealed record CombatRewardRoutePolicyContract(
    CombatRewardFastRoutePolicy FastPolicy,
    CombatRewardExactRoutePolicy ExactPolicy)
{
    public static CombatRewardRoutePolicyContract None { get; } = new(
        CombatRewardFastRoutePolicy.NotApplicable,
        CombatRewardExactRoutePolicy.NotApplicable);

    public bool IsPinned => ExactPolicy == CombatRewardExactRoutePolicy.PinnedRealRoute;
    public bool IsUnpinned => ExactPolicy == CombatRewardExactRoutePolicy.UnpinnedVerifyNeutralRealRoute;
}

public sealed record NormalCombatRewardSearchCondition(
    int BattleOrdinal,
    ModelKeySetFilter Cards,
    NormalCombatPotionRequirement PotionRequirement,
    ModelKeySetFilter Potions,
    int? MinimumGold,
    int? MaximumGold)
{
    public bool IsEmpty =>
        Cards.IsEmpty &&
        PotionRequirement == NormalCombatPotionRequirement.Any &&
        Potions.IsEmpty &&
        !MinimumGold.HasValue &&
        !MaximumGold.HasValue;
}

public sealed record NeowSearchFilter(
    ModelKeySetFilter NeowRelics,
    bool RequireNeowsBones,
    ModelKeySetFilter BonesRelics,
    IReadOnlyList<ModelKey> RequiredBonesCombination,
    bool RequireSmallCapsule,
    bool RequireLargeCapsule,
    ModelKeySetFilter CapsuleContainedRelics,
    bool RequireWhetstone,
    bool RequireWarPaint,
    ModelKey? RequiredFinalCurse,
    IReadOnlyList<ModelKey> BannedFinalCurses,
    NeowSearchPreset Preset)
{
    public static NeowSearchFilter Empty { get; } = new(
        ModelKeySetFilter.Empty,
        false,
        ModelKeySetFilter.Empty,
        Array.Empty<ModelKey>(),
        false,
        false,
        ModelKeySetFilter.Empty,
        false,
        false,
        null,
        Array.Empty<ModelKey>(),
        NeowSearchPreset.None);

    /// <summary>
    /// New route-oriented Neow contract. The legacy Any/All/Ban fields above are
    /// retained only for source compatibility with pre-UI1 fixtures and are never
    /// populated by the current Neow page.
    /// </summary>
    public NeowRouteSearchCondition? NeowRoute { get; init; }
    public IReadOnlyList<NeowStructuredEffectSearchCondition> StructuredNeowEffects { get; init; } =
        Array.Empty<NeowStructuredEffectSearchCondition>();

    public IReadOnlyList<ModelKey> RequiredBonesAcquisitionOrder { get; init; } = Array.Empty<ModelKey>();
    public IReadOnlyList<NeowEffectOutputSearchCondition> EffectOutputConditions { get; init; } =
        Array.Empty<NeowEffectOutputSearchCondition>();

    public IReadOnlyList<ActModelKeySetFilter> BossFilters { get; init; } = Array.Empty<ActModelKeySetFilter>();
    public IReadOnlyList<ActOrdinalModelKeySetFilter> BossOrdinalFilters { get; init; } =
        Array.Empty<ActOrdinalModelKeySetFilter>();
    public IReadOnlyList<AncientSearchBranchCondition> AncientBranchConditions { get; init; } =
        Array.Empty<AncientSearchBranchCondition>();
    public IReadOnlyList<ActModelKeySetFilter> AncientIdentityFilters { get; init; } = Array.Empty<ActModelKeySetFilter>();
    public IReadOnlyList<ActModelKeySetFilter> AncientOptionFilters { get; init; } = Array.Empty<ActModelKeySetFilter>();
    public IReadOnlyList<ActModelKeySetFilter> AncientSeaGlassTargetFilters { get; init; } =
        Array.Empty<ActModelKeySetFilter>();
    public AncientOptionConditionProfile AncientOptionConditions { get; init; } = AncientOptionConditionProfile.BroadDefault;

    public IReadOnlyList<RelicSequenceSearchCondition> RelicSequenceConditions { get; init; } =
        Array.Empty<RelicSequenceSearchCondition>();
    public IReadOnlyList<RelicShopSequenceSearchCondition> RelicShopSequenceConditions { get; init; } =
        Array.Empty<RelicShopSequenceSearchCondition>();
    public IReadOnlyList<EventSequenceSearchCondition> EventSequenceConditions { get; init; } =
        Array.Empty<EventSequenceSearchCondition>();
    public IReadOnlyList<EventResultSearchCondition> EventResultConditions { get; init; } =
        Array.Empty<EventResultSearchCondition>();
    public IReadOnlyList<MerchantColorlessSlotCondition> MerchantColorlessConditions { get; init; } =
        Array.Empty<MerchantColorlessSlotCondition>();
    public IReadOnlyList<MerchantColorlessSequenceSearchCondition> MerchantColorlessSequenceConditions { get; init; } =
        Array.Empty<MerchantColorlessSequenceSearchCondition>();
    public IReadOnlyList<NormalCombatRewardSearchCondition> NormalCombatRewardConditions { get; init; } =
        Array.Empty<NormalCombatRewardSearchCondition>();
    /// <summary>
    /// Canonical modern Combat Reward sequence semantics carried through the legacy
    /// execution DTO boundary. The flattened NormalCombatRewardConditions above are
    /// conservative Fast compatibility predicates, not the modern semantic authority.
    /// </summary>
    public CombatCardRewardSequenceSearchCondition? CombatCardRewardSequence { get; init; }
    public CombatPotionRewardSequenceSearchCondition? CombatPotionRewardSequence { get; init; }

    public bool HasNeowConstraints =>
        NeowRoute is { IsValid: true } ||
        !NeowRelics.IsEmpty ||
        RequireNeowsBones ||
        !BonesRelics.IsEmpty ||
        RequiredBonesCombination.Count > 0 ||
        RequiredBonesAcquisitionOrder.Count > 0 ||
        RequireSmallCapsule ||
        RequireLargeCapsule ||
        !CapsuleContainedRelics.IsEmpty ||
        RequireWhetstone ||
        RequireWarPaint ||
        RequiredFinalCurse.HasValue ||
        BannedFinalCurses.Count > 0 ||
        EffectOutputConditions.Any(condition => !condition.IsEmpty) ||
        StructuredNeowEffects.Any(condition => !condition.IsEmpty) ||
        Preset != NeowSearchPreset.None;

    public bool RequiresWorldDomain =>
        BossFilters.Any(filter => !filter.IsEmpty) ||
        BossOrdinalFilters.Any(filter => !filter.IsEmpty) ||
        AncientBranchConditions.Any(condition => condition.IsValid) ||
        AncientIdentityFilters.Any(filter => !filter.IsEmpty) ||
        AncientOptionFilters.Any(filter => !filter.IsEmpty) ||
        AncientSeaGlassTargetFilters.Any(filter => !filter.IsEmpty) ||
        EventSequenceConditions.Any(condition => !condition.IsEmpty);

    public bool RequiresRelicSequenceDomain =>
        RelicSequenceConditions.Any(condition => !condition.IsEmpty) ||
        RelicShopSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresEventSequenceDomain => EventSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresEventResultDomain => EventResultConditions.Any(condition => condition.IsValid);
    public bool RequiresMerchantColorlessDomain =>
        MerchantColorlessConditions.Any(condition => condition.IsValid) ||
        MerchantColorlessSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresCanonicalRootLocalDomain => RequiresEventResultDomain || RequiresMerchantColorlessDomain;
    // Event Result and canonical Merchant Colorless are root-local/canonical facts.
    // They intentionally do not imply World/event occurrence or opening-route authority.
    public bool RequiresWorldAuthority => RequiresWorldDomain || RequiresRelicSequenceDomain || RequiresNormalCombatRewardDomain;
    public bool RequiresNormalCombatRewardDomain =>
        NormalCombatRewardConditions.Any(condition => !condition.IsEmpty) ||
        CombatCardRewardSequence is { IsEmpty: false } ||
        CombatPotionRewardSequence is { IsEmpty: false };

    public bool RequiresEffectColdPath =>
        RequireNeowsBones ||
        !BonesRelics.IsEmpty ||
        RequiredBonesCombination.Count > 0 ||
        RequiredBonesAcquisitionOrder.Count > 0 ||
        RequireSmallCapsule ||
        RequireLargeCapsule ||
        !CapsuleContainedRelics.IsEmpty ||
        RequireWhetstone ||
        RequireWarPaint ||
        RequiredFinalCurse.HasValue ||
        BannedFinalCurses.Count > 0 ||
        EffectOutputConditions.Any(condition => !condition.IsEmpty) ||
        StructuredNeowEffects.Any(condition => !condition.IsEmpty) ||
        Preset != NeowSearchPreset.None ||
        RequiresNormalCombatRewardDomain;

    public SeedPredictionDomainSelection CheapPredictionDomains
    {
        get
        {
            // Neow is not a mandatory root for unrelated Search domains. In particular,
            // a modded Character may leave World/Ancient authority exact while Neow
            // catalog/effect authority is intentionally Partial. Do not let that local
            // Neow uncertainty block otherwise independent World or RelicSequence truth.
            SeedPredictionDomainSelection domains = SeedPredictionDomainSelection.None;
            if (HasNeowConstraints || RequiresNormalCombatRewardDomain || AncientOptionFilters.Any(f => f.Act == 1 && !f.IsEmpty))
            {
                domains |= SeedPredictionDomainSelection.Neow;
            }
            if (RequiresWorldDomain)
            {
                domains |= SeedPredictionDomainSelection.World;
            }
            if (RequiresRelicSequenceDomain)
            {
                domains |= SeedPredictionDomainSelection.RelicSequence;
            }
            return domains;
        }
    }
}

/// <summary>
/// Topology-independent query projection consumed by Production Exact. This is
/// deliberately separate from NeowSearchFilter: Family Execution must not inherit
/// the legacy Fast topology's DTO or its absolute-ordinal candidate contract.
/// </summary>
public sealed record ExactSearchEvaluationProjection(
    ModelKeySetFilter NeowRelics,
    bool RequireNeowsBones,
    ModelKeySetFilter BonesRelics,
    IReadOnlyList<ModelKey> RequiredBonesCombination,
    bool RequireSmallCapsule,
    bool RequireLargeCapsule,
    ModelKeySetFilter CapsuleContainedRelics,
    bool RequireWhetstone,
    bool RequireWarPaint,
    ModelKey? RequiredFinalCurse,
    IReadOnlyList<ModelKey> BannedFinalCurses,
    NeowSearchPreset Preset)
{
    public RolltheSpire2.Search.Semantics.TransformationAggregateCondition? TransformationAggregate { get; init; }
    public static ExactSearchEvaluationProjection Empty { get; } = new(
        ModelKeySetFilter.Empty, false, ModelKeySetFilter.Empty, Array.Empty<ModelKey>(),
        false, false, ModelKeySetFilter.Empty, false, false, null,
        Array.Empty<ModelKey>(), NeowSearchPreset.None);

    public NeowRouteSearchCondition? NeowRoute { get; init; }
    public IReadOnlyList<NeowStructuredEffectSearchCondition> StructuredNeowEffects { get; init; } =
        Array.Empty<NeowStructuredEffectSearchCondition>();
    public IReadOnlyList<ModelKey> RequiredBonesAcquisitionOrder { get; init; } = Array.Empty<ModelKey>();
    public IReadOnlyList<NeowEffectOutputSearchCondition> EffectOutputConditions { get; init; } =
        Array.Empty<NeowEffectOutputSearchCondition>();
    public IReadOnlyList<ActModelKeySetFilter> BossFilters { get; init; } = Array.Empty<ActModelKeySetFilter>();
    public IReadOnlyList<ActOrdinalModelKeySetFilter> BossOrdinalFilters { get; init; } =
        Array.Empty<ActOrdinalModelKeySetFilter>();
    public IReadOnlyList<AncientSearchBranchCondition> AncientBranchConditions { get; init; } =
        Array.Empty<AncientSearchBranchCondition>();
    public IReadOnlyList<ActModelKeySetFilter> AncientIdentityFilters { get; init; } = Array.Empty<ActModelKeySetFilter>();
    public IReadOnlyList<ActModelKeySetFilter> AncientOptionFilters { get; init; } = Array.Empty<ActModelKeySetFilter>();
    public IReadOnlyList<ActModelKeySetFilter> AncientSeaGlassTargetFilters { get; init; } =
        Array.Empty<ActModelKeySetFilter>();
    public AncientOptionConditionProfile AncientOptionConditions { get; init; } = AncientOptionConditionProfile.BroadDefault;
    public IReadOnlyList<RelicSequenceSearchCondition> RelicSequenceConditions { get; init; } =
        Array.Empty<RelicSequenceSearchCondition>();
    public IReadOnlyList<RelicShopSequenceSearchCondition> RelicShopSequenceConditions { get; init; } =
        Array.Empty<RelicShopSequenceSearchCondition>();
    public IReadOnlyList<EventSequenceSearchCondition> EventSequenceConditions { get; init; } =
        Array.Empty<EventSequenceSearchCondition>();
    public IReadOnlyList<EventResultSearchCondition> EventResultConditions { get; init; } =
        Array.Empty<EventResultSearchCondition>();
    public IReadOnlyList<MerchantColorlessSlotCondition> MerchantColorlessConditions { get; init; } =
        Array.Empty<MerchantColorlessSlotCondition>();
    public IReadOnlyList<MerchantColorlessSequenceSearchCondition> MerchantColorlessSequenceConditions { get; init; } =
        Array.Empty<MerchantColorlessSequenceSearchCondition>();
    public IReadOnlyList<NormalCombatRewardSearchCondition> NormalCombatRewardConditions { get; init; } =
        Array.Empty<NormalCombatRewardSearchCondition>();
    public CombatCardRewardSequenceSearchCondition? CombatCardRewardSequence { get; init; }
    public CombatPotionRewardSequenceSearchCondition? CombatPotionRewardSequence { get; init; }

    public bool HasNeowConstraints =>
        NeowRoute is { IsValid: true } || !NeowRelics.IsEmpty || RequireNeowsBones || !BonesRelics.IsEmpty ||
        RequiredBonesCombination.Count > 0 || RequiredBonesAcquisitionOrder.Count > 0 ||
        RequireSmallCapsule || RequireLargeCapsule || !CapsuleContainedRelics.IsEmpty || RequireWhetstone ||
        RequireWarPaint || RequiredFinalCurse.HasValue || BannedFinalCurses.Count > 0 ||
        EffectOutputConditions.Any(condition => !condition.IsEmpty) ||
        StructuredNeowEffects.Any(condition => !condition.IsEmpty) || Preset != NeowSearchPreset.None;

    public bool RequiresWorldDomain =>
        BossFilters.Any(filter => !filter.IsEmpty) || BossOrdinalFilters.Any(filter => !filter.IsEmpty) ||
        AncientBranchConditions.Any(condition => condition.IsValid) ||
        AncientIdentityFilters.Any(filter => !filter.IsEmpty) || AncientOptionFilters.Any(filter => !filter.IsEmpty) ||
        AncientSeaGlassTargetFilters.Any(filter => !filter.IsEmpty) ||
        EventSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresRelicSequenceDomain =>
        RelicSequenceConditions.Any(condition => !condition.IsEmpty) || RelicShopSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresEventSequenceDomain => EventSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresEventResultDomain => EventResultConditions.Any(condition => condition.IsValid);
    public bool RequiresMerchantColorlessDomain =>
        MerchantColorlessConditions.Any(condition => condition.IsValid) ||
        MerchantColorlessSequenceConditions.Any(condition => !condition.IsEmpty);
    public bool RequiresCanonicalRootLocalDomain => RequiresEventResultDomain || RequiresMerchantColorlessDomain || TransformationAggregate is not null;
    public bool RequiresNormalCombatRewardDomain =>
        NormalCombatRewardConditions.Any(condition => !condition.IsEmpty) ||
        CombatCardRewardSequence is { IsEmpty: false } || CombatPotionRewardSequence is { IsEmpty: false };
    public bool RequiresWorldAuthority => RequiresWorldDomain || RequiresRelicSequenceDomain || RequiresNormalCombatRewardDomain;
    public bool RequiresEffectColdPath =>
        RequireNeowsBones || !BonesRelics.IsEmpty || RequiredBonesCombination.Count > 0 ||
        RequiredBonesAcquisitionOrder.Count > 0 || RequireSmallCapsule || RequireLargeCapsule ||
        !CapsuleContainedRelics.IsEmpty || RequireWhetstone || RequireWarPaint || RequiredFinalCurse.HasValue ||
        BannedFinalCurses.Count > 0 || EffectOutputConditions.Any(condition => !condition.IsEmpty) ||
        StructuredNeowEffects.Any(condition => !condition.IsEmpty) || Preset != NeowSearchPreset.None ||
        RequiresNormalCombatRewardDomain;

    public SeedPredictionDomainSelection CheapPredictionDomains
    {
        get
        {
            SeedPredictionDomainSelection domains = SeedPredictionDomainSelection.None;
            if (HasNeowConstraints || RequiresNormalCombatRewardDomain || AncientOptionFilters.Any(f => f.Act == 1 && !f.IsEmpty))
                domains |= SeedPredictionDomainSelection.Neow;
            if (RequiresWorldDomain) domains |= SeedPredictionDomainSelection.World;
            if (RequiresRelicSequenceDomain) domains |= SeedPredictionDomainSelection.RelicSequence;
            return domains;
        }
    }

    internal NeowSearchFilter ToLegacyFilter() => new(
        NeowRelics, RequireNeowsBones, BonesRelics, RequiredBonesCombination,
        RequireSmallCapsule, RequireLargeCapsule, CapsuleContainedRelics,
        RequireWhetstone, RequireWarPaint, RequiredFinalCurse, BannedFinalCurses, Preset)
    {
        NeowRoute = NeowRoute,
        StructuredNeowEffects = StructuredNeowEffects,
        RequiredBonesAcquisitionOrder = RequiredBonesAcquisitionOrder,
        EffectOutputConditions = EffectOutputConditions,
        BossFilters = BossFilters,
        BossOrdinalFilters = BossOrdinalFilters,
        AncientBranchConditions = AncientBranchConditions,
        AncientIdentityFilters = AncientIdentityFilters,
        AncientOptionFilters = AncientOptionFilters,
        AncientSeaGlassTargetFilters = AncientSeaGlassTargetFilters,
        AncientOptionConditions = AncientOptionConditions,
        RelicSequenceConditions = RelicSequenceConditions,
        RelicShopSequenceConditions = RelicShopSequenceConditions,
        EventSequenceConditions = EventSequenceConditions,
        EventResultConditions = EventResultConditions,
        MerchantColorlessConditions = MerchantColorlessConditions,
        MerchantColorlessSequenceConditions = MerchantColorlessSequenceConditions,
        NormalCombatRewardConditions = NormalCombatRewardConditions,
        CombatCardRewardSequence = CombatCardRewardSequence,
        CombatPotionRewardSequence = CombatPotionRewardSequence
    };

    internal static ExactSearchEvaluationProjection FromLegacyFilter(NeowSearchFilter filter) => new(
        filter.NeowRelics, filter.RequireNeowsBones, filter.BonesRelics, filter.RequiredBonesCombination,
        filter.RequireSmallCapsule, filter.RequireLargeCapsule, filter.CapsuleContainedRelics,
        filter.RequireWhetstone, filter.RequireWarPaint, filter.RequiredFinalCurse, filter.BannedFinalCurses, filter.Preset)
    {
        NeowRoute = filter.NeowRoute,
        StructuredNeowEffects = filter.StructuredNeowEffects,
        RequiredBonesAcquisitionOrder = filter.RequiredBonesAcquisitionOrder,
        EffectOutputConditions = filter.EffectOutputConditions,
        BossFilters = filter.BossFilters,
        BossOrdinalFilters = filter.BossOrdinalFilters,
        AncientBranchConditions = filter.AncientBranchConditions,
        AncientIdentityFilters = filter.AncientIdentityFilters,
        AncientOptionFilters = filter.AncientOptionFilters,
        AncientSeaGlassTargetFilters = filter.AncientSeaGlassTargetFilters,
        AncientOptionConditions = filter.AncientOptionConditions,
        RelicSequenceConditions = filter.RelicSequenceConditions,
        RelicShopSequenceConditions = filter.RelicShopSequenceConditions,
        EventSequenceConditions = filter.EventSequenceConditions,
        EventResultConditions = filter.EventResultConditions,
        MerchantColorlessConditions = filter.MerchantColorlessConditions,
        MerchantColorlessSequenceConditions = filter.MerchantColorlessSequenceConditions,
        NormalCombatRewardConditions = filter.NormalCombatRewardConditions,
        CombatCardRewardSequence = filter.CombatCardRewardSequence,
        CombatPotionRewardSequence = filter.CombatPotionRewardSequence
    };
}

public sealed record LegacySearchInput(
    RuntimeProfileId ProfileId,
    ModelKey CharacterKey,
    int Ascension,
    string StartSeed,
    long ScanCount,
    int TargetMatchCount,
    int WorkerCount,
    NeowSearchFilter Filter,
    RuntimeContextAuthoritySnapshot Authority,
    GameVersionDetection Detection,
    bool IncludeDiagnostics = true);

public sealed record SearchRunOptions(
    string StartSeed,
    long ScanCount,
    int TargetMatchCount,
    int WorkerCount,
    bool IncludeDiagnostics = true,
    bool SkipExactValidation = false)
{
    public SearchResourceBudget? Resources { get; init; }
    private SearchOptimizationSettings _optimizations = SearchOptimizationSettings.AllEnabled;
    public SearchOptimizationSettings Optimizations
    {
        get => _optimizations;
        init => _optimizations = value ?? SearchOptimizationSettings.AllEnabled;
    }
}

public sealed record ExactSearchExecutionRequest(
    CompiledSearch CompiledSearch,
    SearchRunOptions RunOptions,
    string CanonicalStartSeed,
    long ResolvedScanCount,
    ExactSearchEvaluationProjection Evaluation,
    CombatRewardRoutePolicyContract CombatRewardRoutePolicy,
    string SnapshotFingerprint)
{
    internal IReadOnlyList<RolltheSpire2.Core.Neow.NeowChoiceResult>? PartyOpeningChoices { get; init; }
    internal RolltheSpire2.Core.Rewards.PreparedCombatRewardPools? PreparedRewardPools { get; init; }
    public RuntimeProfileId ProfileId => CompiledSearch.Context.ProfileId;
    public ModelKey CharacterKey => CompiledSearch.Context.CharacterKey;
    public int Ascension => CompiledSearch.Context.Ascension;
    public long ScanCount => ResolvedScanCount;
    public int TargetMatchCount => RunOptions.TargetMatchCount;
    public int WorkerCount => RunOptions.WorkerCount;
    public RuntimeContextAuthoritySnapshot Authority => CompiledSearch.Context.Authority;
    public GameVersionDetection Detection => CompiledSearch.Context.Detection;
    public bool IncludeDiagnostics => RunOptions.IncludeDiagnostics;
}

public sealed record ExactSearchExecutionCompileResult(
    bool Success,
    ExactSearchExecutionRequest? Plan,
    SearchDisposition Disposition,
    string Issue)
{
    public static ExactSearchExecutionCompileResult Accepted(ExactSearchExecutionRequest plan) =>
        new(true, plan, SearchDisposition.NoMatch, string.Empty);

    public static ExactSearchExecutionCompileResult Rejected(SearchDisposition disposition, string issue) =>
        new(false, null, disposition, issue);
}
public sealed record SearchExecutionRequest(
    CompiledSearch CompiledSearch,
    SearchRunOptions RunOptions,
    string CanonicalStartSeed,
    long ResolvedScanCount,
    NeowSearchFilter Filter,
    CombatRewardRoutePolicyContract CombatRewardRoutePolicy,
    string SnapshotFingerprint)
{

    public RuntimeProfileId ProfileId => CompiledSearch.Context.ProfileId;
    public ModelKey CharacterKey => CompiledSearch.Context.CharacterKey;
    public int Ascension => CompiledSearch.Context.Ascension;
    public long ScanCount => ResolvedScanCount;
    public int TargetMatchCount => RunOptions.TargetMatchCount;
    public int WorkerCount => RunOptions.WorkerCount;
    public RuntimeContextAuthoritySnapshot Authority => CompiledSearch.Context.Authority;
    public GameVersionDetection Detection => CompiledSearch.Context.Detection;
    public bool IncludeDiagnostics => RunOptions.IncludeDiagnostics;


}

public sealed record SearchMatchEvidence(
    string Code,
    ModelKey? RelatedKey = null,
    string? RouteId = null,
    IReadOnlyList<ModelKey>? AcquisitionOrder = null,
    EvidenceCode EvidenceCode = default,
    string? ChoicePolicyId = null,
    string? OutcomeId = null,
    IReadOnlyList<ModelKey>? AutomaticEffectKeys = null,
    RuntimeProfileId? ProfileId = null,
    int? Act = null,
    string? StreamDomain = null,
    SourceAuthority? Authority = null,
    string? AuthorityFingerprint = null,
    IReadOnlyList<ModelKey>? OrderedOptionKeys = null,
    int? Ordinal = null,
    string? ConditionId = null);

/// <summary>
/// Stable proof that all route-related conditions were satisfied by one legal
/// opening route. Global seed conditions may add evidence, but never manufacture
/// or widen OpeningRouteId.
/// </summary>
public sealed record SearchMatchWitness(
    string Seed,
    string OpeningRouteId,
    IReadOnlyList<ModelKey> AcquisitionOrder,
    IReadOnlyList<string> MatchedConditions,
    string RewardContinuationFingerprint,
    int? MatchedBattleOrdinal,
    IReadOnlyList<EvidenceCode> EvidenceCodes)
{
    public string RewardRouteGroupId { get; init; } = string.Empty;
    public string ChoicePolicyId { get; init; } = string.Empty;
    public string OutcomeId { get; init; } = string.Empty;
}

/// <summary>
/// Search/Exact disposition plus evidence and route witness. FailureCode is a
/// developer-facing technical reason for this Search evaluation; it is not an
/// PredictionWarning, Planner fallback, GPU recovery result, capability status, or
/// player-facing compatibility message.
/// </summary>
public sealed record SearchQueryEvaluation(
    SearchDisposition Disposition,
    IReadOnlyList<SearchMatchEvidence> Evidence,
    IReadOnlyList<string> MatchedRouteIds,
    IReadOnlyList<SearchMatchWitness> Witnesses,
    string FailureCode = "")
{
    // Diagnostic only: preserve the first failed predicate for each attempted opening.
    public IReadOnlyList<SearchRouteRejection> RouteRejections { get; init; } = Array.Empty<SearchRouteRejection>();
    public static SearchQueryEvaluation Match(
        IReadOnlyList<SearchMatchEvidence>? evidence = null,
        IReadOnlyList<string>? routeIds = null,
        IReadOnlyList<SearchMatchWitness>? witnesses = null) =>
        new(
            SearchDisposition.Match,
            evidence ?? Array.Empty<SearchMatchEvidence>(),
            routeIds ?? Array.Empty<string>(),
            witnesses ?? Array.Empty<SearchMatchWitness>());

    public static SearchQueryEvaluation NoMatch(string code) =>
        new(SearchDisposition.NoMatch, Array.Empty<SearchMatchEvidence>(), Array.Empty<string>(), Array.Empty<SearchMatchWitness>(), code);

    public static SearchQueryEvaluation Unknown(string code) =>
        new(SearchDisposition.Unknown, Array.Empty<SearchMatchEvidence>(), Array.Empty<string>(), Array.Empty<SearchMatchWitness>(), code);

    public static SearchQueryEvaluation NotEvaluatedByPolicy(string code) =>
        new(SearchDisposition.NotEvaluatedByPolicy, Array.Empty<SearchMatchEvidence>(), Array.Empty<string>(), Array.Empty<SearchMatchWitness>(), code);

    public static SearchQueryEvaluation Unsupported(string code) =>
        new(SearchDisposition.Unsupported, Array.Empty<SearchMatchEvidence>(), Array.Empty<string>(), Array.Empty<SearchMatchWitness>(), code);
}

public sealed record SearchRouteRejection(string RouteId, IReadOnlyList<ModelKey> AcquisitionOrder,
    SearchDisposition Disposition, string Reason);

public sealed record SearchCandidate(
    string Seed,
    RuntimeProfileId ProfileId,
    ModelKey CharacterKey,
    int Ascension,
    string SnapshotFingerprint,
    [property: System.Text.Json.Serialization.JsonPropertyName("AnalysisRequest")] SeedPredictionRequest PredictionRequest,
    SeedPredictionDocument? Document,
    RuntimeContextAuthoritySnapshot Authority,
    IReadOnlyList<SearchMatchEvidence> MatchEvidence,
    IReadOnlyList<string> MatchedRouteIds,
    IReadOnlyList<SearchMatchWitness> Witnesses)
{
    // An unverified candidate has no document, match evidence or witness. Fast is not truth.
    public bool IsUnverified { get; init; }
    public SearchMatchWitness? PrimaryWitness => Witnesses.FirstOrDefault();
}

public sealed record SearchProgressSnapshot(
    SearchRunState State,
    long ScannedCount,
    int MatchCount,
    long RequestedScanCount,
    int TargetMatchCount,
    double ElapsedSeconds,
    double SeedsPerSecond,
    SearchDisposition LastDisposition,
    string FailureCode,
    bool CancellationRequested)
{
    public int UnverifiedCandidateCount { get; init; }
    // UI observation only; never a pricing/calibration denominator.
    public double ObservedScanningSeconds { get; init; }
    public long ObservedScanningRoots { get; init; }
}

public sealed record SearchExecutionCompileResult(
    bool Success,
    SearchExecutionRequest? Plan,
    SearchDisposition Disposition,
    string Issue)
{
    public static SearchExecutionCompileResult Accepted(SearchExecutionRequest plan) =>
        new(true, plan, SearchDisposition.NoMatch, string.Empty);

    public static SearchExecutionCompileResult Rejected(SearchDisposition disposition, string issue) =>
        new(false, null, disposition, issue);
}
