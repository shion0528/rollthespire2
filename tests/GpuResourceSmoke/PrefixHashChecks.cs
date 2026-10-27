using System.Buffers.Binary;
using System.Text;
using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Seed;
using RolltheSpire2.Search.FamilyExecution;
#if !RT2_PREFIX_HASH_CPU_ONLY
using Godot;

namespace GpuResourceSmoke
{
public partial class Smoke
{
    private const int PrefixProbeInputWords = 6, PrefixProbeOutputWords = 8;

    private static void TestPrefixHashCache(RenderingDevice rd)
    {
        int cpuChecks = PrefixHashCpuChecks.Run();
        var batches = PrefixHashCpuChecks.CreateBatches();
        var tables = new List<uint>();
        var inputs = new List<uint>();
        var probes = new List<(PrefixHashCpuChecks.BatchCase Batch, uint First)>();
        foreach (var batch in batches)
        {
            uint tableOffset = (uint)tables.Count;
            uint[] table = PrefixHashCache.BuildTable(batch.Base, batch.Count);
            tables.AddRange(table);
            foreach (uint first in PrefixHashCpuChecks.CreateStarts(batch))
            {
                inputs.AddRange([(uint)batch.Base, (uint)(batch.Base >> 32), (uint)batch.Count,
                    first, tableOffset, table[0]]);
                probes.Add((batch, first));
            }
        }
        Require(inputs.Count == probes.Count * PrefixProbeInputWords, "Prefix probe input ABI mismatch.");
        uint[]? control = null;
        foreach (bool cached in new[] { false, true })
        {
            var owned = new List<Rid>();
            Rid Own(Rid value) { Require(value.IsValid, "Prefix hash probe allocation failed."); owned.Add(value); return value; }
            try
            {
                Rid Buffer(uint[] values) => Own(FamilyGpuComputeUtility.CreateStorageBuffer(rd, FamilyGpuComputeUtility.ToBytes(values)));
                Rid header = Buffer(new uint[8]);
                int outputCount = checked(probes.Count * 8 * PrefixProbeOutputWords);
                Rid output = Own(FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, checked(outputCount * sizeof(uint)), "PrefixHashProbe.Output"));
                var buffers = new List<Rid> { Buffer([(uint)probes.Count]), Buffer(new uint[128]), Buffer([0]), Buffer([0]),
                    Buffer([0]), Buffer([0]), header, output, Buffer([0]), Buffer(new uint[160]),
                    Buffer(inputs.ToArray()), Buffer([0]), Buffer([0]) };
                if (cached) buffers.Add(Buffer(tables.ToArray()));
                Rid shader = Own(FamilyGpuComputeUtility.CompileShader(rd, PrefixHashProbeSource(cached),
                    "PrefixHashProbe." + (cached ? "Cached" : "Legacy")));
                Rid pipeline = Own(FamilyGpuComputeUtility.CreateComputePipeline(rd, shader));
                Rid uniforms = Own(FamilyGpuComputeUtility.CreateUniformSet(rd, shader, buffers));
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, pipeline);
                rd.ComputeListBindUniformSet(list, uniforms, 0);
                rd.ComputeListDispatch(list, checked(((uint)probes.Count + 63u) / 64u), 1, 1);
                rd.ComputeListEnd();
                FamilyGpuComputeUtility.SubmitAndSync(rd, "Smoke.PrefixHashProbe");
                uint[] status = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(header), 8);
                Require(status[3] == 0, "Prefix hash probe detected an out-of-range table row.");
                uint[] actual = FamilyGpuComputeUtility.FromUInt32Bytes(rd.BufferGetData(output), (uint)outputCount);
                for (int probe = 0; probe < probes.Count; probe++)
                for (uint lane = 0; lane < 8; lane++)
                {
                    var (batch, first) = probes[probe];
                    int at = checked((probe * 8 + (int)lane) * PrefixProbeOutputWords);
                    if (first + lane >= batch.Count)
                    {
                        Require(actual.AsSpan(at, PrefixProbeOutputWords).IndexOfAnyExcept(0u) < 0,
                            "Prefix probe wrote an inactive tail lane.");
                        continue;
                    }
                    ulong ordinal = checked(batch.Base + first + lane);
                    string seed = VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, ordinal);
                    ulong expected = XxHash64.HashUtf8(seed, 0);
                    byte[] bytes = Encoding.ASCII.GetBytes(seed);
                    ulong first8 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                    uint tail = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                    uint row = ((uint)(batch.Base % PrefixHashCache.BlockSize) + first + lane) / PrefixHashCache.BlockSize;
                    Require(actual[at] == (uint)expected && actual[at + 1] == (uint)(expected >> 32) &&
                        actual[at + 2] == (uint)expected && actual[at + 3] == (uint)(expected >> 32) &&
                        actual[at + 4] == (uint)first8 && actual[at + 5] == (uint)(first8 >> 32) &&
                        actual[at + 6] == tail && actual[at + 7] == row,
                        $"Prefix hash mismatch: cached={cached},base={batch.Base},count={batch.Count},first={first},lane={lane},seed={seed}.");
                }
                if (control is null) control = actual;
                else Require(control.AsSpan().SequenceEqual(actual), "Cached/legacy prefix hash output bits differ.");
            }
            finally { FamilyGpuComputeUtility.FreeAll(rd, owned); }
        }
        Emit(new { phase = "prefixHashCache", result = "PASS", batches = batches.Count,
            probes = probes.Count, lanes = probes.Count * 8, macroModes = 2, cpuChecks,
            evidence = "ProductionTableAndGlslHelpers;CanonicalCpuXxHash64;LegacyGpuHash;ArbitraryBase;EveryTableRow;TailCarry;PartialEight;Final12Z;EmptyRange" });
    }

    private static string PrefixHashProbeSource(bool cached)
    {
        string source = NeowFamilyGpuExecutor.ShaderSource(stage: 0);
        const string inputDeclaration = "layout(set=0,binding=10,std430) readonly restrict buffer Input { uint values[]; } input_ordinals;";
        const string reader = "uint prefix_hash_cache_value(uint i) { return prefix_hash_cache.values[i]; }";
        Require(source.Contains(inputDeclaration, StringComparison.Ordinal) && source.Contains(reader, StringComparison.Ordinal) &&
            source.Contains("#define RT2_PREFIX_HASH_CACHE 0", StringComparison.Ordinal), "Prefix hash production probe insertion point changed.");
        source = source.Replace("#define RT2_PREFIX_HASH_CACHE 0", cached ? "#define RT2_PREFIX_HASH_CACHE 1" : "#define RT2_PREFIX_HASH_CACHE 0", StringComparison.Ordinal)
            .Replace(inputDeclaration, inputDeclaration + """

                uint prefix_probe_row;
                uint prefix_probe_input(uint i) { return input_ordinals.values[prefix_probe_row*6u+i]; }
                """, StringComparison.Ordinal)
            .Replace(reader, "uint prefix_hash_cache_value(uint i) { return prefix_hash_cache.values[prefix_probe_input(4u)+i]; }", StringComparison.Ordinal)
            .Replace("void main()", "void prefix_unused_production_main()", StringComparison.Ordinal);
        return source + """

            void main() {
                prefix_probe_row=gl_GlobalInvocationID.x;
                if(prefix_probe_row>=batch.values[0])return;
                uint64_t base=make_u64(prefix_probe_input(0u),prefix_probe_input(1u));
                uint count=prefix_probe_input(2u),first=prefix_probe_input(3u);
                uint64_t first8=uint64_t(0u),prefix_hash=uint64_t(0u);
                uint tail=0u,row=0u,suffix_ordinal=0u;
            #if RT2_PREFIX_HASH_CACHE
                if(first<count && !prefix_hash_cache_initialize(first,row,suffix_ordinal,tail,prefix_hash)) {
                    atomicExchange(header.values[3u],1u);return;
                }
            #else
                if(first<count)encode_seed12_packed(base+uint64_t(first),first8,tail);
            #endif
                for(uint lane=0u;lane<8u;lane++) {
                    uint at=(prefix_probe_row*8u+lane)*8u;
                    if(first+lane>=count) {
                        for(uint word=0u;word<8u;word++)output_ordinals.values[at+word]=0u;
                        continue;
                    }
                    uint64_t legacy=root_hash_for_ordinal(base+uint64_t(first+lane));
            #if RT2_PREFIX_HASH_CACHE
                    uint64_t root=xxhash64_seed12_from_prefix(prefix_hash,tail);
                    uint table_at=4u+row*4u;
                    first8=make_u64(prefix_hash_cache_value(table_at+2u),prefix_hash_cache_value(table_at+3u));
            #else
                    uint64_t root=xxhash64_seed12_packed(first8,tail);
                    row=(prefix_probe_input(5u)+first+lane)/1336336u;
            #endif
                    output_ordinals.values[at]=uint(root);output_ordinals.values[at+1u]=uint(root>>32u);
                    output_ordinals.values[at+2u]=uint(legacy);output_ordinals.values[at+3u]=uint(legacy>>32u);
                    output_ordinals.values[at+4u]=uint(first8);output_ordinals.values[at+5u]=uint(first8>>32u);
                    output_ordinals.values[at+6u]=tail;output_ordinals.values[at+7u]=row;
                    if(lane<7u && first+lane+1u<count) {
            #if RT2_PREFIX_HASH_CACHE
                        if(!prefix_hash_cache_advance(row,suffix_ordinal,tail,prefix_hash)) {
                            atomicExchange(header.values[3u],1u);return;
                        }
            #else
                        advance_seed12_packed(first8,tail);
            #endif
                    }
                }
            }
            """;
    }
}
}
#endif

