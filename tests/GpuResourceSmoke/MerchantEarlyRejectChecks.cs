using System.Security.Cryptography;
using Godot;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Merchant;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private const string MerchantEagerSha256 = "7C60D3B5B14773F7D029E2CBF5A52254FC49C80BAD79196E01DD96633172839D";
    private sealed record MerchantEarlyFixture(string Name, ExactSearchExecutionRequest Request,
        bool HasWitness = true, int WitnessLogicalOrdinal = 0);

    // This suite compares the production executor against two independent
    // numerical implementations: the frozen resource13 eager GPU predicate and
    // the canonical CPU evaluator, which advances every complete merchant.
    // It deliberately does not reconstruct an eager predicate from new source.
    private static void TestMerchantEarlyReject(RenderingDevice rd)
    {
        string referencePath = System.Environment.GetEnvironmentVariable("RT2_MERCHANT_EAGER_SHADER")
            ?? throw new InvalidDataException("RT2_MERCHANT_EAGER_SHADER must name the frozen resource13 template.");
        byte[] referenceBytes = File.ReadAllBytes(referencePath);
        Require(Convert.ToHexString(SHA256.HashData(referenceBytes)) == MerchantEagerSha256,
            "Merchant eager oracle is not the byte-for-byte frozen resource13 shader.");
        string source = File.ReadAllText(referencePath);
        const string marker = "/*__RT2_VISIBLE_SEED_ROOT_HASH__*/";
        Require(source.Contains(marker, StringComparison.Ordinal), "Merchant eager root hash seam missing.");
        source = source.Replace(marker, FamilyGpuComputeUtility.LoadEmbeddedShader("VisibleSeedRootHashCommon.glsl"), StringComparison.Ordinal);
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong start, out _, out string issue), issue);
        using var settings = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled);
        string? prior = System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH");
        int comparisons = 0, fixtures = 0;
        try
        {
            var cases = MerchantEarlyFixtures(start).ToArray();
            foreach (bool cached in new[] { false, true })
            {
                System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", cached ? "1" : "0");
                foreach (var fixture in cases)
                {
                    var batch = new SearchBatch(start, 1031);
                    var dense = FamilyCandidateSet.Dense(batch);
                    var compact = MerchantEarlySparse(batch, fixture.WitnessLogicalOrdinal);
                    ulong[] denseExpected = MerchantEarlyExpected(fixture.Request, dense);
                    ulong[] compactExpected = MerchantEarlyExpected(fixture.Request, compact);
                    Require(fixture.HasWitness ? denseExpected.Contains((ulong)fixture.WitnessLogicalOrdinal) &&
                        compactExpected.Contains((ulong)fixture.WitnessLogicalOrdinal)
                        : denseExpected.Length == 0, "Merchant fixture failed its independent witness/contradiction: " + fixture.Name);
                    using (var eager = new MerchantEagerGpu(rd, source, fixture.Request, 4097))
                    {
                        MerchantEarlyEqual(eager.Execute(dense), denseExpected, fixture.Name + ".eagerDense");
                        MerchantEarlyEqual(eager.Execute(compact), compactExpected, fixture.Name + ".eagerCompact");
                        foreach (var path in new[] { MerchantShopColorlessCompactionPath.AtomicAppendHostSort,
                            MerchantShopColorlessCompactionPath.StableOrderedCompaction })
                        {
                            using var production = MerchantShopColorlessGpuExecutor.Create(rd, fixture.Request, path);
                            Require(production.StableOrderedCompactionEnabled == (path == MerchantShopColorlessCompactionPath.StableOrderedCompaction),
                                "Merchant production compaction route changed.");
                            Require(production.PhysicalImplementationRevision.Contains(SharedVisibleSeedGpuCache.Revision, StringComparison.Ordinal) == cached,
                                "Merchant prefix on/off fixture did not bind the requested production source.");
                            foreach (var (input, expected) in new[] { (dense, denseExpected), (compact, compactExpected) })
                            {
                                var actual = production.Execute(input, CancellationToken.None, out var metrics);
                                MerchantEarlyEqual(actual.EnumerateLogicalOrdinals().ToArray(), expected, fixture.Name + "." + path);
                                Require(actual.Batch == input.Batch && metrics.ProcessedCandidates == input.Count &&
                                    metrics.Survivors == expected.Length, "Merchant public identity/counters changed.");
                                comparisons++;
                            }
                            MerchantEarlyPreCancel(() => production.Execute(dense, new(canceled: true), out _));
                            MerchantEarlyEqual(production.Execute(compact, CancellationToken.None, out _).EnumerateLogicalOrdinals().ToArray(),
                                compactExpected, fixture.Name + ".postCancelReuse");
                        }
                    }
                    MerchantEarlyPrivate(rd, fixture.Request, dense, compact, denseExpected, compactExpected);
                    comparisons += 3;
                    fixtures++;
                    Emit(new { phase = "merchantEarlyFixture", result = "PASS", fixture = fixture.Name, cached,
                        denseInputs = dense.Count, denseSurvivors = denseExpected.Length,
                        compactInputs = compact.Count, compactSurvivors = compactExpected.Length,
                        witnessLogicalOrdinal = fixture.WitnessLogicalOrdinal });
                }
                comparisons += MerchantEarlyBoundaries(rd, source, cases[0].Request, start);
            }
            Emit(new { phase = "merchantEarlyReject", result = "PASS", fixtures, productionComparisons = comparisons,
                eagerSha256 = MerchantEagerSha256, canonicalCpuEvaluator = true, everyMerchant1To5 = true,
                denseAndCompact = true, atomicAndStable = true, privateInputAndOutput = true,
                orderedUnorderedDuplicateWildcard = true, anyFirstThreeOnlySecondOrThird = true, nonzeroWitnesses = true,
                prefixOnAndOff = true, prefixBoundaryAndLastSeed = true,
                preCancelledThenReused = true, invalidPrivateOrdinalThenReused = true,
                limits = "Synthetic32/21And3/2Pools;PrivateStableIsAnExplicitUnsupportedContract;NoWindowPolicyChangeOrPerformanceClaim" });
        }
        finally { System.Environment.SetEnvironmentVariable("RT2_SHARED_PREFIX_HASH", prior); }
    }

    private static int MerchantEarlyBoundaries(RenderingDevice rd, string source, ExactSearchExecutionRequest request, ulong start)
    {
        SearchBatch[] batches = [new(0, 0), new(0, 1), new(1, 7), new(33, 8), new(start, 511), new(start, 512),
            new(start, 513), new(0x100000003UL, 4097), new(PrefixHashCache.SpaceSize - 513, 513),
            new(PrefixHashCache.SpaceSize - 1, 1), new(PrefixHashCache.SpaceSize, 0)];
        int comparisons = 0;
        using var eager = new MerchantEagerGpu(rd, source, request, 4097);
        foreach (var path in new[] { MerchantShopColorlessCompactionPath.AtomicAppendHostSort,
            MerchantShopColorlessCompactionPath.StableOrderedCompaction })
        {
            using var production = MerchantShopColorlessGpuExecutor.Create(rd, request, path);
            foreach (var batch in batches)
            foreach (bool compact in new[] { false, true })
            {
                var input = compact ? MerchantEarlySparse(batch) : FamilyCandidateSet.Dense(batch);
                var expected = MerchantEarlyExpected(request, input);
                MerchantEarlyEqual(eager.Execute(input), expected, "merchant boundary eager");
                var actual = production.Execute(input, CancellationToken.None, out var metrics);
                MerchantEarlyEqual(actual.EnumerateLogicalOrdinals().ToArray(), expected, "merchant boundary production");
                Require(metrics.ProcessedCandidates == input.Count && metrics.Survivors == expected.Length,
                    "Merchant boundary counters changed.");
                comparisons++;
            }
            // Root range and actual Compact population are different quantities.
            // This invokes the real cache-disabled fallback with only a few roots.
            var fallback = MerchantEarlySparse(new(start, 1 << 25));
            var fallbackExpected = MerchantEarlyExpected(request, fallback);
            MerchantEarlyEqual(eager.Execute(fallback), fallbackExpected, "merchant 32M-root eager");
            MerchantEarlyEqual(production.Execute(fallback, CancellationToken.None, out _).EnumerateLogicalOrdinals().ToArray(),
                fallbackExpected, "merchant 32M-root production");
            comparisons++;
        }
        return comparisons;
    }

    private static void MerchantEarlyPrivate(RenderingDevice rd, ExactSearchExecutionRequest request,
        FamilyCandidateSet dense, FamilyCandidateSet compact, ulong[] denseExpected, ulong[] compactExpected)
    {
        using var input = new PrivateOrdinalBuffer(rd);
        using var output = new PrivateOrdinalBuffer(rd);
        uint[] reversed = compact.EnumerateLogicalOrdinals().Reverse().Select(x => checked((uint)x)).ToArray();
        FamilyGpuComputeUtility.Update(rd, input.Buffer, reversed, "MerchantEarly.PrivateInput");
        // Private buffers use atomic append by contract. Stable private ports must
        // be rejected before allocation rather than pretending that route exists.
        Expect<ArgumentException>(() => MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.StableOrderedCompaction, input, output));
        using (var stage = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, output))
        {
            int count = stage.ExecutePrivateStage(compact.Batch, compact.Count, CancellationToken.None, out var metrics);
            MerchantEarlyEqual(output.ReadForVerification(compact.Batch, count).EnumerateLogicalOrdinals().ToArray(), compactExpected, "merchant private stage");
            Require(metrics.ProcessedCandidates == compact.Count && metrics.ReadbackBytes == 32,
                "Merchant private stage changed population/header-only completion.");
            MerchantEarlyPreCancel(() => stage.ExecutePrivateStage(compact.Batch, compact.Count, new(canceled: true), out _));
            FamilyGpuComputeUtility.Update(rd, input.Buffer, [(uint)compact.Batch.BatchCandidateCount], "MerchantEarly.InvalidOrdinal");
            Expect<InvalidDataException>(() => stage.ExecutePrivateStage(compact.Batch, 1, CancellationToken.None, out _));
            FamilyGpuComputeUtility.Update(rd, input.Buffer, reversed, "MerchantEarly.RestorePrivateInput");
            count = stage.ExecutePrivateStage(compact.Batch, compact.Count, CancellationToken.None, out _);
            MerchantEarlyEqual(output.ReadForVerification(compact.Batch, count).EnumerateLogicalOrdinals().ToArray(), compactExpected, "merchant private recovery");
        }
        using (var denseStage = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, null, output))
        {
            int count = denseStage.ExecutePrivateStage(dense.Batch, dense.Count, CancellationToken.None, out _);
            MerchantEarlyEqual(output.ReadForVerification(dense.Batch, count).EnumerateLogicalOrdinals().ToArray(), denseExpected, "merchant dense private output");
        }
        using var terminal = MerchantShopColorlessGpuExecutor.Create(rd, request,
            MerchantShopColorlessCompactionPath.AtomicAppendHostSort, input, null);
        MerchantEarlyEqual(terminal.ExecutePrivate(compact.Batch, compact.Count, CancellationToken.None, out _).EnumerateLogicalOrdinals().ToArray(),
            compactExpected, "merchant terminal private input");
    }

    private static void MerchantEarlyPreCancel(Action execute)
    {
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(execute);
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, "Pre-cancelled merchant call submitted GPU work.");
    }

    private static FamilyCandidateSet MerchantEarlySparse(SearchBatch batch, int witnessLogicalOrdinal = 0)
    {
        ulong[] positions = [0, 1, 6, 7, 8, 33, 34, 511, 512, 1023, 1048575, 1336335, 1336336,
            16777215, 16777216, (ulong)witnessLogicalOrdinal,
            batch.BatchCandidateCount == 0 ? 0UL : (ulong)batch.BatchCandidateCount - 1];
        return FamilyCandidateSet.FromSortedAbi1(batch, positions.Where(x => x < (ulong)batch.BatchCandidateCount).Distinct().Order().ToArray());
    }

    private static ulong[] MerchantEarlyExpected(ExactSearchExecutionRequest request, FamilyCandidateSet input) =>
        input.EnumerateLogicalOrdinals().Where(ordinal =>
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(input.Batch.GlobalCandidate(ordinal)), 0);
            var result = MerchantShopColorlessQueryEvaluator.Evaluate(request.Evaluation, root, request.Authority);
            Require(result.Disposition is SearchDisposition.Match or SearchDisposition.NoMatch,
                "Merchant canonical oracle was not exact.");
            return result.Disposition == SearchDisposition.Match;
        }).ToArray();

    private static void MerchantEarlyEqual(ulong[] actual, ulong[] expected, string scope) =>
        Require(actual.SequenceEqual(expected), $"{scope}: full output differs; actual={actual.Length}; expected={expected.Length}");

    private static IEnumerable<MerchantEarlyFixture> MerchantEarlyFixtures(ulong witnessOrdinal)
    {
        foreach ((int uCount, int rCount) in new[] { (3, 2), (32, 21) })
        {
            var basis = MerchantEarlyRequest(uCount, rCount);
            var authority = Beta111MerchantColorlessAuthority.From(basis.Authority);
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(witnessOrdinal), 0);
            var witness = Beta111NormalMerchantColorlessSequenceProjector.Project(root, authority).Merchants;
            Require(witness.Count == 5, "Merchant witness projection incomplete.");
            ModelKey Key(int ordinal, MerchantColorlessSlot slot) => slot == MerchantColorlessSlot.Uncommon
                ? witness[ordinal - 1].UncommonColorless : witness[ordinal - 1].RareColorless;
            MerchantEarlyFixture Fixture(string name, MerchantColorlessSlotCondition[] slots,
                MerchantColorlessSequenceSearchCondition[]? sequences = null, bool positive = true, int witnessLogicalOrdinal = 0) =>
                new($"{uCount}x{rCount}.{name}", basis with { Evaluation = ExactSearchEvaluationProjection.Empty with
                { MerchantColorlessConditions = slots, MerchantColorlessSequenceConditions = sequences ?? [] } }, positive, witnessLogicalOrdinal);
            foreach (var slot in new[] { MerchantColorlessSlot.Uncommon, MerchantColorlessSlot.Rare })
            foreach (int ordinal in Enumerable.Range(1, 5))
                yield return Fixture($"slot{ordinal}.{slot}", [new(ordinal, slot, Key(ordinal, slot))]);
            yield return Fixture("twoLanesFirstMerchant", [new(1, MerchantColorlessSlot.Uncommon, Key(1, MerchantColorlessSlot.Uncommon)),
                new(1, MerchantColorlessSlot.Rare, Key(1, MerchantColorlessSlot.Rare))]);
            yield return Fixture("fiveSlotContinuation", Enumerable.Range(1, 5)
                .Select(i => new MerchantColorlessSlotCondition(i, MerchantColorlessSlot.Uncommon, Key(i, MerchantColorlessSlot.Uncommon))).ToArray());
            // "Any of the first three" is decided only after its full window.
            // Discover explicit CPU witnesses where the target occurs ONLY in
            // merchant 2 or ONLY in merchant 3; merchant 1 must not reject them.
            foreach (var slot in new[] { MerchantColorlessSlot.Uncommon, MerchantColorlessSlot.Rare })
            foreach (int hitMerchant in new[] { 2, 3 })
            {
                bool found = false;
                for (int candidate = 0; candidate < 1031; candidate++)
                {
                    ulong candidateRoot = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(witnessOrdinal + (ulong)candidate), 0);
                    var firstThree = Beta111NormalMerchantColorlessSequenceProjector.Project(candidateRoot, authority, 3).Merchants;
                    ModelKey[] observed = firstThree.Select(m => slot == MerchantColorlessSlot.Uncommon ? m.UncommonColorless : m.RareColorless).ToArray();
                    ModelKey target = observed[hitMerchant - 1];
                    if (observed.Where((_, i) => i != hitMerchant - 1).Contains(target)) continue;
                    yield return Fixture($"anyFirstThree.{slot}.onlyMerchant{hitMerchant}", [],
                        [new(3, CombatRewardSequenceOrderMode.Unordered, slot, [target, null, null])], witnessLogicalOrdinal: candidate);
                    found = true;
                    break;
                }
                Require(found, "No independent later-merchant positive witness found in the bounded test corpus.");
            }
            foreach (var slot in new[] { MerchantColorlessSlot.Uncommon, MerchantColorlessSlot.Rare })
            foreach (var order in new[] { CombatRewardSequenceOrderMode.Ordered, CombatRewardSequenceOrderMode.Unordered })
            {
                ModelKey?[] targets = Enumerable.Range(1, 5).Select(i => (ModelKey?)Key(i, slot)).ToArray();
                if (order == CombatRewardSequenceOrderMode.Unordered) Array.Reverse(targets);
                yield return Fixture($"sequence5.{slot}.{order}", [], [new(5, order, slot, targets)]);
                targets = (ModelKey?[])targets.Clone(); targets[1] = null; targets[3] = null;
                yield return Fixture($"sequence5Wildcards.{slot}.{order}", [], [new(5, order, slot, targets)]);
            }
            if (uCount != 3) continue;
            // Five draws from a three-card pool necessarily contain a repeated
            // target, so the unordered fixture above verifies multiplicity.
            Require(witness.Select(m => m.UncommonColorless).Distinct().Count() < 5,
                "Merchant duplicate fixture unexpectedly has no duplicate.");
            var first = Key(1, MerchantColorlessSlot.Uncommon);
            yield return Fixture("duplicateSameSlot", [new(1, MerchantColorlessSlot.Uncommon, first), new(1, MerchantColorlessSlot.Uncommon, first)]);
            yield return Fixture("contradictorySameSlot", [new(1, MerchantColorlessSlot.Uncommon, first),
                new(1, MerchantColorlessSlot.Uncommon, authority.UncommonPool.First(k => k != first))], positive: false);
            yield return Fixture("mixedFixedAndTwoSequences", [new(3, MerchantColorlessSlot.Rare, Key(3, MerchantColorlessSlot.Rare))],
                [new(4, CombatRewardSequenceOrderMode.Ordered, MerchantColorlessSlot.Uncommon,
                    [Key(1, MerchantColorlessSlot.Uncommon), null, Key(3, MerchantColorlessSlot.Uncommon), null]),
                 new(5, CombatRewardSequenceOrderMode.Unordered, MerchantColorlessSlot.Rare,
                    witness.Select(m => (ModelKey?)m.RareColorless).Reverse().ToArray())]);
            foreach (var order in new[] { CombatRewardSequenceOrderMode.Ordered, CombatRewardSequenceOrderMode.Unordered })
                yield return Fixture("entirelyWildcard." + order, [], [new(5, order, MerchantColorlessSlot.Uncommon, [null, null, null, null, null])]);
        }
    }

    private static ExactSearchExecutionRequest MerchantEarlyRequest(int uncommonCount, int rareCount)
    {
        NeowEffectCardSnapshot Card(int index, EffectCardRarity rarity)
        {
            ModelKey key = new("CARD", $"MERCHANT_EARLY_{rarity}_{index}");
            return new(key.Serialized, key, index, rarity, EffectCardType.Skill, false, false, false, false, true);
        }
        var cards = Enumerable.Range(0, uncommonCount).Select(i => Card(i, EffectCardRarity.Uncommon))
            .Concat(Enumerable.Range(0, rareCount).Select(i => Card(i, EffectCardRarity.Rare))).ToArray();
        var effects = new NeowEffectAuthoritySnapshot(OrderedDeck: [], CharacterRewardPool: [], ColorlessRewardPool: cards,
            OtherCharacterPools: [], TransformPool: [], PotionPool: [], OrderedRelicBag: [], GeneratedCursePool: [], BonesEligibleRelics: [],
            CharacterStrikeKey: null, CharacterDefendKey: null, ClawKey: null, CardRewardPoolsExact: true, DeckExact: true,
            TransformPoolsExact: true, PotionPoolExact: true, RelicBagExact: true, CursePoolExact: true, BonesEligibilityExact: true,
            CardRewardHooksNoOpExact: true, PotionProcurementHooksNoOpExact: true, NestedRelicHooksExact: true,
            CurrentGold: 0, CurrentPotionCount: 0, PotionCapacity: 3, SourceAuthority: SourceAuthority.AuditedStaticExact,
            Completeness: SnapshotCompleteness.Complete, AuthoritySource: NeowEffectAuthoritySource.FixtureAuthority,
            CapturedProfileId: RuntimeProfileId.Beta111, CatalogFingerprint: "MerchantEarlySynthetic", SnapshotFingerprint: "MerchantEarlySynthetic",
            MerchantColorlessOrderedPool: cards, MerchantColorlessOrderedPoolExact: true, MerchantColorlessPoolIdentityHooksExact: true,
            MerchantColorlessRarityIdentityHooksExact: true, MerchantInitialInventoryShapeExact: true);
        var authority = new RuntimeContextAuthoritySnapshot(CharacterIdentity.FromKey(BaseGameModelKeys.Characters.Ironclad),
            0, 0, 1, true, true, true, 0, 0, "0.111.0", RuntimeProfileId.Beta111, true, null, "sts2",
            SourceAuthority.AuditedStaticExact, SnapshotCompleteness.Complete, IdentityResolutionStatus.Exact, effects);
        var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "MerchantEarlySynthetic", true, "");
        var context = new SearchContext(RuntimeProfileId.Beta111, authority.Character.CharacterKey, 0, authority, detection, SearchEvaluationAssumptions.BroadDefault);
        var compiled = new CompiledSearch(SearchQuery.Empty, context,
            new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
            new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "MerchantEarlySynthetic")), false, "MerchantEarlySynthetic");
        return new(compiled, new("000000000000", 1 << 25, int.MaxValue, 1) { Resources = new(100, 100) },
            "000000000000", 1 << 25, ExactSearchEvaluationProjection.Empty, CombatRewardRoutePolicyContract.None, "MerchantEarlySynthetic");
    }

    // Small test-only ABI1 harness. The old source is immutable and its dispatch
    // and buffers are separate from production executor objects. Only the wire
    // format is shared; target packing is built here without private reflection.
    private sealed class MerchantEagerGpu : IDisposable
    {
        private readonly RenderingDevice _rd;
        private readonly List<Rid> _owned = [];
        private readonly Rid _meta, _input, _header, _output, _pipeline, _set;
        private readonly int _capacity;
        internal MerchantEagerGpu(RenderingDevice rd, string source, ExactSearchExecutionRequest request, int capacity)
        {
            _rd = rd; _capacity = capacity;
            try
            {
                Rid Add(Rid rid) { _owned.Add(rid); return rid; }
                _meta = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 24));
                _input = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, capacity * 8));
                var authority = Beta111MerchantColorlessAuthority.From(request.Authority);
                ulong salt = XxHash64.HashUtf8("shops", 0);
                var plan = new List<uint> { (uint)request.Evaluation.MerchantColorlessConditions.Count,
                    (uint)request.Evaluation.MerchantColorlessSequenceConditions.Count, (uint)authority.UncommonPool.Count,
                    (uint)authority.RarePool.Count, (uint)authority.PlayerSlotIndex, (uint)salt, (uint)(salt >> 32) };
                var targets = new List<uint>();
                uint Id(MerchantColorlessSlot slot, ModelKey? key)
                {
                    if (key is null) return ushort.MaxValue;
                    var pool = slot == MerchantColorlessSlot.Uncommon ? authority.UncommonPool : authority.RarePool;
                    for (int i = 0; i < pool.Count; i++) if (pool[i] == key.Value) return (uint)i;
                    return ushort.MaxValue;
                }
                foreach (var condition in request.Evaluation.MerchantColorlessConditions)
                    plan.AddRange([(uint)condition.MerchantOrdinal, condition.Slot == MerchantColorlessSlot.Uncommon ? 0u : 1u, Id(condition.Slot, condition.TargetCardKey)]);
                foreach (var condition in request.Evaluation.MerchantColorlessSequenceConditions)
                {
                    plan.AddRange([condition.Slot == MerchantColorlessSlot.Uncommon ? 0u : 1u, (uint)condition.Count,
                        condition.OrderMode == CombatRewardSequenceOrderMode.Ordered ? 0u : 1u, (uint)targets.Count, (uint)condition.Slots.Count]);
                    targets.AddRange(condition.Slots.Select(key => Id(condition.Slot, key)));
                }
                Rid planBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(plan.ToArray())));
                Rid targetBuffer = Add(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytesNonEmpty(targets.ToArray())));
                _header = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, 32));
                _output = Add(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, capacity * 8));
                Rid shader = Add(FamilyGpuComputeUtility.CompileShader(rd, source, "MerchantEagerResource13Reference"));
                _pipeline = Add(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader, "MerchantEagerReference"));
                _set = Add(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, [_meta, _input, planBuffer, targetBuffer, _header, _output]));
            }
            catch (Exception failure)
            {
                FamilyGpuComputeUtility.CleanupAfterFailure(failure, () => FamilyGpuComputeUtility.FreeAll(rd, _owned), "MerchantEager.Initialize");
                throw;
            }
        }
        internal ulong[] Execute(FamilyCandidateSet input)
        {
            Require(input.Count <= _capacity, "Merchant eager test input exceeds its small buffer.");
            if (input.Count == 0) return [];
            uint[] meta = [(uint)input.Batch.BatchBase, (uint)(input.Batch.BatchBase >> 32), (uint)input.Batch.BatchCandidateCount,
                (uint)input.Count, input.IsDense ? 0u : 1u, (uint)_capacity];
            FamilyGpuComputeUtility.Update(_rd, _meta, meta, "MerchantEager.Meta");
            FamilyGpuComputeUtility.Update(_rd, _header, [0x53464D52, 1, 0, 0, 0, 0xEAE013, 0, 0], "MerchantEager.Header");
            if (!input.IsDense)
            {
                uint[] pairs = input.EnumerateLogicalOrdinals().SelectMany(x => new[] { (uint)x, (uint)(x >> 32) }).ToArray();
                FamilyGpuComputeUtility.Update(_rd, _input, pairs, "MerchantEager.Input");
            }
            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, _set, 0);
            _rd.ComputeListDispatch(list, (uint)((input.Count + 511) / 512), 1, 1);
            _rd.ComputeListEnd();
            FamilyGpuComputeUtility.SubmitAndSync(_rd, "MerchantEagerReference");
            uint[] header = FamilyGpuComputeUtility.FromUInt32Bytes(_rd.BufferGetData(_header, 0, 32), 8);
            Require(header[0] == 0x53464D52 && header[1] == 1 && header[3] == 0 && header[4] == input.Count &&
                header[5] == 0xEAE013 && header[2] <= input.Count, "Merchant eager actual header/counters invalid.");
            if (header[2] == 0) return [];
            uint[] words = FamilyGpuComputeUtility.FromUInt32Bytes(_rd.BufferGetData(_output, 0, header[2] * 8), header[2] * 2);
            ulong[] result = Enumerable.Range(0, (int)header[2]).Select(i => (ulong)words[2 * i] | ((ulong)words[2 * i + 1] << 32)).ToArray();
            Array.Sort(result);
            // Reuse the ABI validator to reject duplicates and out-of-batch data.
            return FamilyCandidateSet.FromSortedAbi1(input.Batch, result).ExportAbi1().ToArray();
        }
        public void Dispose() => FamilyGpuComputeUtility.FreeAll(_rd, _owned);
    }
}
