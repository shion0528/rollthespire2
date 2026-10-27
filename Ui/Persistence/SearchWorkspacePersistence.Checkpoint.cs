using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;

namespace RolltheSpire2.Ui.Persistence;

internal sealed partial class SearchWorkspacePersistence
{
    // NextCursor remains the only resume position. These fields bind that exact
    // value to canonical query semantics, never to run options or preset names.
    internal bool CanResumeSearch(string queryFingerprint) =>
        _cursor.Initialized && (_cursor.CheckpointVersion == 0
            ? _cursor.NextCursor != _cursor.PersistentOrigin
            : MatchesSearchCheckpoint(queryFingerprint));

    internal ulong ResolveSearchStartOrdinal(string queryFingerprint)
    {
        EnsureCursorInitialized();
        // Older releases saved an exact cursor without a binding. Preserve it
        // for the first upgraded run even when its old query cannot be proven.
        return _cursor.CheckpointVersion == 0 || MatchesSearchCheckpoint(queryFingerprint)
            ? _cursor.NextCursor : _cursor.PersistentOrigin;
    }

    internal void BeginSearchCheckpoint(string queryFingerprint, ulong startOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        if (startOrdinal >= Beta110SeedCodec.SpaceSize) throw new ArgumentOutOfRangeException(nameof(startOrdinal));
        if (HasSearchInFlight) throw new InvalidOperationException("SearchCheckpointAlreadyActive");
        bool legacy = _cursor.CheckpointVersion == 0;
        _cursor.Initialized = true;
        _cursor.NextCursor = startOrdinal;
        _cursor.CheckpointVersion = 1;
        _cursor.CheckpointQueryFingerprint = queryFingerprint;
        _cursor.CheckpointEnvironmentFingerprint = CurrentCheckpointEnvironment;
        _cursor.EndOfSpaceWrapCommitted = false;
        MarkCursorDirty();
        FlushCursor();
        RuntimeLog.Info($"searchCheckpointBound=true;legacyCursorPreserved={legacy};nextOrdinal={startOrdinal};prefixBlock={startOrdinal / VisibleSeedBlockPolicy.BlockSize};suffixOrdinal={startOrdinal % VisibleSeedBlockPolicy.BlockSize};nextSeed={Beta110SeedCodec.FormatOrdinal(startOrdinal)}");
    }

    internal bool TryFlushSearchCheckpoint()
    {
        FlushWorkspace();
        FlushCursor();
        return !_workspaceDirty && !_cursorDirty;
    }

    internal void ObserveSessionCheckpoint(IProductionSearchSession session, string queryFingerprint)
    {
        // A detached result drain must never write over a newer query's cursor.
        // Legacy/test callers without a binding retain their existing behavior.
        if (_cursor.CheckpointVersion != 0 && _cursor.CheckpointQueryFingerprint.Length > 0 &&
            !string.Equals(_cursor.CheckpointQueryFingerprint, queryFingerprint, StringComparison.Ordinal)) return;
        if (!session.TryGetSafeNextOrdinal(out ulong next)) return;
        // Snapshot the producer's safe boundary FIRST. A later commit must not
        // drag this observation past candidates that are still in transit.
        if (ReferenceEquals(_receivingSearch, session) && _resultReceiver is { } receiver)
        {
            long before = receiver.TransferVersion;
            if ((before & 1) != 0 || receiver.TransferInFlight) return;
            long published = session.GetProgress().MatchCount;
            int sourcePending = session.PendingCandidateCount;
            bool sourceClosed = session.Completion.IsCompleted && receiver.ReceptionCompletion.IsCompleted;
            if ((sourcePending != 0 && !(sourceClosed && sourcePending < 0)) ||
                _storedReceivedResults < published || receiver.BufferedCount != 0 ||
                receiver.ReceivedCount != _storedReceivedResults || receiver.TransferInFlight ||
                receiver.TransferVersion != before) return;
        }
        if (next < Beta110SeedCodec.SpaceSize) ObserveSafeNextCursor(next);
        else if (session.Completion.IsCompleted && next == Beta110SeedCodec.SpaceSize) CommitEndOfSpaceWrap();
    }

    private string CurrentCheckpointEnvironment => _environment.CaptureComplete ? _environment.Fingerprint : "";

    private bool MatchesSearchCheckpoint(string queryFingerprint) =>
        _cursor.CheckpointVersion == 1 && !string.IsNullOrWhiteSpace(queryFingerprint) &&
        string.Equals(_cursor.CheckpointQueryFingerprint, queryFingerprint, StringComparison.Ordinal) &&
        string.Equals(_cursor.CheckpointEnvironmentFingerprint, CurrentCheckpointEnvironment, StringComparison.Ordinal);

    private void ClearSearchCheckpointBinding()
    {
        _cursor.CheckpointVersion = 1;
        _cursor.CheckpointQueryFingerprint = "";
        _cursor.CheckpointEnvironmentFingerprint = "";
        _cursor.EndOfSpaceWrapCommitted = false;
    }
}
