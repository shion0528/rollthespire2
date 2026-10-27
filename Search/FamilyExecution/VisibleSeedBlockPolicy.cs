namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Random starting seeds begin an eight-character prefix block. This policy
/// does not change candidate stride, manually chosen seeds, or saved cursors.
/// </summary>
internal static class VisibleSeedBlockPolicy
{
    internal const int RandomPrefixLength = 8;
    internal const int ZeroSuffixLength = 4;
    internal const ulong BlockSize = (ulong)Beta110SeedCodec.Radix * Beta110SeedCodec.Radix *
        Beta110SeedCodec.Radix * Beta110SeedCodec.Radix;
    internal static readonly ulong BlockCount = Beta110SeedCodec.SpaceSize / BlockSize;

    // Reject this short leading interval when sampling an unsigned random word,
    // so every prefix has the same number of accepted representations.
    internal static readonly ulong MinimumUnbiasedRandomValue = unchecked(0UL - BlockCount) % BlockCount;

    internal static ulong RandomOriginFromValue(ulong value) => (value % BlockCount) * BlockSize;
}
