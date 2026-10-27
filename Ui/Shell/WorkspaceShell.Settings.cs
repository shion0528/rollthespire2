using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Presentation.Localization;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Ui.Shell;

internal sealed partial class WorkspaceShell
{
    private Control _settingsPage = null!;
    private Workspace _settingsReturn = Workspace.Search;
    private readonly OptionButton _language = new() { Name = "SettingsLanguage", CustomMinimumSize = new(360, 44), FitToLongestItem = false };
    private readonly CheckButton _runPredictionEntry = new() { Name = "SettingsRunPredictionEntry", CustomMinimumSize = new(360, 44),
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
    private readonly LineEdit _originInput = new() { Name = "SettingsOrigin", MaxLength = Beta110Profile.Instance.SeedLength,
        CustomMinimumSize = new(340, 44), PlaceholderText = "000000000000" };
    private readonly LineEdit _logPath = new() { Name = "SettingsLogPath", Editable = false,
        SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 40), ExpandToTextLength = false };
    private Label _originStatus = null!, _originFeedback = null!, _logFeedback = null!;
    private Button _originApply = null!, _originRandom = null!, _settingsBack = null!;
    private readonly List<(Label Label, string Key)> _settingsLabels = [];
    private readonly List<(Button Button, string Key)> _settingsButtons = [];
    private IUiTextProvider? _settingsText;
    private string _originReceiptKey = "", _logReceiptKey = "";
    private bool _bindingSettings;
    private readonly CheckButton _skipExact = new() { Name = "SettingsSkipExact", CustomMinimumSize = new(360, 44), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
    private readonly SpinBox _queryHistoryLimit = new() { Name = "SettingsQueryHistoryLimit", MinValue = 1, MaxValue = 300, Step = 1, CustomMinimumSize = new(180, 42) };
    private readonly SpinBox _cpuLoadPercent = new() { Name = "SettingsCpuLoadPercent", MinValue = 1, MaxValue = 100, Step = 1, Suffix = "%", CustomMinimumSize = new(180, 42) };
    private readonly SpinBox _gpuLoadPercent = new() { Name = "SettingsGpuLoadPercent", MinValue = 0, MaxValue = 100, Step = 1, Suffix = "%", CustomMinimumSize = new(180, 42) };
    private Button _maximumPerformance = null!;
    private Label _resourceStatus = null!;
    private bool SettingsSearchBusy => _references?.HasActiveSearch == true || _persistence?.HasSearchInFlight == true;

    private void BuildSettings()
    {
        _settingsPage = new Control { Name = "SettingsPage", Visible = false };
        _content.AddChild(_settingsPage); _settingsPage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 24);
        _settingsPage.AddChild(body); body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var heading = new HBoxContainer(); body.AddChild(heading);
        var title = SettingsLabel("shell.settings", 30); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; heading.AddChild(title);
        _settingsBack = SettingsButton("settings.back", CloseSettings); heading.AddChild(_settingsBack);
        body.AddChild(SettingsLabel("settings.subtitle", 18, true));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(scroll);
        var sections = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sections.AddThemeConstantOverride("separation", 20); scroll.AddChild(sections);

        VBoxContainer Section(string titleKey, string helpKey)
        {
            var panel = new PanelContainer();
            var style = _palette.Box(_palette.Surface);
            style.ContentMarginLeft = style.ContentMarginRight = 24;
            style.ContentMarginTop = style.ContentMarginBottom = 22;
            panel.AddThemeStyleboxOverride("panel", style); sections.AddChild(panel);
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 32);
            row.AddThemeConstantOverride("v_separation", 14); panel.AddChild(row);
            var intro = new VBoxContainer { CustomMinimumSize = new(320, 0) }; intro.AddThemeConstantOverride("separation", 10); row.AddChild(intro);
            intro.AddChild(SettingsLabel(titleKey, 23)); intro.AddChild(SettingsLabel(helpKey, 17, true, wrap: true));
            var controls = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; controls.AddThemeConstantOverride("separation", 10); row.AddChild(controls);
            return controls;
        }

