using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Core.World;

namespace RolltheSpire2.Core.Prediction.Profiles;

/// <summary>
/// Completed opening work shared only by the two synchronous Predictor calls in
/// one Production Exact candidate. It never enters a request, plan or global cache.
/// The captured authority keeps its existing immutable-snapshot contract.
/// </summary>
internal sealed class Beta111NeowOpeningProof
{
    internal const string Revision = "Beta111SameCandidateNeowOpeningV1";
    private readonly Beta111PredictionProfile _owner;
    private readonly Delegate _identityPredictor;
    private readonly IRuntimeProfile _profile;
    private readonly TrustedRootHashInput _input;
    private readonly RuntimeContextAuthoritySnapshot _authority;
    private readonly GameVersionDetection _detection;
    private readonly RuntimeVersionResolution _compatibility;
    private readonly CharacterIdentity _character;
    private readonly int _ascension, _players, _slot, _preview;
    private readonly bool _complexBones, _diagnostics, _exactAuthority, _runtimeAccepted;
    private readonly string _originalSeed;
    private readonly AncientOptionConditionProfile _ancientConditions;
    private readonly ModernNeowIdentityResult _identity;
    private readonly NeowChoiceResult[] _choices;

    private Beta111NeowOpeningProof(Beta111PredictionProfile owner, Delegate identityPredictor,
        IRuntimeProfile profile, SeedPredictionRequest request, GameVersionDetection detection,
        TrustedRootHashInput input, RuntimeVersionResolution compatibility, bool exactAuthority,
        ModernNeowIdentityResult identity, NeowChoiceResult[] choices, PredictionPrecision precision)
    {
        _owner = owner; _identityPredictor = identityPredictor; _profile = profile;
        _input = input; _authority = request.Authority; _detection = detection; _compatibility = compatibility;
        _character = request.Character; _ascension = request.Ascension; _players = request.PlayersCount;
        _slot = request.PlayerSlotIndex; _preview = request.RelicSequencePreviewCount;
        _complexBones = request.EnableComplexBonesDeckInteractions; _diagnostics = request.IncludeDiagnostics;
        _exactAuthority = exactAuthority; _runtimeAccepted = Beta111ValidationAuthority.RuntimeAccepted;
        _originalSeed = request.OriginalSeed; _ancientConditions = request.AncientOptionConditions;
        _identity = identity; _choices = choices; IdentityPrecision = precision;
    }

    internal PredictionPrecision IdentityPrecision { get; }
    internal IReadOnlyList<NeowChoiceResult> Choices => _choices;

    internal static Beta111NeowOpeningProof? TryCreate(Beta111PredictionProfile owner, Delegate identityPredictor,
        IRuntimeProfile profile, SeedPredictionRequest request, GameVersionDetection detection,
        TrustedRootHashInput input, RuntimeVersionResolution compatibility, bool exactAuthority,
        ModernNeowIdentityResult identity, NeowChoiceResult[] choices, PredictionPrecision precision)
    {
        // Only the production singleton has the known static pure identity
        // predictor. An injected delegate can be stateful even if its reference
        // stays the same, so injected profiles must always run their own work.
        if (!ReferenceEquals(owner, Beta111PredictionProfile.Instance) || !ReferenceEquals(profile, Beta111Profile.Instance) ||
            request.Authority.ProfileId != RuntimeProfileId.Beta111 || request.PlayersCount != 1 || request.PlayerSlotIndex != 0 ||
            request.PartyOpeningChoices is not null || !request.Includes(SeedPredictionDomainSelection.Neow) ||
            request.Includes(SeedPredictionDomainSelection.NormalCombatRewards) || !exactAuthority ||
            !identity.ExactAuthority || precision != PredictionPrecision.Exact || choices.Length == 0)
            return null;
        return new(owner, identityPredictor, profile, request, detection, input, compatibility, exactAuthority, identity, choices, precision);
    }

    internal bool TryReuse(Beta111PredictionProfile owner, Delegate identityPredictor, IRuntimeProfile profile,
        SeedPredictionRequest request, GameVersionDetection detection, TrustedRootHashInput input,
        RuntimeVersionResolution compatibility, bool exactAuthority, out ModernNeowIdentityResult identity,
        out NeowChoiceResult[] choices, out PredictionPrecision precision)
    {
        identity = null!; choices = Array.Empty<NeowChoiceResult>(); precision = PredictionPrecision.Unknown;
        if (!ReferenceEquals(owner, _owner) || !ReferenceEquals(owner, Beta111PredictionProfile.Instance) ||
            !ReferenceEquals(identityPredictor, _identityPredictor) || !ReferenceEquals(profile, _profile) ||
            input != _input || request.TrustedRootHashInput != input || request.OriginalSeed != _originalSeed ||
            !ReferenceEquals(request.Authority, _authority) || !ReferenceEquals(detection, _detection) || compatibility != _compatibility ||
            request.Character != _character || request.Ascension != _ascension || request.PlayersCount != _players || request.PlayerSlotIndex != _slot ||
            request.RelicSequencePreviewCount != _preview || !ReferenceEquals(request.AncientOptionConditions, _ancientConditions) ||
            request.EnableComplexBonesDeckInteractions != _complexBones || request.IncludeDiagnostics != _diagnostics ||
            request.PlayersCount != 1 || request.PlayerSlotIndex != 0 || request.PartyOpeningChoices is not null ||
            !request.Includes(SeedPredictionDomainSelection.Neow | SeedPredictionDomainSelection.NormalCombatRewards) ||
            request.CombatRewardProjectionRequest.Scope != NormalCombatRewardProjectionScope.SearchExact ||
            exactAuthority != _exactAuthority || Beta111ValidationAuthority.RuntimeAccepted != _runtimeAccepted)
            return false;

        PredictionPrecision currentPrecision = !_identity.ExactAuthority ? PredictionPrecision.Unknown :
            Beta111ValidationAuthority.RuntimeAccepted && compatibility.AllowsProductionSearch && !compatibility.Blocks(CompatibilityDomainMask.Neow)
                ? PredictionPrecision.Exact : PredictionPrecision.Partial;
        if (currentPrecision != IdentityPrecision) return false;
        identity = _identity; choices = _choices; precision = currentPrecision; return true;
    }
}
