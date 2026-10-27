using RolltheSpire2.Core.Authority;
using System.Security.Cryptography;
using System.Text;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Semantics;

namespace RolltheSpire2.Search.Compilation;

/// <summary>
/// Production S0 compile boundary. CompiledSearch is projected directly to Exact
/// evaluation criteria; no Legacy execution DTO, Fast plan, or backend planner is built.
/// </summary>
public static class ExactSearchExecutionRequestFactory
{
    internal static ExactSearchExecutionRequest ForPlayer(ExactSearchExecutionRequest parent, CompiledSearch player)
    {
        var projection = CompiledSearchEvaluationProjector.Project(player);
        // Match the top-level Exact admission: runtime-proven equivalence is valid,
        // while lossy and unsupported projections remain rejected.
        if (projection.Fidelity is ProjectionFidelity.LossyButConservative or ProjectionFidelity.Unsupported)
            throw new InvalidOperationException("Party.PlayerProjection:" + string.Join(",", projection.Diagnostics));
        return new(player, parent.RunOptions, parent.CanonicalStartSeed, parent.ResolvedScanCount,
            projection.Evaluation, BuildCombatRewardRoutePolicy(player), player.SemanticFingerprint);
    }

    public static ExactSearchExecutionCompileResult Compile(CompiledSearch compiled, SearchRunOptions runOptions)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(runOptions);

        SearchContext context = compiled.Context;
        RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(context.Detection);
        IRuntimeProfile profile = RuntimeProfileRegistry.Select(compatibility);
        if (profile.ProfileId == RuntimeProfileId.Unsupported || profile.ProfileId != context.ProfileId)
            return Reject(SearchDisposition.Unsupported, "ProfileDetectionMismatch");
        if (!compatibility.AllowsProductionSearch || compatibility.Blocks(CompatibilityDomainMask.RuntimeBinding))
        {
            return Reject(SearchDisposition.Unknown,
                compatibility.IsKnownIncompatible
                    ? "KnownIncompatibleCompatibilitySearchFailClosed:" + compatibility.EvidenceCode
                    : "UnsupportedCompatibilitySearchFailClosed:" + compatibility.EvidenceCode);
        }
        if (profile.ProfileId == RuntimeProfileId.Beta110 && !Beta110ValidationAuthority.RuntimeAccepted)
            return Reject(SearchDisposition.Unknown, "Beta110PendingUserValidationSearchFailClosed");

        string? contextIssue = ValidateContext(compiled);
        if (contextIssue is not null) return Reject(SearchDisposition.Unknown, contextIssue);

        if (context.Party is not null)
        {
            var verified = SearchCompiler.Compile(compiled.Query, context);
            if (verified.SemanticFingerprint != compiled.SemanticFingerprint || verified.Status != compiled.Status ||
                System.Text.Json.JsonSerializer.Serialize(verified.NormalizedQuery) != System.Text.Json.JsonSerializer.Serialize(compiled.NormalizedQuery))
                return Reject(SearchDisposition.Unsupported, "Party.CompiledPredicateMismatch");
            if (compiled.Status == QueryNormalizationStatus.Impossible)
                return Reject(SearchDisposition.NoMatch, "Party.InitialConjunctionImpossible");
        }
        else
        {
            SearchFeasibilityResult feasibility = SearchFeasibilityAnalyzer.Analyze(compiled);
            if (feasibility.IsImpossible)
                return Reject(SearchDisposition.NoMatch, "SearchImpossible:" + feasibility.Proof!.ReasonCode + ":" + feasibility.Proof.Diagnostic);
        }

        if (runOptions.ScanCount <= 0) return Reject(SearchDisposition.Unsupported, "ScanCountMustBePositive");
        if (runOptions.TargetMatchCount <= 0) return Reject(SearchDisposition.Unsupported, "TargetMatchCountMustBePositive");
        if (runOptions.WorkerCount is < 1 or > 64) return Reject(SearchDisposition.Unsupported, "WorkerCountOutsideSupportedRange");
        if (runOptions.Resources is { } resources)
        {
            if (resources != resources.Normalize())
                return Reject(SearchDisposition.Unsupported, "ResourceBudgetOutsideSupportedRange");
            runOptions = runOptions with
            {
                WorkerCount = Math.Min(runOptions.WorkerCount, resources.ResolveCpuWorkers(Environment.ProcessorCount))
            };
        }
        if (!profile.TryCanonicalizeSeed(runOptions.StartSeed, out string canonicalStart, out string seedIssue))
            return Reject(SearchDisposition.Unsupported, "InvalidStartSeed:" + seedIssue);

        long resolvedScanCount = runOptions.ScanCount;
        if (RuntimeProfilePolicies.UsesBeta110SharedAlgorithms(context.ProfileId))
        {
            if (!VisibleSeedCandidateCodec.TryParseOrdinal(profile, canonicalStart, out ulong startOrdinal, out _, out string ordinalIssue))
                return Reject(SearchDisposition.Unsupported, "InvalidStartSeedOrdinal:" + ordinalIssue);
            ulong remainingVisibleSeeds = VisibleSeedCandidateCodec.SpaceSize(profile) - startOrdinal;
            if (runOptions.ScanCount == long.MaxValue)
                resolvedScanCount = checked((long)remainingVisibleSeeds);
            else if ((ulong)runOptions.ScanCount > remainingVisibleSeeds)
                return Reject(SearchDisposition.Unsupported, "SearchRangeExceedsBeta110VisibleSeedSpace");
        }

        ExactSearchEvaluationProjectionResult projection = CompiledSearchEvaluationProjector.Project(compiled);
        if (projection.Fidelity is ProjectionFidelity.LossyButConservative or ProjectionFidelity.Unsupported)
            return Reject(SearchDisposition.Unsupported,
                "ExactEvaluationProjectionFailClosed:" + projection.Fidelity + ":" + string.Join(",", projection.Diagnostics));

