using RolltheSpire2.Infrastructure.Snapshots;
using RolltheSpire2.Core.Authority;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.World;
using RolltheSpire2.Core.World.Snapshots;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Ui.Pages.Search;
using RolltheSpire2.Ui.Pages.Search.CombatReward;
using RolltheSpire2.Ui.Settings;

namespace RolltheSpire2.Ui.Persistence;

/// <summary>
/// Release persistence boundary. This owns player preferences, lightweight Search
/// draft/result DTOs, Predictor UI context, cursor state, and environment identity.
/// Predictor result/authority, Fast/GPU/Exact/Witness objects are never serialized here.
/// </summary>
internal sealed partial class SearchWorkspacePersistence
{
    internal const int SchemaVersion = 1;
    private const double PreferenceDebounceSeconds = 0.60;
    private const double WorkspaceDebounceSeconds = 0.60;
    private const double CursorCheckpointSeconds = 1.50;

    private readonly string _directory;
    private readonly JsonSerializerOptions _json;
    private readonly SearchPresetCatalog _presets;
    private UserPreferencesDocument _preferences;
    private SearchWorkspaceDocument _workspace;
    private PredictorContextDocument _predictorContext;
    private SearchCursorDocument _cursor;
    private SearchEnvironmentSignature _environment;
    private bool _preferencesDirty;
    private bool _workspaceDirty;
    private bool _predictorContextDirty;
    private bool _cursorDirty;
    private double _preferencesDirtyAge;
    private double _workspaceDirtyAge;
    private double _predictorContextDirtyAge;
    private double _cursorDirtyAge;
    private bool _readWarningIssued;
    private bool _writeWarningIssued;
    private readonly HashSet<string> _unreadableDocuments = new(StringComparer.OrdinalIgnoreCase);

    public SearchWorkspacePersistence(string userDataDirectory, RuntimeProfileId profileId, bool initializeSearchCursor = true)
    {
        _ = profileId; // Reserved for future schema migrations; environment identity is captured separately.
        _directory = Path.Combine(userDataDirectory, "RolltheSpire2", "state");
        _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        _json.Converters.Add(new ModelKeyJsonConverter());
        _presets = new SearchPresetCatalog(_directory);
        _preferences = Load<UserPreferencesDocument>("user_preferences.json") ?? new UserPreferencesDocument();
        _workspace = Load<SearchWorkspaceDocument>("search_workspace.json") ?? new SearchWorkspaceDocument();
        _predictorContext = Load<PredictorContextDocument>("predictor_context.json") ?? new PredictorContextDocument();
        _cursor = Load<SearchCursorDocument>("search_cursor.json") ?? new SearchCursorDocument();
        _environment = Load<SearchEnvironmentSignature>("search_environment.json") ?? new SearchEnvironmentSignature();
        NormalizeDocuments();
        LoadQueryHistory();
        if (initializeSearchCursor)
            EnsureCursorInitialized();
    }

    public UserPreferencesDocument Preferences => _preferences;

    internal static int ResolveSearchWorkers(int? savedBudget, int logicalProcessors) =>
        Math.Clamp(savedBudget ?? Math.Max(1, logicalProcessors / 2), 1, 64);

    internal static SearchResourceBudget ResolveSearchResources(int? cpuLoadPercent, int gpuLoadPercent,
        int? savedWorkerBudget, int logicalProcessors)
    {
        int cpu = cpuLoadPercent ?? (savedWorkerBudget is { } legacy
            ? (int)Math.Clamp((long)Math.Clamp(legacy, 1, 64) * 100 / Math.Max(1, logicalProcessors), 1L, 100L)
            : 50);
        return new SearchResourceBudget(cpu, gpuLoadPercent).Normalize();
    }

    internal SearchResourceBudget SearchResources => ResolveSearchResources(_preferences.CpuLoadPercent,
        _preferences.GpuLoadPercent, _preferences.SearchWorkerBudget, System.Environment.ProcessorCount);

    internal SearchOptimizationSettings SearchOptimizations =>
        _preferences.Optimizations ?? SearchOptimizationSettings.AllEnabled;

    internal void SetSearchOptimizations(SearchOptimizationSettings optimizations)
    {
        ArgumentNullException.ThrowIfNull(optimizations);
        if (HasSearchInFlight) return;
        _preferences.Optimizations = optimizations;
        MarkPreferencesDirty();
        FlushPreferences();
    }

    internal void SetSearchResources(SearchResourceBudget resources)
    {
        if (HasSearchInFlight) return;
        var normalized = resources.Normalize();
        _preferences.CpuLoadPercent = normalized.CpuPercent;
        _preferences.GpuLoadPercent = normalized.GpuPercent;
        MarkPreferencesDirty();
        FlushPreferences();
    }

    public void SetSkipExactValidation(bool enabled)
    {
        _preferences.SkipExactValidation = enabled;
        MarkPreferencesDirty(); FlushPreferences();
    }

    public bool NeedsNotesAcknowledgement(RolltheSpire2.Presentation.DeveloperNotes.DeveloperNotesChapter chapter) =>
        chapter.RequiresAcknowledgement &&
        _preferences.ReadNotesVersions.GetValueOrDefault(chapter.Id) != chapter.AcknowledgementVersion;

    public void AcknowledgeNotesChapter(RolltheSpire2.Presentation.DeveloperNotes.DeveloperNotesChapter chapter)
    {
        if (!chapter.RequiresAcknowledgement || string.IsNullOrWhiteSpace(chapter.AcknowledgementVersion)) return;
        _preferences.ReadNotesVersions[chapter.Id] = chapter.AcknowledgementVersion;
        MarkPreferencesDirty(); FlushPreferences();
    }

    public void SetShowInRunPredictionEntry(bool enabled)
    {
        if (_preferences.ShowInRunPredictionEntry == enabled) return;
        _preferences.ShowInRunPredictionEntry = enabled;
        MarkPreferencesDirty();
        FlushPreferences();
    }

