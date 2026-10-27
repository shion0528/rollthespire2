using System.Globalization;
using System.Text.Json;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Runtime;

namespace RolltheSpire2.Search.FamilyExecution;

public static class FamilyPhysicalImplementationRevisions
{
    // Shader/algorithm revisions remain the execution source identity used by the
    // executors. Planner performance identities below additionally name the actual
    // input path and the canonical ABI1-ready timing boundary.
    public const string MerchantShopColorlessAtomicAppendHostSort =
        "S.MerchantShopColorless.Gpu.v9.Base34Carry8.ProcessedWorkgroup.ConsumeOnly.HistoricalNextInt.TailAfterPredicates";
    public const string MerchantShopColorlessStableOrderedCompaction =
        "S.MerchantShopColorless.Gpu.v10.StableOrderedCompaction.HostPrefixScatter.Base34Carry8.ProcessedWorkgroup.ConsumeOnly.HistoricalNextInt.TailAfterPredicates";
    public const string MerchantShopColorless = MerchantShopColorlessAtomicAppendHostSort;
    public const string Relic = "R.Relic.Gpu.v7.TrackedPositions.Base34Carry8.ProcessedWorkgroup.CompactFullDecode.HistoricalNextInt";
    public const string RelicFull = RelicFullGpuPlan.AlgorithmRevision;

    public const string MerchantShopColorlessAtomicDensePerformance =
        "S.MerchantShopColorless.Atomic.DenseCarry8.CanonicalAbi1Ready.TailAfterPredicates.20261022.v2";
    public const string MerchantShopColorlessStableDensePerformance =
        "S.MerchantShopColorless.Stable.DenseCarry8.CanonicalAbi1Ready.TailAfterPredicates.20261022.v2";
    public const string MerchantShopColorlessCompactPerformance =
        "S.MerchantShopColorless.Atomic.CompactAbi1FullDecode.CanonicalAbi1Ready.TailAfterPredicates.20261022.v2";
    public const string RelicDensePerformance =
        "R.Relic.DenseCarry8.CanonicalAbi1Ready.20260904.v1";
    public const string RelicCompactPerformance =
        "R.Relic.CompactAbi1FullDecode.CanonicalAbi1Ready.20260904.v1";
}

/// <summary>
/// Fixed Family-local mapping from the resolved Query shape to relative physical
/// throughput. It owns no device performance, runtime timing, Cost or Survival.
/// </summary>
public sealed record FamilyConditionPerformanceProjection
{
    internal FamilyConditionPerformanceProjection(
        string familyId,
        string physicalImplementationRevision,
        string algorithmRevision,
        double factor,
        string evidence,
        bool usesGpu = true)
    {
        if (string.IsNullOrWhiteSpace(familyId)) throw new ArgumentException("Family id is required.", nameof(familyId));
        if (string.IsNullOrWhiteSpace(physicalImplementationRevision))
            throw new ArgumentException("Physical implementation revision is required.", nameof(physicalImplementationRevision));
        if (string.IsNullOrWhiteSpace(algorithmRevision))
            throw new ArgumentException("Condition algorithm revision is required.", nameof(algorithmRevision));
        if (!(factor > 0d) || factor > 1d || !double.IsFinite(factor))
            throw new ArgumentOutOfRangeException(nameof(factor));
        FamilyId = familyId.Trim();
        PhysicalImplementationRevision = physicalImplementationRevision.Trim();
        AlgorithmRevision = algorithmRevision.Trim();
        Factor = factor;
        Evidence = San(evidence);
        UsesGpu = usesGpu;
    }

    public string FamilyId { get; }
    public string PhysicalImplementationRevision { get; }
    public string AlgorithmRevision { get; }
    public double Factor { get; }
    public string Evidence { get; }
    public bool UsesGpu { get; }

