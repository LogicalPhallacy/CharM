using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CharM.RulesDb.Import;

/// <summary>
/// Lists and downloads parts from a GitHub repository using the public contents
/// API. The folder layout supplies categories (sorted / UnearthedArcana /
/// Homebrew / 3rdParty). The git blob SHA returned by the API is used as the
/// content fingerprint for update detection — cheap (no file download needed to
/// build the catalog). The actual <c>&lt;Version&gt;</c> is read after download.
/// </summary>
public sealed class GitHubPartSource : IPartSource
{
    /// <summary>Repo folder holding the CBLoader <c>.index</c> files (cbparts convention).</summary>
    private const string IndexesFolder = "indexes";

    private readonly string _owner;
    private readonly string _repo;
    private readonly string _ref;
    private readonly IReadOnlyList<string> _folders;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GitHubPartSource(
        string owner, string repo, string @ref, IReadOnlyList<string> folders, HttpClient? httpClient = null)
    {
        _owner = owner;
        _repo = repo;
        _ref = string.IsNullOrWhiteSpace(@ref) ? "master" : @ref;
        _folders = folders is { Count: > 0 } ? folders : [RulePartCategories.Sorted];
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient();
        // GitHub requires a User-Agent on API requests.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CharM-PartSource/1.0");
    }

    public string DisplayName => $"GitHub {_owner}/{_repo}@{_ref}";

    public async Task<IReadOnlyList<RemotePartInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var (catalog, indexes) = await BuildIndexCatalogAsync(cancellationToken);

        var parts = new List<RemotePartInfo>();
        foreach (var folder in _folders)
        {
            var entries = await ListFolderAsync(folder, cancellationToken);
            foreach (var e in entries)
            {
                if (!string.Equals(e.Type, "file", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(e.DownloadUrl)) continue;

                parts.Add(new RemotePartInfo
                {
                    PartId = $"{folder}/{e.Name}",
                    Filename = e.Name,
                    // Category + official come from the index that references
                    // this part; fall back to the physical folder name.
                    Category = catalog.CategoryFor(e.Name, folder),
                    IsOfficial = catalog.IsOfficial(e.Name),
                    Version = null,        // not known without fetching the file
                    ContentHash = e.Sha,   // git blob SHA — stable content id
                    DownloadUrl = e.DownloadUrl!,
                });
            }
        }

        // Load order: WotC → Unearthed Arcana → other indexes alphabetically,
        // parts alphabetical within each index (shared with every source).
        return PartLoadOrder.Order(parts, p => p.Filename, indexes);
    }

    /// <summary>
    /// Build a <see cref="PartIndexCatalog"/> (+ the parsed index documents that
    /// drive load order) from the repo's <c>indexes/</c> folder so parts can be
    /// categorized and ordered by the content pack (index) they belong to rather
    /// than by their physical folder. Missing folder → empty catalog (parts fall
    /// back to folder categorization and alphabetical order).
    /// </summary>
    private async Task<(PartIndexCatalog Catalog, IReadOnlyList<PartIndexDocument> Indexes)> BuildIndexCatalogAsync(
        CancellationToken cancellationToken)
    {
        var catalog = new PartIndexCatalog();
        var indexes = new List<PartIndexDocument>();
        var entries = await ListFolderAsync(IndexesFolder, cancellationToken);
        foreach (var e in entries
                     .Where(e => string.Equals(e.Type, "file", StringComparison.OrdinalIgnoreCase))
                     .Where(e => e.Name.EndsWith(".index", StringComparison.OrdinalIgnoreCase))
                     .Where(e => !string.IsNullOrWhiteSpace(e.DownloadUrl))
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var doc = await PartIndexFetcher.FetchIndexAsync(_http, new Uri(e.DownloadUrl!), cancellationToken);
            if (doc is not null) { indexes.Add(doc); catalog.Add(doc); }
        }
        return (catalog, indexes);
    }

    private async Task<IReadOnlyList<GitHubContentEntry>> ListFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/{folder}?ref={Uri.EscapeDataString(_ref)}";
        try
        {
            return await _http.GetFromJsonAsync<List<GitHubContentEntry>>(url, cancellationToken) ?? [];
        }
        catch (HttpRequestException)
        {
            return []; // folder may not exist in this repo/ref
        }
    }

    public async Task<byte[]> DownloadAsync(RemotePartInfo part, CancellationToken cancellationToken = default)
    {
        return await _http.GetByteArrayAsync(part.DownloadUrl, cancellationToken);
    }

    private sealed class GitHubContentEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    }
}
