using System.Text.Json;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Rng;
using RolltheSpire2.Search.FamilyExecution;
using RolltheSpire2.Ui.Persistence;

namespace CpuExecutionChecks;

// Exercises the production origin mapper and persistence class. Every file is
// confined to a new temporary fixture, never the player's saved workspace.
public static class OriginChecks
{
    public static void Run(Action<string> emit)
    {
        int assertions = 0, randomWordCases = 0;
        void Require(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidDataException(message);
        }
        void CheckRandomWord(ulong value)
        {
            ulong ordinal = VisibleSeedBlockPolicy.RandomOriginFromValue(value);
            Require(ordinal < Beta110SeedCodec.SpaceSize, "Random origin escaped the visible seed space.");
            Require(ordinal % VisibleSeedBlockPolicy.BlockSize == 0, "Random origin was not block aligned.");
            Require(Beta110SeedCodec.FormatOrdinal(ordinal).EndsWith("0000", StringComparison.Ordinal),
                "Random origin did not retain its four zero suffix characters.");
            randomWordCases++;
        }

        Require(VisibleSeedBlockPolicy.RandomPrefixLength == 8 && VisibleSeedBlockPolicy.ZeroSuffixLength == 4 &&
            VisibleSeedBlockPolicy.BlockSize == 1_336_336 && VisibleSeedBlockPolicy.BlockCount == 1_785_793_904_896,
            "Origin policy must split the twelve-character seed into eight plus four characters.");
        Require(VisibleSeedBlockPolicy.RandomOriginFromValue(0) == 0 &&
            VisibleSeedBlockPolicy.RandomOriginFromValue(1) == VisibleSeedBlockPolicy.BlockSize &&
            VisibleSeedBlockPolicy.RandomOriginFromValue(VisibleSeedBlockPolicy.BlockCount) == 0,
            "Random prefix boundary mapping changed.");
        ulong last = VisibleSeedBlockPolicy.RandomOriginFromValue(VisibleSeedBlockPolicy.BlockCount - 1);
        Require(Beta110SeedCodec.FormatOrdinal(last) == "ZZZZZZZZ0000" &&
            checked(last + VisibleSeedBlockPolicy.BlockSize) == Beta110SeedCodec.SpaceSize,
            "Last aligned origin does not finish exactly at the visible-space boundary.");
        UInt128 acceptedWords = ((UInt128)1 << 64) - VisibleSeedBlockPolicy.MinimumUnbiasedRandomValue;
        Require(acceptedWords % VisibleSeedBlockPolicy.BlockCount == 0 &&
            VisibleSeedBlockPolicy.MinimumUnbiasedRandomValue < VisibleSeedBlockPolicy.BlockCount,
            "Random-prefix rejection sampling must leave equally many words per prefix.");
        foreach (ulong value in new ulong[] { 0, 1, uint.MaxValue, ulong.MaxValue,
            VisibleSeedBlockPolicy.BlockCount - 1, VisibleSeedBlockPolicy.BlockCount,
            VisibleSeedBlockPolicy.BlockCount + 1, Beta110SeedCodec.SpaceSize - 1,
            Beta110SeedCodec.SpaceSize, VisibleSeedBlockPolicy.MinimumUnbiasedRandomValue })
            CheckRandomWord(value);
        ulong random = 0xbe927a0981d6f435UL;
        for (int i = 0; i < 4096; i++) CheckRandomWord(SplitMix64.Next(ref random));

        string temporaryBase = Path.GetFullPath(Path.GetTempPath());
        string fixtureRoot = Path.Combine(temporaryBase, "Rt2-OriginChecks-" + Guid.NewGuid().ToString("N"));
        string CursorFile(string root) => Path.Combine(root, "RolltheSpire2", "state", "search_cursor.json");
        SearchWorkspacePersistence Open(string root, bool initialize = true) =>
            new(root, RuntimeProfileId.Beta111, initializeSearchCursor: initialize);
        try
        {
            string freshRoot = Path.Combine(fixtureRoot, "fresh");
            var fresh = Open(freshRoot, initialize: false);
            Require(!fresh.Cursor.Initialized && !File.Exists(CursorFile(freshRoot)),
                "Opening an empty workspace generated an origin before the first search.");
            fresh.InitializeWorkbenchCursor();
            Require(fresh.Cursor.Initialized && fresh.CurrentPersistentOriginOrdinal == fresh.CurrentNextCursorOrdinal &&
                fresh.CurrentPersistentOriginOrdinal % VisibleSeedBlockPolicy.BlockSize == 0,
                "First-search initialization did not store one aligned origin and cursor.");
            byte[] firstSaved = File.ReadAllBytes(CursorFile(freshRoot));
            fresh.InitializeWorkbenchCursor();
            Require(firstSaved.AsSpan().SequenceEqual(File.ReadAllBytes(CursorFile(freshRoot))),
                "A later initialization randomized an already initialized cursor.");

            ulong next = checked(fresh.CurrentNextCursorOrdinal + 1);
            fresh.ObserveSafeNextCursor(next);
            fresh.FlushCursor();
            var resumed = Open(freshRoot);
            Require(resumed.CurrentNextCursorOrdinal == next && resumed.CurrentNextCursorOrdinal % VisibleSeedBlockPolicy.BlockSize == 1,
                "Saving or reopening progress aligned the next candidate and skipped work.");
            resumed.InitializeWorkbenchCursor();
            Require(resumed.CurrentNextCursorOrdinal == next, "Search continuation changed the saved in-block cursor.");

            // Existing nonaligned origins and cursors must survive upgrades as
            // exact values, including a previous end-of-space wrap count.
            string legacyRoot = Path.Combine(fixtureRoot, "existing");
            Directory.CreateDirectory(Path.GetDirectoryName(CursorFile(legacyRoot))!);
            byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1, Initialized = true, PersistentOrigin = 672520313344496122UL,
                NextCursor = 672576120277045754UL, WrapCount = 7
            });
            File.WriteAllBytes(CursorFile(legacyRoot), legacy);
            var existing = Open(legacyRoot);
            existing.InitializeWorkbenchCursor();
            Require(existing.CurrentPersistentOriginOrdinal == 672520313344496122UL &&
                existing.CurrentNextCursorOrdinal == 672576120277045754UL && existing.Cursor.WrapCount == 7,
                "An existing cursor was silently migrated to an aligned origin.");
            Require(legacy.AsSpan().SequenceEqual(File.ReadAllBytes(CursorFile(legacyRoot))),
                "Reading existing progress rewrote its saved cursor.");

