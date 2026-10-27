using System.Diagnostics;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.World;
using RolltheSpire2.Core.World.Beta109;
using RolltheSpire2.Core.World.Snapshots;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Predictability;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

namespace ExactWorldProjectionChecks;

internal static class Program
{
    private static int _assertions, _parityCases, _matches, _rejects;
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly IRuntimeProfile Profile = Beta111Profile.Instance;
    private static readonly GameVersionDetection Detection = new("0.111.0", "0.111.0", "beta", "FrozenExactWorldProjectionCpuChecks", true, "");
    private static RuntimeContextAuthoritySnapshot _authority = null!;
    private static SearchContext _context = null!;
    private static ulong _random = 0x20261022B055F00DUL;

    private static JsonSerializerOptions Options()
    {
        var result = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        result.Converters.Add(new SearchWorkspacePersistence.ModelKeyJsonConverter());
        return result;
    }
    private static void Require(bool value, string message)
    {
        _assertions++;
        if (!value) throw new InvalidDataException(message);
    }
    private static void Equal<T>(T expected, T actual, string message) =>
        Require(JsonSerializer.Serialize(expected, Json) == JsonSerializer.Serialize(actual, Json), message);
    private static void Report(object value) => Console.WriteLine("RT2_EXACT_WORLD_PROJECTION " + JsonSerializer.Serialize(value, Json));
    private static ModelKey Key(string text) => new(text.Split(':')[0], text.Split(':')[1]);
    private static ModelKeySetFilter Any(ModelKey value) => new([value], [], []);
    private static ulong Next()
    {
        unchecked
        {
            ulong value = _random += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
    private static ExactSearchExecutionRequest Request(SearchQuery query, string seed, bool diagnostics = false)
    {
        var compiled = SearchCompiler.Compile(query, _context);
        var result = ExactSearchExecutionRequestFactory.Compile(compiled,
            new(seed, 1, int.MaxValue, 1, IncludeDiagnostics: diagnostics));
        if (!result.Success || result.Plan is null) throw new InvalidDataException("Compile: " + result.Issue);
        return result.Plan;
    }
    private static ProductionExactSearchResult Evaluate(ExactSearchExecutionRequest plan, string seed, bool full) =>
        ProductionExactSearchEvaluator.Evaluate(plan, TrustedRootHashInput.FromCanonicalSeed(Profile, seed),
            CancellationToken.None, forceFullWorldProjection: full);
    private static void CompareExact(ExactSearchExecutionRequest plan, string seed, string label)
    {
        var all = Evaluate(plan, seed, true);
        var scoped = Evaluate(plan, seed, false);
        Equal(all.Evaluation, scoped.Evaluation, label + ": Exact disposition/evidence/routes/witnesses differ.");
        Require(all.FailureCode == scoped.FailureCode && all.IsMatch == scoped.IsMatch,
            label + ": Exact acceptance or failure differs.");
        Require(all.Authority.WorldSnapshotFingerprint == scoped.Authority.WorldSnapshotFingerprint &&
            all.Authority.EffectSnapshotFingerprint == scoped.Authority.EffectSnapshotFingerprint,
            label + ": immutable authority binding differs.");
        var allBosses = all.Document?.Sections.SelectMany(s => s.Bosses).Select(b => b with { RngTrace = [] }).ToArray();
        var scopedBosses = scoped.Document?.Sections.SelectMany(s => s.Bosses).Select(b => b with { RngTrace = [] }).ToArray();
        Equal(allBosses, scopedBosses, label + ": boss identities, precision, call counts or authority differ.");
        _parityCases++;
        if (scoped.IsMatch) _matches++; else if (scoped.Evaluation.Disposition == SearchDisposition.NoMatch) _rejects++;
    }

    private static void Main(string[] args)
    {
        string fixtures = Path.GetFullPath(args.Length > 0 ? args[0] : ".build-evidence/broad-original-comparison/scenarios/random-complex-v1");
        _authority = JsonSerializer.Deserialize<RuntimeContextAuthoritySnapshot>(File.ReadAllText(Path.Combine(fixtures, "source-authority.json")), Json)!;
        _context = SearchContextFactory.From(RuntimeProfileId.Beta111, _authority.Character.CharacterKey, _authority.Ascension,
            _authority, Detection, AncientOptionConditionProfile.BroadDefault);
        TestRngTraceParity();
        TestProviderBoundaryParity();

        var timed = new List<(ExactSearchExecutionRequest Plan, string Seed)>();
        foreach (string id in new[] { "RC05", "RC06", "RC07", "RC08" })
        {
            using var witness = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, id + ".witness.json")));
            string seed = witness.RootElement.GetProperty("seed").GetString()!;
            var query = witness.RootElement.GetProperty("query").Deserialize<SearchQuery>(Json)!;
            var plan = Request(query, seed);
            Require(ExactWorldProjectionPolicy.Resolve(plan) == WorldPredictionProjection.BossIdentityOnly,
                id + ": fixture did not enter the query-demand path.");
            Require(Evaluate(plan, seed, false).IsMatch, id + ": preregistered witness no longer matches.");
            CompareExact(plan, seed, id + ":witness");
            for (int i = 0; i < 16; i++)
                CompareExact(plan, VisibleSeedCandidateCodec.FormatOrdinal(Profile, Next() % VisibleSeedCandidateCodec.SpaceSize(Profile)), id + ":random" + i);
            timed.Add((plan, seed));
        }
        Report(new { phase = "complexFixtureParity", result = "PASS", fixtures = 4, randomPerFixture = 16,
            sameDiagnosticFlag = true, includeDiagnostics = false, checks = "All+false versus BossProjection+false" });

