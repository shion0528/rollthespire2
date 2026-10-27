using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Rewards;
using RolltheSpire2.Core.World;

namespace RolltheSpire2.Core.Prediction;

[Flags]
public enum SeedPredictionDomainSelection
{
    None = 0,
    Neow = 1 << 0,
    World = 1 << 1,
    RelicSequence = 1 << 2,
    NormalCombatRewards = 1 << 3,
    All = Neow | World | RelicSequence | NormalCombatRewards
}

public enum SeedPredictionRequestError
{
    None,
    MissingSeed,
    MissingCharacter,
    AscensionOutOfRange,
    PlayersCountOutOfRange,
    PlayerSlotOutOfRange,
    MissingAuthority,
    AuthorityContextMismatch,
    MissingSnapshotFingerprint,
    RelicSequencePreviewCountOutOfRange
}

public sealed class SeedPredictionRequest
{
    // Set only by Production Exact after its query-demand proof. Public requests
    // and ordinary single-seed analysis always start with the full document.
    internal WorldPredictionProjection WorldProjection { get; set; } = WorldPredictionProjection.All;
    // Root-local party replay supplies the actual opening choices to the existing information pipeline.
    internal IReadOnlyList<NeowChoiceResult>? PartyOpeningChoices { get; set; }
    internal PreparedCombatRewardPools? PreparedRewardPools { get; set; }
    // Legacy result envelope metadata only. The party projector owns its observation;
    // this does not invoke the single-player analysis pipeline or inherit its UI cap.
    internal static SeedPredictionRequest ForPartyInitialInformation(TrustedRootHashInput input, OrderedPartyAuthority party, bool diagnostics)
    {
        var owner = party.Players[0];
        if (owner.Ascension is < SeedPredictionInputLimits.MinimumAscension or > SeedPredictionInputLimits.MaximumAscension)
            throw new ArgumentException("Party.InvalidAscension");
        return new(PredictionRequestId.Create(), input.SeedIdentity, owner.Character, owner.Ascension,
            party.Players.Count, 0, owner, AncientOptionConditionProfile.BroadDefault, SeedPredictionDomainSelection.None,
            SeedPredictionInputLimits.DefaultRelicSequencePreviewCount, diagnostics, false, input, NormalCombatRewardProjectionRequest.RichAnalysis);
    }
    private SeedPredictionRequest(
        PredictionRequestId requestId,
        string originalSeed,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot authority,
        AncientOptionConditionProfile ancientOptionConditions,
        SeedPredictionDomainSelection domains,
        int relicSequencePreviewCount,
        bool includeDiagnostics,
        bool enableComplexBonesDeckInteractions,
        TrustedRootHashInput? trustedRootHashInput,
        NormalCombatRewardProjectionRequest normalCombatRewardProjectionRequest)
    {
        RequestId = requestId;
        OriginalSeed = originalSeed;
        Character = character;
        Ascension = ascension;
        PlayersCount = playersCount;
        PlayerSlotIndex = playerSlotIndex;
        Authority = authority;
        AncientOptionConditions = ancientOptionConditions;
        Domains = domains;
        RelicSequencePreviewCount = relicSequencePreviewCount;
        IncludeDiagnostics = includeDiagnostics;
        EnableComplexBonesDeckInteractions = enableComplexBonesDeckInteractions;
        TrustedRootHashInput = trustedRootHashInput;
        CombatRewardProjectionRequest = normalCombatRewardProjectionRequest;
    }

    public PredictionRequestId RequestId { get; }
    public string OriginalSeed { get; }
    public CharacterIdentity Character { get; }
    public int Ascension { get; }
    public int PlayersCount { get; }
    public int PlayerSlotIndex { get; }
    public RuntimeContextAuthoritySnapshot Authority { get; }
    public AncientOptionConditionProfile AncientOptionConditions { get; }
    public SeedPredictionDomainSelection Domains { get; }
    public int RelicSequencePreviewCount { get; }
    public bool IncludeDiagnostics { get; }
    public bool EnableComplexBonesDeckInteractions { get; }
    internal TrustedRootHashInput? TrustedRootHashInput { get; }
    internal NormalCombatRewardProjectionRequest CombatRewardProjectionRequest { get; }

    public SeedPredictionRequest WithComplexBonesDeckInteractions(bool enabled) => new(
        RequestId,
        OriginalSeed,
        Character,
        Ascension,
        PlayersCount,
        PlayerSlotIndex,
        Authority,
        AncientOptionConditions,
        Domains,
        RelicSequencePreviewCount,
        IncludeDiagnostics,
        enabled,
        TrustedRootHashInput,
        CombatRewardProjectionRequest) { PartyOpeningChoices = PartyOpeningChoices, WorldProjection = WorldProjection, PreparedRewardPools = PreparedRewardPools };