            const string manualSeed = "12345678ABCD";
            Require(existing.TrySetPersistentOrigin(manualSeed, out string canonical, out string issue) &&
                canonical == manualSeed && issue.Length == 0 && existing.CurrentPersistentOriginSeed == manualSeed &&
                existing.CurrentNextCursorSeed == manualSeed && existing.CurrentNextCursorOrdinal % VisibleSeedBlockPolicy.BlockSize != 0,
                "A manually entered twelve-character origin was forced onto a block boundary.");
            for (int i = 0; i < 8; i++)
            {
                string chosen = existing.RandomizePersistentOrigin();
                Require(chosen.Length == 12 && chosen.EndsWith("0000", StringComparison.Ordinal) &&
                    existing.CurrentPersistentOriginOrdinal == existing.CurrentNextCursorOrdinal &&
                    existing.CurrentNextCursorOrdinal < Beta110SeedCodec.SpaceSize,
                    "Explicit Randomize did not reset origin and cursor to an aligned valid seed.");
            }

            // The policy changes only random origins. Existing terminal-wrap
            // persistence still preserves the origin and makes zero resumable.
            Require(existing.TrySetPersistentOrigin("ZZZZZZZZZZZZ", out _, out _), "Maximum manual seed was rejected.");
            long oldWrapCount = existing.Cursor.WrapCount;
            existing.ObserveSafeNextCursor(Beta110SeedCodec.SpaceSize);
            Require(existing.CurrentNextCursorOrdinal == Beta110SeedCodec.SpaceSize - 1,
                "An end sentinel was saved as an invalid visible cursor.");
            existing.CommitEndOfSpaceWrap();
            existing.FlushCursor();
            var wrapped = Open(legacyRoot);
            Require(wrapped.CurrentNextCursorOrdinal == 0 && wrapped.CurrentNextCursorSeed == "000000000000" &&
                wrapped.CurrentPersistentOriginSeed == "ZZZZZZZZZZZZ" && wrapped.Cursor.WrapCount == oldWrapCount + 1,
                "End-of-space wrap failed to preserve origin or resume from zero.");
            wrapped.InitializeWorkbenchCursor();
            Require(wrapped.CurrentNextCursorOrdinal == 0 && wrapped.Cursor.WrapCount == oldWrapCount + 1,
                "Reopening a wrapped cursor randomized it or counted another wrap.");
        }
        finally
        {
            string resolved = Path.GetFullPath(fixtureRoot);
            string expectedParent = temporaryBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Origin fixture cleanup escaped the temporary directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
        emit("RT2_ORIGIN_CHECKS " + JsonSerializer.Serialize(new { result = "PASS", assertions, randomWordCases,
            firstOriginAligned = true, explicitRandomAligned = true, existingAndManualPreserved = true,
            sequentialProgressPreserved = true, terminalWrapPreserved = true, fixtureScope = "IsolatedTemporaryDirectory" }));
    }
}
