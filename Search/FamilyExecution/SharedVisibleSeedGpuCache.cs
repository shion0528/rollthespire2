using System.Text.RegularExpressions;
using Godot;
using RolltheSpire2.Bootstrap;
using RolltheSpire2.Search.Contracts;

namespace RolltheSpire2.Search.FamilyExecution;

// A cache of pure visible-seed hash state. Every row is indexed by the actual
// batch-local ordinal, so compact/private inputs need not be consecutive.
// The containing executor owns the RID, including initialization failures.
internal sealed class SharedVisibleSeedGpuCache
{
    internal const string Revision = "SharedVisiblePrefix34Pow4.20261021.v1";
    internal static bool Requested => SearchOptimizationScope.Current.PrefixCache &&
        System.Environment.GetEnvironmentVariable("RT2_SHARED_PREFIX_HASH") != "0";
    internal static string RevisionSuffix => Requested ? "." + Revision : "";
    internal const int Bytes = PrefixHashCache.MaximumTableWords * sizeof(uint);
    private readonly RenderingDevice _rd;
    private readonly int _owner = System.Environment.CurrentManagedThreadId;
    private readonly Rid _table;
    internal Rid Buffer => _table;
    private ulong _lastBase = ulong.MaxValue;
    private int _lastCount = -1;

    private SharedVisibleSeedGpuCache(RenderingDevice rd, Rid table) { _rd = rd; _table = table; }

    internal static SharedVisibleSeedGpuCache? Attach(RenderingDevice rd, List<Rid> owned,
        ref string source, ref Rid[] buffers, string family, bool? enabled = null)
    {
        if (!(enabled ?? Requested)) return null;
        string updated = ApplySource(source, buffers.Length);
        Rid table = FamilyGpuComputeUtility.CreateZeroedStorageBuffer(rd, Bytes, family + ".VisiblePrefix");
        owned.Add(table);
        buffers = [.. buffers, table];
        source = updated;
        RuntimeLog.TryBackgroundInfo($"sharedPrefixHashCacheReady=true;family={family};revision={Revision};dense=True;compact=True;privateInput=True;tableBytes={Bytes}");
        return new SharedVisibleSeedGpuCache(rd, table);
    }

    internal static SharedVisibleSeedGpuCache? Attach(RenderingDevice rd, List<Rid> owned,
        ref string source, List<Rid> buffers, string family, bool? enabled = null)
    {
        Rid[] array = buffers.ToArray();
        var cache = Attach(rd, owned, ref source, ref array, family, enabled);
        if (cache is not null) buffers.Add(array[^1]);
        return cache;
    }

    internal void Refresh(SearchBatch batch)
    {
        if (_owner != System.Environment.CurrentManagedThreadId) throw new InvalidOperationException("SharedPrefix.OwnerMismatch");
        if (batch.BatchBase == _lastBase && batch.BatchCandidateCount == _lastCount) return;
        // Larger batches use the original hash. Do not silently truncate the
        // prefix table or change the caller's window/ordinal geometry.
        uint[] data = batch.BatchCandidateCount <= PrefixHashCache.MaxCandidateCount
            ? PrefixHashCache.BuildTable(batch.BatchBase, batch.BatchCandidateCount) : new uint[4];
        data[2] = (uint)batch.BatchBase;
        data[3] = (uint)(batch.BatchBase >> 32);
        FamilyGpuComputeUtility.Update(_rd, _table, data, "SharedVisibleSeedPrefix");
        _lastBase = batch.BatchBase; _lastCount = batch.BatchCandidateCount;
    }

