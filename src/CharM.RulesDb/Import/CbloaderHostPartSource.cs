using System.Xml.Linq;

namespace CharM.RulesDb.Import;

/// <summary>
/// Lists and downloads parts from a CBLoader-style host (e.g.
/// <c>https://cbloader.vorpald20.com/</c>). Versions and content hashes come
/// from <c>versions2.txt</c> (<c>name:sha256:version</c> lines); download URLs
/// and categories come from the host's index file (<c>WotC.index</c> /
/// <c>index.xml</c>) whose &lt;Part&gt; entries carry &lt;Filename&gt; and
/// &lt;PartAddress&gt;. Tolerant of a missing index or missing versions file.
/// </summary>
public sealed class CbloaderHostPartSource : IPartSource
{
    private readonly Uri _base;
    private readonly HttpClient _http;

    public CbloaderHostPartSource(string baseUrl, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        _base = new Uri(baseUrl, UriKind.Absolute);
        _http = httpClient ?? new HttpClient();
    }

    public string DisplayName => $"CBLoader host {_base}";

    public async Task<IReadOnlyList<RemotePartInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var versions = await LoadVersionsAsync(cancellationToken);
        var entries = await LoadIndexAsync(cancellationToken);

        var parts = new List<RemotePartInfo>();
        foreach (var (filename, address) in entries)
        {
            var downloadUrl = new Uri(_base, address).ToString();
            versions.TryGetValue(filename, out var v);
            parts.Add(new RemotePartInfo
            {
                PartId = address.TrimStart('/'),
                Filename = filename,
                Category = CategoryFromAddress(address),
                Version = v.Version,
                ContentHash = v.Hash,
                DownloadUrl = downloadUrl,
            });
        }

        // If the index gave us nothing, fall back to versions2.txt filenames
        // with naive base-relative download URLs.
        if (parts.Count == 0)
        {
            foreach (var (filename, v) in versions)
            {
                parts.Add(new RemotePartInfo
                {
                    PartId = filename,
                    Filename = filename,
                    Category = null,
                    Version = v.Version,
                    ContentHash = v.Hash,
                    DownloadUrl = new Uri(_base, filename).ToString(),
                });
            }
        }

        return parts;
    }

    public async Task<byte[]> DownloadAsync(RemotePartInfo part, CancellationToken cancellationToken = default)
        => await _http.GetByteArrayAsync(part.DownloadUrl, cancellationToken);

    private async Task<Dictionary<string, (string? Hash, string? Version)>> LoadVersionsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var text = await _http.GetStringAsync(new Uri(_base, "versions2.txt"), ct);
            foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("CBLoader", StringComparison.OrdinalIgnoreCase)) continue;
                var cols = line.Split(':');
                if (cols.Length >= 3)
                    map[cols[0]] = (cols[1], cols[2]);
            }
        }
        catch (HttpRequestException) { /* versions file optional */ }
        return map;
    }

    private async Task<List<(string Filename, string Address)>> LoadIndexAsync(CancellationToken ct)
    {
        var result = new List<(string, string)>();
        foreach (var indexName in new[] { "WotC.index", "index.xml" })
        {
            try
            {
                var xml = await _http.GetStringAsync(new Uri(_base, indexName), ct);
                var doc = XDocument.Parse(xml);
                result.AddRange(PartIndexReader.ReadPartEntries(doc));
                if (result.Count > 0) return result;
            }
            catch (HttpRequestException) { /* try next index name */ }
            catch (System.Xml.XmlException) { /* malformed; try next */ }
        }
        return result;
    }

    private static string? CategoryFromAddress(string address)
    {
        // e.g. https://host/sorted/06-races.part  OR  /sorted/06-races.part
        var segments = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        var folder = segments[^2];
        return folder.Contains('.') ? null : folder; // skip host segment
    }
}
