using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Events;
using RolltheSpire2.Core.World.Snapshots;

namespace RolltheSpire2.Core.World.Beta109;

public static class Beta109WorldPredictionProvider
{
    public static WorldPredictionResult Predict(
        string canonicalSeed,
        int ascension,
        int playerSlotIndex,
        ModelKey characterKey,
        WorldAuthoritySnapshot world,
        AncientOptionConditionProfile ancientOptionConditions)
    {
        Beta109WorldGenerationSnapshot? source = world.Beta109Generation;
        if (source is null)
        {
            return WorldPredictionResult.Unknown("ModernWorldGenerationSnapshotMissing");
        }

        Beta109WorldGenerationSnapshot snapshot = Beta109WorldSnapshotProjector.ProjectForSeed(
            source,
            canonicalSeed);
        return PredictProjected(
            snapshot,
            ascension,
            playerSlotIndex,
            characterKey,
            world,
            ancientOptionConditions);
    }

    internal static WorldPredictionResult PredictFromRootHash(
        ulong rootHash,
        string seedIdentity,
        int ascension,
        int playerSlotIndex,
        ModelKey characterKey,
        WorldAuthoritySnapshot world,
        AncientOptionConditionProfile ancientOptionConditions,
        WorldPredictionProjection projection = WorldPredictionProjection.All)
    {
        Beta109WorldGenerationSnapshot? source = world.Beta109Generation;
        if (source is null)
        {
            return WorldPredictionResult.Unknown("ModernWorldGenerationSnapshotMissing");
        }

        Beta109WorldGenerationSnapshot snapshot = Beta109WorldSnapshotProjector.ProjectForRootHash(
            source,
            rootHash,
            seedIdentity);
        return PredictProjected(
            snapshot,
            ascension,
            playerSlotIndex,
            characterKey,
            world,
            ancientOptionConditions,
            projection);
    }

