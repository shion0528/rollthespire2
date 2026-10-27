using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Shell;

namespace RolltheSpire2.Ui.Persistence;

// A query owns its accumulated results. Runs and presets refer to this record;
// no execution plan, authority, or real-game state is retained here.
internal sealed class QueryHistoryEntry
{
    public string Id { get; set; } = "";
    public string QueryKey { get; set; } = "";
    public string EnvironmentFingerprint { get; set; } = "";
    public string RuntimeEnvironmentFingerprint { get; set; } = "";
    public int IdentityVersion { get; set; }
    public bool EnvironmentKnown { get; set; }
    public WorkbenchSearchDraft Draft { get; set; } = null!;
    public SeedLibraryContext? Context { get; set; }
    public DateTimeOffset LastUsedAtUtc { get; set; }
    public int RunCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<PersistedSearchResult> Results { get; set; } = [];

    internal SearchPresetDefinition AsPreset(string title) => new("history:" + Id, SearchPresetSource.Temporary,
        title, "", Draft.Character, Draft.Ascension, null, "", QueryWorkbenchFrame.CountPresetConditions(Draft.Query), [],
        SearchPresetProbabilitySnapshot.Unavailable(LastUsedAtUtc),
        SearchPresetProvenance.Unknown(Context?.GameVersion ?? Draft.GameVersion, LastUsedAtUtc), LastUsedAtUtc)
        { Workbench = Draft.ToPresetIntent() };
}

internal static class QueryHistoryIdentity
{
    // Only unordered predicates are sorted. Player order, pickup order, slots,
    // and other ordered observations are deliberately left intact.
    private static readonly HashSet<string> Sets = new(StringComparer.Ordinal)
    {
        "Any", "All", "Ban", "OptionAny", "SeaGlassTargetAny", "EventSequenceConstraints",
        "EventResultConditions", "VariantBossBranches", "AncientBranches", "RelicSequenceConstraints",
        "RelicShopSequenceConditions", "MerchantColorlessSequenceConditions", "StandardMaps",
        "BossFilters", "BossOrdinalFilters", "AncientIdentityFilters", "AncientOptionFilters",
        "AncientSeaGlassTargetFilters", "StructuredOpeningEffects", "unlocked_epochs", "encounters_seen"
    };

    internal static string Key(WorkbenchSearchDraft draft)
    {
        var clean = draft.ToPresetIntent();
        var query = clean.Query;
        if (query.Players.Count > 0)
        {
            try { query = PartyInitialQuery.BindSharedConditions(query); }
            // Saved presets may contain conflicting conditions. Keep their raw
            // identity; normalization failure here must never discard user intent.
            catch (ArgumentException) { }
        }
        return Hash(new { IntentVersion = 2, clean.Character, clean.Ascension, clean.Mode, Query = query, clean.AncientPremises,
            Players = clean.Players.Select(p => new { p.Slot, p.Character, UnlockPolicy = p.UnlockSource }).ToArray() });
    }

    internal static string EnvironmentKey(WorkbenchSearchDraft draft, string runtimeFingerprint) => Hash(new
    {
        EnvironmentVersion = 2, Runtime = runtimeFingerprint,
        Players = draft.Players.Select(p => new { p.Slot, p.Unlocks }).ToArray()
    });
    internal static string RecordId(string queryKey, string environmentFingerprint) => Hash(new { Query = queryKey, Environment = environmentFingerprint });

    private static string Hash(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, SearchPresetStore.CreateJsonOptions());
        string canonical = Canonical(node, "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Canonical(JsonNode? node, string name)
    {
        if (node is JsonObject obj)
            return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value, p.Key))) + "}";
        if (node is JsonArray array)
        {
            IEnumerable<string> items = array.Select(item => Canonical(item, ""));
            if (Sets.Contains(name)) items = items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            return "[" + string.Join(",", items) + "]";
        }
        return node?.ToJsonString() ?? "null";
    }
}

