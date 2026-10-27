namespace RolltheSpire2.Search.Contracts;

/// <summary>
/// Search resource limits. Percentages bound concurrency and GPU admission;
/// they do not promise an operating-system utilization percentage.
/// </summary>
public sealed record SearchResourceBudget(int CpuPercent = 50, int GpuPercent = 100)
{
    public SearchResourceBudget Normalize() => this with
    {
        CpuPercent = Math.Clamp(CpuPercent, 1, 100),
        GpuPercent = Math.Clamp(GpuPercent, 0, 100)
    };

    public int ResolveCpuWorkers(int logicalProcessors)
    {
        SearchResourceBudget normalized = Normalize();
        long processors = Math.Max(1, logicalProcessors);
        return (int)Math.Clamp((processors * normalized.CpuPercent + 99) / 100, 1L, 64L);
    }
}