    private static WorldPredictionResult PredictProjected(
        Beta109WorldGenerationSnapshot snapshot,
        int ascension,
        int playerSlotIndex,
        ModelKey characterKey,
        WorldAuthoritySnapshot world,
        AncientOptionConditionProfile ancientOptionConditions,
        WorldPredictionProjection projection = WorldPredictionProjection.All)
    {
        if (projection is not (WorldPredictionProjection.All or WorldPredictionProjection.BossIdentityOnly))
            throw new ArgumentOutOfRangeException(nameof(projection));
        bool bossOnly = projection == WorldPredictionProjection.BossIdentityOnly;
        if (!RuntimeProfilePolicies.IsModernCore(snapshot.Profile) ||
            snapshot.Ascension != ascension ||
            snapshot.CharacterKey != characterKey ||
            playerSlotIndex < 0 ||
            playerSlotIndex >= snapshot.PlayerCount)
        {
            return new WorldPredictionResult(
                SeedDomainEvaluationStatus.Unknown,
                SeedDomainEvaluationStatus.Unknown,
                Array.Empty<BossPredictionResult>(),
                Array.Empty<AncientPredictionResult>(),
                Diagnostics(snapshot, "ModernWorldRequestSnapshotMismatch"),
                "WorldRequestSnapshotMismatch",
                "WorldRequestSnapshotMismatch");
        }

        bool runtimeAuthorityExact =
            SourceAuthorityRules.SupportsExactIdentity(world.SourceAuthority) &&
            world.Completeness == SnapshotCompleteness.Complete;
        bool bossSearchReplayable =
            runtimeAuthorityExact &&
            snapshot.CanReplayBossIdentityForSearch;
        bool bossExact = runtimeAuthorityExact && snapshot.AllowsBossProductionExact;
        bool ancientIdentityExact = runtimeAuthorityExact && snapshot.AllowsAncientIdentityProductionExact;
        SourceAuthority bossAuthority = bossExact
            ? SourceAuthority.OfficialRuntimeExact
            : SourceAuthority.Incomplete;
        SnapshotCompleteness bossCompleteness = bossExact
            ? SnapshotCompleteness.Complete
            : SnapshotCompleteness.Partial;
        SourceAuthority ancientAuthority = ancientIdentityExact
            ? SourceAuthority.OfficialRuntimeExact
            : SourceAuthority.Incomplete;
        SnapshotCompleteness ancientCompleteness = ancientIdentityExact
            ? SnapshotCompleteness.Complete
            : SnapshotCompleteness.Partial;

        if ((snapshot.ModeFactsExact && snapshot.GameMode is not (WorldGameMode.Singleplayer or WorldGameMode.Multiplayer)) ||
            !snapshot.IsVanilla ||
            snapshot.OldSeedBranchStatus == Beta109OldSeedBranchStatus.UnsupportedOldSeed)
        {
            string unsupported = !snapshot.IsVanilla
                ? "UnsupportedModdedWorldGeneration"
                : snapshot.OldSeedBranchStatus == Beta109OldSeedBranchStatus.UnsupportedOldSeed
                    ? "UnsupportedOldSeed"
                    : "UnsupportedMode";
            return new WorldPredictionResult(
                SeedDomainEvaluationStatus.Unsupported,
                SeedDomainEvaluationStatus.Unsupported,
                Array.Empty<BossPredictionResult>(),
                Array.Empty<AncientPredictionResult>(),
                Diagnostics(snapshot, unsupported),
                unsupported,
                unsupported);
        }

        if (snapshot.SelectedActs.Count == 0)
        {
            string issue = DetermineSelectedActsIssue(snapshot);
            return new WorldPredictionResult(
                SeedDomainEvaluationStatus.Unknown,
                SeedDomainEvaluationStatus.Unknown,
                Array.Empty<BossPredictionResult>(),
                Array.Empty<AncientPredictionResult>(),
                Diagnostics(snapshot, issue),
                issue,
                issue);
        }
        if (!snapshot.HasExactReplayInputs)
        {
            string issue = DeterminePrecisionIssue(snapshot, snapshot.UpFrontPrefix.PriorInputsExact);
            return new WorldPredictionResult(
                SeedDomainEvaluationStatus.Unknown,
                SeedDomainEvaluationStatus.Unknown,
                Array.Empty<BossPredictionResult>(),
                Array.Empty<AncientPredictionResult>(),
                Diagnostics(snapshot, issue),
                issue,
                issue);
        }

        Beta109WorldRng upFront = CreateUpFront(snapshot, captureTrace: !bossOnly);
        bool prefixExact = snapshot.UpFrontPrefix.PriorInputsExact &&
                           snapshot.UpFrontPrefix.Kind != Beta109UpFrontPrefixAuthorityKind.Missing;
        Dictionary<int, List<ModelKey>> sharedByAct = AssignSharedAncients(
            snapshot.SelectedActs,
            snapshot.OrderedActCatalog,
            snapshot.SharedAncients,
            upFront);
        var bosses = new List<BossPredictionResult>();
        var ancients = new List<AncientPredictionResult>();
        var eventSequences = new List<EventPoolActSequenceResult>();
        var encounterSequences = new List<ActEncounterSequenceResult>();
        string eventSequenceIssue = runtimeAuthorityExact
            ? string.Empty
            : "MissingModernEventSourceAuthority";

        for (int index = 0; index < snapshot.SelectedActs.Count; index++)
        {
            ModelKey selectedActKey = snapshot.SelectedActs[index];
            Beta109ActGenerationSnapshot? act = snapshot.OrderedActCatalog
                .FirstOrDefault(candidate => candidate.ActKey == selectedActKey);
            if (act is null)
            {
                return new WorldPredictionResult(
                    SeedDomainEvaluationStatus.Unknown,
                    SeedDomainEvaluationStatus.Unknown,
                    bosses,
                    ancients,
                    Diagnostics(snapshot, "MissingRuntimeCatalog:" + selectedActKey.Serialized),
                    "MissingCatalog",
                    "MissingCatalog");
            }

            var ancientPool = act.OrderedAncients.ToList();
            if (sharedByAct.TryGetValue(act.Act, out List<ModelKey>? assignedSharedAncients))
            {
                ancientPool.AddRange(assignedSharedAncients);
            }
            bool openingAncientCursorAuthorityExact = ancientPool.Count > 0;
            int eventCallsBefore = upFront.CallCount;
            EventPoolSequenceProjector.PreparedAct? preparedEvents = null;
            string eventIssue = string.Empty;
            if (openingAncientCursorAuthorityExact && EventPoolSequenceProjector.TryPrepare(
                    act.Act,
                    act.ActKey,
                    act.EventRngConsumptionExact,
                    act.OrderedRawEvents,
                    snapshot.EventAuthority.OrderedSharedEventsRaw,
                    snapshot.EventAuthority,
                    act.OrderedEligibleEvents,
                    out preparedEvents,
                    out eventIssue) && preparedEvents is not null)
            {
                upFront.UnstableShuffle(preparedEvents.Candidates, $"act{act.Act}:event-shuffle");
                var immutableEventContext = new EventImmutableEligibilityContext(
                    snapshot.GameMode,
                    snapshot.ModeFactsExact,
                    snapshot.PlayerCount,
                    snapshot.ModeFactsExact && snapshot.IsMultiplayerExact,
                    snapshot.EventAuthority.CharacterCardPoolCount,
                    snapshot.EventAuthority.CharacterCardPoolCountExact);
                if (!bossOnly) eventSequences.Add(EventPoolSequenceProjector.Complete(
                    snapshot.Profile,
                    preparedEvents,
                    EventEffectiveCandidateContext.AuthoritativeOpeningAncient(
                        immutableEventContext,
                        "NormalNeowStandardOpeningAncient"),
                    world.SourceAuthority,
                    SnapshotCompleteness.Complete,
                    eventCallsBefore,
                    upFront.CallCount,
                    Evidence(snapshot, "event-pool.direct-source-shuffle")));
            }
            else
            {
                if (!openingAncientCursorAuthorityExact)
                {
                    eventIssue = "MissingOpeningAncientCursorAuthority";
                }
                eventSequenceIssue = string.IsNullOrWhiteSpace(eventSequenceIssue) ? eventIssue : eventSequenceIssue;
                List<ModelKey> events = act.OrderedEligibleEvents.ToList();
                upFront.UnstableShuffle(events, $"act{act.Act}:event-shuffle");
            }

            int regularSlots = Math.Max(0, act.TotalNormalRooms - act.WeakEncounterSlots);
            var normalHistory = new List<Beta109EncounterEntrySnapshot>();
            ConsumeEncounterQueue(
                act.WeakEncounters,
                act.WeakEncounterSlots,
                upFront,
                normalHistory,
                $"act{act.Act}:weak");
            ConsumeEncounterQueue(
                act.RegularEncounters,
                regularSlots,
                upFront,
                normalHistory,
                $"act{act.Act}:regular");
            var eliteHistory = new List<Beta109EncounterEntrySnapshot>();
            ConsumeEncounterQueue(
                act.EliteEncounters,
                act.EliteEncounterSlots,
                upFront,
                eliteHistory,
                $"act{act.Act}:elite");

            if (!bossOnly) encounterSequences.Add(Beta109EncounterSequenceProjection.Build(act, normalHistory, eliteHistory, snapshot, world));

            ModelKey boss = upFront.NextModelKey(act.Bosses, $"act{act.Act}:boss:first");
            if (snapshot.IsMultiplayer && snapshot.PartyBossDiscoveryOverrides.TryGetValue(act.ActKey.Serialized, out var discoveryBoss))
                boss = discoveryBoss;
            bool bossIdentityReplayable =
                bossSearchReplayable &&
                prefixExact &&
                act.HasExactGenerationInputs;
            bosses.Add(new BossPredictionResult(
                act.Act,
                1,
                boss,
                bossExact && bossIdentityReplayable
                    ? PredictionPrecision.Exact
                    : PredictionPrecision.Partial,
                bossAuthority,
                bossCompleteness,
                "up_front",
                upFront.CallCount,
                Evidence(snapshot, "direct-source.boss.up-front"))
            {
                RngTrace = upFront.Trace.ToArray(),
                AuthorityFingerprint = snapshot.SnapshotFingerprint,
                GenerationRuleFingerprint = snapshot.GenerationRuleFingerprint,
                SearchIdentityReplayable = bossIdentityReplayable,
                SearchCompatibilityIssueCode = BossSearchCompatibilityIssue(snapshot, bossExact, bossIdentityReplayable)
            });

            if (ancientPool.Count == 0)
            {
                if (bossOnly) continue;
                ancients.Add(new AncientPredictionResult(
                    act.Act,
                    default,
                    PredictionPrecision.Unknown,
                    PredictionPrecision.Unknown,
                    SourceAuthority.Incomplete,
                    SnapshotCompleteness.Partial,
                    "up_front",
                    upFront.CallCount,
                    "event-local",
                    0,
                    Array.Empty<AncientOptionPredictionResult>(),
                    Evidence(snapshot, "ancient.identity-pool-empty"),
                    Evidence(snapshot, "ancient.options-not-generated"))
                {
                    IdentityRngTrace = upFront.Trace.ToArray(),
                    AuthorityFingerprint = snapshot.SnapshotFingerprint,
                    GenerationRuleFingerprint = snapshot.GenerationRuleFingerprint,
                    OptionsEvaluationStatus = AncientOptionsEvaluationStatus.UnknownMissingAuthority,
                    OptionIssueCode = "AncientIdentityPoolEmpty"
                });
                continue;
            }

            ModelKey ancient = upFront.NextModelKey(ancientPool, $"act{act.Act}:ancient:identity");
            // The identity draw belongs to up_front and is mandatory even when
            // no Ancient output was requested. Options use an independent
            // event-local RNG and cannot affect any later boss draw.
            if (bossOnly) continue;
            Beta109AncientOptionPrediction optionPrediction = Beta109AncientOptionProvider.Predict(
                snapshot,
                act.Act,
                ancient,
                playerSlotIndex,
                characterKey,
                runtimeAuthorityExact: ancientIdentityExact,
                conditions: ancientOptionConditions);
            PredictionPrecision ancientIdentityPrecision = ancientIdentityExact && prefixExact && act.HasExactGenerationInputs
                ? PredictionPrecision.Exact
                : PredictionPrecision.Partial;
            ancients.Add(new AncientPredictionResult(
                act.Act,
                ancient,
                ancientIdentityPrecision,
                optionPrediction.Precision,
                ancientAuthority,
                ancientCompleteness,
                "up_front",
                upFront.CallCount,
                "event-local",
                optionPrediction.RngCallCount,
                optionPrediction.Options,
                Evidence(snapshot, "direct-source.ancient.identity.up-front"),
                optionPrediction.EvidenceCode)
            {
                IdentityRngTrace = upFront.Trace.ToArray(),
                OptionRngTrace = optionPrediction.Trace,
                AuthorityFingerprint = snapshot.SnapshotFingerprint,
                GenerationRuleFingerprint = snapshot.GenerationRuleFingerprint,
                OptionsEvaluationStatus = optionPrediction.EvaluationStatus,
                OptionIssueCode = optionPrediction.IssueCode
            });
        }

        if (snapshot.SelectedActs.Count > 0 && ascension >= 10)
        {
            ModelKey finalActKey = snapshot.SelectedActs[^1];
            Beta109ActGenerationSnapshot? finalAct = snapshot.OrderedActCatalog
                .FirstOrDefault(candidate => candidate.ActKey == finalActKey);
            BossPredictionResult? first = bosses.LastOrDefault(candidate => candidate.Act == finalAct?.Act && candidate.Ordinal == 1);
            if (finalAct is not null && first is not null)
            {
                ModelKey[] secondPool = finalAct.Bosses.Where(candidate => candidate != first.BossKey).ToArray();
                if (secondPool.Length > 0)
                {
                    ModelKey second = upFront.NextModelKey(secondPool, $"act{finalAct.Act}:boss:second-a10");
                    bool secondBossIdentityReplayable =
                        bossSearchReplayable &&
                        prefixExact &&
                        finalAct.HasExactGenerationInputs;
                    bosses.Add(new BossPredictionResult(
                        finalAct.Act,
                        2,
                        second,
                        bossExact && secondBossIdentityReplayable
                            ? PredictionPrecision.Exact
                            : PredictionPrecision.Partial,
                        bossAuthority,
                        bossCompleteness,
                        "up_front",
                        upFront.CallCount,
                        Evidence(snapshot, "direct-source.boss.a10-second-excludes-first"))
                    {
                        RngTrace = upFront.Trace.ToArray(),
                        AuthorityFingerprint = snapshot.SnapshotFingerprint,
                        GenerationRuleFingerprint = snapshot.GenerationRuleFingerprint,
                        SearchIdentityReplayable = secondBossIdentityReplayable,
                        SearchCompatibilityIssueCode = BossSearchCompatibilityIssue(snapshot, bossExact, secondBossIdentityReplayable)
                    });
                }
            }
        }

        string commonIssue = DeterminePrecisionIssue(snapshot, prefixExact);
        string bossIssue = bossExact ? string.Empty :
            bossSearchReplayable
                ? BossSearchCompatibilityIssue(snapshot, bossExact: false, replayable: true)
                : snapshot.HasExactReplayInputs && snapshot.TutorialBossOverrideWillApply
                    ? "UnsupportedTutorialBossOverride"
                    : commonIssue;
        string ancientIssue = ancientIdentityExact ? string.Empty :
            snapshot.HasExactReplayInputs && !snapshot.AllowsAncientIdentityProductionExact
                ? "PendingRealGameAncientIdentityFixture"
                : commonIssue;
        EventPoolSequencePredictionResult? eventPrediction = bossOnly ? null : runtimeAuthorityExact &&
                                                                    string.IsNullOrWhiteSpace(eventSequenceIssue) &&
                                                                    eventSequences.Count == snapshot.SelectedActs.Count
            ? EventPoolSequenceProjector.Build(
                snapshot.Profile,
                eventSequences,
                world.SourceAuthority,
                SnapshotCompleteness.Complete,
                snapshot.SnapshotFingerprint,
                Evidence(snapshot, "event-pool.raw-order.static-candidate-ordinal-exact"))
            : EventPoolSequencePredictionResult.Unknown(
                snapshot.Profile,
                string.IsNullOrWhiteSpace(eventSequenceIssue) ? "ModernEventPoolSequenceIncomplete" : eventSequenceIssue,
                snapshot.SnapshotFingerprint,
                snapshot.CatalogFingerprint);

        return new WorldPredictionResult(
            bosses.Count > 0 ? SeedDomainEvaluationStatus.Evaluated : SeedDomainEvaluationStatus.Unknown,
            ancients.Count > 0 ? SeedDomainEvaluationStatus.Evaluated : SeedDomainEvaluationStatus.Unknown,
            bosses,
            ancients,
            bossOnly ? Array.Empty<PredictionDiagnostic>() : Diagnostics(snapshot, string.Join("|", new[] { bossIssue, ancientIssue }
                .Where(issue => !string.IsNullOrWhiteSpace(issue))
                .Distinct(StringComparer.Ordinal))),
            bosses.Count > 0 ? bossIssue : "ModernBossPredictionUnavailable",
            ancients.Count > 0 ? ancientIssue : "ModernAncientPredictionUnavailable")
        {
            EventPoolSequencePrediction = eventPrediction,
            EncounterSequences = encounterSequences
        };
    }

