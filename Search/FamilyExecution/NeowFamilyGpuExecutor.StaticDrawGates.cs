namespace RolltheSpire2.Search.FamilyExecution;

internal sealed partial class NeowFamilyGpuExecutor
{
    // Constructor capture and explicit same-binary controls are wired by the
    // primary executor. Reading the environment later would change its identity.
    private readonly bool _useStaticDrawGates;
    private NeowStaticDrawGates.Kinds _staticDenseDrawGates, _staticFusedDrawGates;
    internal static bool RequestStaticDrawGates => SearchOptimizationScope.Current.ExactDrawIntervals &&
        System.Environment.GetEnvironmentVariable("RT2_NEOW_STATIC_DRAW_GATE") != "0";
    internal const string LegacyStaticDrawGateRevision = "StaticDrawLegacy.20261021.v1";
    internal const string OptimizedStaticDrawGateRevision = "StaticDrawIntervals53.20261021.v1";
    internal static string RequestedStaticDrawGateRevision => RequestStaticDrawGates
        ? OptimizedStaticDrawGateRevision + ".Requested" : LegacyStaticDrawGateRevision;
    private NeowStaticDrawGates.Kinds DefaultStaticDrawGateKinds => _plan.UsesStagedDense && !_privateInput
        ? _staticDenseDrawGates : _staticFusedDrawGates;
    internal bool UsesStaticCurseDrawGate => (DefaultStaticDrawGateKinds & NeowStaticDrawGates.Kinds.Curse) != 0;
    internal bool UsesStaticLeafyDrawGate => (DefaultStaticDrawGateKinds & NeowStaticDrawGates.Kinds.Leafy) != 0;
    internal string StaticDrawGateRevision => NeowStaticDrawGates.Revision(DefaultStaticDrawGateKinds);
    internal string StaticDrawGateRevisionFor(bool compactAbi1Input) => NeowStaticDrawGates.Revision(
        compactAbi1Input || _privateInput || !_plan.UsesStagedDense ? _staticFusedDrawGates : _staticDenseDrawGates);

    private string ApplyStaticDrawGates(string source, int stage, NeowFamilyGpuPlan plan, bool enabled)
    {
        var result = NeowStaticDrawGates.Apply(source, stage, plan, enabled);
        if (stage == 0) _staticDenseDrawGates = result.Applied;
        else if (stage == 3) _staticFusedDrawGates = result.Applied;
        if (stage is 0 or 3)
            Bootstrap.RuntimeLog.TryBackgroundInfo($"nStaticDrawGates=true;stage={stage};requested={enabled};curse={(result.Applied & NeowStaticDrawGates.Kinds.Curse) != 0};leafy={(result.Applied & NeowStaticDrawGates.Kinds.Leafy) != 0};revision={NeowStaticDrawGates.Revision(result.Applied)}");
        return result.Source;
    }
}

internal static class NeowStaticDrawGates
{
    [Flags] internal enum Kinds { None = 0, Curse = 1, Leafy = 2 }
    internal readonly record struct Result(string Source, Kinds Applied);
    internal const int MaximumIntervalsPerTarget = 2;
    private const string CursePredicate = "draw_neow_curse(root,event_rng,curse_ordinal,curse) && (plan_value(69u)&(1u<<curse_ordinal))==0u";
    private const string LeafyCall = "return leafy_pre_gate_matches(root + plan_u64(37u),\n        conditions.values[o],conditions.values[o+1u],conditions.values[o+2u]);";
    private const string LeafyDeclaration = "bool leafy_pre_gate_matches(uint64_t seed, uint target_count, uint target0, uint target1) {";

    internal static string Revision(Kinds kinds) => kinds == Kinds.None ? NeowFamilyGpuExecutor.LegacyStaticDrawGateRevision :
        NeowFamilyGpuExecutor.OptimizedStaticDrawGateRevision +
        ((kinds & Kinds.Curse) != 0 ? ".Curse" : "") + ((kinds & Kinds.Leafy) != 0 ? ".Leafy" : "");

