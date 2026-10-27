using System.Text.Json;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Ui.Persistence;

namespace CheckpointResumeChecks;

internal static partial class Program
{
    static partial void CheckQueryBinding(string root)
    {
        const ulong legacyOrigin = 672520313344496122UL, legacyNext = 672576649782759930UL;
        const string queryA = "canonical-query-A", queryB = "canonical-query-B";
        Directory.CreateDirectory(Path.GetDirectoryName(CursorFile(root))!);
        byte[] legacyJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1, Initialized = true, PersistentOrigin = legacyOrigin,
            NextCursor = legacyNext, WrapCount = 7
        });
        File.WriteAllBytes(CursorFile(root), legacyJson);
        var legacy = Open(root);
        legacy.EnsureEnvironment(Environment("fixture-env-A"));
        Require(legacy.Cursor.CheckpointVersion == 0 && legacy.CanResumeSearch(queryA) &&
            legacy.ResolveSearchStartOrdinal(queryA) == legacyNext && legacy.ResolveSearchStartOrdinal(queryB) == legacyNext,
            "Legacy cursor was rounded, reset, or incorrectly bound to an unknown query.");
        Require(legacyJson.AsSpan().SequenceEqual(File.ReadAllBytes(CursorFile(root))), "Merely inspecting a legacy resume rewrote its cursor.");
        legacy.BeginSearchCheckpoint(queryA, legacy.ResolveSearchStartOrdinal(queryA));
        Require(legacy.Cursor.CheckpointVersion == 1 && legacy.Cursor.CheckpointQueryFingerprint == queryA &&
            legacy.Cursor.CheckpointEnvironmentFingerprint == "fixture-env-A" && legacy.CurrentNextCursorOrdinal == legacyNext,
            "Binding a legacy checkpoint lost its exact saved value.");
        legacy.ObserveSafeNextCursor(legacyNext + 17);
        Require(legacy.TryFlushSearchCheckpoint(), "Checkpoint flush did not finish.");

        var reloaded = Open(root);
        Require(reloaded.CanResumeSearch(queryA) && reloaded.ResolveSearchStartOrdinal(queryA) == legacyNext + 17,
            "Same-query disk reload did not recover its exact checkpoint.");
        byte[] boundJson = File.ReadAllBytes(CursorFile(root));
        Require(!reloaded.CanResumeSearch(queryB) && reloaded.ResolveSearchStartOrdinal(queryB) == legacyOrigin &&
            reloaded.CurrentNextCursorOrdinal == legacyNext + 17,
            "A different query reused incomplete filtering work or destroyed the old checkpoint before starting.");
        Require(boundJson.AsSpan().SequenceEqual(File.ReadAllBytes(CursorFile(root))), "Previewing a different query changed the saved checkpoint.");
        reloaded.SetSearchResources(new SearchResourceBudget(25, 75));
        Require(reloaded.CanResumeSearch(queryA) && reloaded.ResolveSearchStartOrdinal(queryA) == legacyNext + 17,
            "Changing resource budgets invalidated query semantics or the checkpoint.");

        reloaded.EnsureEnvironment(Environment("fixture-env-B"));
        Require(!reloaded.CanResumeSearch(queryA) && reloaded.ResolveSearchStartOrdinal(queryA) == legacyOrigin,
            "A changed environment reused rejected roots from the old environment.");
        reloaded.BeginSearchCheckpoint(queryB, reloaded.ResolveSearchStartOrdinal(queryB));
        reloaded.ObserveSafeNextCursor(legacyOrigin + 23);
        var stale = new SentinelSession(legacyNext + 99);
        reloaded.ObserveSessionCheckpoint(stale, queryA);
        Require(reloaded.CurrentNextCursorOrdinal == legacyOrigin + 23,
            "An old detached session overwrote the newer query's checkpoint.");
        Require(reloaded.TryFlushSearchCheckpoint(), "New-query checkpoint did not flush.");
        var rebound = Open(root);
        Require(rebound.CanResumeSearch(queryB) && rebound.ResolveSearchStartOrdinal(queryB) == legacyOrigin + 23 &&
            rebound.Cursor.CheckpointEnvironmentFingerprint == "fixture-env-B" && rebound.Cursor.WrapCount == 7,
            "New query/environment binding did not survive disk reload.");
        Require(rebound.TrySetPersistentOrigin("12345678ZZFZ", out _, out _) && !rebound.CanResumeSearch(queryB) &&
            rebound.Cursor.CheckpointQueryFingerprint.Length == 0 && rebound.ResolveSearchStartOrdinal(queryB) == Parse("12345678ZZFZ"),
            "An explicitly changed origin retained the old checkpoint binding.");
        Report(new { phase = "queryBinding", result = "PASS", legacyNext, exactLegacyPreserved = true,
            sameQueryResumes = true, changedQueryUsesOrigin = true, changedEnvironmentUsesOrigin = true,
            resourceBudgetIndependent = true, staleDetachedSessionIgnored = true });
    }

    private static SearchEnvironmentSignature Environment(string fingerprint) => new()
    {
        CaptureComplete = true, Fingerprint = fingerprint, GameVersionIdentity = "Fixture-0.111.0"
    };
}
