using RolltheSpire2.Search.FamilyExecution;

namespace RolltheSpire2.Search.FamilyExecution;

internal sealed class WorldFamilyGpuPlan
{
    // Scratch is bounded by the actual pool, not the authored event horizon.
    // Oversized FirstN observes the entire pool; an absent ExactSlot rejects.
    internal static bool Supports(WorldFamilyReplay replay) => replay.Plan.ActSelectionGroups.Length <= 8 &&
        replay.Plan.SharedAncients.Length <= 512 && replay.Plan.MaxEncounterPoolCount <= 512 &&
        replay.Plan.Acts.All(a => a.EventCandidates.Length <= 512);
    internal uint[][] Buffers { get; }
    internal int Capacity { get; }
    internal int GroupingK { get; } = 4;
    internal long GroupingWorkspaceBytes => (GroupingK - 1) * (Capacity * 8L + 64) + (GroupingK > 1 ? GroupingK * 32 : 0);
    internal int ScratchWords { get; }
    private readonly bool _soloConstants;
    private readonly bool _sharedPrefixRequested = SharedVisibleSeedGpuCache.Requested;
    internal long ScratchBytes => Math.Max(4, Capacity * ScratchWords * 4L);
    internal long WorkspaceBytes => Capacity * 8L + ScratchBytes + 64 + GroupingWorkspaceBytes +
        Buffers.Where((_, i) => i is not (0 or 12 or 16 or 17 or 18)).Sum(b => Math.Max(4, b.Length * 4L)) +
        (_sharedPrefixRequested ? SharedVisibleSeedGpuCache.Bytes : 0);
    internal string Revision(bool compact) => "W.World.Gpu." + (VariantOnly ? "Variant" : "Progression") +
        (compact ? ".CompactAbi1" : ".Dense") + $".K{GroupingK}." +
        (_soloConstants ? "SoloConstants.CanonicalAbi1Ready.20260929.v3" : "CanonicalAbi1Ready.20260907.v2") +
        (_sharedPrefixRequested ? "." + SharedVisibleSeedGpuCache.Revision : "");
    internal bool VariantOnly { get; }
    internal WorldFamilyGpuPlan(WorldFamilyReplay replay, bool specializeSolo = false)
    {
        var w = replay.Plan;
        VariantOnly = w.MaxRequiredAct == 0;
        _soloConstants = specializeSolo && !VariantOnly;
        WorldFamilyGpuPacking.PackWorldPlan(w, out var sm, out var si, out var map,
            out var acts, out var encounters, out var conflicts, out var bosses, out var predicates, out var targets);
        var ps = predicates.ToList(); var ids = targets.ToList();
        var extra = new List<uint>(); var data = new List<uint>();
        uint Store(IEnumerable<uint> values) { uint offset = (uint)data.Count; data.AddRange(values); return offset; }
        uint Pred(IEnumerable<WorldFastDenseSetPredicate> values)
        {
            WorldFamilyGpuPacking.PackPredicates(values.ToArray(), ps, ids, out int offset, out _);
            return (uint)offset;
        }
        // Per Act: variantAllowed, eventOffset/count, H, eventPredOffset/count,
        // ancientOffset, ancientPredOffset/count, branchOffset/count, hasBoss.
        foreach (var act in w.Acts)
        {
            uint events = Store(act.EventCandidates.Select(c => (uint)c.EventId | (uint)c.Source << 16 | (c.StaticEligible ? 1u << 24 : 0)));
            uint eventPred = (uint)data.Count;
            foreach (var p in act.EventPredicates) data.AddRange([p.RangeMode,p.RangeValue,p.SourceFilter,Pred([p.Keys])]);
            uint ancients = Store(act.Ancients.Select(i => (uint)i));
            var ancientPreds = act.AncientIdentityPredicates.Concat(act.AncientBranchIdentityPredicates).ToArray();
            uint ancientPred = Pred(ancientPreds);
            uint branches = (uint)data.Count;
            foreach (var b in act.FamilyBossBranches) data.AddRange([Pred([b.First]),Pred([b.Second])]);
            extra.AddRange([act.FamilyVariantAllowed?1u:0u,events,(uint)act.EventCandidates.Length,
                act.EventPredicates.Length==0?0u:act.EventPredicates.Max(p=>(uint)p.RangeValue),eventPred,(uint)act.EventPredicates.Length,
                ancients,ancientPred,(uint)ancientPreds.Length,branches,(uint)act.FamilyBossBranches.Length,act.HasBossPredicate?1u:0u]);
        }
        uint sharedOffset = Store(w.SharedAncients.Select(i => (uint)i));
        bool needsEncounters = w.Acts.Any(a => a.Act <= w.MaxRequiredAct &&
            (a.Act < w.MaxRequiredAct || a.HasBossPredicate || a.HasAncientPredicate || w.RequiresSecondBoss));
        int encounterStride = needsEncounters ? w.MaxEncounterPoolCount : 0;
        int eventStride = w.Acts.Max(a => a.EventCandidates.Length);
        ScratchWords = encounterStride + eventStride;
        // 32 MiB scratch ceiling independent of the external 2^24 root batch.
        Capacity = Math.Min(1 << 20, Math.Max(64, (32 * 1024 * 1024 / 4 / Math.Max(1, ScratchWords) / 64) * 64));
        uint[] meta = [0,(uint)replay.BucketLengths.Length,(uint)w.ActSelectionGroups.Length,(uint)map.Length,
            (uint)w.Acts.Length,(uint)w.SharedAncients.Length,(uint)w.MaxRequiredAct,w.RequiresSecondBoss?1u:0u,
            (uint)encounterStride,(uint)(ps.Count/7),0,w.Act1OverrideId==ushort.MaxValue?uint.MaxValue:w.Act1OverrideId,
            (uint)w.Ascension,(uint)eventStride,sharedOffset];
        // Bindings 0/12/16/17/18 are private dynamic batch/scratch/header/output/input.
        Buffers = [[],replay.BucketLengths,meta,sm,si,map,acts,encounters,conflicts,bosses,ps.ToArray(),ids.ToArray(),[],extra.ToArray(),data.ToArray(),[],[],[],[]];
    }

