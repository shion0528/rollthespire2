namespace RolltheSpire2.Search.Runtime;

/// <summary>One shared CPU concurrency ceiling for Family replay and Exact.</summary>
internal sealed class SearchCpuConcurrencyBudget(int workers) : IDisposable
{
    private static readonly AsyncLocal<SearchCpuConcurrencyBudget?> Current = new();
    private readonly SemaphoreSlim _slots = new(Math.Clamp(workers, 1, 64));

    internal Scope EnterScope()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Scope(previous, true);
    }

    internal static Lease Acquire(CancellationToken token)
    {
        var slots = Current.Value?._slots;
        slots?.Wait(token);
        return new Lease(slots);
    }

    internal readonly struct Scope(SearchCpuConcurrencyBudget? previous, bool entered) : IDisposable
    {
        public void Dispose() { if (entered) Current.Value = previous; }
    }

    internal readonly struct Lease(SemaphoreSlim? slots) : IDisposable
    {
        public void Dispose() => slots?.Release();
    }

    public void Dispose() => _slots.Dispose();
}
