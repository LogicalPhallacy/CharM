using System.Text;
using System.Text.RegularExpressions;
using CharM.Engine.Rules;

namespace CharM.Engine.Prerequisites;

/// <summary>
/// Render a <c>&lt;RulesElement&gt;</c>'s raw <c>Prereqs</c> string (or its
/// <c>print-prereqs</c> override field) into human-readable plain text.
///
/// <para>The raw <c>Prereqs</c> string is the engine-readable form: it
/// can contain bare <c>ID_*</c> internal-id references, the negation
/// prefixes <c>!</c> and <c>~</c>, and the operator tokens <c>|</c>
/// (OR), <c>&amp;</c> / <c>,</c> / <c>;</c> (AND). Rules data ships a
/// pre-baked <c>print-prereqs</c> field for most user-facing elements,
/// but plenty of feats (notably the Arena Fighter variants like
/// <c>Spear Expertise</c> with <c>!ID_INTERNAL_ARENA_WEAPON_CATEGORY_SPEAR</c>)
/// have only the raw form, leaving UI consumers to display the raw IDs
/// to the user.</para>
///
/// <para>Resolution strategy:</para>
/// <list type="number">
///   <item>If <c>print-prereqs</c> is set, return it verbatim.</item>
///   <item>Otherwise walk the raw string, replace each <c>ID_*</c>
///     token with the display name of the element it refers to
///     (via the supplied <paramref name="findById"/> resolver), and
///     rewrite the engine operators to plain English connectors.</item>
/// </list>
/// </summary>
public static class PrereqDisplay
{
    private static readonly Regex IdToken = new(@"ID_[A-Z0-9_]+", RegexOptions.Compiled);

    /// <summary>
    /// Build a plain-English prerequisite string for <paramref name="element"/>.
    /// Returns <c>null</c> when the element has no prereqs to render.
    /// </summary>
    public static string? Format(RulesElement element, Func<string, RulesElement?>? findById)
    {
        if (element.Fields.TryGetValue("print-prereqs", out var printPrereqs)
            && !string.IsNullOrWhiteSpace(printPrereqs))
        {
            return printPrereqs;
        }

        if (element.Fields.TryGetValue("_print-prereqs", out printPrereqs)
            && !string.IsNullOrWhiteSpace(printPrereqs))
        {
            return printPrereqs;
        }

        return FormatRaw(element.Prereqs, findById);
    }

    /// <summary>
    /// Resolve a raw prereq string directly. Splits on <c>;</c> first
    /// (top-level AND), then commas, then <c>|</c> (OR groups). IDs are
    /// resolved to their element name via <paramref name="findById"/>;
    /// unresolved IDs fall back to a humanized form of the token
    /// (e.g. <c>ID_INTERNAL_ARENA_WEAPON_CATEGORY_SPEAR</c> →
    /// <c>arena weapon category spear</c>).
    /// </summary>
    public static string? FormatRaw(string? prereqs, Func<string, RulesElement?>? findById)
    {
        if (string.IsNullOrWhiteSpace(prereqs))
            return null;

        var semicolonClauses = prereqs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Select(c => FormatCommaChain(c, findById))
            .Where(s => !string.IsNullOrWhiteSpace(s));

        return string.Join("; ", semicolonClauses);
    }

    private static string FormatCommaChain(string text, Func<string, RulesElement?>? findById)
    {
        var commaParts = text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => FormatOrChain(p, findById))
            .Where(s => !string.IsNullOrWhiteSpace(s));

        return string.Join(", ", commaParts);
    }

    private static string FormatOrChain(string text, Func<string, RulesElement?>? findById)
    {
        // | inside a comma segment is OR. & inside the same segment is
        // AND (used by category-style prereqs like
        // "ID_X&Paragon Tier" — render with the word "and").
        var orParts = text.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => FormatAndChain(p, findById))
            .ToList();

        return string.Join(" or ", orParts);
    }

    private static string FormatAndChain(string text, Func<string, RulesElement?>? findById)
    {
        var andParts = text.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => FormatAtom(p, findById))
            .ToList();

        return string.Join(" and ", andParts);
    }

    private static string FormatAtom(string text, Func<string, RulesElement?>? findById)
    {
        text = text.Trim();
        if (text.Length == 0) return string.Empty;

        bool negate = false;
        while (text.StartsWith('!') || text.StartsWith('~'))
        {
            negate = !negate;
            text = text[1..].Trim();
        }

        string resolved = ResolveIds(text, findById);
        return negate ? $"not {resolved}" : resolved;
    }

    private static string ResolveIds(string text, Func<string, RulesElement?>? findById)
    {
        if (findById is null || !text.Contains("ID_", StringComparison.Ordinal))
            return text;

        return IdToken.Replace(text, match =>
        {
            var resolved = findById(match.Value);
            if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.Name))
                return resolved.Name;
            return HumanizeId(match.Value);
        });
    }

    private static string HumanizeId(string id)
    {
        // Strip the common ID_PREFIX_ scaffolding tokens, then turn
        // underscores into spaces and lower-case the result.
        var trimmed = id;
        foreach (var prefix in _idPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
            sb.Append(ch == '_' ? ' ' : char.ToLowerInvariant(ch));
        return sb.ToString().Trim();
    }

    private static readonly string[] _idPrefixes =
    [
        "ID_INTERNAL_",
        "ID_FMP_",
        "ID_WOG_",
        "ID_TIV_",
        "ID_DBB_",
        "ID_LFR_",
        "ID_CDJ_",
        "ID_",
    ];
}