    internal Shell.WorkbenchSearchDraft? LoadWorkbench()
    {
        const string file = "query_workbench.json";
        bool existed = File.Exists(Path.Combine(_directory, file));
        var strict = new JsonSerializerOptions(_json)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true
        };
        var draft = Load<Shell.WorkbenchSearchDraft>(file, strict);
        if (existed && draft is null) throw new InvalidDataException("integration.load_shape");
        if (draft is not null && draft.Version is not (1 or 2 or 3 or 4))
        {
            _unreadableDocuments.Add(file);
            throw new InvalidDataException("integration.load_version");
        }
        return draft;
    }

    internal void SaveWorkbench(Shell.WorkbenchSearchDraft draft)
    {
        bool dirty = true;
        double age = 0;
        FlushDocument(ref dirty, ref age, "query_workbench.json", draft.WithoutCapturedAuthority());
        if (dirty) throw new IOException("WorkbenchPersistenceWriteFailed");
    }

    internal void RecoverWorkbench(Shell.WorkbenchSearchDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Version is not (1 or 2 or 3 or 4)) throw new InvalidDataException("integration.load_version");
        const string file = "query_workbench.json";
        string path = Path.Combine(_directory, file);
        // Explicit recovery is the only operation allowed to replace protected
        // intent. Keep protection until both preservation and commit succeed.
        _unreadableDocuments.Add(file);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(draft.WithoutCapturedAuthority(), _json);
        if (File.Exists(path))
        {
            string backup = path + ".recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
            SearchPersistenceFile.WriteAtomic(backup, File.ReadAllBytes(path));
        }
        // Previously quarantined .corrupt files are deliberately left intact.
        SearchPersistenceFile.WriteAtomic(path, payload);
        _unreadableDocuments.Remove(file);
    }

    internal void SetWorkbenchSearchMode(string mode)
    {
        _preferences.SearchMode = mode == "CPU" ? "CPU" : "Auto";
        MarkPreferencesDirty(); FlushPreferences();
    }

    internal void SaveWorkbenchPreferences(IReadOnlyDictionary<string, bool> flags, string page)
    {
        _preferences.WorkbenchFlags = new(flags);
        _preferences.WorkbenchPage = page;
        MarkPreferencesDirty();
        FlushPreferences();
    }

    internal void InitializeWorkbenchCursor() => EnsureCursorInitialized();

    public void SetLanguageOverride(string language)
    {
        _preferences.LanguageOverride = language is "en" or "zh" ? language : string.Empty;
        MarkPreferencesDirty();
        FlushPreferences();
    }

    internal void SetAppearance(UiColorPreferences appearance, bool flush = true)
    {
        _preferences.Appearance = appearance.Normalize();
        MarkPreferencesDirty();
        if (flush) FlushPreferences();
    }

    public void SetActInformationIdentityGuideExpanded(bool expanded)
    {
        if (_preferences.ActInformationIdentityGuideExpanded == expanded) return;
        _preferences.ActInformationIdentityGuideExpanded = expanded;
        MarkPreferencesDirty();
        FlushPreferences();
    }

    public void SetActInformationMapGuideExpanded(bool expanded)
    {
        if (_preferences.ActInformationMapGuideExpanded == expanded) return;
        _preferences.ActInformationMapGuideExpanded = expanded;
        _preferences.ActInformationGuideExpanded = expanded;
        MarkPreferencesDirty();
        FlushPreferences();
    }
    public SearchPresetCatalog Presets => _presets;
    public SearchWorkspaceDocument Workspace => _workspace;
    public PredictorContextDocument PredictorContext => _predictorContext;
    public SearchCursorDocument Cursor => _cursor;
    public SearchEnvironmentSignature Environment => _environment;
    public bool EnvironmentResetOccurred { get; private set; }
    public string EnvironmentResetReason { get; private set; } = string.Empty;

    public void ApplyPreferencesToRuntimeSettings(RuntimePredictionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Resources = SearchResources;
        settings.RelicComputeBackendPreference = settings.Resources.GpuPercent == 0
            ? Beta110RelicComputeBackendPreference.Cpu : _preferences.SearchMode switch
        {
            "CPU" => Beta110RelicComputeBackendPreference.Cpu,
            _ => Beta110RelicComputeBackendPreference.Auto
        };
        settings.NormalizeWorkshopSearchModePreference();
        settings.SearchWorkerCount = settings.Resources.ResolveCpuWorkers(System.Environment.ProcessorCount);
    }

    public void CapturePreferences(string languageCode, Vector2 logicalSize, RuntimePredictionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string normalizedLanguage = string.Equals(languageCode, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
        Beta110RelicComputeBackendPreference mode = settings.NormalizeWorkshopSearchModePreference();
        string storedMode = settings.Resources.GpuPercent == 0 ? _preferences.SearchMode
            : mode == Beta110RelicComputeBackendPreference.Cpu ? "CPU" : "Auto";
        var resources = settings.Resources.Normalize();
        float width = Math.Max(0f, logicalSize.X);
        float height = Math.Max(0f, logicalSize.Y);
        if (string.Equals(_preferences.Language, normalizedLanguage, StringComparison.Ordinal) &&
            Math.Abs(_preferences.PanelWidth - width) < 0.5f &&
            Math.Abs(_preferences.PanelHeight - height) < 0.5f &&
            string.Equals(_preferences.SearchMode, storedMode, StringComparison.Ordinal) &&
            _preferences.CpuLoadPercent == resources.CpuPercent &&
            _preferences.GpuLoadPercent == resources.GpuPercent)
        {
            return;
        }
        _preferences.Language = normalizedLanguage;
        _preferences.PanelWidth = width;
        _preferences.PanelHeight = height;
        _preferences.SearchMode = storedMode;
        _preferences.CpuLoadPercent = resources.CpuPercent;
        _preferences.GpuLoadPercent = resources.GpuPercent;
        MarkPreferencesDirty();
    }

    public void SetLastPage(string pageKey)
    {
        string normalized = pageKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(_preferences.LastPage, normalized, StringComparison.Ordinal))
        {
            return;
        }
        _preferences.LastPage = normalized;
        MarkPreferencesDirty();
    }

    public void SetShowOfficialPresets(bool show)
    {
        if (_preferences.ShowOfficialPresets == show)
            return;
        _preferences.ShowOfficialPresets = show;
        MarkPreferencesDirty();
    }

    public void SaveWorkspaceDraft(
        ModelKey characterKey,
        int ascension,
        SearchDraft draft,
        SearchRunDraft runDraft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(runDraft);
        _workspace.CharacterKey = characterKey.Serialized;
        _workspace.Ascension = ascension;
        _workspace.Draft = draft;
        _workspace.RunDraft = runDraft;
        MarkWorkspaceDirty();
    }

    public void SavePredictorContext(
        string seed,
        ModelKey characterKey,
        int ascension,
        int playersCount,
        int playerSlotIndex,
        bool? allCharacterCardPoolsUnlocked,
        AncientOptionConditionProfile conditions,
        string preferredOpeningRouteId,
        int? preferredOpeningChoiceSlotIndex,
        string preferredRewardRouteGroupId)
    {
        _predictorContext.Seed = seed?.Trim() ?? string.Empty;
        _predictorContext.CharacterKey = characterKey.Serialized;
        _predictorContext.Ascension = Math.Clamp(ascension, SeedPredictionInputLimits.MinimumAscension, SeedPredictionInputLimits.MaximumAscension);
        _predictorContext.PlayersCount = Math.Clamp(playersCount, SeedPredictionInputLimits.MinimumPlayers, SeedPredictionInputLimits.MaximumPlayers);
        _predictorContext.PlayerSlotIndex = Math.Clamp(playerSlotIndex, 0, Math.Max(0, _predictorContext.PlayersCount - 1));
        // Last-known Predictor display context only. A subsequent analysis still
        // captures fresh immutable Runtime Authority before prediction.
        _predictorContext.AllCharacterCardPoolsUnlocked = allCharacterCardPoolsUnlocked;
        _predictorContext.TezcataraHasBasicStrike = conditions.TezcataraHasBasicStrike;
        _predictorContext.NonupeipeSwiftEnchantableAtLeast4 = conditions.NonupeipeSwiftEnchantableAtLeast4;
        _predictorContext.TanxInstinctEnchantableAtLeast3 = conditions.TanxInstinctEnchantableAtLeast3;
        _predictorContext.PaelGoopyDefendCardsAtLeast3 = conditions.PaelGoopyDefendCardsAtLeast3;
        _predictorContext.PaelAllowLegionNoEventPet = conditions.PaelAllowLegionNoEventPet;
        _predictorContext.PaelRemovableCardsAtLeast5 = conditions.PaelRemovableCardsAtLeast5;
        _predictorContext.OrobasArchaicToothConditionMet = conditions.OrobasArchaicToothConditionMet;
        _predictorContext.OrobasTouchOfOrobasConditionMet = conditions.OrobasTouchOfOrobasConditionMet;
        _predictorContext.DarvAllowPandorasBoxRelicSet = conditions.DarvAllowPandorasBoxRelicSet;
        _predictorContext.PreferredOpeningRouteId = preferredOpeningRouteId?.Trim() ?? string.Empty;
        _predictorContext.PreferredOpeningChoiceSlotIndex = preferredOpeningChoiceSlotIndex ?? -1;
        _predictorContext.PreferredRewardRouteGroupId = preferredRewardRouteGroupId?.Trim() ?? string.Empty;
        MarkPredictorContextDirty();
    }

    internal long ResultsRevision { get; private set; }

    public void ResetWorkspaceForSuccessfulStart(string queryFingerprint,
        Shell.WorkbenchSearchDraft? draft = null, SeedLibraryContext? context = null, bool skipExactValidation = false)
    {
        // Freeze user intent separately from the editor's next query. Runtime
        // authority, prediction documents and execution plans never cross this boundary.
        var savedDraft = draft is null ? null : JsonSerializer.Deserialize<Shell.WorkbenchSearchDraft>(
            JsonSerializer.Serialize(draft.WithoutCapturedAuthority(), _json), _json);
        var savedContext = context is null ? null : JsonSerializer.Deserialize<SeedLibraryContext>(
            JsonSerializer.Serialize(context, _json), _json);
        _workspace.QueryFingerprint = queryFingerprint ?? string.Empty;
        _workspace.Results.Clear();
        _workspace.ResultBatchId = Guid.NewGuid().ToString("N");
        _workspace.ResultsStartedAtUtc = DateTimeOffset.UtcNow;
        _workspace.ResultDraft = savedDraft;
        _workspace.ResultContext = savedContext;
        _workspace.ResultsSkipExactValidation = skipExactValidation;
        _workspace.ResultProgress = null;
        BeginQueryHistory(savedDraft, savedContext);
        ResultsRevision++;
        MarkWorkspaceDirty();
    }

    internal void SaveResultProgress(SearchProgressSnapshot progress)
    {
        if (ActiveHistory is { } history)
            history.ElapsedSeconds += Math.Max(0, progress.ElapsedSeconds - _historyRunElapsed);
        _historyRunElapsed = progress.ElapsedSeconds;
        _workspace.ResultProgress = progress;
        MarkWorkspaceDirty();
    }

    internal void SavePredictorParty(SeedLibraryContext party, string seed, bool active)
    {
        _predictorContext.Party = party;
        _predictorContext.PartySeed = seed;
        _predictorContext.PartyActive = active;
        MarkPredictorContextDirty();
    }

    public void AppendResult(SearchCandidate candidate, string queryFingerprint)
        => RecordHistoryResult(CreatePersistedResult(candidate, queryFingerprint));

    internal void AppendResults(IReadOnlyList<SearchCandidate> candidates, string queryFingerprint)
    {
        if (candidates.Count == 0) return;
        var results = new PersistedSearchResult[candidates.Count];
        for (int i = 0; i < candidates.Count; i++) results[i] = CreatePersistedResult(candidates[i], queryFingerprint);
        RecordHistoryResults(results);
    }

    private PersistedSearchResult CreatePersistedResult(SearchCandidate candidate, string queryFingerprint)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        SearchMatchWitness? routeWitness = candidate.PrimaryWitness is { OpeningRouteId.Length: > 0 } primaryRouteWitness
            ? primaryRouteWitness
            : candidate.Witnesses.FirstOrDefault(witness => !string.IsNullOrWhiteSpace(witness.OpeningRouteId));
        var result = new PersistedSearchResult
        {
            Seed = candidate.Seed,
            CharacterKey = candidate.CharacterKey.Serialized,
            Ascension = candidate.Ascension,
            QueryFingerprint = queryFingerprint ?? string.Empty,
            Party = candidate.Document?.Party,
            IsUnverified = candidate.IsUnverified,
            WitnessOpeningRouteId = routeWitness?.OpeningRouteId ?? string.Empty
        };
        if (_workspace.ResultContext is { } context)
        {
            try
            {
                result.Context = candidate.IsUnverified ? context : SeedLibraryContextCapture.WithWitness(context, candidate);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("savedSearchResultContextUnavailable=" + ex.Message);
            }
        }
        return result;
    }

    public IReadOnlyList<PersistedSearchResult> GetPersistedResults() => _workspace.Results.ToArray();

    public ulong CurrentNextCursorOrdinal => _cursor.NextCursor;
    public ulong CurrentPersistentOriginOrdinal => _cursor.PersistentOrigin;
    public string CurrentNextCursorSeed => Beta110SeedCodec.FormatOrdinal(Math.Min(_cursor.NextCursor, Beta110SeedCodec.SpaceSize - 1UL));
    public string CurrentPersistentOriginSeed => Beta110SeedCodec.FormatOrdinal(Math.Min(_cursor.PersistentOrigin, Beta110SeedCodec.SpaceSize - 1UL));

    public bool TrySetPersistentOrigin(string rawSeed, out string canonicalSeed, out string issue)
    {
        canonicalSeed = string.Empty;
        if (HasSearchInFlight) { issue = "PersistentOriginSearchInFlight"; return false; }
        if (!Beta110SeedCodec.TryParseOrdinal(rawSeed?.Trim() ?? string.Empty, out ulong ordinal, out canonicalSeed, out issue))
            return false;
        if (ordinal >= Beta110SeedCodec.SpaceSize)
        {
            issue = "PersistentOriginOutsideVisibleSeedSpace";
            return false;
        }
        SetPersistentOriginOrdinal(ordinal, "Manual");
        return true;
    }

    public string RandomizePersistentOrigin()
    {
        if (HasSearchInFlight) throw new InvalidOperationException("PersistentOriginSearchInFlight");
        ulong ordinal = RandomVisibleOrdinal();
        SetPersistentOriginOrdinal(ordinal, "SystemRandom");
        return Beta110SeedCodec.FormatOrdinal(ordinal);
    }

    public void ObserveSafeNextCursor(ulong nextOrdinal)
    {
        if (nextOrdinal >= Beta110SeedCodec.SpaceSize)
            return;
        if (_cursor.NextCursor == nextOrdinal)
            return;
        _cursor.NextCursor = nextOrdinal;
        if (nextOrdinal != 0) _cursor.EndOfSpaceWrapCommitted = false;
        MarkCursorDirty();
    }

    public void CommitEndOfSpaceWrap()
    {
        if (_cursor.EndOfSpaceWrapCommitted && _cursor.NextCursor == 0) return;
        _cursor.NextCursor = 0UL;
        _cursor.WrapCount++;
        _cursor.EndOfSpaceWrapCommitted = true;
        MarkCursorDirty();
        RuntimeLog.Info($"persistentSearchCursorWrap=true;nextCursor=0;wrapCount={_cursor.WrapCount}");
    }

    public void EnsureEnvironment(SearchEnvironmentSignature current)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnvironmentResetOccurred = false;
        EnvironmentResetReason = string.Empty;
        if (!current.CaptureComplete || string.IsNullOrWhiteSpace(current.Fingerprint))
        {
            RuntimeLog.Warn($"searchWorkspaceEnvironmentCheckDeferred=true;reason=CurrentSignatureUnavailable;issue={current.CaptureIssue};workspacePreserved=true");
            // An old successful capture is not evidence about this process.
            _environment = current;
            return;
        }
        if (string.IsNullOrWhiteSpace(_environment.Fingerprint))
        {
            _environment = current;
            WriteNow("search_environment.json", _environment);
            return;
        }

        string[] reasons = CompareEnvironment(_environment, current).ToArray();
        if (reasons.Length == 0)
        {
            _environment = current;
            return;
        }

        EnvironmentResetOccurred = true;
        EnvironmentResetReason = string.Join(",", reasons);
        RuntimeLog.Warn(
            "searchWorkspaceEnvironmentReset=true;" +
            $"reasons={EnvironmentResetReason};" +
            $"oldGameVersion={_environment.GameVersionIdentity};newGameVersion={current.GameVersionIdentity};" +
            $"oldCatalog={_environment.CatalogFingerprint};newCatalog={current.CatalogFingerprint};" +
            $"oldUnlock={_environment.UnlockFingerprint};newUnlock={current.UnlockFingerprint}");
        // Historical results belong to the user. Environment changes invalidate
        // reusable query state, not the saved seeds and their original context.
        _workspace = new SearchWorkspaceDocument
        {
            QueryFingerprint = _workspace.QueryFingerprint, Results = _workspace.Results,
            ResultBatchId = _workspace.ResultBatchId, ResultsStartedAtUtc = _workspace.ResultsStartedAtUtc,
            ResultDraft = _workspace.ResultDraft, ResultContext = _workspace.ResultContext,
            ResultsSkipExactValidation = _workspace.ResultsSkipExactValidation, ResultProgress = _workspace.ResultProgress,
            QueryHistory = _workspace.QueryHistory, ActiveHistoryId = _workspace.ActiveHistoryId,
            LegacyPresetQueryKeys = _workspace.LegacyPresetQueryKeys,
            LegacyQueryAliases = _workspace.LegacyQueryAliases
        };
        ResultsRevision++;
        _environment = current;
        MarkWorkspaceDirty();
        FlushWorkspace();
        WriteNow("search_environment.json", _environment);
    }

    public void Tick(double deltaSeconds)
    {
        double delta = Math.Max(0d, deltaSeconds);
        if (_preferencesDirty && (_preferencesDirtyAge += delta) >= PreferenceDebounceSeconds)
            FlushPreferences();
        if (_workspaceDirty && (_workspaceDirtyAge += delta) >= WorkspaceDebounceSeconds)
            FlushWorkspace();
        if (_predictorContextDirty && (_predictorContextDirtyAge += delta) >= WorkspaceDebounceSeconds)
            FlushPredictorContext();
        if (_cursorDirty && (_cursorDirtyAge += delta) >= CursorCheckpointSeconds)
            FlushCursor();
    }

    public void FlushAll()
        => TryFlushAll();

    internal bool TryFlushAll()
    {
        FlushPreferences();
        FlushWorkspace();
        FlushPredictorContext();
        FlushCursor();
        bool environmentSaved = WriteNow("search_environment.json", _environment);
        return !_preferencesDirty && !_workspaceDirty && !_predictorContextDirty && !_cursorDirty && environmentSaved;
    }

    public string DescribeCursor() =>
        $"origin={CurrentPersistentOriginSeed};next={CurrentNextCursorSeed};wrapCount={_cursor.WrapCount}";

    public void FlushCursor()
    {
        // A durable resume cursor must never overtake unsaved result records.
        if (_workspaceDirty) FlushWorkspace();
        if (_workspaceDirty) return;
        FlushDocument(ref _cursorDirty, ref _cursorDirtyAge, "search_cursor.json", _cursor);
    }
    public void FlushWorkspace() => FlushDocument(ref _workspaceDirty, ref _workspaceDirtyAge, "search_workspace.json", _workspace);
    public void FlushPredictorContext() => FlushDocument(ref _predictorContextDirty, ref _predictorContextDirtyAge, "predictor_context.json", _predictorContext);
    public void FlushPreferences() => FlushDocument(ref _preferencesDirty, ref _preferencesDirtyAge, "user_preferences.json", _preferences);

    private void SetPersistentOriginOrdinal(ulong ordinal, string source)
    {
        _cursor.Initialized = true;
        _cursor.PersistentOrigin = ordinal;
        _cursor.NextCursor = ordinal;
        ClearSearchCheckpointBinding();
        MarkCursorDirty();
        FlushCursor();
        RuntimeLog.Info(
            $"persistentSearchOriginChanged=true;source={source};origin={ordinal};seed={Beta110SeedCodec.FormatOrdinal(ordinal)};nextCursor={ordinal}");
    }

    private void EnsureCursorInitialized()
    {
        if (_cursor.Initialized && _cursor.PersistentOrigin < Beta110SeedCodec.SpaceSize && _cursor.NextCursor < Beta110SeedCodec.SpaceSize)
            return;
        ulong ordinal = RandomVisibleOrdinal();
        _cursor = new SearchCursorDocument
        {
            Initialized = true,
            PersistentOrigin = ordinal,
            NextCursor = ordinal,
            WrapCount = 0,
            CheckpointVersion = 1
        };
        MarkCursorDirty();
        FlushCursor();
        RuntimeLog.Info($"persistentSearchCursorInitialized=true;origin={ordinal};seed={Beta110SeedCodec.FormatOrdinal(ordinal)};randomSource=SystemCryptographicRandom;originPolicy=RandomPrefix8ZeroSuffix4;continuation=Sequential");
    }

    private static ulong RandomVisibleOrdinal()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt64(bytes);
        }
        while (value < VisibleSeedBlockPolicy.MinimumUnbiasedRandomValue);
        return VisibleSeedBlockPolicy.RandomOriginFromValue(value);
    }

    private void NormalizeDocuments()
    {
        _preferences.SchemaVersion = SchemaVersion;
        _preferences.Language = string.Equals(_preferences.Language, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" :
            string.Equals(_preferences.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : string.Empty;
        _preferences.SearchMode = string.Equals(_preferences.SearchMode, "CPU", StringComparison.OrdinalIgnoreCase) ? "CPU" : "Auto";
        _preferences.WorkbenchFlags ??= new();
        _preferences.ReadNotesVersions ??= new();
        if (_preferences.Optimizations is null)
        {
            _preferences.Optimizations = SearchOptimizationSettings.AllEnabled;
            MarkPreferencesDirty();
        }
        // The old global button only acknowledged release notes, not positioning.
        if (!string.IsNullOrWhiteSpace(_preferences.ReadReleaseNotesVersion))
            _preferences.ReadNotesVersions.TryAdd("release", _preferences.ReadReleaseNotesVersion);
        if (_preferences.SearchWorkerBudget is { } budget)
            _preferences.SearchWorkerBudget = ResolveSearchWorkers(budget, System.Environment.ProcessorCount);
        var resources = SearchResources;
        if (_preferences.CpuLoadPercent != resources.CpuPercent || _preferences.GpuLoadPercent != resources.GpuPercent)
            MarkPreferencesDirty();
        _preferences.CpuLoadPercent = resources.CpuPercent;
        _preferences.GpuLoadPercent = resources.GpuPercent;
        _preferences.LastPage = string.IsNullOrWhiteSpace(_preferences.LastPage)
            ? "Analysis"
            : _preferences.LastPage.Trim();
        _workspace.SchemaVersion = SchemaVersion;
        _workspace.Results ??= new List<PersistedSearchResult>();
        _predictorContext.SchemaVersion = SchemaVersion;
        _predictorContext.Ascension = Math.Clamp(_predictorContext.Ascension, SeedPredictionInputLimits.MinimumAscension, SeedPredictionInputLimits.MaximumAscension);
        _predictorContext.PlayersCount = Math.Clamp(_predictorContext.PlayersCount, SeedPredictionInputLimits.MinimumPlayers, SeedPredictionInputLimits.MaximumPlayers);
        _predictorContext.PlayerSlotIndex = Math.Clamp(_predictorContext.PlayerSlotIndex, 0, Math.Max(0, _predictorContext.PlayersCount - 1));
        _predictorContext.Seed ??= string.Empty;
        _predictorContext.PreferredOpeningRouteId ??= string.Empty;
        if (_predictorContext.PreferredOpeningChoiceSlotIndex < -1)
            _predictorContext.PreferredOpeningChoiceSlotIndex = -1;
        _predictorContext.PreferredRewardRouteGroupId ??= string.Empty;
        // Pre-cutover SearchDraft embedded run/session controls. They are deliberately
        // no longer semantic draft state. Existing persistent cursor and runtime settings
        // remain authoritative; the UI-local run draft is repopulated on the next save.
        if (_workspace.Draft is not null && _workspace.RunDraft is null)
        {
            RuntimeLog.Info("searchWorkspaceRunDraftMigrated=true;legacyEmbeddedRunOptionsDropped=true");
        }
        // Beta 19.x stored Combat Reward as battle-bound rows. The current Draft
        // owns independent Card/Potion sequences. System.Text.Json can deserialize
        // the old object with null constructor-backed members, so normalize only
        // that local Draft block to Empty instead of letting UI restore fail later.
        if (_workspace.Draft?.CombatRewardDraft is { } rewardDraft &&
            (rewardDraft.Cards is null || rewardDraft.Potions is null))
        {
            _workspace.Draft = _workspace.Draft with { CombatRewardDraft = CombatRewardSearchDraft.Empty };
            RuntimeLog.Info("searchWorkspaceCombatRewardDraftMigrated=true;legacyBattleBundleDropped=true");
        }
        _cursor.SchemaVersion = SchemaVersion;
        _cursor.CheckpointQueryFingerprint ??= "";
        _cursor.CheckpointEnvironmentFingerprint ??= "";
        _environment.SchemaVersion = SchemaVersion;
    }

    private IEnumerable<string> CompareEnvironment(SearchEnvironmentSignature previous, SearchEnvironmentSignature current)
    {
        if (!string.Equals(previous.GameVersionIdentity, current.GameVersionIdentity, StringComparison.Ordinal)) yield return "GameVersionChanged";
        if (!string.Equals(previous.CharacterCatalogFingerprint, current.CharacterCatalogFingerprint, StringComparison.Ordinal)) yield return "CharacterCatalogChanged";
        if (!string.Equals(previous.CardCatalogFingerprint, current.CardCatalogFingerprint, StringComparison.Ordinal)) yield return "CardCatalogChanged";
        if (!string.Equals(previous.RelicCatalogFingerprint, current.RelicCatalogFingerprint, StringComparison.Ordinal)) yield return "RelicCatalogChanged";
        if (!string.Equals(previous.PotionCatalogFingerprint, current.PotionCatalogFingerprint, StringComparison.Ordinal)) yield return "PotionCatalogChanged";
        if (!string.Equals(previous.WorldCatalogFingerprint, current.WorldCatalogFingerprint, StringComparison.Ordinal)) yield return "WorldCatalogChanged";
        if (!string.Equals(previous.UnlockFingerprint, current.UnlockFingerprint, StringComparison.Ordinal)) yield return "UnlockStateChanged";
        if (!string.Equals(previous.Fingerprint, current.Fingerprint, StringComparison.Ordinal)) yield return "RuntimeEnvironmentChanged";
    }

    private T? Load<T>(string fileName, JsonSerializerOptions? options = null) where T : class
    {
        string path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            using (var header = JsonDocument.Parse(json))
                if (header.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var field in header.RootElement.EnumerateObject())
                        if (field.Name.Equals("SchemaVersion",StringComparison.OrdinalIgnoreCase) &&
                            field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out int version) && version > SchemaVersion)
                            throw new InvalidDataException($"UnsupportedFutureSchema:{version}");
            T value = JsonSerializer.Deserialize<T>(json, options ?? _json)
                ?? throw new InvalidDataException("PersistenceDocumentNull");
            int schema = value switch
            {
                UserPreferencesDocument item => item.SchemaVersion,
                SearchWorkspaceDocument item => item.SchemaVersion,
                PredictorContextDocument item => item.SchemaVersion,
                SearchCursorDocument item => item.SchemaVersion,
                SearchEnvironmentSignature item => item.SchemaVersion,
                _ => SchemaVersion
            };
            if (schema > SchemaVersion)
                throw new InvalidDataException($"UnsupportedFutureSchema:{schema}");
            return value;
        }
        catch (Exception ex)
        {
            // A future schema is not corruption. Preserve it in place; likewise
            // never overwrite a file whose failed read/quarantine left it intact.
            if (ex is not InvalidDataException || !ex.Message.StartsWith("UnsupportedFutureSchema:", StringComparison.Ordinal))
                Quarantine(path);
            if (File.Exists(path)) _unreadableDocuments.Add(fileName);
            WarnReadOnce(fileName, ex);
            return null;
        }
    }

    private void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string quarantined = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            File.Move(path, quarantined, overwrite: false);
        }
        catch
        {
            // Fail-soft: a quarantine failure must never block the UI.
        }
    }

    private void FlushDocument<T>(ref bool dirty, ref double age, string fileName, T document)
    {
        if (!dirty) return;
        if (WriteNow(fileName, document)) dirty = false;
        age = 0d;
    }

    private bool WriteNow<T>(string fileName, T document)
    {
        if (_unreadableDocuments.Contains(fileName)) return false;
        try
        {
            string path = Path.Combine(_directory, fileName);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(document, _json);
            SearchPersistenceFile.WriteAtomic(path, payload);
            return true;
        }
        catch (Exception ex)
        {
            WarnWriteOnce(fileName, ex);
            return false;
        }
    }

    private void WarnReadOnce(string fileName, Exception ex)
    {
        if (_readWarningIssued) return;
        _readWarningIssued = true;
        RuntimeLog.Warn($"searchPersistenceReadFailed=true;file={fileName};failSoft=true;issue={ex.GetType().Name}:{ex.Message}");
    }

    private void WarnWriteOnce(string fileName, Exception ex)
    {
        if (_writeWarningIssued) return;
        _writeWarningIssued = true;
        RuntimeLog.Warn($"searchPersistenceWriteFailed=true;file={fileName};inMemoryContinues=true;issue={ex.GetType().Name}:{ex.Message}");
    }

    private void MarkPreferencesDirty() { _preferencesDirty = true; _preferencesDirtyAge = 0d; }
    private void MarkWorkspaceDirty() { if (!_workspaceDirty) _workspaceDirtyAge = 0d; _workspaceDirty = true; }
    private void MarkPredictorContextDirty() { _predictorContextDirty = true; _predictorContextDirtyAge = 0d; }
    private void MarkCursorDirty() { if (!_cursorDirty) _cursorDirtyAge = 0d; _cursorDirty = true; }

    internal sealed class ModelKeyJsonConverter : JsonConverter<ModelKey>
    {
        public override ModelKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string value = reader.GetString() ?? string.Empty;
            // ':' is the existing serialized empty ModelKey used by inactive UI
            // rows. Other malformed identities must not silently become that value.
            if (value == ":") return default;
            if (!ModelKey.TryParseExact(value, out ModelKey key))
                throw new JsonException("PersistenceModelKeyInvalid");
            return key;
        }

        public override void Write(Utf8JsonWriter writer, ModelKey value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Serialized);
    }
}

