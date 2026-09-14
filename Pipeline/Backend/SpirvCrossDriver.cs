using System.Runtime.InteropServices;
using System.Text;
using Ruri.ShaderTools.Pipeline.Naming;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using CrossBackend = Silk.NET.SPIRV.Cross.Backend;

namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>
/// SPIR-V → HLSL through the spirv-cross C ABI, configured from an
/// <see cref="EmissionPlan"/> instead of patched afterwards.
///
/// Names are not set here: they already live in the module as <c>OpName</c> and
/// spirv-cross reads them from there. This driver only supplies what the module
/// cannot express — vertex input semantics and backend options — and undoes the
/// one spelling the backend imposes that HLSL property binding cannot accept.
///
/// HLSL only. A module the backend refuses is a failure with the backend's own
/// message, not a fallback to another language.
/// </summary>
internal sealed unsafe class SpirvCrossDriver
{
    private static readonly Cross Api = Cross.GetApi();

    public string? LastFailure { get; private set; }

    public string? Emit(ReadOnlySpan<byte> spirv, EmissionPlan plan)
    {
        LastFailure = null;

        if (spirv.Length < 4 || (spirv.Length & 3) != 0)
        {
            LastFailure = $"SPIR-V byte length {spirv.Length} is not a positive multiple of 4.";
            return null;
        }

        Context* context = null;
        try
        {
            if (Api.ContextCreate(&context) != Result.Success || context == null)
            {
                LastFailure = "spvc_context_create failed.";
                return null;
            }

            ParsedIr* parsed;
            fixed (byte* words = spirv)
            {
                if (Api.ContextParseSpirv(context, (uint*)words, (nuint)(spirv.Length >> 2), &parsed) != Result.Success)
                {
                    return Fail(context);
                }
            }

            Compiler* compiler;
            if (Api.ContextCreateCompiler(context, CrossBackend.Hlsl, parsed, CaptureMode.TakeOwnership, &compiler) != Result.Success)
            {
                return Fail(context);
            }

            foreach (VertexAttributeSemantic attribute in plan.VertexAttributes)
            {
                AddVertexAttributeRemap(compiler, attribute);
            }

            CompilerOptions* options;
            if (Api.CompilerCreateCompilerOptions(compiler, &options) != Result.Success)
            {
                return Fail(context);
            }

            Api.CompilerOptionsSetUint(options, CompilerOption.HlslShaderModel, plan.ShaderModel);
            Api.CompilerOptionsSetBool(options, CompilerOption.ForceZeroInitializedVariables, 1);

            if (Api.CompilerInstallCompilerOptions(compiler, options) != Result.Success)
            {
                return Fail(context);
            }

            if (!string.IsNullOrEmpty(plan.EntryPoint.Name))
            {
                Api.CompilerSetEntryPoint(compiler, plan.EntryPoint.Name, (ExecutionModel)plan.EntryPoint.ExecutionModel);
            }

            byte* source;
            if (Api.CompilerCompile(compiler, &source) != Result.Success || source == null)
            {
                return Fail(context);
            }

            string text = Marshal.PtrToStringUTF8((IntPtr)source) ?? string.Empty;

            string? unlanded = RestoreAuthoredMembers(compiler, ref text, plan.FlattenedBlocks);
            if (unlanded is not null)
            {
                LastFailure = unlanded;
                return null;
            }

            return text;
        }
        finally
        {
            if (context != null)
            {
                Api.ContextDestroy(context);
            }
        }
    }

    private static void AddVertexAttributeRemap(Compiler* compiler, VertexAttributeSemantic attribute)
    {
        int byteCount = Encoding.UTF8.GetByteCount(attribute.Semantic) + 1;
        Span<byte> semantic = byteCount <= 64 ? stackalloc byte[64] : new byte[byteCount];
        int written = Encoding.UTF8.GetBytes(attribute.Semantic, semantic);
        semantic[written] = 0;

        fixed (byte* name = semantic)
        {
            HlslVertexAttributeRemap entry = new() { Location = attribute.Location, Semantic = name };
            Api.CompilerHlslAddVertexAttributeRemap(compiler, &entry, 1);
        }
    }

    private string? Fail(Context* context)
    {
        byte* message = Api.ContextGetLastErrorString(context);
        LastFailure = message != null
            ? "spirv-cross: " + Marshal.PtrToStringUTF8((IntPtr)message)
            : "spirv-cross failed without an error string.";
        return null;
    }

    /// <summary>
    /// The backend flattens every constant buffer into
    /// <c>&lt;variable&gt;_&lt;member&gt;</c> identifiers and offers no option to
    /// stop. Its spelling of both halves is read back from it after compilation —
    /// that is the spelling it uniquified and wrote, including any respelling of
    /// a name it would not accept — and joined the way it joins them: one
    /// underscore, runs collapsed. Each joined identifier is then replaced, as a
    /// whole token, by the member's own name.
    ///
    /// A planned member whose joined identifier is not in the text is a member
    /// Unity will never bind. Refusing here turns a renders-wrong shader into a
    /// failure that names the exact member and the spelling that was looked for,
    /// so the naming path that lost it can be found instead of guessed at.
    /// </summary>
    private static string? RestoreAuthoredMembers(Compiler* compiler, ref string text, IReadOnlyList<FlattenedBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        StringBuilder builder = new(text.Length);

        foreach (FlattenedBlock block in blocks)
        {
            string variable = ReadName(Api.CompilerGetName(compiler, block.VariableId));
            if (variable.Length == 0)
            {
                return $"Planned constant-buffer variable {block.VariableId} lost its name in the backend.";
            }

            foreach (uint member in block.AuthoredMembers)
            {
                string name = ReadName(Api.CompilerGetMemberName(compiler, block.StructTypeId, member));
                if (name.Length == 0)
                {
                    return $"Planned member {member} of constant buffer '{variable}' lost its name in the backend.";
                }

                string flattened = HlslIdentifier.CollapseUnderscores(variable + "_" + name);
                if (!ContainsWholeToken(text, flattened))
                {
                    return $"Planned constant-buffer member did not land in the emitted HLSL: {variable}.{name} (looked for '{flattened}').";
                }

                text = ReplaceWholeToken(text, flattened, name, builder);
            }
        }

        return null;
    }

    private static string ReadName(byte* utf8)
        => utf8 == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)utf8) ?? string.Empty;

    private static bool ContainsWholeToken(string text, string token)
    {
        int index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (IsWholeTokenAt(text, index, token.Length))
            {
                return true;
            }

            index = text.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string ReplaceWholeToken(string text, string token, string replacement, StringBuilder builder)
    {
        int index = text.IndexOf(token, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        builder.Clear();
        int cursor = 0;

        while (index >= 0)
        {
            int after = index + token.Length;
            builder.Append(text, cursor, index - cursor);
            builder.Append(IsWholeTokenAt(text, index, token.Length) ? replacement : token);
            cursor = after;

            index = text.IndexOf(token, cursor, StringComparison.Ordinal);
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static bool IsWholeTokenAt(string text, int index, int length)
    {
        bool boundedBefore = index == 0 || !IsIdentifierCharacter(text[index - 1]);
        int after = index + length;
        bool boundedAfter = after >= text.Length || !IsIdentifierCharacter(text[after]);
        return boundedBefore && boundedAfter;
    }

    private static bool IsIdentifierCharacter(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
}