internal sealed partial class SearchWorkspacePersistence
{
    internal const int ResultsPerQuery = 30;
    // Current-process display may exceed the disk quota when the search target does.
    private List<PersistedSearchResult>? _displayResults;
    private double _historyRunElapsed;
    internal string QueryHistoryIssue { get; private set; } = "";
    internal IReadOnlyList<PersistedSearchResult> DisplayResults => _displayResults ?? _workspace.Results;
    internal string CurrentQueryKey => ActiveHistory?.QueryKey ?? "";
    internal string CurrentEnvironmentFingerprint => _environment.CaptureComplete ? _environment.Fingerprint : "";
    internal SeedQueryAssociation? ResultAssociation(string seed, QueryHistoryEntry? record = null)
    {
        record ??= ActiveHistory;
        if (record is null) return null;
        var result = record.Results.FirstOrDefault(r => r.Seed == seed) ??
            (record == ActiveHistory ? DisplayResults.FirstOrDefault(r => r.Seed == seed) : null);
        if (result is null || result.IsUnverified) return null;
        return new(record.QueryKey, record.IdentityVersion == 2 && record.EnvironmentKnown
            ? SeedQueryAssociationKind.VerifiedFromSearch : SeedQueryAssociationKind.LegacyUnspecified,
            record.EnvironmentFingerprint, record.Id);
    }
    private QueryHistoryEntry? ActiveHistory => _workspace.QueryHistory.FirstOrDefault(q => q.Id == _workspace.ActiveHistoryId);

    internal IReadOnlyList<QueryHistoryEntry> RecentQueries => _workspace.QueryHistory.Where(q => q.RunCount > 0)
        .OrderByDescending(q => q.LastUsedAtUtc).Take(_preferences.QueryHistoryLimit).ToArray();
    internal string QueryKeyForPreset(SearchPresetDefinition preset) => preset.Workbench is { } draft
        ? QueryHistoryIdentity.Key(draft) : _workspace.LegacyPresetQueryKeys.GetValueOrDefault(preset.Id, "");
    internal string NormalizeQueryKey(string key) => _workspace.LegacyQueryAliases.GetValueOrDefault(key, key);
    internal IReadOnlyList<QueryHistoryEntry> QueriesForPreset(SearchPresetDefinition preset) =>
        _workspace.QueryHistory.Where(q => q.QueryKey == QueryKeyForPreset(preset)).OrderByDescending(q => q.LastUsedAtUtc).ToArray();
    internal IReadOnlyList<SearchPresetDefinition> PresetsForQuery(string key) => _presets.GetAll()
        .Where(p => p.Source == SearchPresetSource.User && QueryKeyForPreset(p) == NormalizeQueryKey(key)).ToArray();

    internal void BindLegacyPresetQuery(SearchPresetDefinition preset, WorkbenchSearchDraft resolved)
    {
        if (preset.Workbench is not null) return;
        _workspace.LegacyPresetQueryKeys[preset.Id] = QueryHistoryIdentity.Key(resolved);
        PreservePresetQuery(preset with { Workbench = resolved });
    }

    internal void SetQueryHistoryLimit(int count)
    {
        _preferences.QueryHistoryLimit = Math.Clamp(count, 1, 300);
        TrimQueryHistory(); MarkPreferencesDirty(); FlushPreferences(); FlushWorkspace();
    }

    internal QueryHistoryEntry EnsureQuery(WorkbenchSearchDraft draft, SeedLibraryContext? context)
    {
        string key = QueryHistoryIdentity.Key(draft);
        string environment = _environment.CaptureComplete
            ? QueryHistoryIdentity.EnvironmentKey(draft, _environment.Fingerprint)
            : "unknown:" + _workspace.ResultBatchId;
        string id = QueryHistoryIdentity.RecordId(key, environment);
        var record = _workspace.QueryHistory.FirstOrDefault(q => q.Id == id);
        if (record is not null) return record;
        record = new QueryHistoryEntry { Id = id, QueryKey = key,
            IdentityVersion = 2, EnvironmentKnown = _environment.CaptureComplete, EnvironmentFingerprint = environment,
            RuntimeEnvironmentFingerprint = CurrentEnvironmentFingerprint,
            Draft = JsonSerializer.Deserialize<WorkbenchSearchDraft>(JsonSerializer.Serialize(draft.ToPresetIntent(), _json), _json)!,
            Context = context is null ? null : JsonSerializer.Deserialize<SeedLibraryContext>(JsonSerializer.Serialize(context, _json), _json) };
        _workspace.QueryHistory.Add(record); MarkWorkspaceDirty();
        return record;
    }

