using System.Diagnostics;
using Godot;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private sealed record BenchmarkVariant(string Name, bool LegacyLeafy, bool PreSubmitHeader, bool SingleWindows,
        bool PrefixHash, bool CountedDispatch);

    // Isolated, fixed-input comparison. Never writes production calibration or
    // treats this synthetic fixture as an actual in-game query measurement.
    private static void RunFourWayBenchmark(RenderingDevice rd)
    {
        int batches = int.TryParse(System.Environment.GetEnvironmentVariable("RT2_GPU_AB_BATCHES"), out int requested)
            ? Math.Clamp(requested, 32, 8192) : 512;
        int rounds = int.TryParse(System.Environment.GetEnvironmentVariable("RT2_GPU_AB_ROUNDS"), out requested)
            ? Math.Clamp(requested, 3, 9) : 5;
        bool zeroTerminal = System.Environment.GetEnvironmentVariable("RT2_GPU_AB_ZERO_TERMINAL") == "1";
        string fixture = zeroTerminal ? "SyntheticLeafyFixedPair64PoolsEmptyTerminal" : "SyntheticLeafyFixedPair64Pools";
        BenchmarkVariant[] variants = [
            new("Resource4PathControl", false, true, false, false, false),
            new("PrefixOnly", false, true, false, true, false),
            new("CountedDispatchOnly", false, true, false, false, true),
            new("Resource5", false, true, false, true, true),
            new("UpstreamGpuGeometryEquivalent", true, false, true, false, false)
        ];
        var executors = new List<NeowFamilyGpuExecutor>();
        try
        {
            foreach (var variant in variants)
            {
                var plan = Plan(true);
                // Isolate the dense pre-gate and status path from the unrelated
                // large-output-buffer download cost. An empty synthetic final
                // curse projection rejects the tiny stage-2 population. It is
                // explicitly a transport fixture, not an authored game query.
                if (zeroTerminal) plan.Meta[83] = 1;
                executors.Add(new(rd, plan, 42, useIndirectStaging: variant.CountedDispatch,
                    legacyLeafyGate: variant.LegacyLeafy, usePreSubmitHeaderReadback: variant.PreSubmitHeader,
                    usePrefixHashCache: variant.PrefixHash));
            }

            FamilyCandidateSet Run(int index, SearchBatch batch)
            {
                var executor = executors[index];
                return variants[index].SingleWindows ? ExecuteSingles(executor, batch, out _, out _)
                    : executor.ExecuteDenseWindows(FamilyCandidateSet.Dense(batch), CancellationToken.None, out _);
            }

            // Force all shader/module construction and both workspaces before
            // measurement; check outputs and checkpoint semantics elsewhere in
            // the full smoke suite, and output equality here on every input.
            for (int warmup = 0; warmup < 16; warmup++)
            {
                var batch = new SearchBatch(0x100000003UL + (ulong)warmup * NeowFamilyGpuExecutor.DenseWindowBatchCapacity,
                    NeowFamilyGpuExecutor.DenseWindowBatchCapacity);
                ulong[] reference = Run(0, batch).ExportAbi1().ToArray();
                for (int index = 1; index < variants.Length; index++)
                    Require(reference.AsSpan().SequenceEqual(Run(index, batch).ExportAbi1().Span),
                        "A/B warmup output mismatch: " + variants[index].Name);
            }
            Emit(new { phase = "fourWayWarmup", result = "PASS", variants = variants.Select(v => v.Name),
                fixture, warmupsPerVariant = 16 });

            var measurements = variants.Select(_ => new List<double>()).ToArray();
            for (int round = 0; round < rounds; round++)
            {
                // Rotate and reverse order to spread clock/temperature drift;
                // every variant receives identical seed ranges within a round.
                int[] order = Enumerable.Range(0, variants.Length).Select(i => (i + round) % variants.Length).ToArray();
                if ((round & 1) != 0) Array.Reverse(order);
                ulong roundBase = 0x100000003UL + (ulong)round * (ulong)batches * NeowFamilyGpuExecutor.DenseWindowBatchCapacity;
                ulong[][]? referenceOutputs = null;
                foreach (int index in order)
                {
                    ulong count = 0;
                    var watch = Stopwatch.StartNew();
                    for (int batchIndex = 0; batchIndex < batches; batchIndex++)
                    {
                        var batch = new SearchBatch(roundBase + (ulong)batchIndex * NeowFamilyGpuExecutor.DenseWindowBatchCapacity,
                            NeowFamilyGpuExecutor.DenseWindowBatchCapacity);
                        var result = Run(index, batch);
                        Require(!zeroTerminal || result.Count == 0, "Empty-terminal fixture produced a candidate.");
                        count += (ulong)result.Count;
                        if (index == order[0])
                        {
                            referenceOutputs ??= new ulong[batches][];
                            referenceOutputs[batchIndex] = result.ExportAbi1().ToArray();
                        }
                        else Require(referenceOutputs![batchIndex].AsSpan().SequenceEqual(result.ExportAbi1().Span),
                            "A/B per-batch ordinal mismatch: " + variants[index].Name);
                    }
                    watch.Stop();
                    measurements[index].Add(watch.Elapsed.TotalMilliseconds);
                    var executor = executors[index];
                    Emit(new { phase = "fourWayRound", result = "PASS", round, variant = variants[index].Name,
                        batches, roots = (ulong)batches * NeowFamilyGpuExecutor.DenseWindowBatchCapacity,
                        wallMs = watch.Elapsed.TotalMilliseconds, survivors = count,
                        headerRevision = executor.HeaderReadbackRevision, leafyRevision = executor.LeafyGateRevision,
                        prefixRevision = executor.PrefixHashRevision, dispatchRevision = executor.StagedDispatchRevision,
                        transport = executor.WindowTransportRevision });
                }
            }
            double rootsPerRound = (double)batches * NeowFamilyGpuExecutor.DenseWindowBatchCapacity;
            double Median(IEnumerable<double> values)
            {
                double[] sorted = values.Order().ToArray();
                return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
                    : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            }
            double referenceMedian = Median(measurements[0]);
            Emit(new { phase = "fourWayBenchmark", result = "PASS", batches, rounds,
                fixture, actualGameQuery = false,
                evidence = "SameInputPerRound;RotatingReversedOrder;SixteenWarmupsExcluded;HostCompletedWall;NoProductionCalibration;NotGpuKernelTimestamps",
                variants = variants.Select((variant, index) => new {
                    variant.Name, variant.LegacyLeafy, variant.PreSubmitHeader, variant.SingleWindows,
                    totalRoots = rootsPerRound * rounds, totalWallMs = measurements[index].Sum(),
                    medianRoundMs = Median(measurements[index]), rootsPerSecond = rootsPerRound * 1000 / Median(measurements[index]),
                    speedupVsResource4Control = referenceMedian / Median(measurements[index]), roundWallMs = measurements[index]
                }) });
        }
        finally { foreach (var executor in executors) executor.Dispose(); }
    }
}
