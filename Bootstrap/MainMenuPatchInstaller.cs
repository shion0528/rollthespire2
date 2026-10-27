using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using RolltheSpire2.Ui;
using RolltheSpire2.Ui.Shell;

namespace RolltheSpire2.Bootstrap;

internal static partial class MainMenuPatchInstaller
{
    private static readonly string[] MainMenuTypeNames =
    {
        "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu",
        "MegaCrit.Sts2.Core.Nodes.Screens.NMainMenu",
        "MegaCrit.Sts2.Core.Nodes.MainMenu.NMainMenu"
    };

    public static void Install(Harmony harmony)
    {
        RolltheSpire2.Infrastructure.Snapshots.CrystalSphereLiveCapture.Install(harmony);
        CrystalSphereMaskAppearance.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar), "Initialize"),
            postfix: new HarmonyMethod(typeof(MainMenuPatchInstaller), nameof(RunTopBarPostfix)));
        MethodInfo? readyPostfix = AccessTools.Method(typeof(MainMenuPatchInstaller), nameof(Postfix));
        MethodInfo? submenuPostfix = AccessTools.Method(typeof(MainMenuPatchInstaller), nameof(SubmenuStackChangedPostfix));
        if (readyPostfix is null || submenuPostfix is null)
        {
            RuntimeLog.Error("main-menu UI postfix method was not found; panel disabled.");
            return;
        }

        int patched = 0;
        foreach (string typeName in MainMenuTypeNames)
        {
            Type? type = AccessTools.TypeByName(typeName);
            if (type is null || !typeof(Control).IsAssignableFrom(type))
            {
                continue;
            }

            MethodInfo? ready = AccessTools.Method(type, "_Ready");
            if (ready is null)
            {
                continue;
            }

            harmony.Patch(ready, postfix: new HarmonyMethod(readyPostfix));
            RuntimeLog.Info($"minimalUiPatchTarget={type.FullName}._Ready");

            MethodInfo? submenuChanged = AccessTools.Method(type, "OnSubmenuStackChanged");
            if (submenuChanged is not null)
            {
                harmony.Patch(submenuChanged, postfix: new HarmonyMethod(submenuPostfix));
                RuntimeLog.Info($"mainMenuVisibilityPatchTarget={type.FullName}.OnSubmenuStackChanged");
            }
            else
            {
                RuntimeLog.Info(
                    $"mainMenuVisibilityPatchTargetMissing={type.FullName}.OnSubmenuStackChanged;beta111-top-level-launcher-sync-unavailable=true");
            }

            patched++;
            break;
        }

        if (patched == 0)
        {
            RuntimeLog.Error("no supported main-menu _Ready target was found; analysis remains disabled and no panel will be injected.");
        }
    }

    private static void Postfix(object __instance)
    {
        if (__instance is NMainMenu mainMenu)
        {
            // Main-menu _Ready is the first source-audited Godot-main-thread boundary
            // where ModelDb + SaveManager runtime facts are available without creating
            // or mutating a run. Capture into RT2-owned immutable DTOs before UI consumers.
            try
            {
                RuntimeLog.BindMainThreadPump();
                RuntimeAuthorityEnvironment.CaptureOnMainThread(ModRuntime.Snapshot);
            }
            catch (Exception ex)
            {
                RuntimeLog.WarnException("runtimeAuthorityCaptureFailed=true;failSoft=true", ex);
            }
            MinimalPanelInjector.EnsureAttached(mainMenu, ModRuntime.Snapshot);
            ActualSearchBenchmarkRequested(mainMenu);
        }
    }

    private static void SubmenuStackChangedPostfix(object __instance)
    {
        if (__instance is NMainMenu mainMenu)
        {
            AppShellHost.SyncMainMenuSurfaceVisibility(mainMenu);
        }
    }

    private static void RunTopBarPostfix(MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar __instance,
        MegaCrit.Sts2.Core.Runs.IRunState runState)
    {
        try { InRunPredictionLauncher.EnsureAttached(__instance, runState, ModRuntime.Snapshot); }
        catch (Exception ex) { RuntimeLog.WarnException("runPredictionLauncherAttachFailed=true", ex); }
    }

    static partial void ActualSearchBenchmarkRequested(Node mainMenu);
}
