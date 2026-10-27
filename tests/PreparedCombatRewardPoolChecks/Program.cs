using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Core.World;
using RolltheSpire2.Core.World.Snapshots;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

internal static class Program
{
    private static int _assertions, _exactCases, _matches, _rejects, _actualRewardRequests, _projectedHits, _fallbackCases, _coreCases, _evaluatedCoreCases;
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly GameVersionDetection Detection = new("0.111.0", "0.111.0", "beta", "PreparedRewardPoolOfflineChecks", true, "");
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new SearchWorkspacePersistence.ModelKeyJsonConverter());
        return options;
    }
    private static void Need(bool value, string message)
    {
        _assertions++;
        if (!value) throw new InvalidDataException(message);
    }
    private static void Equal<T>(T a, T b, string message) =>
        Need(JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json), message);
    private static ModelKey Key(string text) => new(text.Split(':')[0], text.Split(':')[1]);
    private static ExactSearchExecutionRequest Compile(RuntimeContextAuthoritySnapshot authority, SearchQuery query, bool enabled, bool diagnostics = false)
    {
        var context = SearchContextFactory.From(RuntimeProfileId.Beta111, authority.Character.CharacterKey, authority.Ascension,
            authority, Detection, AncientOptionConditionProfile.BroadDefault);
        var compiled = SearchCompiler.Compile(query, context);
        var options = new SearchRunOptions("9KT7EQEZ0000", 1L << 20, int.MaxValue, 1, IncludeDiagnostics: diagnostics)
        { Optimizations = SearchOptimizationSettings.AllEnabled with { CostOrderedFilters = enabled } };
        var result = ExactSearchExecutionRequestFactory.Compile(compiled, options);
        Need(result.Success && result.Plan is not null, "Offline compile: " + result.Issue);
        return result.Plan!;
    }
    private static void CompareDocuments(SeedPredictionDocument? a, SeedPredictionDocument? b)
    {
        Need((a is null) == (b is null), "Document presence differs.");
        if (a is null) return;
        Equal(a.Context with { RequestId = b!.Context.RequestId }, b.Context, "Document context differs beyond the per-call request ID.");
        Equal(a.Sections, b.Sections, "Complete document sections differ (including RNG traces, routes, fingerprints).");
        Equal(a.Warnings, b.Warnings, "Document warnings differ.");
        Equal(a.Diagnostics.Where(x => x.Code != PredictionDiagnosticCodes.RequestId).ToArray(),
            b.Diagnostics.Where(x => x.Code != PredictionDiagnosticCodes.RequestId).ToArray(), "Diagnostics differ beyond per-call request ID.");
        Equal(new { a.PredictorId, a.OriginalSeed, a.CanonicalSeed, a.OverallStatus, a.ProductRelevantProjectionPrecision,
            a.ProductRelevantProjectionStatus, a.FullEffectSemanticsCompleteness, a.Party },
            new { b.PredictorId, b.OriginalSeed, b.CanonicalSeed, b.OverallStatus, b.ProductRelevantProjectionPrecision,
            b.ProductRelevantProjectionStatus, b.FullEffectSemanticsCompleteness, b.Party }, "Document summary differs.");
    }
    private static ProductionExactSearchResult CompareExact(ExactSearchExecutionRequest prepared, string seed)
    {
        var baseline = prepared with { PreparedRewardPools = null };
        var input = TrustedRootHashInput.FromCanonicalSeed(Beta111Profile.Instance, seed);
        var old = ProductionExactSearchEvaluator.Evaluate(baseline, input);
        var current = ProductionExactSearchEvaluator.Evaluate(prepared, input);
        Equal(old.Evaluation, current.Evaluation, "Exact evaluation/evidence/witnesses differ: " + seed);
        Need(old.FailureCode == current.FailureCode && old.IsMatch == current.IsMatch, "Exact acceptance differs.");
        Equal(old.Authority, current.Authority, "Projected authority differs.");
        CompareDocuments(old.Document, current.Document);
        Need(SearchAuthorityProjector.TryProjectForRootHash(prepared, input.RootHash, seed,
            out var authority, out _, out _), "Root authority projection failed.");
        Need(prepared.PreparedRewardPools!.IsCompatible(prepared.ProfileId, authority.EffectAuthority!, authority.WorldAuthority),
            "Root-projected authority did not retain the owned pool references.");
        _projectedHits++;
        if (current.Request?.PreparedRewardPools is { } passed)
        {
            Need(ReferenceEquals(passed, prepared.PreparedRewardPools), "Reward request lost prepared metadata.");
            Need(passed.IsCompatible(current.Request.Authority.ProfileId, current.Request.Authority.EffectAuthority!, current.Request.Authority.WorldAuthority),
                "Actual reward request would use fallback instead of prepared pools.");
            _actualRewardRequests++;
        }
        _exactCases++;
        if (current.IsMatch) _matches++; else if (current.Evaluation.Disposition == SearchDisposition.NoMatch) _rejects++;
        return current;
    }
    private static NormalCombatRewardSequencePredictionResult Core(IRuntimeProfile profile, string seed,
        IReadOnlyList<NeowChoiceResult> choices, NeowEffectAuthoritySnapshot authority, WorldAuthoritySnapshot world,
        NormalCombatRewardProjectionRequest projection, PreparedCombatRewardPools? prepared) =>
        NormalCombatRewardSequencePredictor.PredictFromRootHash(profile, profile.ComputeRootSeed(seed), seed, 0, 10,
            choices, authority, world, projection, prepared);
    private static void CompareCore(IRuntimeProfile profile, string seed, IReadOnlyList<NeowChoiceResult> choices,
        NeowEffectAuthoritySnapshot authority, WorldAuthoritySnapshot world, NormalCombatRewardProjectionRequest projection,
        PreparedCombatRewardPools prepared, bool expectPrepared)
    {
        Need(prepared.IsCompatible(profile.ProfileId, authority, world) == expectPrepared, "Unexpected prepared/fallback selection.");
        var baseline = Core(profile, seed, choices, authority, world, projection, null);
        var current = Core(profile, seed, choices, authority, world, projection, prepared);
        Equal(baseline, current, "Full core reward result differs.");
        _coreCases++;
        if (current.Status == SeedDomainEvaluationStatus.Evaluated && current.Routes.Count > 0) _evaluatedCoreCases++;
        if (!expectPrepared) _fallbackCases++;
    }
    private static void MetadataAndFallbackChecks(RuntimeContextAuthoritySnapshot fixture, IReadOnlyList<NeowChoiceResult> choices)
    {
        foreach (IRuntimeProfile profile in new IRuntimeProfile[] { Beta110Profile.Instance, Beta111Profile.Instance })
        foreach (bool multiplayer in new[] { false, true })
        {
            var source = fixture.EffectAuthority! with
            {
                CapturedProfileId = profile.ProfileId,
                CharacterRewardPool = fixture.EffectAuthority!.CharacterRewardPool!.Select(c => c with { CatalogProfileId = profile.ProfileId }).Concat(
                    [fixture.EffectAuthority.CharacterRewardPool!.First(c => c.Rarity == EffectCardRarity.Common) with
                    { CardKey = Key("CARD:PREPARED_CHECK_MP"), PoolOrder = 100000, CatalogProfileId = profile.ProfileId, IsMultiplayerOnly = true }]).ToList(),
                PotionPool = fixture.EffectAuthority.PotionPool!.Concat(
                    [fixture.EffectAuthority.PotionPool!.First() with { PotionKey = Key("POTION:PREPARED_CHECK_MP"), PoolOrder = 100000, IsMultiplayerOnly = true }]).ToList()
            };
            var world = fixture.WorldAuthority! with { CapturedProfileId = profile.ProfileId,
                Beta109Generation = fixture.WorldAuthority!.Beta109Generation! with { Profile = profile.ProfileId, IsMultiplayer = multiplayer } };
            var pools = PreparedCombatRewardPools.TryCreate(profile.ProfileId, source, world);
            Need(pools is not null, "Valid pool preparation rejected.");
            Equal(source, pools!.OwnedAuthority, "Freeze changed authority values or fingerprints.");
            var cards = source.CharacterRewardPool!.Where(c => multiplayer || !c.IsMultiplayerOnly)
                .Where(c => c.EligibleForPostCombatRewardByPoolMembership && c.IsUnlockedInCapturedPool)
                .Where(c => c.Rarity is EffectCardRarity.Common or EffectCardRarity.Uncommon or EffectCardRarity.Rare).OrderBy(c => c.PoolOrder).ToArray();
            var potions = source.PotionPool!.Where(p => multiplayer || !p.IsMultiplayerOnly)
                .Where(p => p.Rarity is EffectPotionRarity.Common or EffectPotionRarity.Uncommon or EffectPotionRarity.Rare).OrderBy(p => p.PoolOrder).ToArray();
            Equal(cards, pools.Cards, "Prepared card order differs."); Equal(potions, pools.Potions, "Prepared potion order differs.");
            Need(cards.Any(c => c.IsMultiplayerOnly) == multiplayer && potions.Any(p => p.IsMultiplayerOnly) == multiplayer, "MP membership fixture ineffective.");
            Need(pools.CardPoolFingerprint == NormalCombatRewardSequencePredictor.BuildCardPoolFingerprint(cards) &&
                pools.PotionPoolFingerprint == NormalCombatRewardSequencePredictor.BuildPotionPoolFingerprint(potions), "Prepared pool fingerprints differ.");
            var projections = new[] { NormalCombatRewardProjectionRequest.RichAnalysis,
                new NormalCombatRewardProjectionRequest(NormalCombatRewardProjectionScope.SearchExact, NormalCombatRewardRouteSelectionMode.ProvablyNeutralRealRoutes),
                new NormalCombatRewardProjectionRequest(NormalCombatRewardProjectionScope.SearchExact, NormalCombatRewardRouteSelectionMode.PinnedRealRoute,
                    choices.First().RelicKey) };
            foreach (var projection in projections)
                CompareCore(profile, "9KT7EQEZ002M", choices, pools.OwnedAuthority, world, projection, pools, true);
            var replaced = pools.OwnedAuthority with { CharacterRewardPool = pools.OwnedAuthority.CharacterRewardPool!.ToArray() };
            CompareCore(profile, "9KT7EQEZ002M", choices, replaced, world, projections[0], pools, false);
            var changedMode = world with { Beta109Generation = world.Beta109Generation! with { IsMultiplayer = !multiplayer } };
            CompareCore(profile, "9KT7EQEZ002M", choices, pools.OwnedAuthority, changedMode, projections[0], pools, false);
            var duplicate = pools.OwnedAuthority with { CharacterRewardPool = pools.OwnedAuthority.CharacterRewardPool!.Concat([cards.First()]).ToArray() };
            Need(PreparedCombatRewardPools.TryCreate(profile.ProfileId, duplicate, world) is null, "Duplicate card key/order accepted.");
            CompareCore(profile, "9KT7EQEZ002M", choices, duplicate, world, projections[0], pools, false);
            var duplicatePotion = pools.OwnedAuthority with { PotionPool = pools.OwnedAuthority.PotionPool!.Concat([potions.First()]).ToArray() };
            Need(PreparedCombatRewardPools.TryCreate(profile.ProfileId, duplicatePotion, world) is null, "Duplicate potion key/order accepted.");
            CompareCore(profile, "9KT7EQEZ002M", choices, duplicatePotion, world, projections[0], pools, false);
            foreach (var bad in new[]
            {
                pools.OwnedAuthority with { CharacterRewardPool = pools.OwnedAuthority.CharacterRewardPool!.Append(cards.First() with { PoolOrder = 200000 }).ToArray() },
                pools.OwnedAuthority with { CharacterRewardPool = pools.OwnedAuthority.CharacterRewardPool!.Append(cards.First() with { CardKey = Key("CARD:DUPLICATE_ORDER") }).ToArray() },
                pools.OwnedAuthority with { PotionPool = pools.OwnedAuthority.PotionPool!.Append(potions.First() with { PoolOrder = 200000 }).ToArray() },
                pools.OwnedAuthority with { PotionPool = pools.OwnedAuthority.PotionPool!.Append(potions.First() with { PotionKey = Key("POTION:DUPLICATE_ORDER") }).ToArray() },
                pools.OwnedAuthority with { CharacterRewardPool = cards.Select((c, i) => i == 0 ? c with { EligibilityAuthority = "" } : c).ToArray() },
                pools.OwnedAuthority with { CharacterRewardPool = cards.Select((c, i) => i == 0 ? c with { CatalogProfileId = RuntimeProfileId.Unsupported } : c).ToArray() }
            })
            {
                Need(PreparedCombatRewardPools.TryCreate(profile.ProfileId, bad, world) is null, "Invalid duplicate/order/eligibility/catalog pool prepared.");
                CompareCore(profile, "9KT7EQEZ002M", choices, bad, world, projections[0], pools, false);
            }
            var invalidPool = source with { CharacterRewardPoolExact = false };
            Need(PreparedCombatRewardPools.TryCreate(profile.ProfileId, invalidPool, world) is null, "Unknown pool prepared.");
            CompareCore(profile, "9KT7EQEZ002M", choices, invalidPool, world, projections[0], pools, false);
            Need(PreparedCombatRewardPools.TryCreate(profile.ProfileId, source with { CharacterRewardPool = [] }, world) is null, "Empty card pool prepared.");
            string frozen = JsonSerializer.Serialize(pools.OwnedAuthority, Json);
            // Same source reference/count can still mutate: the plan must own a detached snapshot.
            ((List<NeowEffectCardSnapshot>)source.CharacterRewardPool!)[0] = cards.First() with { CardKey = Key("CARD:EXTERNAL_MUTATION") };
            ((List<NeowEffectPotionSnapshot>)source.PotionPool!)[0] = potions.First() with { PotionKey = Key("POTION:EXTERNAL_MUTATION") };
            Need(JsonSerializer.Serialize(pools.OwnedAuthority, Json) == frozen, "External source mutation changed owned pools.");
            bool blocked = false;
            try { ((IList<NeowEffectCardSnapshot>)pools.OwnedAuthority.CharacterRewardPool!)[0] = cards.First(); }
            catch (NotSupportedException) { blocked = true; }
            Need(blocked, "Owned pool wrapper permits replacement.");
        }
    }
    private static void Main(string[] args)
    {
        string sourcePath = args.Length > 0 ? args[0] : ".build-evidence/broad-original-comparison/scenarios/random-complex-v1/source-authority.json";
        var fixture = JsonSerializer.Deserialize<RuntimeContextAuthoritySnapshot>(File.ReadAllText(sourcePath), Json)!;
        ModelKey target = fixture.EffectAuthority!.CharacterRewardPool!.First(c => c.Rarity == EffectCardRarity.Common && c.IsUnlockedInCapturedPool).CardKey;
        var query = SearchQuery.Empty with { CombatCardRewards = new(3, CombatRewardSequenceOrderMode.Ordered, [target, null, null]) };
        var plan = Compile(fixture, query, true);
        Need(plan.PreparedRewardPools is not null, "Default enabled reward plan was not prepared.");
        Need(Compile(fixture, query, false).PreparedRewardPools is null, "CostOrderedFilters off still prepared pools.");
        Need(Compile(fixture, SearchQuery.Empty, true).PreparedRewardPools is null, "Unrelated query prepared reward pools.");
        Equal(fixture, plan.Authority, "Compile freeze changed authority contents/fingerprints.");
        Need(!ReferenceEquals(fixture.EffectAuthority.CharacterRewardPool, plan.Authority.EffectAuthority!.CharacterRewardPool), "Compile did not detach the card pool.");
        Equal(SearchCompiler.Compile(query, plan.CompiledSearch.Context).NormalizedQuery, plan.CompiledSearch.NormalizedQuery, "Freeze changed normalized query.");
        Need(SearchCompiler.Compile(query, plan.CompiledSearch.Context).SemanticFingerprint == plan.CompiledSearch.SemanticFingerprint,
            "Freeze changed semantic fingerprint.");
        var mutableCards = fixture.EffectAuthority.CharacterRewardPool!.ToList();
        var mutablePotions = fixture.EffectAuthority.PotionPool!.ToList();
        var mutable = fixture.WithEffectAuthority(fixture.EffectAuthority with { CharacterRewardPool = mutableCards, PotionPool = mutablePotions });
        var detached = Compile(mutable, query, true);
        string detachedBefore = JsonSerializer.Serialize(detached.Authority, Json);
        mutableCards[0] = mutableCards[0] with { CardKey = Key("CARD:EXTERNAL_COMPILE_MUTATION") };
        mutablePotions[0] = mutablePotions[0] with { PotionKey = Key("POTION:EXTERNAL_COMPILE_MUTATION") };
        Need(JsonSerializer.Serialize(detached.Authority, Json) == detachedBefore, "Post-compile external mutation changed the execution plan.");
        IReadOnlyList<NeowChoiceResult>? sample = null;
        for (ulong n = 0; n < 256; n++)
        {
            string seed = VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, 672575402513859024UL + n * 127);
            var result = CompareExact(plan, seed);
            var choices = result.Document?.Sections.FirstOrDefault(s => s.Kind == PredictionSectionKind.NeowIdentity)?.NeowChoices;
            if (choices is { Count: > 0 }) sample ??= choices;
            if (n >= 31 && _matches > 0 && _rejects > 0) break;
        }
        Need(_matches > 0 && _rejects > 0 && _actualRewardRequests > 0 && sample is not null,
            "No positive/negative full Exact witness or no actual prepared reward request; cannot pass vacuously.");
        CompareExact(Compile(fixture, query, true, diagnostics: true), "9KT7EQEZ002M");
        MetadataAndFallbackChecks(fixture, sample!);
        Need(_evaluatedCoreCases > 0 && _fallbackCases >= 20, "Core evaluated/fallback coverage ineffective.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool threw = false;
        try { ProductionExactSearchEvaluator.Evaluate(plan, TrustedRootHashInput.FromCanonicalSeed(Beta111Profile.Instance, "9KT7EQEZ0000"), cancelled.Token); }
        catch (OperationCanceledException) { threw = true; }
        Need(threw, "Exact cancellation was bypassed.");
        Console.WriteLine("RT2_PREPARED_REWARD_POOLS " + JsonSerializer.Serialize(new
        {
            result = "PASS", assertions = _assertions, revision = PreparedCombatRewardPools.Revision,
            exactCases = _exactCases, matches = _matches, rejects = _rejects, projectedPreparedHits = _projectedHits,
            actualRewardRequestsWithPreparedHit = _actualRewardRequests, projectedFallbacks = 0,
            coreCases = _coreCases, evaluatedCoreCases = _evaluatedCoreCases, deliberateFallbackCases = _fallbackCases,
            supports = "Beta110/Beta111 pool metadata; single/multiplayer pool selection; production party intentionally falls back",
            scope = "Offline correctness only; no game/GPU/benchmark/installation", fixture = Path.GetFullPath(sourcePath)
        }, Json));
    }
}
