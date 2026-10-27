using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Diagnostics;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.World;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;
using RolltheSpire2.Ui.Shell;

int assertions = 0;
void Require(bool condition, string issue)
{
    assertions++;
    if (!condition) throw new InvalidDataException(issue);
}
SearchWorkspacePersistence Open(string root) => new(root, RuntimeProfileId.Beta111, initializeSearchCursor: false);
string State(string root, string file) => Path.Combine(root, "RolltheSpire2", "state", file);
void Preferences(string root, string json)
{
    Directory.CreateDirectory(Path.GetDirectoryName(State(root, "user_preferences.json"))!);
    File.WriteAllText(State(root, "user_preferences.json"), json);
}
SearchOptimizationSettings FromMask(int mask) => new()
{
    PrefixCache = (mask & 1) != 0,
    ExactDrawIntervals = (mask & 2) != 0,
    LeafyLazyInitialization = (mask & 4) != 0,
    CostOrderedFilters = (mask & 8) != 0,
    GroupedOutput = (mask & 16) != 0,
    AsyncHeaderReadback = (mask & 32) != 0,
    DoubleWindowBatching = (mask & 64) != 0
};
string fixture = Path.Combine(Path.GetTempPath(), "Rt2-OptimizationUiChecks-" + Guid.NewGuid().ToString("N"));
try
{
    foreach (var legacy in new[] { ("missing", "{}"), ("null", "{\"Optimizations\":null}"),
        ("empty", "{\"Optimizations\":{}}") })
    {
        string root = Path.Combine(fixture, legacy.Item1);
        Preferences(root, legacy.Item2);
        var old = Open(root);
        Require(old.SearchOptimizations == SearchOptimizationSettings.AllEnabled &&
            old.Preferences.Optimizations == SearchOptimizationSettings.AllEnabled,
            "Old or absent optimization preferences did not default to all enabled: " + legacy.Item1);
        Require(!File.Exists(State(root, "search_cursor.json")), "Reading old preferences initialized a new cursor.");
    }
    string partial = Path.Combine(fixture, "partial");
    Preferences(partial, "{\"Optimizations\":{\"PrefixCache\":false,\"GroupedOutput\":false}}");
    Require(Open(partial).SearchOptimizations == (SearchOptimizationSettings.AllEnabled with
        { PrefixCache = false, GroupedOutput = false }), "Partially saved preferences disabled new or absent settings.");

    string savedRoot = Path.Combine(fixture, "roundtrip");
    var saved = Open(savedRoot);
    Require(saved.TrySetPersistentOrigin("12345678ZZFZ", out _, out _), "The non-aligned checkpoint fixture was rejected.");
    saved.BeginSearchCheckpoint("unchanged-query", saved.CurrentNextCursorOrdinal);
    var draft = new WorkbenchSearchDraft(BaseGameModelKeys.Characters.Silent, 10, SearchQuery.Empty,
        AncientOptionConditionProfile.BroadDefault);
    var json = new JsonSerializerOptions { WriteIndented = true };
    json.Converters.Add(new SearchWorkspacePersistence.ModelKeyJsonConverter());
    string workspace = State(savedRoot, "search_workspace.json");
    File.WriteAllText(workspace, JsonSerializer.Serialize(new SearchWorkspaceDocument
    {
        QueryFingerprint = "unchanged-query", ResultDraft = draft,
        Results = [new PersistedSearchResult { Seed = "12345678ZZFZ", CharacterKey = BaseGameModelKeys.Characters.Silent.Serialized,
            Ascension = 10, QueryFingerprint = "unchanged-query" }]
    }, json));
    string preset = State(savedRoot, "search_presets/optimization-fixture.json");
    Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
    File.WriteAllText(preset, JsonSerializer.Serialize(new { SchemaVersion = 3, Id = "user:optimization-fixture",
        Title = "Optimization fixture", CharacterKey = BaseGameModelKeys.Characters.Silent.Serialized,
        Ascension = 10, WorkbenchSnapshot = draft, CreatedAtUtc = DateTimeOffset.UtcNow }, json));
    string[] protectedFiles = [State(savedRoot, "search_cursor.json"), workspace, preset];
    byte[][] protectedBytes = protectedFiles.Select(File.ReadAllBytes).ToArray();
    ulong next = saved.CurrentNextCursorOrdinal;
    for (int mask = 0; mask < 128; mask++)
    {
        var settings = FromMask(mask);
        saved.SetSearchOptimizations(settings);
        var reopened = Open(savedRoot);
        Require(saved.SearchOptimizations == settings && reopened.SearchOptimizations == settings,
            "One independent optimization choice did not survive preference reload: " + mask);
        Require(reopened.CanResumeSearch("unchanged-query") && reopened.ResolveSearchStartOrdinal("unchanged-query") == next,
            "An optimization change invalidated or rounded the exact query checkpoint.");
        Require(reopened.GetPersistedResults().Single().Seed == "12345678ZZFZ" &&
            reopened.Presets.GetAll().Any(p => p.Id == "user:optimization-fixture") && reopened.Presets.LoadIssues.Count == 0,
            "An optimization change lost a saved result or a valid preset.");
        for (int i = 0; i < protectedFiles.Length; i++)
            Require(File.ReadAllBytes(protectedFiles[i]).AsSpan().SequenceEqual(protectedBytes[i]),
                "An optimization change rewrote a checkpoint, workspace or preset.");
    }

    saved.SetSearchOptimizations(SearchOptimizationSettings.AllDisabled);
    byte[] preferencesBeforeBusy = File.ReadAllBytes(State(savedRoot, "user_preferences.json"));
    var session = new BusySession();
    saved.TrackSearch(session);
    saved.SetSearchOptimizations(SearchOptimizationSettings.AllEnabled);
    Require(saved.HasSearchInFlight && saved.SearchOptimizations == SearchOptimizationSettings.AllDisabled &&
        File.ReadAllBytes(State(savedRoot, "user_preferences.json")).AsSpan().SequenceEqual(preferencesBeforeBusy),
        "An active search allowed optimization preference mutation.");
    saved.ReleaseSearch(session);
    var finishing = typeof(SearchWorkspacePersistence).GetField("_finishingSearch", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidDataException("The finishing-search test seam changed.");
    finishing.SetValue(saved, session);
    saved.SetSearchOptimizations(SearchOptimizationSettings.AllEnabled);
    Require(saved.HasSearchInFlight && saved.SearchOptimizations == SearchOptimizationSettings.AllDisabled,
        "A finishing search allowed optimization preference mutation.");
    finishing.SetValue(saved, null);
    saved.SetSearchOptimizations(SearchOptimizationSettings.AllEnabled);
    Require(!saved.HasSearchInFlight && Open(savedRoot).SearchOptimizations == SearchOptimizationSettings.AllEnabled,
        "Restoring all enabled failed after a search finished.");
    Console.WriteLine("RT2_OPTIMIZATION_UI " + JsonSerializer.Serialize(new { result = "PASS", assertions,
        all128CombinationsPersisted = true, oldAndPartialPreferencesKeepDefaults = true,
        exactZzfzCheckpointPreserved = true, presetsAndWorkspaceBytesUnchanged = true,
        activeAndFinishingMutationGuard = true, gpuDeviceCreated = false,
        assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SearchWorkspacePersistence).Assembly.Location))) }));
}
finally
{
    string absolute = Path.GetFullPath(fixture);
    string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!absolute.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new InvalidDataException("Optimization test cleanup escaped its temporary directory.");
    if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
}

sealed class BusySession : IProductionSearchSession
{
    public Task Completion { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    public void Cancel() { }
    public bool TryReadCandidate(out SearchCandidate? candidate) { candidate = null; return false; }
    public SearchProgressSnapshot GetProgress() => throw new NotSupportedException();
    public SearchDiagnosticSummarySnapshot GetDiagnosticSummary() => throw new NotSupportedException();
    public ulong GetSafeNextOrdinal() => throw new NotSupportedException();
    public bool TryGetSafeNextOrdinal(out ulong ordinal) { ordinal = 0; return false; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
