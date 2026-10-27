using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Effects;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Relics;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Core.World;

namespace RolltheSpire2.Core.Prediction.Profiles;

public sealed class Beta110PredictionProfile : ISeedPredictionProfile
{
    public static Beta110PredictionProfile Instance { get; } = new();

    private Beta110PredictionProfile() { }

    public RuntimeProfileId ProfileId => RuntimeProfileId.Beta110;
    public string PredictorId => "Beta110AnalysisProfile";

    public SeedPredictionDocument Predict(SeedPredictionRequest request, GameVersionDetection detection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detection);
        RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(detection);
        if (request.Authority.ProfileId != ProfileId ||
            compatibility.ProfileId != ProfileId ||
            !RuntimeVersionCompatibility.UsesBeta110Profile(request.Authority.GameVersion) ||
            !string.Equals(request.Authority.GameVersion, detection.NormalizedVersion, StringComparison.Ordinal))
        {
            return Fail(request, detection, string.Empty, PredictionWarningCode.InvalidRequestContext, SeedPredictionOverallStatus.Unknown);
        }
        if (!Beta110Profile.Instance.TryCanonicalizeSeed(request.OriginalSeed, out string canonicalSeed, out string issue))
        {
            return SeedPredictionDocument.Invalid(detection.DisplayVersion, ProfileId, PredictorId, request, issue);
        }

