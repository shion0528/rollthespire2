using RolltheSpire2.Search.FamilyExecution;

internal static class HeaderRevisionChecks
{
    internal static int Run()
    {
        string queued = FamilyGpuHeaderReadback.QueuedRevision;
        string fallback = FamilyGpuHeaderReadback.FallbackRevision;
        string synchronous = FamilyGpuHeaderReadback.SynchronousRevision;
        var cases = new[]
        {
            (queued, queued, queued), (fallback, fallback, fallback), (synchronous, synchronous, synchronous),
            (queued, fallback, "E." + queued + ".W." + fallback),
            (fallback, queued, "E." + fallback + ".W." + queued),
            (FamilyGpuHeaderReadback.EmptyRevision, queued, "E." + FamilyGpuHeaderReadback.EmptyRevision + ".W." + queued)
        };
        foreach (var (eventRevision, worldRevision, expected) in cases)
            if (EwPrivateGpuExecutor.CombineHeaderReadbackRevisions(eventRevision, worldRevision) != expected)
                throw new InvalidDataException("EW component header transports lost their distinct performance identity.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { phase = "ewHeaderRevision", result = "PASS",
            cases = cases.Length, mixedFallbackSeparated = true, gpuOrGameRequired = false }));
        return cases.Length;
    }
}