        var language = Section("settings.language", "settings.language.help");
        for (int i = 0; i < 3; i++) _language.AddItem("");
        _language.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        foreach (var (state, fill) in new[] { ("normal", _palette.Canvas), ("hover", _palette.Hover), ("pressed", _palette.Line), ("disabled", _palette.Canvas) })
            _language.AddThemeStyleboxOverride(state, _palette.Box(fill, _palette.Line, 1));
        _language.AddThemeStyleboxOverride("focus", _palette.FocusRing());
        _language.AddThemeFontSizeOverride("font_size", 20); language.AddChild(_language);
        _language.ItemSelected += index =>
        {
            _persistence!.SetLanguageOverride(index == 1 ? "zh" : index == 2 ? "en" : "");
            _persistence.FlushPreferences();
            _languageCode = ""; RefreshLanguage();
        };

        BuildAppearanceSettings(Section("settings.appearance.title", "settings.appearance.help"));

        var resources = Section("settings.resources.title", "settings.resources.help");
        var resourceFields = new HFlowContainer();
        resourceFields.AddThemeConstantOverride("h_separation", 24);
        resourceFields.AddThemeConstantOverride("v_separation", 12); resources.AddChild(resourceFields);
        void ResourceField(string label, SpinBox input)
        {
            var field = new VBoxContainer(); field.AddThemeConstantOverride("separation", 6);
            field.AddChild(SettingsLabel(label, 18));
            input.AddThemeFontSizeOverride("font_size", 18); field.AddChild(input); resourceFields.AddChild(field);
        }
        ResourceField("settings.resources.cpu", _cpuLoadPercent);
        ResourceField("settings.resources.gpu", _gpuLoadPercent);
        _cpuLoadPercent.ValueChanged += _ => SaveSearchResources();
        _gpuLoadPercent.ValueChanged += _ => SaveSearchResources();
        _maximumPerformance = SettingsButton("settings.resources.maximum", () =>
        {
            if (SettingsSearchBusy) { RefreshSettings(); return; }
            _persistence!.SetSearchResources(new(100, 100));
            _references?.SyncSearchPreferences(); RefreshSettings();
        }, primary: true);
        _maximumPerformance.Name = "SettingsMaximumPerformance"; resources.AddChild(_maximumPerformance);
        resources.AddChild(SettingsLabel("settings.resources.maximum_help", 17, true, wrap: true));
        _resourceStatus = _palette.Label("", 17, true);
        _resourceStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; resources.AddChild(_resourceStatus);

        BuildSearchOptimizationSettings(Section("settings.optimizations.title", "settings.optimizations.help"));

        var prediction = Section("settings.run_prediction.title", "settings.run_prediction.help");
        _runPredictionEntry.AddThemeFontSizeOverride("font_size", 20);
        _runPredictionEntry.AddThemeColorOverride("font_color", _palette.Color(_palette.Text));
        prediction.AddChild(_runPredictionEntry);
        _runPredictionEntry.Toggled += enabled =>
        {
            _persistence!.SetShowInRunPredictionEntry(enabled);
            InRunPredictionLauncher.ApplyPreference(GetTree(), enabled);
        };

        var experimental = Section("settings.candidates.title", "settings.candidates.help");
        _skipExact.AddThemeFontSizeOverride("font_size", 20); experimental.AddChild(_skipExact);
        _skipExact.Toggled += enabled =>
        {
            if (SettingsSearchBusy) { RefreshSettings(); return; }
            _persistence!.SetSkipExactValidation(enabled);
            _references?.SyncSearchPreferences();
        };

        var history = Section("settings.history.title", "settings.history.help");
        history.AddChild(_queryHistoryLimit);
        _queryHistoryLimit.ValueChanged += value =>
        {
            if (_bindingSettings) return;
            _persistence!.SetQueryHistoryLimit((int)value);
        };
        var origin = Section("settings.origin.title", "settings.origin.help");
        var originActions = new HFlowContainer(); originActions.AddThemeConstantOverride("h_separation", 12);
        originActions.AddThemeConstantOverride("v_separation", 8); origin.AddChild(originActions);
        StyleSettingsInput(_originInput); originActions.AddChild(_originInput);
        _originApply = SettingsButton("settings.origin.apply", () => ApplySettingsOrigin(false), true);
        _originApply.Name = "SettingsOriginApply"; originActions.AddChild(_originApply);
        _originRandom = SettingsButton("settings.origin.random", () => ApplySettingsOrigin(true));
        _originRandom.Name = "SettingsOriginRandom"; originActions.AddChild(_originRandom);
        _originStatus = _palette.Label("", 18); origin.AddChild(_originStatus);
        _originFeedback = _palette.Label("", 17, true); _originFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart; origin.AddChild(_originFeedback);
        _originInput.TextChanged += _ => { if (!_bindingSettings) { _originReceiptKey = ""; RefreshSettings(); } };
        _originInput.TextSubmitted += _ => ApplySettingsOrigin(false);

