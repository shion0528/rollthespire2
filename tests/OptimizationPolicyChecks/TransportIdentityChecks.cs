using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace OptimizationPolicyChecks;

internal static partial class Program
{
    private static void TestHeaderTransportIdentity()
    {
        var gpu = new FamilyConditionPerformanceProjection("Check.Header", "OriginalGpuRevision", "Check.v1", 1, "PureIdentityTest");
        var cpu = new FamilyConditionPerformanceProjection("Check.Header", "OriginalCpuRevision", "Check.v1", 1,
            "PureIdentityTest", usesGpu: false);
        var disabled = new FamilyHeaderTransportPerformance(SearchOptimizationSettings.AllDisabled);
        FamilyHeaderTransportPerformance enabled;
        string enabledRequested;
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled))
        {
            enabledRequested = FamilyGpuHeaderReadback.RequestedRevision;
            enabled = new(SearchOptimizationSettings.AllEnabled);
        }
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled))
        {
            var identity = disabled.Apply(gpu);
            Require(identity.PhysicalImplementationRevision.EndsWith("." + FamilyHeaderTransportPerformance.Revision + "." +
                    FamilyGpuHeaderReadback.SynchronousRevision, StringComparison.Ordinal),
                "An all-disabled request borrowed the caller's queued header identity.");
            Require(identity.PhysicalImplementationRevision != gpu.PhysicalImplementationRevision &&
                ReferenceEquals(disabled.Apply(cpu), cpu), "The new GPU transport reused an old key or changed a CPU key.");
        }
        using (SearchOptimizationScope.Enter(SearchOptimizationSettings.AllDisabled))
        {
            Require(enabled.RevisionFor() == enabledRequested,
                "A requested header identity drifted with a later unrelated scope.");
            string observed = enabled.Apply(gpu, FamilyGpuHeaderReadback.FallbackRevision).PhysicalImplementationRevision;
            Require(observed.EndsWith("." + FamilyGpuHeaderReadback.FallbackRevision, StringComparison.Ordinal) &&
                enabled.Apply(gpu).PhysicalImplementationRevision == observed,
                "The observed registration fallback was lost after leaving its executor.");
        }
        Report(new { phase = "headerTransportIdentity", result = "PASS", requestedCaptured = true,
            actualFallbackRetained = true, gpuAtlasRevisionSeparated = true, cpuUnchanged = true });
    }

    private static void TestCpuQuoteScope()
    {
        foreach (var expected in new[] { SearchOptimizationSettings.AllEnabled, SearchOptimizationSettings.AllDisabled })
        {
            var request = Request(expected);
            var owner = new PolicyFamily(expected, _ => Task.CompletedTask);
            int quoted = 0;
            int workers = Math.Min(request.WorkerCount,
                request.RunOptions.Resources!.ResolveCpuWorkers(Environment.ProcessorCount));
            IFamilyInvocation cpu = new FamilyCpuExecution(request, owner, "Check.Quote.Cpu.v1", _ => true, workers, _ =>
            {
                Require(SearchOptimizationScope.Current == expected,
                    "A CPU realization's owner quote used its caller's unrelated optimization scope.");
                quoted++;
                return new(owner.FamilyId, "ScopeQuote", 1, 8192, null, "PureOwnerDelegateProbe");
            });
            using var unrelated = SearchOptimizationScope.Enter(expected == SearchOptimizationSettings.AllEnabled
                ? SearchOptimizationSettings.AllDisabled : SearchOptimizationSettings.AllEnabled);
            var before = SearchOptimizationScope.Current;
            string identity = cpu.ConditionPerformance.PhysicalImplementationRevision;
            Require(cpu.QuotePhysicalWork(new(false, false, 64)) is not null && quoted == 1 &&
                SearchOptimizationScope.Current == before && cpu.ConditionPerformance.PhysicalImplementationRevision == identity,
                "CPU owner quote failed to restore its caller or changed its captured physical identity.");
        }
        Report(new { phase = "cpuOwnerQuoteScope", result = "PASS", explicitRequestWins = true, callerRestored = true });
    }

    private static void TestPlanIdentityCapture()
    {
        foreach (var expected in new[] { SearchOptimizationSettings.AllEnabled, SearchOptimizationSettings.AllDisabled })
        {
            CombatRewardGpuPlan plan;
            string identity;
            using (SearchOptimizationScope.Enter(expected))
            {
                plan = new([], [], [], [], [], Beta110GpuCombatRewardHotLoopPlan.Disabled("PolicyIdentityOnly"), 1);
                identity = plan.Revision(false);
            }
            using var unrelated = SearchOptimizationScope.Enter(expected == SearchOptimizationSettings.AllEnabled
                ? SearchOptimizationSettings.AllDisabled : SearchOptimizationSettings.AllEnabled);
            Require(plan.Revision(false) == identity && (plan with { Routes = 2 }).Revision(false) == identity,
                "An immutable combat plan's prefix identity changed after its creating scope ended.");
        }
        Report(new { phase = "immutablePlanIdentity", result = "PASS", combatPlanCapturedPrefix = true,
            recordClonePreservesPhysicalPolicy = true, noDeviceCreated = true });
    }
}