internal sealed class UserPreferencesDocument
{
    public UiColorPreferences Appearance { get; set; } = new();
    public Dictionary<string, bool> WorkbenchFlags { get; set; } = new();
    public string WorkbenchPage { get; set; } = "neow";
    public int SchemaVersion { get; set; } = SearchWorkspacePersistence.SchemaVersion;
    // Last observed Workshop UI language only. The workspace deliberately does not use this
    // as localization authority; live TranslationServer locale wins on startup and hot reload.
    public string Language { get; set; } = string.Empty;
    // Explicit UI choice; missing/empty preserves existing follow-game behavior.
    public string LanguageOverride { get; set; } = string.Empty;
    public float PanelWidth { get; set; }
    public float PanelHeight { get; set; }
    public string SearchMode { get; set; } = "Auto";
    public int? SearchWorkerBudget { get; set; }
    // Nullable so preferences from before resource budgets retain their explicit worker choice.
    public int? CpuLoadPercent { get; set; }
    public int GpuLoadPercent { get; set; } = 100;
    // Missing, null and partially specified older preferences keep all options
    // enabled by default. These are execution choices, never query semantics.
    public SearchOptimizationSettings? Optimizations { get; set; } = SearchOptimizationSettings.AllEnabled;
    public int QueryHistoryLimit { get; set; } = 30;
    public bool ShowOfficialPresets { get; set; } = true;
    // Opt-in, including preferences saved before the in-run entry was introduced.
    public bool ShowInRunPredictionEntry { get; set; }
    public bool SkipExactValidation { get; set; }
    public string ReadReleaseNotesVersion { get; set; } = string.Empty;
    public Dictionary<string, string> ReadNotesVersions { get; set; } = new();
    public string LastPage { get; set; } = "Analysis";
    public bool ActInformationGuideExpanded { get; set; }
    public bool ActInformationIdentityGuideExpanded { get; set; }
    public bool? ActInformationMapGuideExpanded { get; set; }
}

