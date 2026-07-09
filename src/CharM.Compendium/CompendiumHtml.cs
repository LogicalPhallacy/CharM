using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CharM.Compendium;

/// <summary>
/// Helpers for the HTML rules pages stored in the <c>Txt</c> column of the
/// SQL-dump and SQLite compendium sources. The pages are full HTML documents;
/// the entry body is the content of <c>&lt;div id="detail"&gt;…&lt;/div&gt;</c>.
/// </summary>
public static partial class CompendiumHtml
{
    /// <summary>
    /// Reduce a full compendium HTML page to the entry body — the content of
    /// <c>&lt;div id="detail"&gt;…&lt;/div&gt;</c> — dropping the document chrome and the
    /// trailing script. Falls back to the whole string if the div isn't found.
    /// </summary>
    public static string ExtractDetail(string html)
    {
        var m = DetailDivRegex().Match(html);
        return m.Success ? m.Groups[1].Value.Trim() : html.Trim();
    }

    /// <summary>Strip HTML tags/entities to whitespace-collapsed plain text.</summary>
    public static string HtmlToText(string html)
    {
        var noTags = TagRegex().Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        var sb = new StringBuilder(decoded.Length);
        bool lastSpace = false;
        foreach (var ch in decoded)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
            }
            else { sb.Append(ch); lastSpace = false; }
        }
        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"<div\s+id=""detail"">(.*?)</div>\s*</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DetailDivRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
