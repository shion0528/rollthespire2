using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

// A necessary N identity predicate, never a replacement for the selected N
// matcher or Production Exact. Four vector lanes own four independent roots.
// No state crosses ABI1, a worker partition, a route or a resumed search.
internal sealed class CpuNeowBatchGate
{
    internal const string Revision = "CpuNeowCurseBatch4.Avx2.ExactDouble53.20261025.v1";
    private const ulong Increment = 11400714819323198485UL;
    private const double DoubleUnit = 1.1102230246251565E-16;
    private static readonly ulong EventHash = XxHash64.Hash("NEOW"u8, 0);
    private readonly ulong _offset, _acceptedRanks;
    private readonly int _bound;

    internal static bool HardwareSupported => Avx2.IsSupported;
    internal static bool Requested
    {
        get
        {
#if RT2_GPU_PROFILING
            // A diagnostic process may explicitly reproduce the scalar control.
            // An unset override has exactly the same default as the release.
            return System.Environment.GetEnvironmentVariable("RT2_CPU_BATCH_N") != "0";
#else
            return true;
#endif
        }
    }
    internal static bool RequestedFor(SearchOptimizationSettings optimizations) =>
        Requested && optimizations.PrefixCache && optimizations.CostOrderedFilters;

    private CpuNeowBatchGate(ulong offset, int bound, ulong acceptedRanks)
    { _offset = offset; _bound = bound; _acceptedRanks = acceptedRanks; }

    internal static CpuNeowBatchGate? TryCreate(NeowReplayPlan plan, ulong allowedCurses, bool? requested = null)
    {
        if (!(requested ?? Requested) || !plan.DirectNestedVanilla111 || plan.Authority.PlayersCount != 1 ||
            plan.Authority.PlayerSlotIndex != 0 || !plan.Authority.IdentityAuthorityExact) return null;
        var pool = plan.Authority.EligibleCurseRelicIds;
        if (pool.Length is < 1 or > 64 || pool.Any(id => id >= 64)) return null;
        ulong ranks = 0;
        for (int i = 0; i < pool.Length; i++)
            if ((allowedCurses & (1UL << pool[i])) != 0) ranks |= 1UL << i;
        return TryCreateForRanks(EventHash, pool.Length, ranks, requested: true);
    }

    // Explicit constructor control is only for independent CPU checks. Runtime
    // callers use the audited immutable curse pool and captured run settings.
    internal static CpuNeowBatchGate? TryCreateForRanks(ulong offset, int bound,
        ulong acceptedRanks, bool requested)
    {
        if (!requested || !HardwareSupported || bound is < 1 or > 64) return null;
        ulong legal = bound == 64 ? ulong.MaxValue : (1UL << bound) - 1;
        if ((acceptedRanks & ~legal) != 0 || acceptedRanks == legal) return null;
        return new(offset, bound, acceptedRanks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int MatchesFour(ulong a, ulong b, ulong c, ulong d)
    {
        var draws = FirstDraws(Vector256.Create(a, b, c, d), _offset);
        var ranks = MapMantissas(Avx2.ShiftRightLogical(draws, 11), _bound);
        int mask = 0;
        for (int lane = 0; lane < 4; lane++)
            if ((_acceptedRanks & (1UL << ranks.GetElement(lane))) != 0) mask |= 1 << lane;
        return mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<ulong> FirstDraws(Vector256<ulong> roots, ulong offset)
    {
        // Xoshiro256**'s first result needs only initial s1, which is the
        // second SplitMix output. Its input is seed + 2*Increment modulo 2^64.
        var x = Avx2.Add(roots, Vector256.Create(unchecked(offset + 2 * Increment)));
        x = MultiplyLow64(Avx2.Xor(x, Avx2.ShiftRightLogical(x, 30)), 13787848793156543929UL);
        x = MultiplyLow64(Avx2.Xor(x, Avx2.ShiftRightLogical(x, 27)), 10723151780598845931UL);
        x = Avx2.Xor(x, Avx2.ShiftRightLogical(x, 31));
        x = Avx2.Add(x, Avx2.ShiftLeftLogical(x, 2)); // *5 modulo 2^64
        x = Avx2.Or(Avx2.ShiftLeftLogical(x, 7), Avx2.ShiftRightLogical(x, 57));
        return Avx2.Add(x, Avx2.ShiftLeftLogical(x, 3)); // *9 modulo 2^64
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> MultiplyLow64(Vector256<ulong> x, ulong factor)
    {
        // Three exact 32x32->64 vector products reconstruct the low 64 bits.
        // There is no ISA requirement beyond AVX2 and no native dependency.
        var low = Avx2.Multiply(x.AsUInt32(), Vector256.Create((uint)factor));
        var crossA = Avx2.Multiply(x.AsUInt32(), Vector256.Create((uint)(factor >> 32)));
        var crossB = Avx2.Multiply(Avx2.ShiftRightLogical(x, 32).AsUInt32(), Vector256.Create((uint)factor));
        return Avx2.Add(low, Avx2.ShiftLeftLogical(Avx2.Add(crossA, crossB), 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> MapMantissas(Vector256<ulong> mantissas, int bound)
    {
        if (bound is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(bound));
        // All 53-bit integers are represented exactly in binary64. Split into
        // 52+1 bits and use exponent injection to avoid an approximate unsigned
        // conversion. Keep the same TWO rounded multiplications as NextInt:
        // ((double)mantissa * 2^-53) * bound. Never replace this with floor of an
        // integer product: bin boundaries can differ by one finite mantissa.
        var magicBits = Vector256.Create(0x4330000000000000UL);
        var magicDouble = Vector256.Create(4503599627370496d);
        var low = Avx.Subtract(Avx2.Or(Avx2.And(mantissas, Vector256.Create((1UL << 52) - 1)), magicBits).AsDouble(), magicDouble);
        var high = Avx.Subtract(Avx2.Or(Avx2.ShiftRightLogical(mantissas, 52), magicBits).AsDouble(), magicDouble);
        var integer = Avx.Add(low, Avx.Multiply(high, magicDouble));
        var unit = Avx.Multiply(integer, Vector256.Create(DoubleUnit));
        return Avx.ConvertToVector128Int32WithTruncation(Avx.Multiply(unit, Vector256.Create((double)bound)));
    }
}