    internal void PreservePresetQuery(SearchPresetDefinition preset, SeedLibraryContext? context = null)
    {
        TrimQueryHistory(); FlushWorkspace();
    }

    private QueryHistoryEntry? FindCurrentQuery(WorkbenchSearchDraft draft)
    {
        if (!_environment.CaptureComplete)
            return ActiveHistory is { EnvironmentKnown: false } active && active.QueryKey == QueryHistoryIdentity.Key(draft) ? active : null;
        string id = QueryHistoryIdentity.RecordId(QueryHistoryIdentity.Key(draft),
            QueryHistoryIdentity.EnvironmentKey(draft, _environment.Fingerprint));
        return _workspace.QueryHistory.FirstOrDefault(q => q.IdentityVersion == 2 && q.Id == id);
    }

    internal int ResultCountForQuery(WorkbenchSearchDraft draft)
    {
        var record = FindCurrentQuery(draft);
        return record is null ? 0 : record.Id == _workspace.ActiveHistoryId ? DisplayResults.Count : record.Results.Count;
    }

    internal void ActivateQueryResults(WorkbenchSearchDraft draft)
    {
        if (HasSearchInFlight) return;
        var record = FindCurrentQuery(draft);
        string selection = record?.Id ?? "empty:" + QueryHistoryIdentity.RecordId(QueryHistoryIdentity.Key(draft),
            QueryHistoryIdentity.EnvironmentKey(draft, _environment.Fingerprint));
        if (_workspace.ResultBatchId == selection || record is not null && record.Id == _workspace.ActiveHistoryId) return;
        _workspace.ActiveHistoryId = record?.Id ?? "";
        _workspace.ResultBatchId = selection;
        _workspace.Results = record?.Results.ToList() ?? [];
        _displayResults = _workspace.Results.ToList();
        _workspace.ResultDraft = record?.Draft ?? draft.ToPresetIntent();
        _workspace.ResultContext = record?.Context;
        _workspace.ResultsSkipExactValidation = _workspace.Results.Any(r => r.IsUnverified);
        _workspace.ResultProgress = null;
        ResultsRevision++; MarkWorkspaceDirty(); FlushWorkspace();
    }

    internal void TrimQueryHistory()
    {
        var userPresets = _presets.GetAll().Where(p => p.Source == SearchPresetSource.User).ToArray();
        var pinned = userPresets.Select(QueryKeyForPreset).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (string id in _workspace.LegacyPresetQueryKeys.Keys.Where(id => !userPresets.Any(p => p.Id == id)).ToArray())
            _workspace.LegacyPresetQueryKeys.Remove(id);
        var recent = RecentQueries.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        _workspace.QueryHistory.RemoveAll(q => !recent.Contains(q.Id) && !pinned.Contains(q.QueryKey) && q.Id != _workspace.ActiveHistoryId);
        foreach (var query in _workspace.QueryHistory)
            if (query.Results.Count > ResultsPerQuery) query.Results.RemoveRange(0, query.Results.Count - ResultsPerQuery);
        MarkWorkspaceDirty();
    }

    private void BeginQueryHistory(WorkbenchSearchDraft? draft, SeedLibraryContext? context)
    {
        _historyRunElapsed = 0;
        _displayResults = [];
        _workspace.ActiveHistoryId = "";
        if (draft is null) return;
        var record = EnsureQuery(draft, context);
        record.RunCount++; record.LastUsedAtUtc = DateTimeOffset.UtcNow;
        _workspace.ActiveHistoryId = record.Id;
        _workspace.Results = record.Results.ToList();
        _displayResults = record.Results.ToList();
        TrimQueryHistory();
    }

    private void RecordHistoryResult(PersistedSearchResult result)
        => RecordHistoryResults([result]);