    public bool Includes(SeedPredictionDomainSelection domain) => (Domains & domain) == domain;

    // Reuses immutable captured authority for a UI-only Ancient premise revision.
    // It is a new request, while the independent opening/world observations remain reusable.
    internal SeedPredictionRequest WithAncientOptionConditions(AncientOptionConditionProfile conditions) => new(
        PredictionRequestId.Create(), OriginalSeed, Character, Ascension, PlayersCount, PlayerSlotIndex,
        Authority, conditions, Domains, RelicSequencePreviewCount, IncludeDiagnostics,
        EnableComplexBonesDeckInteractions, TrustedRootHashInput, CombatRewardProjectionRequest)
        { PartyOpeningChoices = PartyOpeningChoices, WorldProjection = WorldProjection, PreparedRewardPools = PreparedRewardPools };

    public static bool TryCreate(
        string? originalSeed,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot? authority,
        bool includeDiagnostics,
        out SeedPredictionRequest? request,
        out SeedPredictionRequestError error) =>
        TryCreate(
            originalSeed,
            character,
            ascension,
            playersCount,
            playerSlotIndex,
            authority,
            AncientOptionConditionProfile.BroadDefault,
            SeedPredictionDomainSelection.All,
            SeedPredictionInputLimits.DefaultRelicSequencePreviewCount,
            includeDiagnostics,
            out request,
            out error);

    public static bool TryCreate(
        string? originalSeed,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot? authority,
        AncientOptionConditionProfile? ancientOptionConditions,
        bool includeDiagnostics,
        out SeedPredictionRequest? request,
        out SeedPredictionRequestError error) =>
        TryCreate(
            originalSeed,
            character,
            ascension,
            playersCount,
            playerSlotIndex,
            authority,
            ancientOptionConditions,
            SeedPredictionDomainSelection.All,
            SeedPredictionInputLimits.DefaultRelicSequencePreviewCount,
            includeDiagnostics,
            out request,
            out error);

    public static bool TryCreate(
        string? originalSeed,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot? authority,
        AncientOptionConditionProfile? ancientOptionConditions,
        SeedPredictionDomainSelection domains,
        bool includeDiagnostics,
        out SeedPredictionRequest? request,
        out SeedPredictionRequestError error) =>
        TryCreate(
            originalSeed,
            character,
            ascension,
            playersCount,
            playerSlotIndex,
            authority,
            ancientOptionConditions,
            domains,
            SeedPredictionInputLimits.DefaultRelicSequencePreviewCount,
            includeDiagnostics,
            out request,
            out error);

    public static bool TryCreate(
        string? originalSeed,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot? authority,
        AncientOptionConditionProfile? ancientOptionConditions,
        SeedPredictionDomainSelection domains,
        int relicSequencePreviewCount,
        bool includeDiagnostics,
        out SeedPredictionRequest? request,
        out SeedPredictionRequestError error)
    {
        string seed = originalSeed?.Trim() ?? string.Empty;
        if (seed.Length == 0)
        {
            request = null;
            error = SeedPredictionRequestError.MissingSeed;
            return false;
        }

        if (!character.IsValid)
        {
            request = null;
            error = SeedPredictionRequestError.MissingCharacter;
            return false;
        }

        if (ascension is < SeedPredictionInputLimits.MinimumAscension or > SeedPredictionInputLimits.MaximumAscension)
        {
            request = null;
            error = SeedPredictionRequestError.AscensionOutOfRange;
            return false;
        }

        if (playersCount is < SeedPredictionInputLimits.MinimumPlayers or > SeedPredictionInputLimits.MaximumPlayers)
        {
            request = null;
            error = SeedPredictionRequestError.PlayersCountOutOfRange;
            return false;
        }

        if (playerSlotIndex < 0 || playerSlotIndex >= playersCount)
        {
            request = null;
            error = SeedPredictionRequestError.PlayerSlotOutOfRange;
            return false;
        }


        if (relicSequencePreviewCount is < 1 or > SeedPredictionInputLimits.MaximumRelicSequencePreviewCount)
        {
            request = null;
            error = SeedPredictionRequestError.RelicSequencePreviewCountOutOfRange;
            return false;
        }

        if (authority is null)
        {
            request = null;
            error = SeedPredictionRequestError.MissingAuthority;
            return false;
        }

        if (authority.Character != character ||
            authority.Ascension != ascension ||
            authority.PlayersCount != playersCount ||
            authority.PlayerSlotIndex != playerSlotIndex)
        {
            request = null;
            error = SeedPredictionRequestError.AuthorityContextMismatch;
            return false;
        }

        if (string.IsNullOrWhiteSpace(authority.UnlockSnapshotFingerprint) ||
            string.IsNullOrWhiteSpace(authority.CatalogFingerprint))
        {
            request = null;
            error = SeedPredictionRequestError.MissingSnapshotFingerprint;
            return false;
        }

        request = new SeedPredictionRequest(
            PredictionRequestId.Create(),
            seed,
            character,
            ascension,
            playersCount,
            playerSlotIndex,
            authority,
            ancientOptionConditions ?? AncientOptionConditionProfile.BroadDefault,
            domains == SeedPredictionDomainSelection.None ? SeedPredictionDomainSelection.Neow : domains,
            relicSequencePreviewCount,
            includeDiagnostics,
            enableComplexBonesDeckInteractions: false,
            trustedRootHashInput: null,
            NormalCombatRewardProjectionRequest.RichAnalysis);
        error = SeedPredictionRequestError.None;
        return true;
    }

