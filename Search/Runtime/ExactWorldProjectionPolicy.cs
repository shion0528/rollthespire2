using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.World;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.Runtime;

/// <summary>
/// A deliberately narrow query projection: every requested World fact is a boss
/// identity. Other domains still run their independent Exact validators. Any
/// richer World, party, map or transformation request retains full prediction.
/// </summary>
internal static class ExactWorldProjectionPolicy
{
    internal const string Revision = "ExactWorldBossOutputDemandV1";

    /// <summary>
    /// Explicit ordinary-search entry only. Keep the historical full diagnostic
    /// workload for every shape outside the proven Boss output-demand scope.
    /// Analysis and explicitly diagnostic/API requests do not call this helper;
    /// a trace/sink passed to the coordinator still restores full diagnostics.
    /// </summary>
    internal static ExactSearchExecutionRequest ApplyOrdinarySearchPolicy(ExactSearchExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidate = request.IncludeDiagnostics
            ? request with { RunOptions = request.RunOptions with { IncludeDiagnostics = false } }
            : request;
        return Resolve(candidate) == WorldPredictionProjection.BossIdentityOnly ? candidate : request;
    }

    internal static WorldPredictionProjection Resolve(ExactSearchExecutionRequest request)
    {
        var evaluation = request.Evaluation;
        var query = request.CompiledSearch.NormalizedQuery;
        if (request.ProfileId != RuntimeProfileId.Beta111 || request.IncludeDiagnostics ||
            request.CompiledSearch.Context.Party is not null || request.Authority.PlayersCount != 1 ||
            request.PartyOpeningChoices is not null || query.Players.Count != 0 ||
            query.StandardMaps.Count != 0 || evaluation.TransformationAggregate is not null ||
            !evaluation.RequiresWorldDomain ||
            evaluation.AncientBranchConditions.Count != 0 || evaluation.AncientIdentityFilters.Count != 0 ||
            evaluation.AncientOptionFilters.Count != 0 || evaluation.AncientSeaGlassTargetFilters.Count != 0 ||
            evaluation.EventSequenceConditions.Count != 0)
            return WorldPredictionProjection.All;

        bool hasBoss = evaluation.BossFilters.Any(f => !f.IsEmpty) ||
            evaluation.BossOrdinalFilters.Any(f => !f.IsEmpty) ||
            query.VariantBossBranches.Any(b => !b.FirstBoss.IsEmpty || !b.SecondBoss.IsEmpty);
        return hasBoss ? WorldPredictionProjection.BossIdentityOnly : WorldPredictionProjection.All;
    }
}
