using Godot;
using System.Text.Json;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Ui.Persistence;

namespace RolltheSpire2.Ui.Shell;

internal sealed partial class QueryWorkbenchFrame
{
    private ModRuntimeSnapshot _runtime = null!;
    private SearchWorkspacePersistence _persistence = null!;
    private IProductionSearchSession? _session;
    private ExactSearchExecutionRequest? _resultPlan;
    private Task<ProductionExactSearchResult>? _manualValidation;
    private SearchCandidate? _validatingCandidate;
    private RolltheSpire2.Core.Diagnostics.SearchDiagnosticSummarySnapshot? _lastDiagnostics;
    internal object? CaptureSearchDiagnostics() => _session?.GetDiagnosticSummary() ?? _lastDiagnostics;
    internal bool HasActiveSearch => _session is not null || _persistence.HasSearchInFlight;
    internal void SyncSearchPreferences()
    {
        _planSelector?.Select(_persistence.Preferences.SearchMode == "CPU" ? 1 : 0);
        _analysisKey = "";
        UpdateDraftStatus();
        if (_session is null) Refresh(_language, _text);
    }
    private readonly List<SearchCandidate> _results = [];
    private Button? _start;
    private Label? _checkpointLabel;
    private string _checkpointFingerprint = "";
    private bool _pauseRequested;
    private Label? _status, _summary;
    private OptionButton? _planSelector;
    private SpinBox? _targetCount;
    private ScrollContainer? _resultSurface;
    private VBoxContainer? _resultRows;
    private string _activeFingerprint = "", _lastSaved = "", _lastPreferences = "", _lastIssue = "";
    private bool _restored, _showResults;
    private bool _loadFailed;
    private bool _restoreAdjusted;
    private double _receiptRemaining;
    private readonly Dictionary<BaseButton, bool> _runBlocked = [];
    private int _target = 30;
    private double _saveTick;
    private double _resultRefreshRemaining;
    private bool _resultRefreshPending;
    private long _completionFlushRetryAfter;
    private bool _completionSaveFailed;

    internal WorkbenchSearchDraft CaptureDraft()
    {
        if (_multiplayer) return CapturePartyDraft();
        SearchQuery q = _neowEditor.ExportQuery();
        q = q with { RelicSequenceConstraints = _relicEditor.BuildConditions(), CombatCardRewards = _combatEditor.ExportCondition(), CombatPotionRewards = _combatEditor.ExportPotionCondition() };
        q = _ancientEditor.ExportQuery(q);
        q = _eventEditor.ExportQuery(q);
        q = _actInformationEditor.ExportQuery(q);
        q = _shopEditor.ExportQuery(q);
        q = q with { TransformationAggregate = _transformationEditor.ExportCondition(q) };
        return new(_soloCharacter, _soloAscension, q, _ancientEditor.OptionConditions)
        { AncientEditor = _ancientEditor.ExportEditorState() };
    }

    internal void RestoreDraft(WorkbenchSearchDraft draft, bool render = true)
    {
        if (draft.Version is not (1 or 2 or 3 or 4)) throw new InvalidOperationException("integration.load_version");
        if (draft.Players.Count > 0) { RestorePartyDraft(draft, render); return; }
        ValidateRepresentable(draft.Query);
        _multiplayer = false; _seat = 0; _soloCharacter = draft.Character; _soloAscension = draft.Ascension;
        RefreshEditorContexts(render: false);
        _neowEditor.ImportQuery(draft.Query);
        _relicEditor.ImportQuery(draft.Query);
        _combatEditor.ImportQuery(draft.Query);
        _ancientEditor.ImportQuery(draft.Query, draft.AncientPremises, draft.AncientEditor);
        _eventEditor.ImportQuery(draft.Query);
        _actInformationEditor.ImportQuery(draft.Query);
        _shopEditor.ImportQuery(draft.Query);
        _transformationEditor.ImportQuery(draft.Query);
        RefreshEditorContexts(render);
        _restoreAdjusted=JsonSerializer.Serialize(draft.WithoutCapturedAuthority().Query)!=
            JsonSerializer.Serialize(CaptureDraft().WithoutCapturedAuthority().Query);
    }

