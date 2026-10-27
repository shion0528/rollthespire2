using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Diagnostics;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Predictability;
using RolltheSpire2.Search.Runtime;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Registers applicable Filter Families, selects a complete physical allocation,
/// and starts the Search runtime. Range/cursor/result lifecycle lives in ProductionSearchSession.
/// </summary>
public static partial class FamilyExecutionCoordinator
{
    internal static FamilyExecutionPlan Plan(ExactSearchExecutionRequest plan, bool? gpuAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var optimizationScope = SearchOptimizationScope.Enter(plan.RunOptions.Optimizations);
        if (plan.RunOptions.Resources?.GpuPercent == 0) gpuAvailable = false;
        using var logScope = RuntimeLog.PlanningScope();
        var registered = CreateRegisteredFamilies(plan, gpuAvailable);
        if (plan.CompiledSearch.Context.Party is not null) return PlanParty(plan, registered);
        bool gpu = gpuAvailable ?? FamilyDeviceProfileFoundation.GpuAvailable;
        RuntimeLog.TryBackgroundInfo($"searchPhysicalAvailability=true;gpuAvailable={gpu};explicitResourceConstraint={gpuAvailable.HasValue};cpuQuotes=ReferenceTimesIndependentLocalRatio;unpricedNotUnsupported=true");
        bool explicitSelection = false;
        var baseline = gpu ? PlanRegistered(plan, registered, out explicitSelection) : FamilyPlanner.Plan(registered.SelectMany(f=>f.CpuRealizations.Take(1)).ToArray());
        if (explicitSelection) return baseline;
        var selected = AutomaticPrivateSerialPricing.SelectPhysicalAlternatives(plan, registered, baseline, gpu);
        return gpu ? NeowTransformationComposite.SelectPriced(plan, registered, selected) : selected;
    }
    private static FamilyExecutionPlan PlanRegistered(ExactSearchExecutionRequest plan, IReadOnlyList<IFamilyInvocation> registered, out bool explicitSelection)
    {
        using var logScope = RuntimeLog.PlanningScope();
        explicitSelection = true;
        if (AutomaticPrivateSerialInvocation.TrySelectExplicit(plan, registered, out var explicitSerial)) return explicitSerial!;
        FamilyExecutionPlan ordinary = FamilyPlanner.Plan(registered);
        if (BonesCapsuleAllocation.TrySelect(plan, registered, out var bonesBoundary)) return bonesBoundary!;
        if (DirectCapsuleAllocation.TrySelect(plan, registered, out var capsuleExperiment)) return capsuleExperiment!;
        if (RswPhysicalExperiment.TrySelect(plan, registered, ordinary, out var rsw)) return rsw!;
        if (NwaePhysicalExperiment.TrySelect(plan, registered, ordinary, out var nwae)) return nwae!;
        if (PrivateOrdinalSelection.TrySelect(plan, registered, ordinary, out var candidate)) return candidate!;
        if (NcPhysicalExperiment.TrySelect(plan, registered, ordinary, out var ncExperiment)) return ncExperiment!;
        if (EwPhysicalExperiment.TrySelect(plan, registered, ordinary, out var experiment)) return experiment!;
        explicitSelection = false;
        if (BonesCapsuleAllocation.TrySelectDefault(plan, registered, out var bonesDefault)) return bonesDefault!;
        if (DirectCapsuleAllocation.TrySelectPriced(plan, registered, out var directPriced)) return directPriced!;
        if (AutomaticPrivateSerialPricing.TrySelect(plan, registered, out var automaticSerial)) return automaticSerial!;
        if (PrivateSerialChainPricing.TrySelect(plan, registered, ordinary, out var chain)) return chain!;
        if (PrivateOrdinalAllocationPricing.TrySelect(plan, registered, ordinary, out var priced)) return priced!;
        if (!registered.Any(f => f is NeowFamily) || !registered.Any(f => f is CapsuleRelicFamily)) return ordinary;
        try
        {
            var n = NeowReplayPlan.Compile(plan, false);
            if (!NeowCapsuleComposite.TryCreate(plan, n, out var composite) || composite is null ||
                !NeowFamilyGpuPlan.TryCreate(n, out _, out _)) return ordinary;
            IFamilyInvocation[] fused = [new NeowFamily(plan, n, composite: composite), ..registered.Where(f => f is not NeowFamily and not CapsuleRelicFamily)];
            return FamilyPlanner.SelectEquivalentAllocations(FamilyPlanner.Plan(fused), ordinary);
        }
        catch (Exception ex)
        {
            RuntimeLog.TryBackgroundInfo("nrPhysicalUnavailable=true;baselineRetained=true;reason=" + ex.Message);
            return ordinary;
        }
    }

