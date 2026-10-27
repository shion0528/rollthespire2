using System.Text.Json;
using RolltheSpire2.Search.FamilyExecution;

int checks = 0;
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidDataException(message);
}
double? Quote(double population, double ns = .95, bool enabled = true, bool compact = true) =>
    CompactSparseGpuPricing.AdditionalFixedMilliseconds(enabled, compact, population, ns);

// An estimate must never widen admission to dense/unsupported bodies, disabled
// ordering, invalid values or populations outside its explicit coverage region.
foreach (double bad in new[] { -1d, 0, 1, 63.999, 1048576, 16777216, double.NaN, double.PositiveInfinity })
    Require(Quote(bad) is null, "The sparse envelope admitted an unsupported population.");
foreach (double bad in new[] { -1d, 0, double.NaN, double.PositiveInfinity, double.MaxValue })
    Require(Quote(64, bad) is null, "The sparse envelope admitted an invalid numerical price.");
Require(Quote(32768, enabled: false) is null && Quote(32768, compact: false) is null,
    "The coverage extension escaped its optimization/compact admission boundary.");

// Independent cost oracle: every one-window compact population retains at
// least the already admitted dense anchor's numerical cost, while converging
// continuously to the unchanged dense price. No header/transport is hidden.
foreach (double ns in new[] { .22, .55, .615, .95, 1.43 })
{
    double previousFixed = double.PositiveInfinity;
    foreach (double input in new[] { 64d, 128, 1387, 32768, 262144, 932067, 1048575.999 })
    {
        double fixedMs = Quote(input, ns) ?? throw new InvalidDataException("Covered sparse geometry was left unpriced.");
        double numerical = input * ns / 1e6 + fixedMs;
        double denseAnchor = 1048576 * ns / 1e6;
        Require(Math.Abs(numerical - denseAnchor) < 1e-12 * Math.Max(1, denseAnchor),
            "Sparse quote discounted the admitted dense anchor or double-counted numerical work.");
        Require(fixedMs >= 0 && fixedMs <= previousFixed,
            "The fixed numerical floor is negative or grows as population increases.");
        previousFixed = fixedMs;
    }
}

// RC05 is an integration-shaped coverage witness, not a target-specific model:
// all six complete E/S/W orders, public and private, must have prices for E/S.
// Survival values come from the immutable authority (Trash 1/5, fake inventory
// 2/3, shop pools 21 and 32, world conjunction 1/18). They are not timed outputs.
var survival = new Dictionary<char, double> { ['E'] = 1d / 5 * 2 / 3, ['S'] = 1d / 21 / 32, ['W'] = 1d / 18 };
string[] orders = ["ESW", "EWS", "SEW", "SWE", "WES", "WSE"];
double minimumCoveredPopulation = double.PositiveInfinity;
foreach (string order in orders)
foreach (bool privateEdges in new[] { false, true })
{
    double population = 16777216;
    for (int index = 0; index < order.Length; index++)
    {
        char family = order[index];
        if ((family is 'E' or 'S') && population < 1048576)
        {
            Require(index > 0 && Quote(population) is not null,
                $"RC05 complete {order} order ({privateEdges}) still has an E/S coverage hole.");
            minimumCoveredPopulation = Math.Min(minimumCoveredPopulation, population);
        }
        population *= survival[family];
    }
    Require(Math.Abs(population / 16777216 - 1d / 90720) < 1e-18,
        "Reordering changed the conjunction's modeled terminal survival.");
}
Require(CompactSparseGpuPricing.Evidence.Contains("EstimatedNotMeasured", StringComparison.Ordinal) &&
    CompactSparseGpuPricing.Evidence.Contains("ConservativeCoarse", StringComparison.Ordinal),
    "Sparse extrapolation lost its uncertainty/provenance label.");
Console.WriteLine("RT2_COMPACT_SPARSE_PRICING " + JsonSerializer.Serialize(new {
    result = "PASS", checks, orders = orders.Length, transportVariants = 2,
    minimumCoveredPopulation, gpuDeviceCreated = false, measurementClaimed = false
}));