    private static string BossSearchCompatibilityIssue(
        Beta109WorldGenerationSnapshot snapshot,
        bool bossExact,
        bool replayable)
    {
        if (bossExact || !replayable)
        {
            return string.Empty;
        }

        var assumptions = new List<string>();
        if (!snapshot.TutorialBossOverrideAuthorityExact)
        {
            assumptions.Add("AssumedNormalFlowNoTutorialBossOverride");
        }
        if (!snapshot.HasVerifiedBossVersionFixture)
        {
            assumptions.Add("BossVersionFixtureUnverified");
        }
        if (snapshot.FixtureValidationStatus == Beta109FixtureValidationStatus.AssumedCompatibleLatestKnown)
        {
            assumptions.Add("AcceptedCompatibleLatestKnown");
        }
        return assumptions.Count == 0
            ? "BossSearchCompatibilityProjection"
            : string.Join("+", assumptions);
    }

    private static Beta109WorldRng CreateUpFront(Beta109WorldGenerationSnapshot snapshot, bool captureTrace = true)
    {
        if (snapshot.UpFrontPrefix.Kind == Beta109UpFrontPrefixAuthorityKind.ExactCheckpoint &&
            snapshot.UpFrontPrefix.Checkpoint is not null)
        {
            return Beta109WorldRng.FromCheckpoint(snapshot.UpFrontPrefix.Checkpoint, "up_front", captureTrace);
        }

        Beta109WorldRng rng = Beta109WorldRng.CreateNamed(snapshot.RunSeedRoot, "up_front", captureTrace);
        foreach (Beta109RelicBucketSnapshot bucket in snapshot.SharedRelicBuckets)
        {
            if (!captureTrace)
            {
                rng.ConsumeUnobservedShuffle(bucket.OrderedRelics.Count);
                continue;
            }
            List<ModelKey> items = bucket.OrderedRelics.ToList();
            rng.UnstableShuffle(items, "initialize:shared-relic-bag:" + bucket.BucketId);
        }
        foreach (Beta109RelicBucketSnapshot bucket in snapshot.IsMultiplayer
            ? snapshot.PartyRelicBuckets.SelectMany(buckets => buckets)
            : snapshot.PlayerRelicBuckets)
        {
            if (!captureTrace)
            {
                rng.ConsumeUnobservedShuffle(bucket.OrderedRelics.Count);
                continue;
            }
            List<ModelKey> items = bucket.OrderedRelics.ToList();
            rng.UnstableShuffle(items, "initialize:player-relic-bag:" + bucket.BucketId);
        }
        return rng;
    }

