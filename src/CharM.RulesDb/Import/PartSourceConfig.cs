using System.Text.Json.Serialization;

namespace CharM.RulesDb.Import;

/// <summary>Which kind of remote source to talk to.</summary>
public enum PartSourceKind
{
    GitHub,
    CbloaderHost,
}

/// <summary>
/// User-editable configuration for the remote part source. Intentionally has
/// NO hard-coded default location — the caller supplies one (tests and the
/// initial UI use <c>LogicalPhallacy/cbparts</c>). Persisted as JSON.
/// </summary>
public sealed class PartSourceConfig
{
    public PartSourceKind Kind { get; set; } = PartSourceKind.GitHub;

    // ---- GitHub ----
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public string Ref { get; set; } = "master";

    /// <summary>
    /// Folders in the repo (GitHub) to enumerate for parts. Defaults to the
    /// cbparts layout. Each maps to a category bucket of the same name.
    /// </summary>
    public List<string> Folders { get; set; } = ["sorted", "UnearthedArcana", "Homebrew", "3rdParty"];

    // ---- CBLoader host ----
    /// <summary>Base URL exposing versions2.txt + part files (e.g. https://cbloader.vorpald20.com/).</summary>
    public string? HostBaseUrl { get; set; }

    /// <summary>True when the config has enough information to construct a source.</summary>
    [JsonIgnore]
    public bool IsComplete => Kind switch
    {
        PartSourceKind.GitHub => !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repo),
        PartSourceKind.CbloaderHost => !string.IsNullOrWhiteSpace(HostBaseUrl),
        _ => false,
    };

    /// <summary>Convenience constructor for the test/initial GitHub source.</summary>
    public static PartSourceConfig GitHubRepo(string owner, string repo, string @ref = "master") => new()
    {
        Kind = PartSourceKind.GitHub,
        Owner = owner,
        Repo = repo,
        Ref = @ref,
    };

    public static PartSourceConfig CbloaderHost(string baseUrl) => new()
    {
        Kind = PartSourceKind.CbloaderHost,
        HostBaseUrl = baseUrl,
    };
}

/// <summary>Builds an <see cref="IPartSource"/> from a <see cref="PartSourceConfig"/>.</summary>
public static class PartSourceFactory
{
    public static IPartSource Create(PartSourceConfig config, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.IsComplete)
            throw new ArgumentException("Part source configuration is incomplete.", nameof(config));

        return config.Kind switch
        {
            PartSourceKind.GitHub => new GitHubPartSource(
                config.Owner!, config.Repo!, config.Ref, config.Folders, httpClient),
            PartSourceKind.CbloaderHost => new CbloaderHostPartSource(config.HostBaseUrl!, httpClient),
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
    }
}
