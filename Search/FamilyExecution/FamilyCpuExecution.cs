using System.Diagnostics;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Runtime;

namespace RolltheSpire2.Search.FamilyExecution;

// CPU physical implementation owned by the existing producer. Optional bounded
// thread-pool partitions join within this invocation; no new cursor authority.
internal sealed class FamilyCpuExecution : IFamilyInvocation
{
    internal const int Capacity = 8192;
    private readonly IFamilyInvocation? _owner;
    private readonly Func<ulong, bool>? _matches;
    private readonly Func<FamilyPhysicalQuoteRequest, FamilyPhysicalQuote?>? _quote;
    private readonly CpuNeowBatchGate? _batchNeowGate;
    private long _batchNeowBlocks, _batchNeowRejected;
    internal long BatchNeowBlocks => Interlocked.Read(ref _batchNeowBlocks);
    private readonly CpuCostSnapshot _cost = CpuCostCalibration.Capture();
    private CpuLocalPeak? _localPeak;
    private bool _calibrationCoverageMatched;
    // Bound once by the selected session order, never by a speculative planning traversal.
    internal void BindCalibrationCoverage(IReadOnlySet<string> passed) =>
        _calibrationCoverageMatched = RequiredPassedCoverage is null || RequiredPassedCoverage.SetEquals(passed);
    internal double? CalibrationMeanInput { get; set; }
    private int _sampleRootBatches;
    private long _sampleInputs;
    private double _sampleMs;
    internal int Workers => _workers;
    internal IEnumerable<CpuLocalPeak> LocalPeaks => !_disabled && _localPeak is not null ? new[]{_localPeak} : [];
    private FamilyPhysicalQuote? LocalQuote(FamilyPhysicalQuoteRequest g)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(_request.RunOptions.Optimizations);
        if (_quote?.Invoke(g) is not { } raw) return null;
        // Legacy global CPU ratios include the old full-root hashing envelope.
        // Keep them out of this distinct implementation until it has its own
        // admitted reference/calibration shape. The unscaled reference carries
        // no invented measured speedup for the new hash implementation.
        if (_batchNeowGate is not null && !g.CompactInput) return raw with
        {
            LocalExecutorCostRatio = 1,
            LocalCostSource = "ReferenceDefaultCpuBatchNeowRevision",
            Evidence = raw.Evidence + ";ExecutionClass=CPU;BatchGate=" + CpuNeowBatchGate.Revision +
                ";LegacyCpuCalibrationExcluded=true;sameRunFrozen=true"
        };
        if (_usePrefixCache) return raw with
        {
            LocalExecutorCostRatio = 1,
            LocalCostSource = "ReferenceDefaultCpuPrefixRevision",
            Evidence = raw.Evidence + ";ExecutionClass=CPU;RootHash=" + CpuPrefixRootHasher.Revision +
                ";LegacyCpuCalibrationExcluded=true;sameRunFrozen=true"
        };
        return _cost.Local(raw,Condition(g.CompactInput).PhysicalImplementationRevision,_workers,g.CompactInput);
    }
    internal int WindowCapacity => _windowCapacity;
    internal IReadOnlySet<string>? RequiredPassedCoverage { get; set; }
    internal FamilyPhysicalQuote? Quote(FamilyPhysicalQuoteRequest g, IReadOnlySet<string> passed) =>
        RequiredPassedCoverage is not null && !RequiredPassedCoverage.SetEquals(passed) ? null : LocalQuote(g);
    internal FamilyCpuExecution(ExactSearchExecutionRequest request, IFamilyInvocation owner,
        string revision, Func<ulong, bool> matches, int workers,
        Func<FamilyPhysicalQuoteRequest, FamilyPhysicalQuote?> quote, CpuNeowBatchGate? batchNeowGate = null)
        : this(request, owner.FamilyId, revision, workers, 65536, numericalRoots: request.ProfileId == RuntimeProfileId.Beta111,
            batchNeowGate: batchNeowGate)
    {
        if (owner.Coverage.Count != 1 || owner.Coverage[0] != owner.FamilyId)
            throw new ArgumentException("CPU realization requires one owning Family", nameof(owner));
        _owner = owner; _matches = matches;
        // A quote for the donor's worker shape cannot price a different explicit
        // budget. Keep that unmeasured shape separate from donor timing evidence.
        _quote = _workers == workers ? quote : null;
    }
    string IFamilyInvocation.FamilyId => _id;
    IReadOnlyList<string> IFamilyInvocation.Coverage => _owner!.Coverage;
    FamilyAnalyticalCostProjection IFamilyInvocation.AnalyticalCost => _owner!.AnalyticalCost;
    FamilySurvivalProjection IFamilyInvocation.Survival => _owner!.Survival;
    bool IFamilyInvocation.HasConditionalProjections => _owner!.HasConditionalProjections;
    FamilySurvivalProjection IFamilyInvocation.ResolveSurvival(IReadOnlySet<string> passed) => _owner!.ResolveSurvival(passed);
    FamilyExpectedFilteringCostProjection? IFamilyInvocation.ExpectedFilteringCost => _owner!.ExpectedFilteringCost;
    FamilyExpectedFilteringCostProjection? IFamilyInvocation.ResolveExpectedFilteringCost(IReadOnlySet<string> passed) => _owner!.ResolveExpectedFilteringCost(passed);
    FamilyPhysicalQuote? IFamilyInvocation.QuotePhysicalWork(FamilyPhysicalQuoteRequest request) => LocalQuote(request);
    FamilyConditionPerformanceProjection IFamilyInvocation.ConditionPerformance => Condition(false);
    FamilyConditionPerformanceProjection IFamilyInvocation.ResolveConditionPerformance(bool compact) => Condition(compact);
    ValueTask<FamilyCandidateSet> IFamilyInvocation.InvokeAsync(FamilyExecutionContext context, FamilyObservationWindow window,
        FamilyCandidateSet input, CancellationToken token) => new(Execute(window, input, token, _matches!));
    FamilyPerformanceObservation IFamilyInvocation.CapturePerformanceObservation() => Observation();
    FamilyLivePerformanceSnapshot? IFamilyInvocation.CaptureLivePerformanceSnapshot() => Live();
    void IFamilyInvocation.LogExecutionEvidence() => WriteSummary();
    private readonly ExactSearchExecutionRequest _request;
    private readonly IRuntimeProfile _profile;
    private readonly string _id, _revision;
    private readonly int _rootMode;
    private readonly bool _usePrefixCache;
    internal bool UsesPrefixCache => _usePrefixCache;
    private readonly int _workers;
    private readonly int _windowCapacity;
    private readonly int _minimumPartitionSize;
    private readonly bool _partitioned;
    private bool _disabled, _compact;
    private int _batches, _steady;
    private long _inputs, _outputs, _liveInputs;
    private double _ms, _liveMs, _peak;
    internal FamilyCpuExecution(ExactSearchExecutionRequest request, string id, string revision, int workers = 1, int windowCapacity = Capacity, bool numericalRoots = false, int? rootMode = null,
        CpuNeowBatchGate? batchNeowGate = null)
    {
        _request = request; _id = id; _profile = RuntimeProfileRegistry.Select(request.Detection);
        if (workers is < 1 or > 64 || windowCapacity is < 1024 or > (1 << 20)) throw new ArgumentOutOfRangeException(nameof(workers));
        SearchResourceBudget? resources = request.RunOptions.Resources;
        _workers = resources is null ? workers : Math.Min(request.WorkerCount, resources.ResolveCpuWorkers(Environment.ProcessorCount));
        _revision = resources is null ? revision : revision + ".ExplicitCpuBudget.v1";
        _windowCapacity = windowCapacity;
        // Legacy 8192-root CPU windows must also admit up to 64 explicitly
        // budgeted workers; sparse inputs still avoid tiny parallel partitions.
        _minimumPartitionSize = resources is null ? 1024 : 128;
        _partitioned = numericalRoots || _workers > 1 || resources is not null;
        _rootMode = rootMode ?? (numericalRoots ? 2 : 0);
        if (_rootMode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(rootMode));
        if (_rootMode != 0 && _profile.ProfileId != RuntimeProfileId.Beta111)
            throw new InvalidOperationException("CpuByteRootsRequireBeta111");
        _usePrefixCache = request.RunOptions.Optimizations.PrefixCache &&
            _profile.ProfileId == RuntimeProfileId.Beta111 && _profile.SeedLength == 12 &&
            _profile.SeedAlphabet == Beta110SeedCodec.Alphabet;
        _batchNeowGate = _partitioned && _usePrefixCache && request.RunOptions.Optimizations.CostOrderedFilters &&
            CpuNeowBatchGate.HardwareSupported ? batchNeowGate : null;
    }
    internal FamilyConditionPerformanceProjection Condition(bool compact) => new(_id,
        _revision + ".RootMode" + _rootMode + ".P" + (_partitioned ? _workers : 0) + ".B" + _windowCapacity + (compact ? ".CompactAbi1" : ".Dense") +
        (_usePrefixCache ? "." + CpuPrefixRootHasher.Revision : "") +
        (_batchNeowGate is not null && !compact ? "." + CpuNeowBatchGate.Revision : "") + ".CanonicalAbi1Ready",
        "Cpu.NeutralCondition.20260905.v1", 1, "Neutral_NoAcceptedWithinPathCurve;device=CPU", usesGpu: false);
    internal FamilyCandidateSet Execute(FamilyObservationWindow window, FamilyCandidateSet input,
        CancellationToken token, Func<ulong, bool> matches)
    {
        token.ThrowIfCancellationRequested();
        if (window.ExactRequest.SnapshotFingerprint != _request.SnapshotFingerprint)
            throw new InvalidOperationException("FamilyObservationContextMismatch");
        if (_disabled) throw new InvalidOperationException("FamilyCpuInvocationFaulted:" + _id);
        if (input.Count == 0) return input;
        _compact = !input.IsDense;
        var watch = Stopwatch.StartNew();
        try
        {
            int chunks;
            FamilyCandidateSet result;
            if (_partitioned) result = ExecutePartitioned(input, token, matches, out chunks);
            else
            {
                var survivors = new List<ulong>(Math.Min(input.Count, Capacity));
                // Keep one observation window and original batch-local ordinals. Only
                // the private CPU output staging is chunked; no partial result is handed
                // to another Family or to Exact before this invocation completes.
                var chunk = new ulong[Math.Min(input.Count, Capacity)];
                int chunkSurvivors = 0, chunkInputs = 0; chunks = 0;
                Span<byte> seedBytes = stackalloc byte[Beta110SeedCodec.SeedLength];
                var prefixHasher = new CpuPrefixRootHasher();
                bool carry = !_usePrefixCache && _rootMode == 2 && input.IsDense;
                if (carry) Beta110SeedCodec.WriteOrdinal(input.Batch.BatchBase, seedBytes);
                foreach (ulong ordinal in input.EnumerateLogicalOrdinals())
                {
                    token.ThrowIfCancellationRequested();
                    ulong global = input.Batch.GlobalCandidate(ordinal);
                    ulong root;
                    if (_usePrefixCache) root = prefixHasher.ComputeRoot(global);
                    else if (_rootMode == 0) root = _profile.ComputeRootSeed(VisibleSeedCandidateCodec.FormatOrdinal(_profile, global));
                    else
                    {
                        if (!carry) Beta110SeedCodec.WriteOrdinal(global, seedBytes);
                        root = XxHash64.Hash(seedBytes, 0);
                        if (carry && ordinal + 1 < (ulong)input.Count && !Beta110SeedCodec.Advance(seedBytes, 1))
                            throw new InvalidOperationException("CpuDenseSeedCarryOverflow");
                    }
                    if (matches(root)) chunk[chunkSurvivors++] = ordinal;
                    if (++chunkInputs == Capacity)
                    {
                        for (int i = 0; i < chunkSurvivors; i++) survivors.Add(chunk[i]);
                        chunkInputs = chunkSurvivors = 0; chunks++;
                    }
                }
                if (chunkInputs > 0)
                {
                    for (int i = 0; i < chunkSurvivors; i++) survivors.Add(chunk[i]);
                    chunks++;
                }
                token.ThrowIfCancellationRequested();
                result = FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors.ToArray());
            }
            watch.Stop();
            _batches++; _inputs += input.Count; _outputs += result.Count; _ms += watch.Elapsed.TotalMilliseconds;
            // Only full-window, completed canonical CPU work is comparable to the atlas.
            // No survivor-based learning, Exact time, terminal wait or cold first invocation.
            if (!_usePrefixCache && (_batchNeowGate is null || _compact) && _batches > 1 && (RequiredPassedCoverage is null || _calibrationCoverageMatched) && input.Batch.BatchCandidateCount == _windowCapacity &&
                (input.IsDense || CalibrationMeanInput.HasValue) &&
                _quote?.Invoke(new(!input.IsDense,false,input.IsDense?_windowCapacity:CalibrationMeanInput!.Value)) is {} raw && watch.Elapsed.TotalMilliseconds > 0)
            {
                _sampleRootBatches++; _sampleInputs += input.Count; _sampleMs += watch.Elapsed.TotalMilliseconds;
                if (_sampleInputs >= 262144 && _sampleMs >= 50)
                {
                    var peak = new CpuLocalPeak(CpuCostCalibration.DeviceKey(),
                        CpuCostCalibration.Key(raw.Shape,Condition(_compact).PhysicalImplementationRevision,_workers,_compact),
                        raw.Shape,Condition(_compact).PhysicalImplementationRevision,_workers,_sampleInputs,_sampleMs,DateTimeOffset.UtcNow,
                        _compact,_sampleRootBatches,CalibrationMeanInput);
                    if (CpuCostCalibration.Valid(peak) && (_localPeak is null || peak.NanosecondsPerInput < _localPeak.NanosecondsPerInput)) _localPeak=peak;
                    _sampleInputs=0; _sampleMs=0; _sampleRootBatches=0;
                }
            }
            if (RuntimeLog.DetailEnabled)
                RuntimeLog.TryBackgroundDetail($"familyCpuBatch=true;family={_id};physicalRevision={Condition(_compact).PhysicalImplementationRevision};batchBase={input.Batch.BatchBase};rootBatchCount={input.Batch.BatchCandidateCount};input={input.Count};survivors={result.Count};cpuChunkSize={(_partitioned ? (input.Count + chunks - 1) / chunks : Capacity)};cpuChunks={chunks};partitioned={_partitioned};maxCpuWorkers={_workers};canonicalAbi1ReadyMs={watch.Elapsed.TotalMilliseconds};abi=ABI1");
            if (_batches > 1)
            {
                Interlocked.Add(ref _liveInputs, input.Count);
                _liveMs += watch.Elapsed.TotalMilliseconds;
                if (input.Count >= (_compact ? Capacity / 4 : Capacity))
                { _steady++; _peak = Math.Max(_peak, input.Count * 1000d / watch.Elapsed.TotalMilliseconds); }
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _disabled = true;
            RuntimeLog.TryBackgroundWarning($"familyPhysicalFault=true;family={_id};reason={ex.GetType().Name}:{ex.Message};recovery=None");
            throw;
        }
    }
    private FamilyCandidateSet ExecutePartitioned(FamilyCandidateSet input, CancellationToken token,
        Func<ulong, bool> matches, out int parts, int firstIndex = 0, int? inputLength = null)
    {
        // Preserve the measured CPU window inside a larger GPU root batch.
        // No intermediate CPU chunk is published to the next Family.
        if (inputLength is null && input.Count > _windowCapacity)
        {
            var combined = new List<ulong>(); parts = 0;
            for(int start=0;start<input.Count;start+=_windowCapacity)
            {
                var result = ExecutePartitioned(input,token,matches,out int chunkParts,start,Math.Min(_windowCapacity,input.Count-start));
                combined.AddRange(result.ExportAbi1().ToArray()); parts += chunkParts;
            }
            token.ThrowIfCancellationRequested();
            return FamilyCandidateSet.FromSortedAbi1(input.Batch,combined.ToArray());
        }
        int length = inputLength ?? input.Count;
        // Pure, batch-local CPU work. All partitions join before publishing ABI1;
        // the existing producer, Exact pipeline and safe cursor remain owners.
        int count = Math.Min(_workers, Math.Max(1, length / _minimumPartitionSize));
        parts = count;
        var populations = new List<ulong>[count];
        void Partition(int part)
        {
            using var cpuSlot = SearchCpuConcurrencyBudget.Acquire(token);
            int first = firstIndex + (int)((long)part * length / count);
            int end = firstIndex + (int)((long)(part + 1) * length / count);
            var output = new List<ulong>(Math.Min(end - first, 256));
            ReadOnlySpan<ulong> ordinals = input.IsDense ? default : input.ExportAbi1().Span;
            Span<byte> bytes = stackalloc byte[Beta110SeedCodec.SeedLength];
            var prefixHasher = new CpuPrefixRootHasher();
            bool carry = !_usePrefixCache && _rootMode == 2 && input.IsDense;
            if (carry) Beta110SeedCodec.WriteOrdinal(input.Batch.GlobalCandidate((ulong)first), bytes);
            int i = first;
            long batchBlocks = 0, batchRejected = 0;
            if (input.IsDense && _batchNeowGate is { } batchGate)
            {
                // Scalar seed-prefix ownership is unchanged. Only the necessary
                // first N observation is SoA4 AVX2; full accepted-lane matchers
                // still own all identity/route work and ordinary CPU semantics.
                int vectorEnd = end - (end - first) % 4;
                for (; i < vectorEnd; i += 4)
                {
                    token.ThrowIfCancellationRequested();
                    ulong root0 = prefixHasher.ComputeRoot(input.Batch.GlobalCandidate((ulong)i));
                    ulong root1 = prefixHasher.ComputeRoot(input.Batch.GlobalCandidate((ulong)(i + 1)));
                    ulong root2 = prefixHasher.ComputeRoot(input.Batch.GlobalCandidate((ulong)(i + 2)));
                    ulong root3 = prefixHasher.ComputeRoot(input.Batch.GlobalCandidate((ulong)(i + 3)));
                    int mask = batchGate.MatchesFour(root0, root1, root2, root3);
                    batchBlocks++;
                    batchRejected += 4 - System.Numerics.BitOperations.PopCount((uint)mask);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        if ((mask & (1 << lane)) == 0) continue;
                        token.ThrowIfCancellationRequested();
                        ulong root = lane switch { 0 => root0, 1 => root1, 2 => root2, _ => root3 };
                        if (matches(root)) output.Add((ulong)(i + lane));
                    }
                }
            }
            for (; i < end; i++)
            {
                token.ThrowIfCancellationRequested();
                ulong ordinal = input.IsDense ? (ulong)i : ordinals[i];
                ulong global = input.Batch.GlobalCandidate(ordinal);
                ulong root;
                if (_usePrefixCache) root = prefixHasher.ComputeRoot(global);
                else if (_rootMode == 0) root = _profile.ComputeRootSeed(VisibleSeedCandidateCodec.FormatOrdinal(_profile, global));
                else
                {
                    if (!carry) Beta110SeedCodec.WriteOrdinal(global, bytes);
                    root = XxHash64.Hash(bytes, 0);
                    if (carry && i + 1 < end && !Beta110SeedCodec.Advance(bytes, 1))
                        throw new InvalidOperationException("CpuDensePartitionCarryOverflow");
                }
                if (matches(root)) output.Add(ordinal);
            }
            if (batchBlocks != 0)
            {
                Interlocked.Add(ref _batchNeowBlocks, batchBlocks);
                Interlocked.Add(ref _batchNeowRejected, batchRejected);
            }
            populations[part] = output;
        }
        if (count == 1) Partition(0);
        else Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = _workers, CancellationToken = token }, Partition);
        token.ThrowIfCancellationRequested();
        int total = 0;
        foreach (var population in populations) total = checked(total + population.Count);
        var survivors = new ulong[total];
        int offset = 0;
        foreach (var population in populations) { population.CopyTo(survivors, offset); offset += population.Count; }
        return FamilyCandidateSet.FromSortedAbi1(input.Batch, survivors);
    }

    internal FamilyPerformanceObservation Observation() => new(Condition(_compact), "", 0, _peak, _peak,
        _batches, _steady, _inputs, _outputs, _ms, !_disabled && _steady > 0,
        _disabled ? "PhysicalFailureOrRecovery" : "CpuCanonicalAbi1Ready;ColdFirstBatchExcluded");
    internal void WriteSummary()
    {
        RuntimeLog.TryBackgroundInfo("cpuCostObservation=true;observationJson="+RuntimeLog.SafeJson(new{family=_id,workers=_workers,best=_localPeak,pendingInputs=_sampleInputs,pendingMs=_sampleMs,eligible=!_disabled,boundary=CpuCostCalibration.Boundary,compactEvidence="OnlyFullRootBatchesWithComparableModeledOccupancy",sameRunRepricing=false}));
        RuntimeLog.TryBackgroundInfo($"familyCpuSummary=true;family={_id};physicalRevision={Condition(_compact).PhysicalImplementationRevision};maxCpuWorkers={_workers};windowCapacity={_windowCapacity};timingBoundary=CanonicalAbi1Ready;denominator=ActualStageInput;batches={_batches};input={_inputs};survivors={_outputs};canonicalMs={_ms};recovery=False;faulted={_disabled}");
        if (_batchNeowGate is not null)
            RuntimeLog.TryBackgroundInfo($"cpuNeowBatchGate=true;family={_id};revision={CpuNeowBatchGate.Revision};denseOnly=True;avx2={CpuNeowBatchGate.HardwareSupported};blocks={Interlocked.Read(ref _batchNeowBlocks)};gateRejectedRoots={Interlocked.Read(ref _batchNeowRejected)};acceptedLanesUseScalarMatcher=True;rootHash=ExistingScalarPrefix;legacyCalibrationExcluded=True");
    }
    internal FamilyLivePerformanceSnapshot? Live() => _disabled || _batches < 2 ? null : new(_id,
        Condition(_compact).PhysicalImplementationRevision, Interlocked.Read(ref _liveInputs), Volatile.Read(ref _liveMs), _batches - 1);
}
