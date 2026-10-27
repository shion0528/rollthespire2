using System.Reflection;
using System.Runtime.CompilerServices;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace OptimizationPolicyChecks;

internal static partial class Program
{
    private const string BoundPrefixMarker = "Check.BoundPrefixMarker";
    private const string BoundStaticDrawMarker = "Check.BoundStaticDrawMarker";
    private const string BoundInternalOrderMarker = "Check.BoundInternalOrderMarker";
    private const string BoundGroupOutputMarker = "Check.BoundGroupOutputMarker";

    private static void TestNeowSharedPrefixPhysicalIdentity()
    {
        foreach (bool boundCompact in new[] { false, true })
        {
            var family = IdentityOnlyNeowFamily(boundCompact);
            using var unrelated = SearchOptimizationScope.Enter(SearchOptimizationSettings.AllEnabled);
            var before = SearchOptimizationScope.Current;
            string sameMode = family.ResolveConditionPerformance(boundCompact).PhysicalImplementationRevision;
            string otherMode = family.ResolveConditionPerformance(!boundCompact).PhysicalImplementationRevision;
            Require(HasSharedPrefixIdentity(sameMode), "A bound N shared-prefix identity was lost in its own input shape.");
            Require(!HasSharedPrefixIdentity(otherMode),
                "A bound N shared-prefix identity leaked into the other Dense/Compact input shape.");
            Require(sameMode.Contains("." + BoundStaticDrawMarker, StringComparison.Ordinal) &&
                !otherMode.Contains("." + BoundStaticDrawMarker, StringComparison.Ordinal),
                "N static-draw physical identity escaped its bound input shape or was lost in that shape.");
            Require(sameMode.Contains("." + BoundPrefixMarker, StringComparison.Ordinal) == !boundCompact &&
                !otherMode.Contains("." + BoundPrefixMarker, StringComparison.Ordinal),
                "N Dense prefix physical identity inherited a Compact binding or was lost for its Dense binding.");
            Require(sameMode.Contains("." + BoundInternalOrderMarker, StringComparison.Ordinal) == !boundCompact &&
                !otherMode.Contains("." + BoundInternalOrderMarker, StringComparison.Ordinal),
                "N Dense filter-order identity inherited an unrelated bound input shape.");
            Require(sameMode.Contains("." + BoundGroupOutputMarker, StringComparison.Ordinal) == !boundCompact &&
                !otherMode.Contains("." + BoundGroupOutputMarker, StringComparison.Ordinal),
                "N Dense grouped-output identity inherited an unrelated bound input shape.");
            Require(SearchOptimizationScope.Current == before,
                "N physical identity resolution failed to restore its caller's unrelated policy.");

            // A live executor's observed absence is authoritative even when a
            // previous private binding recorded a cache for the same shape.
            SetIdentityFixtureField(family, "_gpu", IdentityOnlyNeowExecutor(family.GpuPlan!, null));
            Require(!HasSharedPrefixIdentity(family.ResolveConditionPerformance(boundCompact).PhysicalImplementationRevision) &&
                !HasSharedPrefixIdentity(family.ResolveConditionPerformance(!boundCompact).PhysicalImplementationRevision),
                "An actual N executor reporting no shared cache inherited a stale bound cache identity.");

            // Exercise the actual executor's shape-aware reporting as well as
            // the bound snapshot. A managed sentinel models a registered table;
            // no cache/device constructor, native call or GPU work is executed.
            SetIdentityFixtureField(family, "_gpu", IdentityOnlyNeowExecutor(family.GpuPlan!, !boundCompact));
            Require(!HasSharedPrefixIdentity(family.ResolveConditionPerformance(boundCompact).PhysicalImplementationRevision) &&
                HasSharedPrefixIdentity(family.ResolveConditionPerformance(!boundCompact).PhysicalImplementationRevision),
                "The actual N executor's shape-local cache was replaced by a different bound snapshot.");
        }
        Report(new { phase = "neowSharedPrefixPhysicalIdentity", result = "PASS",
            denseToCompactIsolated = true, compactToDenseIsolated = true,
            staticDrawPrefixOrderAndGroupShapeIsolated = true,
            observedNullOverridesBound = true, actualShapeOverridesBound = true, gpuDeviceCreated = false });
    }

    private static bool HasSharedPrefixIdentity(string revision) =>
        revision.Contains("." + SharedVisibleSeedGpuCache.Revision, StringComparison.Ordinal);

    private static NeowFamily IdentityOnlyNeowFamily(bool boundCompact)
    {
        // The normal constructor builds donor/probability plans from the live
        // game authority. Populate just the immutable inputs consumed by the
        // real public identity projection, without admitting an execution plan.
        var family = (NeowFamily)RuntimeHelpers.GetUninitializedObject(typeof(NeowFamily));
        var meta = new uint[128]; meta[6] = 1; meta[66] = 1;
        var gpuPlan = new NeowFamilyGpuPlan(meta, [], [], [], [], [], []);
        var replay = new NeowReplayPlan(null!, NeowSearchFilter.Empty, Beta110FastDomain.None,
            [], [], [], 0, 0, 0, 0, 0, 0, 255, 255, 255, false, false, false, []);
        SetIdentityFixtureField(family, "_request", Request(SearchOptimizationSettings.AllDisabled));
        SetIdentityFixtureField(family, "_plan", replay);
        SetIdentityFixtureField(family, "_gpuPlan", gpuPlan);
        SetIdentityFixtureField(family, "_boundSharedPrefixHashRevision", SharedVisibleSeedGpuCache.Revision);
        SetIdentityFixtureField(family, "_boundSharedPrefixHashCompactInput", boundCompact);
        SetIdentityFixtureField(family, "_boundPrefixHashRevision", BoundPrefixMarker);
        SetIdentityFixtureField(family, "_boundStaticDrawGateRevision", BoundStaticDrawMarker);
        SetIdentityFixtureField(family, "_boundInternalFilterOrderRevision", BoundInternalOrderMarker);
        SetIdentityFixtureField(family, "_boundInternalFilterOrderObserved", true);
        SetIdentityFixtureField(family, "_boundGroupOutputRevision", BoundGroupOutputMarker);
        SetIdentityFixtureField(family, "_boundGroupOutputObserved", true);
        return family;
    }

    private static NeowFamilyGpuExecutor IdentityOnlyNeowExecutor(NeowFamilyGpuPlan plan, bool? cachedCompact)
    {
        var executor = (NeowFamilyGpuExecutor)RuntimeHelpers.GetUninitializedObject(typeof(NeowFamilyGpuExecutor));
        SetIdentityFixtureField(executor, "_plan", plan);
        SetIdentityFixtureField(executor, "_headerReadback", new FamilyGpuHeaderReadback(null!, false));
        SetIdentityFixtureField(executor, "_stagedDispatchRevision", NeowFamilyGpuExecutor.DirectStagedRevision);
        if (cachedCompact is { } compact)
            SetIdentityFixtureField(executor, compact ? "_sharedPrefixCache" : "_sharedDensePrefixCache",
                RuntimeHelpers.GetUninitializedObject(typeof(SharedVisibleSeedGpuCache)));
        return executor;
    }

    private static void SetIdentityFixtureField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }
}
