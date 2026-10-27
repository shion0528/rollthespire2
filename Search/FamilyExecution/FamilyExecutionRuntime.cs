using System.Collections.Concurrent;
using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Runtime;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>
/// Session-scoped execution services shared by every Family. Search remains
/// externally asynchronous while the single local RenderingDevice is operated
/// synchronously by one bounded, dedicated owner thread.
/// </summary>
public sealed class FamilyExecutionContext : IAsyncDisposable
{
    private readonly FamilyGpuExecutionOwner? _gpuOwner;
    private readonly SearchDutyCycleLimiter _gpuBudget;
    private readonly SearchCpuConcurrencyBudget? _cpuBudget;
    internal SearchOptimizationSettings Optimizations { get; }
    private readonly FamilyPipelineBoundaryAudit? _pipelineBoundaryAudit = FamilyPipelineBoundaryAudit.TryCreate();

    internal FamilyExecutionContext(bool requiresGpu, bool failOnPhysicalRecovery = false,
        SearchResourceBudget? resources = null, int cpuWorkers = 64,
        SearchOptimizationSettings? optimizations = null)
    {
        Optimizations = optimizations ?? SearchOptimizationScope.Current;
        using var optimizationScope = SearchOptimizationScope.Enter(Optimizations);
        if (requiresGpu && resources?.GpuPercent == 0)
            throw new InvalidOperationException("GpuExecutionDisabledByResourceBudget");
        _gpuBudget = new(resources?.GpuPercent ?? 100);
        _cpuBudget = resources is null ? null : new(Math.Min(cpuWorkers,
            resources.ResolveCpuWorkers(System.Environment.ProcessorCount)));
        _gpuOwner = requiresGpu ? new FamilyGpuExecutionOwner() : null;
        FailOnPhysicalRecovery = failOnPhysicalRecovery;
    }

    internal bool FailOnPhysicalRecovery { get; }

    internal int GpuOwnerThreadId => _gpuOwner?.OwnerThreadId ?? 0;
    internal bool GpuOwnerStable => _gpuOwner?.IsStable ?? true;

    internal SearchCpuConcurrencyBudget.Scope EnterCpuBudget() =>
        _cpuBudget?.EnterScope() ?? default;

    internal ValueTask<T> ExecuteGpuAsync<T>(
        Func<RenderingDevice, T> action,
        CancellationToken cancellationToken)
    {
        using var optimizationScope = SearchOptimizationScope.Enter(Optimizations);
        return _gpuOwner is null
            ? ValueTask.FromException<T>(new InvalidOperationException("FamilyGpuOwnerUnavailable"))
            : _gpuBudget.IsUnlimited || !cancellationToken.CanBeCanceled
                ? _gpuOwner.ExecuteAsync(action, cancellationToken)
                : ExecuteBudgetedGpuAsync(action, cancellationToken);
    }

    private async ValueTask<T> ExecuteBudgetedGpuAsync<T>(Func<RenderingDevice, T> action,
        CancellationToken cancellationToken)
    {
        await _gpuBudget.WaitAsync(cancellationToken).ConfigureAwait(false);
        var measured = await _gpuOwner!.ExecuteAsync(rd =>
        {
            long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
            T result = action(rd);
            return (Result: result, ActiveTicks: FamilyGpuComputeUtility.CompletedSubmissionTicks - before,
                Completed: FamilyGpuComputeUtility.LastSubmissionCompleted);
        }, cancellationToken).ConfigureAwait(false);
        // Compilation, resource allocation and owner initialization are startup,
        // not GPU duty. A small budget must not multiply a shader compile into
        // a minutes-long pause before the first real submission.
        _gpuBudget.RecordWork(measured.Completed - measured.ActiveTicks, measured.Completed);
        return measured.Result;
    }

    internal void RecordPipelineBoundarySource(
        SearchBatch batch,
        MerchantShopColorlessGpuBatchMetrics metrics,
        long ownerCompletionTimestamp,
        int sourceBatchNumber) =>
        _pipelineBoundaryAudit?.RecordSource(batch, metrics, ownerCompletionTimestamp, sourceBatchNumber);

    internal void CompletePipelineBoundaryTarget(
        SearchBatch batch,
        RelicFamilyGpuBatchMetrics metrics,
        long targetOwnerStartTimestamp,
        bool targetDispatched) =>
        _pipelineBoundaryAudit?.CompleteTarget(batch, metrics, targetOwnerStartTimestamp, targetDispatched);

    public async ValueTask DisposeAsync()
    {
        using var optimizationScope = SearchOptimizationScope.Enter(Optimizations);
        try { _pipelineBoundaryAudit?.WriteSummary(); }
        finally
        {
            try
            {
                if (_gpuOwner is not null)
                    await _gpuOwner.DisposeAsync().ConfigureAwait(false);
            }
            finally { _cpuBudget?.Dispose(); }
        }
    }
}

