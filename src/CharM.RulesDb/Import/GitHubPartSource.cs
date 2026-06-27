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
        var parts = new List<RemotePartInfo>();
        foreach (var folder in _folders)
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/contents/{folder}?ref={Uri.EscapeDataString(_ref)}";
            List<GitHubContentEntry>? entries;
            try
            {
                entries = await _http.GetFromJsonAsync<List<GitHubContentEntry>>(url, cancellationToken);
            }
            catch (HttpRequestException)
            {
                continue; // folder may not exist in this repo/ref
            }

            if (entries is null) continue;

            foreach (var e in entries)
            {
                if (!string.Equals(e.Type, "file", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(e.DownloadUrl)) continue;

                parts.Add(new RemotePartInfo
                {
                    PartId = $"{folder}/{e.Name}",
                    Filename = e.Name,
                    Category = folder,
                    Version = null,        // not known without fetching the file
                    ContentHash = e.Sha,   // git blob SHA — stable content id
                    DownloadUrl = e.DownloadUrl!,
                });
            }
        }
        return parts;
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
