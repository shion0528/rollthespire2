using System.Security.Cryptography;
using System.Text;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Rng;
using RolltheSpire2.Core.World;
using RolltheSpire2.Core.World.Beta109;
using RolltheSpire2.Core.World.Snapshots;

namespace RolltheSpire2.Core.Rewards;

/// <summary>
/// Pure copied-data projection of three consecutive default Monster rewards from
/// one completed Neow/Bones OpeningRewardContinuation. The first battle is tied
/// to the selected opening route. Battles two and three remain a consecutive
/// no-intervening-consumer baseline.
/// </summary>
public static class NormalCombatRewardSequencePredictor
{
    private const int CardsPerBattle = 3;
    private const float PotionOddsStep = 0.1f;
    private const float InitialCardRarityOffset = -0.05f;
    private const float CardRarityOffsetCap = 0.4f;
    private const float UncommonBase = 0.37f;
    private const string Beta109RewardAuditSha256 =
        "9e3dc19016f535609a5a751e9983523df9b262c1666a6b405ee4779cdcd906a4";
    private const string Beta110RewardAuditSha256 =
        "cb4a2009cc5cffd4383e65aa82104d2f39a1c9532d844c6861a77b53c75088f4";
    private const string Beta111CompatibilityAuditSha256 =
        "ca7cecb2a9900f8d621d34a29f90cf6b0167b684fa2e22674d2d472893ae5523";

    private static readonly IReadOnlyList<NormalCombatRewardConditionalAssumption> DefaultConditionalAssumptions =
        Array.AsReadOnly(new[]
        {
            NormalCombatRewardConditionalAssumption.NonTutorialMonsterPipeline,
            NormalCombatRewardConditionalAssumption.RewardHooksNoOp,
            NormalCombatRewardConditionalAssumption.ForcePotionRewardFalse,
            NormalCombatRewardConditionalAssumption.PositiveGoldProportionAndDefaultGoldRewardPresent,
            NormalCombatRewardConditionalAssumption.NoInterveningRewardsConsumersOrHookStateChanges,
            NormalCombatRewardConditionalAssumption.OpeningRouteContinuationApplied,
            NormalCombatRewardConditionalAssumption.FullKillGoldProportionOne,
            NormalCombatRewardConditionalAssumption.DefaultMonsterGoldRangeNoEncounterOverride
        });

    private sealed record FixedGoldImpact(
        RewardImpactSourceKey Source,
        int Amount);

    private sealed record EveryOtherPowerCardImpact(
        RewardImpactSourceKey Source,
        int Amount,
        bool RequiresIsFromCombat);

    private sealed record LimitedCardRewardUpgradeImpact(
        RewardImpactSourceKey Source,
        int MaxUses);

    private readonly record struct CardRarityRollOutcome(
        EffectCardRarity Rarity,
        float OffsetBefore,
        float RareThreshold,
        float UncommonThreshold,
        float OffsetAfter);

    private readonly record struct ContinuationSelection(
        OpeningRewardContinuation[] Selected,
        int RealRouteCount,
        int IgnoredPerturbingRouteCount,
        int IgnoredUnknownRouteCount);

    private sealed class RouteRewardImpactState
    {
        public int LastingCandyCounter { get; set; }

        public HashSet<RewardImpactSourceKey> UsedFirstCardRewardEnchantSources { get; } = new();

        public Dictionary<RewardImpactSourceKey, int> CardRewardUpgradeUses { get; } = new();

        public int GetUpgradeUses(RewardImpactSourceKey source) =>
            CardRewardUpgradeUses.TryGetValue(source, out int uses) ? uses : 0;

        public void IncrementUpgradeUses(RewardImpactSourceKey source) =>
            CardRewardUpgradeUses[source] = GetUpgradeUses(source) + 1;
    }

    private sealed record RouteRewardImpactPlan(
        IReadOnlyList<RewardImpactSourceKey> Sources,
        bool ForcePotionReward,
        IReadOnlyList<bool> AdditionalCardRewardIsFromCombat,
        IReadOnlyDictionary<EffectCardType, IReadOnlyList<RewardImpactSourceKey>> ForcedUpgradeSourcesByType,
        IReadOnlyList<FixedGoldImpact> FixedGoldImpacts,
        IReadOnlyList<EveryOtherPowerCardImpact> EveryOtherPowerCardImpacts,
        IReadOnlyList<RewardImpactSourceKey> FirstCardRewardGlamSources,
        IReadOnlyList<LimitedCardRewardUpgradeImpact> LimitedCardRewardUpgradeImpacts,
        string Fingerprint)
    {
        public bool HasAnyImpact => Sources.Count > 0;
        public int AdditionalCardRewardCount => AdditionalCardRewardIsFromCombat.Count;

        public IReadOnlyList<RewardImpactSourceKey> ForcedUpgradeSources(EffectCardType cardType) =>
            ForcedUpgradeSourcesByType.TryGetValue(cardType, out IReadOnlyList<RewardImpactSourceKey>? sources)
                ? sources
                : Array.Empty<RewardImpactSourceKey>();

        public RouteRewardImpactState CreateState() => new();
    }

    public static NormalCombatRewardSequencePredictionResult Predict(
        IRuntimeProfile profile,
        string canonicalSeed,
        int playerSlotIndex,
        int ascension,
        IReadOnlyList<NeowChoiceResult> neowChoices,
        NeowEffectAuthoritySnapshot? effectAuthority,
        WorldAuthoritySnapshot? worldAuthority) =>
        PredictCore(
            profile,
            profile.ComputeRootSeed(canonicalSeed),
            canonicalSeed,
            playerSlotIndex,
            ascension,
            neowChoices,
            effectAuthority,
            worldAuthority,
            NormalCombatRewardProjectionRequest.RichAnalysis);

    internal static NormalCombatRewardSequencePredictionResult PredictFromRootHash(
        IRuntimeProfile profile,
        ulong rootHash,
        string seedIdentity,
        int playerSlotIndex,
        int ascension,
        IReadOnlyList<NeowChoiceResult> neowChoices,
        NeowEffectAuthoritySnapshot? effectAuthority,
        WorldAuthoritySnapshot? worldAuthority) =>
        PredictFromRootHash(
            profile, rootHash, seedIdentity, playerSlotIndex, ascension, neowChoices,
            effectAuthority, worldAuthority, NormalCombatRewardProjectionRequest.RichAnalysis);

    internal static NormalCombatRewardSequencePredictionResult PredictFromRootHash(
        IRuntimeProfile profile,
        ulong rootHash,
        string seedIdentity,
        int playerSlotIndex,
        int ascension,
        IReadOnlyList<NeowChoiceResult> neowChoices,
        NeowEffectAuthoritySnapshot? effectAuthority,
        WorldAuthoritySnapshot? worldAuthority,
        NormalCombatRewardProjectionRequest projectionRequest,
        PreparedCombatRewardPools? preparedPools = null) =>
        PredictCore(
            profile,
            rootHash,
            seedIdentity,
            playerSlotIndex,
            ascension,
            neowChoices,
            effectAuthority,
            worldAuthority,
            projectionRequest,
            preparedPools);