internal sealed class PredictorContextDocument
{
    public SeedLibraryContext? Party { get; set; }
    public string PartySeed { get; set; } = "";
    public bool PartyActive { get; set; }
    public int SchemaVersion { get; set; } = SearchWorkspacePersistence.SchemaVersion;
    public string Seed { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = BaseGameModelKeys.Characters.Silent.Serialized;
    public int Ascension { get; set; } = SeedPredictionInputLimits.MaximumAscension;
    public int PlayersCount { get; set; } = SeedPredictionInputLimits.MinimumPlayers;
    public int PlayerSlotIndex { get; set; }
    // Last-known display value; never a substitute for runtime authority capture.
    public bool? AllCharacterCardPoolsUnlocked { get; set; }
    public bool TezcataraHasBasicStrike { get; set; } = true;
    public bool NonupeipeSwiftEnchantableAtLeast4 { get; set; } = true;
    public bool TanxInstinctEnchantableAtLeast3 { get; set; } = true;
    public bool PaelGoopyDefendCardsAtLeast3 { get; set; } = true;
    public bool PaelAllowLegionNoEventPet { get; set; } = true;
    public bool PaelRemovableCardsAtLeast5 { get; set; } = true;
    public bool OrobasArchaicToothConditionMet { get; set; } = true;
    public bool OrobasTouchOfOrobasConditionMet { get; set; } = true;
    public bool DarvAllowPandorasBoxRelicSet { get; set; } = true;
    public string PreferredOpeningRouteId { get; set; } = string.Empty;
    public int PreferredOpeningChoiceSlotIndex { get; set; } = -1;
    public string PreferredRewardRouteGroupId { get; set; } = string.Empty;

    public AncientOptionConditionProfile AncientOptionConditions => new(
        TezcataraHasBasicStrike,
        NonupeipeSwiftEnchantableAtLeast4,
        TanxInstinctEnchantableAtLeast3,
        PaelGoopyDefendCardsAtLeast3,
        PaelAllowLegionNoEventPet,
        PaelRemovableCardsAtLeast5,
        OrobasArchaicToothConditionMet,
        OrobasTouchOfOrobasConditionMet,
        DarvAllowPandorasBoxRelicSet);
}

internal sealed class SearchWorkspaceDocument
{
    public int SchemaVersion { get; set; } = SearchWorkspacePersistence.SchemaVersion;
    public string CharacterKey { get; set; } = BaseGameModelKeys.Characters.Silent.Serialized;
    public int Ascension { get; set; } = SeedPredictionInputLimits.MaximumAscension;
    public SearchDraft? Draft { get; set; }
    public SearchRunDraft? RunDraft { get; set; }
    public string QueryFingerprint { get; set; } = string.Empty;
    public List<PersistedSearchResult> Results { get; set; } = new();
    public Dictionary<string, string> LegacyPresetQueryKeys { get; set; } = [];
    public Dictionary<string, string> LegacyQueryAliases { get; set; } = [];
    public List<QueryHistoryEntry> QueryHistory { get; set; } = [];
    public string ActiveHistoryId { get; set; } = "";
    public string ResultBatchId { get; set; } = string.Empty;
    public DateTimeOffset? ResultsStartedAtUtc { get; set; }
    public Shell.WorkbenchSearchDraft? ResultDraft { get; set; }
    public SeedLibraryContext? ResultContext { get; set; }
    public bool ResultsSkipExactValidation { get; set; }
    public SearchProgressSnapshot? ResultProgress { get; set; }
}

internal sealed class PersistedSearchResult
{
    public SeedLibraryContext? Context { get; set; }
    public bool IsUnverified { get; set; }
    public PartySeedInformation? Party { get; set; }
    public string Seed { get; set; } = string.Empty;
    public string CharacterKey { get; set; } = string.Empty;
    public int Ascension { get; set; }
    public string QueryFingerprint { get; set; } = string.Empty;
    public string WitnessOpeningRouteId { get; set; } = string.Empty;
}

internal sealed class SearchCursorDocument
{
    public int SchemaVersion { get; set; } = SearchWorkspacePersistence.SchemaVersion;
    public bool Initialized { get; set; }
    public ulong PersistentOrigin { get; set; }
    public ulong NextCursor { get; set; }
    public long WrapCount { get; set; }
    public int CheckpointVersion { get; set; }
    public string CheckpointQueryFingerprint { get; set; } = "";
    public string CheckpointEnvironmentFingerprint { get; set; } = "";
    public bool EndOfSpaceWrapCommitted { get; set; }
}

internal sealed class SearchEnvironmentSignature
{
    public int SchemaVersion { get; set; } = SearchWorkspacePersistence.SchemaVersion;
    public string GameVersionIdentity { get; set; } = string.Empty;
    public string CharacterCatalogFingerprint { get; set; } = string.Empty;
    public string CardCatalogFingerprint { get; set; } = string.Empty;
    public string RelicCatalogFingerprint { get; set; } = string.Empty;
    public string PotionCatalogFingerprint { get; set; } = string.Empty;
    public string WorldCatalogFingerprint { get; set; } = string.Empty;
    public string UnlockFingerprint { get; set; } = string.Empty;
    public string CatalogFingerprint { get; set; } = string.Empty;
    public string RuntimeContentFingerprint { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public int CharacterCount { get; set; }
    public int CardCount { get; set; }
    public int RelicCount { get; set; }
    public int PotionCount { get; set; }
    public int AncientCount { get; set; }
    public bool CaptureComplete { get; set; }
    public string CaptureIssue { get; set; } = string.Empty;
}

internal static class SearchEnvironmentSignatureBuilder
{
    public static SearchEnvironmentSignature Capture(ModRuntimeSnapshot runtime)
    {
        if (!ProfileSeedGenerator.TryCreateProbeSeed(runtime.Profile, out string seed, out string issue))
            return new SearchEnvironmentSignature
            {
                GameVersionIdentity = runtime.Detection.DisplayVersion,
                CaptureComplete = false,
                CaptureIssue = issue + ";versionEvidence=" + runtime.Detection.Evidence
            };
        try
        {
            RuntimeContextAuthoritySnapshot authority = RuntimeContextAuthorityCapture.CaptureRuntimeReadOnly(
                runtime.Profile,
                seed,
                CharacterIdentity.FromKey(BaseGameModelKeys.Characters.Silent),
                SeedPredictionInputLimits.MaximumAscension,
                runtime.Detection.DisplayVersion,
                playersCount: 1,
                playerSlotIndex: 0,
                predictionGameMode: WorldGameMode.Singleplayer,
                predictionGameModeAuthority: PredictionGameModeAuthority.ExplicitRequest);

            var effect = authority.EffectAuthority;
            var world = authority.WorldAuthority?.Beta109Generation;
            ModelKey[] characters = (world?.AllCharactersInSourceOrder ?? BaseGameModelKeys.Characters.All)
                .Where(key => key.IsValid).Distinct(ModelKeyComparer.Instance).OrderBy(key => key.Serialized, StringComparer.Ordinal).ToArray();
            ModelKey[] cards = EnumerateCards(effect)
                .Distinct(ModelKeyComparer.Instance).OrderBy(key => key.Serialized, StringComparer.Ordinal).ToArray();
            ModelKey[] relics = EnumerateRelics(effect, world)
                .Distinct(ModelKeyComparer.Instance).OrderBy(key => key.Serialized, StringComparer.Ordinal).ToArray();
            ModelKey[] potions = (effect?.PotionPool ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectPotionSnapshot>())
                .Select(item => item.PotionKey).Where(key => key.IsValid)
                .Distinct(ModelKeyComparer.Instance).OrderBy(key => key.Serialized, StringComparer.Ordinal).ToArray();
            ModelKey[] ancients = (world?.OrderedActCatalog ?? Array.Empty<RolltheSpire2.Core.World.Snapshots.Beta109ActGenerationSnapshot>())
                .SelectMany(act => act.OrderedAncients)
                .Concat(world?.AllSharedAncients ?? Array.Empty<ModelKey>())
                .Where(key => key.IsValid).Distinct(ModelKeyComparer.Instance).OrderBy(key => key.Serialized, StringComparer.Ordinal).ToArray();

            string characterFingerprint = HashKeys(characters);
            string cardFingerprint = HashKeys(cards);
            string relicFingerprint = HashKeys(relics);
            string potionFingerprint = HashKeys(potions);
            string eventFingerprint = world is null
                ? string.Empty
                : HashText(string.Join("|", new[]
                {
                    world.EventAuthority.EvidenceCode ?? string.Empty,
                    world.EventAuthority.SharedRawOrderExact.ToString(),
                    HashKeys(world.EventAuthority.OrderedSharedEventsRaw),
                    world.EventAuthority.EpochFilterOrderExact.ToString(),
                    string.Join(";", world.EventAuthority.EpochsInFilterOrder.Select(epoch =>
                        string.Join(":",
                            epoch.EpochId,
                            epoch.IsRevealed ? "1" : "0",
                            epoch.MembershipExact ? "1" : "0",
                            epoch.RevealFactExact ? "1" : "0",
                            HashKeys(epoch.OrderedMemberKeys))))
                }));
            string worldFingerprint = HashText(string.Join("|", new[]
            {
                world?.CatalogFingerprint ?? string.Empty,
                eventFingerprint,
                HashKeys(ancients)
            }));
            string unlockFingerprint = HashText(string.Join("|", new[]
            {
                authority.UnlockSnapshotFingerprint,
                effect?.UnlockFingerprint ?? string.Empty,
                world?.UnlockFingerprint ?? string.Empty
            }));
            string catalogFingerprint = HashText(string.Join("|", new[]
            {
                authority.CatalogFingerprint,
                effect?.CatalogFingerprint ?? string.Empty,
                world?.CatalogFingerprint ?? string.Empty,
                characterFingerprint,
                cardFingerprint,
                relicFingerprint,
                potionFingerprint,
                worldFingerprint
            }));
            string gameVersion = authority.ExactGameVersionIdentity.ToString();
            var semantic = RuntimeAuthorityEnvironment.Current.Authority.Fingerprint;
            // Actual loaded DLL module identities and mod versions supplement the
            // semantic catalog hash. No local paths or old preset provenance enter it.
            string runtimeContent = HashText(JsonSerializer.Serialize(new
            {
                GameModule = typeof(MegaCrit.Sts2.Core.Models.ModelDb).Assembly.ManifestModule.ModuleVersionId,
                semantic.SchemaVersion, semantic.OverallSemanticHash, semantic.UnlockUniverseHash,
                Mods = MegaCrit.Sts2.Core.Modding.ModManager.Mods
                    .Where(m => m.state == MegaCrit.Sts2.Core.Modding.ModLoadState.Loaded)
                    .Select(m => new { m.manifest?.id, m.manifest?.version,
                        Modules = m.assemblies.Select(a => a.ManifestModule.ModuleVersionId).ToArray() }).ToArray()
            }));
            string fingerprint = HashText(string.Join("|", gameVersion, catalogFingerprint, unlockFingerprint, runtimeContent));
            return new SearchEnvironmentSignature
            {
                GameVersionIdentity = gameVersion,
                CharacterCatalogFingerprint = characterFingerprint,
                CardCatalogFingerprint = cardFingerprint,
                RelicCatalogFingerprint = relicFingerprint,
                PotionCatalogFingerprint = potionFingerprint,
                WorldCatalogFingerprint = worldFingerprint,
                UnlockFingerprint = unlockFingerprint,
                CatalogFingerprint = catalogFingerprint,
                RuntimeContentFingerprint = runtimeContent,
                Fingerprint = fingerprint,
                CharacterCount = characters.Length,
                CardCount = cards.Length,
                RelicCount = relics.Length,
                PotionCount = potions.Length,
                AncientCount = ancients.Length,
                CaptureComplete = semantic.Complete,
                CaptureIssue = semantic.Complete ? string.Empty : semantic.EvidenceCode
            };
        }
        catch (Exception ex)
        {
            // Environment capture is a compatibility guard, never an app-start blocker.
            RuntimeLog.Warn($"searchEnvironmentSignatureCaptureFailed=true;failSoft=true;issue={ex.GetType().Name}:{ex.Message}");
            string version = GameVersionIdentity.From(runtime.Detection.DisplayVersion).ToString();
            return new SearchEnvironmentSignature
            {
                GameVersionIdentity = version,
                CaptureComplete = false,
                CaptureIssue = ex.GetType().Name + ":" + ex.Message,
                Fingerprint = string.Empty
            };
        }
    }