    internal string ShaderSource()
    {
        string donor = FamilyGpuComputeUtility.LoadEmbeddedShader("Beta110GpuWorldBossG1.comp.glsl");
        // Retain the actual donor numerical implementation, not a second copy.
        string rng = donor[donor.IndexOf("uint64_t s0;",StringComparison.Ordinal)..donor.IndexOf("void write_shared_state",StringComparison.Ordinal)];
        string numerical = donor[donor.IndexOf("bool encounter_eligible",StringComparison.Ordinal)..donor.IndexOf("void main()",StringComparison.Ordinal)];
        string body=FamilyGpuComputeUtility.LoadFamilyShaderWithVisibleSeedRootHash("WorldFamily.comp.glsl")
            .Replace("/*__W_DONOR__*/",rng+numerical,StringComparison.Ordinal)
            .Replace("uint sharedIds[512]",$"uint sharedIds[{Math.Max(1,Buffers[2][5])}]",StringComparison.Ordinal)
            .Replace("uint maxAct=plan_meta_buffer.values[6u];", VariantOnly ? "return true; uint maxAct=0u;" : "uint maxAct=plan_meta_buffer.values[6u];",StringComparison.Ordinal);
        if (_soloConstants)
        {
            // These packed values cannot change during an executor's lifetime.
            // No target identity or pool member becomes a shader constant.
            const string horizon="uint maxAct=plan_meta_buffer.values[6u];", second="bool second=plan_meta_buffer.values[7u]!=0u;";
            if (!body.Contains(horizon,StringComparison.Ordinal) || !body.Contains(second,StringComparison.Ordinal))
                throw new InvalidOperationException("W.SoloConstantsShaderSeam");
            body=body.Replace(horizon,
                "uint maxAct="+Buffers[2][6].ToString(System.Globalization.CultureInfo.InvariantCulture)+"u;",StringComparison.Ordinal)
                .Replace(second,
                    "bool second="+(Buffers[2][7]!=0 ? "true" : "false")+";",StringComparison.Ordinal);
        }
        return body;
    }
}
