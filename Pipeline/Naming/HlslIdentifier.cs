using System.Text;

namespace Ruri.ShaderTools.Pipeline.Naming;

/// <summary>
/// Turns an author-supplied symbol name into the identifier the source backend
/// emits for it UNCHANGED — the fixed point of its sanitiser — so the name
/// injected into the module is the name that comes back out.
///
/// The backend replaces every character outside <c>[A-Za-z0-9_]</c> with an
/// underscore, collapses runs of underscores, and will not accept a leading
/// digit; a name already in that form passes through it untouched. Nothing else
/// is normalised. In particular an author's leading underscore is part of the
/// name: Unity binds <c>_Color</c> and <c>unity_OrthoParams</c> by exactly that
/// spelling, and trimming it would manufacture a property nothing on the CPU
/// side ever sets.
///
/// Encoding-agnostic by construction: CJK, Arabic, emoji, punctuation and
/// whitespace all take the same replace-then-collapse path. No character lists.
/// </summary>
internal static class HlslIdentifier
{
    /// <summary>
    /// Sanitise, collapse underscore runs, and guard a leading digit. Returns
    /// empty when no alphanumeric character survives — the caller substitutes an
    /// offset-based placeholder rather than emitting a member named <c>_</c>.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length + 1);
        bool anyAlphanumeric = false;

        foreach (char c in raw)
        {
            bool isAlphanumeric = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            anyAlphanumeric |= isAlphanumeric;
            builder.Append(isAlphanumeric || c == '_' ? c : '_');
        }

        if (!anyAlphanumeric)
        {
            return string.Empty;
        }

        if (builder[0] >= '0' && builder[0] <= '9')
        {
            builder.Insert(0, '_');
        }

        return CollapseUnderscores(builder.ToString());
    }

    /// <summary>
    /// Runs of underscores collapsed to one: the backend's own rule, applied both
    /// to every identifier it sanitises and to the <c>variable_member</c>
    /// identifiers it builds when it flattens a block.
    /// </summary>
    public static string CollapseUnderscores(string value)
    {
        if (!HasUnderscoreRun(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        bool lastWasUnderscore = false;

        foreach (char c in value)
        {
            bool isUnderscore = c == '_';
            if (isUnderscore && lastWasUnderscore)
            {
                continue;
            }

            builder.Append(c);
            lastWasUnderscore = isUnderscore;
        }

        return builder.ToString();
    }

    private static bool HasUnderscoreRun(string value)
    {
        bool lastWasUnderscore = false;
        foreach (char c in value)
        {
            bool isUnderscore = c == '_';
            if (isUnderscore && lastWasUnderscore)
            {
                return true;
            }

            lastWasUnderscore = isUnderscore;
        }

        return false;
    }

    /// <summary>
    /// Marker for a member whose name did not survive compilation.
    ///
    /// The name STATES WHAT HAPPENED rather than looking like an identifier. A
    /// bare <c>f_2032</c> reads as an ordinary variable, so a later reader — human
    /// or model — has no way to tell it apart from a real author name, may quote
    /// it as one, and will waste time hunting for a matching CPU-side property
    /// that does not exist. Spelling out "symbol stripped" removes that whole
    /// class of mistake at zero cost.
    ///
    /// The byte offset is kept because it is the member's ONLY surviving
    /// identity: it is what the metadata is keyed on, what a future symbol source
    /// would be matched against, and what makes the name stable across runs.
    /// </summary>
    public static string PlaceholderAt(int byteOffset) => $"{StrippedSymbolMarker}_{byteOffset}";

    /// <summary>
    /// Marker for a block that could not be split into members at all — the one
    /// member spans the whole buffer. Deliberately NOT the offset form: nothing
    /// was stripped at offset 0, the structuring simply did not happen, and
    /// saying so is a different fact.
    /// </summary>
    public static string UnstructuredBlockName => GeneratedNames.UnstructuredBlock;

    private const string StrippedSymbolMarker = GeneratedNames.StrippedSymbol;

    /// <summary>Disambiguator appended when two members would become one identifier.</summary>
    public static string DisambiguateAt(string sanitized, int byteOffset) => $"{sanitized}_at_{byteOffset}";
}
