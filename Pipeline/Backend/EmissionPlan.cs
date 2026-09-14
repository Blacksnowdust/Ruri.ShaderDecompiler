namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>One vertex input: the SPIR-V location it arrives at and the D3D semantic it must declare.</summary>
internal readonly record struct VertexAttributeSemantic(uint Location, string Semantic);

/// <summary>
/// A constant buffer whose members the backend will spell as
/// <c>&lt;VariableName&gt;_&lt;MemberName&gt;</c>. The prefix is ours — it is the
/// variable name we injected — so undoing it afterwards is an exact-token
/// operation on a known key, not a search.
/// </summary>
internal sealed record BlockMemberPrefix(string VariableName, IReadOnlyList<string> MemberNames);

/// <summary>
/// Every decision the source backend needs that the SPIR-V module itself cannot
/// carry. Names live in the module as <c>OpName</c>; this holds the rest:
/// which semantic each vertex input declares, and which block-member prefixes
/// to strip back off the emitted text.
/// </summary>
internal sealed class EmissionPlan
{
    public List<VertexAttributeSemantic> VertexAttributes { get; } = new();

    public List<BlockMemberPrefix> BlockMemberPrefixes { get; } = new();

    public EntryPointSelection EntryPoint { get; init; }

    public uint ShaderModel { get; init; }
}