namespace GpuResourceSmoke
{
// Define RT2_PREFIX_HASH_CPU_ONLY and link this file into a plain CPU host to
// run this same fixture without a Godot reference or a RenderingDevice.
internal static class PrefixHashCpuChecks
{
    internal readonly record struct BatchCase(ulong Base, int Count);

    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string issue)
        {
            if (!condition) throw new InvalidDataException(issue);
            checks++;
        }
        void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentOutOfRangeException) { checks++; return; }
            throw new InvalidDataException("Prefix hash table accepted an invalid seed range.");
        }
        Reject(() => PrefixHashCache.BuildTable(0, -1));
        Reject(() => PrefixHashCache.BuildTable(0, PrefixHashCache.MaxCandidateCount + 1));
        Reject(() => PrefixHashCache.BuildTable(PrefixHashCache.SpaceSize + 1, 0));
        Reject(() => PrefixHashCache.BuildTable(PrefixHashCache.SpaceSize, 1));
        Reject(() => PrefixHashCache.BuildTable(ulong.MaxValue, 1));
        Reject(() => PrefixHashCache.EncodeFirst8(PrefixHashCache.PrefixCount));
        Reject(() => PrefixHashCache.EncodeTail4(PrefixHashCache.BlockSize));
        foreach (var batch in CreateBatches())
        {
            uint[] table = PrefixHashCache.BuildTable(batch.Base, batch.Count);
            uint offset = (uint)(batch.Base % PrefixHashCache.BlockSize);
            int rows = batch.Count == 0 ? 0 : checked((int)((offset + (uint)batch.Count - 1) / PrefixHashCache.BlockSize + 1));
            Check(table.Length == PrefixHashCache.HeaderWords + rows * PrefixHashCache.RowWords &&
                table.Length <= PrefixHashCache.MaximumTableWords && table[0] == offset && table[1] == rows && table[2] == 0 && table[3] == 0,
                "Prefix hash table ABI/header mismatch.");
            for (int row = 0; row < rows; row++)
            {
                int at = PrefixHashCache.HeaderWords + row * PrefixHashCache.RowWords;
                ulong prefix = table[at] | ((ulong)table[at + 1] << 32);
                ulong first8 = table[at + 2] | ((ulong)table[at + 3] << 32);
                ulong block = batch.Base / PrefixHashCache.BlockSize + (ulong)row;
                foreach (uint suffix in new uint[] { 0, 1, 33, 34, PrefixHashCache.BlockSize - 1 })
                {
                    string seed = VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance,
                        block * PrefixHashCache.BlockSize + suffix);
                    byte[] bytes = Encoding.ASCII.GetBytes(seed);
                    Check(first8 == BinaryPrimitives.ReadUInt64LittleEndian(bytes) &&
                        PrefixHashCache.EncodeTail4(suffix) == BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) &&
                        PrefixHashCache.ComputePrefixHash(first8) == prefix &&
                        PrefixHashCache.ComputeRootHashFromPrefix(prefix, PrefixHashCache.EncodeTail4(suffix)) == XxHash64.HashUtf8(seed, 0),
                        "Prefix hash table row differs from canonical CPU xxHash64.");
                }
            }
            foreach (uint first in CreateStarts(batch))
            for (uint lane = 0; lane < 8 && first + lane < batch.Count; lane++)
            {
                uint position = offset + first + lane;
                uint row = position / PrefixHashCache.BlockSize, suffix = position % PrefixHashCache.BlockSize;
                int at = checked(PrefixHashCache.HeaderWords + (int)row * PrefixHashCache.RowWords);
                ulong prefix = table[at] | ((ulong)table[at + 1] << 32);
                ulong ordinal = batch.Base + first + lane;
                string seed = VisibleSeedCandidateCodec.FormatOrdinal(Beta111Profile.Instance, ordinal);
                Check(PrefixHashCache.ComputeRootHashFromPrefix(prefix, PrefixHashCache.EncodeTail4(suffix)) ==
                    XxHash64.HashUtf8(seed, 0), "Arbitrary-base prefix table lookup changed the root hash.");
            }
        }
        Check(PrefixHashCache.BuildTable(PrefixHashCache.BlockSize - 1, PrefixHashCache.MaxCandidateCount)[1] == PrefixHashCache.MaximumRows,
            "Worst-aligned workspace did not cover all fourteen table rows.");
        return checks;
    }

    internal static List<BatchCase> CreateBatches()
    {
        var result = new List<BatchCase>();
        void Add(ulong start, int count)
        {
            if (start <= PrefixHashCache.SpaceSize && (ulong)count <= PrefixHashCache.SpaceSize - start)
                result.Add(new(start, count));
        }
        foreach (int count in new[] { 0, 1, 7, 8, 9, 511, 512, 513, PrefixHashCache.MaxCandidateCount })
        {
            Add(0, count);
            Add(PrefixHashCache.SpaceSize - (ulong)count, count);
        }
        foreach (ulong block in new ulong[] { 1, 2, 33, 34, 1155, 39303, 1336335, PrefixHashCache.PrefixCount - 1 })
        foreach (int delta in new[] { -9, -8, -7, -1, 0, 1, 7, 8, 9 })
        {
            ulong boundary = block * PrefixHashCache.BlockSize;
            Add(delta < 0 ? boundary - (ulong)-delta : boundary + (ulong)delta, 513);
        }
        Add(PrefixHashCache.BlockSize - 1, PrefixHashCache.MaxCandidateCount);
        Add(123456789012345, PrefixHashCache.MaxCandidateCount);
        var random = new Random(0x50f17);
        for (int i = 0; i < 64; i++)
        {
            int count = i % 4 == 0 ? random.Next(1, 514) : random.Next(514, PrefixHashCache.MaxCandidateCount + 1);
            Add((ulong)random.NextInt64((long)(PrefixHashCache.SpaceSize - (ulong)count + 1)), count);
        }
        return result;
    }

    internal static IReadOnlyList<uint> CreateStarts(BatchCase batch)
    {
        var result = new SortedSet<uint> { 0 };
        void Add(long value) { if (value >= 0 && value < batch.Count) result.Add((uint)value); }
        if (batch.Count <= 1024)
            for (int first = 0; first < batch.Count; first += 8) Add(first);
        foreach (int delta in new[] { 0, 1, 7, 8, 9, 15 })
        {
            Add(delta); Add((long)batch.Count - 1 - delta);
        }
        uint offset = (uint)(batch.Base % PrefixHashCache.BlockSize);
        for (long boundary = PrefixHashCache.BlockSize - offset; boundary < batch.Count; boundary += PrefixHashCache.BlockSize)
            for (int delta = -9; delta <= 9; delta++) Add(boundary + delta);
        if (batch.Count > 1024)
        {
            var random = new Random(unchecked((int)batch.Base ^ batch.Count));
            for (int i = 0; i < 64; i++) Add(random.Next(batch.Count));
        }
        return result.ToArray();
    }
}
}
