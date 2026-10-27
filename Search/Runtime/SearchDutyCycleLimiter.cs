using System.Diagnostics;

namespace RolltheSpire2.Search.Runtime;

/// <summary>
/// Paces useful work, never creates synthetic load. This is a workload duty-cycle
/// budget, not a promise about an OS-wide utilization counter. Owned by one
/// serial producer; the 100% path performs no delay or clock sampling.
/// </summary>
internal sealed class SearchDutyCycleLimiter(int percent)
{
    private readonly int _percent = Math.Clamp(percent, 1, 100);
    private double _eligibleAt;
    // Coalesce sub-millisecond pauses instead of rounding every fast GPU batch
    // up to a complete timer tick. The resulting short bursts are intentional.
    private static readonly double MinimumDelayTicks = Stopwatch.Frequency * .004;

    internal bool IsUnlimited => _percent == 100;

    internal void RecordWork(long started, long completed)
    {
        if (IsUnlimited || completed <= started) return;
        // An idle producer earns no credits for a future burst. Already accrued
        // debt is retained when several small batches run within one timer tick.
        _eligibleAt = Math.Max(_eligibleAt, started) +
            (completed - started) * (100d / _percent);
    }

    internal async ValueTask WaitAsync(CancellationToken token)
    {
        if (IsUnlimited) return;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double remaining = _eligibleAt - Stopwatch.GetTimestamp();
            if (remaining < MinimumDelayTicks) return;
            // Bounded cancellable waits also avoid overflowing Task.Delay after
            // an unusually long driver call at a very small configured budget.
            double milliseconds = Math.Min(100, remaining * 1000 / Stopwatch.Frequency);
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), token).ConfigureAwait(false);
        }
    }
}
