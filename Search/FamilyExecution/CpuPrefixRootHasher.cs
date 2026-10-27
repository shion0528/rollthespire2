using System.Runtime.CompilerServices;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Partition-local root hashing for the audited 12-character visible seed space.
/// This value owns no RNG state and accepts actual global ordinals, including
/// sparse inputs and resumed ranges that do not start at a suffix boundary.
/// </summary>
internal struct CpuPrefixRootHasher
{
    internal const string Revision = "CpuPrefixHash34Pow4.20261023.v1";
    private bool _initialized;
    private ulong _lastOrdinal, _block, _prefixHash;
    private uint _suffix, _tail4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong ComputeRoot(ulong globalOrdinal)
    {
        if (globalOrdinal >= PrefixHashCache.SpaceSize)
            throw new ArgumentOutOfRangeException(nameof(globalOrdinal));
        if (_initialized && globalOrdinal == _lastOrdinal + 1)
        {
            if (++_suffix == PrefixHashCache.BlockSize)
            {
                _suffix = 0;
                _block++;
                _prefixHash = PrefixHashCache.ComputePrefixHash(PrefixHashCache.EncodeFirst8(_block));
                _tail4 = 0x30303030u;
            }
            else _tail4 = AdvanceTail4(_tail4);
        }
        else
        {
            ulong block = globalOrdinal / PrefixHashCache.BlockSize;
            if (!_initialized || block != _block)
            {
                _block = block;
                _prefixHash = PrefixHashCache.ComputePrefixHash(PrefixHashCache.EncodeFirst8(block));
            }
            _suffix = (uint)(globalOrdinal - block * PrefixHashCache.BlockSize);
            _tail4 = PrefixHashCache.EncodeTail4(_suffix);
            _initialized = true;
        }
        _lastOrdinal = globalOrdinal;
        return PrefixHashCache.ComputeRootHashFromPrefix(_prefixHash, _tail4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AdvanceTail4(uint packed)
    {
        // Packed xxHash input is little endian: the last visible character is
        // the highest byte. Only a trailing Z carries into its predecessor.
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            uint previous = (packed >> shift) & 255u;
            uint next = previous switch
            {
                (uint)'9' => 'A', (uint)'H' => 'J', (uint)'N' => 'P',
                (uint)'Z' => '0', _ => previous + 1
            };
            packed = (packed & ~(255u << shift)) | (next << shift);
            if (previous != 'Z') break;
        }
        return packed;
    }
}
