using System.Numerics;
using System.Runtime.Intrinsics;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Rng;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;

int assertions = 0, drawVectors = 0, mapVectors = 0, executions = 0;
// The default invocation checks a normal release DLL. When binding a profiling
// DLL, explicitly pass --diagnostic-control to check its 0/1 A/B override.
bool diagnosticControl = args.Contains("--diagnostic-control", StringComparer.Ordinal);
void Require(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidDataException(message);
}
ulong state = 0x9ee34a19b75c021dUL;
ulong Random64() { state ^= state << 13; state ^= state >> 7; state ^= state << 17; return state; }
const ulong mantissaSpace = 1UL << 53;
const double unit = 1.1102230246251565E-16;
string? previousBatchOverride = Environment.GetEnvironmentVariable("RT2_CPU_BATCH_N");
try
{
    foreach (string? value in new string?[] { null, "1", "0" })
    {
        Environment.SetEnvironmentVariable("RT2_CPU_BATCH_N", value);
        bool expected = !diagnosticControl || value != "0";
        Require(CpuNeowBatchGate.Requested == expected, "Default/diagnostic request policy differs from the selected build.");
        Require(CpuNeowBatchGate.RequestedFor(SearchOptimizationSettings.AllEnabled) == expected,
            "The all-enabled default did not preserve the request policy.");
        Require(!CpuNeowBatchGate.RequestedFor(SearchOptimizationSettings.AllEnabled with { PrefixCache = false }),
            "Disabling prefix caching still requested the batch gate.");
        Require(!CpuNeowBatchGate.RequestedFor(SearchOptimizationSettings.AllEnabled with { CostOrderedFilters = false }),
            "Disabling cost-ordered filters still requested the batch gate.");
        Require(!CpuNeowBatchGate.RequestedFor(SearchOptimizationSettings.AllDisabled),
            "All-disabled settings still requested the batch gate.");
    }
}
finally { Environment.SetEnvironmentVariable("RT2_CPU_BATCH_N", previousBatchOverride); }
if (CpuNeowBatchGate.HardwareSupported)
{
    foreach (ulong offset in new[] { 0UL, XxHash64.Hash("NEOW"u8, 0), ulong.MaxValue })
    for (int vector = 0; vector < 8192; vector++)
    {
        ulong[] roots = vector == 0 ? [0, 1, ulong.MaxValue - 1, ulong.MaxValue] :
            [Random64(), Random64(), Random64(), Random64()];
        var actual = CpuNeowBatchGate.FirstDraws(Vector256.Create(roots[0], roots[1], roots[2], roots[3]), offset);
        for (int lane = 0; lane < 4; lane++)
            Require(actual.GetElement(lane) == new Xoshiro256StarStar(unchecked(roots[lane] + offset)).NextUInt64(),
                "SIMD first draw differs from the independent full four-word CPU RNG.");
        drawVectors++;
    }
    // Exact CPU floating semantics at every small-bound bucket boundary,
    // including mantissas for which integer-floor multiplication disagrees.
    int roundedBoundaryDifferences = 0;
    for (int bound = 1; bound <= 64; bound++)
    {
        var values = new List<ulong> { 0, 1, (1UL << 52) - 1, 1UL << 52, mantissaSpace - 2, mantissaSpace - 1 };
        for (int rank = 0; rank <= bound; rank++)
        {
            ulong edge = (ulong)(((UInt128)(uint)rank * mantissaSpace) / (uint)bound);
            for (int delta = -3; delta <= 3; delta++)
            {
                Int128 value = (Int128)edge + delta;
                if (value >= 0 && value < mantissaSpace) values.Add((ulong)value);
            }
        }
        for (int n = 0; n < 512; n++) values.Add(Random64() >> 11);
        while (values.Count % 4 != 0) values.Add(0);
        for (int at = 0; at < values.Count; at += 4)
        {
            var vector = Vector256.Create(values[at], values[at + 1], values[at + 2], values[at + 3]);
            var actual = CpuNeowBatchGate.MapMantissas(vector, bound);
            for (int lane = 0; lane < 4; lane++)
            {
                int expected = (int)(((double)values[at + lane] * unit) * bound);
                int integerFloor = (int)(((UInt128)values[at + lane] * (uint)bound) >> 53);
                roundedBoundaryDifferences += expected != integerFloor ? 1 : 0;
                Require(actual.GetElement(lane) == expected, "SIMD bounded mapping changed CPU double rounding.");
            }
            mapVectors++;
        }
    }
    Require(roundedBoundaryDifferences > 0, "The boundary fixture failed to distinguish CPU rounding from integer floor.");
    Console.WriteLine(JsonSerializer.Serialize(new { phase = "simdPrimitives", result = "PASS", drawVectors, mapVectors,
        roundedBoundaryDifferences, fourIndependentRoots = true, fullFourWordCpuOracle = true, rawU64DrawParity = true }));
}