internal sealed class FamilyGpuExecutionOwner : IAsyncDisposable
{
    private readonly long _requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    [ThreadStatic] private static bool _firstSubmitObserved;
    internal static bool ObserveFirstSubmit(string family = "Unknown")
    {
        if (_firstSubmitObserved) return false;
        _firstSubmitObserved=true;
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=FirstSubmit;family={Sanitize(family)};ownerThreadId={System.Environment.CurrentManagedThreadId};processId={System.Environment.ProcessId}");
        return true;
    }
    private const int QueueCapacity = 1;
    private readonly BlockingCollection<IWorkItem> _queue =
        new(new ConcurrentQueue<IWorkItem>(), QueueCapacity);
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<RenderingDevice?> _createDevice;
    private readonly FamilyGpuInitialization? _initialization;
    private readonly long _initializationAttempt;
    private int _disposed;
    private int _faulted;
    private Exception? _ownerFault;

    public FamilyGpuExecutionOwner() : this(() => RenderingServer.CreateLocalRenderingDevice(),
        FamilyDeviceProfileFoundation.Initialization)
    {
    }

    internal FamilyGpuExecutionOwner(Func<RenderingDevice?> createDevice,
        FamilyGpuInitialization? initialization = null, bool explicitRetry = false)
    {
        ArgumentNullException.ThrowIfNull(createDevice);
        _createDevice = createDevice;
        _initialization = initialization;
        var previousState = initialization?.Capture().State ?? FamilyGpuInitializationState.Unverified;
        try { _initializationAttempt = initialization?.BeginCreation(explicitRetry) ?? 0; }
        catch { _queue.Dispose(); throw; }
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=GpuOwnerRequested;processId={System.Environment.ProcessId};lifetime={(explicitRetry ? "InitializationProbe" : "PerSearch")}");
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RolltheSpire2.FamilyExecution.GpuOwner"
        };
        try { _thread.Start(); }
        catch
        {
            // Thread startup is not a Local RenderingDevice creation failure.
            _initialization?.CreationAborted(_initializationAttempt, previousState);
            _initialization?.OwnerStopped();
            _queue.Dispose();
            throw;
        }
    }

    public int OwnerThreadId { get; private set; }
    public bool IsStable => Volatile.Read(ref _faulted) == 0;

    public ValueTask<T> ExecuteAsync<T>(Func<RenderingDevice, T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        Exception? ownerFault = Volatile.Read(ref _ownerFault);
        if (ownerFault is not null)
            return ValueTask.FromException<T>(ownerFault);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var item = new WorkItem<T>(action, cancellationToken);
        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            CancellationToken token = exception.CancellationToken.CanBeCanceled
                ? exception.CancellationToken
                : cancellationToken;
            item.Cancel(token);
        }
        catch (InvalidOperationException ex)
        {
            item.Fail(Volatile.Read(ref _ownerFault) ??
                new ObjectDisposedException(nameof(FamilyGpuExecutionOwner), ex));
        }
        return new ValueTask<T>(item.Task);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _stopped.Task.ConfigureAwait(false);
            return;
        }

        _queue.CompleteAdding();
        try { await _stopped.Task.ConfigureAwait(false); }
        finally { _queue.Dispose(); }
    }

    private void ThreadMain()
    {
        RenderingDevice? device = null;
        OwnerThreadId = Thread.CurrentThread.ManagedThreadId;
        RuntimeLog.TryBackgroundInfo(
            $"familyGpuOwnerStarted=true;ownerThreadId={OwnerThreadId};queueCapacity={QueueCapacity};" +
            "renderingDeviceExecution=Synchronous");
        try
        {
            try
            {
                device = _createDevice() ??
                         throw new InvalidOperationException("FamilyGpuRenderingDeviceUnavailable: CreateLocalRenderingDevice returned null; see Godot log for native driver details.");
            }
            catch (Exception creationFailure)
            {
                _initialization?.CreationFailed(_initializationAttempt, creationFailure);
                throw;
            }
            _initialization?.CreationSucceeded(_initializationAttempt);
            RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=GpuOwnerReady;ownerThreadId={OwnerThreadId};requestedToReadyMs={System.Diagnostics.Stopwatch.GetElapsedTime(_requestedAt).TotalMilliseconds:F4};processId={System.Environment.ProcessId}");
            try
            {
                LogDeviceFacts(device);
                FamilyDeviceProfileFoundation.ObserveFamilyComputeAvailable(device.GetDeviceName()?.Trim() ?? string.Empty);
            }
            catch (Exception diagnosticsFailure)
            { RuntimeLog.TryBackgroundWarning("familyGpuDeviceDiagnosticsFailed=true;failure=" + diagnosticsFailure); }
            foreach (IWorkItem item in _queue.GetConsumingEnumerable())
                item.Execute(device);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _ownerFault, ex);
            Interlocked.Exchange(ref _faulted, 1);
            try { _queue.CompleteAdding(); }
            catch { }
            while (_queue.TryTake(out IWorkItem? pending))
                pending.Fail(ex);
            RuntimeLog.TryBackgroundWarning(
                $"familyGpuOwnerFault=true;ownerThreadId={OwnerThreadId};" +
                $"failure={ex.GetType().Name}:{Sanitize(ex.Message)}");
        }
        finally
        {
            Exception? releaseFailure = null;
            if (device is not null)
            {
                // Work items finish their own Submit/Sync pairs. An extra Sync
                // without an outstanding Submit is a Godot API error.
                try { device.Free(); }
                catch (Exception freeFailure)
                {
                    releaseFailure = freeFailure;
                    try { device.Dispose(); }
                    catch (Exception disposeFailure)
                    {
                        releaseFailure = new AggregateException("FamilyGpuOwner.DeviceReleaseFailed", freeFailure, disposeFailure);
                    }
                    Interlocked.Exchange(ref _faulted, 1);
                    RuntimeLog.TryBackgroundWarning($"familyGpuDeviceReleaseFailed=true;ownerThreadId={OwnerThreadId};failure={Sanitize(releaseFailure.ToString())}");
                }
            }
            RuntimeLog.TryBackgroundInfo(
                $"familyGpuOwnerStopped=true;ownerThreadId={OwnerThreadId};deviceCreated={device is not null};deviceReleased={device is not null && releaseFailure is null}");
            _initialization?.OwnerStopped();
            if (releaseFailure is null) _stopped.TrySetResult(true);
            else _stopped.TrySetException(releaseFailure);
        }
    }

    private void LogDeviceFacts(RenderingDevice device)
    {
        // Report the engine's actual limits. These are diagnostic facts, not a
        // new admission policy or an automatic change to batch geometry.
        using var version = Engine.GetVersionInfo();
        RenderingDevice.Limit[] limits =
        [
            RenderingDevice.Limit.MaxStorageBuffersPerUniformSet,
            RenderingDevice.Limit.MaxStorageBuffersPerShaderStage,
            RenderingDevice.Limit.MaxComputeSharedMemorySize,
            RenderingDevice.Limit.MaxComputeWorkgroupInvocations,
            RenderingDevice.Limit.MaxComputeWorkgroupSizeX,
            RenderingDevice.Limit.MaxComputeWorkgroupSizeY,
            RenderingDevice.Limit.MaxComputeWorkgroupSizeZ,
            RenderingDevice.Limit.MaxComputeWorkgroupCountX,
            RenderingDevice.Limit.MaxComputeWorkgroupCountY,
            RenderingDevice.Limit.MaxComputeWorkgroupCountZ
        ];
        string facts = string.Join(';', limits.Select(limit => $"{limit}={device.LimitGet(limit)}"));
        RuntimeLog.TryBackgroundInfo($"familyGpuDeviceFacts=true;ownerThreadId={OwnerThreadId};godot={Sanitize(version["string"].AsString())};driver={Sanitize(RenderingServer.GetCurrentRenderingDriverName())};vendor={Sanitize(device.GetDeviceVendorName())};device={Sanitize(device.GetDeviceName())};pipelineCacheUuid={Sanitize(device.GetDevicePipelineCacheUuid())};{facts}");
    }

    private interface IWorkItem
    {
        void Execute(RenderingDevice device);
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<RenderingDevice, T> _action;
        private readonly SearchOptimizationSettings _optimizations = SearchOptimizationScope.Current;
        private readonly string _logSession = RuntimeLog.SessionId;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<RenderingDevice, T> action, CancellationToken cancellationToken)
        {
            _action = action;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public void Cancel(CancellationToken cancellationToken) =>
            _completion.TrySetCanceled(cancellationToken);

        public void Execute(RenderingDevice device)
        {
            using var optimizationScope = SearchOptimizationScope.Enter(_optimizations);
            using var logScope = RuntimeLog.SearchScope(_logSession);
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(_action(device));
            }
            catch (OperationCanceledException exception)
            {
                CancellationToken token = exception.CancellationToken.CanBeCanceled
                    ? exception.CancellationToken
                    : _cancellationToken;
                _completion.TrySetCanceled(token);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    private static string Sanitize(string value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace(';', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