    private void RecordHistoryResults(IReadOnlyList<PersistedSearchResult> results)
    {
        if (results.Count == 0) return;
        QueryHistoryEntry? record = ActiveHistory;
        _displayResults ??= _workspace.Results.ToList();
        foreach (var result in results)
        {
            List<PersistedSearchResult> saved = record?.Results ?? _workspace.Results;
            Accumulate(saved, result);
            if (saved.Count > ResultsPerQuery) saved.RemoveAt(0);
            Accumulate(_displayResults, result);
        }
        if (record is not null) _workspace.Results = record.Results.ToList();
        ResultsRevision++; MarkWorkspaceDirty();
    }

    private static void Accumulate(List<PersistedSearchResult> rows, PersistedSearchResult result)
    {
        var previous = rows.FirstOrDefault(r => r.Seed == result.Seed);
        if (previous is not null)
        {
            rows.Remove(previous);
            // A later unchecked candidate cannot downgrade an already verified seed.
            if (!previous.IsUnverified && result.IsUnverified) result = previous;
        }
        rows.Add(result);
    }

    private void NormalizeQueryHistory()
    {
        _preferences.QueryHistoryLimit = Math.Clamp(_preferences.QueryHistoryLimit, 1, 300);
        _workspace.QueryHistory ??= [];
        _workspace.LegacyPresetQueryKeys ??= [];
        _workspace.LegacyQueryAliases ??= [];
        foreach (var record in _workspace.QueryHistory)
        {
            if (record?.Draft is null || record.Draft.Version is not (1 or 2 or 3 or 4 or 5) || string.IsNullOrEmpty(record.Id) || record.IdentityVersion > 2)
                throw new InvalidDataException("QueryHistory.UnsupportedRecord");
            SearchPresetCompatibilityResolver.ValidateWorkbenchShape(record.Draft);
            record.Results ??= [];
            if (record.IdentityVersion < 2)
            {
                // Historical environment evidence is retained, never assigned today's authority.
                string intentKey = QueryHistoryIdentity.Key(record.Draft);
                _workspace.LegacyQueryAliases[record.QueryKey] = intentKey;
                record.QueryKey = intentKey;
                record.IdentityVersion = 1;
            }
        }
        if (_workspace.QueryHistory.Count == 0 && _workspace.ResultDraft is { Version: < 5 } draft &&
            !_workspace.ResultBatchId.StartsWith("empty:", StringComparison.Ordinal) &&
            (_workspace.ResultsStartedAtUtc is not null || _workspace.Results.Count > 0))
        {
            var migrated = EnsureQuery(draft, _workspace.ResultContext);
            migrated.IdentityVersion = 1;
            migrated.EnvironmentKnown = false;
            migrated.EnvironmentFingerprint = "";
            migrated.Id = "legacy:" + migrated.Id;
            migrated.RunCount = 1;
            migrated.LastUsedAtUtc = _workspace.ResultsStartedAtUtc ?? DateTimeOffset.UtcNow;
            migrated.ElapsedSeconds = _workspace.ResultProgress?.ElapsedSeconds ?? 0;
            foreach (var result in _workspace.Results) Accumulate(migrated.Results, result);
            _workspace.ActiveHistoryId = migrated.Id;
        }
        foreach (string id in _workspace.LegacyPresetQueryKeys.Keys.ToArray())
            _workspace.LegacyPresetQueryKeys[id] = NormalizeQueryKey(_workspace.LegacyPresetQueryKeys[id]);
        TrimQueryHistory();
        if (_workspace.Results.Count > ResultsPerQuery)
            _workspace.Results = _workspace.Results.TakeLast(ResultsPerQuery).ToList();
    }

    private void LoadQueryHistory()
    {
        try { NormalizeQueryHistory(); }
        catch (Exception ex)
        {
            // Protect the original file just like unreadable workbench/preset
            // documents. Current-process edits must not overwrite unknown intent.
            QueryHistoryIssue = ex.Message;
            _unreadableDocuments.Add("search_workspace.json");
            _workspace.QueryHistory = [];
            _workspace.ActiveHistoryId = "";
            Bootstrap.RuntimeLog.Warn("queryHistoryReadProtected=" + ex.Message);
        }
    }
}