var authority = new Beta110FastNeowAuthority(0, 1, 10, true, true, false,
    Enumerable.Range(1, 28).Select(i => (byte)i).ToArray(), Enumerable.Range(0, 10).Select(i => (byte)i).ToArray(),
    true, true, true, null!, "CpuNeowBatchChecks");
var plan = new NeowReplayPlan(authority, NeowSearchFilter.Empty, Beta110FastDomain.None, [], [], [],
    0, 0, 0, 0, (1UL << 14) | (1UL << 28), 0, Beta110FastRelicCatalog.NeowsBones,
    byte.MaxValue, byte.MaxValue, true, true, false, []) { DirectNestedVanilla111 = true };
var scalarPlan = NeowIdentityCpuPlan.TryCompile(plan);
Require(scalarPlan is not null, "The independent scalar identity matcher was not admitted.");
CpuNeowBatchGate? gate;
try
{
    Environment.SetEnvironmentVariable("RT2_CPU_BATCH_N", null);
    gate = CpuNeowBatchGate.TryCreate(plan, 1UL << Beta110FastRelicCatalog.NeowsBones);
    Require(CpuNeowBatchGate.Requested, "An unset override did not enable the release default.");
}
finally { Environment.SetEnvironmentVariable("RT2_CPU_BATCH_N", previousBatchOverride); }
Require((gate is not null) == CpuNeowBatchGate.HardwareSupported, "Hardware admission or forced scalar fallback is wrong.");
Require(CpuNeowBatchGate.TryCreate(plan, 1UL << 5, requested: false) is null, "The disabled control activated SIMD.");
Require(CpuNeowBatchGate.TryCreate(plan with { Authority = authority with { PlayersCount = 2 } }, 1UL << 5, true) is null,
    "Multiplayer entered the single-player necessary gate.");
Require(CpuNeowBatchGate.TryCreate(plan with { DirectNestedVanilla111 = false }, 1UL << 5, true) is null,
    "An unknown replay shape entered the batch gate.");
Require(CpuNeowBatchGate.TryCreate(plan with { Authority = authority with { PlayerSlotIndex = 1 } }, 1UL << 5, true) is null &&
    CpuNeowBatchGate.TryCreate(plan with { Authority = authority with { IdentityAuthorityExact = false } }, 1UL << 5, true) is null,
    "An unaudited slot/authority entered the necessary gate.");
Require(CpuNeowBatchGate.TryCreateForRanks(0, 65, 1, true) is null &&
    CpuNeowBatchGate.TryCreateForRanks(0, 10, 1UL << 20, true) is null, "Invalid rank geometry entered SIMD.");

Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
var inputs = new List<FamilyCandidateSet>();
foreach (var (start, count) in new (ulong, int)[] { (0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 7),
    (zzfz, 8193), (PrefixHashCache.BlockSize - 3, 32771), (PrefixHashCache.SpaceSize - 8193, 8193), (0, 131075) })
    inputs.Add(FamilyCandidateSet.Dense(new SearchBatch(start, count)));
