using Godot;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.Contracts;
using RolltheSpire2.Search.FamilyExecution;

namespace GpuResourceSmoke;

public partial class Smoke
{
    private static void TestSharedVisibleSeedCache(RenderingDevice rd)
    {
        Require(Beta110SeedCodec.TryParseOrdinal("9KT7UD2DZZFZ", out ulong zzfz, out _, out _), "Shared prefix ZZFZ fixture.");
        SearchBatch[] batches = [new(0, 1031), new(zzfz, 1031), new(zzfz + 1, 513),
            new(PrefixHashCache.BlockSize - 7UL, 1031), new(0x100000003UL, 4097),
            new(123456789012345UL, 1031), new(PrefixHashCache.SpaceSize - 1031, 1031),
            new(PrefixHashCache.SpaceSize - 1, 1), new(0, PrefixHashCache.MaxCandidateCount + 1)];
        int checkedRoots = 0, nonzero = 0;
        foreach (string function in new[] { "root_hash_for_ordinal", "hash_seed12", "cr_hash_seed12" })
        foreach (bool dense in new[] { false, true })
        {
            var owned = new List<Rid>();
            Rid Own(Rid r) { owned.Add(r); return r; }
            try
            {
                const int count = 1031;
                Rid Buffer(uint[] data) => Own(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(data)));
                Rid meta = Buffer(new uint[4]), input = Buffer(new uint[count]), output = Buffer(new uint[count * 2]);
                Rid[] buffers = [meta, input, output];
                string source = SharedHashProbeSource(function, dense);
                var cache = SharedVisibleSeedGpuCache.Attach(rd, owned, ref source, ref buffers, "SharedPrefix.HashOracle", enabled: true);
                Require(cache is not null && buffers.Length == 4, "Shared prefix attach failed.");
                Rid shader = Own(FamilyGpuComputeUtility.CompileShader(rd, source, "SharedPrefix.HashOracle." + function));
                Rid pipeline = Own(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader));
                Rid uniforms = Own(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers));
                foreach (var batch in batches)
                {
                    int n = Math.Min(count, batch.BatchCandidateCount);
                    // Dense probes include an offset within a larger root batch;
                    // compact probes are deliberately nonconsecutive and reversed.
                    uint offset = dense && batch.BatchCandidateCount > n ? (uint)(batch.BatchCandidateCount - n) : 0;
                    uint[] ordinals = dense ? Enumerable.Range(0, n).Select(i => offset + (uint)i).ToArray()
                        : Enumerable.Range(0, n).Select(i => (uint)((ulong)(n - i - 1) * (ulong)(batch.BatchCandidateCount - 1) / (ulong)Math.Max(1, n - 1))).ToArray();
                    cache!.Refresh(batch);
                    FamilyGpuComputeUtility.Update(rd, meta, [(uint)batch.BatchBase, (uint)(batch.BatchBase >> 32), (uint)n, offset], "SharedPrefix.ProbeMeta");
                    FamilyGpuComputeUtility.Update(rd, input, ordinals, "SharedPrefix.ProbeInput");
                    long list = rd.ComputeListBegin();
                    rd.ComputeListBindComputePipeline(list, pipeline); rd.ComputeListBindUniformSet(list, uniforms, 0);
                    rd.ComputeListDispatch(list, (uint)((n + (dense ? 511 : 63)) / (dense ? 512 : 64)), 1, 1); rd.ComputeListEnd();
                    FamilyGpuComputeUtility.SubmitAndSync(rd, "SharedPrefix.HashOracle");
                    uint[] actual = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(output, 0, (uint)n * 8), (uint)n * 2);
                    for (int i = 0; i < n; i++)
                    {
                        ulong expected = XxHash64.HashUtf8(Beta110SeedCodec.FormatOrdinal(batch.BatchBase + ordinals[i]), 0);
                        ulong value = actual[2 * i] | ((ulong)actual[2 * i + 1] << 32);
                        Require(value == expected, $"Shared prefix mismatch {function},dense={dense},base={batch.BatchBase},ordinal={ordinals[i]}.");
                        if (value != 0) nonzero++;
                        checkedRoots++;
                    }
                }
            }
            finally { FamilyGpuComputeUtility.FreeAll(rd, owned); }
        }
        Emit(new { phase = "sharedVisibleSeedCache", result = "PASS", checkedRoots, nonzero,
            dialects = 3, denseCarryAndSparse = true, relocatedSubwindows = true, zzfzAndLastSeed = true,
            largerBatchFallsBackWithoutTruncation = true, exactCanonicalXxHash64 = true });
    }

    private static string SharedHashProbeSource(string function, bool dense)
    {
        string common = FamilyGpuComputeUtility.LoadEmbeddedShader("VisibleSeedRootHashCommon.glsl")
            .Replace("root_hash_for_ordinal", function, StringComparison.Ordinal);
        string prefix = "#version 450\n#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require\n" +
            "layout(local_size_x=64)in;layout(set=0,binding=0,std430)readonly buffer Batch{uint values[];}batch;" +
            "layout(set=0,binding=1,std430)readonly buffer Input{uint values[];}input_ids;" +
            "layout(set=0,binding=2,std430)buffer Output{uint values[];}output_ids;" +
            "uint64_t make_u64(uint lo,uint hi){return uint64_t(lo)|(uint64_t(hi)<<32u);}" +
            "uint64_t rotl64(uint64_t v,uint n){return(v<<n)|(v>>(64u-n));}\n";
        string body = dense
            ? "void main(){uint first=gl_GlobalInvocationID.x*8u;if(first>=batch.values[2])return;" +
              "uint64_t global_ordinal=make_u64(batch.values[0],batch.values[1])+uint64_t(batch.values[3]+first);" +
              "uint64_t seed_first8;uint seed_tail4;encode_seed12_packed(global_ordinal, seed_first8, seed_tail4);" +
              "for(uint offset=0u;offset<8u&&first+offset<batch.values[2];offset++){" +
              "uint64_t root=root_hash_for_seed12_packed(seed_first8, seed_tail4);" +
              "output_ids.values[(first+offset)*2u]=uint(root);output_ids.values[(first+offset)*2u+1u]=uint(root>>32u);" +
              "if(offset<7u)advance_seed12_packed(seed_first8, seed_tail4);}}"
            : "void main(){uint i=gl_GlobalInvocationID.x;if(i>=batch.values[2])return;uint64_t root=" + function +
              "(make_u64(batch.values[0],batch.values[1])+uint64_t(input_ids.values[i]));" +
              "output_ids.values[i*2u]=uint(root);output_ids.values[i*2u+1u]=uint(root>>32u);}";
        return prefix + common + body;
    }
}
