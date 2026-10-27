using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

// Physical timing identity only. Captures the request before device creation;
// an observed private/public executor revision replaces the request, including
// registration fallback. No process-global setting is read by later snapshots.
internal sealed class FamilyHeaderTransportPerformance
{
    internal const string Revision = "HeaderTransport.20261022.v1";
    private readonly string _requestedRevision;
    private string? _observedRevision;

    internal FamilyHeaderTransportPerformance(SearchOptimizationSettings settings)
    {
        using var scope = SearchOptimizationScope.Enter(settings);
        _requestedRevision = FamilyGpuHeaderReadback.RequestedRevision;
    }

    internal string RevisionFor(string? actualRevision = null)
    {
        if (actualRevision is not null) Volatile.Write(ref _observedRevision, actualRevision);
        return Volatile.Read(ref _observedRevision) ?? _requestedRevision;
    }

    internal FamilyConditionPerformanceProjection Apply(FamilyConditionPerformanceProjection condition,
        string? actualRevision = null) => !condition.UsesGpu ? condition :
        new(condition.FamilyId, condition.PhysicalImplementationRevision + "." + Revision + "." + RevisionFor(actualRevision),
            condition.AlgorithmRevision, condition.Factor, condition.Evidence, usesGpu: true);

    internal FamilyPrivateGpuExecution Bind(FamilyPrivateGpuExecution binding,
        PrivateOrdinalBuffer? input, PrivateOrdinalBuffer? output, Func<string> actualRevision)
    {
        RevisionFor(actualRevision());
        return new(binding, binding.Device, binding.SetupMs, input, output, (batch, count, token) =>
        {
            var outcome = binding.Execute(batch, count, token);
            RevisionFor(actualRevision());
            return outcome;
        });
    }
}
