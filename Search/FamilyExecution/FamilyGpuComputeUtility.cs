using System.Reflection;
using Godot;
using RolltheSpire2.Bootstrap;

namespace RolltheSpire2.Search.FamilyExecution;

/// <summary>Minimal topology-neutral RenderingDevice helpers used by GPU Families.</summary>
internal static class FamilyGpuComputeUtility
{
    // A failed initialization followed by a failed release leaves resource
    // ownership uncertain. Preserve this fact through module/wrapper exceptions
    // so an optional allocation fallback cannot silently reuse the device.
    internal sealed class InitializationCleanupException(string scope, Exception primary, Exception cleanup)
        : AggregateException(scope + ".InitializationAndCleanup", primary, cleanup) { }

    internal static bool HasInitializationCleanupFailure(Exception failure) =>
        failure is InitializationCleanupException ||
        (failure is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(HasInitializationCleanupFailure)
            : failure.InnerException is { } inner && HasInitializationCleanupFailure(inner));

    private const string VisibleSeedRootHashMarker = "/*__RT2_VISIBLE_SEED_ROOT_HASH__*/";
    private const string VisibleSeedRootHashCommonSuffix = "VisibleSeedRootHashCommon.glsl";

    [ThreadStatic] private static ShaderReuse? _shaderReuse;
    [ThreadStatic] private static long _completedSubmissionTicks;
    [ThreadStatic] private static long _lastSubmissionCompleted;
    internal static long CompletedSubmissionTicks => _completedSubmissionTicks;
    internal static long LastSubmissionCompleted => _lastSubmissionCompleted;
    [ThreadStatic] private static Dictionary<(RenderingDevice, Rid), ShaderReuse>? _borrowedShaders;

    // Owner-thread, search-local module reuse. Executors still own their pipelines
    // and uniforms; dispose every borrower before disposing this module owner.
    internal sealed class ShaderReuse(RenderingDevice rd) : IDisposable
    {
        private readonly Dictionary<string, Rid> _modules = new(StringComparer.Ordinal);
        private readonly Dictionary<Rid, int> _borrowers = [];
        internal IDisposable Activate()
        {
            var previous = _shaderReuse; _shaderReuse = this;
            return new Scope(() => _shaderReuse = previous);
        }
        internal bool Applies(RenderingDevice device) => ReferenceEquals(rd, device);
        internal bool TryBorrow(string source, out Rid shader)
        {
            if (!_modules.TryGetValue(source, out shader)) return false;
            _borrowers[shader]++; return true;
        }
        internal void Add(string source, Rid shader)
        {
            _modules.Add(source, shader); _borrowers.Add(shader, 1);
            (_borrowedShaders ??= []).Add((rd, shader), this);
        }
        internal void Release(Rid shader)
        {
            if (--_borrowers[shader] < 0) throw new InvalidOperationException("ShaderReuse.DoubleRelease");
        }
        public void Dispose()
        {
            if (_borrowers.Values.Any(count => count != 0)) throw new InvalidOperationException("ShaderReuse.LiveBorrower");
            List<Exception>? failures = null;
            foreach (Rid shader in _modules.Values)
            {
                try { rd.FreeRid(shader); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
                finally { _borrowedShaders!.Remove((rd, shader)); }
            }
            _modules.Clear(); _borrowers.Clear();
            if (failures is not null) throw new AggregateException("ShaderReuse.ResourceReleaseFailed", failures);
        }
        private sealed class Scope(Action close) : IDisposable
        {
            public void Dispose() => close();
        }
    }

