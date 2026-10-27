using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

// AsyncLocal keeps concurrent previews/searches separate. Dedicated GPU owner
// work items explicitly restore their captured snapshot instead of depending on
// the execution context inherited when their long-lived thread was created.
internal static class SearchOptimizationScope
{
    private static readonly AsyncLocal<SearchOptimizationSettings?> Ambient = new();
    internal static SearchOptimizationSettings Current => Ambient.Value ?? SearchOptimizationSettings.AllEnabled;

    internal static IDisposable Enter(SearchOptimizationSettings? settings)
    {
        settings ??= SearchOptimizationSettings.AllEnabled;
        if (Current == settings) return EmptyScope.Instance;
        SearchOptimizationSettings? previous = Ambient.Value;
        Ambient.Value = settings;
        return new RestoreScope(previous);
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class RestoreScope(SearchOptimizationSettings? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Ambient.Value = previous;
        }
    }
}
