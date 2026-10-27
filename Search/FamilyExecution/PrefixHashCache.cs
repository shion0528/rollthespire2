using System.Numerics;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>Small, immutable xxHash64 first-eight-byte tables for one Dense workspace.</summary>
internal static class PrefixHashCache
{
    internal const uint BlockSize = 1_336_336; // 34^4 suffixes per fixed first8.
    internal const ulong SpaceSize = 2_386_420_683_693_101_056; // 34^12.
    internal const ulong PrefixCount = 1_785_793_904_896; // 34^8.
    internal const int MaxCandidateCount = 1 << 24;
    internal const int HeaderWords = 4, RowWords = 4, MaximumRows = 14;
    internal const int MaximumTableWords = HeaderWords + RowWords * MaximumRows;
    private const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const ulong Prime1 = 11400714785074694791UL, Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL, Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    // binding13 uint ABI: [initial suffix ordinal, row count, reserved0, reserved0],
    // then rows [prefix hash lo, hi, first8 ASCII lo, hi]. A row's prefix hash is
    // the complete hash state immediately before the tail4 * Prime1 XOR.
    internal static uint[] BuildTable(ulong batchBase, int count)
    {
        if (count < 0 || count > MaxCandidateCount) throw new ArgumentOutOfRangeException(nameof(count));
        if (batchBase > SpaceSize || (ulong)count > SpaceSize - batchBase)
            throw new ArgumentOutOfRangeException(nameof(batchBase), "Dense prefix table exceeds the 12-character seed space.");
        uint suffix = (uint)(batchBase % BlockSize);
        int rows = count == 0 ? 0 : checked((int)((suffix + (uint)count - 1u) / BlockSize + 1u));
        if (rows > MaximumRows) throw new InvalidOperationException("PrefixHashCache.RowCapacity");
        uint[] words = new uint[HeaderWords + rows * RowWords];
        words[0] = suffix; words[1] = (uint)rows;
        ulong firstBlock = batchBase / BlockSize;
        for (int row = 0; row < rows; row++)
        {
            ulong first8 = EncodeFirst8(firstBlock + (ulong)row);
            ulong prefix = ComputePrefixHash(first8);
            int at = HeaderWords + row * RowWords;
            words[at] = (uint)prefix; words[at + 1] = (uint)(prefix >> 32);
            words[at + 2] = (uint)first8; words[at + 3] = (uint)(first8 >> 32);
        }
        return words;
    }

    internal static ulong EncodeFirst8(ulong blockOrdinal)
    {
        if (blockOrdinal >= PrefixCount) throw new ArgumentOutOfRangeException(nameof(blockOrdinal));
        ulong packed = 0;
        for (int index = 7; index >= 0; index--)
        {
            packed |= (ulong)Alphabet[(int)(blockOrdinal % 34)] << (index * 8);
            blockOrdinal /= 34;
        }
        return packed;
    }

    internal static uint EncodeTail4(uint suffixOrdinal)
    {
        if (suffixOrdinal >= BlockSize) throw new ArgumentOutOfRangeException(nameof(suffixOrdinal));
        uint packed = 0;
        for (int index = 3; index >= 0; index--)
        {
            packed |= (uint)Alphabet[(int)(suffixOrdinal % 34)] << (index * 8);
            suffixOrdinal /= 34;
        }
        return packed;
    }

    internal static ulong ComputePrefixHash(ulong first8)
    {
        unchecked
        {
            ulong round = BitOperations.RotateLeft(first8 * Prime2, 31) * Prime1;
            return BitOperations.RotateLeft((Prime5 + 12UL) ^ round, 27) * Prime1 + Prime4;
        }
    }

    internal static ulong ComputeRootHashFromPrefix(ulong prefixHash, uint tail4)
    {
        unchecked
        {
            ulong hash = prefixHash ^ ((ulong)tail4 * Prime1);
            hash = BitOperations.RotateLeft(hash, 23) * Prime2 + Prime3;
            hash ^= hash >> 33; hash *= Prime2;
            hash ^= hash >> 29; hash *= Prime3;
            return hash ^ (hash >> 32);
        }
    }
}
