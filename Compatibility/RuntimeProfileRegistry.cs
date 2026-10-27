using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Prediction.Profiles;

namespace RolltheSpire2.Compatibility;

/// <summary>Runtime-version resolution and dispatch to the corresponding prediction profile.</summary>
public static class RuntimeProfileRegistry
{
    /// <summary>Concrete-root Group continuation for an already occurring Morphic Grove, independent of Search/Filter.</summary>
    public static MorphicGrovePrediction PredictMorphicGrove(GameVersionDetection detection,
        SeedPredictionRequest request, MorphicGroveScenario scenario,
        Core.Events.MorphicGroveCommitment? commitment) =>
        MorphicGrovePredictor.Predict(detection, request, scenario, commitment);

    private static readonly RuntimeProfileId[] RegisteredProfileIds =
    {
        RuntimeProfileId.Beta110,
        RuntimeProfileId.Beta111
    };

    public static IReadOnlyList<RuntimeProfileId> RegisteredProfiles => RegisteredProfileIds;

    public static RuntimeVersionResolution Resolve(string normalizedGameVersion) => RuntimeVersionCompatibility.Resolve(normalizedGameVersion);
    public static RuntimeVersionResolution Resolve(GameVersionDetection detection) => RuntimeVersionCompatibility.Resolve(detection);
    public static IRuntimeProfile Select(string normalizedGameVersion) => Select(Resolve(normalizedGameVersion));
    public static IRuntimeProfile Select(GameVersionDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        return Select(Resolve(detection));
    }

    public static IRuntimeProfile Select(RuntimeVersionResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return resolution.ProfileId switch
        {
            RuntimeProfileId.Beta110 => Beta110Profile.Instance,
            RuntimeProfileId.Beta111 => Beta111Profile.Instance,
            _ => UnsupportedProfile.Instance
        };
    }

    public static SeedPredictionDocument Predict(GameVersionDetection detection, SeedPredictionRequest request)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(detection).ProfileId switch
        {
            RuntimeProfileId.Beta110 => Beta110PredictionProfile.Instance.Predict(request, detection),
            RuntimeProfileId.Beta111 => Beta111PredictionProfile.Instance.Predict(request, detection),
            _ => UnsupportedPredictionProfile.Instance.Predict(request, detection)
        };
    }

    internal static SeedPredictionDocument PredictFromRootHash(
        GameVersionDetection detection,
        SeedPredictionRequest request,
        TrustedRootHashInput input)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(detection).ProfileId switch
        {
            RuntimeProfileId.Beta110 => Beta110PredictionProfile.Instance.PredictFromRootHash(request, detection, input),
            RuntimeProfileId.Beta111 => Beta111PredictionProfile.Instance.PredictFromRootHash(request, detection, input),
            _ => UnsupportedPredictionProfile.Instance.Predict(request, detection)
        };
    }

    internal static SeedPredictionDocument PredictFromRootHashCapturingOpening(GameVersionDetection detection,
        SeedPredictionRequest request, TrustedRootHashInput input, out Beta111NeowOpeningProof? opening)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(request);
        opening = null;
        return Resolve(detection).ProfileId == RuntimeProfileId.Beta111
            ? Beta111PredictionProfile.Instance.PredictFromRootHashCapturingOpening(request, detection, input, out opening)
            : PredictFromRootHash(detection, request, input);
    }

    internal static SeedPredictionDocument PredictFromRootHashReusingOpening(GameVersionDetection detection,
        SeedPredictionRequest request, TrustedRootHashInput input, Beta111NeowOpeningProof opening)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(detection).ProfileId == RuntimeProfileId.Beta111
            ? Beta111PredictionProfile.Instance.PredictFromRootHashReusingOpening(request, detection, input, opening)
            : PredictFromRootHash(detection, request, input);
    }
}