        TrustedRootHashInput input = TrustedRootHashInput.FromCanonicalSeed(Beta110Profile.Instance, canonicalSeed);
        return PredictCore(request, detection, input);
    }

    internal SeedPredictionDocument PredictFromRootHash(
        SeedPredictionRequest request,
        GameVersionDetection detection,
        TrustedRootHashInput input)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detection);
        if (request.TrustedRootHashInput is not { } requestInput || requestInput != input)
        {
            return Fail(
                request,
                detection,
                input.SeedIdentity,
                PredictionWarningCode.InvalidRequestContext,
                SeedPredictionOverallStatus.Unknown,
                new PredictionDiagnostic("trusted-root-hash-request-mismatch", "true"));
        }
        if (input.HasCanonicalSeed &&
            Beta110Profile.Instance.ComputeRootSeed(input.CanonicalSeed) != input.RootHash)
        {
            return Fail(
                request,
                detection,
                input.SeedIdentity,
                PredictionWarningCode.InvalidRequestContext,
                SeedPredictionOverallStatus.Unknown,
                new PredictionDiagnostic("trusted-root-hash-forward-validation", "failed"));
        }
        return PredictCore(request, detection, input);
    }

    private SeedPredictionDocument PredictCore(
        SeedPredictionRequest request,
        GameVersionDetection detection,
        TrustedRootHashInput input)
    {
        RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(detection);
        string seedIdentity = input.SeedIdentity;
        if (request.Authority.ProfileId != ProfileId ||
            compatibility.ProfileId != ProfileId ||
            !RuntimeVersionCompatibility.UsesBeta110Profile(request.Authority.GameVersion) ||
            !string.Equals(request.Authority.GameVersion, detection.NormalizedVersion, StringComparison.Ordinal))
        {
            return Fail(request, detection, seedIdentity, PredictionWarningCode.InvalidRequestContext, SeedPredictionOverallStatus.Unknown);
        }

        bool requiresNeowIdentity = request.Includes(SeedPredictionDomainSelection.Neow) || request.Includes(SeedPredictionDomainSelection.NormalCombatRewards);
        if (request.Authority.NoRunModifiers == false || requiresNeowIdentity && request.Authority.VanillaNeowCatalogExact == false)
        {
            return Fail(request, detection, seedIdentity, PredictionWarningCode.NonVanillaCatalogUnsupported, SeedPredictionOverallStatus.Unsupported);
        }

        if (requiresNeowIdentity && !request.Authority.HasExactModernNeowIdentityInputs)
        {
            return Fail(request, detection, seedIdentity, PredictionWarningCode.SnapshotAuthorityIncomplete, SeedPredictionOverallStatus.Unknown);
        }

        ModernNeowIdentityResult? result = null;
        PredictionPrecision identityPrecision = PredictionPrecision.Unknown;
        NeowChoiceResult[] choices = [];
        var warnings = new List<PredictionWarning>();
        if (requiresNeowIdentity)
        {
            try
            {
                result = ModernNeowIdentityPredictor.PredictModernCore(
                    input.RootHash,
                    request.Authority,
                    Beta110Profile.Instance,
                    request.Authority.IsBeta110NeowIdentityAuthorityExact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(
                    request,
                    detection,
                    seedIdentity,
                    PredictionWarningCode.AnalysisFailed,
                    SeedPredictionOverallStatus.Unknown,
                    new PredictionDiagnostic(PredictionDiagnosticCodes.Exception, ex.ToString()));
            }

            identityPrecision = !result.ExactAuthority
                ? PredictionPrecision.Unknown
                : Beta110ValidationAuthority.NeowAccepted && !compatibility.IsFallback
                    ? PredictionPrecision.Exact
                    : PredictionPrecision.Partial;
            choices = result.RelicKeys.Select((key, index) =>
            {
                NeowEffectProjection projection = Beta110NeowEffectProjector.ProjectFromRootHash(
                    key,
                    identityPrecision,
                    request.Authority,
                    input.RootHash,
                    request.EnableComplexBonesDeckInteractions);
                return SeedPredictionDocumentFactory.Choice(
                    index + 1,
                    key,
                    request,
                    identityPrecision,
                    "beta110.shared-modern-neow-identity",
                    projection);
            }).ToArray();

            warnings.AddRange(choices.SelectMany(choice => choice.Warnings));
            if (result.UnknownEligibilityAffectedPool)
            {
                warnings.Add(new PredictionWarning(
                    PredictionWarningCode.ModernEligibilityIncomplete,
                    EvidenceCode: "beta110-eligibility"));
            }
            else if (!result.ExactAuthority)
            {
                warnings.Add(new PredictionWarning(
                    PredictionWarningCode.SnapshotAuthorityIncomplete,
                    EvidenceCode: "beta110-identity-authority"));
            }
        }
        if (compatibility.IsFallback)
        {
            warnings.Add(new PredictionWarning(
                PredictionWarningCode.UnverifiedVersionFallback,
                EvidenceCode: compatibility.EvidenceCode));
        }
        else if (compatibility.IsPendingValidation)
        {
            warnings.Add(new PredictionWarning(
                PredictionWarningCode.Beta110ValidationPending,
                EvidenceCode: RuntimeProfilePolicies.Beta110AuditFingerprint));
        }
        PredictionWarning[] distinctWarnings = warnings.Distinct().ToArray();

        WorldPredictionResult? worldPrediction = request.Includes(SeedPredictionDomainSelection.World)
            ? WorldPredictionEngine.PredictFromRootHash(
                Beta110Profile.Instance,
                input.RootHash,
                seedIdentity,
                request.Ascension,
                request.Authority.PlayerSlotIndex,
                request.Character.CharacterKey,
                request.Authority.WorldAuthority,
                request.Authority.EffectAuthority,
                request.AncientOptionConditions)
            : null;

        RelicSequencePredictionResult? relicSequencePrediction = request.Includes(SeedPredictionDomainSelection.RelicSequence)
            ? RelicSequencePredictor.PredictFromRootHash(
                Beta110Profile.Instance,
                input.RootHash,
                seedIdentity,
                request.Authority.WorldAuthority,
                request.RelicSequencePreviewCount)
            : null;

        NormalCombatRewardSequencePredictionResult? normalCombatRewardPrediction =
            request.Includes(SeedPredictionDomainSelection.NormalCombatRewards)
                ? NormalCombatRewardSequencePredictor.PredictFromRootHash(
                    Beta110Profile.Instance,
                    input.RootHash,
                    seedIdentity,
                    request.Authority.PlayerSlotIndex,
                    request.Ascension,
                    choices,
                    request.Authority.EffectAuthority,
                    request.Authority.WorldAuthority,
                    request.CombatRewardProjectionRequest,
                    request.PreparedRewardPools)
                : null;

        return SeedPredictionDocumentFactory.Create(
            request,
            detection,
            ProfileId,
            PredictorId,
            seedIdentity,
            choices,
            distinctWarnings,
            BuildDiagnostics(request, compatibility, result, input),
            SeedPredictionDocumentFactory.ProductFacingOverallStatus(
                choices,
                identityPrecision,
                additionalIdentityAuthorityWarning:
                    (result is not null && (result.UnknownEligibilityAffectedPool || !result.ExactAuthority)) ||
                    compatibility.IsFallback ||
                    compatibility.IsPendingValidation),
            worldPrediction,
            relicSequencePrediction,
            normalCombatRewardPrediction);
    }

    private IReadOnlyList<PredictionDiagnostic> BuildDiagnostics(
        SeedPredictionRequest request,
        RuntimeVersionResolution compatibility,
        ModernNeowIdentityResult? result,
        TrustedRootHashInput input)
    {
        var diagnostics = new List<PredictionDiagnostic>
        {
            new(PredictionDiagnosticCodes.Analyzer, PredictorId),
            new(PredictionDiagnosticCodes.RngCalls, (result?.RngCalls ?? 0).ToString()),
            new(PredictionDiagnosticCodes.EventEntry, result is null ? "None" : "NEOW"),
            new(PredictionDiagnosticCodes.EligibilityExact, (result?.ExactAuthority ?? false).ToString().ToLowerInvariant()),
            new(PredictionDiagnosticCodes.RuntimeCompatibilityMode, compatibility.SupportKind.ToString()),
            new(PredictionDiagnosticCodes.GameVersionIdentity, compatibility.GameVersionIdentity.ToString()),
            new(PredictionDiagnosticCodes.RngSemanticProfile, compatibility.SemanticProfileId.ToString()),
            new(PredictionDiagnosticCodes.CompatibilityConfidence, compatibility.Confidence.ToString()),
            new(PredictionDiagnosticCodes.RuntimeAccepted, compatibility.RuntimeAccepted.ToString().ToLowerInvariant()),
            new(PredictionDiagnosticCodes.RuntimeCompatibilityReferenceVersion, compatibility.ReferenceVersion),
            new(PredictionDiagnosticCodes.RuntimeAuthorityId, request.Authority.RuntimeAuthorityId),
            new(PredictionDiagnosticCodes.RuntimeAuditFingerprint, request.Authority.RuntimeAuditFingerprint),
            new(PredictionDiagnosticCodes.RuntimeCardCatalogFingerprint, request.Authority.RuntimeCardCatalogFingerprint),
            new(PredictionDiagnosticCodes.CardBaseOddsPolicy, RuntimeProfilePolicies.BaseOddsPolicy(ProfileId).ToString()),
            new(PredictionDiagnosticCodes.Beta110ValidationStatus, Beta110ValidationAuthority.RuntimeSupportStatus)
        };
        // Keep the public visible-seed document byte-for-byte compatible with the
        // Clean Baseline diagnostics. RootHash-only analysis is internal and may
        // expose its trusted input for developer diagnostics.
        if (!input.HasCanonicalSeed)
        {
            diagnostics.Add(new PredictionDiagnostic(
                "trusted-root-hash",
                input.RootHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
            diagnostics.Add(new PredictionDiagnostic("trusted-root-hash-visible-seed-bound", "false"));
        }
        return diagnostics;
    }

    private SeedPredictionDocument Fail(
        SeedPredictionRequest request,
        GameVersionDetection detection,
        string canonicalSeed,
        PredictionWarningCode warning,
        SeedPredictionOverallStatus status,
        PredictionDiagnostic? diagnostic = null) =>
        SeedPredictionDocumentFactory.Create(
            request,
            detection,
            ProfileId,
            PredictorId,
            canonicalSeed,
            Array.Empty<NeowChoiceResult>(),
            new[] { new PredictionWarning(warning) },
            diagnostic is null ? Array.Empty<PredictionDiagnostic>() : new[] { diagnostic },
            status);
}
