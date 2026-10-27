namespace RolltheSpire2.Search.FamilyExecution;

// A conservative coverage extension for already supported numerical bodies,
// not a sparse-dispatch measurement or a new executable capability. Preserve
// the old dense anchor's numerical cost for an underfilled compact window.
// Submission, header readback, public payload and Exact remain caller-owned.
internal static class CompactSparseGpuPricing
{
    internal const double MinimumPopulation = 64;
    internal const double DenseAnchorPopulation = 1048576;
    internal const string Revision = "CompactSparseEnvelope.20261022.v1";
    internal const string Evidence = Revision +
        ";Model=ConservativeCoarse;EstimatedNotMeasured;ExistingSupportedBodyOnly;" +
        "CompactInputOnly;MinInput=64;MaxInputExclusive=1048576;" +
        "NumericalFloor=Existing1048576InputQuote;FixedWindowOwnsMissingAnchorWork;" +
        "SubmitHeaderPayloadAndExactExcluded;ModeledPopulationOnly";

    internal static double? AdditionalFixedMilliseconds(bool enabled, bool compactInput,
        double meanInputPopulation, double numericalNanosecondsPerInput)
    {
        if (!enabled || !compactInput || !double.IsFinite(meanInputPopulation) ||
            meanInputPopulation < MinimumPopulation || meanInputPopulation >= DenseAnchorPopulation ||
            !(numericalNanosecondsPerInput > 0) || !double.IsFinite(numericalNanosecondsPerInput)) return null;
        double ms = (DenseAnchorPopulation - meanInputPopulation) * numericalNanosecondsPerInput / 1e6;
        return double.IsFinite(ms) ? ms : null;
    }
}
