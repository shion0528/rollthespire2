using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Prediction.Profiles;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Core.World;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly GameVersionDetection Detection = new("0.111.0", "0.111.0", "beta", "NeowOpeningReuseOfflineChecks", true, "");
    private static readonly IRuntimeProfile Profile = Beta111Profile.Instance;
    private static readonly Beta111PredictionProfile Owner = Beta111PredictionProfile.Instance;
    private static readonly Func<ulong, RuntimeContextAuthoritySnapshot, IRuntimeProfile, bool, ModernNeowIdentityResult> Identity =
        (Func<ulong, RuntimeContextAuthoritySnapshot, IRuntimeProfile, bool, ModernNeowIdentityResult>)typeof(Beta111PredictionProfile)
        .GetField("_identityPredictor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Owner)!;
    private static int _assertions, _exact, _matches, _rejects, _directHits, _fallbacks, _mutationPairs, _exceptionCases;
    private static JsonSerializerOptions Options()
    {
        var value = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        value.Converters.Add(new SearchWorkspacePersistence.ModelKeyJsonConverter()); return value;
    }
    private static void Need(bool condition, string message) { _assertions++; if (!condition) throw new InvalidDataException(message); }
    private static void Normalize(JsonNode? node)
    {
        if (node is JsonObject obj) { obj.Remove("RequestId"); foreach (var item in obj.ToArray()) Normalize(item.Value); }
        else if (node is JsonArray array)
            for (int i = array.Count - 1; i >= 0; i--)
                if (array[i] is JsonObject d && d["Code"] is JsonValue code && code.TryGetValue<string>(out var text) && text == PredictionDiagnosticCodes.RequestId) array.RemoveAt(i);
                else Normalize(array[i]);
    }
    private static string Full(object value) { var node = JsonSerializer.SerializeToNode(value, Json); Normalize(node); return node!.ToJsonString(Json); }
    private static IReadOnlyList<NeowChoiceResult> Choices(SeedPredictionDocument document) =>
        document.Sections.Single(s => s.Kind == PredictionSectionKind.NeowIdentity).NeowChoices;
    private static string OpeningSnapshot(IReadOnlyList<NeowChoiceResult> choices) => JsonSerializer.Serialize(new
    {
        choices,
        internalStreams = choices.SelectMany(c => c.OpeningRewardContinuations?.Routes ?? [])
            .Select(r => new { r.Route.RouteId, r.NicheState, r.CombatPotionGenerationState }).ToArray()
    }, Json);
    private static ExactSearchExecutionRequest Compile(RuntimeContextAuthoritySnapshot authority, SearchQuery query, bool diagnostics = true, bool enabled = true)
    {
        var context = SearchContextFactory.From(RuntimeProfileId.Beta111, authority.Character.CharacterKey, authority.Ascension,
            authority, Detection, AncientOptionConditionProfile.BroadDefault);
        var options = new SearchRunOptions("9KT7EQEZ0000", 1 << 20, int.MaxValue, 1, IncludeDiagnostics: diagnostics)
            { Optimizations = SearchOptimizationSettings.AllEnabled with { CostOrderedFilters = enabled } };
        var result = ExactSearchExecutionRequestFactory.Compile(SearchCompiler.Compile(query, context), options);
        Need(result.Success && result.Plan is not null, "Compile rejected: " + result.Issue); return result.Plan!;
    }
    private static ProductionExactSearchResult Exact(ExactSearchExecutionRequest plan, string seed, bool bound = true)
    {
        var input = bound ? TrustedRootHashInput.FromCanonicalSeed(Profile, seed) : TrustedRootHashInput.RootOnly(Profile.ComputeRootSeed(seed));
        var old = ProductionExactSearchEvaluator.Evaluate(plan, input, CancellationToken.None, false, forceRecomputeOpening: true);
        var current = ProductionExactSearchEvaluator.Evaluate(plan, input, CancellationToken.None, false, forceRecomputeOpening: false);
        Need(Full(old) == Full(current), "Same-DLL complete Exact/request/authority/document/evidence/witness differs: " + seed);
        _exact++; if (current.IsMatch) _matches++; else if (current.Evaluation.Disposition == SearchDisposition.NoMatch) _rejects++;
        return current;
    }
    private static NormalCombatRewardProjectionRequest Projection(ExactSearchExecutionRequest plan) =>
        plan.Evaluation.NeowRoute is { } pinned ? new(NormalCombatRewardProjectionScope.SearchExact,
            NormalCombatRewardRouteSelectionMode.PinnedRealRoute, pinned.RouteRelicKey, plan.Evaluation.RequiredBonesAcquisitionOrder) :
            new(NormalCombatRewardProjectionScope.SearchExact, NormalCombatRewardRouteSelectionMode.ProvablyNeutralRealRoutes);
    private static SeedPredictionRequest Request(RuntimeContextAuthoritySnapshot authority, TrustedRootHashInput input, bool reward,
        bool diagnostics, bool complex, NormalCombatRewardProjectionRequest? projection = null, int preview = 10,
        AncientOptionConditionProfile? ancient = null)
    {
        Need(SeedPredictionRequest.TryCreateFromRootHash(input, authority.Character, authority.Ascension, authority.PlayersCount,
            authority.PlayerSlotIndex, authority, ancient ?? AncientOptionConditionProfile.BroadDefault,
            reward ? SeedPredictionDomainSelection.Neow | SeedPredictionDomainSelection.NormalCombatRewards : SeedPredictionDomainSelection.Neow,
            preview, diagnostics, projection ?? new(NormalCombatRewardProjectionScope.SearchExact, NormalCombatRewardRouteSelectionMode.ProvablyNeutralRealRoutes),
            out var request, out var error), "Request rejected: " + error);
        return request!.WithComplexBonesDeckInteractions(complex);
    }
    private static RuntimeContextAuthoritySnapshot Project(ExactSearchExecutionRequest plan, TrustedRootHashInput input)
    {
        Need(SearchAuthorityProjector.TryProjectForRootHash(plan, input.RootHash, input.SeedIdentity, out var authority, out _, out var issue), "Authority projection rejected: " + issue);
        return authority;
    }
    private static Beta111NeowOpeningProof Pair(ExactSearchExecutionRequest plan, string seed, bool bound, bool complex, bool requireCheapMatch)
    {
        var input = bound ? TrustedRootHashInput.FromCanonicalSeed(Profile, seed) : TrustedRootHashInput.RootOnly(Profile.ComputeRootSeed(seed));
        var authority = Project(plan, input);
        var cheapRequest = Request(authority, input, false, plan.IncludeDiagnostics, complex);
        var cheap = RuntimeProfileRegistry.PredictFromRootHashCapturingOpening(Detection, cheapRequest, input, out var proof);
        Need(proof is not null && ReferenceEquals(Choices(cheap), proof.Choices), "No captured production opening.");
        var opening = proof!; string before = OpeningSnapshot(opening.Choices);
        var cheapEvaluation = ProductionQueryValidator.EvaluateCheap(plan, cheap, authority);
        if (requireCheapMatch) Need(cheapEvaluation.Disposition == SearchDisposition.Match, "Known complete strict cheap witness was lost.");
        Need(before == OpeningSnapshot(opening.Choices), "Cheap validator mutated opening choices/effects/continuations.");
        var rewardRequest = Request(authority, input, true, plan.IncludeDiagnostics, complex, Projection(plan));
        rewardRequest.PreparedRewardPools = plan.PreparedRewardPools;
        var fresh = RuntimeProfileRegistry.PredictFromRootHash(Detection, rewardRequest, input);
        var reused = RuntimeProfileRegistry.PredictFromRootHashReusingOpening(Detection, rewardRequest, input, opening);
        var twice = RuntimeProfileRegistry.PredictFromRootHashReusingOpening(Detection, rewardRequest, input, opening);
        Need(ReferenceEquals(Choices(reused), opening.Choices) && ReferenceEquals(Choices(twice), opening.Choices), "Reward failed to consume the actual captured choices.");
        Need(!ReferenceEquals(Choices(fresh), opening.Choices), "Fresh-control path accidentally reused opening.");
        Need(Full(fresh) == Full(reused) && Full(reused) == Full(twice), "Reward complete document/RNG traces/route fingerprints differ.");
        Need(before == OpeningSnapshot(opening.Choices), "Reward prediction mutated opening choices or saved RNG state.");
        var currentEvaluation = ProductionQueryValidator.EvaluateRewards(plan, reused, cheapEvaluation);
        var freshEvaluation = ProductionQueryValidator.EvaluateRewards(plan, fresh, cheapEvaluation);
        Need(Full(currentEvaluation) == Full(freshEvaluation), "Reward Exact evidence/witness differs after reuse.");
        Need(before == OpeningSnapshot(opening.Choices), "Reward validator mutated opening.");
        _directHits += 2; _mutationPairs++;
        return opening;
    }
    private static RuntimeContextAuthoritySnapshot Rebuild(RuntimeContextAuthoritySnapshot a, CharacterIdentity? character = null,
        int? ascension = null, int? players = null, int? slot = null, RuntimeProfileId? profile = null, string? version = null,
        bool? noModifiers = null, bool? vanillaCatalog = null, bool unknownIdentity = false) => new(
        character ?? a.Character, ascension ?? a.Ascension, slot ?? a.PlayerSlotIndex, players ?? a.PlayersCount,
        noModifiers ?? a.NoRunModifiers, vanillaCatalog ?? a.VanillaNeowCatalogExact, unknownIdentity ? null : a.AllCharacterCardPoolsUnlocked,
        a.UnlockedCommonCards, a.UnlockedUncommonCards, version ?? a.GameVersion, profile ?? a.ProfileId, a.IsVanilla,
        a.SourceModId, a.SourceAssembly, a.SourceAuthority, a.Completeness, a.ResolutionStatus, a.EffectAuthority, a.WorldAuthority,
        a.PredictionGameMode, a.PredictionGameModeAuthority);
    private static void Fallback(Beta111NeowOpeningProof proof, SeedPredictionRequest request, TrustedRootHashInput input,
        GameVersionDetection detection, Beta111PredictionProfile? owner = null)
    {
        var producer = owner ?? Owner;
        var old = producer.PredictFromRootHash(request, detection, input);
        var current = producer.PredictFromRootHashReusingOpening(request, detection, input, proof);
        Need(Full(old) == Full(current), "Fallback changed a complete Predictor result.");
        if (current.Sections.Any(s => s.Kind == PredictionSectionKind.NeowIdentity))
            Need(!ReferenceEquals(Choices(current), proof.Choices), "Invalid proof unexpectedly reused choices.");
        _fallbacks++;
    }
    private static void FallbackException(Beta111NeowOpeningProof proof, SeedPredictionRequest request, TrustedRootHashInput input, string marker)
    {
        Exception? Read(bool reused)
        {
            try
            {
                if (reused) Owner.PredictFromRootHashReusingOpening(request, Detection, input, proof);
                else Owner.PredictFromRootHash(request, Detection, input);
                return null;
            }
            catch (Exception ex) { return ex; }
        }
        var old = Read(false); var current = Read(true);
        Need(old is not null && current is not null && old.GetType() == current.GetType() &&
            old.Message == current.Message && old.Message.Contains(marker, StringComparison.Ordinal), "Invalid copied context changed its original error instead of falling back.");
        _fallbacks++; _exceptionCases++;
    }
    private static void Boundaries(ExactSearchExecutionRequest plan, string seed)
    {
        var input = TrustedRootHashInput.FromCanonicalSeed(Profile, seed); var authority = Project(plan, input);
        var cheapRequest = Request(authority, input, false, true, true);
        Owner.PredictFromRootHashCapturingOpening(cheapRequest, Detection, input, out var captured);
        var proof = captured ?? throw new InvalidDataException("Boundary setup did not capture opening.");
        var reward = Request(authority, input, true, true, true, Projection(plan));
        var compatibility = RuntimeProfileRegistry.Resolve(Detection);
        bool Try(SeedPredictionRequest request, TrustedRootHashInput root, GameVersionDetection detection, Delegate predictor,
            IRuntimeProfile profile, RuntimeVersionResolution version, bool exact) => proof.TryReuse(Owner, predictor, profile, request, detection, root, version, exact, out _, out _, out _);
        Need(Try(reward, input, Detection, Identity, Profile, compatibility, true), "Valid proof binding unexpectedly rejected.");
        Need(!Try(reward, input, Detection, new Func<ulong, RuntimeContextAuthoritySnapshot, IRuntimeProfile, bool, ModernNeowIdentityResult>(ModernNeowIdentityPredictor.PredictModernCore), Profile, compatibility, true), "Different delegate instance reused proof.");
        Need(!Try(reward, input, Detection, Identity, Beta110Profile.Instance, compatibility, true), "Different profile instance reused proof.");
        Need(!Try(reward, input, Detection, Identity, Profile, compatibility with { BlockedDomains = CompatibilityDomainMask.Neow }, true), "Changed compatibility reused proof.");
        Need(!Try(reward, input, Detection, Identity, Profile, compatibility, false), "Changed authority precision reused proof.");
        foreach (var a in new[] { Rebuild(authority), Rebuild(authority, character: CharacterIdentity.FromKey(BaseGameModelKeys.Characters.Ironclad)),
            Rebuild(authority, ascension: authority.Ascension - 1) })
            Fallback(proof, Request(a, input, true, true, true, Projection(plan)), input, Detection);
        // A mere players/slot edit is deliberately not an authored ordered party.
        // Both old and reused entries must retain its existing error.
        FallbackException(proof, Request(Rebuild(authority, players: 2, slot: 1), input, true, true, true, Projection(plan)), input, "RelicBag.OrderedPartyAuthorityMissing");
        Fallback(proof, reward, input, Detection with { Evidence = "EquivalentVersionDifferentCapture" });
        Fallback(proof, Request(authority, input, true, false, true, Projection(plan)), input, Detection);
        Fallback(proof, Request(authority, input, true, true, false, Projection(plan)), input, Detection);
        Fallback(proof, Request(authority, input, true, true, true, Projection(plan), preview: 9), input, Detection);
        Fallback(proof, Request(authority, input, true, true, true, Projection(plan), ancient: AncientOptionConditionProfile.BroadDefault with { }), input, Detection);
        Fallback(proof, Request(authority, input, true, true, true, NormalCombatRewardProjectionRequest.RichAnalysis), input, Detection);
        Fallback(proof, Request(authority, input, false, true, true), input, Detection);
        var rootOnly = TrustedRootHashInput.RootOnly(input.RootHash);
        Fallback(proof, Request(authority, rootOnly, true, true, true, Projection(plan)), rootOnly, Detection);
        var otherRoot = TrustedRootHashInput.FromCanonicalSeed(Profile, "9KT7EQEZ0000");
        Fallback(proof, Request(authority, otherRoot, true, true, true, Projection(plan)), otherRoot, Detection);
        Fallback(proof, reward, otherRoot, Detection); // Original trusted-request mismatch must win before reuse.
        var partyOverride = Request(authority, input, true, true, true, Projection(plan)); partyOverride.PartyOpeningChoices = proof.Choices;
        Fallback(proof, partyOverride, input, Detection);
        foreach (var bad in new[] { Rebuild(authority, noModifiers: false), Rebuild(authority, vanillaCatalog: false), Rebuild(authority, unknownIdentity: true) })
            Fallback(proof, Request(bad, input, true, true, true, Projection(plan)), input, Detection);
        int identityCalls = 0;
        var injected = new Beta111PredictionProfile((root, a, profile, exact) => { identityCalls++; return Identity(root, a, profile, exact); });
        injected.PredictFromRootHashCapturingOpening(cheapRequest, Detection, input, out var forbidden);
        Need(forbidden is null && identityCalls == 1, "Injected profile captured a proof or skipped its predictor.");
        Fallback(proof, reward, input, Detection, injected);
        Need(identityCalls == 3, "Mismatched owner failed to rerun its identity delegate in both controls.");
        foreach (bool diagnostics in new[] { false, true })
        {
            int failingCalls = 0;
            var failing = new Beta111PredictionProfile((root, a, profile, exact) => { failingCalls++; throw new InvalidOperationException("OpeningProof.FixedFailure"); });
            var request = Request(authority, input, true, diagnostics, true, Projection(plan));
            var doc = failing.PredictFromRootHashReusingOpening(request, Detection, input, proof);
            Need(failingCalls == 1 && doc.Warnings.Any(w => w.Code == PredictionWarningCode.AnalysisFailed), "Proof hid the injected failure.");
            Need(doc.Diagnostics.Single(d => d.Code == PredictionDiagnosticCodes.Exception).Value.Contains("System.InvalidOperationException: OpeningProof.FixedFailure", StringComparison.Ordinal), "Exception evidence lost type/message.");
            if (!diagnostics) Need(doc.Diagnostics.Count == 1, "No-diagnostic failure lost Exception-only behavior.");
            bool threw = false; try { ProductionExactSearchEvaluator.ThrowIfPredictionFailed(doc); } catch (InvalidOperationException ex) { threw = ex.Message.Contains("OpeningProof.FixedFailure", StringComparison.Ordinal); }
            Need(threw, "Exact swallowed AnalysisFailed.");
            failing.PredictFromRootHashReusingOpening(request, Detection, otherRoot, proof);
            Need(failingCalls == 1, "Trusted-input failure ran identity before rejecting the input."); _exceptionCases++;
        }
        var cancelled = new Beta111PredictionProfile((root, a, profile, exact) => throw new OperationCanceledException("OpeningProof.Cancel"));
        bool propagated = false; try { cancelled.PredictFromRootHashReusingOpening(reward, Detection, input, proof); } catch (OperationCanceledException) { propagated = true; }
        Need(propagated, "Cancellation from the Predictor was converted into AnalysisFailed.");
        using var token = new CancellationTokenSource(); token.Cancel();
        foreach (bool recompute in new[] { false, true })
        {
            bool threw = false; try { ProductionExactSearchEvaluator.Evaluate(plan, input, token.Token, false, recompute); } catch (OperationCanceledException) { threw = true; }
            Need(threw, "Exact cancellation was bypassed.");
        }
        Need(!ProductionExactSearchEvaluator.CanReuseNeowOpening(plan with { RunOptions = plan.RunOptions with { Optimizations = plan.RunOptions.Optimizations with { CostOrderedFilters = false } } }), "Disabled optimization admitted reuse.");
        Need(!ProductionExactSearchEvaluator.CanReuseNeowOpening(plan with { PartyOpeningChoices = [] }), "Party override admitted reuse.");
    }
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: ProductionSearchHost <random-complex-fixture-folder> <receipt.json>");
        string fixtures = Path.GetFullPath(args[0]);
        var authority = JsonSerializer.Deserialize<RuntimeContextAuthoritySnapshot>(File.ReadAllText(Path.Combine(fixtures, "source-authority.json")), Json)!;
        using var witness = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "RC04.witness.json")));
        string strictSeed = witness.RootElement.GetProperty("seed").GetString()!;
        var strict = witness.RootElement.GetProperty("query").Deserialize<SearchQuery>(Json)!;
        var target = authority.EffectAuthority!.CharacterRewardPool!.First(c => c.Rarity == EffectCardRarity.Common && c.IsUnlockedInCapturedPool).CardKey;
        var neutral = SearchQuery.Empty with { CombatCardRewards = new(3, CombatRewardSequenceOrderMode.Ordered, [target, null, null]) };
        foreach (bool diagnostics in new[] { false, true })
        foreach (bool bound in new[] { false, true })
        {
            var exact = Compile(authority, strict, diagnostics); Need(ProductionExactSearchEvaluator.CanReuseNeowOpening(exact), "Strict RC04 did not admit reuse.");
            Need(Exact(exact, strictSeed, bound).IsMatch, "Original full RC04 witness failed; do not relax the query.");
            foreach (string seed in new[] { "9KT7EQEZ0000", "9KT7EQEZ002M", "9KT7EQEZ003J" }) Exact(exact, seed, bound);
            Pair(exact, strictSeed, bound, complex: true, requireCheapMatch: true);
            Pair(exact, strictSeed, bound, complex: false, requireCheapMatch: false);
            foreach (bool preparedPools in new[] { false, true })
            {
                var plan = Compile(authority, neutral, diagnostics); if (!preparedPools) plan = plan with { PreparedRewardPools = null };
                Need(ProductionExactSearchEvaluator.CanReuseNeowOpening(plan), "Generic neutral reward plan did not admit reuse.");
                foreach (string seed in new[] { "9KT7EQEZ0000", "9KT7EQEZ002M", "9KT7EQEZ000E", "9KT7EQEZ003J" }) Exact(plan, seed, bound);
                Pair(plan, "9KT7EQEZ002M", bound, complex: false, requireCheapMatch: false);
            }
        }
        Boundaries(Compile(authority, strict), strictSeed);
        var disabled = Compile(authority, neutral, enabled: false); Need(!ProductionExactSearchEvaluator.CanReuseNeowOpening(disabled), "Disabled plan admitted reuse."); Exact(disabled, "9KT7EQEZ002M");
        Need(!ProductionExactSearchEvaluator.CanReuseNeowOpening(Compile(authority, SearchQuery.Empty)), "Non-reward shape admitted reuse.");
        var neutralPositivePlan = Compile(authority, neutral); int neutralPositiveMatches = 0, neutralPositiveRejects = 0; string? neutralWitness = null;
        for (ulong n = 0; n < 256 && (n < 32 || neutralPositiveMatches == 0); n++)
        {
            string seed = VisibleSeedCandidateCodec.FormatOrdinal(Profile, 672575402513859024UL + n * 127);
            var result = Exact(neutralPositivePlan, seed);
            if (result.IsMatch) { neutralPositiveMatches++; neutralWitness ??= seed; }
            else if (result.Evaluation.Disposition == SearchDisposition.NoMatch) neutralPositiveRejects++;
        }
        Need(neutralPositiveMatches > 0 && neutralPositiveRejects > 0, "Neutral reward coverage lacked its own full positive/negative Exact witnesses.");
        Pair(neutralPositivePlan, neutralWitness!, bound: true, complex: false, requireCheapMatch: true);
        Need(_matches > 0 && _rejects > 0 && _directHits > 0 && _fallbacks >= 15, "Coverage was vacuous.");
        var receipt = new { result = "PASS", assertions = _assertions, revision = ProductionExactSearchEvaluator.NeowOpeningReuseRevision,
            exactCases = _exact, matches = _matches, noMatches = _rejects, actualChoiceReferenceReuseHits = _directHits,
            completeOpeningMutationPairs = _mutationPairs, deliberateFallbackCases = _fallbacks, injectedFailureCases = _exceptionCases,
            neutralPositiveMatches, neutralPositiveRejects, neutralWitness,
            sourceFixture = fixtures, strictRC04QueryUnchanged = true, strictWitness = strictSeed,
            neutralRewardScope = "Captured Silent character pool; real authored F02 Ironclad/Havoc is separately game-validated by Root",
            dllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SeedPredictionDocument).Assembly.Location))),
            scope = "Offline same-DLL forceRecomputeOpening complete Exact parity and local proof mutation/error/binding checks; no game or performance claim" };
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(receipt, new JsonSerializerOptions(Json) { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(receipt, Json));
    }
}
