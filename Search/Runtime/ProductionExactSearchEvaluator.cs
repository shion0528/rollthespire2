using RolltheSpire2.Core.Authority;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Prediction.Profiles;
using RolltheSpire2.Core.Effects;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.Runtime;

internal sealed record ProductionExactSearchResult(
    SeedPredictionRequest? Request,
    SeedPredictionDocument? Document,
    RuntimeContextAuthoritySnapshot Authority,
    SearchQueryEvaluation Evaluation,
    string FailureCode)
{
    public bool IsMatch => Evaluation.Disposition == SearchDisposition.Match && Request is not null && Document is not null;
}

/// <summary>
/// Production validation stage: call the version-bound Predictor for the required
/// domains and validate its result against Query. Filter predicates cannot emit
/// a final Match; SearchQueryEvaluation carries the validated route evidence.
/// </summary>
internal static class ProductionExactSearchEvaluator
{
    internal const string NeowOpeningReuseRevision = Beta111NeowOpeningProof.Revision;

    internal static bool CanReuseNeowOpening(ExactSearchExecutionRequest plan) =>
        plan.RunOptions.Optimizations.CostOrderedFilters && plan.ProfileId == RuntimeProfileId.Beta111 &&
        plan.Authority.PlayersCount == 1 && plan.Authority.PlayerSlotIndex == 0 &&
        plan.CompiledSearch.Context.Party is null && plan.PartyOpeningChoices is null &&
        plan.Evaluation.RequiresNormalCombatRewardDomain;

    internal static ProductionExactSearchResult Evaluate(
        SearchExecutionRequest legacyPlan,
        TrustedRootHashInput input) => EvaluateLegacy(legacyPlan, input);

    /// <summary>Legacy diagnostics only. Production Family Execution calls the Exact overload.</summary>
    internal static ProductionExactSearchResult EvaluateLegacy(
        SearchExecutionRequest legacyPlan,
        TrustedRootHashInput input) => Evaluate(
            new ExactSearchExecutionRequest(
                legacyPlan.CompiledSearch,
                legacyPlan.RunOptions,
                legacyPlan.CanonicalStartSeed,
                legacyPlan.ResolvedScanCount,
                ExactSearchEvaluationProjection.FromLegacyFilter(legacyPlan.Filter),
                legacyPlan.CombatRewardRoutePolicy,
                legacyPlan.SnapshotFingerprint),
            input);

    public static ProductionExactSearchResult Evaluate(
        ExactSearchExecutionRequest plan,
        TrustedRootHashInput input) => Evaluate(plan, input, CancellationToken.None);

    internal static ProductionExactSearchResult Evaluate(
        ExactSearchExecutionRequest plan,
        TrustedRootHashInput input, CancellationToken cancellationToken) =>
        Evaluate(plan, input, cancellationToken, forceFullWorldProjection: false);

    // A full projection remains available for diagnostics and same-flag
    // regression/performance comparisons. It can only add work, never weaken
    // the query-demand admission proof or bypass an Exact predicate.
    internal static ProductionExactSearchResult Evaluate(
        ExactSearchExecutionRequest plan,
        TrustedRootHashInput input, CancellationToken cancellationToken, bool forceFullWorldProjection)
        => Evaluate(plan, input, cancellationToken, forceFullWorldProjection, forceRecomputeOpening: false);