        ExactSearchExecutionRequest? samplePlan = null;
        for (int i = 0; i < 48; i++)
        {
            string seed = VisibleSeedCandidateCodec.FormatOrdinal(Profile, Next() % VisibleSeedCandidateCodec.SpaceSize(Profile));
            ulong root = Profile.ComputeRootSeed(seed);
            var world = WorldPredictionEngine.PredictFromRootHash(Profile, root, seed, _authority.Ascension, 0,
                _authority.Character.CharacterKey, _authority.WorldAuthority, _authority.EffectAuthority,
                AncientOptionConditionProfile.BroadDefault);
            Require(world.Bosses.Count >= 3, "Frozen authority could not provide independent random boss observations.");
            var query = SearchQuery.Empty with
            {
                LegacyWorld = LegacyWorldSemanticConstraints.Empty with
                {
                    BossOrdinalFilters = world.Bosses.Select(b => new ActOrdinalModelKeySetFilter(b.Act, b.Ordinal, Any(b.BossKey))).ToArray()
                }
            };
            var plan = Request(query, seed);
            samplePlan ??= plan;
            CompareExact(plan, seed, "randomBossPositive" + i);
            Require(Evaluate(plan, seed, false).IsMatch, "Constructed random positive was not a Match.");
            var first = query.LegacyWorld.BossOrdinalFilters[0];
            var alternate = _authority.WorldAuthority!.Beta109Generation!.OrderedActCatalog
                .Where(a => a.Act == first.Act).SelectMany(a => a.Bosses).First(k => !first.Keys.Any.Contains(k));
            var negativeQuery = query with { LegacyWorld = query.LegacyWorld with
                { BossOrdinalFilters = query.LegacyWorld.BossOrdinalFilters.Select((b, n) => n == 0 ? b with { Keys = Any(alternate) } : b).ToArray() } };
            var negative = Request(negativeQuery, seed);
            CompareExact(negative, seed, "randomBossNegative" + i);
            Require(Evaluate(negative, seed, false).Evaluation.Disposition == SearchDisposition.NoMatch,
                "Constructed random negative was not NoMatch.");

            var scopedWorld = WorldPredictionEngine.PredictFromRootHash(Profile, root, seed, _authority.Ascension, 0,
                _authority.Character.CharacterKey, _authority.WorldAuthority, _authority.EffectAuthority,
                AncientOptionConditionProfile.BroadDefault, WorldPredictionProjection.BossIdentityOnly);
            Equal(world.Bosses.Select(b => b with { RngTrace = [] }).ToArray(), scopedWorld.Bosses.ToArray(),
                "World continuation changed on a random seed.");
            Require(scopedWorld.Ancients.Count == 0 && scopedWorld.EncounterSequences.Count == 0 &&
                scopedWorld.EventPoolSequencePrediction is null && scopedWorld.Bosses.All(b => b.RngTrace.Count == 0),
                "Boss-only projection retained unused output/trace allocations.");
            var ordinary = WorldPredictionEngine.Predict(Profile, seed, _authority.Ascension, 0, _authority.Character.CharacterKey,
                _authority.WorldAuthority, _authority.EffectAuthority, AncientOptionConditionProfile.BroadDefault);
            Equal(world, ordinary, "Ordinary analysis no longer equals the complete default root-hash projection.");
            Require(world.Bosses.Any(b => b.RngTrace.Count > 0) && world.Ancients.Count > 0 &&
                world.EncounterSequences.Count > 0 && world.EventPoolSequencePrediction is not null,
                "Default/ordinary analysis lost its complete World output.");
        }
        TestFallbacks(samplePlan!);
        TestTimingIdentity(samplePlan!);
        TestOrdinarySearchPolicy(samplePlan!, fixtures);
        TestCancellation(samplePlan!);
        BenchmarkSameFlags(timed);
        Require(_matches >= 52 && _rejects >= 48, "Parity did not cover enough genuine positive and negative Exact paths.");
        Report(new { phase = "complete", result = "PASS", assertions = _assertions, parityCases = _parityCases,
            matches = _matches, rejects = _rejects, noGpuUsed = true, timingRevision = ExactWorldProjectionPolicy.Revision });
    }

    private static void TestRngTraceParity()
    {
        for (int seed = 0; seed < 128; seed++)
        {
            ulong root = Next();
            var traced = Beta109WorldRng.CreateNamed(root, "up_front");
            var lean = Beta109WorldRng.CreateNamed(root, "up_front", captureTrace: false);
            for (int step = 0; step < 64; step++)
            {
                int bound = 1 + (int)(Next() % 4096);
                Require(traced.NextInt(bound, "parity") == lean.NextInt(bound, "parity"), "Trace policy changed NextInt.");
                Require(traced.NextDouble("parity") == lean.NextDouble("parity"), "Trace policy changed NextDouble.");
                Require(traced.NextFloat("parity") == lean.NextFloat("parity"), "Trace policy changed NextFloat.");
                ModelKey[] keys = [Key("ENCOUNTER:A"), Key("ENCOUNTER:B"), Key("ENCOUNTER:C")];
                Require(traced.NextModelKey(keys, "parity") == lean.NextModelKey(keys, "parity"), "Trace policy changed NextModelKey.");
                traced.AnnotateLastSelection(keys[0]); lean.AnnotateLastSelection(keys[0]);
                var a = keys.ToArray(); var b = keys.ToArray();
                traced.UnstableShuffle(a, "parity"); lean.UnstableShuffle(b, "parity");
                Require(a.SequenceEqual(b), "Trace policy changed shuffle results.");
            }
            Equal(traced.CaptureState(), lean.CaptureState(), "Trace policy changed final RNG state/call count.");
            Require(lean.Trace.Count == 0 && traced.Trace.Count > 0, "RNG trace capture policy was ignored.");
            var resumed = Beta109WorldRng.FromCheckpoint(lean.CaptureState(), "up_front", captureTrace: false);
            Require(traced.NextDouble("resume") == resumed.NextDouble("resume"), "Trace-free checkpoint resume differs.");
            foreach (int count in new[] { 0, 1, 2, 7, 64, 128, 1024 })
            {
                var materialized = Beta109WorldRng.CreateNamed(root, "up_front");
                var discarded = Beta109WorldRng.CreateNamed(root, "up_front", captureTrace: false);
                materialized.UnstableShuffle(Enumerable.Range(0, count).ToArray(), "initialization");
                discarded.ConsumeUnobservedShuffle(count);
                Equal(materialized.CaptureState(), discarded.CaptureState(), "Discarded relic shuffle changed its RNG continuation.");
            }
        }
        Report(new { phase = "rngParity", result = "PASS", roots = 128, mixedDrawStepsPerRoot = 64 });
    }

    private static void TestFallbacks(ExactSearchExecutionRequest plan)
    {
        Require(ExactWorldProjectionPolicy.Resolve(plan) == WorldPredictionProjection.BossIdentityOnly, "Boss-only sample was not eligible.");
        ExactSearchExecutionRequest WithQuery(SearchQuery query) => plan with
        {
            CompiledSearch = plan.CompiledSearch with { Normalization = plan.CompiledSearch.Normalization with { NormalizedQuery = query } }
        };
        var q = plan.CompiledSearch.NormalizedQuery;
        var filters = new[]
        {
            plan with { RunOptions = plan.RunOptions with { IncludeDiagnostics = true } },
            plan with { PartyOpeningChoices = [] },
            plan with { CompiledSearch = plan.CompiledSearch with { Context = plan.CompiledSearch.Context with { ProfileId = RuntimeProfileId.Beta110 } } },
            plan with { Evaluation = plan.Evaluation with { AncientIdentityFilters = [new(2, Any(Key("EVENT:PAEL")))] } },
            plan with { Evaluation = plan.Evaluation with { AncientOptionFilters = [new(2, Any(Key("RELIC:A")))] } },
            plan with { Evaluation = plan.Evaluation with { AncientSeaGlassTargetFilters = [new(2, Any(Key("CHARACTER:SILENT")))] } },
            plan with { Evaluation = plan.Evaluation with { AncientBranchConditions = [new(2, Key("EVENT:PAEL"), [], [])] } },
            plan with { Evaluation = plan.Evaluation with { EventSequenceConditions = [new(1, null, SearchSequenceRangeMode.FirstN, 1, Any(Key("EVENT:A")))] } },
            plan with { Evaluation = plan.Evaluation with { TransformationAggregate = new(TransformationOpening.LeafyPoultice, TransformationPickupOrder.Any,
                false, false, false, TransformationAggregatePredicate.RareCountAtLeast, 1, []) } },
            WithQuery(q with { Players = [new(0, ModelKeySetFilter.Empty)] }),
            WithQuery(q with { StandardMaps = [new(1, StandardMapMetric.GuaranteedMonster, StandardMapComparison.AtLeast, 0)] })
        };
        foreach (var fallback in filters)
            Require(ExactWorldProjectionPolicy.Resolve(fallback) == WorldPredictionProjection.All, "Unknown/richer/diagnostic request did not retain All.");
        var diagnostics = plan with { RunOptions = plan.RunOptions with { IncludeDiagnostics = true } };
        string seed = plan.CanonicalStartSeed;
        var a = Evaluate(diagnostics, seed, false); var b = Evaluate(diagnostics, seed, true);
        Equal(a.Evaluation, b.Evaluation, "Diagnostic Exact differs from forced full Exact.");
        Require(a.Document is not null && a.Document.Diagnostics.Count > 0 &&
            a.Document.Sections.SelectMany(s => s.Bosses).All(x => x.RngTrace.Count > 0) &&
            a.Document.Sections.SelectMany(s => s.Ancients).Any(), "Explicit diagnostic request lost full diagnostics/World output.");
        Require(FamilyExactTimingDomains.Resolve(plan) == SearchVerificationFamily.WorldBossOutputDemandV1 &&
            FamilyExactTimingDomains.Resolve(diagnostics) == SearchVerificationFamily.WorldOnly,
            "Scoped CPU Exact timing reused its historical all-World identity.");
        Report(new { phase = "fallbackAndDiagnostics", result = "PASS", fallbackCases = filters.Length,
            ordinaryDefaultAll = true, diagnosticAll = true, legacyTimingIsolated = true });
    }

    private static void TestProviderBoundaryParity()
    {
        int cases = 0;
        foreach (int ascension in new[] { 9, 10, 20 })
        for (int sample = 0; sample < 16; sample++)
        {
            // A20 is a provider boundary simulation, not a claim that the game's
            // front end or SeedPredictionInputLimits accepts an A20 request.
            string seed = VisibleSeedCandidateCodec.FormatOrdinal(Profile, Next() % VisibleSeedCandidateCodec.SpaceSize(Profile));
            ulong root = Profile.ComputeRootSeed(seed);
            var world = _authority.WorldAuthority! with
            {
                Beta109Generation = _authority.WorldAuthority!.Beta109Generation! with { Ascension = ascension }
            };
            WorldPredictionResult Predict(WorldAuthoritySnapshot source, bool lean) => WorldPredictionEngine.PredictFromRootHash(
                Profile, root, seed, ascension, 0, _authority.Character.CharacterKey, source, _authority.EffectAuthority,
                AncientOptionConditionProfile.BroadDefault,
                lean ? WorldPredictionProjection.BossIdentityOnly : WorldPredictionProjection.All);
            BossPredictionResult[] WithoutTrace(WorldPredictionResult result) =>
                result.Bosses.Select(b => b with { RngTrace = [] }).ToArray();
            var all = Predict(world, false); var scoped = Predict(world, true);
            Equal(WithoutTrace(all), WithoutTrace(scoped), "A" + ascension + ": full/lean provider boss output differs.");
            var first = scoped.Bosses.Single(b => b.Act == 3 && b.Ordinal == 1);
            var second = scoped.Bosses.SingleOrDefault(b => b.Act == 3 && b.Ordinal == 2);
            Require(ascension >= 10 ? second is not null : second is null,
                "Second-boss A10 threshold changed.");
            if (second is not null)
                Require(second.BossKey != first.BossKey && second.RngCallCount > first.RngCallCount,
                    "A10+ second boss failed its exclusion or RNG continuation rule.");

            // Match the same root identity before binding a genuine checkpoint.
            // Changing the visible identity would correctly downgrade it to
            // replay-from-start, so explicitly assert this path is retained.
            var projected = Beta109WorldSnapshotProjector.ProjectAuthorityForRootHash(world, root, seed);
            var initialized = Beta109WorldRng.CreateNamed(root, "up_front", captureTrace: false);
            var generation = projected.Beta109Generation!;
            foreach (var bucket in generation.SharedRelicBuckets.Concat(generation.PlayerRelicBuckets))
                initialized.ConsumeUnobservedShuffle(bucket.OrderedRelics.Count);
            var checkpointGeneration = generation with
            {
                UpFrontPrefix = new(Beta109UpFrontPrefixAuthorityKind.ExactCheckpoint,
                    Beta109UpFrontReplayOrigin.BeforeGenerateRooms, initialized.CaptureState(), true,
                    "ExactWorldProjectionChecks:SameRootBeforeGenerateRooms")
            };
            var checkpointWorld = projected with { Beta109Generation = checkpointGeneration };
            Require(Beta109WorldSnapshotProjector.ProjectForRootHash(checkpointGeneration, root, seed).UpFrontPrefix.Kind ==
                Beta109UpFrontPrefixAuthorityKind.ExactCheckpoint,
                "Provider checkpoint case accidentally fell back to replay-from-start.");
            var allCheckpoint = Predict(checkpointWorld, false); var leanCheckpoint = Predict(checkpointWorld, true);
            Equal(WithoutTrace(allCheckpoint), WithoutTrace(leanCheckpoint),
                "ExactCheckpoint full/lean provider boss output differs.");
            Equal(WithoutTrace(scoped).Select(b => b with { AuthorityFingerprint = "" }).ToArray(),
                WithoutTrace(leanCheckpoint).Select(b => b with { AuthorityFingerprint = "" }).ToArray(),
                "Same-state checkpoint changed a requested boss field or RNG call count.");
            cases++;
        }
        Report(new { phase = "providerBoundaries", result = "PASS", cases, ascensions = new[] { 9, 10, 20 },
            secondBossThresholdAndExclusion = true, exactCheckpointRetained = true,
            a20IsProviderSimulationOnly = true });
    }

    private static void TestCancellation(ExactSearchExecutionRequest plan)
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (bool full in new[] { false, true })
        {
            try
            {
                ProductionExactSearchEvaluator.Evaluate(plan, TrustedRootHashInput.FromCanonicalSeed(Profile, plan.CanonicalStartSeed),
                    cancelled.Token, forceFullWorldProjection: full);
                throw new InvalidDataException("Pre-cancelled Exact unexpectedly returned a candidate.");
            }
            catch (OperationCanceledException) { _assertions++; }
        }
        CompareExact(plan, plan.CanonicalStartSeed, "postCancellationReuse");
        Report(new { phase = "cancellation", result = "PASS", preCancelledBothPolicies = true, reusableAfterCancellation = true,
            note = "Session bounded queue, shared CPU budget and committed-prefix logic are unchanged and covered by CpuExecutionChecks." });
    }

    private static void TestTimingIdentity(ExactSearchExecutionRequest sample)
    {
        // Persisted enum values retain their historical positions. Diagnostic
        // isolation is appended, never inserted into an existing numeric range.
        string[] historical = ["NeowOnly", "RelicOnly", "WorldOnly", "CombatRewardOnly", "MixedWithoutReward", "MixedWithReward",
            "MapOnly", "MapMixedWithoutReward", "MapMixedWithReward", "Party2", "Party3", "Party4", "Party2Map", "Party3Map", "Party4Map",
            "Party2Reward", "Party3Reward", "Party4Reward", "Party2MapReward", "Party3MapReward", "Party4MapReward"];
        var replacements = new HashSet<SearchVerificationFamily>();
        for (int index = 0; index < historical.Length; index++)
        {
            var oldFamily = Enum.Parse<SearchVerificationFamily>(historical[index]);
            Require((int)oldFamily == index, "Historical persisted Exact family value changed.");
            var newFamily = FamilyExactTimingDomains.WithoutDiagnostics(oldFamily);
            Require(newFamily != oldFamily && newFamily.ToString() == historical[index] + "NoDiagnosticsV1" &&
                replacements.Add(newFamily), "Non-diagnostic workload identity collided with old/other timing evidence.");
        }
        foreach (var scoped in new[] { SearchVerificationFamily.WorldBossOutputDemandV1,
            SearchVerificationFamily.MixedBossOutputDemandWithoutRewardV1, SearchVerificationFamily.MixedBossOutputDemandWithRewardV1 })
            Require(FamilyExactTimingDomains.WithoutDiagnostics(scoped) == scoped && !replacements.Contains(scoped),
                "Revisioned Boss projection lost its own non-diagnostic identity.");
        var empty = Request(SearchQuery.Empty, sample.CanonicalStartSeed);
        Require(FamilyExactTimingDomains.Resolve(empty) == SearchVerificationFamily.MixedWithoutRewardNoDiagnosticsV1,
            "Non-Boss/empty non-diagnostic request reused full-output timing evidence.");
        var richer = sample with { Evaluation = sample.Evaluation with { AncientIdentityFilters = [new(2, Any(Key("EVENT:PAEL")))] } };
        Require(ExactWorldProjectionPolicy.Resolve(richer) == WorldPredictionProjection.All &&
            FamilyExactTimingDomains.Resolve(richer) == SearchVerificationFamily.WorldOnlyNoDiagnosticsV1,
            "Richer World fallback did not isolate its non-diagnostic timing workload.");
        Require(FamilyExactTimingDomains.Resolve(richer with { RunOptions = richer.RunOptions with { IncludeDiagnostics = true } }) ==
            SearchVerificationFamily.WorldOnly, "Explicit diagnostic fallback lost its historical full-output identity.");
        Report(new { phase = "timingIdentity", result = "PASS", historicalValuesUnchanged = historical.Length,
            nonDiagnosticFamiliesIsolated = replacements.Count, partyFamiliesIncluded = 12, bossProjectionFamiliesSeparate = 3,
            note = "Stored Exact evidence can affect initial physical planning; an active session's plan remains frozen." });
    }

    private static void BenchmarkSameFlags(List<(ExactSearchExecutionRequest Plan, string Seed)> cases)
    {
        // Local CPU evidence only, not an end-to-end GPU search speed claim.
        // Alternating batches retain the identical IncludeDiagnostics:false flag.
        var rounds = new List<object>();
        for (int round = 0; round < 2; round++)
        foreach (bool full in round == 0 ? new[] { true, false } : new[] { false, true })
        {
            foreach (var item in cases) Evaluate(item.Plan, item.Seed, full);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            const int repeats = 8;
            for (int repeat = 0; repeat < repeats; repeat++)
            foreach (var item in cases)
                Require(Evaluate(item.Plan, item.Seed, full).IsMatch, "Local benchmark lost a fixed positive.");
            watch.Stop();
            rounds.Add(new { round = round + 1, projection = full ? "All" : "BossIdentityOnly", includeDiagnostics = false,
                attempts = repeats * cases.Count, elapsedMs = watch.Elapsed.TotalMilliseconds,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated });
        }
        Report(new { phase = "localCpuSameFlag", result = "PASS", endToEndSpeedClaim = false, rounds });
    }

    private static void TestOrdinarySearchPolicy(ExactSearchExecutionRequest sample, string fixtures)
    {
        var original = sample with { RunOptions = sample.RunOptions with { IncludeDiagnostics = true } };
        var ordinary = ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(original);
        Require(original.IncludeDiagnostics && !ordinary.IncludeDiagnostics &&
            ExactWorldProjectionPolicy.Resolve(ordinary) == WorldPredictionProjection.BossIdentityOnly &&
            original.CompiledSearch == ordinary.CompiledSearch && original.SnapshotFingerprint == ordinary.SnapshotFingerprint,
            "Ordinary Boss policy failed to preserve query/context while narrowing only diagnostics.");
        Require(ReferenceEquals(ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(ordinary), ordinary),
            "Repeated ordinary policy application unnecessarily replaced its immutable request.");
        var explicitDiagnostics = Evaluate(original, original.CanonicalStartSeed, false);
        Require(original.IncludeDiagnostics && explicitDiagnostics.Document is { Diagnostics.Count: > 0 } &&
            explicitDiagnostics.Document.Sections.SelectMany(s => s.Bosses).All(b => b.RngTrace.Count > 0),
            "An explicit diagnostic/API request was changed by ordinary-search preparation elsewhere.");

        using (var witness = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "RC03.witness.json"))))
        {
            string seed = witness.RootElement.GetProperty("seed").GetString()!;
            var query = witness.RootElement.GetProperty("query").Deserialize<SearchQuery>(Json)!;
            var rc03 = Request(query, seed, diagnostics: true);
            Require(ReferenceEquals(ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(rc03), rc03) && rc03.IncludeDiagnostics,
                "RC03/non-Boss ordinary search lost its resource.13 diagnostic workload.");
        }
        var empty = Request(SearchQuery.Empty, original.CanonicalStartSeed, diagnostics: true);
        Require(ReferenceEquals(ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(empty), empty) && empty.IncludeDiagnostics,
            "Empty/non-Boss ordinary search changed its historical workload.");
        var richer = original with { Evaluation = original.Evaluation with { AncientIdentityFilters = [new(2, Any(Key("EVENT:PAEL")))] } };
        Require(ReferenceEquals(ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(richer), richer) && richer.IncludeDiagnostics,
            "Richer World ordinary search did not retain its full projection.");
        var mappedQuery = original.CompiledSearch.NormalizedQuery with
            { StandardMaps = [new(1, StandardMapMetric.GuaranteedMonster, StandardMapComparison.AtLeast, 0)] };
        var mapped = original with { CompiledSearch = original.CompiledSearch with
            { Normalization = original.CompiledSearch.Normalization with { NormalizedQuery = mappedQuery } } };
        Require(ReferenceEquals(ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(mapped), mapped) && mapped.IncludeDiagnostics,
            "Map ordinary search did not retain its full projection.");

        int partyCases = 0;
        foreach (int count in new[] { 2, 3, 4 })
        {
            var sourceWorld = _authority.WorldAuthority!;
            var partyWorld = sourceWorld with { Beta109Generation = sourceWorld.Beta109Generation! with
            {
                IsMultiplayer = true, GameMode = WorldGameMode.Multiplayer, PlayerCount = count,
                LobbyPlayers = Enumerable.Range(0, count).Select(i => new Beta109LobbyPlayerSnapshot(i, _authority.Character.CharacterKey, false, true)).ToArray(),
                PartyRelicBuckets = Enumerable.Range(0, count).Select(_ => sourceWorld.Beta109Generation!.PlayerRelicBuckets).ToArray(),
                PartyBossDiscoveryExact = true
            } };
            RuntimeContextAuthoritySnapshot Seat(int slot) => new(_authority.Character, _authority.Ascension, slot, count,
                _authority.NoRunModifiers, _authority.VanillaNeowCatalogExact, _authority.AllCharacterCardPoolsUnlocked,
                _authority.UnlockedCommonCards, _authority.UnlockedUncommonCards, _authority.GameVersion, _authority.ProfileId,
                _authority.IsVanilla, _authority.SourceModId, _authority.SourceAssembly, _authority.SourceAuthority,
                _authority.Completeness, _authority.ResolutionStatus, _authority.EffectAuthority, partyWorld,
                WorldGameMode.Multiplayer, _authority.PredictionGameModeAuthority);
            var party = new OrderedPartyAuthority(Enumerable.Range(0, count).Select(Seat).ToArray(),
                Enumerable.Repeat("FrozenSyntheticPolicyMatrix", count).ToArray(), partyWorld);
            foreach (bool map in new[] { false, true })
            foreach (bool reward in new[] { false, true })
            {
                var query = SearchQuery.Empty with
                {
                    StandardMaps = map ? mappedQuery.StandardMaps : [],
                    Players = [new(0, ModelKeySetFilter.Empty) { Conditions = reward ? SearchQuery.Empty with
                        { CombatCardRewards = new(1, CombatRewardSequenceOrderMode.Ordered, [Key("CARD:STRIKE_SILENT")]) } : SearchQuery.Empty }]
                };
                var request = original with { CompiledSearch = original.CompiledSearch with
                {
                    Context = original.CompiledSearch.Context with { Party = party, Authority = party.Players[0] },
                    Normalization = original.CompiledSearch.Normalization with { NormalizedQuery = query }
                } };
                Require(ExactWorldProjectionPolicy.Resolve(request) == WorldPredictionProjection.All &&
                    ReferenceEquals(ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(request), request) && request.IncludeDiagnostics,
                    "Party ordinary search lost its full projection.");
                string name = "Party" + count + (map ? "Map" : "") + (reward ? "Reward" : "");
                Require(FamilyExactTimingDomains.Resolve(request) == Enum.Parse<SearchVerificationFamily>(name) &&
                    FamilyExactTimingDomains.Resolve(request with { RunOptions = request.RunOptions with { IncludeDiagnostics = false } }) ==
                    Enum.Parse<SearchVerificationFamily>(name + "NoDiagnosticsV1"),
                    "Actual Party request diagnostics did not select the matching isolated timing family.");
                partyCases++;
            }
        }

        // Start seed, finite preview horizon and worker count may differ in the
        // UI preview. They must select exactly the same output/timing policy.
        var preview = ExactSearchExecutionRequestFactory.Compile(original.CompiledSearch,
            new("000000000000", 1 << 20, 1, 1));
        Require(preview.Success && preview.Plan is not null, "Ordinary preview fixture failed to compile.");
        var preparedPreview = ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(preview.Plan!);
        Require(preview.Plan!.IncludeDiagnostics && preparedPreview.IncludeDiagnostics == ordinary.IncludeDiagnostics &&
            ExactWorldProjectionPolicy.Resolve(preparedPreview) == ExactWorldProjectionPolicy.Resolve(ordinary) &&
            FamilyExactTimingDomains.Resolve(preparedPreview) == FamilyExactTimingDomains.Resolve(ordinary),
            "Preview and live ordinary-search policy diverged because of horizon/seed/options.");
        Report(new { phase = "ordinarySearchPolicy", result = "PASS", bossScopedOnly = true, rc03AndNonBossUnchanged = true,
            explicitDiagnosticApiUnchanged = true, mapUnchanged = true, partyShapes = partyCases,
            previewUsesSameHelperAndTimingFamily = true, globalSearchRunOptionsDefaultChanged = false });
    }
}
