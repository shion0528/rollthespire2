using System.Reflection;
using System.Text.Json;
using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Ui.Persistence;
using RolltheSpire2.Ui.Shell;

namespace RolltheSpire2.Diagnostics.GpuProfiling;

// Optional in-engine UI acceptance. Its own temporary persistence is isolated
// from the player's real preferences, query, results and checkpoint files.
internal sealed partial class OptimizationUiProbe : Node
{
    private static readonly string[] Ids = ["PrefixCache", "ExactDrawIntervals", "LeafyLazyInitialization",
        "CostOrderedFilters", "GroupedOutput", "AsyncHeaderReadback", "DoubleWindowBatching"];

    public override async void _Ready()
    {
        string directory = System.Environment.GetEnvironmentVariable("RT2_OPTIMIZATION_UI_DIRECTORY")
            ?? throw new InvalidOperationException("UI test requires an explicit evidence directory.");
        Directory.CreateDirectory(directory);
        WorkspaceShell? shell = null;
        CanvasLayer? layer = null;
        int exit = 2;
        try
        {
            layer = new CanvasLayer { Layer = 120 };
            GetTree().Root.AddChild(layer);
            var host = new Control { Size = GetViewport().GetVisibleRect().Size };
            layer.AddChild(host);
            shell = new WorkspaceShell(); host.AddChild(shell);
            shell.InitializeWithPersistence(ModRuntime.Snapshot, Path.Combine(directory, "isolated-user"));
            shell.Open();
            Invoke(shell, "OpenSettings");
            await Frames(4);
            var persistence = (SearchWorkspacePersistence)Field(shell, "_persistence");
            Require(persistence.SearchOptimizations == SearchOptimizationSettings.AllEnabled, "UI default switches differ.");
            var toggles = Ids.Select(id => Find<CheckButton>(shell, "SearchOptimization" + id)).ToArray();
            Require(toggles.All(t => t.ButtonPressed && !t.Disabled), "Default UI toggles are not all enabled.");
            var card = Find<Control>(shell, "SearchOptimizationsCard");
            var scroll = Ancestor<ScrollContainer>(card);
            scroll.ScrollVertical = (int)card.Position.Y;
            persistence.SetLanguageOverride("zh"); Invoke(shell, "RefreshLanguage");
            await Frames(4);
            CheckBounds(card, toggles);
            await Screenshot(directory, "optimizations-zh.png");
            foreach (var toggle in toggles) toggle.ButtonPressed = false;
            Require(persistence.SearchOptimizations == SearchOptimizationSettings.AllDisabled, "UI off toggles were not saved.");
            var restore = Find<Button>(shell, "SearchOptimizationsRestoreAll");
            Require(!restore.Disabled, "Restore action unavailable after disabling switches.");
            restore.EmitSignal(BaseButton.SignalName.Pressed);
            Require(persistence.SearchOptimizations == SearchOptimizationSettings.AllEnabled && toggles.All(t => t.ButtonPressed),
                "UI restore did not restore all toggles.");
            persistence.SetLanguageOverride("en"); Invoke(shell, "RefreshLanguage");
            await Frames(4);
            CheckBounds(card, toggles);
            await Screenshot(directory, "optimizations-en.png");
            host.Size = new Vector2(1100, 780);
            await Frames(4);
            scroll.ScrollVertical = (int)card.Position.Y;
            await Frames(3);
            CheckBounds(card, toggles);
            await Screenshot(directory, "optimizations-narrow.png");
            var report = new { result = "PASS", switches = Ids.Length, defaultAllEnabled = true,
                allOffSaved = true, restoreAllWorks = true, chineseAndEnglish = true,
                normalAndNarrowBounds = true, isolatedPersistence = true };
            File.WriteAllText(Path.Combine(directory, "ui-result.json"), JsonSerializer.Serialize(report));
            RuntimeLog.Info("optimizationUiProbe=true;reportJson=" + JsonSerializer.Serialize(report));
            exit = 0;
        }
        catch (Exception failure)
        {
            File.WriteAllText(Path.Combine(directory, "ui-result.json"),
                JsonSerializer.Serialize(new { result = "FAIL", error = failure.ToString() }));
            RuntimeLog.Fault("optimizationUiProbe=true;result=FAIL", failure);
        }
        finally
        {
            shell?.QueueFree(); layer?.QueueFree();
            OperationalFileLog.Flush(); GetTree().Quit(exit);
        }
    }

    private async Task Frames(int count)
    { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private async Task Screenshot(string directory, string name)
    {
        var drawn = ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        RenderingServer.ForceDraw(false);
        await drawn;
        using var image = GetViewport().GetTexture().GetImage();
        Require(image.SavePng(Path.Combine(directory, name)) == Error.Ok, "UI screenshot save failed.");
    }
    private static void CheckBounds(Control card, CheckButton[] toggles)
    {
        Rect2 bounds = card.GetGlobalRect();
        foreach (var toggle in toggles)
        {
            Rect2 rect = toggle.GetGlobalRect();
            Require(toggle.IsVisibleInTree() && rect.Size.X > 100 && rect.Position.X >= bounds.Position.X &&
                rect.End.X <= bounds.End.X + 1, "Optimization toggle clipped horizontally: " + toggle.Name);
        }
    }
    private static T Find<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T ?? throw new InvalidOperationException("Missing UI node: " + name);
    private static T Ancestor<T>(Node node) where T : Node
    {
        for (Node? p = node.GetParent(); p is not null; p = p.GetParent()) if (p is T found) return found;
        throw new InvalidOperationException("Missing UI ancestor: " + typeof(T).Name);
    }
    private static object Field(object owner, string name) => owner.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string name) => owner.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
    private static void Require(bool value, string issue)
    { if (!value) throw new InvalidDataException(issue); }
}
