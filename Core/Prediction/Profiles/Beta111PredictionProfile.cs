using RolltheSpire2.Core.Authority;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Relics;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Core.World;

namespace RolltheSpire2.Core.Prediction.Profiles;

/// <summary>
/// Beta111 version-bound prediction entry. Domain implementations are shared
/// with the source-audited Modern110 semantic family; game-version/audit/catalog
/// identity remains Beta111-specific. As an accepted A-class semantic-compatible
/// patch, Exact precision follows the normal live-authority gates.
/// </summary>
public sealed class Beta111PredictionProfile : ISeedPredictionProfile
{
    public static Beta111PredictionProfile Instance { get; } = new();
    private readonly Func<ulong, RuntimeContextAuthoritySnapshot, IRuntimeProfile, bool, ModernNeowIdentityResult> _identityPredictor;
    private Beta111PredictionProfile() : this(ModernNeowIdentityPredictor.PredictModernCore) { }
    internal Beta111PredictionProfile(Func<ulong, RuntimeContextAuthoritySnapshot, IRuntimeProfile, bool, ModernNeowIdentityResult> identityPredictor)
        => _identityPredictor = identityPredictor;

    public RuntimeProfileId ProfileId => RuntimeProfileId.Beta111;
    public string PredictorId => "Beta111AnalysisProfile";

