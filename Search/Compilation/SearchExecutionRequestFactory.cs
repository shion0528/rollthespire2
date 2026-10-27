using RolltheSpire2.Core.Authority;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Events;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.Compilation;

/// <summary>
/// Execution-side bridge after the semantic Planner boundary. Modern callers supply a
/// CompiledSearch plus run/session options. Historical callers that only own the legacy
/// NeowSearchFilter enter through CompileLegacy and are first adapted back into SearchQuery.
/// This class does not normalize player intent and does not build FastPlan.
/// </summary>
public static class SearchExecutionRequestFactory
{
    public static SearchExecutionCompileResult Compile(CompiledSearch compiled, SearchRunOptions runOptions)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(runOptions);

        SearchContext context = compiled.Context;
        RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(context.Detection);
        IRuntimeProfile profile = RuntimeProfileRegistry.Select(compatibility);
        if (profile.ProfileId == RuntimeProfileId.Unsupported || profile.ProfileId != context.ProfileId)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "ProfileDetectionMismatch");
        if (!compatibility.AllowsProductionSearch || compatibility.Blocks(CompatibilityDomainMask.RuntimeBinding))
        {
            return SearchExecutionCompileResult.Rejected(
                SearchDisposition.Unknown,
                compatibility.IsKnownIncompatible
                    ? "KnownIncompatibleCompatibilitySearchFailClosed:" + compatibility.EvidenceCode
                    : "UnsupportedCompatibilitySearchFailClosed:" + compatibility.EvidenceCode);
        }
        if (profile.ProfileId == RuntimeProfileId.Beta110 && !Beta110ValidationAuthority.RuntimeAccepted)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unknown, "Beta110PendingUserValidationSearchFailClosed");

        string? contextIssue = ValidateContext(compiled);
        if (!string.IsNullOrWhiteSpace(contextIssue))
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unknown, contextIssue);

        SearchFeasibilityResult feasibility = SearchFeasibilityAnalyzer.Analyze(compiled);
        if (feasibility.IsImpossible)
        {
            return SearchExecutionCompileResult.Rejected(
                SearchDisposition.NoMatch,
                "SearchImpossible:" + feasibility.Proof!.ReasonCode + ":" + feasibility.Proof.Diagnostic);
        }

        if (runOptions.ScanCount <= 0)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "ScanCountMustBePositive");
        if (runOptions.TargetMatchCount <= 0)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "TargetMatchCountMustBePositive");
        if (runOptions.WorkerCount is < 1 or > 64)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "WorkerCountOutsideSupportedRange");
        if (runOptions.Resources is { } resources)
        {
            if (resources != resources.Normalize())
                return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "ResourceBudgetOutsideSupportedRange");
            runOptions = runOptions with
            {
                WorkerCount = Math.Min(runOptions.WorkerCount, resources.ResolveCpuWorkers(Environment.ProcessorCount))
            };
        }
        if (!profile.TryCanonicalizeSeed(runOptions.StartSeed, out string canonicalStart, out string seedIssue))
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "InvalidStartSeed:" + seedIssue);

        long resolvedScanCount = runOptions.ScanCount;
        if (RuntimeProfilePolicies.UsesBeta110SharedAlgorithms(context.ProfileId))
        {
            if (!Beta110SeedCodec.TryParseOrdinal(canonicalStart, out ulong startOrdinal, out _, out string ordinalIssue))
                return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "InvalidStartSeedOrdinal:" + ordinalIssue);

            ulong remainingVisibleSeeds = Beta110SeedCodec.SpaceSize - startOrdinal;
            if (runOptions.ScanCount == long.MaxValue)
                resolvedScanCount = checked((long)remainingVisibleSeeds);
            else if ((ulong)runOptions.ScanCount > remainingVisibleSeeds)
                return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "SearchRangeExceedsBeta110VisibleSeedSpace");
        }

        LegacySearchCriteriaProjection projection = LegacySearchCriteriaAdapter.Project(compiled);
        if (projection.Fidelity is ProjectionFidelity.LossyButConservative or ProjectionFidelity.Unsupported)
        {
            return SearchExecutionCompileResult.Rejected(
                SearchDisposition.Unsupported,
                "LegacyExecutionProjectionFailClosed:" + projection.Fidelity + ":" + string.Join(",", projection.Diagnostics));
        }

        NeowSearchFilter filter = NormalizeFilter(projection.Filter);
        CompatibilityDomainMask requiredCompatibilityDomains = RequiredCompatibilityDomains(filter);
        if (compatibility.Blocks(requiredCompatibilityDomains))
        {
            return SearchExecutionCompileResult.Rejected(
                SearchDisposition.Unknown,
                $"CompatibilityDomainFailClosed:required={requiredCompatibilityDomains};blocked={compatibility.BlockedDomains};evidence={compatibility.EvidenceCode}");
        }

        string? filterIssue = ValidateFilter(filter);
        if (!string.IsNullOrWhiteSpace(filterIssue))
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "LegacyExecutionProjectionInvalid:" + filterIssue);
        if (filter.RequiresEffectColdPath && context.Authority.EffectAuthority is null)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unknown, "EffectAuthorityMissing");
        if (filter.RequiresWorldAuthority && context.Authority.WorldAuthority is null)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unknown, "WorldAuthorityMissing");
        if (filter.RequiresWorldAuthority && RuntimeProfilePolicies.IsModernCore(context.ProfileId) &&
            context.Authority.WorldAuthority?.Beta109Generation is null)
        {
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unknown, "ModernWorldGenerationSnapshotMissing");
        }

        CombatRewardRoutePolicyContract rewardPolicy = BuildCombatRewardRoutePolicy(compiled);
        string fingerprint = BuildExecutionFingerprint(compiled, compatibility, filter, rewardPolicy);
        var execution = new SearchExecutionRequest(
            compiled,
            runOptions,
            canonicalStart,
            resolvedScanCount,
            filter,
            rewardPolicy,
            fingerprint);
        return SearchExecutionCompileResult.Accepted(execution);
    }

    public static SearchExecutionCompileResult CompileLegacy(LegacySearchInput? request)
    {
        if (request is null)
            return SearchExecutionCompileResult.Rejected(SearchDisposition.Unsupported, "MissingSearchRequest");

        SearchContext context = SearchContextFactory.From(
            request.ProfileId,
            request.CharacterKey,
            request.Ascension,
            request.Authority,
            request.Detection,
            request.Filter.AncientOptionConditions);
        SearchQuery query = LegacySearchQueryAdapter.FromFilter(request.Filter);
        CompiledSearch compiled = SearchCompiler.Compile(query, context);
        return Compile(compiled, new SearchRunOptions(
            request.StartSeed,
            request.ScanCount,
            request.TargetMatchCount,
            request.WorkerCount,
            request.IncludeDiagnostics));
    }

    private static string? ValidateContext(CompiledSearch compiled)
    {
        SearchContext context = compiled.Context;
        RuntimeContextAuthoritySnapshot authority = context.Authority;
        if (authority.ProfileId != context.ProfileId ||
            !string.Equals(authority.GameVersion, context.Detection.NormalizedVersion, StringComparison.Ordinal))
            return "SnapshotProfileMismatch";
        if (!context.CharacterKey.IsValid || context.CharacterKey.Category != BaseGameModelKeys.Categories.Character)
            return "InvalidCharacterKey";
        if (authority.Character.CharacterKey != context.CharacterKey || authority.Ascension != context.Ascension)
            return "SnapshotRequestContextMismatch";
        if (string.IsNullOrWhiteSpace(authority.UnlockSnapshotFingerprint) ||
            string.IsNullOrWhiteSpace(authority.CatalogFingerprint))
            return "SnapshotFingerprintMissing";
        return null;
    }

    private static CombatRewardRoutePolicyContract BuildCombatRewardRoutePolicy(CompiledSearch compiled) =>
        compiled.ResolvedRouteSemantics.CombatReward.Kind switch
        {
            ResolvedCombatRewardRouteKind.NotApplicable => CombatRewardRoutePolicyContract.None,
            ResolvedCombatRewardRouteKind.PinnedOpeningRoute => new CombatRewardRoutePolicyContract(
                // Fast/Search projection is query-literal: real opening-route outcomes are
                // not replayed merely to discover latent Reward modifiers. Explicitly
                // authored modifiers are carried separately in ExplicitRewardContext.
                CombatRewardFastRoutePolicy.UnpinnedAssumeUnperturbed,
                // Production Exact remains real-route authoritative.
                CombatRewardExactRoutePolicy.PinnedRealRoute),
            ResolvedCombatRewardRouteKind.NeutralNonPerturbingContinuation => new CombatRewardRoutePolicyContract(
                CombatRewardFastRoutePolicy.UnpinnedAssumeUnperturbed,
                CombatRewardExactRoutePolicy.UnpinnedVerifyNeutralRealRoute),
            _ => CombatRewardRoutePolicyContract.None
        };

    private static CompatibilityDomainMask RequiredCompatibilityDomains(NeowSearchFilter filter)
    {
        CompatibilityDomainMask domains = CompatibilityDomainMask.None;
        if (filter.HasNeowConstraints) domains |= CompatibilityDomainMask.Neow;
        if (filter.RequiresRelicSequenceDomain) domains |= CompatibilityDomainMask.RelicSequence;
        if (filter.RequiresWorldDomain) domains |= CompatibilityDomainMask.WorldEventAncient;
        if (filter.RequiresNormalCombatRewardDomain) domains |= CompatibilityDomainMask.CombatReward;
        return domains;
    }

    private static string BuildExecutionFingerprint(
        CompiledSearch compiled,
        RuntimeVersionResolution compatibility,
        NeowSearchFilter filter,
        CombatRewardRoutePolicyContract rewardPolicy) => string.Join("|", new[]
        {
            compiled.Context.ProfileId.ToString(),
            compatibility.SupportKind.ToString(),
            compatibility.ReferenceVersion,
            compiled.Context.Authority.UnlockSnapshotFingerprint,
            compiled.Context.Authority.CatalogFingerprint,
            compiled.Context.Authority.EffectSnapshotFingerprint,
            compiled.Context.Authority.WorldSnapshotFingerprint,
            compiled.Context.CharacterKey.Serialized,
            compiled.Context.Ascension.ToString(System.Globalization.CultureInfo.InvariantCulture),
            filter.AncientOptionConditions.Fingerprint,
            rewardPolicy.FastPolicy.ToString(),
            rewardPolicy.ExactPolicy.ToString(),
            compiled.SemanticFingerprint,
            FilterFingerprint(filter)
        });

    private static NeowSearchFilter NormalizeFilter(NeowSearchFilter filter) => filter with
    {
        NeowRoute = filter.NeowRoute is { IsValid: true } route ? route : filter.NeowRoute,
        StructuredNeowEffects = filter.StructuredNeowEffects
            .Where(condition => condition.SourceRelicKey.IsValid)
            .Select(NormalizeStructuredNeowEffect)
            .Where(condition => !condition.IsEmpty)
            .Distinct()
            .ToArray(),
        NeowRelics = Normalize(filter.NeowRelics),
        BonesRelics = Normalize(filter.BonesRelics),
        RequiredBonesCombination = DistinctValid(filter.RequiredBonesCombination),
        RequiredBonesAcquisitionOrder = filter.RequiredBonesAcquisitionOrder.Where(key => key.IsValid).ToArray(),
        CapsuleContainedRelics = Normalize(filter.CapsuleContainedRelics),
        BannedFinalCurses = DistinctValid(filter.BannedFinalCurses),
        EffectOutputConditions = filter.EffectOutputConditions
            .Where(condition => condition.SourceRelicKey.IsValid)
            .Select(condition => condition with { OutputKeys = Normalize(condition.OutputKeys) })
            .Where(condition => !condition.IsEmpty)
            .Distinct()
            .ToArray(),
        BossFilters = NormalizeActFilters(filter.BossFilters),
        BossOrdinalFilters = NormalizeActOrdinalFilters(filter.BossOrdinalFilters),
        AncientBranchConditions = NormalizeAncientBranches(filter.AncientBranchConditions),
        AncientIdentityFilters = NormalizeAncientIdentityFilters(filter.AncientIdentityFilters),
        AncientOptionFilters = NormalizeActFilters(filter.AncientOptionFilters),
        AncientSeaGlassTargetFilters = NormalizeActFilters(filter.AncientSeaGlassTargetFilters),
        RelicSequenceConditions = filter.RelicSequenceConditions
            .Select(condition => condition with { Keys = Normalize(condition.Keys) })
            .Where(condition => !condition.IsEmpty)
            .Distinct()
            .ToArray(),
        RelicShopSequenceConditions = filter.RelicShopSequenceConditions
            .Where(condition => !condition.IsEmpty)
            .Select(condition => condition with { Slots = condition.Slots.Take(condition.Count).ToArray() })
            .ToArray(),
        EventSequenceConditions = filter.EventSequenceConditions
            .Select(condition => condition with { Keys = Normalize(condition.Keys) })
            .Where(condition => !condition.IsEmpty)
            .Distinct()
            .ToArray(),
        EventResultConditions = filter.EventResultConditions
            .Where(condition => condition.IsValid)
            .Select(MorphicGroveQuerySemantics.Normalize)
            .Distinct()
            .OrderBy(condition => condition.Kind)
            .ThenBy(condition => condition.TargetKey.Serialized, StringComparer.Ordinal)
            .ToArray(),
        MerchantColorlessConditions = filter.MerchantColorlessConditions
            .Where(condition => condition.IsValid)
            .GroupBy(condition => (condition.MerchantOrdinal, condition.Slot))
            .SelectMany(group => group
                .Distinct()
                .OrderBy(condition => condition.TargetCardKey.Serialized, StringComparer.Ordinal))
            .OrderBy(condition => condition.MerchantOrdinal)
            .ThenBy(condition => condition.Slot)
            .ToArray(),
        MerchantColorlessSequenceConditions = filter.MerchantColorlessSequenceConditions
            .Where(condition => !condition.IsEmpty)
            .Select(condition => condition with { Slots = condition.Slots.Take(condition.Count).ToArray() })
            .ToArray(),
        NormalCombatRewardConditions = filter.NormalCombatRewardConditions
            .Select(condition => condition with
            {
                Cards = Normalize(condition.Cards),
                Potions = Normalize(condition.Potions)
            })
            .Where(condition => !condition.IsEmpty)
            .Distinct()
            .ToArray(),
        CombatCardRewardSequence = NormalizeCombatCardSequence(filter.CombatCardRewardSequence),
        CombatPotionRewardSequence = NormalizeCombatPotionSequence(filter.CombatPotionRewardSequence)
    };

    private static string? ValidateFilter(NeowSearchFilter filter)
    {
        if (filter.NeowRoute is { IsValid: false })
        {
            return "NeowRouteRelicInvalid";
        }
        if (filter.StructuredNeowEffects.Count > 0 && filter.NeowRoute is null)
        {
            return "NeowStructuredEffectsRequireRouteRelic";
        }
        foreach (NeowStructuredEffectSearchCondition condition in filter.StructuredNeowEffects)
        {
            string? issue = ValidateStructuredNeowEffect(filter.NeowRoute, condition);
            if (!string.IsNullOrWhiteSpace(issue))
            {
                return issue;
            }
        }
        if (filter.RequiredBonesAcquisitionOrder.Count is not (0 or 2))
        {
            return "BonesAcquisitionOrderMustContainExactlyTwoRelics";
        }
        if (filter.RequiredBonesAcquisitionOrder.Any(key => key.Category != BaseGameModelKeys.Categories.Relic))
        {
            return "BonesAcquisitionOrderContainsNonRelic";
        }
        if (filter.RequiredBonesAcquisitionOrder.Count == 2 &&
            filter.RequiredBonesAcquisitionOrder[0] == filter.RequiredBonesAcquisitionOrder[1])
        {
            return "BonesAcquisitionOrderRequiresDistinctRelics";
        }
        if (!AllCategory(filter.NeowRelics, BaseGameModelKeys.Categories.Relic) ||
            !AllCategory(filter.BonesRelics, BaseGameModelKeys.Categories.Relic) ||
            !AllCategory(filter.CapsuleContainedRelics, BaseGameModelKeys.Categories.Relic) ||
            filter.RequiredBonesCombination.Any(key => key.Category != BaseGameModelKeys.Categories.Relic) ||
            filter.BannedFinalCurses.Any(key => key.Category != BaseGameModelKeys.Categories.Card) ||
            filter.RequiredFinalCurse is { } finalCurse && finalCurse.Category != BaseGameModelKeys.Categories.Card)
        {
            return "NeowFilterCategoryMismatch";
        }
        if (filter.EffectOutputConditions.Any(condition => condition.SourceRelicKey.Category != BaseGameModelKeys.Categories.Relic))
        {
            return "EffectOutputSourceMustBeRelic";
        }
        if (filter.BossFilters.Any(filterItem => !AllCategory(filterItem.Keys, BaseGameModelKeys.Categories.Encounter)) ||
            filter.BossOrdinalFilters.Any(filterItem => !AllCategory(filterItem.Keys, BaseGameModelKeys.Categories.Encounter)))
        {
            return "BossFilterCategoryMismatch";
        }
        if (filter.BossOrdinalFilters.Any(filterItem => filterItem.Ordinal is < 1 or > 2))
        {
            return "BossOrdinalOutsideSupportedRange";
        }
        foreach (AncientSearchBranchCondition branch in filter.AncientBranchConditions)
        {
            if (!branch.IsValid)
            {
                return "AncientBranchIdentityInvalid";
            }
            if (branch.OptionAny.Any(key => key.Category != BaseGameModelKeys.Categories.Relic))
            {
                return "AncientBranchOptionCategoryMismatch";
            }
            if (branch.SeaGlassTargetAny.Any(key => key.Category != BaseGameModelKeys.Categories.Character))
            {
                return "AncientBranchSeaGlassTargetCategoryMismatch";
            }
            if (branch.SeaGlassTargetAny.Count > 0 &&
                !branch.OptionAny.Contains(SeaGlassOptionKey, ModelKeyComparer.Instance))
            {
                return "AncientBranchSeaGlassTargetRequiresSeaGlassOption";
            }
        }
        if (filter.AncientIdentityFilters.Any(filterItem => !AllCategory(filterItem.Keys, BaseGameModelKeys.Categories.Event)))
        {
            return "AncientIdentityFilterCategoryMismatch";
        }
        if (filter.AncientOptionFilters.Any(filterItem => filterItem.Keys.Any.Concat(filterItem.Keys.All).Concat(filterItem.Keys.Ban).Any(key =>
                key.Category != BaseGameModelKeys.Categories.Relic &&
                key.Category != BaseGameModelKeys.Categories.Card)))
        {
            return "AncientOptionFilterCategoryMismatch";
        }
        if (filter.AncientSeaGlassTargetFilters.Any(filterItem => !AllCategory(filterItem.Keys, BaseGameModelKeys.Categories.Character)))
        {
            return "SeaGlassTargetFilterCategoryMismatch";
        }
        if (filter.RelicSequenceConditions.Any(condition =>
                condition.RangeValue is < 1 or > SeedPredictionInputLimits.MaximumRelicSequencePreviewCount ||
                !AllCategory(condition.Keys, BaseGameModelKeys.Categories.Relic)))
        {
            return "RelicSequenceConditionInvalid";
        }
        if (filter.EventSequenceConditions.Any(condition =>
                condition.Act is < 1 or > 3 ||
                condition.RangeValue is < 1 or > 256 ||
                !AllCategory(condition.Keys, BaseGameModelKeys.Categories.Event)))
        {
            return "EventSequenceConditionInvalid";
        }
        if (filter.EventResultConditions.Any(condition => !condition.IsValid))
        {
            return "EventResultConditionInvalid";
        }
        foreach (IGrouping<(int MerchantOrdinal, MerchantColorlessSlot Slot), MerchantColorlessSlotCondition> group in
                 filter.MerchantColorlessConditions.GroupBy(condition => (condition.MerchantOrdinal, condition.Slot)))
        {
            if (group.Any(condition => !condition.IsValid))
            {
                return "MerchantColorlessConditionInvalid";
            }
            if (group.Select(condition => condition.TargetCardKey).Distinct(ModelKeyComparer.Instance).Skip(1).Any())
            {
                return $"MerchantColorlessSlotConflict:{group.Key.MerchantOrdinal}:{group.Key.Slot}";
            }
        }
        foreach (MerchantColorlessSequenceSearchCondition condition in filter.MerchantColorlessSequenceConditions)
        {
            if (condition.IsEmpty) continue;
            if (condition.Count is < 1 or > 5 || condition.Slots.Count < condition.Count ||
                condition.Slots.Take(condition.Count).Any(key => key is { } value &&
                    (!value.IsValid || value.Category != BaseGameModelKeys.Categories.Card)))
                return "MerchantColorlessSequenceConditionInvalid";
        }
        foreach (RelicShopSequenceSearchCondition condition in filter.RelicShopSequenceConditions)
        {
            if (condition.IsEmpty) continue;
            if (condition.Count is < 1 or > 7 || condition.Slots.Count < condition.Count ||
                condition.Slots.Take(condition.Count).Any(key => key is { } value &&
                    (!value.IsValid || value.Category != BaseGameModelKeys.Categories.Relic)))
                return "RelicShopSequenceConditionInvalid";
        }
        foreach (NormalCombatRewardSearchCondition condition in filter.NormalCombatRewardConditions)
        {
            if (condition.BattleOrdinal is < 0 or > 3)
            {
                return "RewardBattleOrdinalOutsideSupportedRange";
            }
            if (!AllCategory(condition.Cards, BaseGameModelKeys.Categories.Card) ||
                !AllCategory(condition.Potions, BaseGameModelKeys.Categories.Potion))
            {
                return "RewardFilterCategoryMismatch";
            }
            if (condition.MinimumGold is < 0 || condition.MaximumGold is < 0 ||
                condition.MinimumGold.HasValue && condition.MaximumGold.HasValue && condition.MinimumGold > condition.MaximumGold)
            {
                return "RewardGoldRangeInvalid";
            }
        }
        if (filter.CombatCardRewardSequence is { IsEmpty: false } cardSequence &&
            (cardSequence.Count is < 1 or > 6 || cardSequence.Slots.Count != cardSequence.Count ||
             cardSequence.Slots.Any(key => key.HasValue && (!key.Value.IsValid || key.Value.Category != BaseGameModelKeys.Categories.Card))))
        {
            return "CombatCardRewardSequenceInvalid";
        }
        if (filter.CombatPotionRewardSequence is { IsEmpty: false } potionSequence &&
            (potionSequence.Count is < 1 or > 6 || potionSequence.Slots.Count != potionSequence.Count ||
             potionSequence.Slots.Any(slot => !slot.IsValid)))
        {
            return "CombatPotionRewardSequenceInvalid";
        }

        return null;
    }

    private static NeowStructuredEffectSearchCondition NormalizeStructuredNeowEffect(
        NeowStructuredEffectSearchCondition condition)
    {
        ModelKey[] keys = condition.AllowDuplicateOutputs
            ? condition.OutputKeys.Where(key => key.IsValid).ToArray()
            : DistinctValid(condition.OutputKeys).ToArray();
        if (condition.Kind is NeowStructuredConditionKind.ExactUnorderedPair or
            NeowStructuredConditionKind.StructuredCardComposition ||
            condition.Kind == NeowStructuredConditionKind.IndependentOfferGroupTargets &&
            condition.KaleidoscopeGroupOrder != KaleidoscopeGroupOrderMode.ExactOrder)
        {
            keys = keys.OrderBy(key => key.Serialized, StringComparer.Ordinal).ToArray();
        }
        if (condition.SourceRelicKey == BaseGameModelKeys.Relics.Kaleidoscope &&
            condition.Kind == NeowStructuredConditionKind.IndependentOfferGroupTargets &&
            condition.KaleidoscopeGroupOrder == KaleidoscopeGroupOrderMode.ExactOrder)
        {
            ModelKey?[] slots = condition.KaleidoscopePositionalSlots.Count == 2
                ? condition.KaleidoscopePositionalSlots.ToArray()
                : keys.Take(2).Select(key => (ModelKey?)key)
                    .Concat(Enumerable.Repeat<ModelKey?>(null, Math.Max(0, 2 - keys.Length)))
                    .Take(2).ToArray();
            keys = slots.Where(key => key.HasValue).Select(key => key!.Value).ToArray();
            return condition with { OutputKeys = keys, KaleidoscopePositionalSlots = slots };
        }
        return condition with { OutputKeys = keys, KaleidoscopePositionalSlots = Array.Empty<ModelKey?>() };
    }

    private static string? ValidateStructuredNeowEffect(
        NeowRouteSearchCondition? route,
        NeowStructuredEffectSearchCondition condition)
    {
        if (condition.SourceRelicKey.Category != BaseGameModelKeys.Categories.Relic)
        {
            return "NeowStructuredEffectSourceMustBeRelic";
        }
        if (route is { } selected &&
            selected.RouteRelicKey != BaseGameModelKeys.Relics.NeowsBones &&
            condition.SourceRelicKey != selected.RouteRelicKey)
        {
            return "NeowStructuredEffectSourceOutsideSelectedRoute";
        }

        string expectedCategory = condition.OutputKind switch
        {
            NeowStructuredOutputKind.Relic => BaseGameModelKeys.Categories.Relic,
            NeowStructuredOutputKind.Card => BaseGameModelKeys.Categories.Card,
            NeowStructuredOutputKind.Potion => BaseGameModelKeys.Categories.Potion,
            NeowStructuredOutputKind.Curse => BaseGameModelKeys.Categories.Card,
            _ => string.Empty
        };
        if (condition.OutputKeys.Any(key => key.Category != expectedCategory))
        {
            return "NeowStructuredEffectOutputCategoryMismatch";
        }

        bool countIsValid = condition.Kind switch
        {
            NeowStructuredConditionKind.ExactSingle => condition.OutputKeys.Count == 1,
            // Multi-output editors are optional refinements. One selected target
            // means "the generated pair contains this target"; two targets retain
            // the previous full unordered-pair semantics.
            NeowStructuredConditionKind.ExactUnorderedPair => condition.OutputKeys.Count is 1 or 2,
            // ScrollBoxes has one Uncommon and two Common result slots. Search may
            // refine any non-empty subset while the evaluator still validates the
            // complete production bundle shape.
            NeowStructuredConditionKind.StructuredCardComposition => condition.OutputKeys.Count is >= 1 and <= 3,
            NeowStructuredConditionKind.IndependentOfferGroupTargets => condition.OutputKeys.Count is 1 or 2,
            NeowStructuredConditionKind.SpecialOffer => condition.OutputKeys.Count == 0,
            _ => false
        };
        if (!countIsValid)
        {
            return "NeowStructuredEffectOutputCountMismatch";
        }
        if (condition.SourceRelicKey == BaseGameModelKeys.Relics.Kaleidoscope &&
            condition.Kind == NeowStructuredConditionKind.IndependentOfferGroupTargets &&
            condition.KaleidoscopeGroupOrder == KaleidoscopeGroupOrderMode.ExactOrder &&
            (condition.KaleidoscopePositionalSlots.Count != 2 ||
             !condition.KaleidoscopePositionalSlots.Any(key => key.HasValue)))
        {
            return "KaleidoscopeExactOrderRequiresTwoPositionalSlots";
        }
        if (!condition.AllowDuplicateOutputs &&
            condition.OutputKeys.Count != condition.OutputKeys.Distinct(ModelKeyComparer.Instance).Count())
        {
            return "NeowStructuredEffectDuplicateOutputNotAllowed";
        }
        if (condition.AllowDuplicateOutputs && !ProductionAllowsDuplicateOutputs(condition))
        {
            return "NeowStructuredEffectDuplicateOutputNotSupportedByProduction";
        }
        if (condition.Kind == NeowStructuredConditionKind.SpecialOffer)
        {
            if (condition.SpecialOffer != NeowSpecialOfferKind.ScrollBoxesTripleClaw ||
                condition.SourceRelicKey != BaseGameModelKeys.Relics.ScrollBoxes ||
                condition.Scope != NeowStructuredEffectScope.SelectableOfferGroups ||
                condition.OutputKind != NeowStructuredOutputKind.Card)
            {
                return "UnsupportedNeowSpecialOffer";
            }
        }
        else if (condition.SpecialOffer != NeowSpecialOfferKind.None)
        {
            return "NeowSpecialOfferOnlyValidForSpecialCondition";
        }
        if (condition.Scope == NeowStructuredEffectScope.BonesOfferedRelics &&
            (condition.SourceRelicKey != BaseGameModelKeys.Relics.NeowsBones ||
             condition.Kind != NeowStructuredConditionKind.ExactUnorderedPair ||
             condition.OutputKind != NeowStructuredOutputKind.Relic))
        {
            return "BonesOfferedRelicsConditionInvalid";
        }
        if (condition.Scope == NeowStructuredEffectScope.FinalCurse &&
            (condition.SourceRelicKey != BaseGameModelKeys.Relics.NeowsBones ||
             condition.Kind != NeowStructuredConditionKind.ExactSingle ||
             condition.OutputKind != NeowStructuredOutputKind.Curse))
        {
            return "FinalCurseStructuredConditionInvalid";
        }
        if (condition.Kind == NeowStructuredConditionKind.StructuredCardComposition &&
            condition.OutputKind != NeowStructuredOutputKind.Card)
        {
            return "StructuredCardCompositionRequiresCards";
        }
        return null;
    }


    private static bool ProductionAllowsDuplicateOutputs(NeowStructuredEffectSearchCondition condition) =>
        (condition.SourceRelicKey == BaseGameModelKeys.Relics.LeafyPoultice &&
         condition.Kind == NeowStructuredConditionKind.ExactUnorderedPair &&
         condition.Scope == NeowStructuredEffectScope.TransformResults &&
         condition.OutputKind == NeowStructuredOutputKind.Card) ||
        (condition.SourceRelicKey == BaseGameModelKeys.Relics.Kaleidoscope &&
         condition.Kind == NeowStructuredConditionKind.IndependentOfferGroupTargets &&
         condition.Scope == NeowStructuredEffectScope.SelectableOfferGroups &&
         condition.OutputKind == NeowStructuredOutputKind.Card);

    private static readonly ModelKey SeaGlassOptionKey =
        new(BaseGameModelKeys.Categories.Relic, "SEA_GLASS");

    private static IReadOnlyList<AncientSearchBranchCondition> NormalizeAncientBranches(
        IEnumerable<AncientSearchBranchCondition> branches) =>
        branches
            .Where(branch => branch.Act is 2 or 3 && branch.AncientKey.IsValid)
            .Select(branch => branch with { AncientKey = NormalizeAncientIdentity(branch.AncientKey) })
            .GroupBy(branch => (branch.Act, branch.AncientKey))
            .Select(group => new AncientSearchBranchCondition(
                group.Key.Act,
                group.Key.AncientKey,
                DistinctValid(group.SelectMany(branch => branch.OptionAny)),
                DistinctValid(group.SelectMany(branch => branch.SeaGlassTargetAny))))
            .OrderBy(branch => branch.Act)
            .ThenBy(branch => branch.AncientKey.Serialized, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<ActModelKeySetFilter> NormalizeAncientIdentityFilters(
        IEnumerable<ActModelKeySetFilter> filters) =>
        NormalizeActFilters(filters.Select(filter => filter with
        {
            Keys = new ModelKeySetFilter(
                filter.Keys.Any.Select(NormalizeAncientIdentity).ToArray(),
                filter.Keys.All.Select(NormalizeAncientIdentity).ToArray(),
                filter.Keys.Ban.Select(NormalizeAncientIdentity).ToArray())
        }));

    private static ModelKey NormalizeAncientIdentity(ModelKey key) =>
        key.IsValid &&
        (key.Category == BaseGameModelKeys.Categories.Event ||
         key.Category == BaseGameModelKeys.Categories.Ancient)
            ? new ModelKey(BaseGameModelKeys.Categories.Event, key.Entry)
            : key;

    private static IReadOnlyList<ActModelKeySetFilter> NormalizeActFilters(
        IEnumerable<ActModelKeySetFilter> filters) =>
        filters
            .Where(filter => filter.Act is >= 1 and <= 3)
            .GroupBy(filter => filter.Act)
            .Select(group => new ActModelKeySetFilter(
                group.Key,
                new ModelKeySetFilter(
                    DistinctValid(group.SelectMany(filter => filter.Keys.Any)),
                    DistinctValid(group.SelectMany(filter => filter.Keys.All)),
                    DistinctValid(group.SelectMany(filter => filter.Keys.Ban)))))
            .Where(filter => !filter.IsEmpty)
            .OrderBy(filter => filter.Act)
            .ToArray();

    private static IReadOnlyList<ActOrdinalModelKeySetFilter> NormalizeActOrdinalFilters(
        IEnumerable<ActOrdinalModelKeySetFilter> filters) =>
        filters
            .Where(filter => filter.Act is >= 1 and <= 3 && filter.Ordinal is >= 1 and <= 2)
            .GroupBy(filter => (filter.Act, filter.Ordinal))
            .Select(group => new ActOrdinalModelKeySetFilter(
                group.Key.Act,
                group.Key.Ordinal,
                new ModelKeySetFilter(
                    DistinctValid(group.SelectMany(filter => filter.Keys.Any)),
                    DistinctValid(group.SelectMany(filter => filter.Keys.All)),
                    DistinctValid(group.SelectMany(filter => filter.Keys.Ban)))))
            .Where(filter => !filter.IsEmpty)
            .OrderBy(filter => filter.Act)
            .ThenBy(filter => filter.Ordinal)
            .ToArray();

    private static CombatCardRewardSequenceSearchCondition? NormalizeCombatCardSequence(
        CombatCardRewardSequenceSearchCondition? sequence)
    {
        if (sequence is null || sequence.IsEmpty) return null;
        ModelKey?[] slots = sequence.Slots.Take(Math.Max(0, sequence.Count)).ToArray();
        if (sequence.OrderMode == CombatRewardSequenceOrderMode.Unordered)
        {
            slots = slots.Where(key => key.HasValue)
                .OrderBy(key => key!.Value.Serialized, StringComparer.Ordinal)
                .Concat(Enumerable.Repeat<ModelKey?>(null, slots.Count(key => !key.HasValue)))
                .ToArray();
        }
        return sequence with { Slots = slots };
    }

    private static CombatPotionRewardSequenceSearchCondition? NormalizeCombatPotionSequence(
        CombatPotionRewardSequenceSearchCondition? sequence)
    {
        if (sequence is null || sequence.IsEmpty) return null;
        CombatPotionRewardSlotSearchCondition[] slots = sequence.Slots.Take(Math.Max(0, sequence.Count)).ToArray();
        if (sequence.OrderMode == CombatRewardSequenceOrderMode.Unordered)
        {
            slots = slots.Where(slot => !slot.IsNeutral)
                .OrderBy(slot => slot.Requirement)
                .ThenBy(slot => slot.PotionKey?.Serialized ?? string.Empty, StringComparer.Ordinal)
                .Concat(Enumerable.Repeat(
                    new CombatPotionRewardSlotSearchCondition(CombatPotionSlotRequirement.Neutral, null),
                    slots.Count(slot => slot.IsNeutral)))
                .ToArray();
        }
        return sequence with { Slots = slots };
    }

    private static ModelKeySetFilter Normalize(ModelKeySetFilter filter) => new(
        DistinctValid(filter.Any),
        DistinctValid(filter.All),
        DistinctValid(filter.Ban));

    private static IReadOnlyList<ModelKey> DistinctValid(IEnumerable<ModelKey> keys) =>
        keys.Where(key => key.IsValid).Distinct(ModelKeyComparer.Instance).ToArray();

    private static bool AllCategory(ModelKeySetFilter filter, string category) =>
        filter.Any.Concat(filter.All).Concat(filter.Ban).All(key => key.Category == category);


    private static string FilterFingerprint(NeowSearchFilter filter)
    {
        static string Keys(ModelKeySetFilter keys) => string.Join(",", new[]
        {
            "any=" + string.Join("+", keys.Any.Select(key => key.Serialized)),
            "all=" + string.Join("+", keys.All.Select(key => key.Serialized)),
            "ban=" + string.Join("+", keys.Ban.Select(key => key.Serialized))
        });

        return string.Join("|", new[]
        {
            "neowRoute=" + filter.NeowRoute?.RouteRelicKey.Serialized,
            "neowStructured=" + string.Join(";", filter.StructuredNeowEffects.Select(item =>
                string.Join("~", item.SourceRelicKey.Serialized, item.Kind, item.Scope, item.OutputKind,
                    item.SourceRelicKey == BaseGameModelKeys.Relics.Kaleidoscope &&
                    item.KaleidoscopeGroupOrder == KaleidoscopeGroupOrderMode.ExactOrder
                        ? string.Join(">", item.KaleidoscopePositionalSlots.Select(key => key?.Serialized ?? "*"))
                        : string.Join("+", item.OutputKeys.Select(key => key.Serialized)), item.SpecialOffer,
                    item.AllowDuplicateOutputs ? "duplicates" : "distinct", item.KaleidoscopeGroupOrder))),
            "neow=" + Keys(filter.NeowRelics),
            "requireBones=" + filter.RequireNeowsBones,
            "bones=" + Keys(filter.BonesRelics),
            "bonesCombination=" + string.Join("+", filter.RequiredBonesCombination.Select(key => key.Serialized)),
            "bonesOrder=" + string.Join(">", filter.RequiredBonesAcquisitionOrder.Select(key => key.Serialized)),
            "smallCapsule=" + filter.RequireSmallCapsule,
            "largeCapsule=" + filter.RequireLargeCapsule,
            "capsule=" + Keys(filter.CapsuleContainedRelics),
            "whetstone=" + filter.RequireWhetstone,
            "warPaint=" + filter.RequireWarPaint,
            "finalCurse=" + filter.RequiredFinalCurse?.Serialized,
            "banCurses=" + string.Join("+", filter.BannedFinalCurses.Select(key => key.Serialized)),
            "preset=" + filter.Preset,
            "effects=" + string.Join(";", filter.EffectOutputConditions.Select(item => item.SourceRelicKey.Serialized + "=" + Keys(item.OutputKeys))),
            "boss=" + string.Join(";", filter.BossFilters.Select(item => $"{item.Act}:{Keys(item.Keys)}")),
            "bossOrdinal=" + string.Join(";", filter.BossOrdinalFilters.Select(item => $"{item.Act}.{item.Ordinal}:{Keys(item.Keys)}")),
            "ancientBranches=" + string.Join(";", filter.AncientBranchConditions.Select(item =>
                $"{item.Act}:{item.AncientKey.Serialized}:options={string.Join("+", item.OptionAny.Select(key => key.Serialized))}:" +
                $"seaGlassTargets={string.Join("+", item.SeaGlassTargetAny.Select(key => key.Serialized))}")),
            "ancient=" + string.Join(";", filter.AncientIdentityFilters.Select(item => $"{item.Act}:{Keys(item.Keys)}")),
            "ancientOptions=" + string.Join(";", filter.AncientOptionFilters.Select(item => $"{item.Act}:{Keys(item.Keys)}")),
            "seaGlassTargets=" + string.Join(";", filter.AncientSeaGlassTargetFilters.Select(item => $"{item.Act}:{Keys(item.Keys)}")),
            "relicSequence=" + string.Join(";", filter.RelicSequenceConditions.Select(item => $"{item.Lane}:{item.RangeMode}:{item.RangeValue}:{Keys(item.Keys)}")),
            "relicShopSequence=" + string.Join(";", filter.RelicShopSequenceConditions.Select(item =>
                $"{item.Count}:{item.OrderMode}:{string.Join(">", item.Slots.Select(key => key?.Serialized ?? "*"))}")),
            "eventSequence=" + string.Join(";", filter.EventSequenceConditions.Select(item => $"{item.Act}:{item.Source}:{item.RangeMode}:{item.RangeValue}:{Keys(item.Keys)}")),
            "eventResult=" + string.Join(";", filter.EventResultConditions.Select(item => $"{item.Kind}:{item.TargetKey.Serialized}" +
                (item.TrialCase is { } trial ? ":case=" + trial : "") +
                (item.TinkerCardType is { } type ? ":type=" + type + ":rider=" + item.TinkerRider : "") +
                (item.MorphicGroveSecondCard is { } second ? ":second=" + second.Serialized : "") +
                (item.MorphicGroveScenario is null ? "" : ":scenario=" + item.MorphicGroveScenario.Fingerprint))),
            "merchantColorless=" + string.Join(";", filter.MerchantColorlessConditions.Select(item => $"{item.MerchantOrdinal}:{item.Slot}:{item.TargetCardKey.Serialized}")),
            "merchantColorlessSequence=" + string.Join(";", filter.MerchantColorlessSequenceConditions.Select(item =>
                $"{item.Count}:{item.OrderMode}:{item.Slot}:{string.Join(">", item.Slots.Select(key => key?.Serialized ?? "*"))}")),
            "rewards=" + string.Join(";", filter.NormalCombatRewardConditions.Select(item =>
                $"{item.BattleOrdinal}:{Keys(item.Cards)}:{item.PotionRequirement}:{Keys(item.Potions)}:{item.MinimumGold}:{item.MaximumGold}")),
            "combatCardSequence=" + (filter.CombatCardRewardSequence is { IsEmpty: false } cards
                ? $"{cards.Count}:{cards.OrderMode}:{string.Join(">", cards.Slots.Select(key => key?.Serialized ?? "*"))}" : string.Empty),
            "combatPotionSequence=" + (filter.CombatPotionRewardSequence is { IsEmpty: false } potions
                ? $"{potions.Count}:{potions.OrderMode}:{string.Join(">", potions.Slots.Select(slot => slot.Requirement + ":" + slot.PotionKey?.Serialized))}" : string.Empty)
        });
    }
}