    // This UI has a narrower grammar than the backend. Never truncate foreign/older drafts.
    private static void ValidateRepresentable(SearchQuery q, bool allowSharedAncientIdentity = false)
    {
        bool unsupported = q.StandardMaps.Any(c=>!c.IsValid) || q.StandardMaps.Any(c=>c.RouteObjective && c.Scope==0) && q.StandardMaps.Any(c=>c.RouteObjective && c.Scope>0) || q.StandardMaps.Where(c=>c.RouteObjective).GroupBy(c=>c.Scope).Any(g=>g.Count()>1) || q.CombatCardRewards is { Count: > 6 or < 1 } || q.CombatPotionRewards is { Count: > 6 or < 1 } ||
            q.CombatPotionRewards is { } potionSequence && (potionSequence.Slots.Count != potionSequence.Count || potionSequence.Slots.Any(p=>!p.IsValid)) ||
            q.CombatCardRewards is { } cardSequence && cardSequence.Slots.Count != cardSequence.Count ||
            q.LegacyCombatRewardConstraints.Count != 0 || q.LegacyWorld.BossFilters.Count != 0 ||
            q.LegacyWorld.BossOrdinalFilters.Count != 0 ||
            (!allowSharedAncientIdentity && q.LegacyWorld.AncientIdentityFilters.Count != 0) ||
            (allowSharedAncientIdentity && (q.LegacyWorld.AncientIdentityFilters
                .Any(c => c.Act is not (2 or 3) || c.Keys.Any.Count is < 1 or > 4 || c.Keys.All.Count != 0 || c.Keys.Ban.Count != 0 ||
                    c.Keys.Any.Distinct(ModelKeyComparer.Instance).Count() != c.Keys.Any.Count) ||
                q.LegacyWorld.AncientIdentityFilters.Select(c => c.Act).Distinct().Count() != q.LegacyWorld.AncientIdentityFilters.Count)) ||
            q.LegacyWorld.AncientSeaGlassTargetFilters.Count != 0 || q.MerchantColorlessConditions.Count != 0 ||
            q.AncientBranches.Any(b => b.Act is not (2 or 3) || b.SeaGlassTargetAny.Count > 1) ||
            q.VariantBossBranches.Any(b => b.IncludesSecondBoss || !b.SecondBoss.IsEmpty || b.FirstBoss.All.Count > 0 || b.FirstBoss.Ban.Count > 0) ||
            q.RelicSequenceConstraints.Any(c => c.Keys.Any.Count > 1) ||
            q.EventSequenceConstraints.Any(c => c.Source is not null || c.Keys.Any.Count > 1) ||
            q.TransformationAggregate is { UsesNeow: true, Opening: TransformationOpening.None };
        unsupported |= q.AncientBranches.Any(b => !b.IsValid) ||
            q.VariantBossBranches.Any(b => !b.IsValid) ||
            q.EventSequenceConstraints.Any(c => c.Act is < 1 or > 3 || c.RangeValue < 1) ||
            q.StructuredOpeningEffects.Any(c => c.OutputKeys.Count > (c.Kind == NeowStructuredConditionKind.ExactGroupedCapsuleMultiset ? 3 :
                c.SourceRelicKey == BaseGameModelKeys.Relics.ScrollBoxes ? 3 : c.Kind is NeowStructuredConditionKind.ExactUnorderedPair or NeowStructuredConditionKind.IndependentOfferGroupTargets ? 2 : 1)) ||
            q.OpeningRouteRelicRequirement is { RequiredRelicKeys.Count: > 2 } ||
            q.LegacyWorld.AncientOptionFilters.Any(c => c.Act is not (2 or 3) || c.Keys.Any.Count > 0 || c.Keys.Ban.Count > 0 ||
                q.AncientBranches.Count(b => b.Act == c.Act) != 1) ||
            q.LegacyNeow.NeowRelics.Any.Count > 0 && q.LegacyNeow.NeowRelics.All.Count > 0;
        unsupported |= q.EventResultConditions.GroupBy(c=>c.Kind).Any(g=>g.Count()>1) ||
            q.EventResultConditions.Any(c=>!Enum.IsDefined(c.Kind));
        unsupported |= q.EventResultConditions.Any(c => c.Kind == EventResultConditionKind.TrialNondescriptInitialBasicsContains) &&
            q.EventResultConditions.Any(c => c.Kind == EventResultConditionKind.TrialCase && c.TrialCase != TrialCaseTarget.Nondescript);
        foreach(var effect in q.StructuredOpeningEffects)
        {
            bool special=effect.SourceRelicKey==BaseGameModelKeys.Relics.ScrollBoxes ||
                effect.SourceRelicKey==BaseGameModelKeys.Relics.Kaleidoscope ||
                effect.SourceRelicKey==BaseGameModelKeys.Relics.NeowsBones &&
                (effect.Scope==NeowStructuredEffectScope.FinalCurse || effect.Kind==NeowStructuredConditionKind.ExactGroupedCapsuleMultiset);
            if(!special && !Pages.Search.Neow.NeowEffectCardRegistry.Get(effect.SourceRelicKey).Components.Any(c=>
                c.Scope==effect.Scope && c.OutputKind==effect.OutputKind && c.ConditionKind==effect.Kind)) unsupported=true;
        }
        var horizons=q.MerchantColorlessSequenceConditions.Select(c=>c.Count).Concat(q.RelicShopSequenceConditions.Select(c=>c.Count)).Distinct().ToArray();
        unsupported |= horizons.Length > 1 || horizons.Any(n=>n is <1 or >5) || q.RelicShopSequenceConditions.Count>1 ||
            q.MerchantColorlessSequenceConditions.GroupBy(c=>c.Slot).Any(g=>g.Count()>1);
        if (q.TransformationAggregate is { } t)
        {
            unsupported |= !Enum.IsDefined(t.Opening) || !Enum.IsDefined(t.Predicate) || !Enum.IsDefined(t.PickupOrder) ||
                t.TargetMultiset.Count>t.OpportunityCount || t.MinimumRareCount>t.OpportunityCount;
            unsupported |= t.UsesNeow && q.OpeningRoute is null;
            bool Queued(string id) => q.EventSequenceConstraints.Any(c=>c.Keys.Any.Concat(c.Keys.All).Any(k=>k.Entry==id));
            unsupported |= t.MorphicGrove && !Queued("MORPHIC_GROVE") || t.AromaOfChaos && !Queued("AROMA_OF_CHAOS") || t.WhisperingHollow && !Queued("WHISPERING_HOLLOW") || t.Symbiote && !Queued("SYMBIOTE") || t.TrialNondescript && !Queued("TRIAL");
        }
        // Legacy Neow sidecar is only used for A's Neow offer set in this editor.
        var n = q.LegacyNeow;
        unsupported |= n.NeowRelics.Ban.Count > 0 || n.RequireNeowsBones || !n.BonesRelics.IsEmpty || n.RequiredBonesCombination.Count > 0 ||
            n.RequireSmallCapsule || n.RequireLargeCapsule || !n.CapsuleContainedRelics.IsEmpty || n.RequireWhetstone || n.RequireWarPaint ||
            n.RequiredFinalCurse.HasValue || n.BannedFinalCurses.Count > 0 || n.RequiredBonesAcquisitionOrder.Count > 0 || n.EffectOutputConditions.Count > 0 || n.Preset != NeowSearchPreset.None;
        if (unsupported) throw new InvalidOperationException("integration.load_shape");
    }

