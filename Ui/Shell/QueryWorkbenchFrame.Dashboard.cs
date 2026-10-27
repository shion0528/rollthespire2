using Godot;
namespace RolltheSpire2.Ui.Shell;

internal sealed partial class QueryWorkbenchFrame
{
    private sealed record FamilyEntry(string Id,string Title,double? Probability,string Speed,string Detail);
    private readonly List<FamilyEntry> _familyEntries=[];
    private readonly HashSet<string> _expandedFamilies=[];
    private VBoxContainer? _familyRows;
    private string FamilyTitle(string id)=>_text.Get("workflow.family."+id.ToLowerInvariant());
    private static string DashboardRarity(double? probability)=>Rarity(probability);
    private readonly Label?[] _expectationValues = new Label?[3];
    private Label? _expectationNote, _familyHeading;
    private string _expectationContext = "";

    private void BuildSearchSidebar()
    {
        var sidebar = new VBoxContainer { Name = "SearchInformationRail", Position = new(RightRailLeft + 12, 0),
            Size = new(RightRailWidth - 24, CanvasHeight) };
        sidebar.AddThemeConstantOverride("separation", 14);
        AddChild(sidebar);
        var controls = new HBoxContainer(); controls.AddThemeConstantOverride("separation", 12); sidebar.AddChild(controls);
        VBoxContainer Field(string title)
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 6); controls.AddChild(column);
            column.AddChild(_p.Label(title, 15, true)); return column;
        }
        var target = Field(_persistence.Preferences.SkipExactValidation ? _text.Get("workflow.candidate_target") : (_language == "zh" ? "结果数量" : "Results"));
        _targetCount = new SpinBox { MinValue = 1, MaxValue = 1000, Value = _target,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 38) };
        _targetCount.AddThemeFontSizeOverride("font_size", 17);
        _targetCount.ValueChanged += v => { _target = (int)v; _analysisKey = ""; }; target.AddChild(_targetCount);
        var plan = Field(_text.Get("workflow.plan")); plan.SizeFlagsStretchRatio = 1.4f;
        _planSelector = ContextOptions(0, 0, 144); _planSelector.Reparent(plan, false);
        _planSelector.SizeFlagsHorizontal = SizeFlags.ExpandFill; _planSelector.CustomMinimumSize = new(0, 38);
        _planSelector.AddItem(_text.Get("workflow.plan.auto")); _planSelector.AddItem(_text.Get("workflow.plan.cpu"));
        _planSelector.Select(_persistence.Preferences.SearchMode == "CPU" ? 1 : 0);
        _planSelector.ItemSelected += index => { _persistence.SetWorkbenchSearchMode(index == 1 ? "CPU" : "Auto"); _analysisKey = ""; };
        _start = _p.Button(_text.Get("query.action.start"), primary: true); _start.CustomMinimumSize = new(0, 46);
        _start.Pressed += () => { if (_session is not null) PauseSearch(); else StartSearch(); }; sidebar.AddChild(_start);
        _checkpointLabel = _p.Label("", 14, true);
        _checkpointLabel.Name = "SearchCheckpoint";
        _checkpointLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        sidebar.AddChild(_checkpointLabel);
        _savedResultsEntry = _p.CompactButton("", 44, 18, selected: true);
        _savedResultsEntry.Name = "ViewLastSearchResults";
        _savedResultsEntry.Pressed += ShowLastResults;
        sidebar.AddChild(_savedResultsEntry);
        if (_persistence.Preferences.SkipExactValidation)
        {
            var notice = _p.Label(_text.Get("workflow.candidate_mode"), 15, true);
            notice.AutowrapMode = TextServer.AutowrapMode.WordSmart; sidebar.AddChild(notice);
        }

        var scroll = new ScrollContainer { Name = "SearchInformationScroll", SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        sidebar.AddChild(scroll);
        var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 16); scroll.AddChild(content);
        content.AddChild(new RolltheSpire2.Ui.Components.GpuInitializationPanel(_language, () => HasActiveSearch, compact: true));
        var statistics = new PanelContainer(); statistics.AddThemeStyleboxOverride("panel", _p.Box(_p.Surface)); content.AddChild(statistics);
        var values = new VBoxContainer(); values.AddThemeConstantOverride("separation", 10); statistics.AddChild(values);
        string[] keys = ["workflow.total_rarity", "workflow.expected_speed", "workflow.eta"];
        for (int i = 0; i < keys.Length; i++)
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); values.AddChild(row);
            var caption = _p.Label(_text.Get(keys[i]), 14, true);
            caption.SizeFlagsHorizontal = SizeFlags.ExpandFill; caption.VerticalAlignment = VerticalAlignment.Center;
            caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            caption.TooltipText = caption.Text; caption.MouseFilter = MouseFilterEnum.Pass; row.AddChild(caption);
            var value = _p.Label("—", 18); value.CustomMinimumSize = new(92, 28);
            value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
            value.ClipText = true; value.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            value.MouseFilter = MouseFilterEnum.Pass; row.AddChild(value); _expectationValues[i] = value;
        }
        _expectationNote = _p.Label("", 14, true); _expectationNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _expectationNote.SizeFlagsHorizontal = SizeFlags.ExpandFill; content.AddChild(_expectationNote);
        _familyHeading = _p.Label(_text.Get("workflow.conditions"), 17);
        content.AddChild(_familyHeading);
        _familyRows=new VBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill};
        _familyRows.AddThemeConstantOverride("separation",10);content.AddChild(_familyRows);
        _summary=_p.Label("",15,true);_summary.Hide();AddChild(_summary);
        PresentExpectations(null, null, null);
        RenderFamilyDashboard();
        UpdateResultNavigation();
    }

    private void PresentExpectations(double? time, double? probability, double? rate)
    {
        if (_showResults ? _resultPlan?.RunOptions.SkipExactValidation == true : _persistence.Preferences.SkipExactValidation)
        { time = null; rate = null; }
        string[] values = [probability is null ? (_language == "zh" ? "未知" : "Unknown") : Rarity(probability), Rate(rate), Duration(time)];
        for (int i = 0; i < values.Length; i++)
            if (_expectationValues[i] is { } label) { label.Text = values[i]; label.TooltipText = values[i]; }
        if (_expectationNote is not null)
        { _expectationNote.Text = _expectationContext; _expectationNote.Visible = _expectationContext.Length > 0; }
    }
    private void RenderFamilyDashboard()
    {
        if(_familyRows is null) return;
        foreach(var child in _familyRows.GetChildren()){_familyRows.RemoveChild(child);child.QueueFree();}
        if(_draftCompileIssue.Length>0) {
            var issue=_p.Label(_draftCompileIssue,14,true);issue.AutowrapMode=TextServer.AutowrapMode.WordSmart;
            issue.SizeFlagsHorizontal=SizeFlags.ExpandFill;_familyRows.AddChild(issue);
        }
        if (_familyEntries.Count == 0 && _draftCompileIssue.Length == 0)
        {
            var empty = _p.Label(_probabilityPending
                ? (_language == "zh" ? "正在更新条件估算…" : "Updating condition estimates…")
                : _text.Get("workflow.conditions.empty"), 15, true);
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart; _familyRows.AddChild(empty);
        }
        foreach(var entry in _familyEntries.OrderBy(e=>"NACERTMWS".IndexOf(e.Id[0])))
        {
            bool open=_expandedFamilies.Contains(entry.Id);
            var group = new VBoxContainer(); group.AddThemeConstantOverride("separation", 6); _familyRows.AddChild(group);
            var button=_p.Button("");
            button.CustomMinimumSize=new(0,38);group.AddChild(button);
            var header=new HBoxContainer {MouseFilter=MouseFilterEnum.Ignore};
            button.AddChild(header);header.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            header.OffsetLeft=8;header.OffsetRight=-8;
            header.AddThemeConstantOverride("separation",8);
            var title=_p.Label((open?"⌄ ":"› ")+FamilyTitle(entry.Id),16);
            title.SizeFlagsHorizontal=SizeFlags.ExpandFill;title.ClipText=true;title.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;
            button.TooltipText = FamilyTitle(entry.Id);
            title.VerticalAlignment=VerticalAlignment.Center;title.MouseFilter=MouseFilterEnum.Ignore;header.AddChild(title);
            bool undisplayed = _multiplayer && entry.Probability is null && entry.Id is "N" or "R" or "S" or "E" or "A" or "W";
            var rarity=_p.Label(undisplayed ? (_language == "zh" ? "未单列" : "Not itemized") : entry.Probability is null
                ? (_language == "zh" ? "未知" : "Unknown") : DashboardRarity(entry.Probability),16);
            rarity.VerticalAlignment=VerticalAlignment.Center;rarity.HorizontalAlignment=HorizontalAlignment.Right;
            rarity.CustomMinimumSize = new(90, 0); rarity.ClipText = true;
            rarity.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            rarity.MouseFilter=MouseFilterEnum.Ignore;header.AddChild(rarity);
            var summary=_p.Label(_text.Get("workflow.expected_speed")+" · "+entry.Speed,14,true);
            summary.AutowrapMode=TextServer.AutowrapMode.WordSmart;summary.SizeFlagsHorizontal=SizeFlags.ExpandFill;summary.Visible=open;group.AddChild(summary);
            string explanation = undisplayed ? (_language == "zh" ? "此处未单列概率；整桌估算见上方。\n" : "No separate probability here; see party estimate above.\n") : "";
            var detail=_p.Label(explanation + entry.Detail,14,true);detail.AutowrapMode=TextServer.AutowrapMode.WordSmart;
            detail.SizeFlagsHorizontal=SizeFlags.ExpandFill;detail.Visible=open;group.AddChild(detail);
            button.Pressed+=()=> {if(!_expandedFamilies.Add(entry.Id))_expandedFamilies.Remove(entry.Id);detail.Visible=_expandedFamilies.Contains(entry.Id);summary.Visible=detail.Visible;title.Text=(detail.Visible?"⌄ ":"› ")+FamilyTitle(entry.Id);};
        }
    }
}
