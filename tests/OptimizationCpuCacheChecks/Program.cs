using System.Collections.Concurrent;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;

int assertions = 0, rootCases = 0, executionCases = 0;
assertions += HeaderRevisionChecks.Run();
void Require(bool condition, string message)
{
    Interlocked.Increment(ref assertions);
    if (!condition) throw new InvalidDataException(message);
}
static ulong Oracle(ulong ordinal) => Beta111Profile.Instance.ComputeRootSeed(
    VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, ordinal));
static bool Predicate(ulong root) => (root ^ (root >> 17)) % 11 < 3;
void Check(ref CpuPrefixRootHasher hasher, ulong ordinal)
{
    Require(hasher.ComputeRoot(ordinal) == Oracle(ordinal), "Cached root differs from the profile/string oracle: " + ordinal);
    rootCases++;
}

var hash = new CpuPrefixRootHasher();
ulong[] positions = [0, 1, 8, 9, 10, 17, 18, 22, 23, 32, 33, 34, 35,
    PrefixHashCache.BlockSize - 2, PrefixHashCache.BlockSize - 1, PrefixHashCache.BlockSize,
    PrefixHashCache.BlockSize + 1, PrefixHashCache.SpaceSize - 2, PrefixHashCache.SpaceSize - 1];
foreach (ulong ordinal in positions) Check(ref hash, ordinal);
Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
foreach (ulong start in new[] { 0UL, 8UL, 16UL, 31UL, zzfz, (ulong)PrefixHashCache.BlockSize - 513,
    PrefixHashCache.SpaceSize - 2049 })
for (int n = 0; n < 2049; n++) Check(ref hash, start + (ulong)n);
// Arbitrary jumps, repeated positions and reverse visits exercise compact input
// without assuming its indices are consecutive or equal to the dense cursor.
ulong random = 0x9a1c4e0b75aa7db3;
for (int n = 0; n < 100_000; n++)
{
    random ^= random << 13; random ^= random >> 7; random ^= random << 17;
    ulong ordinal = random % PrefixHashCache.SpaceSize;
    Check(ref hash, ordinal);
    if ((n & 255) == 0) { Check(ref hash, ordinal); Check(ref hash, ordinal == 0 ? 0 : ordinal - 1); }
}
foreach (ulong invalid in new[] { PrefixHashCache.SpaceSize, ulong.MaxValue })
{
    try { hash.ComputeRoot(invalid); throw new InvalidDataException("Out-of-space ordinal was accepted."); }
    catch (ArgumentOutOfRangeException) { assertions++; }
}
Check(ref hash, PrefixHashCache.SpaceSize - 1);
// The hot primitive must not allocate a visible seed string per root.
for (int n = 0; n < 1024; n++) hash.ComputeRoot((ulong)n);
long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
ulong checksum = 0;
for (int n = 0; n < 16384; n++) checksum ^= hash.ComputeRoot((ulong)n);
long hotAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
Require(hotAllocatedBytes == 0, "The cached numerical root loop allocated managed seed objects.");
GC.KeepAlive(checksum);
Console.WriteLine(JsonSerializer.Serialize(new { phase = "rootOracle", result = "PASS", rootCases,
    oracle = "ProductionProfile.ComputeRootSeed(VisibleSeedCandidateCodec.FormatOrdinal)",
    resumedZZFZ = true, crossesPrefixBlocks = true, finalSeed = true, hotAllocatedBytes }));

var inputs = new List<FamilyCandidateSet>();
foreach (var (start, count) in new (ulong, int)[]
{
    (0, 0), (0, 1), (zzfz, 1031), (PrefixHashCache.BlockSize - 513, 2049),
    (PrefixHashCache.SpaceSize - 8193, 8193), (PrefixHashCache.BlockSize * 7UL + 539, 20003)
}) inputs.Add(FamilyCandidateSet.Dense(new SearchBatch(start, count)));
var sparseBatch = new SearchBatch(PrefixHashCache.BlockSize - 4097, checked((int)PrefixHashCache.BlockSize + 8194));
inputs.Add(FamilyCandidateSet.FromAbi1(sparseBatch, [0, 1, 19, 4096, 4097, 8191, 65537,
    PrefixHashCache.BlockSize, (ulong)sparseBatch.BatchCandidateCount - 1]));
inputs.Add(FamilyCandidateSet.FromAbi1(sparseBatch, []));
var moreSparse = new SearchBatch(PrefixHashCache.BlockSize * 123UL - 257, 24007);
inputs.Add(FamilyCandidateSet.FromAbi1(moreSparse, Enumerable.Range(0, moreSparse.BatchCandidateCount)
    .Where(i => i % 3 != 1).Select(i => (ulong)i).ToArray()));

