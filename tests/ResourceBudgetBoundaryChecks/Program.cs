using RolltheSpire2.Search.Contracts;

int checks = 0;
void Equal<T>(T expected, T actual, string label)
{
    checks++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
}

Equal(new SearchResourceBudget(50, 100), new SearchResourceBudget(), "Defaults");
Equal(new SearchResourceBudget(1, 0), new SearchResourceBudget(int.MinValue, int.MinValue).Normalize(), "Lower limits");
Equal(new SearchResourceBudget(100, 100), new SearchResourceBudget(int.MaxValue, int.MaxValue).Normalize(), "Upper limits");
Equal(0, new SearchResourceBudget(50, 0).Normalize().GpuPercent, "GPU disabled remains valid");
Equal(16, new SearchResourceBudget().ResolveCpuWorkers(32), "Half of 32 logical processors");
Equal(32, new SearchResourceBudget(100).ResolveCpuWorkers(32), "Full 32 logical processors");
Equal(2, new SearchResourceBudget(50).ResolveCpuWorkers(3), "Fraction rounds upward");
Equal(1, new SearchResourceBudget(1).ResolveCpuWorkers(64), "Small budget still has one worker");
Equal(64, new SearchResourceBudget(100).ResolveCpuWorkers(256), "Runtime worker ceiling");
Equal(64, new SearchResourceBudget(100).ResolveCpuWorkers(int.MaxValue), "Multiplication cannot overflow");
Equal(1, new SearchResourceBudget().ResolveCpuWorkers(0), "Unavailable processor count");
Equal(1, new SearchResourceBudget().ResolveCpuWorkers(int.MinValue), "Invalid processor count");

// Exhaust the supported percentage range against representative machines.
int[] processorMatrix = [1, 2, 3, 4, 8, 16, 24, 32, 64, 96, 128, 256, int.MaxValue];
foreach (int processors in processorMatrix)
{
    int previous = 0;
    for (int percent = 1; percent <= 100; percent++)
    {
        var budget = new SearchResourceBudget(percent, 0);
        Equal(budget, budget.Normalize(), $"Valid budget {percent}%");
        int actual = budget.ResolveCpuWorkers(processors);
        Equal((int)Math.Min(64, Math.Ceiling((decimal)processors * percent / 100)), actual,
            $"Concurrency for {processors} processors at {percent}%");
        if (actual < previous || actual is < 1 or > 64)
            throw new InvalidOperationException("Worker budget must be monotonic and bounded");
        previous = actual;
    }
}
Console.WriteLine($"Passed {checks} resource-budget boundary checks.");
Console.WriteLine("Logical-processor matrix: " + string.Join(",", processorMatrix));