    // Same-DLL diagnostic control only; ordinary production always uses false.
    internal static ProductionExactSearchResult Evaluate(
        ExactSearchExecutionRequest plan,
        TrustedRootHashInput input, CancellationToken cancellationToken, bool forceFullWorldProjection, bool forceRecomputeOpening)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.CompiledSearch.Context.Party is not null) return EvaluatePartyInitial(plan, input, cancellationToken);
        if ((plan.CompiledSearch.NormalizedQuery.TransformationAggregate is null) != (plan.Evaluation.TransformationAggregate is null))
            throw new InvalidOperationException("TransformationAggregate.MissingExactObligation");
        RuntimeContextAuthoritySnapshot authority;
        if (plan.Evaluation.RequiresEffectColdPath || plan.Evaluation.RequiresWorldAuthority)
        {
            if (!SearchAuthorityProjector.TryProjectForRootHash(
                    plan,
                    input.RootHash,
                    input.SeedIdentity,
                    out authority,
                    out SearchDisposition projectionDisposition,
                    out string projectionIssue))
            {
                SearchQueryEvaluation projectionFailure = projectionDisposition switch
                {
                    SearchDisposition.Unsupported => SearchQueryEvaluation.Unsupported(projectionIssue),
                    SearchDisposition.NotEvaluatedByPolicy => SearchQueryEvaluation.NotEvaluatedByPolicy(projectionIssue),
                    _ => SearchQueryEvaluation.Unknown(projectionIssue)
                };
                return new ProductionExactSearchResult(null, null, plan.Authority, projectionFailure, projectionIssue);
            }
        }
        else
        {
            authority = SearchAuthorityProjector.PreserveSeedIndependentEffectFoundation(plan.Authority);
        }

        // Canonical Event Result and Merchant Colorless are global/root-local facts.
        // Prove them directly before document/route evaluation so neither Event
        // occurrence nor OpeningRoute precision can reject an otherwise exact result.
        SearchQueryEvaluation canonicalEvaluation = Beta111CanonicalEventShopSearchEvaluator.Evaluate(
            plan,
            input.RootHash,
            authority);
        if (canonicalEvaluation.Disposition != SearchDisposition.Match)
        {
            return new ProductionExactSearchResult(
                null,
                null,
                authority,
                canonicalEvaluation,
                canonicalEvaluation.FailureCode);
        }

        var mapEvaluation = StandardMapExactEvaluator.Evaluate(plan, input.SeedIdentity);
        if (mapEvaluation.Disposition != SearchDisposition.Match)
            return new(null,null,authority,mapEvaluation,mapEvaluation.FailureCode);
        canonicalEvaluation = SearchQueryEvaluation.Match(canonicalEvaluation.Evidence.Concat(mapEvaluation.Evidence).ToArray());

        CharacterIdentity character = CharacterIdentity.FromKey(plan.CharacterKey);
        int relicSequencePreviewCount = plan.Evaluation.RelicSequenceConditions.Count == 0
            ? SeedPredictionInputLimits.DefaultRelicSequencePreviewCount
            : Math.Max(
                SeedPredictionInputLimits.DefaultRelicSequencePreviewCount,
                plan.Evaluation.RelicSequenceConditions.Max(condition => condition.RangeValue));

        if (!SeedPredictionRequest.TryCreateFromRootHash(
                input,
                character,
                plan.Ascension,
                authority.PlayersCount,
                authority.PlayerSlotIndex,
                authority,
                plan.Evaluation.AncientOptionConditions,
                plan.Evaluation.CheapPredictionDomains,
                relicSequencePreviewCount,
                plan.IncludeDiagnostics,
                out SeedPredictionRequest? cheapRequest,
                out SeedPredictionRequestError requestError))
        {
            string failure = "CheapPathRequestRejected:" + requestError;
            return new ProductionExactSearchResult(
                null,
                null,
                authority,
                SearchQueryEvaluation.Unknown(failure),
                failure);
        }

        // Canonical-only Event/Shop queries do not run the general Predictor at all.
        // Production truth is already proven by the root-local evaluator above; invoking
        // unrelated Neow/World analysis here would let non-observable state or invariant
        // failures reacquire rejection authority. A minimal document is retained only
        // because SearchCandidate currently carries the shared analysis payload contract.
        if ((plan.Evaluation.RequiresCanonicalRootLocalDomain || plan.CompiledSearch.NormalizedQuery.StandardMaps.Count > 0) &&
            plan.Evaluation.CheapPredictionDomains == SeedPredictionDomainSelection.None &&
            !plan.Evaluation.RequiresNormalCombatRewardDomain)
        {
            SeedPredictionDocument canonicalDocument = CreateCanonicalOnlyDocument(
                cheapRequest!,
                plan,
                input);
            SearchQueryEvaluation canonicalOnly = AttachCanonicalGlobalWitness(
                canonicalEvaluation,
                canonicalDocument.CanonicalSeed);
            return new ProductionExactSearchResult(
                cheapRequest,
                canonicalDocument,
                authority,
                canonicalOnly,
                canonicalOnly.FailureCode);
        }

        cheapRequest = cheapRequest!.WithComplexBonesDeckInteractions(
            plan.CompiledSearch.RequiresComplexBonesDeckInteractionEvaluation);
        cheapRequest.PartyOpeningChoices = plan.PartyOpeningChoices;
        cheapRequest.WorldProjection = forceFullWorldProjection
            ? Core.World.WorldPredictionProjection.All : ExactWorldProjectionPolicy.Resolve(plan);
        cancellationToken.ThrowIfCancellationRequested();
        Beta111NeowOpeningProof? opening = null;
        bool captureOpening = !forceRecomputeOpening && CanReuseNeowOpening(plan);
        SeedPredictionDocument cheapDocument = captureOpening
            ? RuntimeProfileRegistry.PredictFromRootHashCapturingOpening(plan.Detection, cheapRequest, input, out opening)
            : RuntimeProfileRegistry.PredictFromRootHash(plan.Detection, cheapRequest, input);
        ThrowIfPredictionFailed(cheapDocument);
        cancellationToken.ThrowIfCancellationRequested();
        SearchQueryEvaluation cheapEvaluation = ProductionQueryValidator.EvaluateCheap(plan, cheapDocument, authority);
        cheapEvaluation = MergeCanonicalGlobalEvidence(cheapEvaluation, canonicalEvaluation);
        if (cheapEvaluation.Disposition != SearchDisposition.Match || !plan.Evaluation.RequiresNormalCombatRewardDomain)
        {
            return new ProductionExactSearchResult(
                cheapRequest,
                cheapDocument,
                authority,
                cheapEvaluation,
                cheapEvaluation.FailureCode);
        }

        SeedPredictionDomainSelection rewardDomains =
            SeedPredictionDomainSelection.Neow |
            SeedPredictionDomainSelection.NormalCombatRewards;
        NormalCombatRewardProjectionRequest rewardProjectionRequest = BuildRewardProjectionRequest(plan) with
        { BattleCount = Math.Max(3, Math.Max(plan.Evaluation.CombatCardRewardSequence?.Count ?? 0, plan.Evaluation.CombatPotionRewardSequence?.Count ?? 0)) };
        if (!SeedPredictionRequest.TryCreateFromRootHash(
                input,
                character,
                plan.Ascension,
                authority.PlayersCount,
                authority.PlayerSlotIndex,
                authority,
                plan.Evaluation.AncientOptionConditions,
                rewardDomains,
                relicSequencePreviewCount,
                plan.IncludeDiagnostics,
                rewardProjectionRequest,
                out SeedPredictionRequest? rewardRequest,
                out requestError))
        {
            string failure = "RewardPathRequestRejected:" + requestError;
            return new ProductionExactSearchResult(
                cheapRequest,
                cheapDocument,
                authority,
                SearchQueryEvaluation.Unknown(failure),
                failure);
        }

        rewardRequest = rewardRequest!.WithComplexBonesDeckInteractions(
            plan.CompiledSearch.RequiresComplexBonesDeckInteractionEvaluation);
        rewardRequest.PartyOpeningChoices = plan.PartyOpeningChoices;
        rewardRequest.PreparedRewardPools = plan.PreparedRewardPools;
        cancellationToken.ThrowIfCancellationRequested();
        SeedPredictionDocument rewardDocument = opening is not null
            ? RuntimeProfileRegistry.PredictFromRootHashReusingOpening(plan.Detection, rewardRequest, input, opening)
            : RuntimeProfileRegistry.PredictFromRootHash(plan.Detection, rewardRequest, input);
        ThrowIfPredictionFailed(rewardDocument);
        cancellationToken.ThrowIfCancellationRequested();
        SearchQueryEvaluation evaluation = ProductionQueryValidator.EvaluateRewards(plan, rewardDocument, cheapEvaluation);
        return new ProductionExactSearchResult(
            rewardRequest,
            rewardDocument,
            authority,
            evaluation,
            evaluation.FailureCode);
    }

    // Error is not a semantic Unknown/NoMatch. Do not let a failed computation
    // mark its candidate complete and advance the session's committed prefix.
    internal static void ThrowIfPredictionFailed(SeedPredictionDocument document)
    {
        if (!document.Warnings.Any(w => w.Code == PredictionWarningCode.AnalysisFailed)) return;
        string evidence = string.Join(" | ", document.Diagnostics
            .Where(d => d.Code == PredictionDiagnosticCodes.Exception).Select(d => d.Value));
        throw new InvalidOperationException($"ProductionExact.AnalysisFailed:{document.PredictorId}:{document.CanonicalSeed}:{evidence}");
    }

    private static ProductionExactSearchResult EvaluatePartyInitial(ExactSearchExecutionRequest plan, TrustedRootHashInput input, CancellationToken cancellationToken)
    {
        if (plan.CompiledSearch.UsesPartyFamilyProjection)
            return EvaluatePartyFamilies(plan, input, cancellationToken);
        var party = plan.CompiledSearch.Context.Party!;
        var initial = PartySeedInformation.Project(input.CanonicalSeed, party);
        var observation = Semantics.PartyNeowQuery.Complete(initial, plan.CompiledSearch, cancellationToken);
        if (observation is null) return new(null, null, plan.Authority, SearchQueryEvaluation.NoMatch("Party.N.NoJointWitness"), "Party.N.NoJointWitness");
        if (observation.Root != input.RootHash) throw new InvalidOperationException("Party.WrongRoot");
        var query = plan.CompiledSearch.NormalizedQuery;
        if (!Semantics.PartyInitialQuery.MatchesWorld(query, observation) || !Semantics.PartyInitialQuery.MatchesOffers(query, observation))
            return new(null, null, plan.Authority, SearchQueryEvaluation.NoMatch("Party.InitialPredicate"), "Party.InitialPredicate");
        var request = SeedPredictionRequest.ForPartyInitialInformation(input, party, plan.IncludeDiagnostics);
        var document = CreateCanonicalOnlyDocument(request, plan, input, observation);
        var evidence = new List<SearchMatchEvidence>();
        if (Semantics.PartyInitialQuery.HasWorld(query)) evidence.Add(new("W.World.Shared.Initial", AuthorityFingerprint: party.Fingerprint));
        foreach (var p in query.Players.Where(p => !p.Offers.IsEmpty))
            evidence.Add(new($"N.Neow.P{p.Slot + 1}.InitialOffers", AuthorityFingerprint: party.Fingerprint,
                OrderedOptionKeys: observation.Players[p.Slot].Offers));
        foreach (var transaction in observation.Transactions)
            evidence.Add(new($"N.Neow.P{transaction.Slot + 1}.SelectedResult.{transaction.Intent}", AuthorityFingerprint: party.Fingerprint,
                OrderedOptionKeys: [transaction.Option]));
        return new(request, document, plan.Authority, SearchQueryEvaluation.Match(evidence), "");
    }

    private static ProductionExactSearchResult EvaluatePartyFamilies(ExactSearchExecutionRequest plan,
        TrustedRootHashInput input, CancellationToken token)
    {
        var party = plan.CompiledSearch.Context.Party!;
        var initial = PartySeedInformation.Project(input.CanonicalSeed, party) with { IncludesFamilyInformation = true };
        if (!Semantics.PartyInitialQuery.MatchesOffers(plan.CompiledSearch.NormalizedQuery, initial))
            return new(null, null, plan.Authority, SearchQueryEvaluation.NoMatch("Party.InitialOffers"), "Party.InitialOffers");
        var shared = Compilation.ExactSearchExecutionRequestFactory.ForPlayer(plan, plan.CompiledSearch.PlayerSearches[^1]);
        var sharedResult = Evaluate(shared, input, token);
        if (!sharedResult.IsMatch) return sharedResult;
        var streams = NeowEffectRngContext.CreateFromRootHash(Beta111Profile.Instance, input.RootHash, 0);
        var evidence = new List<SearchMatchEvidence>(sharedResult.Evaluation.Evidence) { new(Semantics.PartyInitialQuery.OpeningPremiseId) };
        var transactions = new List<PartyNeowResult>();
        if (!plan.CompiledSearch.NormalizedQuery.Players.Any(p => p.Conditions.OpeningRoute is not null || p.Conditions.HasCombatRewardConstraints))
        {
            foreach (var personal in plan.CompiledSearch.PlayerSearches.Take(party.Players.Count))
            {
                var result = EvaluateUnselected(personal);
                if (!result.IsMatch) return result;
                evidence.AddRange(result.Evaluation.Evidence.Select(e => e with { Code = $"P{personal.Context.Authority.PlayerSlotIndex + 1}." + e.Code }));
            }
            var envelope = SeedPredictionRequest.ForPartyInitialInformation(input, party, plan.IncludeDiagnostics);
            return new(envelope, CreateCanonicalOnlyDocument(envelope, plan, input, initial), plan.Authority,
                SearchQueryEvaluation.Match(evidence), "");
        }
        ProductionExactSearchResult? unresolved = null;
        var matched = Visit(0, streams.Niche, streams.CombatPotionGeneration, true);
        return matched ?? unresolved ?? new(null, null, plan.Authority,
            SearchQueryEvaluation.NoMatch("Party.NoJointOpeningWitness"), "Party.NoJointOpeningWitness");

        ProductionExactSearchResult EvaluateUnselected(Semantics.CompiledSearch compiled)
        {
            var child = Compilation.ExactSearchExecutionRequestFactory.ForPlayer(plan, compiled);
            if (!child.Evaluation.HasNeowConstraints) return Evaluate(child, input, token);
            var owner = party.Players[compiled.Context.Authority.PlayerSlotIndex];
            if (!SeedPredictionRequest.TryCreateFromRootHash(input, owner.Character, owner.Ascension,
                owner.PlayersCount, owner.PlayerSlotIndex, owner, compiled.Context.EvaluationAssumptions.AncientEligibilityAssumptions,
                SeedPredictionDomainSelection.Neow, 10, false, out var request, out var error))
                throw new InvalidOperationException("Party.UnselectedPlayerRequest:" + error);
            // Observation-only N starts at its initial shared cursor and uses the
            // same explicit Capsule obtain-effect premise as selected openings.
            var choices = PartyNeowProjection.ProjectChoices(request!, input.RootHash,
                streams.Niche, streams.CombatPotionGeneration, true, Semantics.PartyInitialQuery.CapsuleEffectPremise(compiled.Query));
            return Evaluate(child with { PartyOpeningChoices = choices }, input, token);
        }

        ProductionExactSearchResult? Visit(int slot, Core.Rng.Xoshiro256StarStar niche, Core.Rng.Xoshiro256StarStar potions, bool sharedRngKnown)
        {
            token.ThrowIfCancellationRequested();
            if (slot == party.Players.Count)
            {
                var envelope = SeedPredictionRequest.ForPartyInitialInformation(input, party, plan.IncludeDiagnostics);
                return new(envelope, CreateCanonicalOnlyDocument(envelope, plan, input, initial with { Transactions = transactions.ToArray() }), plan.Authority,
                    SearchQueryEvaluation.Match(evidence.ToArray()), "");
            }
            var compiled = plan.CompiledSearch.PlayerSearches[slot];
            if (compiled.Query.OpeningRoute is null && !compiled.Query.HasCombatRewardConstraints)
            {
                // Explicit product premise: an unselected predecessor consumes no shared RNG.
                var independent = EvaluateUnselected(compiled);
                if (!independent.IsMatch) return independent;
                int count = evidence.Count;
                evidence.AddRange(independent.Evaluation.Evidence.Select(e => e with { Code = $"P{slot + 1}." + e.Code }));
                var found = Visit(slot + 1, niche, potions, sharedRngKnown);
                evidence.RemoveRange(count, evidence.Count - count);
                return found;
            }

            var owner = party.Players[slot];
            if (!SeedPredictionRequest.TryCreateFromRootHash(input, owner.Character, owner.Ascension,
                owner.PlayersCount, slot, owner, compiled.Context.EvaluationAssumptions.AncientEligibilityAssumptions,
                SeedPredictionDomainSelection.All, 10, false, out var request, out var error))
                throw new InvalidOperationException("Party.PlayerRequest:" + error);
            var choices = PartyNeowProjection.ProjectChoices(request!, input.RootHash, niche, potions, sharedRngKnown,
                Semantics.PartyInitialQuery.CapsuleEffectPremise(compiled.Query));
            // Unselected N keeps the established neutral C policy. Do not pin an
            // arbitrary perturbing offer merely because that route can satisfy C.
            // Selected openings still share one concrete whole-table witness.
            var selected = compiled.Query.OpeningRoute?.RouteRelicKey;
            foreach (var choice in choices.Where(c => selected is null || c.RelicKey == selected).SelectMany(PartyNeowProjection.ConcreteRoutes))
            {
                var local = selected is not null
                    ? compiled.Query with { OpeningRoute = new(choice.RelicKey) } : compiled.Query;
                var pinned = local == compiled.Query ? compiled : Semantics.SearchCompiler.CompilePlayer(local, compiled.Context);
                var child = Compilation.ExactSearchExecutionRequestFactory.ForPlayer(plan, pinned) with { PartyOpeningChoices = [choice] };
                var result = Evaluate(child, input, token);
                if (!result.IsMatch)
                {
                    if (result.Evaluation.Disposition != SearchDisposition.NoMatch) unresolved ??= result;
                    continue;
                }
                var continuation = choice.OpeningRewardContinuations?.Routes.SingleOrDefault();
                bool neutralPredecessor = selected is null;
                bool nextSharedKnown = sharedRngKnown && (neutralPredecessor || continuation?.NicheState is not null && continuation.CombatPotionGenerationState is not null);
                int before = evidence.Count;
                evidence.AddRange(result.Evaluation.Evidence.Select(e => e with { Code = $"P{slot + 1}." + e.Code }));
                transactions.Add(new(slot, choice.RelicKey, PartyNeowIntent.ResultTarget, [], choice.EffectGroups, choice.EffectGroups)
                { OpeningRouteId = continuation?.Route.RouteId ?? "" });
                // Unknown shared state taints only a later consumer; independent family facts remain usable.
                var found = Visit(slot + 1, !neutralPredecessor && nextSharedKnown ? continuation!.NicheState!.Restore() : niche,
                    !neutralPredecessor && nextSharedKnown ? continuation!.CombatPotionGenerationState!.Restore() : potions, nextSharedKnown);
                transactions.RemoveAt(transactions.Count - 1);
                evidence.RemoveRange(before, evidence.Count - before);
                if (found is not null) return found;
            }
            return null;
        }
    }

    private static SeedPredictionDocument CreateCanonicalOnlyDocument(
        SeedPredictionRequest request,
        ExactSearchExecutionRequest plan,
        TrustedRootHashInput input, PartySeedInformation? party = null) => new()
    {
        Party = party,
        Context = SeedPredictionDocument.CreateContext(request, plan.Detection.DisplayVersion, plan.ProfileId),
        PredictorId = party is null ? "Beta111CanonicalEventShopSearchEvaluator" : "Beta111PartyInitialInformation",
        OriginalSeed = request.OriginalSeed,
        CanonicalSeed = input.CanonicalSeed,
        Sections = Array.Empty<PredictionSection>(),
        Warnings = Array.Empty<PredictionWarning>(),
        Diagnostics = Array.Empty<PredictionDiagnostic>(),
        OverallStatus = SeedPredictionOverallStatus.Completed,
        ProductRelevantProjectionPrecision = PredictionPrecision.Exact,
        ProductRelevantProjectionStatus = ProductRelevantProjectionStatus.Evaluated,
        FullEffectSemanticsCompleteness = FullEffectSemanticsCompleteness.NotEvaluated
    };

    private static NormalCombatRewardProjectionRequest BuildRewardProjectionRequest(ExactSearchExecutionRequest plan) =>
        plan.CombatRewardRoutePolicy.ExactPolicy switch
        {
            CombatRewardExactRoutePolicy.PinnedRealRoute when plan.Evaluation.NeowRoute is { IsValid: true } pinned =>
                new NormalCombatRewardProjectionRequest(
                    NormalCombatRewardProjectionScope.SearchExact,
                    NormalCombatRewardRouteSelectionMode.PinnedRealRoute,
                    pinned.RouteRelicKey,
                    plan.Evaluation.RequiredBonesAcquisitionOrder),
            CombatRewardExactRoutePolicy.UnpinnedVerifyNeutralRealRoute =>
                new NormalCombatRewardProjectionRequest(
                    NormalCombatRewardProjectionScope.SearchExact,
                    NormalCombatRewardRouteSelectionMode.ProvablyNeutralRealRoutes),
            // SearchExact + AllRealRoutes is deliberately rejected by the
            // predictor in P10-RP1, so a missing/unknown policy fails closed.
            _ => new NormalCombatRewardProjectionRequest(
                NormalCombatRewardProjectionScope.SearchExact,
                NormalCombatRewardRouteSelectionMode.AllRealRoutes)
        };

    private static SearchQueryEvaluation AttachCanonicalGlobalWitness(
        SearchQueryEvaluation canonicalEvaluation,
        string canonicalSeed)
    {
        if (canonicalEvaluation.Disposition != SearchDisposition.Match)
            return canonicalEvaluation;

        string[] conditionIds = canonicalEvaluation.Evidence
            .Select(item => item.ConditionId ?? item.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        EvidenceCode[] evidenceCodes = canonicalEvaluation.Evidence
            .Select(item => item.EvidenceCode)
            .Where(code => !string.IsNullOrWhiteSpace(code.Value))
            .Distinct()
            .ToArray();
        var witness = new SearchMatchWitness(
            canonicalSeed,
            string.Empty,
            Array.Empty<ModelKey>(),
            conditionIds,
            string.Empty,
            null,
            evidenceCodes);
        return SearchQueryEvaluation.Match(
            canonicalEvaluation.Evidence,
            Array.Empty<string>(),
            new[] { witness });
    }

    internal static SearchQueryEvaluation MergeCanonicalGlobalEvidence(
        SearchQueryEvaluation documentEvaluation,
        SearchQueryEvaluation canonicalEvaluation)
    {
        if (documentEvaluation.Disposition != SearchDisposition.Match ||
            canonicalEvaluation.Disposition != SearchDisposition.Match ||
            canonicalEvaluation.Evidence.Count == 0)
        {
            return documentEvaluation;
        }

        // An aggregate's Bones results are route facts, not global facts. Retain
        // every matching route, then intersect with the N witness carrying R's
        // proven bag facts before C validates its copied Rewards continuation.
        // Never combine two existential routes or report discarded T alternatives.
        var aggregateRoutes=canonicalEvaluation.Evidence.Where(e=>e.Code=="TransformationAggregateMatched" && e.AcquisitionOrder is {Count:2}).ToArray();
        if(aggregateRoutes.Length>0)
        {
            var compatible=documentEvaluation.Witnesses.Where(w=>aggregateRoutes.Any(e=>
                w.AcquisitionOrder.SequenceEqual(e.AcquisitionOrder!) &&
                e.RouteId is { } route && w.OpeningRouteId.EndsWith("."+route["transformation-aggregate:".Length..],StringComparison.Ordinal))).ToArray();
            if(compatible.Length==0) return SearchQueryEvaluation.NoMatch("TransformationAggregate.NoSharedOpeningRoute");
            documentEvaluation=SearchQueryEvaluation.Match(documentEvaluation.Evidence,
                compatible.Select(w=>w.OpeningRouteId).Distinct(StringComparer.Ordinal).ToArray(),compatible);
            canonicalEvaluation=SearchQueryEvaluation.Match(canonicalEvaluation.Evidence.Where(e=>
                e.Code!="TransformationAggregateMatched" || compatible.Any(w=>
                    w.AcquisitionOrder.SequenceEqual(e.AcquisitionOrder ?? Array.Empty<ModelKey>()) &&
                    e.RouteId is { } route && w.OpeningRouteId.EndsWith("."+route["transformation-aggregate:".Length..],StringComparison.Ordinal))).ToArray());
        }

        SearchMatchEvidence[] evidence = documentEvaluation.Evidence
            .Concat(canonicalEvaluation.Evidence)
            .Distinct()
            .ToArray();
        string[] conditionIds = canonicalEvaluation.Evidence
            .Select(item => item.ConditionId ?? item.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        EvidenceCode[] evidenceCodes = canonicalEvaluation.Evidence
            .Select(item => item.EvidenceCode)
            .Where(code => !string.IsNullOrWhiteSpace(code.Value))
            .Distinct()
            .ToArray();
        SearchMatchWitness[] witnesses = documentEvaluation.Witnesses
            .Select(witness => witness with
            {
                MatchedConditions = witness.MatchedConditions
                    .Concat(conditionIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                EvidenceCodes = witness.EvidenceCodes
                    .Concat(evidenceCodes)
                    .Distinct()
                    .ToArray()
            })
            .ToArray();

        return SearchQueryEvaluation.Match(evidence, documentEvaluation.MatchedRouteIds, witnesses);
    }
}
