using System.Reflection;
using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    // These immutable numerical fixtures exercise production executors. The
    // independent oracle performs a complete array shuffle and original route
    // replay; it never calls the new projection/planner to decide acceptance.
    private static void TestInternalFilterOrder(RenderingDevice rd)
    {
        TestInternalOrderPlanning();
        TestInternalOrderTransportScopes(rd);
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out string issue), issue);
        SearchBatch[] batches = [new(0, 262144), new(0, 0), new(zzfz, 1031),
            new(zzfz + 7, 513), new(PrefixHashCache.SpaceSize - 513, 513),
            new(PrefixHashCache.SpaceSize - 1, 1)];
        foreach (var fixture in InternalOrderFixtures())
            TestInternalOrderPublic(rd, fixture.Name, fixture.Plan, batches, fixture.Nonzero);
        TestInternalOrderPrivate(rd, InternalOrderPlan(14, 28, order: 0, authored: true), batches[0]);
        TestInternalOrderPrivate(rd, InternalOrderPlan(19, 28, order: 0, authored: true), batches[0]);
        Emit(new { phase = "internalFilterOrder", result = "PASS", realProductionExecutor = true,
            independentFullArrayCpuOracle = true, twentyEightRelicPool = true,
            nonzeroOutputs = true, exactSuccessfulRewardsCheckpoint = true,
            fixedAndEitherPickupOrder = true, finalCurseAndBans = true,
            duplicateCurseIds = true, unknownAdvanceFallback = true,
            denseCompactAndPrivate = true, zzfzAndLastSeed = true, cancellationAndReuse = true });
    }

    private static ulong InternalOrderU64(uint[] values, int at) => values[at] | (ulong)values[at + 1] << 32;

    private static void TestInternalOrderTransportScopes(RenderingDevice rd)
    {
        var ordinary = InternalOrderPlan();
        Require(NeowInternalFilterOrder.Compile(ordinary).Enabled,
            "The transport fallback fixture must have an otherwise eligible cost/dependency plan.");
        // Construct the production owner without evaluating invented joint
        // transformation metadata. This checks exclusion before module creation;
        // real joint semantic coverage belongs to its own query fixtures.
        var jointPlan = ordinary with { JointTransformMetaOffset = 0 };
        Require(!NeowInternalFilterOrder.Compile(jointPlan).Enabled &&
            NeowFamilyGpuExecutor.RequestedInternalFilterOrderRevisionFor(jointPlan, false) is null,
            "The joint transformation plan/requested identity bypassed the ordinary-route exclusion.");
        using var joint = new NeowFamilyGpuExecutor(rd, jointPlan, 42,
            useIndirectStaging: false, useInternalFilterOrder: true);
        Require(!joint.InternalFilterOrderEnabled && joint.InternalFilterOrderRevisionFor(false) is null,
            "A joint transformation composite enabled the ordinary-route reorder.");
        using var indirect = new NeowFamilyGpuExecutor(rd, ordinary, 42,
            useIndirectStaging: true, useInternalFilterOrder: true);
        Require(!indirect.InternalFilterOrderEnabled && indirect.InternalFilterOrderRevisionFor(false) is null,
            "An indirect transport enabled the unsupported four-stage direct reorder.");
        Emit(new { phase = "internalFilterOrderScopes", result = "PASS",
            jointCompositeExcludedBeforeShaderCreation = true, indirectTransportExcluded = true });
    }

    private static NeowFamilyGpuPlan InternalOrderPlan(uint first = 14, uint second = 28,
        int order = 0, bool authored = false, bool unknown = false)
    {
        var plan = Plan(false);
        uint[] meta = plan.Meta.ToArray(), conditions = new uint[authored ? 226 : 160];
        for (int source = 0; source < 32; source++)
        {
            conditions[source * 5 + 1] = uint.MaxValue;
            conditions[source * 5 + 2] = uint.MaxValue;
            conditions[source * 5 + 3] = uint.MaxValue;
        }
        uint[] bones = Enumerable.Range(1, 28).Select(i => (uint)i).ToArray();
        meta[8] = (uint)bones.Length;
        meta[9] = 3;
        meta[12] = order == 0 ? 255u : order > 0 ? first : second;
        meta[13] = order == 0 ? 255u : order > 0 ? second : first;
        meta[25] = (uint)Array.IndexOf(bones, first);
        meta[26] = (uint)Array.IndexOf(bones, second);
        meta[27] = first; meta[28] = second;
        // Use the actual production salt, instead of the historical smoke
        // fixture's uppercase Rewards spelling.
        ulong rewardsSalt = XxHash64.HashUtf8("rewards", 0);
        meta[33] = (uint)rewardsSalt; meta[34] = (uint)(rewardsSalt >> 32);
        meta[39] = 4; meta[68] = 1; meta[69] = 14;
        meta[71] = 5; meta[72] = 0; meta[73] = 2; meta[74] = 4;
        meta[48] = meta[49] = meta[52] = meta[53] = 0;
        ulong pair = (1UL << (int)first) | (1UL << (int)second);
        meta[50] = (uint)pair; meta[51] = (uint)(pair >> 32);
        meta[56] = 2; meta[60] = 0; meta[61] = 3;
        meta[66] = meta[67] = 1; meta[70] = 0;
        meta[81] = 255; meta[82] = 2; meta[83] = 1; meta[84] = 0;
        // Capsules have different known Niche arrivals in the two legal pickup
        // orders. This makes an incorrect AND instead of route OR observable.
        if (authored)
        {
            for (int source = 0; source < 2; source++)
            {
                int offset = 160 + source * 33;
                conditions[offset + 32] = 1;
                conditions[offset + (int)first] = 3;
                conditions[offset + (int)second] = 3;
                if (unknown) conditions[offset + 32] = uint.MaxValue;
            }
        }
        int trailer = 20 + (int)meta[9] * 6;
        uint[] pools = new uint[trailer + 15];
        pools[trailer + 6] = 8; pools[trailer + 7] = 1;
        pools[trailer + 8] = 9; pools[trailer + 9] = 0;
        pools[trailer + 12] = 0; pools[trailer + 13] = 8;
        uint[] cards = [500, 501, 502, 503, 504, 505, 506, 507, 500];
        return plan with { Meta = meta, PoolMeta = pools, Cards = cards,
            Bones = bones, Conditions = conditions, Defend = [100, 101, 102],
            HasAuthoredUpgrades = authored };
    }

    private static NeowFamilyGpuPlan InternalOrderCurseLists(NeowFamilyGpuPlan plan,
        uint[] generated, uint[] required, uint[] banned)
    {
        uint[] pools = plan.PoolMeta.ToArray();
        int trailer = 20 + (int)plan.Meta[9] * 6;
        pools[trailer + 12] = 0; pools[trailer + 13] = (uint)generated.Length;
        pools[trailer + 6] = (uint)generated.Length; pools[trailer + 7] = (uint)required.Length;
        pools[trailer + 8] = (uint)(generated.Length + required.Length); pools[trailer + 9] = (uint)banned.Length;
        return plan with { PoolMeta = pools, Cards = generated.Concat(required).Concat(banned).ToArray() };
    }

    private static IEnumerable<(string Name, NeowFamilyGpuPlan Plan, bool Nonzero)> InternalOrderFixtures()
    {
        yield return ("kaleidoscopeCapsule.eitherKnown", InternalOrderPlan(authored: true), true);
        yield return ("kaleidoscopeCapsule.fixedForward", InternalOrderPlan(order: 1, authored: true), true);
        yield return ("kaleidoscopeCapsule.fixedReverse", InternalOrderPlan(order: -1, authored: true), true);
        yield return ("newLeafCapsule.eitherKnown", InternalOrderPlan(19, 28, authored: true), true);
        yield return ("kaleidoscopeNewLeaf.independent", InternalOrderPlan(14, 19), true);
        yield return ("neutralSources.zeroAdvance", InternalOrderPlan(10, 13), true);
        yield return ("capsule.unknownAdvanceFallback", InternalOrderPlan(authored: true, unknown: true), true);
        yield return ("duplicateCurseIds.requiredAndBan", InternalOrderCurseLists(InternalOrderPlan(),
            [500, 501, 500, 502, 500, 503], [500, 500], [503, 503]), true);
        yield return ("requiredConjunction.impossible", InternalOrderCurseLists(InternalOrderPlan(),
            [500, 501, 500, 502], [500, 501], []), false);
        yield return ("requiredBanned.impossible", InternalOrderCurseLists(InternalOrderPlan(),
            [500, 501, 500, 502], [500], [500]), false);
    }

    private static void TestInternalOrderPlanning()
    {
        void Advances(NeowFamilyGpuPlan plan, params uint[] expected)
        {
            var compiled = NeowInternalFilterOrder.Compile(plan);
            Require(compiled.Enabled && compiled.NicheAdvances.Order().SequenceEqual(expected.Order()),
                "The internal ordering planner changed legal route arrivals: " + compiled.Reason);
        }
        Advances(InternalOrderPlan(authored: true), 5, 7);
        Advances(InternalOrderPlan(order: 1, authored: true), 7);
        Advances(InternalOrderPlan(order: -1, authored: true), 5);
        Advances(InternalOrderPlan(19, 28, authored: true), 2, 4);
        Advances(InternalOrderPlan(14, 19), 5);
        Advances(InternalOrderPlan(10, 13), 0);
        var either = InternalOrderPlan(authored: true);
        int forwardOnly = 0, reverseOnly = 0;
        for (ulong root = 0; root < 1024; root++)
        {
            bool forward = InternalOrderRouteOracle(either, root, 14, 28);
            bool reverse = InternalOrderRouteOracle(either, root, 28, 14);
            forwardOnly += forward && !reverse ? 1 : 0;
            reverseOnly += reverse && !forward ? 1 : 0;
        }
        Require(forwardOnly > 0 && reverseOnly > 0,
            "The either-order fixture did not distinguish the two different legal Niche arrivals.");
        var duplicate = InternalOrderCurseLists(InternalOrderPlan(), [500, 501, 500, 502], [500, 500], [502]);
        var duplicatePlan = NeowInternalFilterOrder.Compile(duplicate);
        Require(Math.Abs(duplicatePlan.AcceptedFraction - .5) < 1e-12,
            "Duplicate curse positions, repeated required conjunction or bans changed planner probability.");
        var impossible = NeowInternalFilterOrder.Compile(InternalOrderCurseLists(InternalOrderPlan(),
            [500, 501, 500, 502], [500, 501], []));
        Require(impossible.AcceptedFraction == 0, "Two distinct required final curses were treated as OR.");
        var banned = NeowInternalFilterOrder.Compile(InternalOrderCurseLists(InternalOrderPlan(),
            [500, 501, 500, 502], [500], [500]));
        Require(banned.AcceptedFraction == 0, "A required and banned final curse was accepted.");
        var off = new List<(string Name, NeowFamilyGpuPlan Plan)>
        {
            ("unknownArrival", InternalOrderPlan(authored: true, unknown: true)),
            ("directNested", InternalOrderPlan() with { DirectNestedMode = 1 })
        };
        void Mutate(string name, Action<uint[]> change)
        {
            var plan = InternalOrderPlan(); change(plan.Meta); off.Add((name, plan));
        }
        Mutate("party", m => m[6] = 2);
        Mutate("nonzeroSlot", m => m[5] = 1);
        Mutate("nonStaged", m => m[66] = 0);
        Mutate("untracked", m => m[67] = 0);
        Mutate("noFinalCurse", m => m[83] = 0);
        Mutate("badTrackedIndex", m => m[25] = m[8]);
        Mutate("mismatchedTrackedIdentity", m => m[27] = 13);
        Mutate("partialOrder", m => m[12] = 14);
        Mutate("wrongFixedPair", m => { m[12] = 14; m[13] = 13; });
        foreach (var fixture in off)
            Require(!NeowInternalFilterOrder.Compile(fixture.Plan).Enabled,
                "An unproved route enabled internal order projection: " + fixture.Name);
        Emit(new { phase = "internalFilterOrderPlanner", result = "PASS", eitherRouteOr = true,
            exactGeneratedPoolMultiplicity = true, requiredIsConjunction = true, bannedWins = true,
            knownAuthoredOffsets = true, forwardOnly, reverseOnly, unprovedScopesDisabled = off.Count });
    }

    private readonly record struct InternalOrderPair(uint A, uint B, UpFrontRngCheckpoint Rewards);

    private static bool InternalOrderPairOracle(NeowFamilyGpuPlan plan, ulong root, out InternalOrderPair pair)
    {
        var eventRng = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 31)));
        int curseOrdinal = eventRng.NextInt((int)plan.Meta[39]);
        uint bit = 1u << curseOrdinal;
        if ((plan.Meta[69] & bit) != 0 || plan.Meta[71 + curseOrdinal] == uint.MaxValue)
        { pair = default; return false; }
        Require((plan.Meta[68] & bit) != 0, "The independent fixture oracle requires a proved top-identity curse.");
        var rewards = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 33)));
        uint[] pool = plan.Bones.ToArray(); rewards.UnstableShuffle(pool.AsSpan());
        uint a = pool[0], b = pool[1];
        ulong mask = 1UL << (int)a | 1UL << (int)b;
        ulong any = InternalOrderU64(plan.Meta, 48), all = InternalOrderU64(plan.Meta, 50), ban = InternalOrderU64(plan.Meta, 52);
        if ((any != 0 && (mask & any) == 0) || (mask & all) != all || (mask & ban) != 0)
        { pair = default; return false; }
        if (plan.Meta[12] != 255 && plan.Meta[13] != 255)
        {
            if (mask != ((1UL << (int)plan.Meta[12]) | (1UL << (int)plan.Meta[13])))
            { pair = default; return false; }
            a = plan.Meta[12]; b = plan.Meta[13];
        }
        pair = new(a, b, rewards.CaptureCheckpoint()); return true;
    }

    private static uint InternalOrderAuthoredAdvance(NeowFamilyGpuPlan plan, uint source, uint prior)
    {
        if (!plan.HasAuthoredUpgrades || source is not (3 or 28)) return 0;
        return plan.Conditions[160 + (source == 28 ? 0 : 33) + (prior == 255 ? 32 : (int)prior)];
    }

    private static bool InternalOrderRouteOracle(NeowFamilyGpuPlan plan, ulong root, uint a, uint b)
    {
        uint da = InternalOrderAuthoredAdvance(plan, a, 255), db = InternalOrderAuthoredAdvance(plan, b, a);
        // This is the original necessary-condition fallback, not a guessed
        // arrival. It is essential that the new prefilter also preserves it.
        if (da == uint.MaxValue || db == uint.MaxValue) return true;
        var niche = new Beta110FastRng(unchecked(root + InternalOrderU64(plan.Meta, 35)));
        foreach (var item in new[] { (Source: a, Advance: da), (Source: b, Advance: db) })
        {
            if (item.Source == 14 && (plan.Meta[82] & 3) != 0)
                for (int pass = 0; pass < 2; pass++)
                for (int count = (int)plan.Meta[9]; count > 1; count--) niche.NextInt(count);
            if (item.Source == 19 && (plan.Meta[82] & 2) != 0)
            {
                if (plan.Meta[61] == 0) return false;
                niche.NextInt((int)plan.Meta[61]);
            }
            for (uint draw = 0; draw < item.Advance; draw++) niche.NextInt(2);
        }
        if (plan.Meta[83] == 0) return true;
        int trailer = 20 + (int)plan.Meta[9] * 6;
        int generated = (int)plan.PoolMeta[trailer + 12], countGenerated = (int)plan.PoolMeta[trailer + 13];
        if (countGenerated == 0) return false;
        uint curse = plan.Cards[generated + niche.NextInt(countGenerated)];
        int required = (int)plan.PoolMeta[trailer + 6], countRequired = (int)plan.PoolMeta[trailer + 7];
        int banned = (int)plan.PoolMeta[trailer + 8], countBanned = (int)plan.PoolMeta[trailer + 9];
        for (int index = 0; index < countRequired; index++) if (plan.Cards[required + index] != curse) return false;
        for (int index = 0; index < countBanned; index++) if (plan.Cards[banned + index] == curse) return false;
        return true;
    }

    private static bool InternalOrderLocalOracle(NeowFamilyGpuPlan plan, ulong root, InternalOrderPair pair) =>
        InternalOrderRouteOracle(plan, root, pair.A, pair.B) || plan.Meta[12] == 255 &&
            InternalOrderRouteOracle(plan, root, pair.B, pair.A);

    private static ulong[] InternalOrderExpected(NeowFamilyGpuPlan plan, SearchBatch batch, IEnumerable<ulong>? ordinals = null)
    {
        var accepted = new List<ulong>();
        foreach (ulong ordinal in ordinals ?? Enumerable.Range(0, batch.BatchCandidateCount).Select(i => (ulong)i))
        {
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            if (InternalOrderPairOracle(plan, root, out var pair) && InternalOrderLocalOracle(plan, root, pair)) accepted.Add(ordinal);
        }
        return accepted.Order().ToArray();
    }

    private static void CheckInternalOrderPairCheckpoints(RenderingDevice rd, NeowFamilyGpuExecutor executor,
        NeowFamilyGpuPlan plan, SearchBatch batch, uint count)
    {
        var field = typeof(NeowFamilyGpuExecutor).GetField("_stagedBuffers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("The pair checkpoint inspection seam changed.");
        Rid[] buffers = (Rid[])field.GetValue(executor)!;
        uint[] records = count == 0 ? [] : FamilyGpuComputeUtility.FromUInt32Bytes(
            rd.BufferGetData(buffers[12], 0, count * 48), count * 12);
        var seen = new HashSet<uint>();
        for (int at = 0; at < records.Length; at += 12)
        {
            uint ordinal = records[at];
            Require(ordinal < batch.BatchCandidateCount && seen.Add(ordinal), "An internal-order pair record repeated or escaped its batch.");
            ulong root = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinal), 0);
            Require(InternalOrderPairOracle(plan, root, out var expected), "A failed CPU full-array pair leaked a checkpoint.");
            ulong Join(int offset) => records[at + offset] | (ulong)records[at + offset + 1] << 32;
            var state = new UpFrontRngCheckpoint(Join(4), Join(6), Join(8), Join(10));
            Require(Join(1) == root && records[at + 3] == (expected.A | expected.B << 8) && state == expected.Rewards,
                "Internal ordering changed retained roots, acquisition order or exact successful Rewards state.");
        }
    }

    private static void TestInternalOrderPublic(RenderingDevice rd, string name, NeowFamilyGpuPlan plan,
        IReadOnlyList<SearchBatch> batches, bool requireNonzero)
    {
        foreach (bool privateOutput in new[] { false, true })
        {
            using var oldOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
            using var newOutput = privateOutput ? new PrivateOrdinalBuffer(rd) : null;
            using var control = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: oldOutput,
                useIndirectStaging: false, useInternalFilterOrder: false);
            using var optimized = new NeowFamilyGpuExecutor(rd, plan, 42, privateOutput: newOutput,
                useIndirectStaging: false, useInternalFilterOrder: true);
            int nonzero = 0; long retainedPairs = 0, originalPairs = 0; bool observedExtraStage = false;
            foreach (SearchBatch batch in batches)
            {
                ulong[] expected = InternalOrderExpected(plan, batch);
                var input = FamilyCandidateSet.Dense(batch);
                var a = RunPrefixScope(control, oldOutput, input, CancellationToken.None, out var am);
                var b = RunPrefixScope(optimized, newOutput, input, CancellationToken.None, out var bm);
                Require(a.ExportAbi1().Span.SequenceEqual(expected) && b.ExportAbi1().Span.SequenceEqual(expected),
                    "Internal filter order dense final population disagreed with the independent CPU oracle: " + name);
                Require(am.Processed == bm.Processed && am.Survivors == bm.Survivors &&
                    am.CurseSurvivors == bm.CurseSurvivors && bm.PairSurvivors <= am.PairSurvivors &&
                    am.ReadbackBytes == bm.ReadbackBytes && control.ResidentBytes == optimized.ResidentBytes,
                    "Internal filter order changed transport, final population or original curse counter: " + name);
                uint[] ah = PrefixScopeHeader(rd, control), bh = PrefixScopeHeader(rd, optimized);
                Require(ah[0] == bh[0] && ah[1] == bh[1] && ah[2] == bh[2] && ah[3] == 0 && bh[3] == 0,
                    "Internal ordering changed header ABI, output count or error validation: " + name);
                if (batch.BatchCandidateCount > 0)
                {
                    CheckInternalOrderPairCheckpoints(rd, control, plan, batch, am.PairSurvivors);
                    CheckInternalOrderPairCheckpoints(rd, optimized, plan, batch, bm.PairSurvivors);
                    retainedPairs += bm.PairSurvivors;
                    originalPairs += am.PairSurvivors;
                    if (optimized.InternalFilterOrderEnabled)
                    {
                        Require(am.Dispatches == 3 && bm.Dispatches == 4,
                            "The enabled projection did not execute the production four-stage chain: " + name);
                        observedExtraStage = true;
                    }
                    else Require(am.Dispatches == bm.Dispatches, "An inapplicable projection changed dispatches: " + name);
                }
                nonzero += expected.Length > 0 ? 1 : 0;
                if (privateOutput || batch.BatchCandidateCount == 0) continue;
                ulong[] sparse = new ulong[] { 0, (ulong)Math.Min(7, batch.BatchCandidateCount - 1),
                    (ulong)batch.BatchCandidateCount - 1 }.Concat(expected.Take(5)).Distinct().Order().ToArray();
                var compact = FamilyCandidateSet.FromSortedAbi1(batch, sparse);
                var ac = control.Execute(compact, CancellationToken.None, out var acm);
                var bc = optimized.Execute(compact, CancellationToken.None, out var bcm);
                Require(ac.ExportAbi1().Span.SequenceEqual(InternalOrderExpected(plan, batch, sparse)) &&
                    ac.ExportAbi1().Span.SequenceEqual(bc.ExportAbi1().Span) && acm.Dispatches == 1 && bcm.Dispatches == 1,
                    "The compact fallback changed final population or dispatch ABI: " + name);
                Require(optimized.InternalFilterOrderRevisionFor(true) == control.InternalFilterOrderRevisionFor(true),
                    "Compact fallback claimed the staged projection identity: " + name);
            }
            Require(!requireNonzero || nonzero > 0 && retainedPairs > 0,
                "An internal-order parity fixture did not exercise any successful payload/checkpoint: " + name);
            Require(!control.InternalFilterOrderEnabled, "The explicit control silently enabled internal ordering.");
            bool admitted = NeowInternalFilterOrder.Compile(plan).Enabled;
            Require(optimized.InternalFilterOrderEnabled == admitted && (!admitted || observedExtraStage),
                "Requested versus actual projection identity did not match planner/dispatch: " + name);
            Require(!admitted || retainedPairs < originalPairs,
                "An admitted projection did not reduce any actual full-pair work: " + name);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var retry = FamilyCandidateSet.Dense(batches[0]);
            long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
            Expect<OperationCanceledException>(() => RunPrefixScope(optimized, newOutput, retry, cancelled.Token, out _));
            Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks, "A cancelled internal-order batch submitted GPU work.");
            Require(RunPrefixScope(optimized, newOutput, retry, CancellationToken.None, out _).ExportAbi1().Span
                .SequenceEqual(InternalOrderExpected(plan, retry.Batch)), "Internal ordering failed cancellation/reuse: " + name);
            Emit(new { phase = "internalFilterOrderPublic", result = "PASS", fixture = name, privateOutput,
                nonzeroBatches = nonzero, retainedPairs, enabled = optimized.InternalFilterOrderEnabled,
                exactRewardsCheckpoint = true, independentCpuOracle = true, compactFallback = true });
        }
    }

    private static void TestInternalOrderPrivate(RenderingDevice rd, NeowFamilyGpuPlan plan, SearchBatch batch)
    {
        using var input = new PrivateOrdinalBuffer(rd);
        using var oldOutput = new PrivateOrdinalBuffer(rd);
        using var newOutput = new PrivateOrdinalBuffer(rd);
        using var control = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: input, privateOutput: oldOutput,
            useIndirectStaging: false, useInternalFilterOrder: false);
        using var optimized = new NeowFamilyGpuExecutor(rd, plan, 42, privateInput: input, privateOutput: newOutput,
            useIndirectStaging: false, useInternalFilterOrder: true);
        ulong[] full = InternalOrderExpected(plan, batch);
        Require(full.Length > 0, "The private input fixture has no successful CPU candidates.");
        uint[] sparse = full.Take(7).Concat(new ulong[] { (ulong)batch.BatchCandidateCount - 1, 0, 7 })
            .Distinct().Reverse().Select(i => (uint)i).ToArray();
        FamilyCandidateSet Run(NeowFamilyGpuExecutor executor, PrivateOrdinalBuffer output, int count,
            CancellationToken token, out NeowFamilyGpuMetrics metrics)
        {
            int survivors = executor.ExecutePrivateStage(batch, count, token, out metrics);
            return output.ReadForVerification(batch, survivors);
        }
        FamilyGpuComputeUtility.Update(rd, input.Buffer, sparse, "InternalOrder.PrivateInput");
        var a = Run(control, oldOutput, sparse.Length, CancellationToken.None, out var am);
        var b = Run(optimized, newOutput, sparse.Length, CancellationToken.None, out var bm);
        Require(a.ExportAbi1().Span.SequenceEqual(InternalOrderExpected(plan, batch, sparse.Select(i => (ulong)i))) &&
            a.ExportAbi1().Span.SequenceEqual(b.ExportAbi1().Span) && am.Dispatches == 1 && bm.Dispatches == 1 &&
            !optimized.InternalFilterOrderEnabled &&
            PrefixScopeHeader(rd, control).AsSpan().SequenceEqual(PrefixScopeHeader(rd, optimized)),
            "Private unsorted sparse input changed the original fallback output/header.");
        FamilyGpuComputeUtility.Update(rd, input.Buffer, [0u, (uint)batch.BatchCandidateCount, 7u], "InternalOrder.BadInput");
        Expect<InvalidDataException>(() => Run(control, oldOutput, 3, CancellationToken.None, out _));
        Expect<InvalidDataException>(() => Run(optimized, newOutput, 3, CancellationToken.None, out _));
        uint[] valid = full.Take(3).Select(i => (uint)i).ToArray();
        FamilyGpuComputeUtility.Update(rd, input.Buffer, valid, "InternalOrder.PrivateReuse");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        long before = FamilyGpuComputeUtility.CompletedSubmissionTicks;
        Expect<OperationCanceledException>(() => Run(optimized, newOutput, valid.Length, cancelled.Token, out _));
        Require(before == FamilyGpuComputeUtility.CompletedSubmissionTicks &&
            Run(optimized, newOutput, valid.Length, CancellationToken.None, out _).ExportAbi1().Span
                .SequenceEqual(valid.Select(i => (ulong)i).ToArray()), "Private internal ordering failed bad-input/cancellation reuse.");
        Emit(new { phase = "internalFilterOrderPrivate", result = "PASS", successfulPayload = valid.Length,
            privateInputIsFallback = true, unorderedSparseInput = true, badOrdinalAndCancellationReuse = true });
    }
}
