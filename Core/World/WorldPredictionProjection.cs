namespace RolltheSpire2.Core.World;

/// <summary>
/// Search-only output demand. This never changes the world RNG continuation;
/// ordinary analysis and diagnostics retain the complete projection.
/// </summary>
internal enum WorldPredictionProjection : byte
{
    All = 0,
    BossIdentityOnly = 1
}