    public SeedPredictionDocument Predict(SeedPredictionRequest request, GameVersionDetection detection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detection);
        if (!ContextMatches(request, detection))
            return Fail(request, detection, string.Empty, PredictionWarningCode.InvalidRequestContext, SeedPredictionOverallStatus.Unknown);
        if (!Beta111Profile.Instance.TryCanonicalizeSeed(request.OriginalSeed, out string canonicalSeed, out string issue))
            return SeedPredictionDocument.Invalid(detection.DisplayVersion, ProfileId, PredictorId, request, issue);
        return PredictCore(request, detection, TrustedRootHashInput.FromCanonicalSeed(Beta111Profile.Instance, canonicalSeed));
    }

    internal SeedPredictionDocument PredictFromRootHash(SeedPredictionRequest request, GameVersionDetection detection, TrustedRootHashInput input)
        => PredictFromRootHashCore(request, detection, input, null, captureOpening: false, out _);

    internal SeedPredictionDocument PredictFromRootHashCapturingOpening(SeedPredictionRequest request,
        GameVersionDetection detection, TrustedRootHashInput input, out Beta111NeowOpeningProof? opening) =>
        PredictFromRootHashCore(request, detection, input, null, captureOpening: true, out opening);

    internal SeedPredictionDocument PredictFromRootHashReusingOpening(SeedPredictionRequest request,
        GameVersionDetection detection, TrustedRootHashInput input, Beta111NeowOpeningProof opening) =>
        PredictFromRootHashCore(request, detection, input, opening, captureOpening: false, out _);

    private SeedPredictionDocument PredictFromRootHashCore(SeedPredictionRequest request, GameVersionDetection detection,
        TrustedRootHashInput input, Beta111NeowOpeningProof? suppliedOpening, bool captureOpening, out Beta111NeowOpeningProof? capturedOpening)
    {
        capturedOpening = null;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(detection);
        if (request.TrustedRootHashInput is not { } requestInput || requestInput != input)
            return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.InvalidRequestContext, SeedPredictionOverallStatus.Unknown,
                new PredictionDiagnostic("trusted-root-hash-request-mismatch", "true"));
        if (input.HasCanonicalSeed && Beta111Profile.Instance.ComputeRootSeed(input.CanonicalSeed) != input.RootHash)
            return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.InvalidRequestContext, SeedPredictionOverallStatus.Unknown,
                new PredictionDiagnostic("trusted-root-hash-forward-validation", "failed"));
        return PredictCore(request, detection, input, suppliedOpening, captureOpening, out capturedOpening);
    }

    private SeedPredictionDocument PredictCore(SeedPredictionRequest request, GameVersionDetection detection, TrustedRootHashInput input) =>
        PredictCore(request, detection, input, null, captureOpening: false, out _);

    private SeedPredictionDocument PredictCore(SeedPredictionRequest request, GameVersionDetection detection, TrustedRootHashInput input,
        Beta111NeowOpeningProof? suppliedOpening, bool captureOpening, out Beta111NeowOpeningProof? capturedOpening)
    {
        capturedOpening = null;
        if (!ContextMatches(request, detection))
            return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.InvalidRequestContext, SeedPredictionOverallStatus.Unknown);
        if (request.Authority.NoRunModifiers == false)
            return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.NonVanillaCatalogUnsupported, SeedPredictionOverallStatus.Unsupported);

        RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(detection);
        bool requiresNeowIdentity = request.Includes(SeedPredictionDomainSelection.Neow) ||
                                    request.Includes(SeedPredictionDomainSelection.NormalCombatRewards);

        ModernNeowIdentityResult? result = null;
        PredictionPrecision identityPrecision = PredictionPrecision.Unknown;
        NeowChoiceResult[] choices = Array.Empty<NeowChoiceResult>();
        var warnings = new List<PredictionWarning>();

        if (requiresNeowIdentity)
        {
            // Neow remains fail-closed for a modded/unknown Neow catalog. Domain-local
            // extensibility means this uncertainty must not block independent World or
            // RelicSequence requests, but it also must not be silently treated as vanilla.
            if (request.Authority.VanillaNeowCatalogExact == false)
                return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.NonVanillaCatalogUnsupported, SeedPredictionOverallStatus.Unsupported);
            if (!request.Authority.HasExactModernNeowIdentityInputs)
                return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.SnapshotAuthorityIncomplete, SeedPredictionOverallStatus.Unknown);

            bool reusedOpening = false;
            bool exactAuthority;
            try
            {
                exactAuthority = request.Authority.IsBeta111NeowIdentityAuthorityExact;
                if (suppliedOpening is not null && suppliedOpening.TryReuse(this, _identityPredictor, Beta111Profile.Instance,
                        request, detection, input, compatibility, exactAuthority, out var knownIdentity, out var knownChoices, out var knownPrecision))
                {
                    result = knownIdentity; choices = knownChoices; identityPrecision = knownPrecision; reusedOpening = true;
                }
                else
                    result = _identityPredictor(input.RootHash, request.Authority, Beta111Profile.Instance, exactAuthority);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(request, detection, input.SeedIdentity, PredictionWarningCode.AnalysisFailed, SeedPredictionOverallStatus.Unknown,
                    new PredictionDiagnostic(PredictionDiagnosticCodes.Exception, ex.ToString()));
            }

            if (!reusedOpening)
            {
                identityPrecision = !result.ExactAuthority
                    ? PredictionPrecision.Unknown
                    // Exact under the selected ruleset and captured inputs does not
                    // claim that an unverified game version has been runtime-accepted.
                    : Beta111ValidationAuthority.RuntimeAccepted && compatibility.AllowsProductionSearch &&
                      !compatibility.Blocks(CompatibilityDomainMask.Neow)
                        ? PredictionPrecision.Exact
                        : PredictionPrecision.Partial;

                choices = request.PartyOpeningChoices?.ToArray() ?? result.RelicKeys.Select((key, index) =>
                    SeedPredictionDocumentFactory.Choice(
                        index + 1,
                        key,
                        request,
                        identityPrecision,
                        "beta111.shared-modern-neow-identity",
                        Beta111NeowEffectProjector.ProjectFromRootHash(key, identityPrecision, request.Authority, input.RootHash, request.EnableComplexBonesDeckInteractions)))
                    .ToArray();
            }
            if (captureOpening)
                capturedOpening = Beta111NeowOpeningProof.TryCreate(this, _identityPredictor, Beta111Profile.Instance,
                    request, detection, input, compatibility, exactAuthority, result, choices, identityPrecision);

            warnings.AddRange(choices.SelectMany(choice => choice.Warnings));
            if (result.UnknownEligibilityAffectedPool)
                warnings.Add(new PredictionWarning(PredictionWarningCode.ModernEligibilityIncomplete, EvidenceCode: "beta111-eligibility"));
            else if (!result.ExactAuthority)
                warnings.Add(new PredictionWarning(PredictionWarningCode.SnapshotAuthorityIncomplete, EvidenceCode: "beta111-identity-authority"));
        }

        if (compatibility.IsFallback)
            warnings.Add(new PredictionWarning(PredictionWarningCode.UnverifiedVersionFallback, EvidenceCode: compatibility.EvidenceCode));
        WorldPredictionResult? worldPrediction = request.Includes(SeedPredictionDomainSelection.World)
            ? WorldPredictionEngine.PredictFromRootHash(Beta111Profile.Instance, input.RootHash, input.SeedIdentity, request.Ascension,
                request.Authority.PlayerSlotIndex, request.Character.CharacterKey, request.Authority.WorldAuthority,
                request.Authority.EffectAuthority, request.AncientOptionConditions, request.WorldProjection)
            : null;
        RelicSequencePredictionResult? relicPrediction = request.Includes(SeedPredictionDomainSelection.RelicSequence)
            ? RelicSequencePredictor.PredictFromRootHash(Beta111Profile.Instance, input.RootHash, input.SeedIdentity,
                request.Authority.WorldAuthority, request.RelicSequencePreviewCount)
            : null;
        NormalCombatRewardSequencePredictionResult? rewardPrediction = request.Includes(SeedPredictionDomainSelection.NormalCombatRewards)
            ? NormalCombatRewardSequencePredictor.PredictFromRootHash(Beta111Profile.Instance, input.RootHash, input.SeedIdentity,
                request.Authority.PlayerSlotIndex, request.Ascension, choices, request.Authority.EffectAuthority,
                request.Authority.WorldAuthority, request.CombatRewardProjectionRequest, request.PreparedRewardPools)
            : null;

        PredictionWarning[] distinctWarnings = warnings.Distinct().ToArray();
        var diagnostics = new List<PredictionDiagnostic>
        {
            new(PredictionDiagnosticCodes.Analyzer, PredictorId),
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
            new(PredictionDiagnosticCodes.Beta111ValidationStatus, Beta111ValidationAuthority.RuntimeSupportStatus),
            new(PredictionDiagnosticCodes.RuntimeValidationStatus, compatibility.Confidence.ToString()),
            new("domain-local-neow-required", requiresNeowIdentity.ToString().ToLowerInvariant())
        };
        if (result is not null)
        {
            diagnostics.Add(new PredictionDiagnostic(PredictionDiagnosticCodes.RngCalls, result.RngCalls.ToString()));
            diagnostics.Add(new PredictionDiagnostic(PredictionDiagnosticCodes.EventEntry, "NEOW"));
            diagnostics.Add(new PredictionDiagnostic(PredictionDiagnosticCodes.EligibilityExact, result.ExactAuthority.ToString().ToLowerInvariant()));
        }
        if (!input.HasCanonicalSeed)
        {
            diagnostics.Add(new PredictionDiagnostic("trusted-root-hash", input.RootHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
            diagnostics.Add(new PredictionDiagnostic("trusted-root-hash-visible-seed-bound", "false"));
        }

        bool neowAuthorityWarning = result is not null &&
                                    (result.UnknownEligibilityAffectedPool || !result.ExactAuthority);
        return SeedPredictionDocumentFactory.Create(
            request, detection, ProfileId, PredictorId, input.SeedIdentity, choices, distinctWarnings, diagnostics,
            SeedPredictionDocumentFactory.ProductFacingOverallStatus(choices, identityPrecision,
                additionalIdentityAuthorityWarning: neowAuthorityWarning || compatibility.IsFallback || !Beta111ValidationAuthority.RuntimeAccepted),
            worldPrediction, relicPrediction, rewardPrediction);
    }

    private bool ContextMatches(SeedPredictionRequest request, GameVersionDetection detection)
    {
        RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(detection);
        return request.Authority.ProfileId == ProfileId &&
               compatibility.ProfileId == ProfileId &&
               RuntimeVersionCompatibility.UsesBeta111Profile(request.Authority.GameVersion) &&
               string.Equals(request.Authority.GameVersion, detection.NormalizedVersion, StringComparison.Ordinal);
    }

    private SeedPredictionDocument Fail(SeedPredictionRequest request, GameVersionDetection detection, string canonicalSeed,
        PredictionWarningCode warning, SeedPredictionOverallStatus status, PredictionDiagnostic? diagnostic = null) =>
        SeedPredictionDocumentFactory.Create(request, detection, ProfileId, PredictorId, canonicalSeed, Array.Empty<NeowChoiceResult>(),
            new[] { new PredictionWarning(warning) }, diagnostic is null ? Array.Empty<PredictionDiagnostic>() : new[] { diagnostic }, status);
}
