using System.Text;

namespace Ruri.ShaderTools.Unity.ShaderLab;

/// <summary>
/// One line naming the generated markers a body actually contains, or null
/// when it contains none.
///
/// A reader arriving with just this file has to be able to tell a recovered
/// name from an authored one — quoting <c>_Stripped_64</c> as a material
/// property and hunting for it in CPU-side code is a real cost. The marker
/// names are already self-describing, so saying what they mean once, in one
/// line, carries the whole legend.
///
/// Returned rather than prepended: the bodies are the largest strings the
/// pipeline handles, and the caller already builds the file around them.
/// </summary>
internal static class GeneratedNameLegend
{
    public static string? For(string body)
    {
        bool stripped = body.Contains(GeneratedNames.StrippedSymbol, StringComparison.Ordinal);
        bool unstructured = body.Contains(GeneratedNames.UnstructuredBlock, StringComparison.Ordinal);
        bool unmapped = body.Contains(GeneratedNames.UnmappedRegion, StringComparison.Ordinal);

        if (!stripped && !unstructured && !unmapped)
        {
            return null;
        }

        var legend = new StringBuilder(160);
        legend.Append("// Generated names (not authored, no CPU-side property matches them):");

        if (stripped)
        {
            legend.Append("  <Buffer>_").Append(GeneratedNames.StrippedSymbol).Append("_<byteOffset> = unnamed member at that offset;");
        }

        if (unstructured)
        {
            legend.Append("  <Buffer>_").Append(GeneratedNames.UnstructuredBlock).Append(" = one member spanning the whole buffer;");
        }

        if (unmapped)
        {
            legend.Append("  <Buffer>_").Append(GeneratedNames.UnmappedRegion).Append(" = byte range no symbol describes;");
        }

        return legend.ToString();
    }
}
