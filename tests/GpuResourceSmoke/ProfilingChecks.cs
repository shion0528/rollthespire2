using System.Reflection;
using Godot;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // Reflection keeps this host buildable against both the shipping DLL (where
    // every implementation is absent) and the positive diagnostic DLL.
    private static void TestProfilingChecks(RenderingDevice rd)
    {
        Type? type = typeof(NeowFamilyGpuExecutor).Assembly.GetType("RolltheSpire2.Diagnostics.GpuProfiling.GpuProfilingSession");
        if (type is null)
        {
            Require(typeof(NeowFamilyGpuExecutor).GetField("_gpuProfilingSession", BindingFlags.NonPublic | BindingFlags.Instance) is null,
                "Shipping executor retained a profiling implementation field.");
            Emit(new { phase = "gpuProfiling", result = "PASS", implementation = "Absent", realTimestamps = "NotRequested" });
            return;
        }
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        var constructor = type.GetConstructors(instance).Single();
        object Create(int every, Action<string> capture, Func<uint> count, Func<uint, string> name,
            Func<uint, ulong> time, Action<string>? log = null) => constructor.Invoke(
                [rd, every, capture, count, name, time, log ?? (_ => { })]);
        object? Call(object target, string method, params object[] args) => type.GetMethod(method, instance)!.Invoke(target, args);
        object Snapshot(object target) => Call(target, "Snapshot")!;
        long Number(object value, string property) => Convert.ToInt64(value.GetType().GetProperty(property)!.GetValue(value));
        double Real(object value, string property) => Convert.ToDouble(value.GetType().GetProperty(property)!.GetValue(value));
        bool Disabled(object value) => (bool)value.GetType().GetProperty("Disabled")!.GetValue(value)!;
        object? Current() => type.GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        NeowFamilyGpuMetrics Metrics(int input) => new(input, 0, 1, 32, 1, .1, .8, .05, .05, 3, 1, 0, 0, 0, 0, 0, 0);

        var interval = type.GetMethod("GpuIntervalMilliseconds", BindingFlags.NonPublic | BindingFlags.Static)!;
        double Milliseconds(ulong begin, ulong end, double cpuMs) => (double)interval.Invoke(null, [begin, end, cpuMs])!;
        Require(Milliseconds(1_000_000_000, 1_001_000_000, 2) == 1d &&
            Milliseconds(ulong.MaxValue - 1_000_000, ulong.MaxValue, 2) == 1d &&
            Math.Abs(Milliseconds(100_000_000, 100_623_616, 3.7678) - .623616) < 1e-12,
            "GPU timestamps did not convert nanoseconds to milliseconds exactly.");
        Require(Milliseconds(0, 1_100_000, .2) == 1.1d,
            "Timestamp quantization tolerance falsely rejected a short completed scope.");
        int intervalRejected = 0;
        void RejectInterval(ulong begin, ulong end, double cpuMs)
        {
            try { Milliseconds(begin, end, cpuMs); throw new Exception("Implausible GPU interval was accepted."); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { intervalRejected++; }
        }
        RejectInterval(0, 3_000_000, 1);
        RejectInterval(0, ulong.MaxValue, .1);
        RejectInterval(2, 1, 1);
        RejectInterval(0, 0, 1);
        RejectInterval(0, 1, double.NaN);
        RejectInterval(0, 1, double.PositiveInfinity);
        RejectInterval(0, 1, -1);

        var markers = new List<string>(2);
        int logs = 0;
        object NewCollector(int rate) => Create(rate, markers.Add, () => checked((uint)markers.Count),
            index => markers[checked((int)index)], index => 1000UL + index * 150UL, _ => logs++);
        void Window(object collector)
        {
            Call(collector, "WindowBegin");
            Call(collector, "WindowEnd");
        }
        void SubmitAndComplete(object collector)
        {
            Call(collector, "BeforeSubmit");
            Call(collector, "AfterSync");
            markers.Clear();
        }
        var sampled = NewCollector(256);
        try
        {
            for (int i = 0; i < 1024; i++)
            {
                Call(sampled, "BeginBatch", 513);
                Window(sampled);
                SubmitAndComplete(sampled);
                Call(sampled, "BatchCompleted", Metrics(513));
                Call(sampled, "EndBatch");
            }
            var value = Snapshot(sampled);
            Require(Number(value, "BatchesSeen") == 1024 && Number(value, "Samples") == 5 &&
                Number(value, "PhysicalWindows") == 5 && Number(value, "Submissions") == 5 &&
                Math.Abs(Real(value, "GpuRecordedSpanMs") - .00075) < 1e-12 &&
                Number(value, "PendingMarkers") == 0 && !Disabled(value) && Current() is null && logs <= 2,
                "Profiling sampling rate, storage bounds, or scope cleanup changed.");
        }
        finally { ((IDisposable)sampled).Dispose(); }

        var paired = NewCollector(1);
        try
        {
            Call(paired, "BeginBatch", 100);
            Window(paired); Window(paired);
            Require(markers.Count == 1, "Profiling inserted a timestamp between independent windows.");
            Call(paired, "BeforeSubmit");
            Require(markers.Count == 2, "A submission did not use exactly one outer timestamp pair.");
            Call(paired, "AfterSync"); markers.Clear();
            Call(paired, "BatchCompleted", Metrics(100)); Call(paired, "EndBatch");

            // Two nested ExecuteCore scopes under one logical fallback batch:
            // two original submissions but one inclusive result observation.
            Call(paired, "BeginBatch", 101);
            foreach (int count in new[] { 50, 51 })
            {
                Call(paired, "BeginBatch", count);
                Window(paired); SubmitAndComplete(paired);
                Call(paired, "BatchCompleted", Metrics(count)); Call(paired, "EndBatch");
                Require(Current() is not null, "Nested completion prematurely closed the outer logical batch.");
            }
            Call(paired, "BatchCompleted", Metrics(101)); Call(paired, "EndBatch");
            var value = Snapshot(paired);
            Require(Number(value, "Samples") == 2 && Number(value, "Roots") == 201 &&
                Number(value, "Submissions") == 3 && Number(value, "PhysicalWindows") == 4 && Current() is null,
                "Nested profiling double-counted results or changed submission geometry.");

            Call(paired, "BeginBatch", 0);
            Call(paired, "BeforeSubmit"); Call(paired, "AfterSync");
            Call(paired, "BatchCompleted", Metrics(0)); Call(paired, "EndBatch");
            Require(Number(Snapshot(paired), "Submissions") == 3, "Empty input fabricated a GPU submission.");

            Call(paired, "BeginBatch", 7); Window(paired); Call(paired, "EndBatch"); markers.Clear();
            Require(Number(Snapshot(paired), "Discarded") == 1 && Current() is null, "Cancelled/failed profiling scope was retained.");
            Call(paired, "BeginBatch", 9); Window(paired); SubmitAndComplete(paired);
            Call(paired, "BatchCompleted", Metrics(9)); Call(paired, "EndBatch");
            Require(!Disabled(Snapshot(paired)) && Current() is null, "Cancelled scope broke safe collector reuse.");
        }
        finally { ((IDisposable)paired).Dispose(); markers.Clear(); }

        int disabledCases = 0;
        void Reject(Action<string> capture, Func<uint> count, Func<uint, string> name, Func<uint, ulong> time,
            Action<string>? log = null)
        {
            object bad = Create(1, capture, count, name, time, log);
            try
            {
                Call(bad, "BeginBatch", 513); Window(bad);
                Call(bad, "BeforeSubmit"); Call(bad, "AfterSync");
                Call(bad, "BatchCompleted", Metrics(513)); Call(bad, "EndBatch");
                Require(Disabled(Snapshot(bad)) && Current() is null, "Timing failure escaped diagnostic disable/cleanup.");
                Call(bad, "BeginBatch", 513); Window(bad);
                Call(bad, "BeforeSubmit"); Call(bad, "AfterSync"); Call(bad, "EndBatch");
                Require(Current() is null, "Disabled diagnostic left a live scope.");
                disabledCases++;
            }
            finally { ((IDisposable)bad).Dispose(); markers.Clear(); }
        }
        Reject(_ => throw new InvalidOperationException("Smoke timestamp unsupported."), () => 0, _ => "", _ => 0);
        Reject(markers.Add, () => 0, _ => "", _ => 0); // Native refusal returns no marker.
        Reject(markers.Add, () => 2, index => markers[checked((int)index)], index => index == 0 ? 200UL : 100UL);
        Reject(markers.Add, () => 4, index => markers[checked((int)(index % 2))], _ => 1000);
        Reject(markers.Add, () => 4097, _ => throw new InvalidDataException("Unbounded timestamp iteration."), _ => 1);
        Reject(markers.Add, () => 2, index => markers[checked((int)index)], index => index == 0 ? 1UL : 10_000_000_001UL);
        Reject(markers.Add, () => 2, index => markers[checked((int)index)], index => 1000 + index * 150UL,
            _ => throw new IOException("Smoke diagnostic logger unavailable."));

        // Real-RD integration uses an explicit runtime opt-in captured at
        // construction. The diagnostic DLL with opt-in absent remains a valid
        // control for the separately orchestrated actual-game benchmark.
        string? old = System.Environment.GetEnvironmentVariable("RT2_GPU_PROFILE");
        NeowFamilyGpuExecutor? control = null, enabled = null;
        try
        {
            System.Environment.SetEnvironmentVariable("RT2_GPU_PROFILE", null);
            control = new(rd, Plan(true), 42, useIndirectStaging: false);
            System.Environment.SetEnvironmentVariable("RT2_GPU_PROFILE", "1");
            enabled = new(rd, Plan(true), 42, useIndirectStaging: false);
            var input = FamilyCandidateSet.Dense(new SearchBatch(0x100000003, 513));
            var expected = control.Execute(input, CancellationToken.None, out var cm);
            var actual = enabled.Execute(input, CancellationToken.None, out var em);
            Require(expected.ExportAbi1().Span.SequenceEqual(actual.ExportAbi1().Span) && cm.Processed == em.Processed &&
                cm.CurseSurvivors == em.CurseSurvivors && cm.PairSurvivors == em.PairSurvivors && cm.Dispatches == em.Dispatches,
                "Real timestamp instrumentation changed output/checkpoints/dispatch topology.");
            var field = typeof(NeowFamilyGpuExecutor).GetField("_gpuProfilingSession", instance)!;
            Require(field.GetValue(control) is null, "Diagnostic runtime control unexpectedly enabled sampling.");
            object collector = field.GetValue(enabled) ?? throw new InvalidDataException("Explicit profiling opt-in did not create a collector.");
            var value = Snapshot(collector);
            Require(!Disabled(value) && Number(value, "Samples") == 1 && Number(value, "PhysicalWindows") == 1 &&
                Number(value, "Submissions") == 1 && Number(value, "Roots") == 513 && Current() is null,
                "Real local-RD timestamp pair did not complete in the existing Sync.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("RT2_GPU_PROFILE", old);
            enabled?.Dispose(); control?.Dispose();
        }
        Emit(new { phase = "gpuProfiling", result = "PASS", implementation = "DiagnosticOnly", sampleEvery = 256,
            batchesChecked = 1024, expectedSamples = 5, nestedFallbackAndEmpty = true,
            timingFailuresDisabled = disabledCases, cancellationReuse = true, boundedStorage = true,
            timestampUnit = "Nanoseconds", exactUnitConversion = true, implausibleIntervalsRejected = intervalRejected,
            noMarkerBetweenPairedWindows = true, realLocalRd = true, explicitRuntimeOptIn = true,
            unchangedOutputAndDispatches = true, calibration = false });
    }
}
