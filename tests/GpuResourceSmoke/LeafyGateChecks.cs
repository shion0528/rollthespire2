using Godot;
using RolltheSpire2.Core.Rng;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private const int LeafyPoolLength = 65536;
    private const int LeafyInputWords = 12, LeafyOutputWords = 16;

    private readonly record struct LeafyGateCase(ulong Root, ulong Salt, uint StrikeLength,
        uint DefendLength, uint TargetCount, uint Target0, uint Target1, uint Bound0, uint Bound1);

    // Invoked by the main smoke runner; it compiles the production helpers in
    // both macro modes. No game state, probability estimates or Exact bypasses.
    private static void TestLeafyGate(RenderingDevice rd)
    {
        uint[] strikes = new uint[LeafyPoolLength], defends = new uint[LeafyPoolLength];
        for (int i = 0; i < LeafyPoolLength; i++)
        {
            // Deliberate duplicate IDs and sentinel-valued targets/pool entries.
            strikes[i] = i % 1021 == 0 ? uint.MaxValue : (uint)(i % 257);
            defends[i] = i % 1031 == 0 ? uint.MaxValue : (uint)((i * 37 + 11) % 257);
        }
        var cases = CreateLeafyGateCases();
        uint[] inputs = new uint[checked(cases.Count * LeafyInputWords)];
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i]; int o = i * LeafyInputWords;
            inputs[o] = (uint)c.Root; inputs[o + 1] = (uint)(c.Root >> 32);
            inputs[o + 2] = (uint)c.Salt; inputs[o + 3] = (uint)(c.Salt >> 32);
            inputs[o + 4] = c.StrikeLength; inputs[o + 5] = c.DefendLength;
            inputs[o + 6] = c.TargetCount; inputs[o + 7] = c.Target0; inputs[o + 8] = c.Target1;
            inputs[o + 9] = c.Bound0; inputs[o + 10] = c.Bound1;
        }
        uint[]? firstMode = null;
        foreach (bool legacy in new[] { false, true })
        {
            var owned = new List<Rid>();
            Rid Own(Rid rid) { Require(rid.IsValid, "Leafy probe allocation failed."); owned.Add(rid); return rid; }
            try
            {
                Rid Buffer(uint[] values) => Own(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(values)));
                uint[] conditions = new uint[160];
                conditions[20] = 2; conditions[21] = 101; conditions[22] = 202;
                Rid output = Own(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd,
                    checked(cases.Count * LeafyOutputWords * sizeof(uint)), "LeafyGateProbe.Output"));
                Rid[] buffers = [Buffer([(uint)cases.Count]), Buffer(new uint[128]), Buffer([0]), Buffer([0]),
                    Buffer(strikes), Buffer(defends), Buffer(new uint[8]), output, Buffer([0]),
                    Buffer(conditions), Buffer(inputs)];
                Rid shader = Own(FamilyGpuComputeUtility.CompileShader(rd, LeafyGateProbeSource(legacy),
                    "LeafyGateProbe." + (legacy ? "Legacy" : "Lazy")));
                Rid pipeline = Own(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader));
                Rid uniforms = Own(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers));
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, pipeline);
                rd.ComputeListBindUniformSet(list, uniforms, 0);
                rd.ComputeListDispatch(list, checked(((uint)cases.Count + 63u) / 64u), 1, 1);
                rd.ComputeListEnd();
                FamilyGpuComputeUtility.SubmitAndSync(rd, "Smoke.LeafyGateProbe");
                uint[] actual = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(output),
                    checked((uint)(cases.Count * LeafyOutputWords)));
                ValidateLeafyGateResults(cases, strikes, defends, actual, legacy);
                if (firstMode is null) firstMode = actual;
                else Require(firstMode.AsSpan().SequenceEqual(actual), "Leafy gate macro modes changed probe output bits.");
            }
            finally { FamilyGpuComputeUtility.FreeAll(rd, owned); }
        }
        Emit(new { phase = "leafyGate", result = "PASS", cases = cases.Count, macroModes = 2,
            exhaustivePoolLengths = "0..65536 in each position", randomCases = 65536,
            evidence = "ProductionHelpers;CanonicalCpuRng;Raw64BitFirstSecondDraws;ExactMantissaMapping;RootSaltOverflow;DuplicateTargetsAndPoolIds;LegacyParity" });
    }

    private static List<LeafyGateCase> CreateLeafyGateCases()
    {
        var cases = new List<LeafyGateCase>(200000);
        ulong random = 0x8ca6e9bafedc0123UL;
        ulong Next() => SplitMix64.Next(ref random);
        void Add(uint strike, uint defend, uint count = 2, uint? target0 = null, uint? target1 = null,
            ulong? root = null, ulong? salt = null, uint? bound0 = null, uint? bound1 = null)
        {
            uint a = target0 ?? (uint)(Next() % 257);
            uint b = target1 ?? (cases.Count % 3 == 0 ? a : (uint)(Next() % 257));
            cases.Add(new(root ?? Next(), salt ?? Next(), strike, defend, count, a, b,
                bound0 ?? Math.Max(1u, strike), bound1 ?? Math.Max(1u, defend)));
        }
        for (uint length = 0; length <= LeafyPoolLength; length++)
        {
            Add(length, 257);
            Add(257, length);
        }
        for (int i = 0; i < 65536; i++)
            Add((uint)(Next() % (LeafyPoolLength + 1)), (uint)(Next() % (LeafyPoolLength + 1)),
                count: (i % 16) switch { 0 => 0u, 1 => 1u, 2 => 3u, _ => 2u });
        ulong[] edges = [0, 1, ulong.MaxValue, ulong.MaxValue - 1, 0x9e3779b97f4a7c15UL,
            unchecked(0UL - 0x9e3779b97f4a7c15UL), 1UL << 63, uint.MaxValue];
        uint[] bounds = [0, 1, 2, 257, 65535, 65536, 0x80000000, uint.MaxValue];
        foreach (ulong root in edges)
        foreach (ulong salt in edges)
        for (int i = 0; i < bounds.Length; i++)
            Add(i % 2 == 0 ? 1u : 257u, i % 3 == 0 ? 1u : 65536u, target0: uint.MaxValue,
                target1: i % 2 == 0 ? uint.MaxValue : 101u, root: root, salt: salt,
                bound0: bounds[i], bound1: bounds[bounds.Length - 1 - i]);
        return cases;
    }

    private static void ValidateLeafyGateResults(IReadOnlyList<LeafyGateCase> cases, uint[] strikes,
        uint[] defends, uint[] actual, bool legacy)
    {
        uint Index(ulong draw, uint bound) => (uint)(((UInt128)(draw >> 11) * bound) >> 53);
        bool Matches(LeafyGateCase c, uint count, uint t0, uint t1, ulong first, ulong second)
        {
            if (c.StrikeLength == 0 || c.DefendLength == 0) return false;
            uint a = strikes[Index(first, c.StrikeLength)], b = defends[Index(second, c.DefendLength)];
            return count == 1 ? a == t0 || b == t0 : (a == t0 && b == t1) || (a == t1 && b == t0);
        }
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i]; int o = i * LeafyOutputWords;
            var rng = new Xoshiro256StarStar(unchecked(c.Root + c.Salt));
            ulong first = rng.NextUInt64(), second = rng.NextUInt64();
            uint expected = Matches(c, c.TargetCount, c.Target0, c.Target1, first, second) ? 1u : 0u;
            uint wrapper = Matches(c, 2, 101, 202, first, second) ? 1u : 0u;
            bool Same64(int at, ulong value) => actual[o + at] == (uint)value && actual[o + at + 1] == (uint)(value >> 32);
            Require(actual[o] == expected && actual[o + 1] == expected && actual[o + 2] == wrapper &&
                actual[o + 3] == 0 && Same64(4, first) && Same64(6, second) && Same64(8, first) && Same64(10, second) &&
                actual[o + 12] == Index(first, c.Bound0) && actual[o + 13] == Index(second, c.Bound1) &&
                actual[o + 14] == Index(first, c.Bound0) && actual[o + 15] == Index(second, c.Bound1),
                $"Leafy gate bit mismatch: legacy={legacy},case={i},root={c.Root:x16},salt={c.Salt:x16},pools={c.StrikeLength}/{c.DefendLength},targets={c.TargetCount}:{c.Target0}/{c.Target1}.");
        }
    }

    private static string LeafyGateProbeSource(bool legacy)
    {
        string source = NeowFamilyGpuExecutor.ShaderSource(stage: 3, leafyPreGate: true,
            legacyLeafyGate: legacy);
        const string planReader = "uint plan_value(uint i) { return plan_meta.values[i]; }";
        Require(source.Contains(planReader, StringComparison.Ordinal) && source.Contains("void main()", StringComparison.Ordinal),
            "Production Leafy shader probe insertion points changed.");
        // Per-invocation pool sizes/salts let one dispatch cover every dense-ID
        // pool length, rather than compiling or submitting once per length.
        source = source.Replace(planReader, """
            uint leafy_probe_row;
            uint leafy_probe_input(uint i) { return input_ordinals.values[leafy_probe_row*12u+i]; }
            uint plan_value(uint i) {
                if(i==58u)return leafy_probe_input(4u);
                if(i==59u)return leafy_probe_input(5u);
                if(i==37u)return leafy_probe_input(2u);
                if(i==38u)return leafy_probe_input(3u);
                return plan_meta.values[i];
            }
            """, StringComparison.Ordinal).Replace("void main()", "void leafy_unused_production_main()", StringComparison.Ordinal);
        return source + """

            void main() {
                leafy_probe_row=gl_GlobalInvocationID.x;
                if(leafy_probe_row>=batch.values[0])return;
                uint64_t root=make_u64(leafy_probe_input(0u),leafy_probe_input(1u));
                uint64_t seed=root+plan_u64(37u);
                uint count=leafy_probe_input(6u),t0=leafy_probe_input(7u),t1=leafy_probe_input(8u);
                uint o=leafy_probe_row*16u;
                RouteRngState legacy_state;
                legacy_state.transformations=rng_initialize(seed);
                output_ordinals.values[o]=leafy_pre_gate_matches(seed,count,t0,t1)?1u:0u;
                output_ordinals.values[o+1u]=execute_leafy_route(count,t0,t1,true,legacy_state)?1u:0u;
                output_ordinals.values[o+2u]=leafy_pre_gate(root)?1u:0u;
                output_ordinals.values[o+3u]=0u;
                uint64_t initial_s1;
                uint64_t first=leafy_pre_gate_first_draw(seed,initial_s1);
                uint64_t second=leafy_pre_gate_second_draw(seed,initial_s1);
                RngState canonical=rng_initialize(seed);
                uint64_t old_first=next_u64(canonical);
                uint64_t old_second=next_u64(canonical);
                output_ordinals.values[o+4u]=uint(first);output_ordinals.values[o+5u]=uint(first>>32u);
                output_ordinals.values[o+6u]=uint(second);output_ordinals.values[o+7u]=uint(second>>32u);
                output_ordinals.values[o+8u]=uint(old_first);output_ordinals.values[o+9u]=uint(old_first>>32u);
                output_ordinals.values[o+10u]=uint(old_second);output_ordinals.values[o+11u]=uint(old_second>>32u);
                uint b0=leafy_probe_input(9u),b1=leafy_probe_input(10u);
                output_ordinals.values[o+12u]=next_int_current_shortcut_from_mantissa(first>>11u,b0);
                output_ordinals.values[o+13u]=next_int_current_shortcut_from_mantissa(second>>11u,b1);
                canonical=rng_initialize(seed);
                output_ordinals.values[o+14u]=next_int(canonical,b0);
                output_ordinals.values[o+15u]=next_int(canonical,b1);
            }
            """;
    }
}