    private Dictionary<string, bool> CaptureUiPreferences() => new()
    {
        ["n.guide"]=_neowEditor.GuideOpen,["c.guide"]=_combatEditor.GuideOpen,["r.guide"]=_relicEditor.GuideOpen,
        ["a.guide"]=_ancientEditor.GuideOpen,["e.guide"]=_eventEditor.GuideOpen,["t.guide"]=_transformationEditor.GuideOpen,
        ["s.guide"]=_shopEditor.GuideOpen,["r.advanced"]=_relicEditor.AdvancedOpen,["a.advanced"]=_ancientEditor.AdvancedOpen,
        ["e.advanced"]=_eventEditor.AdvancedOpen,["m.advanced"]=_actInformationEditor.AdvancedOpen
    };
    private void RestoreUiPreferences()
    {
        var f = _persistence.Preferences.WorkbenchFlags;
        _neowEditor.GuideOpen=f.GetValueOrDefault("n.guide"); _combatEditor.GuideOpen=f.GetValueOrDefault("c.guide");
        _relicEditor.GuideOpen=f.GetValueOrDefault("r.guide"); _ancientEditor.GuideOpen=f.GetValueOrDefault("a.guide");
        _eventEditor.GuideOpen=f.GetValueOrDefault("e.guide"); _transformationEditor.GuideOpen=f.GetValueOrDefault("t.guide");
        _shopEditor.GuideOpen=f.GetValueOrDefault("s.guide"); _relicEditor.AdvancedOpen=f.GetValueOrDefault("r.advanced");
        // Ancient mode is restored with the query/editor state, not a display preference.
        _eventEditor.AdvancedOpen=f.GetValueOrDefault("e.advanced");
        _actInformationEditor.AdvancedOpen=f.GetValueOrDefault("m.advanced");
        string page = _persistence.Preferences.WorkbenchPage;
        if (page == "act") page = "map"; // Older Workbench preference used one combined entry.
        if (_domainButtons.ContainsKey(page)) _selectedDomain=page;
    }

