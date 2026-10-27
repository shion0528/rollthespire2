using System.Diagnostics;
using RolltheSpire2.Search.Runtime;

int passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    passed++;
}

// Separate producer/Exact jobs share the same ceiling even when their async
// operations overlap; draining one restores every permit for the next batch.
using (var budget = new SearchCpuConcurrencyBudget(3))
using (budget.EnterScope())
{
    int active = 0, peak = 0, finished = 0;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            using var slot = SearchCpuConcurrencyBudget.Acquire(cts.Token);
            int count = Interlocked.Increment(ref active);
            int before;
            do { before = Volatile.Read(ref peak); }
            while (count > before && Interlocked.CompareExchange(ref peak, count, before) != before);
            Thread.Sleep(3);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref finished);
        }))));
    Check(finished == 48 && active == 0, "All concurrent CPU work must finish");
    Check(peak <= 3 && peak > 1, "CPU ceiling must be shared and parallel");
    // A child ceiling must restore its parent's remaining permit on disposal.
    using (var nested = new SearchCpuConcurrencyBudget(1))
    using (nested.EnterScope())
    using (SearchCpuConcurrencyBudget.Acquire(cts.Token)) { }
    using var one = SearchCpuConcurrencyBudget.Acquire(cts.Token);
    using var two = SearchCpuConcurrencyBudget.Acquire(cts.Token);
    using var three = SearchCpuConcurrencyBudget.Acquire(cts.Token);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { using var impossible = SearchCpuConcurrencyBudget.Acquire(cancelled.Token); }
    catch (OperationCanceledException) { passed++; }
    Check(passed == 3, "Acquiring an exhausted CPU budget must honor cancellation");
}

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var unlimited = new SearchDutyCycleLimiter(100);
long timestamp = Stopwatch.GetTimestamp();
unlimited.RecordWork(timestamp - Stopwatch.Frequency, timestamp);
Check(unlimited.WaitAsync(lifetime.Token).IsCompletedSuccessfully,
    "100% must have no admission delay");

// Use sleeps as stand-ins for completed device calls. The limiter itself sleeps
// asynchronously and never burns cycles to manufacture utilization.
var paced = new SearchDutyCycleLimiter(50);
var watch = Stopwatch.StartNew();
for (int batch = 0; batch < 12; batch++)
{
    await paced.WaitAsync(lifetime.Token);
    long started = Stopwatch.GetTimestamp();
    Thread.Sleep(5);
    paced.RecordWork(started, Stopwatch.GetTimestamp());
}
await paced.WaitAsync(lifetime.Token);
Check(watch.ElapsedMilliseconds >= 100, "50% pacing must insert meaningful idle time");

var low = new SearchDutyCycleLimiter(1);
timestamp = Stopwatch.GetTimestamp();
low.RecordWork(timestamp - Stopwatch.Frequency / 10, timestamp);
using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
watch.Restart();
try { await low.WaitAsync(stop.Token); throw new Exception("Low GPU budget ignored cancellation"); }
catch (OperationCanceledException) { passed++; }
Check(watch.ElapsedMilliseconds < 1000, "Cancellation must interrupt a long low-budget wait");

// A long idle interval must remove old debt, without granting burst credits.
var idle = new SearchDutyCycleLimiter(50);
timestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
idle.RecordWork(timestamp - Stopwatch.Frequency / 100, timestamp);
Check(idle.WaitAsync(lifetime.Token).IsCompletedSuccessfully, "Old duty-cycle debt must expire during idle");
Console.WriteLine($"Passed {passed} resource runtime checks; 50% pacing, shared CPU concurrency, nesting, cancellation and 100% bypass.");