    private static NormalCombatRewardSequencePredictionResult PredictCore(
        IRuntimeProfile profile,
        ulong rootHash,
        string seedIdentity,
        int playerSlotIndex,
        int ascension,
        IReadOnlyList<NeowChoiceResult> neowChoices,
        NeowEffectAuthoritySnapshot? effectAuthority,
        WorldAuthoritySnapshot? worldAuthority,
        NormalCombatRewardProjectionRequest projectionRequest,
        PreparedCombatRewardPools? preparedPools = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(neowChoices);
        string fingerprint = effectAuthority?.SnapshotFingerprint ?? string.Empty;
        if (!profile.SupportsSeedRng)
        {
            return NormalCombatRewardSequencePredictionResult.Unsupported(
                profile.ProfileId,
                "RewardProfileSeedRngUnsupported",
                fingerprint);
        }
        if (effectAuthority is null)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(
                profile.ProfileId,
                "MissingRewardRuntimeAuthority",
                fingerprint);
        }
        if (effectAuthority.CapturedProfileId != profile.ProfileId)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(
                profile.ProfileId,
                "RewardAuthorityProfileMismatch",
                fingerprint);
        }
        if (!effectAuthority.HasExactFoundation ||
            !effectAuthority.CharacterRewardPoolExact ||
            effectAuthority.CharacterRewardPool is null)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(
                profile.ProfileId,
                effectAuthority.CharacterRewardPool is null
                    ? "MissingCharacterRewardPool"
                    : "CharacterRewardPoolAuthorityIncomplete",
                fingerprint);
        }
        if (!effectAuthority.HasExactFoundation ||
            !effectAuthority.PotionPoolExact ||
            effectAuthority.PotionPool is null)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(
                profile.ProfileId,
                effectAuthority.PotionPool is null
                    ? "MissingPotionPool"
                    : "PotionPoolAuthorityIncomplete",
                fingerprint);
        }

        if (!TryResolveStartingAct(profile, rootHash, seedIdentity, worldAuthority, out int act, out ModelKey actKey, out string actIssue))
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(profile.ProfileId, actIssue, fingerprint);
        }
        if (act is < 1 or > 3)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(
                profile.ProfileId,
                "StartingActUpgradeRuleOutsideAuditedActRange",
                fingerprint);
        }

        bool usePreparedPools = preparedPools?.IsCompatible(profile.ProfileId, effectAuthority, worldAuthority) == true;
        IReadOnlyList<NeowEffectCardSnapshot> cards = usePreparedPools ? preparedPools!.Cards : effectAuthority.CharacterRewardPool
            .Where(card => worldAuthority?.Beta109Generation?.IsMultiplayer == true || !card.IsMultiplayerOnly)
            // Post-combat CardReward uses runtime reward-pool membership. It does not
            // apply CardModel.CanBeGeneratedInCombat, which is a separate combat-only filter.
            .Where(card => card.EligibleForPostCombatRewardByPoolMembership)
            .Where(card => card.IsUnlockedInCapturedPool)
            .Where(card => card.Rarity is EffectCardRarity.Common or EffectCardRarity.Uncommon or EffectCardRarity.Rare)
            .OrderBy(card => card.PoolOrder)
            .ToList();
        IReadOnlyList<NeowEffectPotionSnapshot> potions = usePreparedPools ? preparedPools!.Potions : effectAuthority.PotionPool
            .Where(potion => worldAuthority?.Beta109Generation?.IsMultiplayer == true || !potion.IsMultiplayerOnly)
            .Where(potion => potion.Rarity is EffectPotionRarity.Common or EffectPotionRarity.Uncommon or EffectPotionRarity.Rare)
            .OrderBy(potion => potion.PoolOrder)
            .ToList();
        string? poolIssue = usePreparedPools ? null : ValidatePools(profile.ProfileId, cards, potions);
        if (poolIssue is not null)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(profile.ProfileId, poolIssue, fingerprint);
        }

        OpeningRewardContinuation[] realContinuations = BuildChoiceContinuations(neowChoices);
        if (realContinuations.Length == 0)
        {
            return NormalCombatRewardSequencePredictionResult.Unknown(
                profile.ProfileId,
                "OpeningRewardContinuationMissing",
                fingerprint);
        }

        ContinuationSelection continuationSelection = SelectContinuations(realContinuations, projectionRequest);
        OpeningRewardContinuation[] continuations = continuationSelection.Selected;
        bool noEligibleSearchRewardRoute =
            projectionRequest.Scope == NormalCombatRewardProjectionScope.SearchExact &&
            continuations.Length == 0;

        RewardImpactSourceKey[] activeImpactSources = continuations
            .SelectMany(continuation => continuation.ActiveRewardImpactSources)
            .Where(source => source.IsValid)
            .Distinct()
            .OrderBy(source => source.Serialized, StringComparer.Ordinal)
            .ToArray();

        string cardPoolFingerprint = usePreparedPools ? preparedPools!.CardPoolFingerprint : BuildCardPoolFingerprint(cards);
        string potionPoolFingerprint = usePreparedPools ? preparedPools!.PotionPoolFingerprint : BuildPotionPoolFingerprint(potions);
        IGrouping<string, OpeningRewardContinuation>[] grouped = continuations
            .GroupBy(
                continuation => continuation.RewardsRngState is null
                    ? "unknown:" + continuation.Route.RouteId
                    : string.Join("|",
                        continuation.ContinuationFingerprint,
                        cardPoolFingerprint,
                        potionPoolFingerprint),
                StringComparer.Ordinal)
            .OrderBy(group => group.Min(route => route.Route.RouteOrder))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        int clampedAscension = Math.Clamp(ascension, 0, 10);
        int projectedRouteCount = 0;
        var routeResults = new List<NormalCombatRewardRoutePredictionResult>(grouped.Length);
        foreach (IGrouping<string, OpeningRewardContinuation> group in grouped)
        {
            OpeningRewardContinuation representative = group
                .OrderBy(route => route.Route.RouteOrder)
                .ThenBy(route => route.Route.RouteId, StringComparer.Ordinal)
                .First();
            OpeningRewardRouteDescriptor[] equivalentRoutes = group
                .Select(route => route.Route)
                .OrderBy(route => route.RouteOrder)
                .ThenBy(route => route.RouteId, StringComparer.Ordinal)
                .ToArray();
            string groupId = "reward.route." + BuildFingerprint(new[]
            {
                representative.ContinuationFingerprint,
                string.Join(",", equivalentRoutes.Select(route => route.RouteId))
            })[..16];

            if (!representative.CanProjectRewards)
            {
                string issue = representative.UnknownReasonCodes.FirstOrDefault() ??
                               "OpeningRewardContinuationUnavailable";
                routeResults.Add(new NormalCombatRewardRoutePredictionResult(
                    groupId,
                    representative,
                    equivalentRoutes,
                    SeedDomainEvaluationStatus.Unknown,
                    Array.Empty<NormalCombatRewardBattleResult>(),
                    PredictionPrecision.Unknown,
                    representative.Authority,
                    representative.Completeness,
                    issue,
                    representative.ContinuationFingerprint,
                    representative.Route.EvidenceCode));
                continue;
            }

            projectedRouteCount++;
            routeResults.Add(PredictRoute(
                profile.ProfileId,
                representative,
                equivalentRoutes,
                groupId,
                act,
                actKey,
                clampedAscension,
                cards,
                potions, projectionRequest.BattleCount));
        }

        bool anyEvaluated = routeResults.Any(route => route.Status == SeedDomainEvaluationStatus.Evaluated);
        PredictionPrecision overallPrecision = noEligibleSearchRewardRoute
            ? PredictionPrecision.Exact
            : anyEvaluated
                ? PredictionPrecision.Partial
                : PredictionPrecision.Unknown;
        SeedDomainEvaluationStatus overallStatus = noEligibleSearchRewardRoute
            ? SeedDomainEvaluationStatus.Evaluated
            : anyEvaluated
                ? SeedDomainEvaluationStatus.Evaluated
                : SeedDomainEvaluationStatus.Unknown;
        int maxCallCount = routeResults
            .Where(route => route.Status == SeedDomainEvaluationStatus.Evaluated)
            .SelectMany(route => route.Battles)
            .Select(battle => battle.RngCallCountAfter)
            .DefaultIfEmpty(0)
            .Max();
        string authorityFingerprint = BuildFingerprint(new[]
        {
            effectAuthority.SnapshotFingerprint,
            worldAuthority?.SnapshotFingerprint ?? string.Empty,
            profile.ProfileId.ToString(),
            seedIdentity,
            playerSlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clampedAscension.ToString(System.Globalization.CultureInfo.InvariantCulture),
            actKey.Serialized,
            cardPoolFingerprint,
            potionPoolFingerprint,
            projectionRequest.Scope.ToString(),
            projectionRequest.RouteSelectionMode.ToString(),
            continuationSelection.RealRouteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            continuations.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            continuationSelection.IgnoredPerturbingRouteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            continuationSelection.IgnoredUnknownRouteCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", routeResults.Select(route =>
                $"{route.RouteGroupId}:{route.Status}:{route.ContinuationFingerprint}")),
            string.Join(",", DefaultConditionalAssumptions),
            OpeningCombatRewardImpactAdapterRegistry.RegistryVersion,
            string.Join(",", activeImpactSources.Select(source => source.Serialized)),
            "normal-combat-reward-opening-route-continuation-v3",
            RuntimeProfilePolicies.AuthorityId(profile.ProfileId),
            RuntimeProfilePolicies.AuditFingerprint(profile.ProfileId),
            RuntimeProfilePolicies.CatalogFingerprintPrefix(profile.ProfileId),
            RewardAuditSha256(profile.ProfileId)
        });

        return new NormalCombatRewardSequencePredictionResult(
            overallStatus,
            profile.ProfileId,
            act,
            actKey,
            NormalCombatRewardBaselinePolicy.OpeningRouteContinuationNoInterveningConsumers,
            Array.Empty<NormalCombatRewardBattleResult>(),
            overallPrecision,
            effectAuthority.SourceAuthority,
            effectAuthority.Completeness,
            "player.rewards",
            maxCallCount,
            noEligibleSearchRewardRoute
                ? "NoEligibleSearchRewardRoute"
                : anyEvaluated ? string.Empty : "NoOpeningRewardRouteCanProject",
            authorityFingerprint,
            Evidence(profile.ProfileId, "sequence-by-opening-route"),
            new[]
            {
                new PredictionDiagnostic(PredictionDiagnosticCodes.PredictionDomain, PredictionDomain.NormalCombatRewardSequence.ToString()),
                new PredictionDiagnostic(PredictionDiagnosticCodes.PredictionScope, PredictionScope.OpeningNormalCombatRewardSequence.ToString()),
                new PredictionDiagnostic(PredictionDiagnosticCodes.RngCalls, maxCallCount.ToString()),
                new PredictionDiagnostic("reward-rng-stream", "player.rewards"),
                new PredictionDiagnostic("reward-baseline-policy", NormalCombatRewardBaselinePolicy.OpeningRouteContinuationNoInterveningConsumers.ToString()),
                new PredictionDiagnostic("reward-starting-act", act.ToString()),
                new PredictionDiagnostic("reward-card-pool-count", cards.Count.ToString()),
                new PredictionDiagnostic("reward-potion-pool-count", potions.Count.ToString()),
                new PredictionDiagnostic("reward-projection-scope", projectionRequest.Scope.ToString()),
                new PredictionDiagnostic("reward-route-selection-mode", projectionRequest.RouteSelectionMode.ToString()),
                new PredictionDiagnostic("reward-real-opening-route-count", continuationSelection.RealRouteCount.ToString()),
                new PredictionDiagnostic("reward-opening-route-count", continuations.Length.ToString()),
                new PredictionDiagnostic("reward-ignored-perturbing-route-count", continuationSelection.IgnoredPerturbingRouteCount.ToString()),
                new PredictionDiagnostic("reward-ignored-unknown-route-count", continuationSelection.IgnoredUnknownRouteCount.ToString()),
                new PredictionDiagnostic("reward-no-eligible-search-route", noEligibleSearchRewardRoute.ToString().ToLowerInvariant()),
                new PredictionDiagnostic("reward-continuation-group-count", routeResults.Count.ToString()),
                new PredictionDiagnostic("reward-modern-direct-audit", RuntimeProfilePolicies.IsModernCore(profile.ProfileId).ToString().ToLowerInvariant()),
                new PredictionDiagnostic("reward-source-audit-sha256", RewardAuditSha256(profile.ProfileId)),
                new PredictionDiagnostic(PredictionDiagnosticCodes.RuntimeAuthorityId, RuntimeProfilePolicies.AuthorityId(profile.ProfileId)),
                new PredictionDiagnostic(PredictionDiagnosticCodes.RuntimeAuditFingerprint, RuntimeProfilePolicies.AuditFingerprint(profile.ProfileId)),
                new PredictionDiagnostic(PredictionDiagnosticCodes.RuntimeCardCatalogFingerprint, effectAuthority.CatalogFingerprint),
                new PredictionDiagnostic(PredictionDiagnosticCodes.CardBaseOddsPolicy, RuntimeProfilePolicies.BaseOddsPolicy(profile.ProfileId).ToString()),
                new PredictionDiagnostic("reward-rarity-policy", "Encounter=StatefulCardRarityOdds.Roll;Candy=Other.BaseOdds.NoPityMutation"),
                new PredictionDiagnostic("reward-gold-projection", NormalCombatGoldProjectionStatus.ConditionalDefaultMonsterFullKill.ToString()),
                new PredictionDiagnostic("reward-card-creation-flags", "IsFromCombat|IsCardReward"),
                new PredictionDiagnostic("reward-force-potion-policy", "opening-relic-adapter-aware"),
                new PredictionDiagnostic("reward-tutorial-policy", "non-tutorial"),
                new PredictionDiagnostic("reward-hook-policy", "known-opening-relic-adapters-no-other-hooks"),
                new PredictionDiagnostic("reward-impact-adapter-version", OpeningCombatRewardImpactAdapterRegistry.RegistryVersion),
                new PredictionDiagnostic("reward-impact-adapter-count", activeImpactSources.Length.ToString()),
                new PredictionDiagnostic("reward-impact-adapter-sources", string.Join(",", activeImpactSources.Select(source => source.Serialized))),
                new PredictionDiagnostic("reward-impact-source-policy", "RelicOnly;CardModifierPowerReserved"),
                new PredictionDiagnostic("reward-conditional-assumptions", "route-specific")
            })
        {
            Routes = routeResults,
            ProjectionScope = projectionRequest.Scope,
            RouteSelectionMode = projectionRequest.RouteSelectionMode,
            RealOpeningRewardRouteCount = continuationSelection.RealRouteCount,
            EligibleOpeningRewardRouteCount = continuations.Length,
            ProjectedOpeningRewardRouteCount = projectedRouteCount,
            CanonicalRewardRouteCount = grouped.Length,
            IgnoredPerturbingRouteCount = continuationSelection.IgnoredPerturbingRouteCount,
            IgnoredUnknownRouteCount = continuationSelection.IgnoredUnknownRouteCount,
            NoEligibleSearchRewardRoute = noEligibleSearchRewardRoute
        };
    }

    private static NormalCombatRewardRoutePredictionResult PredictRoute(
        RuntimeProfileId profileId,
        OpeningRewardContinuation continuation,
        IReadOnlyList<OpeningRewardRouteDescriptor> equivalentRoutes,
        string groupId,
        int act,
        ModelKey actKey,
        int ascension,
        IReadOnlyList<NeowEffectCardSnapshot> cards,
        IReadOnlyList<NeowEffectPotionSnapshot> potions, int battleCount)
    {
        if (battleCount is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(battleCount));
        if (!TryBuildRouteImpactPlan(profileId, continuation, out RouteRewardImpactPlan impactPlan, out string impactIssue))
        {
            return UnknownRoute(
                groupId,
                continuation,
                equivalentRoutes,
                impactIssue);
        }

        Xoshiro256StarStar rewards = continuation.RewardsRngState!.Restore();
        float potionOdds = continuation.PotionRewardState;
        float cardOffset = InitialCardRarityOffset;
        var battles = new List<NormalCombatRewardBattleResult>(battleCount);
        RouteRewardImpactState impactState = impactPlan.CreateState();
        IReadOnlyList<NormalCombatRewardConditionalAssumption> conditionalAssumptions =
            BuildConditionalAssumptions(impactPlan);

        for (int battleOrdinal = 1; battleOrdinal <= battleCount; battleOrdinal++)
        {
            int callsBefore = rewards.CallCount;
            float potionBefore = potionOdds;
            float rarityBefore = cardOffset;
            var trace = new List<NormalCombatRewardRngTraceEntry>();

            if (profileId == RuntimeProfileId.Stable107 && impactPlan.EveryOtherPowerCardImpacts.Count > 0)
            {
                impactState.LastingCandyCounter++;
                trace.Add(new NormalCombatRewardRngTraceEntry(
                    trace.Count,
                    battleOrdinal,
                    "lasting-candy-counter-before-reward",
                    $"Stable107CombatsSeen={impactState.LastingCandyCounter}",
                    rewards.CallCount));
            }

            bool potionDropped;
            if (impactPlan.ForcePotionReward && RuntimeProfilePolicies.IsModernCore(profileId))
            {
                // Beta109 PotionRewardOdds returns before the decision draw and
                // before pity mutation when White Beast Statue forces a potion.
                potionDropped = true;
                trace.Add(new NormalCombatRewardRngTraceEntry(
                    trace.Count,
                    battleOrdinal,
                    "potion-drop-gate-forced",
                    "WhiteBeastStatueBeta109EarlyReturn",
                    rewards.CallCount));
            }
            else
            {
                float potionRoll = NextFloat(
                    rewards,
                    trace,
                    battleOrdinal,
                    impactPlan.ForcePotionReward
                        ? "potion-drop-gate-forced-legacy"
                        : "potion-drop-gate");
                potionDropped = impactPlan.ForcePotionReward || potionRoll < potionOdds;
                potionOdds += potionDropped ? -PotionOddsStep : PotionOddsStep;
            }

            int minGold = ascension >= 3 ? 7 : 10;
            int maxGold = ascension >= 3 ? 15 : 20;
            int gold = NextIntRange(
                rewards,
                minGold,
                maxGold + 1,
                trace,
                battleOrdinal,
                "gold-amount-default-monster-full-kill");
            var goldRewards = new List<NormalCombatRewardGoldRewardResult>
            {
                new(
                    1,
                    gold,
                    true,
                    true,
                    null,
                    Evidence(profileId, "gold-default-monster-full-kill"))
            };
            foreach (FixedGoldImpact fixedGold in impactPlan.FixedGoldImpacts)
            {
                int rewardOrdinal = goldRewards.Count + 1;
                goldRewards.Add(new NormalCombatRewardGoldRewardResult(
                    rewardOrdinal,
                    fixedGold.Amount,
                    false,
                    false,
                    fixedGold.Source,
                    Evidence(profileId, "gold-fixed-opening-relic")));
                trace.Add(new NormalCombatRewardRngTraceEntry(
                    trace.Count,
                    battleOrdinal,
                    $"gold-fixed-reward-{rewardOrdinal}",
                    $"AddFixedGold:{fixedGold.Source.Serialized}:{fixedGold.Amount}",
                    rewards.CallCount,
                    IntResult: fixedGold.Amount));
            }

            NormalCombatRewardPotionResult potionResult;
            if (potionDropped)
            {
                float rarityRoll = NextFloat(rewards, trace, battleOrdinal, "potion-rarity");
                EffectPotionRarity potionRarity = RollPotionRarity(rarityRoll);
                List<NeowEffectPotionSnapshot> candidates = potions
                    .Where(item => item.Rarity == potionRarity)
                    .ToList();
                if (candidates.Count == 0)
                {
                    return UnknownRoute(
                        groupId,
                        continuation,
                        equivalentRoutes,
                        $"PotionPoolEmptyForRolledRarity:Battle{battleOrdinal}:{potionRarity}");
                }
                NeowEffectPotionSnapshot selectedPotion = NextItem(
                    rewards,
                    candidates,
                    trace,
                    battleOrdinal,
                    "potion-identity",
                    item => item.PotionKey);
                potionResult = new NormalCombatRewardPotionResult(
                    true,
                    selectedPotion.PotionKey,
                    selectedPotion.Rarity,
                    PredictionPrecision.Partial,
                    Evidence(profileId, "potion"));
            }
            else
            {
                potionResult = new NormalCombatRewardPotionResult(
                    false,
                    null,
                    null,
                    PredictionPrecision.Partial,
                    Evidence(profileId, "potion-none"));
            }

            var cardRewards = new List<NormalCombatRewardCardRewardResult>(
                1 + impactPlan.AdditionalCardRewardCount);
            NormalCombatRewardCardRewardResult primaryCards;
            try
            {
                primaryCards = GenerateCardReward(
                    profileId,
                    rewards,
                    trace,
                    battleOrdinal,
                    1,
                    true,
                    act,
                    ascension,
                    cards,
                    ref cardOffset,
                    impactPlan,
                    impactState);
                cardRewards.Add(primaryCards);
                for (int extraOrdinal = 0; extraOrdinal < impactPlan.AdditionalCardRewardCount; extraOrdinal++)
                {
                    cardRewards.Add(GenerateCardReward(
                        profileId,
                        rewards,
                        trace,
                        battleOrdinal,
                        extraOrdinal + 2,
                        impactPlan.AdditionalCardRewardIsFromCombat[extraOrdinal],
                        act,
                        ascension,
                        cards,
                        ref cardOffset,
                        impactPlan,
                        impactState));
                }
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("CardRewardPoolExhausted:", StringComparison.Ordinal))
            {
                return UnknownRoute(
                    groupId,
                    continuation,
                    equivalentRoutes,
                    ex.Message);
            }

            if (RuntimeProfilePolicies.IsModernCore(profileId) && impactPlan.EveryOtherPowerCardImpacts.Count > 0)
            {
                impactState.LastingCandyCounter++;
                trace.Add(new NormalCombatRewardRngTraceEntry(
                    trace.Count,
                    battleOrdinal,
                    "lasting-candy-counter-after-reward",
                    $"Beta109CombatRewardsSeen={impactState.LastingCandyCounter}",
                    rewards.CallCount));
            }

            NormalCombatRewardBattleProjectionScope projectionScope = battleOrdinal == 1
                ? NormalCombatRewardBattleProjectionScope.OpeningRouteFirstCombat
                : NormalCombatRewardBattleProjectionScope.ConsecutiveDefaultMonsterNoInterveningConsumers;
            battles.Add(new NormalCombatRewardBattleResult(
                battleOrdinal,
                act,
                actKey,
                projectionScope,
                gold,
                NormalCombatGoldProjectionStatus.ConditionalDefaultMonsterFullKill,
                true,
                potionResult,
                primaryCards.Cards,
                potionBefore,
                potionOdds,
                rarityBefore,
                cardOffset,
                PredictionPrecision.Partial,
                continuation.Authority,
                continuation.Completeness,
                "player.rewards",
                callsBefore,
                rewards.CallCount,
                projectionScope == NormalCombatRewardBattleProjectionScope.OpeningRouteFirstCombat
                    ? ConditionalReason(profileId, "opening-route-first-combat")
                    : ConditionalReason(profileId, "consecutive-no-intervening-consumer"),
                Evidence(profileId, battleOrdinal == 1 ? "battle-opening-route" : "battle-consecutive-baseline"))
            {
                RngTrace = trace,
                ConditionalAssumptions = conditionalAssumptions,
                CardRewards = cardRewards,
                GoldRewards = goldRewards,
                AppliedRewardImpactSources = impactPlan.Sources,
                RewardImpactFingerprint = BuildBattleImpactFingerprint(
                    impactPlan,
                    impactState,
                    battleOrdinal)
            });
        }

        return new NormalCombatRewardRoutePredictionResult(
            groupId,
            continuation,
            equivalentRoutes,
            SeedDomainEvaluationStatus.Evaluated,
            battles,
            PredictionPrecision.Partial,
            continuation.Authority,
            continuation.Completeness,
            string.Empty,
            continuation.ContinuationFingerprint,
            Evidence(profileId, "route"));
    }

    private static NormalCombatRewardCardRewardResult GenerateCardReward(
        RuntimeProfileId profileId,
        Xoshiro256StarStar rewards,
        List<NormalCombatRewardRngTraceEntry> trace,
        int battleOrdinal,
        int rewardOrdinal,
        bool isFromCombat,
        int act,
        int ascension,
        IReadOnlyList<NeowEffectCardSnapshot> cards,
        ref float cardOffset,
        RouteRewardImpactPlan impactPlan,
        RouteRewardImpactState impactState)
    {
        var selectedThisReward = new HashSet<ModelKey>();
        var cardResults = new List<NormalCombatRewardCardResult>(CardsPerBattle + 1);
        for (int cardOrdinal = 1; cardOrdinal <= CardsPerBattle; cardOrdinal++)
        {
            cardResults.Add(GenerateCard(
                profileId,
                rewards,
                trace,
                battleOrdinal,
                rewardOrdinal,
                cardOrdinal,
                isFromCombat,
                act,
                ascension,
                cards,
                selectedThisReward,
                ref cardOffset,
                requiredCardType: null));
        }

        foreach (EveryOtherPowerCardImpact impact in impactPlan.EveryOtherPowerCardImpacts)
        {
            bool triggerCounter = RuntimeProfilePolicies.IsModernCore(profileId)
                ? impactState.LastingCandyCounter % 2 == 1
                : impactState.LastingCandyCounter > 0 && impactState.LastingCandyCounter % 2 == 0;
            if (!triggerCounter || impact.RequiresIsFromCombat && !isFromCombat)
            {
                continue;
            }

            for (int added = 0; added < Math.Max(0, impact.Amount); added++)
            {
                if (!cards.Any(c => c.CardType == EffectCardType.Power)) continue;
                int cardOrdinal = cardResults.Count + 1;
                NormalCombatRewardCardResult generated = GenerateCard(
                    profileId,
                    rewards,
                    trace,
                    battleOrdinal,
                    rewardOrdinal,
                    cardOrdinal,
                    isFromCombat,
                    act,
                    ascension,
                    cards,
                    selectedThisReward,
                    ref cardOffset,
                    requiredCardType: EffectCardType.Power);
                cardResults.Add(generated with
                {
                    AppliedImpactSources = new[] { impact.Source }
                });
            }
        }

        ApplyLateCardRewardMutations(
            profileId,
            battleOrdinal,
            rewardOrdinal,
            cardResults,
            trace,
            rewards.CallCount,
            impactPlan,
            impactState);

        return new NormalCombatRewardCardRewardResult(
            rewardOrdinal,
            isFromCombat,
            cardResults,
            Evidence(profileId, rewardOrdinal == 1 ? "card-reward-primary" : "card-reward-additional"));
    }

    private static NormalCombatRewardCardResult GenerateCard(
        RuntimeProfileId profileId,
        Xoshiro256StarStar rewards,
        List<NormalCombatRewardRngTraceEntry> trace,
        int battleOrdinal,
        int rewardOrdinal,
        int cardOrdinal,
        bool isFromCombat,
        int act,
        int ascension,
        IReadOnlyList<NeowEffectCardSnapshot> cards,
        HashSet<ModelKey> selectedThisReward,
        ref float cardOffset,
        EffectCardType? requiredCardType)
    {
        string stagePrefix = rewardOrdinal == 1
            ? string.Empty
            : $"card-reward-{rewardOrdinal}-";
        string specialSuffix = requiredCardType is null ? string.Empty : $"-{requiredCardType.Value.ToString().ToLowerInvariant()}";
        float rarityRoll = NextFloat(
            rewards,
            trace,
            battleOrdinal,
            $"{stagePrefix}card-{cardOrdinal}{specialSuffix}-rarity");
        // The only typed extra card here is Candy: CardCreationSource.Other,
        // base odds and no Encounter pity read/update (including a Rare result).
        bool candy = requiredCardType == EffectCardType.Power;
        float rare = ascension >= 7 ? .0149f : .03f;
        CardRarityRollOutcome rarityOutcome = candy
            ? new(rarityRoll < rare ? EffectCardRarity.Rare : rarityRoll < rare + UncommonBase ? EffectCardRarity.Uncommon : EffectCardRarity.Common,
                cardOffset, rare, rare + UncommonBase, cardOffset)
            : RollCardRarity(rarityRoll, ascension, ref cardOffset);
        NormalCombatRewardRngTraceEntry rarityTrace = trace[^1];
        trace[^1] = rarityTrace with
        {
            CardRarityOffsetBefore = rarityOutcome.OffsetBefore,
            RareThreshold = rarityOutcome.RareThreshold,
            UncommonThreshold = rarityOutcome.UncommonThreshold,
            CardRarityOffsetAfter = rarityOutcome.OffsetAfter,
            RolledCardRarity = rarityOutcome.Rarity
        };

        IReadOnlyList<NeowEffectCardSnapshot> eligibleCards = requiredCardType is EffectCardType cardType
            ? cards.Where(card => card.CardType == cardType).ToArray()
            : cards;
        var exclusions = candy && !eligibleCards.Any(c => !selectedThisReward.Contains(c.CardKey))
            ? new HashSet<ModelKey>() : selectedThisReward;
        EffectCardRarity? selectedRarity = NextAvailableRarity(
            rarityOutcome.Rarity,
            eligibleCards,
            exclusions);
        if (selectedRarity is null)
        {
            throw new InvalidOperationException(
                $"CardRewardPoolExhausted:Battle{battleOrdinal}:Reward{rewardOrdinal}:Card{cardOrdinal}:{requiredCardType}");
        }

        List<NeowEffectCardSnapshot> candidates = eligibleCards
            .Where(card => card.Rarity == selectedRarity.Value && !exclusions.Contains(card.CardKey))
            .ToList();
        NeowEffectCardSnapshot selectedCard = NextItem(
            rewards,
            candidates,
            trace,
            battleOrdinal,
            $"{stagePrefix}card-{cardOrdinal}{specialSuffix}-identity",
            item => item.CardKey);
        selectedThisReward.Add(selectedCard.CardKey);

        float upgradeRoll = NextFloat(
            rewards,
            trace,
            battleOrdinal,
            $"{stagePrefix}card-{cardOrdinal}{specialSuffix}-upgrade-roll");
        float baseUpgradeOdds = CalculateBaseUpgradeOdds(selectedCard, act, ascension);
        NormalCombatCardUpgradeState upgradeState =
            selectedCard.CanUpgrade && upgradeRoll <= baseUpgradeOdds
                ? NormalCombatCardUpgradeState.Upgraded
                : NormalCombatCardUpgradeState.NotUpgraded;

        return new NormalCombatRewardCardResult(
            cardOrdinal,
            selectedCard.CardKey,
            selectedCard.Rarity,
            selectedCard.CanUpgrade,
            upgradeRoll,
            baseUpgradeOdds,
            upgradeState,
            isFromCombat,
            PredictionPrecision.Partial,
            Evidence(profileId, rewardOrdinal == 1 ? "card" : "additional-card-reward-card"))
        {
            CardType = selectedCard.CardType
        };
    }

    private static void ApplyLateCardRewardMutations(
        RuntimeProfileId profileId,
        int battleOrdinal,
        int rewardOrdinal,
        List<NormalCombatRewardCardResult> cards,
        List<NormalCombatRewardRngTraceEntry> trace,
        int rewardsCallCount,
        RouteRewardImpactPlan impactPlan,
        RouteRewardImpactState impactState)
    {
        // Egg relics are late deterministic mutations. Natural upgrade rolls still
        // consume their normal RNG calls before these operations run.
        for (int index = 0; index < cards.Count; index++)
        {
            NormalCombatRewardCardResult card = cards[index];
            IReadOnlyList<RewardImpactSourceKey> eggSources =
                impactPlan.ForcedUpgradeSources(card.CardType);
            if (card.IsUpgradable &&
                card.UpgradeState != NormalCombatCardUpgradeState.Upgraded &&
                eggSources.Count > 0)
            {
                cards[index] = card with
                {
                    UpgradeState = NormalCombatCardUpgradeState.Upgraded,
                    AppliedImpactSources = MergeSources(card.AppliedImpactSources, eggSources)
                };
            }
        }

        // Silken Tress applies to the first card reward only. Lasting Candy's
        // nested Power is already part of that reward before late mutations.
        foreach (RewardImpactSourceKey source in impactPlan.FirstCardRewardGlamSources)
        {
            if (!impactState.UsedFirstCardRewardEnchantSources.Add(source))
            {
                continue;
            }

            for (int index = 0; index < cards.Count; index++)
            {
                NormalCombatRewardCardResult card = cards[index];
                cards[index] = card with
                {
                    Enchantments = MergeEnchantments(
                        card.Enchantments,
                        NormalCombatCardEnchantment.Glam),
                    AppliedImpactSources = MergeSources(
                        card.AppliedImpactSources,
                        new[] { source })
                };
            }
            trace.Add(new NormalCombatRewardRngTraceEntry(
                trace.Count,
                battleOrdinal,
                $"card-reward-{rewardOrdinal}-silken-tress",
                $"EnchantWithGlam:{source.Serialized}",
                rewardsCallCount));
        }

        // Silver Crucible consumes one of its first-three-card-reward charges for
        // each card reward it processes. It upgrades only cards that remain
        // upgradable after natural rolls and Egg mutations.
        foreach (LimitedCardRewardUpgradeImpact impact in impactPlan.LimitedCardRewardUpgradeImpacts)
        {
            int usesBefore = impactState.GetUpgradeUses(impact.Source);
            if (usesBefore >= impact.MaxUses)
            {
                continue;
            }

            for (int index = 0; index < cards.Count; index++)
            {
                NormalCombatRewardCardResult card = cards[index];
                if (!card.IsUpgradable || card.UpgradeState == NormalCombatCardUpgradeState.Upgraded)
                {
                    continue;
                }
                cards[index] = card with
                {
                    UpgradeState = NormalCombatCardUpgradeState.Upgraded,
                    AppliedImpactSources = MergeSources(
                        card.AppliedImpactSources,
                        new[] { impact.Source })
                };
            }

            impactState.IncrementUpgradeUses(impact.Source);
            trace.Add(new NormalCombatRewardRngTraceEntry(
                trace.Count,
                battleOrdinal,
                $"card-reward-{rewardOrdinal}-silver-crucible",
                $"UpgradeReward:{impact.Source.Serialized}:{usesBefore + 1}/{impact.MaxUses}",
                rewardsCallCount));
        }
    }

    private static IReadOnlyList<RewardImpactSourceKey> MergeSources(
        IReadOnlyList<RewardImpactSourceKey> existing,
        IEnumerable<RewardImpactSourceKey> additions) =>
        existing
            .Concat(additions)
            .Where(source => source.IsValid)
            .Distinct()
            .OrderBy(source => source.Serialized, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<NormalCombatCardEnchantment> MergeEnchantments(
        IReadOnlyList<NormalCombatCardEnchantment> existing,
        NormalCombatCardEnchantment addition) =>
        existing.Append(addition).Distinct().OrderBy(item => item).ToArray();

    private static string BuildBattleImpactFingerprint(
        RouteRewardImpactPlan plan,
        RouteRewardImpactState state,
        int battleOrdinal) =>
        OpeningCombatRewardImpactAdapterRegistry.FingerprintParts(
            "opening-combat-reward-impact-battle-state-v1",
            plan.Fingerprint,
            battleOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.LastingCandyCounter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", state.UsedFirstCardRewardEnchantSources
                .OrderBy(source => source.Serialized, StringComparer.Ordinal)
                .Select(source => source.Serialized)),
            string.Join(",", state.CardRewardUpgradeUses
                .OrderBy(pair => pair.Key.Serialized, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key.Serialized}={pair.Value}")));

    private static bool TryBuildRouteImpactPlan(
        RuntimeProfileId profileId,
        OpeningRewardContinuation continuation,
        out RouteRewardImpactPlan plan,
        out string issueCode)
    {
        issueCode = string.Empty;
        bool forcePotion = false;
        var additionalCardRewards = new List<bool>();
        var upgradeSources = new Dictionary<EffectCardType, List<RewardImpactSourceKey>>();
        var fixedGoldImpacts = new List<FixedGoldImpact>();
        var everyOtherPowerImpacts = new List<EveryOtherPowerCardImpact>();
        var firstRewardGlamSources = new List<RewardImpactSourceKey>();
        var limitedUpgradeImpacts = new List<LimitedCardRewardUpgradeImpact>();
        RewardImpactSourceKey[] sources = continuation.ActiveRewardImpactSources
            .Where(source => source.IsValid)
            .Distinct()
            .OrderBy(source => source.Serialized, StringComparer.Ordinal)
            .ToArray();

        foreach (RewardImpactSourceKey source in sources)
        {
            if (!OpeningCombatRewardImpactAdapterRegistry.TryResolve(
                    profileId,
                    source,
                    out OpeningCombatRewardImpactAdapterDescriptor descriptor))
            {
                plan = default!;
                issueCode = $"OpeningRewardImpactAdapterMissing:{source.Serialized}";
                return false;
            }

            foreach (OpeningCombatRewardImpactOperation operation in descriptor.Operations)
            {
                switch (operation.Kind)
                {
                    case OpeningCombatRewardImpactOperationKind.ForcePotionReward:
                        forcePotion = true;
                        break;
                    case OpeningCombatRewardImpactOperationKind.AddCardReward:
                        for (int index = 0; index < Math.Max(0, operation.Amount); index++)
                        {
                            additionalCardRewards.Add(operation.AddedCardRewardIsFromCombat);
                        }
                        break;
                    case OpeningCombatRewardImpactOperationKind.ForceUpgradeCardType:
                        if (operation.CardType is not EffectCardType cardType)
                        {
                            plan = default!;
                            issueCode = $"OpeningRewardImpactAdapterInvalidCardType:{source.Serialized}";
                            return false;
                        }
                        if (!upgradeSources.TryGetValue(cardType, out List<RewardImpactSourceKey>? typeSources))
                        {
                            typeSources = new List<RewardImpactSourceKey>();
                            upgradeSources[cardType] = typeSources;
                        }
                        typeSources.Add(source);
                        break;
                    case OpeningCombatRewardImpactOperationKind.AddFixedGoldReward:
                        fixedGoldImpacts.Add(new FixedGoldImpact(source, Math.Max(0, operation.Amount)));
                        break;
                    case OpeningCombatRewardImpactOperationKind.AddPowerCardEveryOtherCombat:
                        if (operation.CardType is not EffectCardType.Power)
                        {
                            plan = default!;
                            issueCode = $"OpeningRewardImpactAdapterInvalidPowerCardType:{source.Serialized}";
                            return false;
                        }
                        everyOtherPowerImpacts.Add(new EveryOtherPowerCardImpact(
                            source,
                            Math.Max(0, operation.Amount),
                            operation.RequiresIsFromCombat));
                        break;
                    case OpeningCombatRewardImpactOperationKind.EnchantFirstCardRewardWithGlam:
                        if (!continuation.SilkenTressConsumedDuringOpening)
                            firstRewardGlamSources.Add(source);
                        break;
                    case OpeningCombatRewardImpactOperationKind.UpgradeNextCardRewards:
                        limitedUpgradeImpacts.Add(new LimitedCardRewardUpgradeImpact(
                            source,
                            Math.Max(0, operation.Amount)));
                        break;
                    default:
                        plan = default!;
                        issueCode = $"OpeningRewardImpactOperationUnsupported:{source.Serialized}:{operation.Kind}";
                        return false;
                }
            }
        }

        IReadOnlyDictionary<EffectCardType, IReadOnlyList<RewardImpactSourceKey>> immutableUpgradeSources =
            upgradeSources.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<RewardImpactSourceKey>)pair.Value
                    .Distinct()
                    .OrderBy(source => source.Serialized, StringComparer.Ordinal)
                    .ToArray());
        string fingerprint = string.IsNullOrWhiteSpace(continuation.RewardImpactFingerprint)
            ? OpeningCombatRewardImpactAdapterRegistry.BuildImpactFingerprint(profileId, sources)
            : continuation.RewardImpactFingerprint;
        plan = new RouteRewardImpactPlan(
            sources,
            forcePotion,
            additionalCardRewards.AsReadOnly(),
            immutableUpgradeSources,
            fixedGoldImpacts.AsReadOnly(),
            everyOtherPowerImpacts.AsReadOnly(),
            firstRewardGlamSources.Distinct().OrderBy(source => source.Serialized, StringComparer.Ordinal).ToArray(),
            limitedUpgradeImpacts.AsReadOnly(),
            fingerprint);
        return true;
    }

    private static IReadOnlyList<NormalCombatRewardConditionalAssumption> BuildConditionalAssumptions(
        RouteRewardImpactPlan impactPlan)
    {
        if (!impactPlan.HasAnyImpact)
        {
            return DefaultConditionalAssumptions;
        }

        var assumptions = DefaultConditionalAssumptions
            .Where(item => item != NormalCombatRewardConditionalAssumption.RewardHooksNoOp)
            .Where(item => !impactPlan.ForcePotionReward ||
                           item != NormalCombatRewardConditionalAssumption.ForcePotionRewardFalse)
            .Append(NormalCombatRewardConditionalAssumption.KnownOpeningRelicRewardAdaptersApplied)
            .Distinct()
            .ToArray();
        return Array.AsReadOnly(assumptions);
    }

    private static NormalCombatRewardRoutePredictionResult UnknownRoute(
        string groupId,
        OpeningRewardContinuation continuation,
        IReadOnlyList<OpeningRewardRouteDescriptor> equivalentRoutes,
        string issueCode) => new(
        groupId,
        continuation,
        equivalentRoutes,
        SeedDomainEvaluationStatus.Unknown,
        Array.Empty<NormalCombatRewardBattleResult>(),
        PredictionPrecision.Unknown,
        continuation.Authority,
        continuation.Completeness,
        issueCode,
        continuation.ContinuationFingerprint,
        continuation.Route.EvidenceCode);

    private static ContinuationSelection SelectContinuations(
        IReadOnlyList<OpeningRewardContinuation> continuations,
        NormalCombatRewardProjectionRequest request)
    {
        OpeningRewardContinuation[] real = continuations.ToArray();
        if (request.Scope == NormalCombatRewardProjectionScope.RichAnalysis)
        {
            return new ContinuationSelection(real, real.Length, 0, 0);
        }

        // SearchExact has no "all legal routes" product mode in P10-RP1. A
        // malformed request must fail closed rather than silently restoring the
        // historical AnyLegalSameRoute semantics.
        if (request.RouteSelectionMode == NormalCombatRewardRouteSelectionMode.AllRealRoutes)
            return new ContinuationSelection(Array.Empty<OpeningRewardContinuation>(), real.Length, 0, real.Length);

        if (request.RouteSelectionMode == NormalCombatRewardRouteSelectionMode.PinnedRealRoute)
        {
            if (request.PinnedRootRelicKey is not { } pinned || !pinned.IsValid)
                return new ContinuationSelection(Array.Empty<OpeningRewardContinuation>(), real.Length, 0, 0);

            IReadOnlyList<ModelKey> requiredOrder = request.RequiredAcquisitionOrder ?? Array.Empty<ModelKey>();
            OpeningRewardContinuation[] selected = real
                .Where(route => route.Route.RootRelicKey == pinned)
                .Where(route => requiredOrder.Count == 0 || route.Route.AcquisitionOrder.SequenceEqual(requiredOrder))
                .ToArray();
            return new ContinuationSelection(selected, real.Length, 0, 0);
        }

        if (request.RouteSelectionMode == NormalCombatRewardRouteSelectionMode.ProvablyNeutralRealRoutes)
        {
            var selected = new List<OpeningRewardContinuation>();
            int perturbing = 0;
            int unknown = 0;
            foreach (OpeningRewardContinuation continuation in real)
            {
                switch (OpeningRewardNeutralityClassifier.Classify(continuation))
                {
                    case OpeningRewardNeutrality.ProvablyNeutral:
                        selected.Add(continuation);
                        break;
                    case OpeningRewardNeutrality.Perturbing:
                        perturbing++;
                        break;
                    default:
                        unknown++;
                        break;
                }
            }
            return new ContinuationSelection(selected.ToArray(), real.Length, perturbing, unknown);
        }

        return new ContinuationSelection(Array.Empty<OpeningRewardContinuation>(), real.Length, 0, real.Length);
    }

    private static OpeningRewardContinuation[] BuildChoiceContinuations(
        IReadOnlyList<NeowChoiceResult> choices)
    {
        var output = new List<OpeningRewardContinuation>();
        foreach (NeowChoiceResult choice in choices.OrderBy(choice => choice.SlotIndex))
        {
            IReadOnlyList<OpeningRewardContinuation> routes = choice.OpeningRewardContinuations?.Routes ??
                OpeningRewardContinuationAnalysis.Unknown(
                    choice.RelicKey,
                    "OpeningRewardContinuationMissingForChoice",
                    choice.EffectEvidenceCode).Routes;
            foreach (OpeningRewardContinuation route in routes)
            {
                OpeningRewardRouteDescriptor descriptor = route.Route with
                {
                    RouteId = $"choice.{choice.SlotIndex}.{route.Route.RouteId}",
                    RouteOrder = choice.SlotIndex * 1000 + route.Route.RouteOrder,
                    RootRelicKey = choice.RelicKey
                };
                output.Add(route with { Route = descriptor });
            }
        }
        return output.ToArray();
    }

    private static bool TryResolveStartingAct(
        IRuntimeProfile profile,
        ulong rootHash,
        string seedIdentity,
        WorldAuthoritySnapshot? authority,
        out int act,
        out ModelKey actKey,
        out string issue)
    {
        act = 0;
        actKey = default;
        issue = string.Empty;
        if (authority is null ||
            authority.CapturedProfileId != profile.ProfileId ||
            !SourceAuthorityRules.SupportsExactIdentity(authority.SourceAuthority) ||
            authority.Completeness != SnapshotCompleteness.Complete)
        {
            issue = "MissingStartingActAuthority";
            return false;
        }

        if (RuntimeProfilePolicies.IsModernCore(profile.ProfileId))
        {
            if (authority.Beta109Generation is null)
            {
                issue = "MissingModernStartingActAuthority";
                return false;
            }
            Beta109WorldGenerationSnapshot projected;
            projected = Beta109WorldSnapshotProjector.ProjectForRootHash(
                authority.Beta109Generation,
                rootHash,
                seedIdentity);
            if (!projected.SelectedActsExact || projected.SelectedActs.Count == 0)
            {
                issue = "MissingSelectedActsForRewardProjection";
                return false;
            }
            ModelKey selectedActKey = projected.SelectedActs[0];
            actKey = selectedActKey;
            Beta109ActGenerationSnapshot? selected = projected.OrderedActCatalog
                .FirstOrDefault(item => item.ActKey == selectedActKey);
            if (selected is null)
            {
                issue = "SelectedActCatalogEntryMissingForRewardProjection";
                return false;
            }
            act = selected.Act;
            return true;
        }

        if (!authority.CatalogOrderExact || authority.ActGroups is not { Count: > 0 })
        {
            issue = "MissingStable107StartingActAuthority";
            return false;
        }
        WorldActGroupSnapshot? firstGroup = authority.ActGroups
            .OrderBy(group => group.Act)
            .FirstOrDefault(group => group.Acts.Count > 0);
        if (firstGroup is null)
        {
            issue = "MissingStable107StartingActAuthority";
            return false;
        }
        var actSelection = new Xoshiro256StarStar(profile.DeriveNamedStreamSeed(rootHash, "act_selection"));
        WorldActSnapshot legacyAct = firstGroup.Acts[actSelection.NextInt(firstGroup.Acts.Count)];
        if (!legacyAct.ActKey.IsValid)
        {
            issue = "MissingStable107StartingActAuthority";
            return false;
        }
        act = legacyAct.Act;
        actKey = legacyAct.ActKey;
        return true;
    }

    internal static string BuildCardPoolFingerprint(IReadOnlyList<NeowEffectCardSnapshot> cards) =>
        BuildFingerprint(cards.Select(card =>
            $"{card.PoolOrder}:{card.CardKey.Serialized}:{card.Rarity}:{card.CardType}:{card.CanUpgrade}:" +
            $"{card.CanBeGeneratedInCombat}:{card.IsUnlockedInCapturedPool}:{card.IsDiscovered}:" +
            $"{card.EligibleForPostCombatRewardByPoolMembership}:{card.EligibilityAuthority}:{card.CatalogProfileId}"));

    internal static string BuildPotionPoolFingerprint(IReadOnlyList<NeowEffectPotionSnapshot> potions) =>
        BuildFingerprint(potions.Select(potion =>
            $"{potion.PoolOrder}:{potion.PotionKey.Serialized}:{potion.Rarity}"));

    internal static string? ValidatePools(
        RuntimeProfileId profileId,
        IReadOnlyList<NeowEffectCardSnapshot> cards,
        IReadOnlyList<NeowEffectPotionSnapshot> potions)
    {
        if (cards.Count == 0) return "CharacterRewardPoolEmpty";
        if (RuntimeProfilePolicies.UsesBeta110SharedAlgorithms(profileId) &&
            cards.Any(card => card.CatalogProfileId != profileId))
            return profileId + "CharacterRewardPoolCatalogProfileMismatch";
        if (RuntimeProfilePolicies.UsesBeta110SharedAlgorithms(profileId) &&
            cards.Any(card => string.IsNullOrWhiteSpace(card.EligibilityAuthority)))
            return profileId + "CharacterRewardEligibilityAuthorityMissing";
        if (potions.Count == 0) return "PotionPoolEmpty";
        if (cards.Select(card => card.CardKey).Distinct().Count() != cards.Count)
            return "CharacterRewardPoolContainsDuplicateModelKeys";
        if (potions.Select(potion => potion.PotionKey).Distinct().Count() != potions.Count)
            return "PotionPoolContainsDuplicateModelKeys";
        if (cards.Select(card => card.PoolOrder).Distinct().Count() != cards.Count)
            return "CharacterRewardPoolContainsDuplicatePoolOrder";
        if (potions.Select(potion => potion.PoolOrder).Distinct().Count() != potions.Count)
            return "PotionPoolContainsDuplicatePoolOrder";
        return null;
    }

    private static CardRarityRollOutcome RollCardRarity(
        float value,
        int ascension,
        ref float currentOffset)
    {
        float offsetBefore = currentOffset;
        float baseRare = ascension >= 7 ? 0.0149f : 0.03f;
        float rareThreshold = baseRare + offsetBefore;
        float uncommonThreshold = rareThreshold + UncommonBase;
        EffectCardRarity rarity;
        if (value < rareThreshold)
        {
            currentOffset = InitialCardRarityOffset;
            rarity = EffectCardRarity.Rare;
        }
        else
        {
            float growth = ascension >= 7 ? 0.005f : 0.01f;
            currentOffset = Math.Min(CardRarityOffsetCap, offsetBefore + growth);
            rarity = value < uncommonThreshold
                ? EffectCardRarity.Uncommon
                : EffectCardRarity.Common;
        }

        return new CardRarityRollOutcome(
            rarity,
            offsetBefore,
            rareThreshold,
            uncommonThreshold,
            currentOffset);
    }

    private static EffectCardRarity? NextAvailableRarity(
        EffectCardRarity rolled,
        IReadOnlyList<NeowEffectCardSnapshot> cards,
        IReadOnlySet<ModelKey> selected)
    {
        EffectCardRarity current = rolled;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (cards.Any(card => card.Rarity == current && !selected.Contains(card.CardKey))) return current;
            current = current switch
            {
                EffectCardRarity.Common => EffectCardRarity.Uncommon,
                EffectCardRarity.Uncommon => EffectCardRarity.Rare,
                _ => EffectCardRarity.Common
            };
        }
        return null;
    }

    private static float CalculateBaseUpgradeOdds(
        NeowEffectCardSnapshot card,
        int act,
        int ascension)
    {
        if (!card.CanUpgrade || card.Rarity == EffectCardRarity.Rare) return 0f;
        int currentActIndex = Math.Max(0, act - 1);
        float scaling = ascension >= 7 ? 0.125f : 0.25f;
        return currentActIndex * scaling;
    }

    private static EffectPotionRarity RollPotionRarity(float value) => value switch
    {
        <= 0.1f => EffectPotionRarity.Rare,
        <= 0.35f => EffectPotionRarity.Uncommon,
        _ => EffectPotionRarity.Common
    };

    private static float NextFloat(
        Xoshiro256StarStar rng,
        List<NormalCombatRewardRngTraceEntry> trace,
        int battleOrdinal,
        string stage)
    {
        float result = rng.NextFloat();
        trace.Add(new NormalCombatRewardRngTraceEntry(
            trace.Count,
            battleOrdinal,
            stage,
            "NextFloat",
            rng.CallCount,
            NumericResult: result));
        return result;
    }

    private static int NextInt(
        Xoshiro256StarStar rng,
        int bound,
        List<NormalCombatRewardRngTraceEntry> trace,
        int battleOrdinal,
        string stage)
    {
        int result = rng.NextInt(bound);
        trace.Add(new NormalCombatRewardRngTraceEntry(
            trace.Count,
            battleOrdinal,
            stage,
            "NextInt",
            rng.CallCount,
            IntResult: result));
        return result;
    }

    private static int NextIntRange(
        Xoshiro256StarStar rng,
        int minInclusive,
        int maxExclusive,
        List<NormalCombatRewardRngTraceEntry> trace,
        int battleOrdinal,
        string stage)
    {
        int result = minInclusive + rng.NextInt(maxExclusive - minInclusive);
        trace.Add(new NormalCombatRewardRngTraceEntry(
            trace.Count,
            battleOrdinal,
            stage,
            $"NextInt({minInclusive},{maxExclusive})",
            rng.CallCount,
            IntResult: result));
        return result;
    }

    private static T NextItem<T>(
        Xoshiro256StarStar rng,
        IReadOnlyList<T> source,
        List<NormalCombatRewardRngTraceEntry> trace,
        int battleOrdinal,
        string stage,
        Func<T, ModelKey> keySelector)
    {
        int index = NextInt(rng, source.Count, trace, battleOrdinal, stage);
        T selected = source[index];
        NormalCombatRewardRngTraceEntry previous = trace[^1];
        trace[^1] = previous with { SelectedKey = keySelector(selected) };
        return selected;
    }

    private static string ConditionalReason(RuntimeProfileId profileId, string suffix) => profileId switch
    {
        RuntimeProfileId.Beta110 => "Beta110DefaultMonsterRewardPipelinePendingValidation:" + suffix,
        RuntimeProfileId.Beta111 => "Beta111DefaultMonsterRewardPipelineSemanticCompatible:" + suffix,
        RuntimeProfileId.Beta109 => "Beta109HistoricalDonorDefaultMonsterRewardPipelineConditional:" + suffix,
        _ => "Stable107DefaultMonsterRewardPipelineConditional:" + suffix
    };

    private static EvidenceCode Evidence(RuntimeProfileId profileId, string suffix) => profileId switch
    {
        RuntimeProfileId.Beta110 => $"beta110.normal-combat-reward.{suffix}.source-audited-pending-runtime-validation",
        RuntimeProfileId.Beta111 => $"beta111.normal-combat-reward.{suffix}.semantic-compatible-source-audited",
        RuntimeProfileId.Beta109 => $"beta109-historical-donor.normal-combat-reward.{suffix}.fixture-reference-only",
        _ => $"stable107.normal-combat-reward.{suffix}.donor-and-delta-audited-conditional"
    };

    private static string RewardAuditSha256(RuntimeProfileId profileId) => profileId switch
    {
        RuntimeProfileId.Beta110 => Beta110RewardAuditSha256,
        RuntimeProfileId.Beta111 => Beta111CompatibilityAuditSha256,
        RuntimeProfileId.Beta109 => Beta109RewardAuditSha256,
        _ => "stable107-donor-and-runtime-validation"
    };

    private static string BuildFingerprint(IEnumerable<string> values)
    {
        string joined = string.Join("\n", values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}