    private static IEnumerable<ModelKey> EnumerateCards(RolltheSpire2.Core.Effects.Snapshots.NeowEffectAuthoritySnapshot? effect)
    {
        if (effect is null) yield break;
        foreach (var card in effect.CharacterRewardPool ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectCardSnapshot>()) yield return card.CardKey;
        foreach (var card in effect.ColorlessRewardPool ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectCardSnapshot>()) yield return card.CardKey;
        foreach (var pool in effect.OtherCharacterPools ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.CharacterCardPoolSnapshot>())
        foreach (var card in pool.Cards) yield return card.CardKey;
        foreach (var card in effect.TransformPool ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectCardSnapshot>()) yield return card.CardKey;
        foreach (ModelKey curse in effect.GeneratedCursePool ?? Array.Empty<ModelKey>()) yield return curse;
    }

    private static IEnumerable<ModelKey> EnumerateRelics(
        RolltheSpire2.Core.Effects.Snapshots.NeowEffectAuthoritySnapshot? effect,
        RolltheSpire2.Core.World.Snapshots.Beta109WorldGenerationSnapshot? world)
    {
        foreach (var relic in effect?.OrderedRelicBag ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectRelicSnapshot>()) yield return relic.RelicKey;
        foreach (var relic in effect?.SharedRelicPoolSource ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectRelicSnapshot>()) yield return relic.RelicKey;
        foreach (var relic in effect?.CharacterRelicPoolSource ?? Array.Empty<RolltheSpire2.Core.Effects.Snapshots.NeowEffectRelicSnapshot>()) yield return relic.RelicKey;
        foreach (var bucket in world?.SharedRelicBuckets ?? Array.Empty<RolltheSpire2.Core.World.Snapshots.Beta109RelicBucketSnapshot>())
        foreach (ModelKey relic in bucket.OrderedRelics) yield return relic;
        foreach (var bucket in world?.PlayerRelicBuckets ?? Array.Empty<RolltheSpire2.Core.World.Snapshots.Beta109RelicBucketSnapshot>())
        foreach (ModelKey relic in bucket.OrderedRelics) yield return relic;
    }

    private static string HashKeys(IEnumerable<ModelKey> keys) =>
        HashText(string.Join("\n", keys.Select(key => key.Serialized).OrderBy(value => value, StringComparer.Ordinal)));

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();
}
