using System.Globalization;
using System.Text.Json;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Predictability;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Selectivity;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.FamilyExecution;

internal enum FamilySearchEtaStatus
{
    Ready,
    Calibrating,
    Unavailable,
    Impossible
}

internal sealed record FamilyEtaStageProjection(
    string FamilyId,
    bool CompactAbi1Input,
    double InputPopulation,
    double OutputPopulation,
    double Survival,
    string PhysicalImplementationRevision,
    double? ReferenceCandidatesPerSecond,
    FamilyPerformanceEvidenceSource EvidenceSource,
    double? SteadyStateMs);

internal sealed record FamilySearchEtaProjectionV1(
    FamilySearchEtaStatus Status,
    double? AcceptedResultProbability,
    string AcceptedResultProbabilityEvidence,
    FamilyExecutionPlan Plan,
    int TargetCount,
    long ScanCount,
    long? FirstMeanRootHorizon,
    long? FirstP99RootHorizon,
    long? TargetMeanRootHorizon,
    double? FirstResultMeanMs,
    double? FirstResultP99Ms,
    double? TargetMeanMs,
    double? FixedScanRangeMs,
    double? ExpectedMatchesWithinScanRange,
    double? TargetProbabilityWithinScanRange,
    double? TargetFamilyPipelineMs,
    double? TargetExactTailMs,
    IReadOnlyList<FamilyEtaStageProjection> TargetStages,
    IReadOnlyList<string> MissingEvidence)
{
    internal double? FirstResultSearchMeanMs { get; init; }
    internal double? FirstResultSearchP99Ms { get; init; }
    internal double? TargetSearchMeanMs { get; init; }

    internal string FormatSummary()
    {
        string stages = TargetStages.Count == 0
            ? "none"
            : string.Join('|', TargetStages.Select(stage =>
                $"{stage.FamilyId}[inputMode={(stage.CompactAbi1Input ? "CompactAbi1" : "Dense")}," +
                $"input={F(stage.InputPopulation)},output={F(stage.OutputPopulation)}," +
                $"survival={F(stage.Survival)},physicalRevision={San(stage.PhysicalImplementationRevision)}," +
                $"referenceCps={Maybe(stage.ReferenceCandidatesPerSecond)},source={stage.EvidenceSource}," +
                $"steadyMs={Maybe(stage.SteadyStateMs)}]"));
        return $"familySearchEta=true;status={Status};acceptedResultProbability={Maybe(AcceptedResultProbability)};" +
               $"acceptedResultProbabilityEvidence={San(AcceptedResultProbabilityEvidence)};" +
               $"selectedPlan={San(Plan.PlanId)};selectionPolicy={San(Plan.SelectionPolicyId)};" +
               $"targetCount={TargetCount};scanCount={ScanCount};" +
               $"firstMeanRoots={Maybe(FirstMeanRootHorizon)};firstP99Roots={Maybe(FirstP99RootHorizon)};" +
               $"targetMeanRoots={Maybe(TargetMeanRootHorizon)};" +
               $"firstResultMeanMs={Maybe(FirstResultMeanMs)};firstResultP99Ms={Maybe(FirstResultP99Ms)};" +
               $"targetMeanMs={Maybe(TargetMeanMs)};fixedScanRangeMs={Maybe(FixedScanRangeMs)};" +
               $"playerFirstSearchMeanMs={Maybe(FirstResultSearchMeanMs)};playerFirstSearchP99Ms={Maybe(FirstResultSearchP99Ms)};playerTargetSearchMeanMs={Maybe(TargetSearchMeanMs)};playerGpuColdSetupIncluded=false;" +
               $"expectedMatchesWithinScanRange={Maybe(ExpectedMatchesWithinScanRange)};" +
               $"targetProbabilityWithinScanRange={Maybe(TargetProbabilityWithinScanRange)};" +
               $"targetFamilyPipelineMs={Maybe(TargetFamilyPipelineMs)};targetExactTailMs={Maybe(TargetExactTailMs)};" +
               $"targetStages={stages};missingEvidence={(MissingEvidence.Count == 0 ? "none" : string.Join('|', MissingEvidence.Select(San)))};" +
               "steadyStateComposition=max(FamilyPipeline,ExactTail)+min(FamilyPipeline,ExactTail)/ExpectedWindowCount;sameBatchFamiliesOrdered=true;" +
               $"crossBatchFamilyExactOverlap=true;startupIncluded={Plan.EstimatedSetupMilliseconds.HasValue};" +
               $"completeQuote={San(Plan.CompleteQuoteEvidence)};runtimeSurvivalLearning=false;searchAdmissionAffected=false";
    }

    private static string Maybe(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Maybe(double? value) => value.HasValue && double.IsFinite(value.Value) ? F(value.Value) : "unavailable";
    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string San(string value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>
/// Steady-state Family-world ETA. Query-wide probability owns result rarity;
/// Family Survival owns stage populations; physical references own Family time;
/// and the existing timing-only Exact store owns the CPU tail.
/// </summary>
internal static class FamilySearchEtaProjectorV1
{
    private const double FirstResultP99Confidence = 0.99d;

    private sealed record HorizonProjection(
        double? TotalMs,
        double? SearchMs,
        double? FamilyPipelineMs,
        double? ExactTailMs,
        IReadOnlyList<FamilyEtaStageProjection> Stages,
        IReadOnlyList<string> MissingEvidence);

    internal static FamilySearchEtaProjectionV1 Project(
        ExactSearchExecutionRequest request,
        FamilyExecutionPlan plan,
        IReadOnlyDictionary<string, double>? livePerformanceByPhysicalRevision = null,
        double? liveExactMsPerAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var optimizationScope = SearchOptimizationScope.Enter(request.RunOptions.Optimizations);
        var projection = ProjectCore(request, plan, livePerformanceByPhysicalRevision, liveExactMsPerAttempt);
        if (request.RunOptions.Resources?.GpuPercent is not (> 0 and < 100) ||
            !plan.Stages.Any(stage => stage.Family.ConditionPerformance.UsesGpu)) return projection;
        // Canonical GPU timings exclude the outer duty-cycle waits. A mixed
        // CPU/GPU pipeline cannot be corrected by scaling its whole duration;
        // retain probability evidence until measured wall throughput is available.
        return projection with
        {
            Status = projection.Status == FamilySearchEtaStatus.Impossible
                ? FamilySearchEtaStatus.Impossible : FamilySearchEtaStatus.Calibrating,
            FirstResultMeanMs = null,
            FirstResultP99Ms = null,
            TargetMeanMs = null,
            FixedScanRangeMs = null,
            TargetFamilyPipelineMs = null,
            TargetExactTailMs = null,
            FirstResultSearchMeanMs = null,
            FirstResultSearchP99Ms = null,
            TargetSearchMeanMs = null,
            TargetStages = projection.TargetStages.Select(stage => stage with { SteadyStateMs = null }).ToArray(),
            MissingEvidence = Distinct(projection.MissingEvidence.Append("GpuDutyCycleRequiresObservedWallRate"))
        };
    }

    private static FamilySearchEtaProjectionV1 ProjectCore(
        ExactSearchExecutionRequest request,
        FamilyExecutionPlan plan,
        IReadOnlyDictionary<string, double>? livePerformanceByPhysicalRevision,
        double? liveExactMsPerAttempt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        int targetCount = Math.Max(1, request.TargetMatchCount);
        JointSelectivityResult joint = JointSelectivityEstimator.EstimateQuery(SearchSelectivityInput.From(request));
        SearchVerificationFamily exactFamily = FamilyExactTimingDomains.Resolve(request);
        SearchExactTimingEvidence? exactTiming = SearchPredictabilityVerificationStore.TryGetExactTiming(exactFamily);

        HorizonProjection scan = ProjectHorizon(
            request.ScanCount, request, plan, exactTiming, exactFamily,
            livePerformanceByPhysicalRevision, liveExactMsPerAttempt);
        var missing = new List<string>(scan.MissingEvidence);
        if (joint.ExactlyImpossible && joint.Probability == 0d)
        {
            return Build(
                FamilySearchEtaStatus.Impossible, 0d, joint.EvidenceCode, plan, targetCount, request.ScanCount,
                null, null, null, null, null, null, scan.TotalMs, 0d, 0d,
                null, null, [], missing);
        }

        if (!joint.JointlyPriced || joint.Probability is not double probability)
        {
            missing.Add("AcceptedResultProbabilityUnavailable:" + joint.EvidenceCode);
            missing.AddRange(joint.UnknownComponents.Select(item => "QueryProbabilityComponentUnavailable:" + item));
            return Build(
                FamilySearchEtaStatus.Unavailable, null, joint.EvidenceCode, plan, targetCount, request.ScanCount,
                null, null, null, null, null, null, scan.TotalMs, null, null,
                null, null, [], missing);
        }
        if (!(probability > 0d && probability <= 1d) || !double.IsFinite(probability))
        {
            missing.Add("AcceptedResultProbabilityInvalid");
            return Build(
                FamilySearchEtaStatus.Unavailable, null, joint.EvidenceCode, plan, targetCount, request.ScanCount,
                null, null, null, null, null, null, scan.TotalMs, null, null,
                null, null, [], missing);
        }

        if (!TryCeil(SearchPredictabilityMath.ExpectedRootsToFirst(probability), out long firstMeanRoots) ||
            !TryRootsForAtLeastOneConfidence(probability, FirstResultP99Confidence, out long firstP99Roots) ||
            !TryCeil(Math.Min(request.ScanCount, SearchPredictabilityMath.ExpectedRootsToTarget(probability, targetCount)), out long targetMeanRoots))
        {
            missing.Add("QueryProbabilityRootHorizonOverflow");
            return Build(
                FamilySearchEtaStatus.Unavailable, probability, joint.EvidenceCode, plan, targetCount, request.ScanCount,
                null, null, null, null, null, null, scan.TotalMs,
                probability * request.ScanCount,
                SearchPredictabilityMath.ProbabilityAtLeastTarget(probability, request.ScanCount, targetCount),
                null, null, [], missing);
        }

        HorizonProjection firstMean = ProjectHorizon(
            firstMeanRoots, request, plan, exactTiming, exactFamily,
            livePerformanceByPhysicalRevision, liveExactMsPerAttempt);
        HorizonProjection firstP99 = ProjectHorizon(
            firstP99Roots, request, plan, exactTiming, exactFamily,
            livePerformanceByPhysicalRevision, liveExactMsPerAttempt);
        HorizonProjection target = ProjectHorizon(
            Math.Min(targetMeanRoots, request.ScanCount), request, plan, exactTiming, exactFamily,
            livePerformanceByPhysicalRevision, liveExactMsPerAttempt);
        missing.AddRange(firstMean.MissingEvidence);
        missing.AddRange(firstP99.MissingEvidence);
        missing.AddRange(target.MissingEvidence);
        string[] distinctMissing = Distinct(missing);
        bool searchReady = SearchTime(target).HasValue && SearchTime(firstMean).HasValue && SearchTime(firstP99).HasValue;
        bool startupReady = request.CompiledSearch.Context.Party is null ||
            target.TotalMs.HasValue && firstMean.TotalMs.HasValue && firstP99.TotalMs.HasValue;
        FamilySearchEtaStatus status = searchReady && startupReady
            ? FamilySearchEtaStatus.Ready
            : distinctMissing.Any(e => e.StartsWith("FamilyHardwarePerformanceUnavailable:",StringComparison.Ordinal) ||
                e.StartsWith("CompleteAllocationQuoteUnavailable:",StringComparison.Ordinal) ||
                e.StartsWith("FamilySurvivalUnavailable:",StringComparison.Ordinal) ||
                e.StartsWith("CompleteAllocationTerminalSurvivalUnavailable",StringComparison.Ordinal))
                ? FamilySearchEtaStatus.Unavailable : FamilySearchEtaStatus.Calibrating;
        return Build(
            status, probability, joint.EvidenceCode, plan, targetCount, request.ScanCount,
            firstMeanRoots, firstP99Roots, targetMeanRoots,
            firstMean.TotalMs, firstP99.TotalMs, target.TotalMs, scan.TotalMs,
            probability * request.ScanCount,
            SearchPredictabilityMath.ProbabilityAtLeastTarget(probability, request.ScanCount, targetCount),
            target.FamilyPipelineMs, target.ExactTailMs, target.Stages, distinctMissing) with
        {
            FirstResultSearchMeanMs = SearchTime(firstMean),
            FirstResultSearchP99Ms = SearchTime(firstP99),
            TargetSearchMeanMs = SearchTime(target)
        };
    }

    private static double? SearchTime(HorizonProjection horizon) => horizon.SearchMs;

    private static HorizonProjection ProjectHorizon(
        double roots,
        ExactSearchExecutionRequest request,
        FamilyExecutionPlan plan,
        SearchExactTimingEvidence? exactTiming,
        SearchVerificationFamily exactFamily,
        IReadOnlyDictionary<string, double>? livePerformanceByPhysicalRevision,
        double? liveExactMsPerAttempt)
    {
        var missing = new List<string>();
        double population = roots;
        double familyPipelineMs = 0d;
        bool familyComplete = true;
        var stages = new List<FamilyEtaStageProjection>(plan.Stages.Count);
        foreach (FamilyExecutionPlanStage stage in plan.Stages)
        {
            if (stage.Survival is not double survival)
            {
                missing.Add("FamilySurvivalUnavailable:" + stage.Family.FamilyId);
                familyComplete = false;
                break;
            }
            double output = population * survival;
            double liveReference = 0d;
            bool hasLiveReference = livePerformanceByPhysicalRevision is not null &&
                                    livePerformanceByPhysicalRevision.TryGetValue(
                                        stage.Performance.PhysicalImplementationRevision, out liveReference) &&
                                    liveReference > 0d && double.IsFinite(liveReference);
            double? reference = hasLiveReference
                ? liveReference
                : stage.Performance.CurrentQueryReference > 0d && double.IsFinite(stage.Performance.CurrentQueryReference)
                    ? stage.Performance.CurrentQueryReference
                    : null;
            double? time = reference.HasValue
                ? FamilyPerformanceProjectionMath.ExpectedMilliseconds(population, reference.Value)
                : null;
            if (!time.HasValue)
            {
                missing.Add("FamilyHardwarePerformanceUnavailable:" + stage.Family.FamilyId);
                familyComplete = false;
            }
            else
            {
                familyPipelineMs += time.Value;
            }
            stages.Add(new FamilyEtaStageProjection(
                stage.Family.FamilyId, stage.CompactAbi1Input, population, output, survival,
                stage.Performance.PhysicalImplementationRevision,
                reference,
                hasLiveReference ? FamilyPerformanceEvidenceSource.Live : stage.Performance.EvidenceSource,
                time));
            population = output;
        }

        if (plan.EstimateCanonicalMilliseconds is { } completeQuote)
        {
            double? quoted = completeQuote(roots);
            familyComplete = quoted is >= 0 && double.IsFinite(quoted.Value);
            if (familyComplete)
            {
                familyPipelineMs = quoted!.Value;
                missing.Clear(); // Standalone logical-stage prices do not describe this allocation.
            }
            else missing.Add("CompleteAllocationQuoteUnavailable:" + plan.CompleteQuoteEvidence);
            if (plan.EstimatedTerminalSurvival is double terminalSurvival && terminalSurvival is >= 0 and <= 1)
                population = roots * terminalSurvival;
            else
            {
                population = double.NaN;
                missing.Add("CompleteAllocationTerminalSurvivalUnavailable");
            }
        }

        double? exactTailMs = null;
        if (familyComplete && double.IsFinite(population))
        {
            double effectiveExactWorkers = Math.Min(Math.Max(1, request.WorkerCount), Math.Max(1, population));
            if (population <= 0d)
            {
                exactTailMs = 0d;
            }
            else if (liveExactMsPerAttempt is > 0d && double.IsFinite(liveExactMsPerAttempt.Value))
            {
                exactTailMs = population * liveExactMsPerAttempt.Value / effectiveExactWorkers;
            }
            else if (exactTiming is { Usable: true })
            {
                exactTailMs = population * exactTiming.AverageExactMsPerAttempt / effectiveExactWorkers;
            }
            else if (StandardMapExactCost.MillisecondsPerAttempt(request) is double mapExactMs)
            {
                exactTailMs = population * mapExactMs / effectiveExactWorkers;
            }
            else
            {
                missing.Add("ProductionExactHardwareEvidenceUnavailable:" + exactFamily);
            }
        }

        bool setupKnown = plan.EstimateCanonicalMilliseconds is null ||
            plan.EstimatedSetupMilliseconds is >= 0 && double.IsFinite(plan.EstimatedSetupMilliseconds.Value);
        if (!setupKnown) missing.Add("CompleteAllocationSetupUnavailable");
        double? search = null;
        if (familyComplete && exactTailMs.HasValue)
        {
            double windows = Math.Max(1, Math.Ceiling(roots /
                FamilyExecutionCoordinator.ResolveExecutionWindowSize(request, plan.OrderedFamilies)));
            // The single window must finish filtering before its Exact work can
            // start. Later windows overlap the bounded producer and consumer.
            search = Math.Max(familyPipelineMs, exactTailMs.Value) +
                Math.Min(familyPipelineMs, exactTailMs.Value) / windows;
        }
        double? total = search.HasValue && setupKnown
            ? search.Value + (plan.EstimatedSetupMilliseconds ?? 0d) : null;
        return new HorizonProjection(
            total,
            search,
            familyComplete ? familyPipelineMs : null,
            exactTailMs,
            stages,
            Distinct(missing));
    }

    private static FamilySearchEtaProjectionV1 Build(
        FamilySearchEtaStatus status,
        double? probability,
        string probabilityEvidence,
        FamilyExecutionPlan plan,
        int targetCount,
        long scanCount,
        long? firstMeanRoots,
        long? firstP99Roots,
        long? targetMeanRoots,
        double? firstMeanMs,
        double? firstP99Ms,
        double? targetMeanMs,
        double? scanMs,
        double? expectedMatchesInScan,
        double? targetProbabilityInScan,
        double? targetFamilyMs,
        double? targetExactMs,
        IReadOnlyList<FamilyEtaStageProjection> targetStages,
        IEnumerable<string> missing) => new(
            status, probability, probabilityEvidence, plan, targetCount, scanCount,
            firstMeanRoots, firstP99Roots, targetMeanRoots,
            firstMeanMs, firstP99Ms, targetMeanMs, scanMs,
            expectedMatchesInScan, targetProbabilityInScan,
            targetFamilyMs, targetExactMs, targetStages, Distinct(missing));

    private static bool TryCeil(double roots, out long value)
    {
        value = 0L;
        // long.MaxValue rounds to 2^63 as a double; that value cannot be cast
        // back to a positive long. Reject it instead of wrapping to a tiny ETA.
        if (!(roots > 0d) || !double.IsFinite(roots) || roots >= 9223372036854775808d) return false;
        value = Math.Max(1L, (long)Math.Ceiling(roots));
        return true;
    }

    private static bool TryRootsForAtLeastOneConfidence(double probability, double confidence, out long value)
    {
        value = 0L;
        if (!(probability > 0d && probability <= 1d) || !(confidence > 0d && confidence < 1d)) return false;
        if (probability >= 1d) { value = 1L; return true; }
        double roots = Math.Log(1d - confidence) / SearchPredictabilityMath.LogMissProbability(probability);
        return TryCeil(roots, out value);
    }

    private static string[] Distinct(IEnumerable<string> values) => values
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal)
        .ToArray();
}

internal static class FamilyExactTimingDomains
{
    internal static bool HasQueryWork(SearchQuery query)
    {
        // A seat and its eligibility premises alone do not measure normal Exact work.
        // Inspect the complete query so personal and map-only predicates are retained.
        return query.Players.Any(p => !p.Offers.IsEmpty || p.SelectedOption is not null ||
                p.Results.Count > 0 || HasQueryWork(p.Conditions)) ||
            JsonSerializer.Serialize(query with { Players = [] }) != JsonSerializer.Serialize(SearchQuery.Empty);
    }

    internal static SearchVerificationFamily Resolve(ExactSearchExecutionRequest request)
    {
        var family = ResolveProjectionFamily(request);
        // IncludeDiagnostics:false also avoids the request-context diagnostic
        // records and flattened per-domain arrays outside the Boss projection.
        // That CPU workload must not consume or overwrite the old full-output
        // timing evidence, including whole-party Exact envelopes.
        return request.IncludeDiagnostics ? family : WithoutDiagnostics(family);
    }

    internal static SearchVerificationFamily WithoutDiagnostics(SearchVerificationFamily family) => family switch
    {
        SearchVerificationFamily.NeowOnly => SearchVerificationFamily.NeowOnlyNoDiagnosticsV1,
        SearchVerificationFamily.RelicOnly => SearchVerificationFamily.RelicOnlyNoDiagnosticsV1,
        SearchVerificationFamily.WorldOnly => SearchVerificationFamily.WorldOnlyNoDiagnosticsV1,
        SearchVerificationFamily.CombatRewardOnly => SearchVerificationFamily.CombatRewardOnlyNoDiagnosticsV1,
        SearchVerificationFamily.MixedWithoutReward => SearchVerificationFamily.MixedWithoutRewardNoDiagnosticsV1,
        SearchVerificationFamily.MixedWithReward => SearchVerificationFamily.MixedWithRewardNoDiagnosticsV1,
        SearchVerificationFamily.MapOnly => SearchVerificationFamily.MapOnlyNoDiagnosticsV1,
        SearchVerificationFamily.MapMixedWithoutReward => SearchVerificationFamily.MapMixedWithoutRewardNoDiagnosticsV1,
        SearchVerificationFamily.MapMixedWithReward => SearchVerificationFamily.MapMixedWithRewardNoDiagnosticsV1,
        SearchVerificationFamily.Party2 => SearchVerificationFamily.Party2NoDiagnosticsV1,
        SearchVerificationFamily.Party3 => SearchVerificationFamily.Party3NoDiagnosticsV1,
        SearchVerificationFamily.Party4 => SearchVerificationFamily.Party4NoDiagnosticsV1,
        SearchVerificationFamily.Party2Map => SearchVerificationFamily.Party2MapNoDiagnosticsV1,
        SearchVerificationFamily.Party3Map => SearchVerificationFamily.Party3MapNoDiagnosticsV1,
        SearchVerificationFamily.Party4Map => SearchVerificationFamily.Party4MapNoDiagnosticsV1,
        SearchVerificationFamily.Party2Reward => SearchVerificationFamily.Party2RewardNoDiagnosticsV1,
        SearchVerificationFamily.Party3Reward => SearchVerificationFamily.Party3RewardNoDiagnosticsV1,
        SearchVerificationFamily.Party4Reward => SearchVerificationFamily.Party4RewardNoDiagnosticsV1,
        SearchVerificationFamily.Party2MapReward => SearchVerificationFamily.Party2MapRewardNoDiagnosticsV1,
        SearchVerificationFamily.Party3MapReward => SearchVerificationFamily.Party3MapRewardNoDiagnosticsV1,
        SearchVerificationFamily.Party4MapReward => SearchVerificationFamily.Party4MapRewardNoDiagnosticsV1,
        // These revisioned families already imply the non-diagnostic projection.
        SearchVerificationFamily.WorldBossOutputDemandV1 or
        SearchVerificationFamily.MixedBossOutputDemandWithoutRewardV1 or
        SearchVerificationFamily.MixedBossOutputDemandWithRewardV1 => family,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "UnclassifiedExactDiagnosticsWorkload")
    };

    private static SearchVerificationFamily ResolveProjectionFamily(ExactSearchExecutionRequest request)
    {
        if (request.CompiledSearch.Context.Party is not { } party)
        {
            // The query-scoped Exact document has a different CPU workload from
            // the historical all-World predictor. Never reuse or overwrite its
            // timing samples; Family replay calibration is unchanged.
            if (ExactWorldProjectionPolicy.Resolve(request) == Core.World.WorldPredictionProjection.BossIdentityOnly)
                return QueryDomains(request).Length == 1
                    ? SearchVerificationFamily.WorldBossOutputDemandV1
                    : request.Evaluation.RequiresNormalCombatRewardDomain
                        ? SearchVerificationFamily.MixedBossOutputDemandWithRewardV1
                        : SearchVerificationFamily.MixedBossOutputDemandWithoutRewardV1;
            if (request.CompiledSearch.NormalizedQuery.StandardMaps.Count > 0)
                return StandardMapExactCost.MapOnly(request.CompiledSearch.NormalizedQuery)
                    ? SearchVerificationFamily.MapOnly
                    : request.Evaluation.RequiresNormalCombatRewardDomain
                        ? SearchVerificationFamily.MapMixedWithReward
                        : SearchVerificationFamily.MapMixedWithoutReward;
            return SearchPredictabilityVerificationStore.ResolveFamily(QueryDomains(request));
        }
        bool map = request.CompiledSearch.NormalizedQuery.StandardMaps.Count > 0;
        bool reward = request.CompiledSearch.NormalizedQuery.Players.Any(p => p.Conditions.HasCombatRewardConstraints);
        return (party.Players.Count, map, reward) switch
        {
            (2, false, false) => SearchVerificationFamily.Party2,
            (3, false, false) => SearchVerificationFamily.Party3,
            (4, false, false) => SearchVerificationFamily.Party4,
            (2, true, false) => SearchVerificationFamily.Party2Map,
            (3, true, false) => SearchVerificationFamily.Party3Map,
            (4, true, false) => SearchVerificationFamily.Party4Map,
            (2, false, true) => SearchVerificationFamily.Party2Reward,
            (3, false, true) => SearchVerificationFamily.Party3Reward,
            (4, false, true) => SearchVerificationFamily.Party4Reward,
            (2, true, true) => SearchVerificationFamily.Party2MapReward,
            (3, true, true) => SearchVerificationFamily.Party3MapReward,
            (4, true, true) => SearchVerificationFamily.Party4MapReward,
            _ => throw new InvalidOperationException("PartyExactTimingPlayerCount")
        };
    }

    internal static ExactTimingDomain[] QueryDomains(ExactSearchExecutionRequest request)
    {
        var domains = new List<ExactTimingDomain>(4);
        if (request.Evaluation.HasNeowConstraints || request.Evaluation.TransformationAggregate is { UsesNeow: true }) domains.Add(ExactTimingDomain.Neow);
        if (request.Evaluation.RequiresRelicSequenceDomain) domains.Add(ExactTimingDomain.Relic);
        if (request.Evaluation.RequiresWorldDomain || request.Evaluation.TransformationAggregate is { UsesEvents: true }) domains.Add(ExactTimingDomain.WorldEvent);
        if (request.Evaluation.RequiresNormalCombatRewardDomain) domains.Add(ExactTimingDomain.CombatReward);
        return domains.ToArray();
    }
}