    private void BuildProductionDock()
    {
        BuildSearchSidebar();
        _analysisKey="";
        var statusScroll = new ScrollContainer { Name = "SearchStatus", Position = new(CenterLeft + 8, DockTop + 6),
            Size = new(CenterWidth - 16, DockHeight - 6), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(statusScroll);
        var statusColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        statusScroll.AddChild(statusColumn);
        _status=_p.Label("",15,true); _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.AutowrapMode=TextServer.AutowrapMode.WordSmart; statusColumn.AddChild(_status);
        if (_loadFailed)
        {
            var recover = _p.Button(_text.Get("integration.recover_draft"));
            recover.Name = "RecoverWorkbenchDraft";
            recover.TooltipText = _text.Get("integration.recover_draft_hint");
            recover.Pressed += RecoverDraft;
            statusColumn.AddChild(recover);
        }
        BuildRuntimeSurface();
        if(_showResults) RenderResults();
        UpdateDraftStatus();
        if (_loadFailed && _status is not null) _status.Text=_text.Get("integration.load_shape");
        else if (_restoreAdjusted) Receipt("integration.revalidated");
        LockForRun(_session is not null);
    }

    private string Explain(Exception ex)
    {
        RuntimeLog.Warn("workbenchQuery="+ex);
        if (ex.Message.StartsWith(RolltheSpire2.Compatibility.ProfileSeedGenerator.UnavailableCode, StringComparison.Ordinal))
            return SeedProfileUnavailableMessage();
        if (TransformationContinuationIssueKey(ex.Message) is { } continuationKey) return _text.Get(continuationKey);
        if (ex.Message is "FamilyGpuInitializationRetryRequired" or "FamilyGpuInitializationBusy")
            return RolltheSpire2.Presentation.Localization.JsonUiTextProvider.Create(_language).Get(
                ex.Message == "FamilyGpuInitializationBusy" ? "ui1.devices.creating" : "ui1.devices.creation_failed_help");
        return ex.Message.StartsWith("integration.",StringComparison.Ordinal) ? _text.Get(ex.Message) : _text.Get("integration.rejected")+"\n"+ex.Message;
    }
    private string SeedProfileUnavailableMessage() => _runtime.Detection.IsExact
        ? _text.Format("integration.seed_profile_unavailable", _runtime.Detection.DisplayVersion, _runtime.Profile.ProfileId)
        : _text.Get("integration.game_version_unavailable");
    internal static string? TransformationContinuationIssueKey(string issue) =>
        issue.Contains("NeowDependentRelicContinuationNotSupported", StringComparison.Ordinal) ? "integration.transform.relic_continuation_conflict" :
        issue.Contains("NeowDependentCombatRewardContinuationNotSupported", StringComparison.Ordinal) ? "integration.transform.combat_continuation_conflict" :
        issue.Contains("NeowDependentRewardRelicContinuationNotSupportedInV1", StringComparison.Ordinal) ? "integration.transform.continuation_conflict" : null;
    private void SaveDraft()
    {
        try
        {
            // Unavailable catalogs must not replace the saved intent on panel closure.
            _ = RolltheSpire2.Compatibility.ProfileSeedGenerator.CreateProbeSeed(_runtime.Profile);
            var draft=CaptureDraft().WithoutCapturedAuthority();
            _persistence.SaveWorkbench(draft); _lastSaved=JsonSerializer.Serialize(draft);
            _loadFailed=false;
            UpdateResultNavigation();
        }
        catch(Exception ex) { if(_status is not null) _status.Text=Explain(ex); }
    }
    private void RecoverDraft()
    {
        if (!_loadFailed || _session is not null) return;
        try
        {
            var fresh = new WorkbenchSearchDraft(BaseGameModelKeys.Characters.Silent, 0, SearchQuery.Empty,
                RolltheSpire2.Core.World.AncientOptionConditionProfile.BroadDefault);
            _persistence.RecoverWorkbench(fresh);
            RestoreDraft(fresh, render: false);
            _loadFailed = false; _lastIssue = ""; _analysisKey = "";
            _lastSaved = JsonSerializer.Serialize(fresh.WithoutCapturedAuthority());
            Refresh(_language, _text);
            Receipt("integration.recovered_draft");
        }
        catch (Exception ex) { if (_status is not null) _status.Text = Explain(ex); }
    }
    private void UpdateDraftStatus()
    {
        if(_session is not null) return;
        try
        {
            _ = RolltheSpire2.Compatibility.ProfileSeedGenerator.CreateProbeSeed(_runtime.Profile);
            var d=CaptureDraft().WithoutCapturedAuthority(); var q=d.Query;
            if (_editorContextIssue.Length > 0) throw new InvalidOperationException(_editorContextIssue);
            if(_start is not null) _start.Disabled=_loadFailed || _presetBlocked.Count > 0 || _persistence.HasSearchInFlight;
            string serialized=JsonSerializer.Serialize(d);
            if(!UpdateProbabilityPanel(d,serialized)) {
                if(_start is not null)_start.Disabled=true;
                if(_status is not null)_status.Text=_draftCompileIssue;
                return;
            }
            if(serialized!=_lastSaved && !_loadFailed)
            {
                _persistence.SaveWorkbench(d); _lastSaved=serialized;
                UpdateResultNavigation();
                if(_status is not null) _status.Text="";
            }
            _lastIssue="";
            UpdateSearchAction();
        }
        catch(Exception ex)
        {
            _checkpointFingerprint = "";
            _analysisKey="";
            _probabilityPreview.Invalidate();
            _probabilityPending = false;
            if(_summary is not null) _summary.Text="—";
            _familyEntries.Clear(); RenderFamilyDashboard();
            _expectationContext="";
            PresentExpectations(null,null,null);
            if(ex.Message!=_lastIssue || _status?.Text.Length==0) { if(_status is not null) _status.Text=Explain(ex); _lastIssue=ex.Message; }
            UpdateSearchAction();
        }
    }

    private void StartSearch()
    {
        if (_presetBlocked.Count > 0 || _manualValidation is not null || _persistence.HasSearchInFlight) return;
        bool intentValidated = false;
        try
        {
            if (_loadFailed) throw new InvalidOperationException("integration.load_shape");
            if (_editorContextIssue.Length > 0) throw new InvalidOperationException(_editorContextIssue);
            var draft=CaptureDraft();
            var compiled=draft.Compile(_runtime,out _);
            if(compiled.Status==QueryNormalizationStatus.Impossible || SearchFeasibilityAnalyzer.Analyze(compiled).IsImpossible) throw new InvalidOperationException("integration.impossible");
            intentValidated = true;
            UpdateProbabilityPanel(draft,JsonSerializer.Serialize(draft.WithoutCapturedAuthority()));
            _persistence.InitializeWorkbenchCursor();
            _persistence.EnsureEnvironment(SearchEnvironmentSignatureBuilder.Capture(_runtime));
            ulong startOrdinal = _persistence.ResolveSearchStartOrdinal(compiled.SemanticFingerprint);
            var execution=ExactSearchExecutionRequestFactory.Compile(compiled,new SearchRunOptions(Beta110SeedCodec.FormatOrdinal(startOrdinal),
                long.MaxValue,_target,SearchWorkers, SkipExactValidation: _persistence.Preferences.SkipExactValidation)
                { Resources = SearchResources, Optimizations = SearchOptimizations });
            if(!execution.Success || execution.Plan is null) throw new InvalidOperationException(execution.Issue);
            var request = ExactWorldProjectionPolicy.ApplyOrdinarySearchPolicy(execution.Plan);
            _persistence.SaveWorkbench(draft);
            var session=FamilyExecutionCoordinator.Start(request,queueCapacity:64,
                gpuAvailable:SearchCpuOnly?false:null);
            _session=session; _lastDiagnostics=null; _activeFingerprint=compiled.SemanticFingerprint; _resultPlan=request;
            _completionFlushRetryAfter = 0; _completionSaveFailed = false;
            _persistence.BeginResultReception(session, Math.Clamp(_target, 1, SearchResultReceiver.MaximumUiCapacity));
            _pauseRequested = false; _checkpointFingerprint = compiled.SemanticFingerprint;
            _persistence.BeginSearchCheckpoint(_activeFingerprint, startOrdinal);
            _persistence.TrackSearch(session);
            _probabilityPreview.Invalidate();
            _probabilityPending = false;

            BeginRuntimeProgress(session.EtaProjection, session.GetProgress());
            _results.Clear(); InstallResultContext(draft);
            _persistence.ResetWorkspaceForSuccessfulStart(_activeFingerprint, draft, _resultSeedContext, request.RunOptions.SkipExactValidation);
            _observedResultBatchId = _persistence.Workspace.ResultBatchId;
            _observedResultsRevision = _persistence.ResultsRevision;
            _showResults=true;
            RenderResults(); LockForRun(true);
            RuntimeLog.Info("workbenchProductionStart=true;terminal="+(request.RunOptions.SkipExactValidation?"UnverifiedCandidates":"ProductionExact")+";fingerprint="+_activeFingerprint);
        }
        catch(Exception ex)
        {
            // Start may have created a session before a later binding/UI step
            // failed. Keep it owned and polled until cancellation/cleanup ends.
            if (_session is { } pending)
            {
                _persistence.TrackSearch(pending);
                _pauseRequested = true;
                pending.Cancel();
                UpdateSearchAction();
            }
            else
            {
                _checkpointFingerprint = "";
                if (!intentValidated) _draftCompileIssue = ex.Message;
                UpdateSearchAction();
            }
            if(_status is not null) _status.Text=Explain(ex);
        }
    }

    private void SetCheckpointFingerprint(string fingerprint)
    {
        _checkpointFingerprint = fingerprint;
        UpdateSearchAction();
    }

    private void UpdateSearchAction()
    {
        if (_start is null || !GodotObject.IsInstanceValid(_start)) return;
        bool running = _session is not null;
        bool resume = !running && _persistence.CanResumeSearch(_checkpointFingerprint);
        _start.Text = _text.Get(running ? _pauseRequested ? "integration.pausing" : "integration.pause" :
            resume ? "integration.resume" : "query.action.start");
        _start.TooltipText = _text.Get(running ? "integration.pause_hint" : "integration.resume_hint");
        _start.Disabled = running ? _pauseRequested : _loadFailed || _presetBlocked.Count > 0 ||
            _manualValidation is not null || _persistence.HasSearchInFlight || _draftCompileIssue.Length > 0 ||
            _editorContextIssue.Length > 0 || _lastIssue.Length > 0;
        if (_checkpointLabel is not null && GodotObject.IsInstanceValid(_checkpointLabel))
        {
            _checkpointLabel.Text = running || resume
                ? _text.Format("integration.resume_next", _persistence.CurrentNextCursorSeed)
                : _checkpointFingerprint.Length > 0 && _persistence.Cursor.Initialized
                    ? _text.Format("integration.start_at", _persistence.CurrentPersistentOriginSeed) : "";
            _checkpointLabel.TooltipText = _text.Get("integration.resume_hint");
        }
    }

    private void PauseSearch()
    {
        if (_session is not { } session || _pauseRequested) return;
        _pauseRequested = true;
        session.Cancel();
        // Persist only the session's committed boundary. Submitted/in-flight
        // roots are deliberately not used as a checkpoint.
        _persistence.ObserveSessionCheckpoint(session, _activeFingerprint);
        _persistence.SaveResultProgress(session.GetProgress());
        _persistence.FlushAll();
        UpdateSearchAction();
    }

    private void LockForRun(bool running)
    {
        if (_currentDomain is not null) _currentDomain.Visible = !_showResults;
        if (!running)
        {
            foreach(var (button,disabled) in _runBlocked) if(GodotObject.IsInstanceValid(button)) button.Disabled=disabled;
            _runBlocked.Clear();
        }
        else
        {
            foreach(var child in FindChildren("*", "BaseButton", true, false))
                if(child is BaseButton button && button != _start && button != _savedResultsEntry && !(_runtimeSurface?.IsAncestorOf(button) ?? false) && !(_familyRows?.IsAncestorOf(button) ?? false))
                { _runBlocked.TryAdd(button,button.Disabled); button.Disabled=true; }
        }
        if(_targetCount is not null) _targetCount.Editable=!running;
        if(_planSelector is not null) _planSelector.Disabled=running;
        if(_start is not null && running) _start.Disabled=false;
        UpdateSearchAction();
        if(_runtimeSurface is not null) _runtimeSurface.Visible=_showResults;
        if(_showResults) foreach(var editor in new Control[] {_neowEditor,_combatEditor,_relicEditor,_ancientEditor,_eventEditor,_actInformationEditor,_transformationEditor,_shopEditor}) editor.Hide();
        UpdateResultNavigation();
    }

    private void UpdateResultNavigation()
    {
        UpdateConditionActions();
        if (_savedResultsEntry is not null && GodotObject.IsInstanceValid(_savedResultsEntry))
        {
            int count = ResultCount;
            if (!_showResults && _session is null)
            {
                try { count = _persistence.ResultCountForQuery(CaptureDraft()); }
                catch { count = 0; }
            }
            _savedResultsEntry.Text = _text.Format("workflow.results.view_last", count);
            _savedResultsEntry.TooltipText = _text.Get("workflow.results.saved_hint");
        }
        ResultsChanged?.Invoke();
        if (_familyHeading is not null && GodotObject.IsInstanceValid(_familyHeading))
            _familyHeading.Text = _text.Get(_showResults && _session is null ? "workflow.conditions.editing" : "workflow.conditions");
    }

    internal void ShowLastResults()
    {
        if (_session is null)
        {
            try
            {
                _persistence.EnsureEnvironment(SearchEnvironmentSignatureBuilder.Capture(_runtime));
                _persistence.ActivateQueryResults(CaptureDraft());
            }
            catch (Exception ex) { ReceiptText(Explain(ex)); return; }
        }
        SyncSavedResults();
        _showResults = true;
        _expectationContext = "";
        RenderResults();
        LockForRun(_session is not null);
    }
    private void RenderResults()
    {
        if(_resultRows is null) return;
        UpdateSearchAction();
        foreach(var child in _resultRows.GetChildren()) { _resultRows.RemoveChild(child); child.QueueFree(); }
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        bool unverifiedMode = _persistence.Workspace.ResultsSkipExactValidation || _persistence.DisplayResults.Any(r => r.IsUnverified);
        var title = _p.Label(unverifiedMode ? _text.Format("workflow.candidates_count", ResultCount) : _text.Format("workflow.results_count", ResultCount), 22);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        if(_session is null)
        {
            var back=_p.Button(_text.Get("integration.edit"));
            back.CustomMinimumSize = new Vector2(136, 34);
            back.AddThemeFontSizeOverride("font_size", 15);
            back.Pressed+=()=> { _showResults=false; Refresh(_language,_text); };
            header.AddChild(back);
        }
        _resultRows.AddChild(header);
        if (_session is null)
        {
            var retained = _p.Label(_text.Get("workflow.results.retained"), 15, true);
            retained.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _resultRows.AddChild(retained);
        }
        if (unverifiedMode)
        {
            var warning = _p.Label(_text.Get("workflow.candidates_warning"), 16, true);
            warning.AutowrapMode = TextServer.AutowrapMode.WordSmart; _resultRows.AddChild(warning);
        }
        if (ResultCount == 0)
        {
            var empty = _p.Label(_text.Get(_session is null ? "workflow.results.empty" : "workflow.results.waiting"), 17, true);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart; _resultRows.AddChild(empty);
        }
        foreach(var saved in _persistence.DisplayResults)
        {
            var result = _results.FirstOrDefault(candidate => candidate.Seed == saved.Seed && candidate.IsUnverified == saved.IsUnverified);
            var resultPartyDraft = _runningPartyDraft;
            var resultContext = _resultSeedContext;
            string seed=saved.Seed;
            var panel = new PanelContainer { CustomMinimumSize = new Vector2(0, 42) };
            var panelStyle = _p.Box(_p.Surface);
            panelStyle.ContentMarginLeft = panelStyle.ContentMarginRight = 12;
            panelStyle.ContentMarginTop = panelStyle.ContentMarginBottom = 3;
            panel.AddThemeStyleboxOverride("panel", panelStyle);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var seedLabel = _p.Label(seed + (unverifiedMode ? " · " + _text.Get(saved.IsUnverified ? "workflow.unverified" : "workflow.verified") : ""), 17);
            seedLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            seedLabel.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(seedLabel);
            var copy = _p.Button(_text.Get("integration.copy"));
            copy.CustomMinimumSize = new Vector2(72, 32);
            copy.AddThemeFontSizeOverride("font_size", 14);
            copy.Pressed += () => { DisplayServer.ClipboardSet(seed); if(_status is not null) _status.Text=_text.Get("integration.copied"); };
            row.AddChild(copy);
            row.AddChild(result is null ? FavoriteSavedResultButton(saved) : FavoriteResultButton(result, resultContext));
            var analyze = _p.Button(_text.Get("integration.analyze"));
            analyze.CustomMinimumSize = new Vector2(72, 32);
            analyze.AddThemeFontSizeOverride("font_size", 14);
            analyze.Disabled = result is null && saved.Context is null;
            if (analyze.Disabled) analyze.TooltipText = _text.Get("workflow.results.missing_context");
            analyze.Pressed += () =>
            {
                if (result is null) OpenSavedResultRequested?.Invoke(saved);
                else if (resultPartyDraft is { } partyDraft)
                    OpenPartyInformation?.Invoke(result, partyDraft);
                else OpenSeedInformation?.Invoke(result);
            };
            row.AddChild(analyze);
            if (saved.IsUnverified)
            {
                var validate = _p.Button(_text.Get("workflow.validate"));
                validate.CustomMinimumSize = new(72, 32); validate.AddThemeFontSizeOverride("font_size", 14);
                validate.Disabled = result is null || _resultPlan is null || _session is not null || _manualValidation is not null;
                if (result is null) validate.TooltipText = _text.Get("workflow.results.validation_unavailable");
                validate.Pressed += () => { if (result is not null) ValidateCandidate(result); }; row.AddChild(validate);
            }
            panel.AddChild(row);
            _resultRows.AddChild(panel);
        }
        if(_runtimeSurface is not null) _runtimeSurface.Visible=_showResults;
        UpdateRuntimeStats();
        UpdateResultNavigation();
        _resultRefreshPending = false;
        _resultRefreshRemaining = .1;
    }
    private void PollProduction(double delta)
    {
        PollCandidateValidation();
        _resultRefreshRemaining = Math.Max(0, _resultRefreshRemaining - Math.Max(0, delta));
        if (_receiptRemaining>0 && _session is null)
        { _receiptRemaining-=delta; if(_receiptRemaining<=0 && _status is not null && string.IsNullOrEmpty(_lastIssue)) _status.Text=""; }
        if(_session is { } session)
        {
            bool changed = _persistence.DrainReceivedResults(session, _activeFingerprint, _results) > 0;
            bool forceRender = false;
            var progress=session.GetProgress();
            if(_status is not null) _status.Text = _completionSaveFailed ? _text.Get("integration.checkpoint_save_failed") :
                _pauseRequested ? _text.Get("integration.pausing") : "";
            _persistence.ObserveSessionCheckpoint(session, _activeFingerprint);
            UpdateSearchAction();
            if(_persistence.SessionResultsFullyDrained(session) && System.Environment.TickCount64 >= _completionFlushRetryAfter)
            {
                // Both the source and its single receiver have joined, and all
                // buffered candidates were appended on this main thread.
                _persistence.DrainReceivedResults(session, _activeFingerprint, _results);
                progress=session.GetProgress();
                _lastDiagnostics=session.GetDiagnosticSummary();
                string receiveIssue = _persistence.ResultReceptionFailure(session)?.Message ?? "";
                if (!_persistence.TryCompleteResultReception(session, _activeFingerprint))
                {
                    // Keep ownership and input locking until both results and
                    // resume cursor are durable; retry without per-frame I/O.
                    _completionSaveFailed = true; _pauseRequested = true;
                    _completionFlushRetryAfter = System.Environment.TickCount64 + 600;
                    if (_status is not null) _status.Text = _text.Get("integration.checkpoint_save_failed");
                    UpdateSearchAction();
                }
                else
                {
                    _session=null; _=session.DisposeAsync();
                    _pauseRequested = false; _completionSaveFailed = false;
                    if(_status is not null) _status.Text=(receiveIssue.Length > 0 ? _text.Get("integration.failed") : progress.State == SearchRunState.Cancelled
                        ? _text.Format("integration.paused", _persistence.CurrentNextCursorSeed)
                        : _text.Get(progress.State==SearchRunState.Faulted?"integration.failed":"integration.completed"))+
                        (string.IsNullOrEmpty(progress.FailureCode)?"":"\n"+progress.FailureCode) + (receiveIssue.Length == 0 ? "" : "\n" + receiveIssue);
                    _receiptRemaining = progress.State == SearchRunState.Faulted || receiveIssue.Length > 0 ? 0 : 5;
                    LockForRun(false); changed=true; forceRender=true;
                }
            }
            ObserveRuntimeProgress(progress); UpdateRuntimeStats();
            _resultRefreshPending |= changed;
            if(forceRender || (_resultRefreshPending && _resultRefreshRemaining <= 0)) RenderResults();
        }
        _saveTick+=delta;
        if(_saveTick<.75 || !_restored) return;
        _saveTick=0;
        if (_session is not null && _lastProgress is { } currentProgress) _persistence.SaveResultProgress(currentProgress);
        else if (SyncSavedResults() && _showResults) RenderResults();
        if(_session is null && !_showResults) UpdateDraftStatus();
        PollProbabilityPreview();
        var flags=CaptureUiPreferences(); string prefs=JsonSerializer.Serialize(flags)+_selectedDomain;
        if(prefs!=_lastPreferences) { _persistence.SaveWorkbenchPreferences(flags,_selectedDomain); _lastPreferences=prefs; }
    }
    internal void CloseSearch()
    {
        _probabilityPreview.Invalidate(); _probabilityPending = false; _analysisKey = "";
        CloseAllPresetModals();
        if(_session is { } session)
        {
            PauseSearch();
            _persistence.SaveResultProgress(session.GetProgress());
            // Keep polling the cancelled session while the shell closes, until the existing
            // terminal drain has delivered results and its safe cursor.
        }
        else if (!_loadFailed) SaveDraft();
        _persistence.FlushAll();
    }

    private void Receipt(string key)
    { if(_status is not null) _status.Text=_text.Get(key); _receiptRemaining=5; }
}
