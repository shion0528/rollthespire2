using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

// Reads PE metadata and IL without loading or executing a mod/game assembly.
// Always require a positive diagnostic control so a broken detector cannot
// report a clean production artifact merely because it inspected nothing.
internal static class Program
{
    private static readonly string[] ProfilingTokens = [
        "GpuProfiling", "gpuProfiling", "RT2GpuProfiling", "RT2_GPU_PROFILE",
        "ActualSearchBenchmark", "RT2_ACTUAL_SEARCH_BENCH", "RT2_N_INTERNAL_ORDER", "RT2_ACTUAL_AB_INTERNAL_ORDER",
        "RT2_ACTUAL_AB_GROUP_OUTPUT", "RT2_N_SHADER_CONSTANTS", "RT2_GPU_FAST_FLOAT",
        "NeowShaderConstants", "ShaderFloatPrecision",
        "RT2_N_POOL_REACHABILITY", "RT2_N_FINAL_CURSE_INTERVALS", "RT2_N_BONES_IDENTITY_ONLY"
    ];
    // The same group name is a legitimate embedded GLSL macro. The exact
    // environment-key ldstr must still disappear from production method IL.
    private static readonly HashSet<string> DiagnosticOnlyControls = new(StringComparer.Ordinal) {
        "RT2_N_GROUP_OUTPUT", "RT2_CPU_BATCH_N", "RT2_S_ORDERED_SEQUENCE_SLOTS"
    };
    private static readonly HashSet<string> TimestampApis = new(StringComparer.Ordinal) {
        "CaptureTimestamp", "GetCapturedTimestampsCount", "GetCapturedTimestampsFrame",
        "GetCapturedTimestampGpuTime", "GetCapturedTimestampCpuTime", "GetCapturedTimestampName"
    };
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields()
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));

    private static int Main(string[] args)
    {
        try
        {
            var production = new List<string>();
            string? diagnostic = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--production" && i + 1 < args.Length) production.Add(Path.GetFullPath(args[++i]));
                else if (args[i] == "--diagnostic" && i + 1 < args.Length && diagnostic is null) diagnostic = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("Usage: --production <dll> [--production <packaged dll> ...] --diagnostic <profiling-enabled dll>");
            }
            if (production.Count == 0 || diagnostic is null) throw new ArgumentException("A production artifact and a positive diagnostic control are both required.");
            var control = Inspect(diagnostic);
            bool positive = control.TypeDefinitions.Count > 0 && control.MemberReferences.Count > 0 &&
                control.IlReferences.Count > 0 && control.MetadataStrings.Count > 0 && control.ImageStrings.Count > 0;
            Report("diagnosticPositiveControl", diagnostic, control, positive);
            if (!positive) throw new InvalidDataException("Diagnostic positive control did not exercise all required detectors.");
            // A diagnostic DLL presented as a shipping artifact must be rejected.
            if (control.Clean) throw new InvalidDataException("Contaminated-artifact negative control unexpectedly passed.");
            foreach (string path in production)
            {
                if (string.Equals(path, diagnostic, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Production and diagnostic control paths must be distinct.");
                var result = Inspect(path);
                Report("productionArtifact", path, result, result.Clean);
                if (!result.Clean) throw new InvalidDataException("Profiling code/API/string/resource remains in the shipping artifact: " + path);
            }
            Console.WriteLine("RT2_GPU_ARTIFACT " + JsonSerializer.Serialize(new {
                phase = "complete", result = "PASS", productionArtifacts = production.Count,
                positiveControl = true, contaminatedArtifactRejected = true,
                inspected = "ActualPE;TypeDef;MethodDef;TypeRef;MemberRef;MethodIL;Ldstr;MetadataStrings;ImageStrings;ManifestResources",
                executedTargetCode = false
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RT2_GPU_ARTIFACT " + JsonSerializer.Serialize(new { phase = "complete", result = "FAIL", error = ex.ToString() }));
            return 1;
        }
    }

    private static Findings Inspect(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw new BadImageFormatException("A managed PE artifact is required.");
        MetadataReader reader = pe.GetMetadataReader();
        var found = new Findings();
        foreach (var handle in reader.TypeDefinitions)
        {
            var value = reader.GetTypeDefinition(handle);
            string name = Qualified(reader.GetString(value.Namespace), reader.GetString(value.Name));
            if (Forbidden(name)) found.TypeDefinitions.Add(name);
        }
        foreach (var handle in reader.TypeReferences)
        {
            string name = EntityName(reader, handle);
            if (Forbidden(name)) found.TypeReferences.Add(name);
        }
        foreach (var handle in reader.MemberReferences)
        {
            var value = reader.GetMemberReference(handle);
            string name = EntityName(reader, handle);
            if (Forbidden(name) || IsTimestampMember(reader, value)) found.MemberReferences.Add(name);
        }
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            string name = EntityName(reader, handle);
            if (Forbidden(name)) found.MethodDefinitions.Add(name);
            if (method.RelativeVirtualAddress == 0) continue;
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            ScanIl(reader, body.GetILBytes() ?? [], name, found);
            found.MethodBodies++;
        }

        byte[] metadata = pe.GetMetadata().GetContent().ToArray();
        foreach (string token in ProfilingTokens.Concat(TimestampApis))
        {
            if (Contains(metadata, Encoding.UTF8.GetBytes(token)) || Contains(metadata, Encoding.Unicode.GetBytes(token)))
                found.MetadataStrings.Add(token);
            if (Contains(image, Encoding.UTF8.GetBytes(token)) || Contains(image, Encoding.Unicode.GetBytes(token)))
                found.ImageStrings.Add(token);
        }
        var resourceDirectory = pe.PEHeaders.CorHeader.ResourcesDirectory;
        byte[] resourceBytes = resourceDirectory.Size == 0 ? [] :
            pe.GetSectionData(resourceDirectory.RelativeVirtualAddress).GetContent(0, resourceDirectory.Size).ToArray();
        foreach (var handle in reader.ManifestResources)
        {
            var resource = reader.GetManifestResource(handle);
            string name = reader.GetString(resource.Name);
            found.ResourcesScanned++;
            if (Forbidden(name) || TimestampApis.Any(name.Contains)) found.Resources.Add("Name:" + name);
            // External linked resources are unverified and cannot silently pass.
            if (!resource.Implementation.IsNil) throw new InvalidDataException("Linked manifest resources require separate artifact inspection: " + name);
        }
        foreach (string token in ProfilingTokens.Concat(TimestampApis))
            if (Contains(resourceBytes, Encoding.UTF8.GetBytes(token)) || Contains(resourceBytes, Encoding.Unicode.GetBytes(token)))
                found.Resources.Add("Content:" + token);
        return found;
    }

    private static void ScanIl(MetadataReader reader, byte[] il, string owner, Findings found)
    {
        int offset = 0;
        while (offset < il.Length)
        {
            int instruction = offset;
            ushort value = il[offset++];
            if (value == 0xfe)
            {
                if (offset == il.Length) throw new BadImageFormatException("Truncated IL opcode.");
                value = (ushort)(0xfe00 | il[offset++]);
            }
            if (!Opcodes.TryGetValue(value, out var opcode)) throw new BadImageFormatException("Unknown IL opcode.");
            int size = opcode.OperandType switch {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => -1,
                _ => 4
            };
            if (size == -1)
            {
                Ensure(il, offset, 4);
                int count = BitConverter.ToInt32(il, offset);
                if (count < 0) throw new BadImageFormatException("Invalid IL switch count.");
                size = checked(4 + count * 4);
            }
            Ensure(il, offset, size);
            if (opcode.OperandType == OperandType.InlineString)
            {
                int token = BitConverter.ToInt32(il, offset);
                string text = reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff));
                if (Forbidden(text) || TimestampApis.Any(text.Contains) || DiagnosticOnlyControls.Contains(text))
                    found.UserStrings.Add(owner + "@" + instruction + ":" + text);
            }
            else if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok or OperandType.InlineType)
            {
                var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset));
                string name = EntityName(reader, handle);
                if (Forbidden(name) || IsTimestampHandle(reader, handle))
                    found.IlReferences.Add(owner + "@" + instruction + ":" + opcode.Name + ":" + name);
            }
            offset += size;
        }
    }

    private static bool IsTimestampHandle(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
            return IsTimestampHandle(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
        return handle.Kind == HandleKind.MemberReference && IsTimestampMember(reader, reader.GetMemberReference((MemberReferenceHandle)handle));
    }
    private static bool IsTimestampMember(MetadataReader reader, MemberReference member) =>
        TimestampApis.Contains(reader.GetString(member.Name)) && EntityName(reader, member.Parent) == "Godot.RenderingDevice";

    private static string EntityName(MetadataReader reader, EntityHandle handle) => handle.Kind switch {
        HandleKind.TypeDefinition => Qualified(reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Namespace), reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name)),
        HandleKind.TypeReference => Qualified(reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Namespace), reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name)),
        HandleKind.MethodDefinition => EntityName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()) + "." + reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
        HandleKind.FieldDefinition => EntityName(reader, reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType()) + "." + reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
        HandleKind.MemberReference => EntityName(reader, reader.GetMemberReference((MemberReferenceHandle)handle).Parent) + "." + reader.GetString(reader.GetMemberReference((MemberReferenceHandle)handle).Name),
        HandleKind.MethodSpecification => EntityName(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
        _ => handle.Kind.ToString()
    };
    private static string Qualified(string space, string name) => space.Length == 0 ? name : space + "." + name;
    private static bool Forbidden(string text) => ProfilingTokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    private static bool Contains(byte[] bytes, byte[] pattern) => bytes.AsSpan().IndexOf(pattern) >= 0;
    private static void Ensure(byte[] bytes, int start, int size)
    {
        if (size < 0 || start > bytes.Length - size) throw new BadImageFormatException("Truncated IL operand.");
    }
    private static void Report(string phase, string path, Findings found, bool pass) =>
        Console.WriteLine("RT2_GPU_ARTIFACT " + JsonSerializer.Serialize(new {
            phase, path, result = pass ? "PASS" : "FAIL", findings = found,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
        }));

    private sealed class Findings
    {
        public HashSet<string> TypeDefinitions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MethodDefinitions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TypeReferences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MemberReferences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IlReferences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UserStrings { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MetadataStrings { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ImageStrings { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Resources { get; } = new(StringComparer.Ordinal);
        public int MethodBodies { get; set; }
        public int ResourcesScanned { get; set; }
        public bool Clean => TypeDefinitions.Count + MethodDefinitions.Count + TypeReferences.Count + MemberReferences.Count +
            IlReferences.Count + UserStrings.Count + MetadataStrings.Count + ImageStrings.Count + Resources.Count == 0;
    }
}