    private static Dictionary<int, List<ModelKey>> AssignSharedAncients(
        IReadOnlyList<ModelKey> selectedActKeys,
        IReadOnlyList<Beta109ActGenerationSnapshot> catalog,
        IReadOnlyList<ModelKey> source,
        Beta109WorldRng rng)
    {
        var output = new Dictionary<int, List<ModelKey>>();
        var remaining = source.ToList();
        rng.UnstableShuffle(remaining, "shared-ancient:shuffle");
        for (int index = 1; index < selectedActKeys.Count; index++)
        {
            Beta109ActGenerationSnapshot? act = catalog.FirstOrDefault(item => item.ActKey == selectedActKeys[index]);
            if (act is null) continue;
            int count = rng.NextInt(remaining.Count + 1, $"shared-ancient:act{act.Act}:prefix-count");
            List<ModelKey> selected = remaining.Take(count).ToList();
            if (count > 0) remaining.RemoveRange(0, count);
            output[act.Act] = selected;
        }
        return output;
    }

    private static void ConsumeEncounterQueue(
        IReadOnlyList<Beta109EncounterEntrySnapshot> source,
        int slots,
        Beta109WorldRng rng,
        List<Beta109EncounterEntrySnapshot>? history,
        string stage)
    {
        if (slots <= 0) return;
        history ??= new List<Beta109EncounterEntrySnapshot>();
        var bag = new Beta109EncounterGrabBag(source);
        for (int slot = 0; slot < slots; slot++)
        {
            if (bag.Count == 0 && source.Count > 0)
            {
                bag = new Beta109EncounterGrabBag(source);
            }

            Beta109EncounterEntrySnapshot? previous = history.Count == 0 ? null : history[^1];
            Beta109EncounterGrabResult predicateResult = bag.GrabAndRemove(
                rng,
                candidate => Beta109EncounterReplayRules.DoesNotRepeat(candidate, previous),
                $"{stage}:slot{slot}:predicate",
                WorldRngConsumptionShape.PredicateAttempt);

            Beta109EncounterEntrySnapshot? selected = predicateResult.Item;
            if (selected is null)
            {
                Beta109EncounterGrabResult fallbackResult = bag.GrabAndRemove(
                    rng,
                    predicate: null,
                    $"{stage}:slot{slot}:fallback",
                    WorldRngConsumptionShape.Fallback);
                selected = fallbackResult.Item;
            }

            // ActModel keeps executing the fixed slot loop even when an empty
            // source bag produces no encounter. The no-predicate fallback has
            // already consumed the source-equivalent NextDouble call, so moving
            // to the next slot preserves the real cursor shape.
            if (selected is null) continue;
            history.Add(selected);
        }
    }

