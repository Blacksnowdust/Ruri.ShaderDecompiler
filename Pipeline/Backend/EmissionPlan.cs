namespace Ruri.ShaderTools.Pipeline.Backend;

/// <summary>One vertex input: the SPIR-V location it arrives at and the D3D semantic it must declare.</summary>
internal readonly record struct VertexAttributeSemantic(uint Location, string Semantic);

/// <summary>
/// A constant buffer the backend will flatten, spelling every member
/// <c>&lt;variable&gt;_&lt;member&gt;</c>. <see cref="AuthoredMembers"/> lists the
/// member indices whose names are recovered symbols; those are restored to the
/// bare symbol in the emitted text. Members left out keep the prefix: their
/// names are generated placeholders, unique only within their block, and HLSL
/// gives every block member one global namespace.
///
/// Ids rather than strings, because the backend respells names it will not
/// accept. The spelling it actually wrote is read back from it by id after
/// compilation instead of being predicted.
/// </summary>
internal sealed record FlattenedBlock(uint VariableId, uint StructTypeId, IReadOnlyList<uint> AuthoredMembers);

/// <summary>
/// Every decision the source backend needs that the SPIR-V module itself cannot
/// carry. Names live in the module as <c>OpName</c>; this holds the rest:
/// which semantic each vertex input declares, and which flattened blocks get
/// their members restored to bare symbols.
/// </summary>
internal sealed class EmissionPlan
{
    public List<VertexAttributeSemantic> VertexAttributes { get; } = new();

    public List<FlattenedBlock> FlattenedBlocks { get; } = new();

    public EntryPointSelection EntryPoint { get; init; }

    public uint ShaderModel { get; init; }
}