    internal static Result Apply(string source, int stage, NeowFamilyGpuPlan plan, bool enabled)
    {
        if (!enabled || plan.IsMultiplayer || stage is not (0 or 3)) return new(source, Kinds.None);
        string original = source;
        // Source resources may retain CRLF; normalize only this enabled variant.
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        Kinds applied = Kinds.None;
        if (stage == 0 && plan.UsesStagedDense && TryCurse(plan, out var curse))
        {
            const string anchor = "#if N_LOCAL_RESULTS\n";
            int insert = source.IndexOf(anchor, StringComparison.Ordinal);
            if (insert < 0 || Occurrences(source, CursePredicate) != 2)
                throw new InvalidOperationException("N.StaticDrawCurseSourceSeamChanged");
            string expression = curse!.GlslExpression("mantissa");
            string body = expression is "true" or "false" ? "return " + expression + ";" :
                "uint64_t state=root+plan_u64(31u)+make_u64(0x7f4a7c15u,0x9e3779b9u);\n" +
                "    uint64_t initial_s1=splitmix_next(state);\n" +
                "    uint64_t mantissa=(rotl64(initial_s1*uint64_t(5u),7u)*uint64_t(9u))>>11u;\n" +
                "    return " + expression + ";";
            // Only the stage0 Boolean filter loses its continuation state. The
            // fused matches/replay path still calls the original full RNG.
            source = source.Insert(insert, "// RT2_STATIC_DRAW_CURSE\nbool rt2_static_draw_curse(uint64_t root) {\n    " + body + "\n}\n\n")
                .Replace(CursePredicate, "rt2_static_draw_curse(root)", StringComparison.Ordinal);
            applied |= Kinds.Curse;
        }
        if (plan.UsesLeafyPreGate && source.Contains("#define RT2_LEAFY_GATE_LEGACY 0", StringComparison.Ordinal) &&
            source.Contains("#define N_LOCAL_RESULTS 1", StringComparison.Ordinal) &&
            TryLeafy(plan, out var strike0, out var strike1, out var defend0, out var defend1))
        {
            int insert = source.IndexOf(LeafyDeclaration, StringComparison.Ordinal);
            if (insert < 0 || Occurrences(source, LeafyCall) != 1)
                throw new InvalidOperationException("N.StaticDrawLeafySourceSeamChanged");
            // Keep the first/second correlation. Two separate union-membership
            // tests would wrongly accept target0+target0 or target1+target1.
            string helper = "// RT2_STATIC_DRAW_LEAFY\nbool rt2_static_draw_leafy(uint64_t seed) {\n" +
                "    uint64_t initial_s1;\n" +
                "    uint64_t first=leafy_pre_gate_first_draw(seed,initial_s1)>>11u;\n" +
                "    bool first0=" + strike0!.GlslExpression("first") + ";\n" +
                "    bool first1=" + strike1!.GlslExpression("first") + ";\n" +
                "    if(!first0 && !first1)return false;\n" +
                "    uint64_t second=leafy_pre_gate_second_draw(seed,initial_s1)>>11u;\n" +
                "    return (first0 && (" + defend1!.GlslExpression("second") + ")) ||\n" +
                "           (first1 && (" + defend0!.GlslExpression("second") + "));\n}\n\n";
            source = source.Insert(insert, helper).Replace(LeafyCall,
                "return rt2_static_draw_leafy(root + plan_u64(37u));", StringComparison.Ordinal);
            applied |= Kinds.Leafy;
        }
        return new(applied == Kinds.None ? original : source, applied);
    }

    internal static bool TryCurse(NeowFamilyGpuPlan plan, out StaticDrawGateCompiler.Gate? gate)
    {
        gate = null;
        if (plan.Meta.Length < 81 || plan.Meta[39] is 0 or > 10) return false;
        int count = (int)plan.Meta[39]; uint rejected = plan.Meta[69];
        return StaticDrawGateCompiler.TryCompileRanks(count,
            i => (rejected & (1u << i)) == 0 && plan.Meta[71 + i] != uint.MaxValue,
            MaximumIntervalsPerTarget, out gate);
    }

    internal static bool TryLeafy(NeowFamilyGpuPlan plan, out StaticDrawGateCompiler.Gate? strike0,
        out StaticDrawGateCompiler.Gate? strike1, out StaticDrawGateCompiler.Gate? defend0,
        out StaticDrawGateCompiler.Gate? defend1)
    {
        strike0 = strike1 = defend0 = defend1 = null;
        if (plan.Meta.Length <= 70 || plan.Conditions.Length <= 22 || plan.Conditions[20] != 2 ||
            plan.Meta[58] > int.MaxValue || plan.Meta[59] > int.MaxValue) return false;
        uint a = plan.Conditions[21], b = plan.Conditions[22];
        return StaticDrawGateCompiler.TryCompilePool(plan.Strike, (int)plan.Meta[58], [a], MaximumIntervalsPerTarget, out strike0) &&
            StaticDrawGateCompiler.TryCompilePool(plan.Strike, (int)plan.Meta[58], [b], MaximumIntervalsPerTarget, out strike1) &&
            StaticDrawGateCompiler.TryCompilePool(plan.Defend, (int)plan.Meta[59], [a], MaximumIntervalsPerTarget, out defend0) &&
            StaticDrawGateCompiler.TryCompilePool(plan.Defend, (int)plan.Meta[59], [b], MaximumIntervalsPerTarget, out defend1);
    }

    private static int Occurrences(string source, string value)
    {
        int count = 0;
        for (int at = 0; (at = source.IndexOf(value, at, StringComparison.Ordinal)) >= 0; at += value.Length) count++;
        return count;
    }
}
