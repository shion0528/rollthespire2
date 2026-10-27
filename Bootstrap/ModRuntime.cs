using HarmonyLib;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Verification;

namespace RolltheSpire2.Bootstrap;

internal static class ModRuntime
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static ModRuntimeSnapshot Snapshot { get; private set; } = ModRuntimeSnapshot.NotInitialized;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            RuntimeLog.InitializeOnMainThread();
            Infrastructure.Persistence.HistoricalRuntimeData.Archive(Path.GetDirectoryName(OperationalFileLog.LogDirectory)!);
        Search.FamilyExecution.FamilyDeviceProfileFoundation.CaptureAvailabilityOnMainThread();
            // OneTimeInitialization reads this manager before ModManager invokes us.
            GameVersionDetection detection = GameVersionDetector.Detect(() =>
            {
                var release = MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo;
                return release is null ? null : (release.Version, release.Branch);
            });
            RuntimeVersionResolution compatibility = RuntimeProfileRegistry.Resolve(detection);
            IRuntimeProfile profile = RuntimeProfileRegistry.Select(compatibility);
            SeedRngVectorVerification vectors = SeedRngVectorVerifier.RunAll();
            AssemblyEvidence assemblyEvidence = AssemblyEvidence.Capture();
            Snapshot = new ModRuntimeSnapshot(
                detection,
                profile,
                compatibility,
                vectors,
                assemblyEvidence,
                "RolltheSpire2 1.3.3: Query -> Filter -> Predictor -> Query validation -> Result. Runtime version and authority identities remain separate.");

            RuntimeLog.Info(
                $"Runtime initialized: game={detection.DisplayVersion}; profile={profile.ProfileId}; " +
                $"compatibility={compatibility.SupportKind}; confidence={compatibility.Confidence}; runtimeAccepted={compatibility.RuntimeAccepted.ToString().ToLowerInvariant()}; " +
                $"gameVersionIdentity={compatibility.GameVersionIdentity}; reference={compatibility.ReferenceVersion}; " +
                $"semanticProfile={compatibility.SemanticProfileId}; " +
                $"semanticFingerprint={RuntimeProfilePolicies.RngSemanticFingerprint(profile.ProfileId)}; " +
                $"capability={profile.Capability}; versionDetected={detection.IsExact.ToString().ToLowerInvariant()}; " +
                $"rngVectors={(vectors.Passed ? "Passed" : "Failed")}({vectors.Cases.Count})");
            if (compatibility.IsFallback)
            {
                RuntimeLog.Warn(
                    $"compatibilityWarning=UnvalidatedGameVersion;detected={compatibility.DetectedVersion}; " +
                    $"reference={compatibility.ReferenceVersion};profile={compatibility.ProfileId}; " +
                    "RT2 will attempt compatible prediction/search using captured authority; game-version validation remains pending.");
            }
            RuntimeLog.Info($"Readable log: {RuntimeLog.CurrentLogPath}");
            RuntimeLog.Info($"rt2BuildIdentity={typeof(ModRuntime).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion}");
            ResourceTransportProbe.RunIfRequested();
            RuntimeLog.GlobalSummary("RT2 loaded", startup: true);
            RuntimeLog.Info($"versionEvidence={detection.Evidence}");
            if (!detection.IsExact) RuntimeLog.Warn($"versionDetectionFailed={detection.FailureReason}");
            // Stable historical log-parser keys; the current CLR/build identity is AssemblyEvidence.
            RuntimeLog.Info($"rewriteAssemblyPath={assemblyEvidence.AssemblyPath}");
            RuntimeLog.Info($"rewriteAssemblySha256={(assemblyEvidence.IsExact ? assemblyEvidence.Sha256 : "unavailable")}");
            RuntimeLog.Info($"rewriteAssemblyHashExact={assemblyEvidence.IsExact.ToString().ToLowerInvariant()}");
            if (!assemblyEvidence.IsExact)
            {
                RuntimeLog.Error($"mod assembly hash evidence unavailable: {assemblyEvidence.FailureReason}");
            }

            try
            {
                var harmony = new Harmony(ModEntry.ModId + ".MainMenu");
                MainMenuPatchInstaller.Install(harmony);
            }
            catch (Exception ex)
            {
                RuntimeLog.Fault("startupFault=true;phase=MainMenuPatchInstallation", ex);
            }

            _initialized = true;
        }
    }
}

internal sealed record ModRuntimeSnapshot(
    GameVersionDetection Detection,
    IRuntimeProfile Profile,
    RuntimeVersionResolution Compatibility,
    SeedRngVectorVerification Vectors,
    AssemblyEvidence AssemblyEvidence,
    string Status)
{
    public bool IsCompatibilityFallback => Compatibility.IsFallback;
    public bool IsPendingRuntimeValidation => Compatibility.IsPendingValidation;
    public bool RequiresCompatibilityWarning => Compatibility.RequiresCompatibilityWarning;
    public bool RuntimeAccepted => Compatibility.RuntimeAccepted;
    public CompatibilityConfidence CompatibilityConfidence => Compatibility.Confidence;

    public static ModRuntimeSnapshot NotInitialized { get; } = new(
        GameVersionDetection.NotDetected,
        UnsupportedProfile.Instance,
        RuntimeVersionResolution.Unsupported(string.Empty, "not-initialized"),
        SeedRngVectorVerification.NotRun,
        new AssemblyEvidence(string.Empty, string.Empty, false, "Not initialized."),
        "Not initialized.");
}
