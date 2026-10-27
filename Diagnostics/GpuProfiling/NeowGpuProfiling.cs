using RolltheSpire2.Diagnostics.GpuProfiling;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class NeowFamilyGpuExecutor
{
    private GpuProfilingSession? _gpuProfilingSession;
    private bool _gpuProfilingUnavailable;
    private readonly bool _gpuProfilingEnabled = System.Environment.GetEnvironmentVariable("RT2_GPU_PROFILE") == "1";

    partial void GpuProfilingBeginBatch(int inputCount)
    {
        try
        {
            if (GpuProfilingSession.Current is { } current) current.BeginBatch(inputCount);
            else if (_gpuProfilingEnabled && !_gpuProfilingUnavailable)
                (_gpuProfilingSession ??= new GpuProfilingSession(_rd)).BeginBatch(inputCount);
        }
        catch (Exception) { _gpuProfilingUnavailable = true; }
    }
    partial void GpuProfilingWindowBegin() => GpuProfilingSession.Current?.WindowBegin();
    partial void GpuProfilingWindowEnd() => GpuProfilingSession.Current?.WindowEnd();
    partial void GpuProfilingBeforeSubmit() => GpuProfilingSession.Current?.BeforeSubmit();
    partial void GpuProfilingAfterSync() => GpuProfilingSession.Current?.AfterSync();
    partial void GpuProfilingBatchCompleted(NeowFamilyGpuMetrics metrics) => GpuProfilingSession.Current?.BatchCompleted(metrics);
    partial void GpuProfilingEndBatch() => GpuProfilingSession.Current?.EndBatch();
    partial void GpuProfilingDispose() => _gpuProfilingSession?.Dispose();
}
