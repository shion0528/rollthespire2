using Godot;
using RolltheSpire2.Search.FamilyExecution;

namespace RolltheSpire2.Bootstrap;

/// <summary>Opt-in verification against the game's own custom Godot runtime.</summary>
internal static class ResourceTransportProbe
{
    internal static void RunIfRequested()
    {
        if (System.Environment.GetEnvironmentVariable("RT2_VERIFY_RESOURCE_TRANSPORT") != "1") return;
        RuntimeLog.Info($"neowExecutionPolicy=true;prefixHashCache={NeowFamilyGpuExecutor.RequestPrefixHashCache.ToString().ToLowerInvariant()};" +
            $"countedIndirect={NeowFamilyGpuExecutor.RequestIndirectStaging.ToString().ToLowerInvariant()};" +
            $"dispatchRevision={NeowFamilyGpuExecutor.RequestedStagedDispatchRevision};prefixRevision={NeowFamilyGpuExecutor.RequestedPrefixHashRevision}");
        RenderingDevice? device = null;
        Rid buffer = default;
        Exception? failure = null;
        string revision = "not-created";
        try
        {
            device = RenderingServer.CreateLocalRenderingDevice()
                ?? throw new InvalidOperationException("ResourceTransportProbe.RenderingDeviceUnavailable");
            uint[] expected = [0x4e464d52, 1, 0, 0, 513, 42, 20, 4,
                0x4e464d52, 1, 0, 0, 1024, 43, 19, 3];
            buffer = FamilyGpuComputeUtility.CreateStorageBuffer(device, FamilyGpuComputeUtility.ToBytes(expected));
            var transport = new FamilyGpuHeaderReadback(device, requested: true);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                expected[2] = checked((uint)iteration);
                expected[10] = checked((uint)iteration + 1);
                FamilyGpuComputeUtility.Update(device, buffer, expected, "ResourceTransportProbe");
                uint[] actual = transport.CompleteRecorded(buffer, 16, true, "ResourceTransportProbe", () => { });
                if (!actual.AsSpan().SequenceEqual(expected) || transport.Revision != FamilyGpuHeaderReadback.QueuedRevision)
                    throw new InvalidDataException("ResourceTransportProbe.QueuedDownloadNotVerified");
            }
            revision = transport.Revision;
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (device is not null)
            {
                try { if (buffer.IsValid) device.FreeRid(buffer); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
                try { device.Free(); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            }
        }
        if (failure is not null) RuntimeLog.Fault("resourceTransportProbe=true;result=FAIL", failure);
        else RuntimeLog.Info($"resourceTransportProbe=true;result=PASS;iterations=3;bytes=64;revision={revision};gameCustomRuntime=true");
    }
}
