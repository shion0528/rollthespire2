using System.Globalization;

namespace RolltheSpire2.Search.FamilyExecution;

// A query-fixed pool predicate over the exact integer mantissa consumed by
// next_int_current_shortcut_from_mantissa. No random distribution approximation.
internal static class StaticDrawGateCompiler
{
    internal const ulong MantissaSpace = 1UL << 53;
    internal readonly record struct Interval(ulong LowerInclusive, ulong UpperExclusive);

    internal sealed class Gate(int bound, Interval[] intervals)
    {
        internal int Bound { get; } = bound;
        internal IReadOnlyList<Interval> Intervals { get; } = intervals;
        internal bool Contains(ulong mantissa) => mantissa < MantissaSpace &&
            Intervals.Any(i => mantissa >= i.LowerInclusive && mantissa < i.UpperExclusive);

        internal string GlslExpression(string value)
        {
            if (Intervals.Count == 0) return "false";
            return string.Join(" || ", Intervals.Select(i =>
                i.LowerInclusive == 0 && i.UpperExclusive == MantissaSpace ? "true" :
                i.LowerInclusive == 0 ? $"({value} < {U64(i.UpperExclusive)})" :
                i.UpperExclusive == MantissaSpace ? $"({value} >= {U64(i.LowerInclusive)})" :
                $"({value} >= {U64(i.LowerInclusive)} && {value} < {U64(i.UpperExclusive)})"));
        }
    }

    // ceil(rank * 2^53 / bound). UInt128 also makes large legal pool lengths
    // safe; doing rank * MantissaSpace in UInt64 would silently overflow.
    internal static ulong Boundary(int rank, int bound)
    {
        if (bound <= 0 || rank < 0 || rank > bound) throw new ArgumentOutOfRangeException(nameof(rank));
        return checked((ulong)((((UInt128)(uint)rank << 53) + (uint)bound - 1u) / (uint)bound));
    }

    internal static bool TryCompilePool(IReadOnlyList<uint> pool, int length,
        IReadOnlyCollection<uint> targets, int maximumIntervals, out Gate? gate)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(targets);
        if (length <= 0 || length > pool.Count) { gate = null; return false; }
        var accepted = targets.ToHashSet();
        // Every matching position is retained, including repeated IDs.
        return TryCompileRanks(length, i => accepted.Contains(pool[i]), maximumIntervals, out gate);
    }

    internal static bool TryCompileRanks(int bound, Func<int, bool> accepted,
        int maximumIntervals, out Gate? gate)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        gate = null;
        if (bound <= 0 || maximumIntervals <= 0) return false;
        var intervals = new List<Interval>();
        int first = -1;
        for (int rank = 0; rank < bound; rank++)
        {
            bool match = accepted(rank);
            if (match && first < 0) first = rank;
            if (first >= 0 && (!match || rank == bound - 1))
            {
                int end = match ? rank + 1 : rank;
                intervals.Add(new(Boundary(first, bound), Boundary(end, bound)));
                if (intervals.Count > maximumIntervals) return false;
                first = -1;
            }
        }
        gate = new(bound, intervals.ToArray());
        return true;
    }

    private static string U64(ulong value) => "make_u64(0x" + ((uint)value).ToString("x8", CultureInfo.InvariantCulture) +
        "u,0x" + ((uint)(value >> 32)).ToString("x8", CultureInfo.InvariantCulture) + "u)";
}