        ExactSearchEvaluationProjection evaluation = projection.Evaluation;
        CompatibilityDomainMask requiredDomains = RequiredCompatibilityDomains(evaluation);
        if (compatibility.Blocks(requiredDomains))
            return Reject(SearchDisposition.Unknown,
                $"CompatibilityDomainFailClosed:required={requiredDomains};blocked={compatibility.BlockedDomains};evidence={compatibility.EvidenceCode}");
        if (evaluation.RequiresEffectColdPath && context.Authority.EffectAuthority is null)
            return Reject(SearchDisposition.Unknown, "EffectAuthorityMissing");
        if (evaluation.RequiresWorldAuthority && context.Authority.WorldAuthority is null)
            return Reject(SearchDisposition.Unknown, "WorldAuthorityMissing");
        if (evaluation.RequiresWorldAuthority && RuntimeProfilePolicies.IsModernCore(context.ProfileId) &&
            context.Authority.WorldAuthority?.Beta109Generation is null)
            return Reject(SearchDisposition.Unknown, "ModernWorldGenerationSnapshotMissing");

        CombatRewardRoutePolicyContract rewardPolicy = BuildCombatRewardRoutePolicy(compiled);
        string fingerprint = Fingerprint(new[]
        {
            "family-execution-s0", compiled.SemanticFingerprint, compatibility.EvidenceCode,
            canonicalStart, resolvedScanCount.ToString(), runOptions.TargetMatchCount.ToString(),
            runOptions.WorkerCount.ToString(), rewardPolicy.ExactPolicy.ToString()
        });
        var request = new ExactSearchExecutionRequest(
            compiled, runOptions, canonicalStart, resolvedScanCount, evaluation, rewardPolicy, fingerprint);
        return ExactSearchExecutionCompileResult.Accepted(PrepareRewardPools(request));
    }

    private static ExactSearchExecutionRequest PrepareRewardPools(ExactSearchExecutionRequest request)
    {
        // ForPlayer is deliberately not prepared: it is invoked during party
        // candidate replay rather than at a once-per-session compile boundary.
        if (!request.Evaluation.RequiresNormalCombatRewardDomain || !request.RunOptions.Optimizations.CostOrderedFilters ||
            request.CompiledSearch.Context.Party is not null || request.Authority.PlayersCount != 1)
            return request;
        var pools = PreparedCombatRewardPools.TryCreate(request.ProfileId,
            request.Authority.EffectAuthority, request.Authority.WorldAuthority);
        if (pools is null) return request;
        var ownedAuthority = request.Authority.WithEffectAuthority(pools.OwnedAuthority);
        return request with
        {
            CompiledSearch = request.CompiledSearch with
            {
                Context = request.CompiledSearch.Context with { Authority = ownedAuthority }
            },
            PreparedRewardPools = pools
        };
    }

    private static ExactSearchExecutionCompileResult Reject(SearchDisposition disposition, string issue) =>
        ExactSearchExecutionCompileResult.Rejected(disposition, issue);

    private static string? ValidateContext(CompiledSearch compiled)
    {
        SearchContext context = compiled.Context;
        RuntimeContextAuthoritySnapshot authority = context.Authority;
        if (authority.ProfileId != context.ProfileId ||
            !string.Equals(authority.GameVersion, context.Detection.NormalizedVersion, StringComparison.Ordinal))
            return "SnapshotProfileMismatch";
        if (!context.CharacterKey.IsValid || context.CharacterKey.Category != BaseGameModelKeys.Categories.Character)
            return "InvalidCharacterKey";
        if (authority.Character.CharacterKey != context.CharacterKey || authority.Ascension != context.Ascension)
            return "SnapshotRequestContextMismatch";
        if (string.IsNullOrWhiteSpace(authority.UnlockSnapshotFingerprint) || string.IsNullOrWhiteSpace(authority.CatalogFingerprint))
            return "SnapshotFingerprintMissing";
        return null;
    }

    private static CombatRewardRoutePolicyContract BuildCombatRewardRoutePolicy(CompiledSearch compiled) =>
        compiled.ResolvedRouteSemantics.CombatReward.Kind switch
        {
            ResolvedCombatRewardRouteKind.NotApplicable => CombatRewardRoutePolicyContract.None,
            ResolvedCombatRewardRouteKind.PinnedOpeningRoute => new(
                CombatRewardFastRoutePolicy.UnpinnedAssumeUnperturbed,
                CombatRewardExactRoutePolicy.PinnedRealRoute),
            ResolvedCombatRewardRouteKind.NeutralNonPerturbingContinuation => new(
                CombatRewardFastRoutePolicy.UnpinnedAssumeUnperturbed,
                CombatRewardExactRoutePolicy.UnpinnedVerifyNeutralRealRoute),
            _ => CombatRewardRoutePolicyContract.None
        };

    private static CompatibilityDomainMask RequiredCompatibilityDomains(ExactSearchEvaluationProjection evaluation)
    {
        CompatibilityDomainMask domains = CompatibilityDomainMask.None;
        if (evaluation.HasNeowConstraints) domains |= CompatibilityDomainMask.Neow;
        if (evaluation.RequiresRelicSequenceDomain) domains |= CompatibilityDomainMask.RelicSequence;
        if (evaluation.RequiresWorldDomain) domains |= CompatibilityDomainMask.WorldEventAncient;
        if (evaluation.RequiresNormalCombatRewardDomain) domains |= CompatibilityDomainMask.CombatReward;
        return domains;
    }

    private static string Fingerprint(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))).ToLowerInvariant();
}
