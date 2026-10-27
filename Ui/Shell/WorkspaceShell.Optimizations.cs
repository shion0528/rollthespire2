using Godot;
using RolltheSpire2.Presentation.Localization;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Ui.Shell;

internal sealed partial class WorkspaceShell
{
    private sealed record OptimizationSetting(string Id, string TextKey,
        Func<SearchOptimizationSettings, bool> Read,
        Func<SearchOptimizationSettings, bool, SearchOptimizationSettings> Change);

    private static readonly OptimizationSetting[] OptimizationSettings =
    [
        new("PrefixCache", "prefix_cache", s => s.PrefixCache, (s, v) => s with { PrefixCache = v }),
        new("ExactDrawIntervals", "exact_draw", s => s.ExactDrawIntervals, (s, v) => s with { ExactDrawIntervals = v }),
        new("LeafyLazyInitialization", "leafy", s => s.LeafyLazyInitialization, (s, v) => s with { LeafyLazyInitialization = v }),
        new("CostOrderedFilters", "cost_order", s => s.CostOrderedFilters, (s, v) => s with { CostOrderedFilters = v }),
        new("GroupedOutput", "grouped_output", s => s.GroupedOutput, (s, v) => s with { GroupedOutput = v }),
        new("DoubleWindowBatching", "double_window", s => s.DoubleWindowBatching, (s, v) => s with { DoubleWindowBatching = v }),
        new("AsyncHeaderReadback", "early_readback", s => s.AsyncHeaderReadback, (s, v) => s with { AsyncHeaderReadback = v })
    ];

    private readonly List<(CheckButton Toggle, OptimizationSetting Setting)> _optimizationToggles = [];
    private Button _restoreOptimizations = null!;
    private Label _optimizationStatus = null!;

    private void BuildSearchOptimizationSettings(VBoxContainer controls)
    {
        controls.Name = "SearchOptimizationControls";
        controls.GetParent().GetParent().Name = "SearchOptimizationsCard";
        controls.AddThemeConstantOverride("separation", 14);
        foreach (var setting in OptimizationSettings)
        {
            var row = new VBoxContainer { Name = "SearchOptimizationRow" + setting.Id,
                SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 3); controls.AddChild(row);
            var toggle = new CheckButton { Name = "SearchOptimization" + setting.Id,
                CustomMinimumSize = new(0, 42), SizeFlagsHorizontal = SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart };
            toggle.AddThemeFontSizeOverride("font_size", 19);
            row.AddChild(toggle); _optimizationToggles.Add((toggle, setting));
            var help = SettingsLabel("settings.optimizations." + setting.TextKey + ".help", 16, true, wrap: true);
            help.Name = "SearchOptimizationHelp" + setting.Id;
            help.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(help);
            toggle.Toggled += enabled => SaveSearchOptimization(setting, enabled);
        }
        _restoreOptimizations = SettingsButton("settings.optimizations.restore", () =>
        {
            if (SettingsSearchBusy) { RefreshSettings(); return; }
            _persistence!.SetSearchOptimizations(SearchOptimizationSettings.AllEnabled);
            _references?.SyncSearchPreferences(); RefreshSettings();
        });
        _restoreOptimizations.Name = "SearchOptimizationsRestoreAll";
        _restoreOptimizations.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        controls.AddChild(_restoreOptimizations);
        _optimizationStatus = _palette.Label("", 16, true);
        _optimizationStatus.Name = "SearchOptimizationsStatus";
        _optimizationStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        controls.AddChild(_optimizationStatus);
    }

    private void LocalizeSearchOptimizationSettings(IUiTextProvider text)
    {
        foreach (var (toggle, setting) in _optimizationToggles)
        {
            string key = "settings.optimizations." + setting.TextKey;
            toggle.Text = text.Get(key);
            toggle.TooltipText = toggle.Text + "\n" + text.Get(key + ".help");
        }
    }

    private void RefreshSearchOptimizationSettings()
    {
        if (_settingsText is not { } text || _persistence is not { } persistence || _optimizationStatus is null) return;
        var saved = persistence.SearchOptimizations;
        int enabled = 0;
        foreach (var (toggle, setting) in _optimizationToggles)
        {
            bool selected = setting.Read(saved);
            toggle.SetPressedNoSignal(selected); toggle.Disabled = SettingsSearchBusy;
            if (selected) enabled++;
        }
        _restoreOptimizations.Disabled = SettingsSearchBusy || enabled == OptimizationSettings.Length;
        _optimizationStatus.Text = SettingsSearchBusy ? text.Get("settings.optimizations.busy")
            : text.Format("settings.optimizations.status", enabled, OptimizationSettings.Length);
    }

    private void SaveSearchOptimization(OptimizationSetting setting, bool enabled)
    {
        if (_bindingSettings) return;
        if (SettingsSearchBusy) { RefreshSettings(); return; }
        _persistence!.SetSearchOptimizations(setting.Change(_persistence.SearchOptimizations, enabled));
        _references?.SyncSearchPreferences(); RefreshSettings();
    }
}