    public static Rid CompileShader(RenderingDevice rd, string sourceText, string name)
    {
        var reuse = _shaderReuse is { } current && current.Applies(rd) ? current : null;
        if (reuse is not null && reuse.TryBorrow(sourceText, out var cached))
        {
            RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=ShaderModuleReused;name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId};cache=SearchLocalSourceReuse");
            return cached;
        }
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(sourceText);
        string sourceHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes));
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=ShaderCompileStarted;name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId};sourceSha256={sourceHash};sourceBytes={sourceBytes.Length};allowCache=true;engineCacheHit=Unknown");
        using var source = new RDShaderSource { SourceCompute = sourceText };
        using RDShaderSpirV spirV = rd.ShaderCompileSpirVFromSource(source, allowCache: true);
        if (spirV is null) throw CreationFailure("ShaderCompile", name, "NoSpirV");
        string error = spirV.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrWhiteSpace(error))
            throw CreationFailure("ShaderCompile", name, error);
        byte[] bytecode = spirV.GetStageBytecode(RenderingDevice.ShaderStage.Compute);
        if (bytecode.Length == 0) throw CreationFailure("ShaderCompile", name, "EmptyComputeSpirV");
        double compileMs=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string spirvHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytecode));
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=ShaderModuleCreateStarted;name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId};sourceSha256={sourceHash};spirvSha256={spirvHash};spirvBytes={bytecode.Length};compileSpirvMs={compileMs:F4}");
        started=System.Diagnostics.Stopwatch.GetTimestamp();
        Rid shader=rd.ShaderCreateFromSpirV(spirV, name);
        // The creation API returns an empty RID on failure. IsValid only tests
        // that fresh result; it must never be used to prove a RID is still owned.
        if (!shader.IsValid) throw CreationFailure("ShaderModule", name, "EmptyRid;seeGodotNativeLog=true");
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=ShaderModuleReady;name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId};sourceSha256={sourceHash};spirvSha256={spirvHash};compileSpirvMs={compileMs:F4};moduleMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F4};allowCache=true;engineCacheHit=Unknown");
        reuse?.Add(sourceText, shader);
        return shader;
    }

    internal static Rid CreateComputePipeline(RenderingDevice rd,Rid shader, string name = "Family")
    {
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=ComputePipelineCreateStarted;name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId}");
        Rid pipeline=rd.ComputePipelineCreate(shader);
        if (!rd.ComputePipelineIsValid(pipeline)) throw CreationFailure("ComputePipeline", name, "InvalidPipeline;seeGodotNativeLog=true");
        RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=ComputePipelineReady;name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId};elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F4}");
        return pipeline;
    }

    public static Rid CreateStorageBuffer(RenderingDevice rd, byte[] bytes, string name = "Storage")
    {
        if (bytes.Length == 0) bytes = new byte[sizeof(uint)];
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        Rid buffer=rd.StorageBufferCreate(checked((uint)bytes.Length), bytes);
        if (!buffer.IsValid) throw CreationFailure("StorageBuffer", name, $"EmptyRid;bytes={bytes.Length};initialBytes={bytes.Length}");
        TraceBuffer(started,bytes.Length,name,bytes.Length);return buffer;
    }

    // Allocation does not upload zeros. Callers initialize counters explicitly
    // and only read payload entries written by the current dispatch.
    public static Rid CreateZeroedStorageBuffer(RenderingDevice rd, int byteCount, string name = "Workspace")
    {
        if (byteCount < sizeof(uint)) byteCount = sizeof(uint);
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        Rid buffer=rd.StorageBufferCreate(checked((uint)byteCount), Array.Empty<byte>());
        if (!buffer.IsValid) throw CreationFailure("StorageBuffer", name, $"EmptyRid;bytes={byteCount};initialBytes=0");
        TraceBuffer(started,byteCount,name,0);return buffer;
    }

    private static void TraceBuffer(long started,int bytes,string name,int initialBytes) => RuntimeLog.TryBackgroundDetail(
        $"searchStartup=true;phase=BufferReady;name={Sanitize(name)};ownerThreadId={System.Environment.CurrentManagedThreadId};bytes={bytes};initialBytes={initialBytes};elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F4}");

    public static Rid CreateUniformSet(RenderingDevice rd, Rid shader, IReadOnlyList<Rid> buffers, string name = "Family")
    {
        var uniforms = new Godot.Collections.Array<RDUniform>();
        try
        {
            for (int index = 0; index < buffers.Count; index++)
            {
                var uniform = new RDUniform
                {
                    UniformType = RenderingDevice.UniformType.StorageBuffer,
                    Binding = index
                };
                uniform.AddId(buffers[index]);
                uniforms.Add(uniform);
            }
            Rid set = rd.UniformSetCreate(uniforms, shader, 0);
            if (!rd.UniformSetIsValid(set)) throw CreationFailure("UniformSet", name, $"InvalidUniformSet;set=0;storageBindings={buffers.Count};seeGodotNativeLog=true");
            RuntimeLog.TryBackgroundDetail($"searchStartup=true;phase=UniformSetReady;name={Sanitize(name)};stage=Compute;set=0;storageBindings={buffers.Count}");
            return set;
        }
        finally { foreach (RDUniform uniform in uniforms) uniform.Dispose(); }
    }

    internal static (double SubmitMs, double SyncMs) SubmitAndSync(RenderingDevice rd, string family)
    {
        bool first = FamilyGpuExecutionOwner.ObserveFirstSubmit(family);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        rd.Submit();
        double submitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (first) RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=FirstSubmitReturned;family={Sanitize(family)};ownerThreadId={System.Environment.CurrentManagedThreadId}");
        // Cancellation belongs before submission or after completion. Never
        // leave a submitted local device unsynchronized while unwinding.
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        rd.Sync();
        _lastSubmissionCompleted = System.Diagnostics.Stopwatch.GetTimestamp();
        _completedSubmissionTicks += _lastSubmissionCompleted - start;
        _completedSubmissionTicks += (long)(submitMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        double syncMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (first) RuntimeLog.TryBackgroundInfo($"searchStartup=true;phase=FirstSyncCompleted;family={Sanitize(family)};ownerThreadId={System.Environment.CurrentManagedThreadId}");
        return (submitMs, syncMs);
    }

    private static InvalidOperationException CreationFailure(string operation, string name, string detail)
    {
        RuntimeLog.TryBackgroundWarning($"familyGpuCreationFailed=true;operation={operation};name={Sanitize(name)};stage=Compute;ownerThreadId={System.Environment.CurrentManagedThreadId};detail={Sanitize(detail)}");
        return new InvalidOperationException($"FamilyGpu.{operation}Failed:{name}:{detail}");
    }

    private static string Sanitize(string value) => value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');

    public static string LoadEmbeddedShader(string suffix)
    {
        Assembly assembly = typeof(FamilyGpuComputeUtility).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource) ??
                              throw new InvalidOperationException("Embedded Family shader missing: " + suffix);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string LoadFamilyShaderWithVisibleSeedRootHash(string suffix)
    {
        string source = LoadEmbeddedShader(suffix);
        if (!source.Contains(VisibleSeedRootHashMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("Family shader root-hash marker missing: " + suffix);
        return source.Replace(
            VisibleSeedRootHashMarker,
            LoadEmbeddedShader(VisibleSeedRootHashCommonSuffix),
            StringComparison.Ordinal);
    }

    public static byte[] ToBytes(uint[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(uint))];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static byte[] ToBytesNonEmpty(uint[] values) => values.Length == 0 ? new byte[sizeof(uint)] : ToBytes(values);

    public static uint[] FromUInt32Bytes(byte[] bytes)
    {
        if ((bytes.Length & 3) != 0) throw new InvalidDataException("Family GPU uint buffer is not aligned.");
        var output = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);
        return output;
    }

    internal static uint[] FromUInt32Bytes(byte[] bytes, uint expectedCount)
    {
        // Godot may report a native readback failure by returning an empty
        // buffer. Alignment alone cannot distinguish that from valid output.
        // Widen before multiplication so even a malformed count cannot wrap.
        if ((ulong)bytes.LongLength != (ulong)expectedCount * sizeof(uint))
            throw new InvalidDataException("Family GPU payload length does not match header count.");
        return expectedCount == 0 ? [] : FromUInt32Bytes(bytes);
    }

    public static void Update(RenderingDevice rd, Rid buffer, uint[] values, string label)
    {
        byte[] bytes = ToBytes(values);
        UpdateBytes(rd, buffer, bytes, label);
    }

    public static void UpdateBytes(RenderingDevice rd, Rid buffer, byte[] bytes, string label)
    {
        Error error = rd.BufferUpdate(buffer, 0u, checked((uint)bytes.Length), bytes);
        if (error != Error.Ok) throw new InvalidOperationException(label + ":" + error);
    }

    // Call from an initialization catch and rethrow the original exception after
    // successful cleanup. If both fail, retain both causes and their stacks.
    internal static void CleanupAfterFailure(Exception primary, Action cleanup, string scope)
    {
        try { cleanup(); }
        catch (Exception failure) { throw new InitializationCleanupException(scope, primary, failure); }
    }

    public static void FreeAll(RenderingDevice rd, List<Rid> owned)
    {
        List<Exception>? failures = null;
        for (int index = owned.Count - 1; index >= 0; index--)
        {
            try
            {
                if (_borrowedShaders is not null && _borrowedShaders.TryGetValue((rd, owned[index]), out var owner)) owner.Release(owned[index]);
                else rd.FreeRid(owned[index]);
            }
            catch (Exception ex)
            {
                RuntimeLog.TryBackgroundWarning($"familyGpuResourceReleaseFailed=true;ownerThreadId={System.Environment.CurrentManagedThreadId};ownedIndex={index};failure={Sanitize(ex.GetType().Name + ":" + ex.Message)}");
                (failures ??= []).Add(ex);
            }
        }
        owned.Clear();
        if (failures is not null) throw new AggregateException("PrivateGpu.ResourceReleaseFailed", failures);
    }
}