foreach (FamilyCandidateSet input in inputs)
{
    ulong[] expected = input.EnumerateLogicalOrdinals().Where(o => Predicate(Oracle(input.Batch.GlobalCandidate(o)))).ToArray();
    foreach (bool enabled in new[] { false, true })
    foreach (int mode in new[] { 0, 1, 2 })
    foreach (int workers in new[] { 1, 4 })
    foreach (bool budgeted in new[] { false, true })
    {
        var request = Request(enabled, workers, budgeted);
        var cpu = new FamilyCpuExecution(request, "Check.CpuPrefix", "Check.CpuPrefix.v1", workers,
            windowCapacity: 8192, rootMode: mode);
        FamilyCandidateSet actual = cpu.Execute(new(input.Batch, request), input, CancellationToken.None, Predicate);
        Require(actual.Batch == input.Batch && (actual.Count == 0 && expected.Length == 0 ||
            !actual.IsDense && actual.ExportAbi1().Span.SequenceEqual(expected)),
            $"CPU result/order mismatch: enabled={enabled},mode={mode},workers={workers},budgeted={budgeted},dense={input.IsDense}.");
        string revision = cpu.Condition(!input.IsDense).PhysicalImplementationRevision;
        Require(cpu.UsesPrefixCache == enabled && revision.Contains(CpuPrefixRootHasher.Revision, StringComparison.Ordinal) == enabled,
            "CPU physical identity does not describe the actually selected prefix path.");
        Require(revision.Contains(".RootMode" + mode + ".", StringComparison.Ordinal), "Root-mode control was lost.");
        Require(!cpu.LocalPeaks.Any(), "Uncalibrated cache work leaked into legacy peak samples.");
        executionCases++;
    }
}

foreach (bool enabled in new[] { false, true })
{
    var request = Request(enabled, 1, false, RuntimeProfileId.Beta110);
    var cpu = new FamilyCpuExecution(request, "Check.Beta110", "Check.Beta110.v1");
    var input = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 257));
    ulong[] expected = input.EnumerateLogicalOrdinals().Where(o => Predicate(Beta110Profile.Instance.ComputeRootSeed(
        VisibleSeedCandidateCodec.FormatOrdinal(Beta110Profile.Instance, input.Batch.GlobalCandidate(o))))).ToArray();
    Require(!cpu.UsesPrefixCache && !cpu.Condition(false).PhysicalImplementationRevision.Contains(CpuPrefixRootHasher.Revision, StringComparison.Ordinal),
        "An unaudited runtime profile incorrectly claimed prefix-cache execution.");
    Require(cpu.Execute(new(input.Batch, request), input, CancellationToken.None, Predicate).ExportAbi1().Span.SequenceEqual(expected),
        "Non-Beta111 fallback changed the legacy profile result.");
}

var cancelRequest = Request(true, 4, true);
var cancelCpu = new FamilyCpuExecution(cancelRequest, "Check.Cancel", "Check.Cancel.v1", workers: 4, rootMode: 2);
var cancelInput = FamilyCandidateSet.Dense(new SearchBatch(zzfz, 8193));
int visited = 0;
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { cancelCpu.Execute(new(cancelInput.Batch, cancelRequest), cancelInput, cancelled.Token, _ => { Interlocked.Increment(ref visited); return true; });
        throw new InvalidDataException("Pre-cancelled CPU prefix execution returned a partial result."); }
    catch (OperationCanceledException) { assertions++; }
    Require(visited == 0, "Pre-cancelled CPU prefix execution evaluated candidates.");
}
using (var cancelled = new CancellationTokenSource())
{
    try { cancelCpu.Execute(new(cancelInput.Batch, cancelRequest), cancelInput, cancelled.Token,
        _ => { if (Interlocked.Increment(ref visited) == 97) cancelled.Cancel(); return true; });
        throw new InvalidDataException("Cancelled CPU prefix execution published a partial result."); }
    catch (OperationCanceledException) { assertions++; }
}
ulong[] afterCancel = cancelInput.EnumerateLogicalOrdinals().Where(o => Predicate(Oracle(cancelInput.Batch.GlobalCandidate(o)))).ToArray();
Require(cancelCpu.Execute(new(cancelInput.Batch, cancelRequest), cancelInput, CancellationToken.None, Predicate)
    .ExportAbi1().Span.SequenceEqual(afterCancel), "Fresh partition-local hash state did not recover after cancellation.");
Console.WriteLine(JsonSerializer.Serialize(new { phase = "productionCpuExecution", result = "PASS", executionCases,
    rootModes = new[] { 0, 1, 2 }, workers = new[] { 1, 4 }, denseAndSparse = true,
    sequentialAndPartitioned = true, toggleAndProfileFallback = true, cancellationAndReuse = true,
    physicalRevisionIsolation = true, assertions }));

static ExactSearchExecutionRequest Request(bool prefixEnabled, int workers, bool budgeted,
    RuntimeProfileId profile = RuntimeProfileId.Beta111)
{
    string version = profile == RuntimeProfileId.Beta111 ? "0.111.0" : "0.110.1";
    var detection = new GameVersionDetection(version, version, "beta", "OptimizationCpuCacheChecks", true, "");
    var context = new SearchContext(profile, default, 0, null!, detection, SearchEvaluationAssumptions.BroadDefault);
    var compiled = new CompiledSearch(SearchQuery.Empty, context,
        new QueryNormalizationResult(SearchQuery.Empty, QueryNormalizationStatus.Legal, [], [], [], []),
        new ResolvedRouteSemantics(new(ResolvedCombatRewardRouteKind.NotApplicable, null, false, "OptimizationCpuCacheChecks")),
        false, "OptimizationCpuCacheChecks");
    var options = new SearchRunOptions("000000000000", 24007, 1, workers)
        { Resources = budgeted ? new SearchResourceBudget(100, 0) : null };
    options = options with { Optimizations = options.Optimizations with { PrefixCache = prefixEnabled } };
    return new(compiled, options, "000000000000", 24007, ExactSearchEvaluationProjection.Empty,
        CombatRewardRoutePolicyContract.None, "OptimizationCpuCacheChecks");
}
