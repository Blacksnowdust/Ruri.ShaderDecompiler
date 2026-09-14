using Ruri.ShaderTools.Pipeline.Backend;

namespace Ruri.ShaderTools.Unity;

/// <summary>
/// The semantic Unity binds to each vertex input location, in
/// <c>VertexAttribute</c> enum order. A Vulkan module carries only the location,
/// and Unity assigned that location from this very order — so for a
/// Unity-sourced module the location is the semantic.
///
/// Every entry is registered; the backend consults a remap only for locations
/// the module actually declares, so absent ones cost nothing. Locations past the
/// table are left to the backend's default: an engine may feed extra streams
/// there, and inventing a semantic for one is the exact mistake this table
/// exists to prevent.
/// </summary>
internal static class UnityVertexAttributeOrder
{
    private static readonly VertexAttributeSemantic[] Table =
    [
        new(0, "POSITION0"),
        new(1, "NORMAL0"),
        new(2, "TANGENT0"),
        new(3, "COLOR0"),
        new(4, "TEXCOORD0"),
        new(5, "TEXCOORD1"),
        new(6, "TEXCOORD2"),
        new(7, "TEXCOORD3"),
        new(8, "TEXCOORD4"),
        new(9, "TEXCOORD5"),
        new(10, "TEXCOORD6"),
        new(11, "TEXCOORD7"),
        new(12, "BLENDWEIGHT0"),
        new(13, "BLENDINDICES0"),
    ];

    public static void AppendAll(List<VertexAttributeSemantic> attributes) => attributes.AddRange(Table);
}
