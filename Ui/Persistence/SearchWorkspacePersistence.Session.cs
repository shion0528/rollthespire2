using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Ui.Persistence;

internal sealed partial class SearchWorkspacePersistence
{
    // Shells in the menu and a run must not overwrite one another's final cursor.
    // Access and polling are confined to the Godot main thread.
    private static readonly Dictionary<string, WeakReference<SearchWorkspacePersistence>> OpenStores =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal static SearchWorkspacePersistence Open(string directory, RuntimeProfileId profile)
    {
        string key = Path.GetFullPath(directory);
        if (OpenStores.TryGetValue(key, out var reference) && reference.TryGetTarget(out var existing)) return existing;
        var store = new SearchWorkspacePersistence(directory, profile, initializeSearchCursor: false);
        OpenStores[key] = new(store);
        return store;
    }

    private IProductionSearchSession? _finishingSearch;
    private string _finishingFingerprint = "";
    private SceneTree? _finishingTree;
    private long _finishingFlushRetryAfter;
    internal bool HasFinishingSearch => _finishingSearch is not null;
    private IProductionSearchSession? _activeSearch;
    private IProductionSearchSession? _receivingSearch;
    private SearchResultReceiver? _resultReceiver;
    private long _storedReceivedResults;
    internal bool HasSearchInFlight => _activeSearch is not null || HasFinishingSearch;
    internal void TrackSearch(IProductionSearchSession session) => _activeSearch = session;
    internal void BeginResultReception(IProductionSearchSession session, int capacity)
    {
        if (ReferenceEquals(_receivingSearch, session)) return;
        if (_resultReceiver is not null) throw new InvalidOperationException("PreviousSearchResultsNotReleased");
        if (capacity is < 1 or > SearchResultReceiver.MaximumUiCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _receivingSearch = session; _storedReceivedResults = 0;
        _resultReceiver = new SearchResultReceiver(session, capacity);
    }
    internal Exception? ResultReceptionFailure(IProductionSearchSession session) =>
        ReferenceEquals(_receivingSearch, session) ? _resultReceiver?.Failure : null;
    internal bool SessionResultsFullyDrained(IProductionSearchSession session) => session.Completion.IsCompleted &&
        (!ReferenceEquals(_receivingSearch, session) || _resultReceiver?.IsFullyDrained == true);
    internal bool TryCompleteResultReception(IProductionSearchSession session, string fingerprint)
    {
        if (!SessionResultsFullyDrained(session)) return false;
        ObserveSessionCheckpoint(session, fingerprint);
        SaveResultProgress(session.GetProgress());
        if (!TryFlushAll() || !TryFlushSearchCheckpoint()) return false;
        ReleaseSearch(session);
        return true;
    }

    internal int DrainReceivedResults(IProductionSearchSession session, string fingerprint,
        List<SearchCandidate>? uiResults = null, int maximum = SearchResultReceiver.UiDrainBatchSize)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        SearchResultReceiver? receiver = ReferenceEquals(_receivingSearch, session) ? _resultReceiver : null;
        var batch = new List<SearchCandidate>(Math.Min(maximum, SearchResultReceiver.MaximumUiCapacity));
        // The legacy/untracked path is retained for existing session test seams
        // and startup failure cleanup. Normal UI owns an asynchronous receiver.
        while (receiver is null || batch.Count < maximum)
        {
            bool read = receiver is null ? session.TryReadCandidate(out var candidate) : receiver.TryReadCandidate(out candidate);
            if (!read) break;
            if (candidate is not null) batch.Add(candidate);
        }
        AppendResults(batch, fingerprint);
        if (receiver is not null) _storedReceivedResults += batch.Count;
        uiResults?.AddRange(batch);
        return batch.Count;
    }

    internal void ReleaseSearch(IProductionSearchSession session)
    {
        if (ReferenceEquals(_receivingSearch, session))
        {
            if (_resultReceiver?.IsFullyDrained != true) throw new InvalidOperationException("SearchResultTailNotDrained");
            _resultReceiver.ReceptionCompletion.GetAwaiter().GetResult();
            _receivingSearch = null; _resultReceiver = null; _storedReceivedResults = 0;
        }
        if (ReferenceEquals(_activeSearch, session)) _activeSearch = null;
    }

    internal void FinishDetachedSearch(IProductionSearchSession session, string fingerprint, SceneTree tree)
    {
        if (_finishingSearch is not null) throw new InvalidOperationException("SearchHandoffAlreadyActive");
        _finishingSearch = session; _finishingFingerprint = fingerprint;
        _finishingFlushRetryAfter = 0;
        session.Cancel();
        _finishingTree = tree;
        tree.ProcessFrame += PollDetachedSearch;
        PollDetachedSearch();
        FlushAll();
    }

    private void PollDetachedSearch()
    {
        if (_finishingSearch is not { } session) return;
        if (!DrainSessionResults(session, _finishingFingerprint)) return;
        if (System.Environment.TickCount64 < _finishingFlushRetryAfter) return;
        // Keep the detached owner until the result tail and cursor are durable.
        if (!TryCompleteResultReception(session, _finishingFingerprint))
        { _finishingFlushRetryAfter = System.Environment.TickCount64 + 600; return; }
        if (_finishingTree is { } tree) tree.ProcessFrame -= PollDetachedSearch;
        _finishingTree = null; _finishingSearch = null;
        ReleaseSearch(session);
        RuntimeLog.Info("searchDetachedHandoffCompleted=true;state=" + session.GetProgress().State);
        _ = DisposeDetachedSearch(session);
    }

    // Shared terminal handoff logic can be checked without constructing a Godot node.
    internal bool DrainSessionResults(IProductionSearchSession session, string fingerprint)
    {
        DrainReceivedResults(session, fingerprint);
        bool complete = SessionResultsFullyDrained(session);
        if (complete) DrainReceivedResults(session, fingerprint);
        ObserveSessionCheckpoint(session, fingerprint);
        return complete;
    }

    private static async Task DisposeDetachedSearch(IProductionSearchSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.TryBackgroundWarning("searchDetachedDisposeFailed=" + ex); }
    }
}