    internal string FormatSummary() =>
        $"family={FamilyId};physicalImplementationRevision={PhysicalImplementationRevision};" +
        $"conditionAlgorithm={AlgorithmRevision};conditionFactor={F(Factor)};evidence={Evidence}";

    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string San(string value) => string.IsNullOrWhiteSpace(value)
        ? "Unspecified"
        : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>Completed Search-local physical timing facts emitted by one Family.</summary>
public sealed record FamilyPerformanceObservation(
    FamilyConditionPerformanceProjection Condition,
    string DeviceName,
    double SetupMs,
    double CurrentSearchPeakCandidatesPerSecond,
    double ValidSearchPeakCandidatesPerSecond,
    int PhysicalBatchCount,
    int ComparableSteadyBatchCount,
    long InputCandidates,
    long OutputCandidates,
    double PerformanceBoundaryMs,
    bool CalibrationEligible,
    string EligibilityEvidence);

public sealed record FamilyDeviceProfile(
    string RuntimeTarget,
    string CpuIdentity,
    string GpuIdentity,
    string RenderingBackend,
    bool GpuIdentityKnown,
    bool FamilyComputeRuntimeObserved,
    IReadOnlyList<string> CpuPaths,
    IReadOnlyList<string> FamilyGpuPaths)
{
    internal string FormatSummary() =>
        $"runtimeTarget={San(RuntimeTarget)};cpu={San(CpuIdentity)};gpu={San(GpuIdentity)};" +
        $"renderingBackend={San(RenderingBackend)};gpuIdentityKnown={GpuIdentityKnown.ToString().ToLowerInvariant()};" +
        $"familyComputeRuntimeObserved={FamilyComputeRuntimeObserved.ToString().ToLowerInvariant()};" +
        $"cpuPaths={string.Join(',', CpuPaths)};" +
        $"familyGpuPaths={string.Join(',', FamilyGpuPaths)}";

    private static string San(string value) => string.IsNullOrWhiteSpace(value)
        ? "unknown"
        : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal static class FamilyDeviceProfileFoundation
{
    internal static readonly FamilyGpuInitialization Initialization = new();
    internal static bool GpuAvailable => Initialization.Capture().GpuEligible;
    private static readonly object RetryGate = new();
    private static Task<string>? _retry;
    private static string _retryEnvironment = "";
    internal static Task<string>? RetryTask
    {
        get { lock (RetryGate) return _retryEnvironment == Initialization.Capture().EnvironmentKey ? _retry : null; }
    }

    internal static Task<string> RetryInitializationAsync()
    {
        lock (RetryGate)
        {
            if (_retry is { IsCompleted: false }) return _retry;
            _retryEnvironment = Initialization.Capture().EnvironmentKey;
            return _retry = ProbeAsync();
        }
    }

    private static async Task<string> ProbeAsync()
    {
        try
        {
            await using var owner = new FamilyGpuExecutionOwner(
                () => Godot.RenderingServer.CreateLocalRenderingDevice(), Initialization, explicitRetry: true);
            await owner.ExecuteAsync(_ => true, CancellationToken.None).ConfigureAwait(false);
            return "";
        }
        catch (Exception ex)
        {
            RuntimeLog.TryBackgroundWarning("familyGpuExplicitRetryFailed=true;failure=" + ex);
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    internal static void CaptureAvailabilityOnMainThread()
    {
        Godot.RenderingDevice? device = Godot.RenderingServer.GetRenderingDevice();
        string backend = Godot.RenderingServer.GetCurrentRenderingDriverName();
        Initialization.ObserveEnvironment($"{backend}|{device?.GetDeviceName()}|{device?.GetInstanceId()}", device is not null);
        if (device is not null)
        {
            var known = SearchPerformanceProfileFoundation.CaptureKnownDeviceIdentity();
            SearchPerformanceProfileFoundation.ObserveGpuIdentity(device.GetDeviceName(), known.RenderingBackend);
        }
    }

    internal static FamilyDeviceProfile Capture()
    {
        SearchPerformanceDeviceIdentity identity = SearchPerformanceProfileFoundation.CaptureKnownDeviceIdentity();
        return new FamilyDeviceProfile(
            identity.RuntimeTarget,
            identity.CpuIdentity,
            identity.GpuIdentity,
            identity.RenderingBackend,
            identity.HasKnownGpu,
            Initialization.Capture().State == FamilyGpuInitializationState.Available,
            ["ProductionExact.Terminal", "N.Neow.Cpu.LocalReplay.20260905.v1", "N.Neow.Cpu.IdentityPair.20260912.v1",
             "S.MerchantShopColorless.Cpu.Slot.20260912.v1", "C.CombatReward.Cpu.AuthoredPrefix.20260912.v1",
             "R.Relic.Cpu.TrackedPositions.20260912.v1", "R.Relic.Cpu.CapsuleTargets.20260912.v1",
             "R.Relic.Cpu.ShopBackPrefix.20260912.v1", "R.Relic.Cpu.CapsuleAndSequenceReplay.20260905.v1",
             "W.World.Cpu.Progression.20260907.v1", "A.AncientOption.Cpu.EventLocal.20260907.v1", "E.EventResult.Cpu.EventLocal.20260907.v1"],
            [
                $"N.Neow.Dense@{NeowFamilyGpuPlan.DenseRevision}",
                $"N.Neow.DensePreBones@{NeowFamilyGpuPlan.StagedDenseRevision}",
                $"N.Neow.LeafyPreGate.Dense@{NeowFamilyGpuPlan.LeafyDenseRevision}",
                $"N.Neow.LeafyPreGate.DensePreBones@{NeowFamilyGpuPlan.LeafyStagedDenseRevision}",
                $"N.Neow.RCapsule.Dense@{NeowCapsuleComposite.DenseRevision}",
                $"N.Neow.RCapsule.Compact@{NeowCapsuleComposite.CompactRevision}",
                $"N.Neow.RCapsule.BonesGrouped.Dense@{NeowCapsuleComposite.BonesDenseRevision}",
                $"N.Neow.RCapsule.BonesGrouped.DensePreBones@{NeowCapsuleComposite.BonesStagedRevision}",
                $"N.Neow.RCapsule.BonesGrouped.Compact@{NeowCapsuleComposite.BonesCompactRevision}",
                $"N.Neow.Compact@{NeowFamilyGpuPlan.CompactRevision}",
                $"N.Neow.LeafyPreGate.Compact@{NeowFamilyGpuPlan.LeafyCompactRevision}",
                $"S.MerchantShopColorless.Atomic.Dense@{FamilyPhysicalImplementationRevisions.MerchantShopColorlessAtomicDensePerformance}",
                $"S.MerchantShopColorless.Stable.Dense@{FamilyPhysicalImplementationRevisions.MerchantShopColorlessStableDensePerformance}",
                $"S.MerchantShopColorless.Compact@{FamilyPhysicalImplementationRevisions.MerchantShopColorlessCompactPerformance}",
                $"R.Relic.Dense@{FamilyPhysicalImplementationRevisions.RelicDensePerformance}",
                $"R.Relic.Compact@{FamilyPhysicalImplementationRevisions.RelicCompactPerformance}",
                $"R.Relic.Rfull.Dense@{RelicFullGpuPlan.DenseRevision}",
                $"R.Relic.Rfull.Compact@{RelicFullGpuPlan.CompactRevision}"
            ]);
    }

    internal static void ObserveFamilyComputeAvailable(string deviceName)
    {
        SearchPerformanceDeviceIdentity known = SearchPerformanceProfileFoundation.CaptureKnownDeviceIdentity();
        SearchPerformanceProfileFoundation.ObserveGpuIdentity(deviceName, known.RenderingBackend);
        RuntimeLog.TryBackgroundInfo(
            "familyDeviceRuntimeAvailable=true;source=LocalRenderingDevice;" + Capture().FormatSummary());
    }
}

internal static class MerchantShopColorlessConditionPerformance
{
    internal const string AlgorithmRevision = "S.ConditionPerformance.Neutral.CanonicalBoundary.20260904.v2";

    internal static FamilyConditionPerformanceProjection Project(
        ExactSearchEvaluationProjection evaluation,
        string physicalImplementationRevision = FamilyPhysicalImplementationRevisions.MerchantShopColorlessAtomicDensePerformance)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        int maximumMerchantOrdinal = evaluation.MerchantColorlessConditions
            .Select(item => item.MerchantOrdinal)
            .Concat(evaluation.MerchantColorlessSequenceConditions.Select(item => item.Count))
            .DefaultIfEmpty(0).Max();
        int ordered = evaluation.MerchantColorlessSequenceConditions.Count(item =>
            item.OrderMode == CombatRewardSequenceOrderMode.Ordered);
        int unordered = evaluation.MerchantColorlessSequenceConditions.Count(item =>
            item.OrderMode == CombatRewardSequenceOrderMode.Unordered);
        return new FamilyConditionPerformanceProjection(
            "S.MerchantShopColorless",
            physicalImplementationRevision,
            AlgorithmRevision,
            1d,
            "NeutralV2_NoAcceptedWithinPathCurve;timingBoundary=CanonicalAbi1Ready;" +
            $"auditedDimensions=MaximumMerchantOrdinal:{maximumMerchantOrdinal}|SlotPredicates:{evaluation.MerchantColorlessConditions.Count}|" +
            $"OrderedSequences:{ordered}|UnorderedSequences:{unordered}");
    }
}

internal static class RelicConditionPerformance
{
    internal const string AlgorithmRevision = "R.ConditionPerformance.Neutral.CanonicalBoundary.20260904.v2";

    internal static FamilyConditionPerformanceProjection Project(
        RelicFamilyPlan plan,
        string physicalImplementationRevision = FamilyPhysicalImplementationRevisions.RelicDensePerformance)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new FamilyConditionPerformanceProjection(
            "R.Relic",
            physicalImplementationRevision,
            AlgorithmRevision,
            1d,
            "NeutralV2_NoAcceptedWithinPathCurve;timingBoundary=CanonicalAbi1Ready;" +
            $"auditedDimensions=LastRequiredBucket:{plan.LastRequiredBucket}|TrackedCounts:{string.Join('-', plan.TrackedCountsByLane)}|" +
            $"OrdinaryPredicates:{plan.Predicates.Length}|ShopPredicates:{plan.ShopPredicates.Length}|MaxBucketLength:{plan.Pool.MaxBucketLength}");
    }
}

internal enum FamilyPerformanceEvidenceSource
{
    Unavailable,
    Reference,
    Condition,
    Global,
    Live
}

public sealed record FamilyLivePerformanceSnapshot(
    string FamilyId,
    string PhysicalImplementationRevision,
    long InputCandidates,
    double CanonicalAbi1ReadyMs,
    int LiveSteadyBatchCount)
{
    internal double CandidatesPerSecond => CanonicalAbi1ReadyMs > 0d
        ? InputCandidates * 1000d / CanonicalAbi1ReadyMs
        : 0d;
}

/// <summary>
/// Retained per-invocation diagnostics only. Durable GPU Cost samples use
/// GpuCostSamples and its explicit full-window measurement denominator.
/// </summary>
internal static class FamilyPerformanceEvidenceEligibility
{
    internal const double MinimumDurableCompactFillRatio = 0.25d;

    internal static int MinimumDurableCompactInput(int compactCapacity)
    {
        if (compactCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(compactCapacity));
        return checked((int)Math.Ceiling(compactCapacity * MinimumDurableCompactFillRatio));
    }

    internal static bool IsLiveSteadyInput(
        bool dense,
        int inputCandidates,
        int denseCapacity) =>
        dense ? inputCandidates == denseCapacity : inputCandidates > 0;

    internal static bool IsDurableComparableInput(
        bool dense,
        int inputCandidates,
        int denseCapacity,
        int compactCapacity) =>
        dense
            ? inputCandidates == denseCapacity
            : inputCandidates >= MinimumDurableCompactInput(compactCapacity);
}

internal sealed record FamilyPerformanceReference(
    string FamilyId,
    string PhysicalImplementationRevision,
    double ConditionFactor,
    double CurrentQueryReference,
    FamilyPerformanceEvidenceSource EvidenceSource,
    string DeviceName,
    string RenderingBackend,
    string Evidence)
{
    internal string FormatSummary() =>
        $"family={FamilyId};physicalImplementationRevision={PhysicalImplementationRevision};" +
        $"conditionFactor={F(ConditionFactor)};currentQueryLocalReference={F(CurrentQueryReference)};" +
        $"evidenceSource={EvidenceSource};device={San(DeviceName)};renderingBackend={San(RenderingBackend)};evidence={San(Evidence)}";

    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string San(string value) => string.IsNullOrWhiteSpace(value)
        ? "unknown"
        : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>
/// Shared Planner/ETA arithmetic over an already resolved physical-performance
/// reference. Family/ConditionPerformance owns the reference; this helper only
/// converts Reach population to steady-state time.
/// </summary>
internal static class FamilyPerformanceProjectionMath
{
    internal static double? PerInputMilliseconds(FamilyPerformanceReference performance) =>
        PerInputMilliseconds(performance.CurrentQueryReference);

    internal static double? PerInputMilliseconds(double candidatesPerSecond) =>
        candidatesPerSecond > 0d && double.IsFinite(candidatesPerSecond)
            ? 1000d / candidatesPerSecond
            : null;

    internal static double? ExpectedMilliseconds(
        double expectedInputPopulation,
        double candidatesPerSecond)
    {
        if (!(expectedInputPopulation >= 0d) || !double.IsFinite(expectedInputPopulation)) return null;
        double? perInput = PerInputMilliseconds(candidatesPerSecond);
        return perInput.HasValue ? expectedInputPopulation * perInput.Value : null;
    }
}
