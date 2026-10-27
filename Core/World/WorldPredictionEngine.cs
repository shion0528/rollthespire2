using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Events;
using RolltheSpire2.Core.Rng;
using RolltheSpire2.Core.World.Snapshots;
using RolltheSpire2.Core.World.Beta109;

namespace RolltheSpire2.Core.World;

/// <summary>
/// Pure copied-data Boss/Ancient vertical slice. Stable107 rules are preserved from
/// Batch A. Modern profiles use the shared direct-source-audited replay provider;
/// production Exact remains profile-gated by runtime authority and accepted real-game fixtures.
/// </summary>
public static class WorldPredictionEngine
{
    public static WorldPredictionResult Predict(
        IRuntimeProfile profile,
        string canonicalSeed,
        int ascension,
        int playerSlotIndex,
        ModelKey characterKey,
        WorldAuthoritySnapshot? world,
        NeowEffectAuthoritySnapshot? effects,
        AncientOptionConditionProfile? ancientOptionConditions = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (world is null || world.CapturedProfileId != profile.ProfileId)
        {
            return WorldPredictionResult.Unknown("WorldAuthorityMissingOrProfileMismatch");
        }

        if (RuntimeProfilePolicies.IsModernCore(profile.ProfileId))
        {
            return Beta109WorldPredictionProvider.Predict(
                canonicalSeed,
                ascension,
                playerSlotIndex,
                characterKey,
                world,
                ancientOptionConditions ?? AncientOptionConditionProfile.BroadDefault);
        }

        if (profile.ProfileId != RuntimeProfileId.Stable107)
        {
            return WorldPredictionResult.Unsupported("UnsupportedWorldProfile", "UnsupportedWorldProfile");
        }
        if (!world.HasExactLegacyFoundation)
        {
            return WorldPredictionResult.Unknown("Stable107WorldAuthorityIncomplete");
        }

        return PredictStable107(
            profile,
            canonicalSeed,
            ascension,
            playerSlotIndex,
            characterKey,
            world,
            ancientOptionConditions ?? AncientOptionConditionProfile.BroadDefault);
    }

    internal static WorldPredictionResult PredictFromRootHash(
        IRuntimeProfile profile,
        ulong rootHash,
        string seedIdentity,
        int ascension,
        int playerSlotIndex,
        ModelKey characterKey,
        WorldAuthoritySnapshot? world,
        NeowEffectAuthoritySnapshot? effects,
        AncientOptionConditionProfile? ancientOptionConditions = null,
        WorldPredictionProjection projection = WorldPredictionProjection.All)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (world is null || world.CapturedProfileId != profile.ProfileId)
        {
            return WorldPredictionResult.Unknown("WorldAuthorityMissingOrProfileMismatch");
        }
        if (!RuntimeProfilePolicies.IsModernCore(profile.ProfileId))
        {
            return WorldPredictionResult.Unsupported("RootHashWorldProfileUnsupported", "RootHashWorldProfileUnsupported");
        }

