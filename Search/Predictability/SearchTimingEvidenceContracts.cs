namespace RolltheSpire2.Search.Predictability;

internal enum SearchPredictabilityConfidence
{
    Unavailable,
    Low,
    Medium,
    High
}

internal enum SearchVerificationFamily
{
    NeowOnly,
    RelicOnly,
    WorldOnly,
    CombatRewardOnly,
    MixedWithoutReward,
    MixedWithReward,
    MapOnly,
    MapMixedWithoutReward,
    MapMixedWithReward,
    Party2,
    Party3,
    Party4,
    Party2Map,
    Party3Map,
    Party4Map,
    Party2Reward,
    Party3Reward,
    Party4Reward,
    Party2MapReward,
    Party3MapReward,
    Party4MapReward,
    WorldBossOutputDemandV1,
    MixedBossOutputDemandWithoutRewardV1,
    MixedBossOutputDemandWithRewardV1,
    NeowOnlyNoDiagnosticsV1,
    RelicOnlyNoDiagnosticsV1,
    WorldOnlyNoDiagnosticsV1,
    CombatRewardOnlyNoDiagnosticsV1,
    MixedWithoutRewardNoDiagnosticsV1,
    MixedWithRewardNoDiagnosticsV1,
    MapOnlyNoDiagnosticsV1,
    MapMixedWithoutRewardNoDiagnosticsV1,
    MapMixedWithRewardNoDiagnosticsV1,
    Party2NoDiagnosticsV1,
    Party3NoDiagnosticsV1,
    Party4NoDiagnosticsV1,
    Party2MapNoDiagnosticsV1,
    Party3MapNoDiagnosticsV1,
    Party4MapNoDiagnosticsV1,
    Party2RewardNoDiagnosticsV1,
    Party3RewardNoDiagnosticsV1,
    Party4RewardNoDiagnosticsV1,
    Party2MapRewardNoDiagnosticsV1,
    Party3MapRewardNoDiagnosticsV1,
    Party4MapRewardNoDiagnosticsV1
}

internal sealed record SearchVerificationEvidence(
    SearchVerificationFamily Family,
    int SearchSampleCount,
    long ExactAttempts,
    long VerifiedMatches,
    double ExactAggregateWorkMs,
    double ExactAcceptanceRate,
    double ExactAttemptsPerVerifiedMatch,
    double AverageExactMsPerAttempt,
    SearchPredictabilityConfidence Confidence,
    string Source,
    DateTimeOffset LastObservedAtUtc)
{
    public bool Usable => SearchSampleCount > 0 && ExactAttempts > 0 && VerifiedMatches > 0 &&
                          ExactAggregateWorkMs > 0d && double.IsFinite(ExactAggregateWorkMs) &&
                          AverageExactMsPerAttempt > 0d && double.IsFinite(AverageExactMsPerAttempt) &&
                          ExactAttemptsPerVerifiedMatch >= 1d && double.IsFinite(ExactAttemptsPerVerifiedMatch);
}

internal sealed record SearchExactTimingEvidence(
    SearchVerificationFamily Family,
    int SearchSampleCount,
    long ExactAttempts,
    double ExactAggregateWorkMs,
    double AverageExactMsPerAttempt,
    SearchPredictabilityConfidence Confidence,
    string Source,
    DateTimeOffset LastObservedAtUtc)
{
    public bool Usable => SearchSampleCount > 0 && ExactAttempts > 0 &&
                          ExactAggregateWorkMs > 0d && double.IsFinite(ExactAggregateWorkMs) &&
                          AverageExactMsPerAttempt > 0d && double.IsFinite(AverageExactMsPerAttempt);
}