    internal static string ApplySource(string source, int binding)
    {
        string? function = null;
        int start = -1;
        foreach (string name in new[] { "root_hash_for_ordinal", "cr_hash_seed12", "hash_seed12" })
        {
            var matches = Regex.Matches(source, @"\buint64_t\s+" + name + @"\s*\(\s*uint64_t\s+\w+\s*\)\s*\{");
            if (matches.Count == 0) continue;
            if (matches.Count != 1) throw new InvalidOperationException("SharedPrefix.DuplicateRootFunction");
            Match found = matches[0];
            function = name; start = found.Index;
            // Composite donors may contain other, unused canonical hash
            // helpers. The family entry point uses the first dialect listed.
            break;
        }
        if (function is null) throw new InvalidOperationException("SharedPrefix.RootFunctionMissing");
        int open = source.IndexOf('{', start), depth = 1, end = open + 1;
        for (; end < source.Length && depth != 0; end++)
        {
            if (source[end] == '{') depth++;
            else if (source[end] == '}') depth--;
        }
        if (depth != 0) throw new InvalidOperationException("SharedPrefix.RootFunctionUnclosed");
        string original = source[start..end];
        string renamed = new Regex(@"\b" + function + @"\b").Replace(original, "rt2_legacy_visible_root", 1);
        string helpers = FamilyGpuComputeUtility.LoadEmbeddedShader("SharedVisibleSeedPrefixHash.glsl")
            .Replace("__RT2_PREFIX_BINDING__", binding.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__RT2_ROOT_FUNCTION__", function, StringComparison.Ordinal);
        string updated = source[..start] + renamed + "\n" + helpers + source[end..];
        // R/S already process eight consecutive roots per invocation. Keep
        // their carry-based tail enumeration, rather than doing four divisions
        // for every root. Compact calls use the ordinal-indexed helper above.
        const string init = "encode_seed12_packed(global_ordinal, seed_first8, seed_tail4);";
        if (updated.Contains(init, StringComparison.Ordinal))
        {
            updated = updated.Replace(init,
                "uint rt2_row,rt2_suffix; uint64_t rt2_prefix; bool rt2_cached=rt2_prefix_begin(global_ordinal,rt2_row,rt2_suffix,seed_tail4,rt2_prefix); if(!rt2_cached) " + init, StringComparison.Ordinal)
                .Replace("root_hash_for_seed12_packed(seed_first8, seed_tail4)",
                    "(rt2_cached ? rt2_prefix_root(rt2_prefix,seed_tail4) : root_hash_for_seed12_packed(seed_first8, seed_tail4))", StringComparison.Ordinal)
                .Replace("advance_seed12_packed(seed_first8, seed_tail4);",
                    "{ if(rt2_cached) rt2_prefix_advance(rt2_row,rt2_suffix,seed_tail4,rt2_prefix); else advance_seed12_packed(seed_first8, seed_tail4); }", StringComparison.Ordinal);
        }
        const string transformInit = "if(batch.v[4]==0u)encode_seed12_packed(base+uint64_t(batch.v[6]+first),seed8,tail);";
        if (updated.Contains(transformInit, StringComparison.Ordinal))
        {
            updated = updated.Replace(transformInit,
                "uint rt2_row=0u,rt2_suffix=0u;uint64_t rt2_prefix=uint64_t(0u);bool rt2_cached=false;" +
                "if(batch.v[4]==0u){rt2_cached=rt2_prefix_begin(base+uint64_t(batch.v[6]+first),rt2_row,rt2_suffix,tail,rt2_prefix);" +
                "if(!rt2_cached)encode_seed12_packed(base+uint64_t(batch.v[6]+first),seed8,tail);}", StringComparison.Ordinal)
                .Replace("xxhash64_seed12_packed(seed8,tail)",
                    "(rt2_cached?rt2_prefix_root(rt2_prefix,tail):xxhash64_seed12_packed(seed8,tail))", StringComparison.Ordinal)
                .Replace("advance_seed12_packed(seed8,tail);",
                    "{if(rt2_cached)rt2_prefix_advance(rt2_row,rt2_suffix,tail,rt2_prefix);else advance_seed12_packed(seed8,tail);}", StringComparison.Ordinal);
        }
        const string neowInit = "if (batch.values[4u]==0u && first<count) encode_seed12_packed(base+uint64_t(first),seed_first8,tail);";
        if (updated.Contains(neowInit, StringComparison.Ordinal))
        {
            updated = updated.Replace(neowInit,
                "uint rt2_row=0u,rt2_suffix=0u;uint64_t rt2_prefix=uint64_t(0u);bool rt2_cached=false;" +
                "if(batch.values[4u]==0u&&first<count){rt2_cached=rt2_prefix_begin(base+uint64_t(first),rt2_row,rt2_suffix,tail,rt2_prefix);" +
                "if(!rt2_cached)encode_seed12_packed(base+uint64_t(first),seed_first8,tail);}", StringComparison.Ordinal)
                .Replace("xxhash64_seed12_packed(seed_first8,tail)",
                    "(rt2_cached?rt2_prefix_root(rt2_prefix,tail):xxhash64_seed12_packed(seed_first8,tail))", StringComparison.Ordinal)
                .Replace("advance_seed12_packed(seed_first8,tail);",
                    "{if(rt2_cached)rt2_prefix_advance(rt2_row,rt2_suffix,tail,rt2_prefix);else advance_seed12_packed(seed_first8,tail);}", StringComparison.Ordinal);
        }
        return updated;
    }
}