    private static IReadOnlyList<PredictionDiagnostic> Diagnostics(
        Beta109WorldGenerationSnapshot snapshot,
        string issue)
    {
        string prefix = snapshot.Profile switch
        {
            RuntimeProfileId.Beta111 => "world-beta111-",
            RuntimeProfileId.Beta110 => "world-beta110-",
            _ => "world-beta109-historical-donor-"
        };
        return new[]
        {
            new PredictionDiagnostic(prefix + "source-audit", RuntimeProfilePolicies.AuditFingerprint(snapshot.Profile)),
            new PredictionDiagnostic(prefix + "snapshot", snapshot.SnapshotFingerprint),
            new PredictionDiagnostic(prefix + "catalog", snapshot.CatalogFingerprint),
            new PredictionDiagnostic(prefix + "unlock", snapshot.UnlockFingerprint),
            new PredictionDiagnostic(prefix + "generation-rules", snapshot.GenerationRuleFingerprint),
            new PredictionDiagnostic(prefix + "capture", snapshot.CaptureDiagnosticCode),
            new PredictionDiagnostic(prefix + "selected-acts", string.Join(",", snapshot.SelectedActs.Select(key => key.Serialized))),
            new PredictionDiagnostic(prefix + "act-selection-root", snapshot.ActSelectionRootExact ? snapshot.ActSelectionRoot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing"),
            new PredictionDiagnostic(prefix + "run-root", snapshot.RunSeedRootExact ? snapshot.RunSeedRoot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing"),
            new PredictionDiagnostic(prefix + "fixture-status", snapshot.FixtureValidationStatus.ToString()),
            new PredictionDiagnostic(prefix + "fixture-ids", string.Join(",", snapshot.VerifiedRealGameFixtureIds)),
            new PredictionDiagnostic(prefix + "precision-issue", issue)
        };
    }

    private static string Evidence(Beta109WorldGenerationSnapshot snapshot, string suffix) => snapshot.Profile switch
    {
        RuntimeProfileId.Beta111 => "beta111." + suffix,
        RuntimeProfileId.Beta110 => "beta110." + suffix,
        _ => "beta109-historical-donor." + suffix
    };

    private static string ProfileIssue(Beta109WorldGenerationSnapshot snapshot, string suffix) => snapshot.Profile switch
    {
        RuntimeProfileId.Beta111 => "Beta111" + suffix,
        RuntimeProfileId.Beta110 => "Beta110" + suffix,
        _ => "Beta109HistoricalDonor" + suffix
    };

    private static string DetermineSelectedActsIssue(Beta109WorldGenerationSnapshot snapshot)
    {
        if (!snapshot.ActSelectionRootExact) return "MissingActSelectionRoot";
        if (!snapshot.ActSelectionAuthorityExact)
        {
            string[] priority =
            {
                "MissingActCatalog",
                "MissingActsByIndexOrder",
                "MissingUnlockState",
                "MissingDiscoveredActs",
                "MissingMultiplayerMode",
                "MissingTestMode",
                "MissingAct1Override",
                "MissingLobbyPlayerAuthority"
            };
            foreach (string code in priority)
            {
                if (snapshot.CaptureDiagnosticCode.Contains(code, StringComparison.Ordinal)) return code;
            }
            if (snapshot.CaptureDiagnosticCode.Contains("ActSelectionGroupIncomplete", StringComparison.Ordinal))
                return "ActSelectionGroupIncomplete";
            return "MissingActSelectionAuthority";
        }
        if (!snapshot.CanReconstructSelectedActs) return "MissingActSelectionReplayInputs";
        return "MissingSelectedActs";
    }

    private static string DeterminePrecisionIssue(
        Beta109WorldGenerationSnapshot snapshot,
        bool prefixExact)
    {
        if (!RuntimeProfilePolicies.IsModernCore(snapshot.Profile)) return "WorldProfileMismatch";
        if (!snapshot.DirectSourceAudited) return "DirectSourceAuditMissing";
        if (!snapshot.ActSelectionRootExact) return "MissingActSelectionRoot";
        if (!snapshot.RunSeedRootExact ||
            snapshot.RunSeedHashKind != Beta109RunSeedHashKind.ModernXxHash64 ||
            snapshot.OldSeedBranchStatus != Beta109OldSeedBranchStatus.NewSeedHashed)
            return snapshot.OldSeedBranchStatus == Beta109OldSeedBranchStatus.UnsupportedOldSeed
                ? "UnsupportedOldSeedRunRoot"
                : "MissingRunSeedRoot";
        if (!snapshot.ActSelectionAuthorityExact) return "MissingActSelectionAuthority";
        if (!snapshot.SelectedActsExact || snapshot.SelectedActs.Count == 0) return "MissingSelectedActs";
        if (!snapshot.IsMultiplayerExact) return "MissingMultiplayerMode";
        if (!snapshot.TestModeFactExact) return "MissingTestMode";
        if (!snapshot.Act1OverrideExact) return "MissingAct1Override";
        if (!snapshot.ModeFactsExact) return "MissingModeFacts";
        if (!(snapshot.GameMode == WorldGameMode.Singleplayer && !snapshot.IsMultiplayer && snapshot.PlayerCount == 1) &&
            !snapshot.HasExactFixedParty) return "MissingOrderedPartyWorldAuthority";
        if (snapshot.LobbyPlayers.Count != snapshot.PlayerCount || snapshot.LobbyPlayers.Any(player => !player.Exact))
            return "MissingLobbyPlayerAuthority";
        if (snapshot.LobbyPlayers.Any(player => player.IsRandomCharacter))
            return "UnsupportedRandomCharacterWorldReplay";
        if (!snapshot.EventAuthority.SharedRawOrderExact) return "MissingSharedEventCatalog";
        if (!snapshot.EventAuthority.EpochMembershipExact) return "MissingEventEpochMembership";
        if (!snapshot.EventAuthority.EpochRevealFactsExact) return "MissingEventEpochAuthority";
        if (!snapshot.SharedEventCatalogExact) return "MissingEventCatalog";
        if (!snapshot.AllSharedAncientCatalogExact) return "MissingAllSharedAncientCatalog";
        if (!snapshot.SharedAncientCatalogExact) return "MissingUnlockedSharedAncientCatalog";
        if (snapshot.OrderedActCatalog.Count == 0) return "MissingActCatalog";
        foreach (ModelKey selectedAct in snapshot.SelectedActs)
        {
            Beta109ActGenerationSnapshot? act = snapshot.OrderedActCatalog
                .FirstOrDefault(candidate => candidate.ActKey == selectedAct);
            if (act is null) return "MissingActCatalog";
            if (!act.RawEventCatalogOrderExact) return "MissingActEventCatalogOrder";
            if (!act.EventEpochMembershipExact) return "MissingEventEpochMembership";
            if (!act.EventEpochRevealFactsExact) return "MissingEventEpochAuthority";
            if (!act.EligibleEventOrderExact) return "MissingEventEligibleOrder";
            if (!act.EncounterCatalogOfficialVanilla) return "UnsupportedModEncounter";
            if (act.OrderedGenerateAllEncounters is null) return "MissingEncounterCatalog";
            if (string.Equals(
                    act.EncounterAuthorityEvidenceCode,
                    "VanillaEncounterSourceShapeMismatch",
                    StringComparison.Ordinal))
                return "EncounterCatalogShapeMismatch";
            if (!act.GenerateAllEncountersOrderExact) return "MissingGenerateAllEncountersOrder";
            if (!act.EncounterClassificationExact) return "MissingEncounterClassificationAuthority";
            if (!act.EncounterTagIdentityExact) return "MissingEncounterTagAuthority";
            if (!act.EncounterTagComparerExact) return "MissingEncounterTagComparer";
            if (!act.EncounterReferenceIdentityExact) return "MissingEncounterReferenceIdentity";
            if (!act.EncounterWeightModelExact) return "MissingEncounterWeightAuthority";
            if (!act.EncounterRetryShapeExact) return "MissingEncounterRetryCallShape";
            if (!act.RoomShapeExact) return "MissingRoomShapeAuthority";
            if (!act.UnlockFilteringExact) return "MissingEventOrAncientUnlockFacts";
            if (!act.CatalogOrderExact) return "MissingCatalog";
        }
        if (!snapshot.UnlockFactsExact) return "MissingUnlockAuthority";
        if (!snapshot.SharedRelicPoolOrderExact) return "MissingSharedRelicPoolOrder";
        if (!snapshot.CharacterRelicPoolOrderExact) return "MissingCharacterRelicPoolOrder";
        if (!snapshot.RelicRarityAuthorityExact) return "MissingRelicRarityAuthority";
        if (!snapshot.PlayerRelicPoolCompositionExact) return "MissingPlayerRelicPoolComposition";
        if (!snapshot.RelicInitializationExact) return "MissingRelicInitialization";
        if (!prefixExact || !snapshot.HasExactUpFrontPrefix)
            return snapshot.UpFrontPrefix.EvidenceCode is { Length: > 0 }
                ? snapshot.UpFrontPrefix.EvidenceCode
                : "MissingPriorUpFront";
        if (!snapshot.WorldGenerationHooksExact) return "UnknownWorldGenerationHookOrModifier";
        if (snapshot.FixtureValidationStatus is not (
                Beta109FixtureValidationStatus.VerifiedRealGame or
                Beta109FixtureValidationStatus.AssumedCompatibleLatestKnown) ||
            snapshot.VerifiedRealGameFixtureIds.Count == 0)
            return "PendingRealGameFixture";
        return ProfileIssue(snapshot, "AuthorityPartial");
    }

}
