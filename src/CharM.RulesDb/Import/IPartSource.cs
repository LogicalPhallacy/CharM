namespace CharM.RulesDb.Import;

/// <summary>
/// A part available from a remote source (GitHub repo or CBLoader host),
/// described well enough to decide whether it is new/updated relative to what
/// is installed, and to download it on demand.
/// </summary>
public sealed record RemotePartInfo
{
    /// <summary>Stable id, typically <c>folder/filename</c> (e.g. <c>sorted/06-races.part</c>).</summary>
    public required string PartId { get; init; }

    /// <summary>Bare file name (e.g. <c>06-races.part</c>).</summary>
    public required string Filename { get; init; }

    /// <summary>Category bucket: sorted | UnearthedArcana | Homebrew | 3rdParty, or null.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// <c>&lt;UpdateInfo&gt;&lt;Version&gt;</c> when known (CBLoader host exposes
    /// this in versions2.txt; GitHub does not without fetching the file).
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// A content fingerprint usable for change detection. For the CBLoader host
    /// this is the versions2.txt hash; for GitHub it is the git blob SHA. Either
    /// way, a change here means the part content changed.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>Absolute URL to download the raw part bytes.</summary>
    public required string DownloadUrl { get; init; }
}

/// <summary>
/// A source of CBLoader part files: lists what's available and downloads bytes.
/// Implementations: <see cref="GitHubPartSource"/> and
/// <see cref="CbloaderHostPartSource"/>.
/// </summary>
public interface IPartSource
{
    /// <summary>Human-readable identifier for status/UI (e.g. "GitHub LogicalPhallacy/cbparts").</summary>
    string DisplayName { get; }

    /// <summary>Enumerate all available parts across the configured folders.</summary>
    Task<IReadOnlyList<RemotePartInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Download the raw bytes of a part.</summary>
    Task<byte[]> DownloadAsync(RemotePartInfo part, CancellationToken cancellationToken = default);
}
