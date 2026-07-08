namespace CharM.RulesDb.Import;

/// <summary>
/// Lists and downloads parts from a CBLoader-style HTML landing page (e.g.
/// <c>https://cbloader.vorpald20.com/</c>). The page cannot be crawled as a
/// directory, so the <c>.index</c> links are scraped out of the page HTML; each
/// index is then fetched and parsed. Categories and the "official" flag come
/// from the index that references each part (index name → category, and
/// <c>&lt;Description category="Official"&gt;</c> → official). Any direct
/// <c>.part</c> links on the page are included too, categorized by their
/// physical folder.
/// </summary>
public sealed class WebPageIndexPartSource : IPartSource
{
    private readonly Uri _pageUri;
    private readonly HttpClient _http;

    public WebPageIndexPartSource(string pageUrl, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
        _pageUri = new Uri(pageUrl, UriKind.Absolute);
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CharM-PartSource/1.0");
    }

    public string DisplayName => $"Index page {_pageUri}";

    public async Task<IReadOnlyList<RemotePartInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        string html = await _http.GetStringAsync(_pageUri, cancellationToken);
        var links = PartIndexFetcher.ScrapeLinks(html, _pageUri, includeParts: true);

        var parts = new List<RemotePartInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexes = new List<PartIndexDocument>();

        foreach (var link in links)
        {
            if (link.AbsolutePath.EndsWith(".index", StringComparison.OrdinalIgnoreCase))
            {
                var doc = await PartIndexFetcher.FetchIndexAsync(_http, link, cancellationToken);
                if (doc is null) continue;
                indexes.Add(doc);
                foreach (var part in PartIndexFetcher.ToRemoteParts(doc, link))
                    if (seen.Add(part.PartId)) parts.Add(part);
            }
        }

        // Direct .part links (rare): only add ones no index already covered.
        foreach (var link in links)
        {
            if (link.AbsolutePath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                var part = PartIndexFetcher.DirectPart(link);
                if (seen.Add(part.PartId)) parts.Add(part);
            }
        }

        // Load order: WotC → Unearthed Arcana → other indexes alphabetically,
        // parts alphabetical within each index (shared with every source).
        return PartLoadOrder.Order(parts, p => p.Filename, indexes);
    }

    public async Task<byte[]> DownloadAsync(RemotePartInfo part, CancellationToken cancellationToken = default)
        => await _http.GetByteArrayAsync(part.DownloadUrl, cancellationToken);
}