    public static ProductionSearchSession Start(
        ExactSearchExecutionRequest plan,
        int queueCapacity = 64,
        IRuntimePredictionDiagnosticSink? diagnosticSink = null,
        string traceSingleCandidateSeed = "",
        bool failOnPhysicalRecovery = false,
        bool? gpuAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (diagnosticSink is not null || !string.IsNullOrWhiteSpace(traceSingleCandidateSeed))
            plan = plan with { RunOptions = plan.RunOptions with { IncludeDiagnostics = true } };
        using var optimizationScope = SearchOptimizationScope.Enter(plan.RunOptions.Optimizations);
        using var logScope = RuntimeLog.SearchScope(RuntimeLog.SessionId.Length > 0 ? RuntimeLog.SessionId : Guid.NewGuid().ToString("N"));
        if (plan.RunOptions.Resources?.GpuPercent == 0) gpuAvailable = false;
        // A failed search is never silently restarted on CPU. The user can retry
        // creation explicitly, or choose the existing CPU resource constraint.
        if (gpuAvailable != false)
        {
            if (FamilyDeviceProfileFoundation.RetryTask is { IsCompleted: false })
                throw new InvalidOperationException("FamilyGpuInitializationBusy");
            if (FamilyDeviceProfileFoundation.Initialization.Capture().State == FamilyGpuInitializationState.Failed)
                throw new InvalidOperationException("FamilyGpuInitializationRetryRequired");
        }
        Infrastructure.Snapshots.ProductionSearchReplay.LogSessionWorkload(plan, RuntimeLog.SessionId);
        long started=Stopwatch.GetTimestamp();
        FamilyExecutionPlan executionPlan = Plan(plan, gpuAvailable);
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=AllocationSelected;elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F4};plan={executionPlan.PlanId};processId={Environment.ProcessId}");
        IReadOnlyList<IFamilyInvocation> families = executionPlan.OrderedFamilies;
        int executionWindowSize = ResolveExecutionWindowSize(plan, families);
        FamilySearchEtaProjectionV1 eta = FamilySearchEtaProjectorV1.Project(plan, executionPlan);
        return new ProductionSearchSession(
            plan, executionPlan, eta, executionWindowSize, queueCapacity, diagnosticSink, traceSingleCandidateSeed, failOnPhysicalRecovery);
    }

    internal static int ResolveExecutionWindowSize(ExactSearchExecutionRequest plan, IReadOnlyList<IFamilyInvocation> families)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(plan.RunOptions.Optimizations);
        if (plan.CompiledSearch.Context.Party is not null) return families.Any(f => f.ConditionPerformance.UsesGpu)
            ? FamilyPhysicalExecutionDefaults.DefaultCandidateBatchSize : FamilyCpuExecution.Capacity;
        if (families.Count > 0 && families.All(f=>f is FamilyCpuExecution)) return 65536;
        if (families.Count == 1 && families[0] is NeowFamily { SupportsDenseWindowBatching: true } neow)
            return neow.PreferredDenseWindowSize;
        MerchantShopColorlessFamily? shop = families.OfType<MerchantShopColorlessFamily>().SingleOrDefault();
        RelicFamily? relic = families.OfType<RelicFamily>().SingleOrDefault();

        int executionWindowSize = families.Count == 0
            ? Math.Min(ProductionSearchSession.ExactOnlyMaximumBatchSize, Math.Max(256, plan.WorkerCount * 128))
            : FamilyPhysicalExecutionDefaults.DefaultCandidateBatchSize;
        if (shop is not null) executionWindowSize = Math.Min(executionWindowSize, shop.PreferredExecutionWindowSize);
        if (relic is not null) executionWindowSize = Math.Min(executionWindowSize, relic.PreferredExecutionWindowSize);
        // CPU replay chunks its admitted ordinals privately. It must not shrink
        // the root batch of an upstream/downstream GPU in the same linear chain.
        if (!families.Any(family => family.ConditionPerformance.UsesGpu) &&
            families.Any(family => family is StandardMapFamily or NeowFamily or CapsuleRelicFamily or CombatRewardFamily or WorldFamily or AncientOptionFamily or EventResultFamily or TransformationAggregateFamily))
            executionWindowSize = Math.Min(executionWindowSize, FamilyCpuExecution.Capacity);
        return executionWindowSize;
    }

    internal static IReadOnlyList<IFamilyInvocation> CreateRegisteredFamilies(ExactSearchExecutionRequest plan, bool? gpuAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var optimizationScope = SearchOptimizationScope.Enter(plan.RunOptions.Optimizations);
        return plan.CompiledSearch.Context.Party is not null
            ? CreatePartyFamilies(plan, gpuAvailable ?? FamilyDeviceProfileFoundation.GpuAvailable)
            : CreateSingleplayerFamilies(plan);
    }

    // Whole-query entry only. Party child requests go through the party registrar,
    // which owns shared predicates, slot conjunction and opening continuation.
    private static IReadOnlyList<IFamilyInvocation> CreateSingleplayerFamilies(ExactSearchExecutionRequest plan)
    {
        if (plan.Authority.PlayersCount != 1 || plan.Authority.PlayerSlotIndex != 0)
            throw new InvalidOperationException("SingleplayerFamilyRegistrationRequiresSoloContext");
        long started = Stopwatch.GetTimestamp();
        var families = new List<IFamilyInvocation>(4);
        if (plan.Evaluation.TransformationAggregate is not null) families.Add(new TransformationAggregateFamily(plan));
        if (NeowFamily.TryCreate(plan, out IFamilyInvocation? neow) && neow is not null) families.Add(neow);
        if (MerchantShopColorlessFamily.TryCreate(plan, out IFamilyInvocation? shopFamily) &&
            shopFamily is MerchantShopColorlessFamily createdShop)
        {
            families.Add(createdShop);
        }
        if (NeowReplayPlan.HasCapsule(plan))
        {
            if (CapsuleRelicFamily.TryCreate(plan, out IFamilyInvocation? capsule) && capsule is not null)
                families.Add(capsule);
            // Partial R sequence coverage must not impersonate the Capsule R
            // predicate when N requests a conditional projection by Coverage.
        }
        else if (RelicFamily.TryCreate(plan, out IFamilyInvocation? relicFamily) &&
            relicFamily is RelicFamily createdRelic)
        {
            families.Add(createdRelic);
        }
        if (CombatRewardFamily.TryCreate(plan, out IFamilyInvocation? combat) && combat is not null)
            families.Add(combat);
        if (WorldFamily.TryCreate(plan, out IFamilyInvocation? world) && world is not null) families.Add(world);
        if (AncientOptionFamily.TryCreate(plan, out IFamilyInvocation? ancient) && ancient is not null) families.Add(ancient);
        if (EventResultFamily.TryCreate(plan, out IFamilyInvocation? events) && events is not null) families.Add(events);
        // Unpriced fallback order: bounded card/relic/world sieves precede full map replay.
        // This is not a fabricated calibrated map cost or a change to map semantics.
        if (plan.CompiledSearch.NormalizedQuery.StandardMaps.Count > 0) families.Add(new StandardMapFamily(plan));
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=FamilyInvocationsConstructed;elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F4};families={string.Join(',',families.Select(f=>f.FamilyId))};gpuResourcesCreated=false");
        return families;
    }
}
