using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Effects.Snapshots;
using RolltheSpire2.Core.World.Snapshots;

namespace RolltheSpire2.Core.Rewards;

/// <summary>
/// One Exact plan's owned card/potion snapshot and seed-independent reward-pool
/// metadata. No process-wide cache and no live game/model references are kept.
/// The compiler installs OwnedAuthority into that plan before it can be used.
/// </summary>
internal sealed class PreparedCombatRewardPools
{
    internal const string Revision = "ExactRewardOwnedPools.PreparedOrderValidationFingerprints.20261026.v1";
    private readonly RuntimeProfileId _profileId;
    private readonly bool _multiplayer;

    private PreparedCombatRewardPools(RuntimeProfileId profileId, bool multiplayer,
        NeowEffectAuthoritySnapshot ownedAuthority,
        IReadOnlyList<NeowEffectCardSnapshot> cards, IReadOnlyList<NeowEffectPotionSnapshot> potions)
    {
        _profileId = profileId;
        _multiplayer = multiplayer;
        OwnedAuthority = ownedAuthority;
        Cards = cards;
        Potions = potions;
        CardPoolFingerprint = NormalCombatRewardSequencePredictor.BuildCardPoolFingerprint(cards);
        PotionPoolFingerprint = NormalCombatRewardSequencePredictor.BuildPotionPoolFingerprint(potions);
    }

    internal NeowEffectAuthoritySnapshot OwnedAuthority { get; }
    internal IReadOnlyList<NeowEffectCardSnapshot> Cards { get; }
    internal IReadOnlyList<NeowEffectPotionSnapshot> Potions { get; }
    internal string CardPoolFingerprint { get; }
    internal string PotionPoolFingerprint { get; }

    internal static PreparedCombatRewardPools? TryCreate(RuntimeProfileId profileId,
        NeowEffectAuthoritySnapshot? authority, WorldAuthoritySnapshot? worldAuthority)
    {
        if (profileId is not (RuntimeProfileId.Beta110 or RuntimeProfileId.Beta111) ||
            authority is null || authority.CapturedProfileId != profileId || !authority.HasExactFoundation ||
            !authority.CharacterRewardPoolExact || !authority.PotionPoolExact ||
            authority.CharacterRewardPool is not { } sourceCards || authority.PotionPool is not { } sourcePotions)
            return null;

        // IReadOnlyList alone is not an immutability proof. Detach from callers'
        // lists/arrays, and expose only wrappers that reject replacement/addition.
        var owned = authority with
        {
            CharacterRewardPool = Array.AsReadOnly(sourceCards.ToArray()),
            PotionPool = Array.AsReadOnly(sourcePotions.ToArray())
        };
        bool multiplayer = worldAuthority?.Beta109Generation?.IsMultiplayer == true;
        var cards = Array.AsReadOnly(owned.CharacterRewardPool
            .Where(card => multiplayer || !card.IsMultiplayerOnly)
            .Where(card => card.EligibleForPostCombatRewardByPoolMembership)
            .Where(card => card.IsUnlockedInCapturedPool)
            .Where(card => card.Rarity is EffectCardRarity.Common or EffectCardRarity.Uncommon or EffectCardRarity.Rare)
            .OrderBy(card => card.PoolOrder).ToArray());
        var potions = Array.AsReadOnly(owned.PotionPool
            .Where(potion => multiplayer || !potion.IsMultiplayerOnly)
            .Where(potion => potion.Rarity is EffectPotionRarity.Common or EffectPotionRarity.Uncommon or EffectPotionRarity.Rare)
            .OrderBy(potion => potion.PoolOrder).ToArray());
        // Preserve the old failure/Unknown ordering: bad pools take the old path
        // and are rejected by its existing validator at its existing boundary.
        if (NormalCombatRewardSequencePredictor.ValidatePools(profileId, cards, potions) is not null)
            return null;
        return new(profileId, multiplayer, owned, cards, potions);
    }

    internal bool IsCompatible(RuntimeProfileId profileId, NeowEffectAuthoritySnapshot authority,
        WorldAuthoritySnapshot? worldAuthority) =>
        profileId == _profileId && authority.CapturedProfileId == _profileId &&
        (worldAuthority?.Beta109Generation?.IsMultiplayer == true) == _multiplayer &&
        ReferenceEquals(authority.CharacterRewardPool, OwnedAuthority.CharacterRewardPool) &&
        ReferenceEquals(authority.PotionPool, OwnedAuthority.PotionPool);
}