var sparseBatch = new SearchBatch(zzfz, 20003);
inputs.Add(FamilyCandidateSet.FromAbi1(sparseBatch, Enumerable.Range(0, 20003).Where(i => i % 7 == 1).Select(i => (ulong)i).ToArray()));
int positive = 0;
foreach (var input in inputs)
{
    ulong[] expected = input.EnumerateLogicalOrdinals().Where(ordinal => scalarPlan!.Matches(Oracle(input.Batch.GlobalCandidate(ordinal)))).ToArray();
    positive += expected.Length;
    foreach (bool prefix in new[] { false, true })
    foreach (bool costOrdered in new[] { false, true })
    foreach (int workers in new[] { 1, 4, 32 })
    {
        var request = Request(prefix, workers, costOrdered);
        var cpu = new FamilyCpuExecution(request, "N.Check", "N.Check.v1", workers, numericalRoots: true, batchNeowGate: gate);
        var actual = cpu.Execute(new(input.Batch, request), input, CancellationToken.None, scalarPlan!.Matches);
        Require(actual.Batch == input.Batch && (actual.Count == 0 && expected.Length == 0 ||
            !actual.IsDense && actual.ExportAbi1().Span.SequenceEqual(expected)), "Batch predicate changed full N result/order.");
        Require(cpu.Workers > 0 && cpu.Workers <= workers && cpu.Workers <= Environment.ProcessorCount,
            "Batch execution exceeded the existing requested/resource CPU worker budget.");
        bool expectedIdentity = gate is not null && prefix && costOrdered && input.IsDense;
        Require(cpu.Condition(!input.IsDense).PhysicalImplementationRevision.Contains(CpuNeowBatchGate.Revision) == expectedIdentity,
            "Physical identity did not describe the admitted dense-only path.");
        if (gate is not null && prefix && costOrdered && input.IsDense && input.Count >= 8193)
            Require(cpu.BatchNeowBlocks > 0, "A supposed SIMD execution did not process any actual SIMD block.");
        if (!prefix || !costOrdered || !input.IsDense)
            Require(cpu.BatchNeowBlocks == 0, "A scalar fallback executed the dense SIMD gate.");
        Require(!cpu.LocalPeaks.Any(), "SIMD samples contaminated legacy CPU calibration.");
        executions++;
    }
}
Require(positive > 0, "All full N fixtures were empty; positive identity acceptance was not exercised.");
var cancelInput = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 20003));
var cancelRequest = Request(true, 4);
var cancelCpu = new FamilyCpuExecution(cancelRequest, "N.Cancel", "N.Cancel.v1", 4, numericalRoots: true, batchNeowGate: gate);
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { cancelCpu.Execute(new(cancelInput.Batch, cancelRequest), cancelInput, cancelled.Token, scalarPlan!.Matches); throw new InvalidDataException("Pre-cancel returned a result."); }
    catch (OperationCanceledException) { assertions++; }
}
using (var cancelled = new CancellationTokenSource())
{
    int visits = 0;
    try
    {
        cancelCpu.Execute(new(cancelInput.Batch, cancelRequest), cancelInput, cancelled.Token, root =>
        { if (Interlocked.Increment(ref visits) == 31) cancelled.Cancel(); return scalarPlan!.Matches(root); });
        throw new InvalidDataException("Cancellation published a partial SIMD result.");
    }
    catch (OperationCanceledException) { assertions++; }
}
var recovered = cancelCpu.Execute(new(cancelInput.Batch, cancelRequest), cancelInput, CancellationToken.None, scalarPlan!.Matches);
Require(recovered.ExportAbi1().Span.SequenceEqual(cancelInput.EnumerateLogicalOrdinals()
    .Where(o => scalarPlan!.Matches(Oracle(cancelInput.Batch.GlobalCandidate(o)))).ToArray()), "Cancellation reuse changed the resumed range.");
Console.WriteLine(JsonSerializer.Serialize(new { phase = "cpuNeowBatch", result = "PASS", hardwareSupported = CpuNeowBatchGate.HardwareSupported,
    executions, positive, denseAndSparse = true, tails = true, zzfzAndPrefixCrossing = true, finalSeed = true,
    cancellationAndReuse = true, resourceWorkerBudgetPreserved = true, legacyCalibrationExcluded = true,
    defaultEnabled = true, existingSwitchFallbacks = true, diagnosticControl, assertions }));

static ulong Oracle(ulong ordinal) => Beta111Profile.Instance.ComputeRootSeed(Beta110SeedCodec.FormatOrdinal(ordinal));
static ExactSearchExecutionRequest Request(bool prefix, int workers, bool costOrdered = true)
{
    var detection = new GameVersionDetection("0.111.0", "0.111.0", "beta", "CpuNeowBatchChecks", true, "");
    var context = new SearchContext(RuntimeProfileId.Beta111, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
    var compiled = new CompiledSearch(SearchQuery.Empty, context,
        new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
        new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "CpuNeowBatchChecks")), false, "CpuNeowBatchChecks");
    var options = new SearchRunOptions("000000000000", 131075, 1, workers)
    { Resources = new SearchResourceBudget(100, 0),
      Optimizations = SearchOptimizationSettings.AllEnabled with { PrefixCache = prefix, CostOrderedFilters = costOrdered } };
    return new(compiled, options, "000000000000", 131075, ExactSearchEvaluationProjection.Empty,
        CombatRewardRoutePolicyContract.None, "CpuNeowBatchChecks");
}
