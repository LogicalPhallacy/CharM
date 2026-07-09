using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CharM.RulesDb.Import;

/// <summary>
/// Shared helpers for turning CBLoader <c>.index</c> files into a
/// <see cref="PartIndexCatalog"/> and into <see cref="RemotePartInfo"/> entries.
/// Used by both <see cref="GitHubPartSource"/> and
/// <see cref="WebPageIndexPartSource"/> so the two can't drift on how indexes
/// are fetched, parsed, and categorized.
/// </summary>
public static partial class PartIndexFetcher
{
    /// <summary>Fetch and parse a single <c>.index</c> document, or null on failure.</summary>
    public static async Task<PartIndexDocument?> FetchIndexAsync(
        HttpClient http, Uri indexUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var xml = await http.GetStringAsync(indexUrl, cancellationToken);
            var doc = XDocument.Parse(xml);
            return PartIndexDocument.Parse(doc, GetFileName(indexUrl));
        }
        catch (HttpRequestException) { return null; }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// Extract absolute links to <c>.index</c> (and, when
    /// <paramref name="includeParts"/> is true, direct <c>.part</c>) files from
    /// an HTML page. The page cannot be crawled as a directory, so links are
    /// scraped from the anchor <c>href</c> attributes and resolved against
    /// <paramref name="pageUri"/>. Duplicate URLs are collapsed.
    /// </summary>
    public static IReadOnlyList<Uri> ScrapeLinks(string html, Uri pageUri, bool includeParts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Uri>();
        foreach (Match m in HrefRegex().Matches(html))
        {
            string href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (href.Length == 0) continue;

            bool isIndex = href.EndsWith(".index", StringComparison.OrdinalIgnoreCase);
            bool isPart = href.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
            if (!isIndex && !(includeParts && isPart)) continue;

            if (!Uri.TryCreate(pageUri, href, out var abs)) continue;
            if (seen.Add(abs.AbsoluteUri)) result.Add(abs);
        }
        return result;
    }

    /// <summary>
    /// Turn a parsed index document into <see cref="RemotePartInfo"/> entries,
    /// resolving each part's download URL relative to the index URL and tagging
    /// it with the index-derived category + official flag.
    /// </summary>
    public static IEnumerable<RemotePartInfo> ToRemoteParts(PartIndexDocument document, Uri indexUrl)
    {
        var obsolete = document.ObsoleteFilenames;
        foreach (var (filename, address) in document.Parts)
        {
            if (obsolete.Contains(filename)) continue;
            if (!Uri.TryCreate(indexUrl, address, out var partUri)) continue;

            yield return new RemotePartInfo
            {
                PartId = PartIdFromUri(partUri, filename),
                Filename = filename,
                Category = document.Category,
                IsOfficial = document.IsOfficial,
                Version = null,
                ContentHash = null,
                DownloadUrl = partUri.ToString(),
            };
        }
    }

    /// <summary>A direct part link (not routed through an index): folder-categorized, not official.</summary>
    public static RemotePartInfo DirectPart(Uri partUri)
    {
        string filename = GetFileName(partUri);
        return new RemotePartInfo
        {
            PartId = PartIdFromUri(partUri, filename),
            Filename = filename,
            Category = FolderCategory(partUri),
            IsOfficial = false,
            DownloadUrl = partUri.ToString(),
        };
    }

    /// <summary>Stable id <c>folder/filename</c> from a part URL (matches the GitHub convention).</summary>
    private static string PartIdFromUri(Uri partUri, string filename)
    {
        var segments = partUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[^2]}/{filename}" : filename;
    }

    /// <summary>The physical folder segment of a part URL (e.g. <c>Homebrew</c>), or null.</summary>
    private static string? FolderCategory(Uri partUri)
    {
        var segments = partUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : null;
    }

    private static string GetFileName(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : uri.AbsolutePath;
    }

    [GeneratedRegex("""href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
}
