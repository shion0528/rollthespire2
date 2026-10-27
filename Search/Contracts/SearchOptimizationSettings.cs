namespace RolltheSpire2.Search.Contracts;

/// <summary>
/// Immutable physical execution preferences. These switches change work only;
/// they are not query predicates or part of the semantic/checkpoint identity.
/// </summary>
public sealed record SearchOptimizationSettings
{
    public bool PrefixCache { get; init; } = true;
    public bool ExactDrawIntervals { get; init; } = true;
    public bool LeafyLazyInitialization { get; init; } = true;
    public bool CostOrderedFilters { get; init; } = true;
    public bool GroupedOutput { get; init; } = true;
    public bool AsyncHeaderReadback { get; init; } = true;
    public bool DoubleWindowBatching { get; init; } = true;

    public static SearchOptimizationSettings AllEnabled { get; } = new();
    public static SearchOptimizationSettings AllDisabled { get; } = new()
    {
        PrefixCache = false,
        ExactDrawIntervals = false,
        LeafyLazyInitialization = false,
        CostOrderedFilters = false,
        GroupedOutput = false,
        AsyncHeaderReadback = false,
        DoubleWindowBatching = false
    };

    // Fixed bit order and versioned spelling; independent of culture, hashing
    // randomization and JSON property ordering. Suitable for a physical/preview
    // cache key, but deliberately excluded from the semantic query fingerprint.
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => "SearchOptimizations.v2." +
        (PrefixCache ? "1" : "0") + (ExactDrawIntervals ? "1" : "0") +
        (LeafyLazyInitialization ? "1" : "0") + (CostOrderedFilters ? "1" : "0") +
        (GroupedOutput ? "1" : "0") + (AsyncHeaderReadback ? "1" : "0") +
        (DoubleWindowBatching ? "1" : "0");
}