        return Beta109WorldPredictionProvider.PredictFromRootHash(
            rootHash,
            seedIdentity,
            ascension,
            playerSlotIndex,
            characterKey,
            world,
            ancientOptionConditions ?? AncientOptionConditionProfile.BroadDefault,
            projection);
    }

    private static string WorldProviderDiagnosticPrefix(RuntimeProfileId profileId) => profileId switch
    {
        RuntimeProfileId.Beta111 => "world-beta111-",
        RuntimeProfileId.Beta110 => "world-beta110-",
        _ => "world-beta109-provider-"
    };

    private static string WorldProfileIssue(RuntimeProfileId profileId, string suffix) => profileId switch
    {
        RuntimeProfileId.Beta111 => "Beta111" + suffix,
        RuntimeProfileId.Beta110 => "Beta110" + suffix,
        _ => "Beta109" + suffix
    };

    private static WorldPredictionResult PredictStable107(
        IRuntimeProfile profile,
        string canonicalSeed,
        int ascension,
        int playerSlotIndex,
        ModelKey characterKey,
        WorldAuthoritySnapshot world,
        AncientOptionConditionProfile ancientOptionConditions)
    {
        ulong root = profile.ComputeRootSeed(canonicalSeed);
        var actSelection = new Xoshiro256StarStar(profile.DeriveNamedStreamSeed(root, "act_selection"));
        var upFront = new Xoshiro256StarStar(profile.DeriveNamedStreamSeed(root, "up_front"));
        ConsumeRelicBagInitialization(world, upFront);

        IReadOnlyList<WorldActSnapshot> acts = SelectActs(world.ActGroups!, actSelection);
        Dictionary<int, List<ModelKey>> sharedAncients = AssignSharedAncients(acts, world.SharedAncients!, upFront);
        var bosses = new List<BossPredictionResult>();
        var ancients = new List<AncientPredictionResult>();
        var eventSequences = new List<EventPoolActSequenceResult>();
        string eventSequenceIssue = world.SharedAncients!.Count == 0
            ? "Stable107EmptySharedAncientPrefixUnsupported"
            : string.Empty;

        for (int index = 0; index < acts.Count; index++)
        {
            WorldActSnapshot act = acts[index];
            var ancientPool = act.Ancients.ToList();
            if (sharedAncients.TryGetValue(act.Act, out List<ModelKey>? assignedSharedAncients))
            {
                ancientPool.AddRange(assignedSharedAncients);
            }
            bool openingAncientCursorAuthorityExact = ancientPool.Count > 0;
            int eventCallsBefore = upFront.CallCount;
            ModelKey[] existingReplayEvents = act.Events.Concat(world.SharedEvents!).ToArray();
            EventPoolSequenceProjector.PreparedAct? preparedEvents = null;
            string eventIssue = string.Empty;
            if (openingAncientCursorAuthorityExact && EventPoolSequenceProjector.TryPrepare(
                    act.Act,
                    act.ActKey,
                    act.EventSequenceAuthorityExact,
                    act.OrderedRawEvents,
                    world.EventAuthority.OrderedSharedEventsRaw,
                    world.EventAuthority,
                    act.OrderedEligibleEvents,
                    out preparedEvents,
                    out eventIssue) && preparedEvents is not null &&
                preparedEvents.Candidates.Select(candidate => candidate.EventKey)
                    .SequenceEqual(existingReplayEvents, ModelKeyComparer.Instance))
            {
                upFront.UnstableShuffle(preparedEvents.Candidates);
                eventSequences.Add(EventPoolSequenceProjector.Complete(
                    RuntimeProfileId.Stable107,
                    preparedEvents,
                    EventEffectiveCandidateContext.AuthoritativeOpeningAncient(
                        EventImmutableEligibilityContext.Unknown,
                        "Stable107NormalNeowOpeningAncient"),
                    world.SourceAuthority,
                    world.Completeness,
                    eventCallsBefore,
                    upFront.CallCount,
                    "stable107.event-pool.direct-source-shuffle"));
            }
            else
            {
                if (!openingAncientCursorAuthorityExact)
                {
                    eventIssue = "MissingOpeningAncientCursorAuthority";
                }
                else if (string.IsNullOrWhiteSpace(eventIssue) && preparedEvents is not null)
                {
                    eventIssue = "Stable107EventReplayAlignmentMismatch";
                }
                eventSequenceIssue = string.IsNullOrWhiteSpace(eventSequenceIssue) ? eventIssue : eventSequenceIssue;
                var replayEvents = existingReplayEvents.ToList();
                upFront.UnstableShuffle(replayEvents);
            }

            int normalSlots = Math.Max(0, act.TotalNormalRooms - act.WeakEncounterSlots);
            var normalHistory = new List<WorldEncounterSnapshot>();
            ConsumeEncounterQueue(act.WeakEncounters, act.WeakEncounterSlots, upFront, normalHistory);
            ConsumeEncounterQueue(act.RegularEncounters, normalSlots, upFront, normalHistory);
            ConsumeEncounterQueue(act.EliteEncounters, 15, upFront, history: null);

            ModelKey boss = Next(act.Bosses, upFront);
            int firstBossCalls = upFront.CallCount;
            bosses.Add(new BossPredictionResult(
                act.Act,
                1,
                boss,
                PredictionPrecision.Exact,
                world.SourceAuthority,
                world.Completeness,
                "up_front",
                firstBossCalls,
                "batch-a.stable107.boss.up-front-source-order"));

            ModelKey ancient = Next(ancientPool, upFront);
            int ancientCalls = upFront.CallCount;
            var optionResult = Stable107AncientOptionPredictor.Predict(
                canonicalSeed,
                act.Act,
                ancient,
                characterKey,
                playerSlotIndex,
                world,
                ancientOptionConditions);
            AncientOptionPredictionResult[] options = optionResult.Options
                .Select(option => option with { OptionPrecision = optionResult.Precision })
                .ToArray();
            ancients.Add(new AncientPredictionResult(
                act.Act,
                ancient,
                PredictionPrecision.Exact,
                optionResult.Precision,
                world.SourceAuthority,
                world.Completeness,
                "up_front",
                ancientCalls,
                $"event:{ancient.Entry}:slot{(string.Equals(ancient.Entry, "DARV", StringComparison.Ordinal) ? 0 : playerSlotIndex)}",
                optionResult.RngCalls,
                options,
                "batch-a.stable107.ancient.up-front-source-order",
                optionResult.Evidence)
            {
                OptionsEvaluationStatus = optionResult.Precision switch
                {
                    PredictionPrecision.Exact when options.Length == 0 => AncientOptionsEvaluationStatus.EvaluatedKnownEmpty,
                    PredictionPrecision.Exact => AncientOptionsEvaluationStatus.EvaluatedNonEmpty,
                    PredictionPrecision.Unsupported => AncientOptionsEvaluationStatus.Unsupported,
                    _ => AncientOptionsEvaluationStatus.UnknownMissingAuthority
                },
                OptionIssueCode = optionResult.Precision is PredictionPrecision.Exact
                    ? string.Empty
                    : optionResult.Evidence.ToString()
            });

            bool finalAct = index == acts.Count - 1;
            if (finalAct && ascension >= 10)
            {
                ModelKey[] remaining = act.Bosses.Where(candidate => candidate != boss).ToArray();
                ModelKey second = Next(remaining, upFront);
                bosses.Add(new BossPredictionResult(
                    act.Act,
                    2,
                    second,
                    PredictionPrecision.Exact,
                    world.SourceAuthority,
                    world.Completeness,
                    "up_front",
                    upFront.CallCount,
                    "batch-a.stable107.boss.a10-second-boss"));
            }
        }

        EventPoolSequencePredictionResult eventPrediction = string.IsNullOrWhiteSpace(eventSequenceIssue) &&
                                                                    eventSequences.Count == acts.Count
            ? EventPoolSequenceProjector.Build(
                RuntimeProfileId.Stable107,
                eventSequences,
                world.SourceAuthority,
                world.Completeness,
                world.SnapshotFingerprint,
                "stable107.event-pool.direct-source-audit.static-effective")
            : EventPoolSequencePredictionResult.Unknown(
                RuntimeProfileId.Stable107,
                string.IsNullOrWhiteSpace(eventSequenceIssue) ? "Stable107EventPoolSequenceIncomplete" : eventSequenceIssue,
                world.SnapshotFingerprint,
                world.CatalogFingerprint);

        return new WorldPredictionResult(
            SeedDomainEvaluationStatus.Evaluated,
            SeedDomainEvaluationStatus.Evaluated,
            bosses,
            ancients,
            new[]
            {
                new PredictionDiagnostic("world-profile-rule", "stable107-donor-semantic-port"),
                new PredictionDiagnostic("world-catalog-fingerprint", world.CatalogFingerprint),
                new PredictionDiagnostic("world-snapshot-fingerprint", world.SnapshotFingerprint),
                new PredictionDiagnostic("world-act-selection-rng-calls", actSelection.CallCount.ToString()),
                new PredictionDiagnostic("world-up-front-rng-calls", upFront.CallCount.ToString())
            })
        {
            EventPoolSequencePrediction = eventPrediction
        };
    }

    private static IReadOnlyList<WorldActSnapshot> SelectActs(
        IReadOnlyList<WorldActGroupSnapshot> groups,
        Xoshiro256StarStar rng)
    {
        var output = new List<WorldActSnapshot>();
        foreach (WorldActGroupSnapshot group in groups.OrderBy(group => group.Act))
        {
            if (group.Acts.Count == 0) continue;
            output.Add(group.Acts[rng.NextInt(group.Acts.Count)]);
        }
        return output;
    }

    private static void ConsumeRelicBagInitialization(WorldAuthoritySnapshot world, Xoshiro256StarStar rng)
    {
        IReadOnlyList<NeowEffectRelicSnapshot> shared = world.SharedRelicPoolSource!;
        IReadOnlyList<NeowEffectRelicSnapshot> character = world.CharacterRelicPoolSource!;

        // Source-equivalent UpFront initialization: the shared bag is populated first,
        // then the player bag from shared.Concat(character). Do not deduplicate here;
        // the donor PureRelicGrabBag preserves source multiplicity and first-seen rarity order.
        ConsumeRelicBuckets(shared, rng, playerBag: false);
        ConsumeRelicBuckets(shared.Concat(character), rng, playerBag: true);
    }

    private static void ConsumeRelicBuckets(
        IEnumerable<NeowEffectRelicSnapshot> source,
        Xoshiro256StarStar rng,
        bool playerBag)
    {
        IEnumerable<NeowEffectRelicSnapshot> filtered = playerBag
            ? source.Where(relic => relic.Rarity is EffectRelicRarity.Common or EffectRelicRarity.Uncommon or EffectRelicRarity.Rare or EffectRelicRarity.Shop)
            : source;
        foreach (IGrouping<string, NeowEffectRelicSnapshot> bucket in filtered.GroupBy(
                     relic => string.IsNullOrWhiteSpace(relic.RarityCode) ? relic.Rarity.ToString() : relic.RarityCode,
                     StringComparer.Ordinal))
        {
            var shuffled = bucket.ToList();
            rng.UnstableShuffle(shuffled);
        }
    }

    private static Dictionary<int, List<ModelKey>> AssignSharedAncients(
        IReadOnlyList<WorldActSnapshot> acts,
        IReadOnlyList<ModelKey> source,
        Xoshiro256StarStar rng)
    {
        var output = new Dictionary<int, List<ModelKey>>();
        var remaining = source.ToList();
        if (remaining.Count == 0) return output;
        rng.UnstableShuffle(remaining);
        foreach (WorldActSnapshot act in acts)
        {
            if (act.Act <= 1) continue;
            int count = rng.NextInt(remaining.Count + 1);
            List<ModelKey> selected = remaining.Take(count).ToList();
            if (count > 0) remaining.RemoveRange(0, count);
            output[act.Act] = selected;
        }
        return output;
    }

    private static void ConsumeEncounterQueue(
        IReadOnlyList<WorldEncounterSnapshot> source,
        int slots,
        Xoshiro256StarStar rng,
        List<WorldEncounterSnapshot>? history)
    {
        if (source.Count == 0 || slots <= 0) return;

        history ??= new List<WorldEncounterSnapshot>();
        var bag = new LocalEncounterBag(source);
        for (int slot = 0; slot < slots; slot++)
        {
            if (bag.Count == 0) bag = new LocalEncounterBag(source);

            WorldEncounterSnapshot? previous = history.Count == 0 ? null : history[^1];
            WorldEncounterSnapshot? selected = bag.GrabAndRemove(
                rng,
                candidate => DoesNotRepeat(candidate, previous));
            selected ??= bag.GrabAndRemove(rng, predicate: null);
            if (selected is null) break;
            history.Add(selected);
        }
    }

    private static bool DoesNotRepeat(WorldEncounterSnapshot candidate, WorldEncounterSnapshot? previous)
    {
        if (previous is null) return true;
        if (candidate.EncounterKey == previous.EncounterKey) return false;
        if (candidate.Tags.Count == 0 || previous.Tags.Count == 0) return true;
        var previousTags = new HashSet<string>(previous.Tags, StringComparer.Ordinal);
        return !candidate.Tags.Any(previousTags.Contains);
    }

    /// <summary>
    /// Pure copy of the donor/source GrabAndRemove behavior. A singleton draw still
    /// consumes one RNG call, predicate rejection retries consume additional calls,
    /// and an impossible predicate returns null without consuming RNG.
    /// </summary>
    private sealed class LocalEncounterBag
    {
        private readonly List<WorldEncounterSnapshot> _items;

        public LocalEncounterBag(IEnumerable<WorldEncounterSnapshot> items) =>
            _items = items.ToList();

        public int Count => _items.Count;

        public WorldEncounterSnapshot? GrabAndRemove(
            Xoshiro256StarStar rng,
            Func<WorldEncounterSnapshot, bool>? predicate)
        {
            if (_items.Count == 0) return null;
            if (predicate is not null && !_items.Any(predicate)) return null;

            int guard = 0;
            while (guard++ < 10000)
            {
                int index = GrabIndex(rng);
                WorldEncounterSnapshot candidate = _items[index];
                if (predicate is null || predicate(candidate))
                {
                    _items.RemoveAt(index);
                    return candidate;
                }
            }

            int fallbackIndex = predicate is null ? -1 : _items.FindIndex(item => predicate(item));
            if (fallbackIndex < 0) fallbackIndex = GrabIndex(rng);
            WorldEncounterSnapshot selected = _items[fallbackIndex];
            _items.RemoveAt(fallbackIndex);
            return selected;
        }

        private int GrabIndex(Xoshiro256StarStar rng)
        {
            if (_items.Count <= 1)
            {
                _ = rng.NextDouble();
                return 0;
            }

            double target = rng.NextDouble() * _items.Count;
            return Math.Clamp((int)Math.Floor(target), 0, _items.Count - 1);
        }
    }

    private static ModelKey Next(IReadOnlyList<ModelKey> source, Xoshiro256StarStar rng)
    {
        if (source.Count == 0) throw new InvalidOperationException("WorldCandidatePoolEmpty");
        return source[rng.NextInt(source.Count)];
    }
}