    internal static bool TryCreateFromRootHash(
        TrustedRootHashInput trustedRootHashInput,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot? authority,
        AncientOptionConditionProfile? ancientOptionConditions,
        SeedPredictionDomainSelection domains,
        int relicSequencePreviewCount,
        bool includeDiagnostics,
        out SeedPredictionRequest? request,
        out SeedPredictionRequestError error) =>
        TryCreateFromRootHash(
            trustedRootHashInput,
            character,
            ascension,
            playersCount,
            playerSlotIndex,
            authority,
            ancientOptionConditions,
            domains,
            relicSequencePreviewCount,
            includeDiagnostics,
            NormalCombatRewardProjectionRequest.RichAnalysis,
            out request,
            out error);

    internal static bool TryCreateFromRootHash(
        TrustedRootHashInput trustedRootHashInput,
        CharacterIdentity character,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        RuntimeContextAuthoritySnapshot? authority,
        AncientOptionConditionProfile? ancientOptionConditions,
        SeedPredictionDomainSelection domains,
        int relicSequencePreviewCount,
        bool includeDiagnostics,
        NormalCombatRewardProjectionRequest normalCombatRewardProjectionRequest,
        out SeedPredictionRequest? request,
        out SeedPredictionRequestError error)
    {
        if (!character.IsValid)
        {
            request = null;
            error = SeedPredictionRequestError.MissingCharacter;
            return false;
        }
        if (ascension is < SeedPredictionInputLimits.MinimumAscension or > SeedPredictionInputLimits.MaximumAscension)
        {
            request = null;
            error = SeedPredictionRequestError.AscensionOutOfRange;
            return false;
        }
        if (playersCount is < SeedPredictionInputLimits.MinimumPlayers or > SeedPredictionInputLimits.MaximumPlayers)
        {
            request = null;
            error = SeedPredictionRequestError.PlayersCountOutOfRange;
            return false;
        }
        if (playerSlotIndex < 0 || playerSlotIndex >= playersCount)
        {
            request = null;
            error = SeedPredictionRequestError.PlayerSlotOutOfRange;
            return false;
        }
        if (relicSequencePreviewCount is < 1 or > SeedPredictionInputLimits.MaximumRelicSequencePreviewCount)
        {
            request = null;
            error = SeedPredictionRequestError.RelicSequencePreviewCountOutOfRange;
            return false;
        }
        if (authority is null)
        {
            request = null;
            error = SeedPredictionRequestError.MissingAuthority;
            return false;
        }
        if (authority.Character != character ||
            authority.Ascension != ascension ||
            authority.PlayersCount != playersCount ||
            authority.PlayerSlotIndex != playerSlotIndex)
        {
            request = null;
            error = SeedPredictionRequestError.AuthorityContextMismatch;
            return false;
        }
        if (string.IsNullOrWhiteSpace(authority.UnlockSnapshotFingerprint) ||
            string.IsNullOrWhiteSpace(authority.CatalogFingerprint))
        {
            request = null;
            error = SeedPredictionRequestError.MissingSnapshotFingerprint;
            return false;
        }

        request = new SeedPredictionRequest(
            PredictionRequestId.Create(),
            trustedRootHashInput.SeedIdentity,
            character,
            ascension,
            playersCount,
            playerSlotIndex,
            authority,
            ancientOptionConditions ?? AncientOptionConditionProfile.BroadDefault,
            domains == SeedPredictionDomainSelection.None ? SeedPredictionDomainSelection.Neow : domains,
            relicSequencePreviewCount,
            includeDiagnostics,
            enableComplexBonesDeckInteractions: false,
            trustedRootHashInput,
            normalCombatRewardProjectionRequest);
        error = SeedPredictionRequestError.None;
        return true;
    }
}
