using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.Prediction.Profiles;
using RolltheSpire2.Core.World;
using RolltheSpire2.Search.Compilation;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.Runtime;
using RolltheSpire2.Search.Semantics;
using RolltheSpire2.Ui.Persistence;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly GameVersionDetection Detection = new("0.111.0", "0.111.0", "beta", "DiagnosticDemandChecks", true, "");
    private static readonly IRuntimeProfile Profile = Beta111Profile.Instance;
    private static readonly List<object> Cases = new();
    private static int _assertions, _documents, _exact, _matches, _rejects, _exceptions;
    private static RuntimeContextAuthoritySnapshot Authority = null!;
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new SearchWorkspacePersistence.ModelKeyJsonConverter());
        return options;
    }
    private static void Need(bool value, string message)
    {
        _assertions++;
        if (!value) throw new InvalidDataException(message);
    }
    // Only the per-call random identity and injected exception's source stack vary.
    private static void Normalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("RequestId");
            if (obj["Code"] is JsonValue code && code.TryGetValue<string>(out var codeText) && codeText == PredictionDiagnosticCodes.Exception && obj["Value"] is { } value)
                obj["Value"] = value.GetValue<string>().Split('\n')[0].TrimEnd('\r');
            foreach (var item in obj.ToArray()) Normalize(item.Value);
        }
        else if (node is JsonArray array)
        {
            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (array[i] is JsonObject diagnostic && diagnostic["Code"] is JsonValue code && code.TryGetValue<string>(out var codeText) && codeText == PredictionDiagnosticCodes.RequestId)
                    array.RemoveAt(i);
                else Normalize(array[i]);
            }
        }
    }
    private static void Add(string id, object value)
    {
        JsonNode? snapshot = JsonSerializer.SerializeToNode(value, Json);
        Normalize(snapshot);
        Cases.Add(new { id, snapshot });
    }
    private static SeedPredictionRequest Request(TrustedRootHashInput input, SeedPredictionDomainSelection domains, bool diagnostics)
    {
        Need(SeedPredictionRequest.TryCreateFromRootHash(input, Authority.Character, Authority.Ascension,
            Authority.PlayersCount, Authority.PlayerSlotIndex, Authority, AncientOptionConditionProfile.BroadDefault,
            domains, 10, diagnostics, out var request, out var error), "Request rejected: " + error);
        return request!;
    }
    private static void DirectDocuments()
    {
        var domains = new[] { SeedPredictionDomainSelection.Neow, SeedPredictionDomainSelection.World,
            SeedPredictionDomainSelection.Neow | SeedPredictionDomainSelection.NormalCombatRewards };
        foreach (string seed in new[] { "9KT7EQEZ0000", "9KT7EQEZ002M", "9KT7EQEZL3S0" })
        foreach (bool bound in new[] { false, true })
        foreach (bool diagnostics in new[] { false, true })
        foreach (var domain in domains)
        {
            var input = bound ? TrustedRootHashInput.FromCanonicalSeed(Profile, seed) : TrustedRootHashInput.RootOnly(Profile.ComputeRootSeed(seed));
            var request = Request(input, domain, diagnostics);
            if (domain == SeedPredictionDomainSelection.World && !diagnostics) request.WorldProjection = WorldPredictionProjection.BossIdentityOnly;
            var doc = RuntimeProfileRegistry.PredictFromRootHash(Detection, request, input);
            Need(doc.Sections.Count > 0 && !doc.Warnings.Any(w => w.Code == PredictionWarningCode.AnalysisFailed), "Direct case failed instead of exercising prediction.");
            Need(diagnostics ? doc.Diagnostics.Count >= 14 : doc.Diagnostics.Count == 0, "Normal diagnostic demand differs.");
            if (!bound && diagnostics) Need(doc.Diagnostics.Any(d => d.Code == "trusted-root-hash"), "Missing RootOnly diagnostics.");
            Add($"direct:{seed}:{bound}:{diagnostics}:{domain}", doc); _documents++;
        }
    }
    private static void InjectedFailure()
    {
        var predictor = new Beta111PredictionProfile((root, authority, profile, exact) =>
            throw new InvalidOperationException("DiagnosticDemandChecks.FixedFailure"));
        foreach (bool diagnostics in new[] { false, true })
        foreach (bool bound in new[] { false, true })
        {
            var input = bound ? TrustedRootHashInput.FromCanonicalSeed(Profile, "9KT7EQEZ002M") : TrustedRootHashInput.RootOnly(Profile.ComputeRootSeed("9KT7EQEZ002M"));
            var request = Request(input, SeedPredictionDomainSelection.Neow, diagnostics);
            var doc = predictor.PredictFromRootHash(request, Detection, input);
            Need(doc.Warnings.Any(w => w.Code == PredictionWarningCode.AnalysisFailed), "Injected failure lost warning.");
            var exception = doc.Diagnostics.Single(d => d.Code == PredictionDiagnosticCodes.Exception);
            Need(exception.Value.Contains("System.InvalidOperationException: DiagnosticDemandChecks.FixedFailure", StringComparison.Ordinal), "Exception type/message lost.");
            if (!diagnostics) Need(doc.Diagnostics.Count == 1, "No-diagnostic failure must retain only Exception.");
            bool threw = false;
            try { ProductionExactSearchEvaluator.ThrowIfPredictionFailed(doc); }
            catch (InvalidOperationException error) { threw = error.Message.Contains("DiagnosticDemandChecks.FixedFailure", StringComparison.Ordinal); }
            Need(threw, "Exact silently accepted AnalysisFailed.");
            Add($"failure:{bound}:{diagnostics}", doc); _exceptions++;
            // Trust mismatch is another Fail path, independent of injected identity failure.
            var other = TrustedRootHashInput.RootOnly(input.RootHash + 1);
            var mismatch = predictor.PredictFromRootHash(request, Detection, other);
            Need(mismatch.Warnings.Any(w => w.Code == PredictionWarningCode.InvalidRequestContext), "Trusted input mismatch did not fail closed.");
            Add($"trustMismatch:{bound}:{diagnostics}", mismatch);
        }
    }
    private static void ExactCases(string fixtures)
    {
        var context = SearchContextFactory.From(RuntimeProfileId.Beta111, Authority.Character.CharacterKey,
            Authority.Ascension, Authority, Detection, AncientOptionConditionProfile.BroadDefault);
        foreach (string id in new[] { "RC05", "RC06", "RC07", "RC08" })
        {
            using var witness = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, id + ".witness.json")));
            string known = witness.RootElement.GetProperty("seed").GetString()!;
            var query = witness.RootElement.GetProperty("query").Deserialize<SearchQuery>(Json)!;
            foreach (bool diagnostics in new[] { false, true })
            {
                var compiled = SearchCompiler.Compile(query, context);
                var result = ExactSearchExecutionRequestFactory.Compile(compiled, new(known, 1, int.MaxValue, 1, IncludeDiagnostics: diagnostics));
                Need(result.Success && result.Plan is not null, "Exact compile failed: " + result.Issue);
                var plan = result.Plan!;
                Need(ExactWorldProjectionPolicy.Resolve(plan) == (diagnostics ? WorldPredictionProjection.All : WorldPredictionProjection.BossIdentityOnly), "Fixture failed to exercise Boss diagnostic scope.");
                foreach (string seed in new[] { known, "9KT7EQEZ0000", "9KT7EQEZ002M", "9KT7EQEZ003J" })
                foreach (bool bound in new[] { false, true })
                {
                    var input = bound ? TrustedRootHashInput.FromCanonicalSeed(Profile, seed) : TrustedRootHashInput.RootOnly(Profile.ComputeRootSeed(seed));
                    var exact = ProductionExactSearchEvaluator.Evaluate(plan, input);
                    if (seed == known) Need(exact.IsMatch, "Known witness no longer matches: " + id);
                    if (exact.IsMatch) _matches++;
                    if (exact.Evaluation.Disposition == SearchDisposition.NoMatch) _rejects++;
                    Add($"exact:{id}:{seed}:{bound}:{diagnostics}", exact); _exact++;
                }
            }
        }
        Need(_matches > 0 && _rejects > 0, "Exact coverage lacked Match/NoMatch.");
    }
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: ProductionSearchHost <random-complex-fixture-folder> <snapshot.json>");
        string fixtures = Path.GetFullPath(args[0]);
        Authority = JsonSerializer.Deserialize<RuntimeContextAuthoritySnapshot>(File.ReadAllText(Path.Combine(fixtures, "source-authority.json")), Json)!;
        DirectDocuments(); InjectedFailure(); ExactCases(fixtures);
        var snapshot = new { schema = 1, assertions = _assertions, directDocuments = _documents, exactCases = _exact,
            exactMatches = _matches, exactNoMatches = _rejects, injectedFailures = _exceptions, cases = Cases };
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(snapshot, Json));
        string dll = typeof(SeedPredictionDocument).Assembly.Location;
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", _assertions, _documents, _exact, _matches, _rejects, _exceptions,
            dll, dllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))), snapshot = output,
            snapshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))), scope = "Offline full-document parity snapshot; no game or installation" }, Json));
    }
}