        var logs = Section("settings.logs.title", "settings.logs.help");
        StyleSettingsInput(_logPath); logs.AddChild(_logPath);
        var logActions = new HFlowContainer(); logActions.AddThemeConstantOverride("h_separation", 12); logs.AddChild(logActions);
        logActions.AddChild(SettingsButton("settings.logs.open", OpenSettingsLogDirectory));
        logActions.AddChild(SettingsButton("settings.logs.copy", () =>
        {
            string path = RuntimeLog.CurrentLogPath;
            if (string.IsNullOrWhiteSpace(path)) path = RuntimeLog.LogDirectory;
            DisplayServer.ClipboardSet(path); _logReceiptKey = "settings.logs.copied"; RefreshSettings();
        }));
        _logFeedback = _palette.Label("", 17, true); _logFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart; logs.AddChild(_logFeedback);
    }

    private Label SettingsLabel(string key, int size, bool secondary = false, bool wrap = false)
    {
        var label = _palette.Label("", size, secondary);
        if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _settingsLabels.Add((label, key)); return label;
    }

    private Button SettingsButton(string key, Action action, bool primary = false)
    {
        var button = _palette.Button("", primary); button.CustomMinimumSize = new(112, 44);
        button.AddThemeFontSizeOverride("font_size", 18); button.Pressed += action;
        _settingsButtons.Add((button, key)); return button;
    }

    private void StyleSettingsInput(LineEdit input)
    {
        input.AddThemeFontSizeOverride("font_size", 18);
        input.AddThemeColorOverride("font_color", _palette.Color(_palette.Text));
        input.AddThemeColorOverride("font_uneditable_color", _palette.Color(_palette.Secondary));
        input.AddThemeStyleboxOverride("normal", _palette.Box(_palette.Canvas, _palette.Line, 1));
        input.AddThemeStyleboxOverride("read_only", _palette.Box(_palette.Canvas));
        input.AddThemeStyleboxOverride("focus", _palette.FocusRing());
    }

    private void OpenSettings()
    {
        if (_workspace != Workspace.Settings) _settingsReturn = _workspace;
        SelectTask(Workspace.Settings); _language.GrabFocus();
    }

    private void CloseSettings()
    {
        if (_workspace != Workspace.Settings) return;
        SelectTask(_settingsReturn); _settings.GrabFocus();
    }

    private void LocalizeSettings(IUiTextProvider text)
    {
        _settingsText = text;
        RefreshAppearanceSettings();
        foreach (var (label, key) in _settingsLabels) label.Text = text.Get(key);
        foreach (var (button, key) in _settingsButtons) button.Text = text.Get(key);
        _language.SetItemText(0, text.Get("settings.language.follow_game"));
        _language.SetItemText(1, text.Get("settings.language.zh")); _language.SetItemText(2, text.Get("settings.language.en"));
        _language.Select(_persistence!.Preferences.LanguageOverride switch { "zh" => 1, "en" => 2, _ => 0 });
        _runPredictionEntry.Text = text.Get("settings.run_prediction.show");
        _skipExact.Text = text.Get("settings.candidates.enable");
        LocalizeSearchOptimizationSettings(text);
        RefreshSettings();
    }

    private void RefreshSettings(bool resetInput = false)
    {
        if (_settingsText is not { } text || _persistence is not { } persistence) return;
        _runPredictionEntry.SetPressedNoSignal(persistence.Preferences.ShowInRunPredictionEntry);
        _skipExact.SetPressedNoSignal(persistence.Preferences.SkipExactValidation);
        _skipExact.Disabled = SettingsSearchBusy;
        _queryHistoryLimit.SetValueNoSignal(persistence.Preferences.QueryHistoryLimit);
        var resources = persistence.SearchResources;
        _cpuLoadPercent.SetValueNoSignal(resources.CpuPercent);
        _gpuLoadPercent.SetValueNoSignal(resources.GpuPercent);
        _cpuLoadPercent.Editable = _gpuLoadPercent.Editable = !SettingsSearchBusy;
        _maximumPerformance.Disabled = SettingsSearchBusy;
        _resourceStatus.Text = SettingsSearchBusy ? text.Get("settings.resources.busy")
            : text.Format(resources.GpuPercent == 0 ? "settings.resources.cpu_only_status" : "settings.resources.status",
                resources.ResolveCpuWorkers(System.Environment.ProcessorCount));
        RefreshSearchOptimizationSettings();
        if (resetInput)
        {
            _bindingSettings = true;
            _originInput.Text = persistence.Cursor.Initialized ? persistence.CurrentPersistentOriginSeed : "";
            _bindingSettings = false; _originReceiptKey = _logReceiptKey = "";
        }
        bool busy = SettingsSearchBusy;
        bool valid = Beta110Profile.Instance.TryCanonicalizeSeed(_originInput.Text, out _, out _);
        _originInput.Editable = !busy; _originApply.Disabled = busy || !valid; _originRandom.Disabled = busy;
        _originStatus.Text = persistence.Cursor.Initialized
            ? text.Format("settings.origin.status", persistence.CurrentPersistentOriginSeed, persistence.CurrentNextCursorSeed)
            : text.Get("settings.origin.unset");
        _originFeedback.Text = busy ? text.Get("settings.origin.busy")
            : _originReceiptKey.Length > 0 ? text.Get(_originReceiptKey)
            : string.IsNullOrWhiteSpace(_originInput.Text) ? ""
            : !valid ? text.Get("settings.origin.invalid")
            : _originInput.Text.Trim() != persistence.CurrentPersistentOriginSeed ? text.Get("settings.origin.pending") : "";
        _originFeedback.AddThemeColorOverride("font_color", _palette.Color(!busy && valid ? _palette.Secondary : _palette.Active));
        string path = RuntimeLog.CurrentLogPath;
        if (string.IsNullOrWhiteSpace(path)) path = RuntimeLog.LogDirectory;
        if (_logPath.Text != path) _logPath.Text = path;
        _logPath.TooltipText = path;
        _logFeedback.Text = RuntimeLog.LastFailureCode.Length > 0 ? text.Get("settings.logs.unavailable")
            : _logReceiptKey.Length > 0 ? text.Get(_logReceiptKey)
            : text.Get(RuntimeLog.DetailEnabled ? "settings.logs.detail" : "settings.logs.standard");
    }

    private void ApplySettingsOrigin(bool random)
    {
        if (SettingsSearchBusy) { RefreshSettings(); return; }
        string canonical;
        if (random) canonical = _persistence!.RandomizePersistentOrigin();
        else if (!_persistence!.TrySetPersistentOrigin(_originInput.Text, out canonical, out _))
        { _originReceiptKey = "settings.origin.invalid"; RefreshSettings(); return; }
        _originInput.Text = canonical;
        _originReceiptKey = "settings.origin.saved"; RefreshSettings();
    }

    private void SaveSearchResources()
    {
        if (_bindingSettings) return;
        if (SettingsSearchBusy) { RefreshSettings(); return; }
        _persistence!.SetSearchResources(new((int)_cpuLoadPercent.Value, (int)_gpuLoadPercent.Value));
        _references?.SyncSearchPreferences();
        RefreshSettings();
    }

    private void OpenSettingsLogDirectory()
    {
        try
        {
            OperationalFileLog.Flush();
            string directory = RuntimeLog.LogDirectory;
            Directory.CreateDirectory(directory);
            var uri = new Uri(Path.GetFullPath(directory) + Path.DirectorySeparatorChar).AbsoluteUri;
            if (OS.ShellOpen(uri) != Error.Ok) throw new IOException("ShellOpen failed");
            _logReceiptKey = "settings.logs.opened";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("openOperationalLogDirectoryFailed=" + ex.Message);
            _logReceiptKey = "settings.logs.open_failed";
        }
        RefreshSettings();
    }
}
