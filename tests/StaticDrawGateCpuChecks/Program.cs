using System.Numerics;
using System.Text.Json;
using RolltheSpire2.Search.FamilyExecution;

static void Require(bool ok, string issue)
{
    if (!ok) throw new InvalidDataException(issue);
}

ulong space = StaticDrawGateCompiler.MantissaSpace;
int boundaryCases = 0, membershipCases = 0, pairCases = 0;
for (int length = 1; length <= 128; length++)
for (int selected = 0; selected < length; selected++)
{
    Require(StaticDrawGateCompiler.TryCompileRanks(length, i => i == selected, 1, out var gate), "Rank compilation failed.");
    ulong lo = StaticDrawGateCompiler.Boundary(selected, length), hi = StaticDrawGateCompiler.Boundary(selected + 1, length);
    var probes = new HashSet<ulong> { 0, space - 1, lo, hi - 1 };
    if (lo != 0) probes.Add(lo - 1);
    if (hi != space) probes.Add(hi);
    foreach (ulong x in probes)
    {
        // Same limb operations as NeowSingleplayerDonor.glsl, with an
        // independent full-width integer oracle as a third comparison.
        ulong lowProduct = (uint)x * (ulong)(uint)length;
        uint rank = (uint)(((x >> 32) * (uint)length + (lowProduct >> 32)) >> 21);
        uint oracle = (uint)(((BigInteger)x * length) >> 53);
        Require(rank == oracle && gate!.Contains(x) == (rank == selected), "Boundary or production arithmetic mismatch.");
        boundaryCases++;
    }
}
foreach (int length in new[] { 1, 10, 64, 128, 65536, int.MaxValue })
foreach (int rank in new[] { 0, length / 2, length - 1, length }.Distinct())
    Require(StaticDrawGateCompiler.Boundary(rank, length) == (ulong)(((BigInteger)rank * space + length - 1) / length),
        "Large bound overflowed.");

uint[][] pools = [[100, 200, 100, 300, 200], [200, 100, 400, 200, 100], [100], [200], [100, 100], [9, 4, 9, 4, 9, 9, 4]];
foreach (uint[] pool in pools)
foreach (uint target in new uint[] { 100, 200, 9, 4, 999 })
{
    Require(StaticDrawGateCompiler.TryCompilePool(pool, pool.Length, [target], 16, out var gate), "Pool compilation failed.");
    for (int index = 0; index < pool.Length; index++)
    {
        ulong start = StaticDrawGateCompiler.Boundary(index, pool.Length);
        ulong end = StaticDrawGateCompiler.Boundary(index + 1, pool.Length) - 1;
        foreach (ulong value in new[] { start, end })
        {
            Require(gate!.Contains(value) == (pool[index] == target), "Repeated or missing pool ID changed membership.");
            membershipCases++;
        }
    }
}
foreach (uint[] strike in pools)
foreach (uint[] defend in pools)
foreach ((uint a, uint b) in new[] { (100u, 200u), (100u, 100u), (200u, 100u), (999u, 100u) })
{
    StaticDrawGateCompiler.TryCompilePool(strike, strike.Length, [a], 16, out var s0);
    StaticDrawGateCompiler.TryCompilePool(strike, strike.Length, [b], 16, out var s1);
    StaticDrawGateCompiler.TryCompilePool(defend, defend.Length, [a], 16, out var d0);
    StaticDrawGateCompiler.TryCompilePool(defend, defend.Length, [b], 16, out var d1);
    for (int i = 0; i < strike.Length; i++)
    for (int j = 0; j < defend.Length; j++)
    {
        ulong x = StaticDrawGateCompiler.Boundary(i, strike.Length), y = StaticDrawGateCompiler.Boundary(j, defend.Length);
        bool compiled = (s0!.Contains(x) && d1!.Contains(y)) || (s1!.Contains(x) && d0!.Contains(y));
        bool original = (strike[i] == a && defend[j] == b) || (strike[i] == b && defend[j] == a);
        Require(compiled == original, "Unordered pair correlation was lost.");
        pairCases++;
    }
}
Require(!StaticDrawGateCompiler.TryCompilePool(pools[^1], pools[^1].Length, [9u], 2, out _), "Too many intervals did not fail closed.");
Require(StaticDrawGateCompiler.TryCompilePool(pools[^1], pools[^1].Length, [9u], 3, out var merged) && merged!.Intervals.Count == 3,
    "Adjacent accepted positions were not merged.");
Require(StaticDrawGateCompiler.TryCompileRanks(10, _ => false, 2, out var none) && none!.GlslExpression("x") == "false" &&
    StaticDrawGateCompiler.TryCompileRanks(10, _ => true, 2, out var all) && all!.GlslExpression("x") == "true", "Constant gates changed.");
Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", productionCompiler = true, boundaryCases, membershipCases, pairCases,
    largeBoundChecked = true, duplicateIdsChecked = true, intervalBudgetFallback = true, gpuInvoked = false }));
