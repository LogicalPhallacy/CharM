namespace CharM.RulesDb.Import;

/// <summary>How an installed part compares to what a remote source offers.</summary>
public enum PartUpdateStatus
{
    /// <summary>Installed and matches the remote fingerprint.</summary>
    UpToDate,
    /// <summary>Available remotely but not installed.</summary>
    New,
    /// <summary>Installed; remote fingerprint differs (newer content available).</summary>
    Updated,
    /// <summary>Installed but no longer offered by the remote source.</summary>
    MissingRemotely,
    /// <summary>Installed locally with no remote fingerprint — not remotely managed.</summary>
    Unmanaged,
}

/// <summary>One row of an update report.</summary>
public sealed record PartUpdateEntry(
    string PartId,
    PartUpdateStatus Status,
    string? InstalledVersion,
    string? RemoteVersion,
    RemotePartInfo? Remote);

/// <summary>Full comparison of installed parts vs a remote source listing.</summary>
public sealed record PartUpdateReport(IReadOnlyList<PartUpdateEntry> Entries)
{
    public IEnumerable<PartUpdateEntry> New => Entries.Where(e => e.Status == PartUpdateStatus.New);
    public IEnumerable<PartUpdateEntry> Updated => Entries.Where(e => e.Status == PartUpdateStatus.Updated);
    public IEnumerable<PartUpdateEntry> MissingRemotely => Entries.Where(e => e.Status == PartUpdateStatus.MissingRemotely);

    /// <summary>True when anything is new or updated (the actionable signal).</summary>
    public bool HasUpdates => Entries.Any(e => e.Status is PartUpdateStatus.New or PartUpdateStatus.Updated);
}

/// <summary>
/// Compares the installed manifest against a remote source listing to find new,
/// updated, and removed parts. Matching is by PartId. Update detection uses the
/// stored remote fingerprint (<see cref="PartManifestEntry.SourceHash"/>) vs the
/// remote's <see cref="RemotePartInfo.ContentHash"/>; falls back to version
/// comparison when hashes aren't comparable.
/// </summary>
public static class PartUpdateChecker
{
    public static async Task<PartUpdateReport> CheckAsync(
        IPartSource source, PartManifest installed, CancellationToken cancellationToken = default)
    {
        var remote = await source.ListAsync(cancellationToken);
        return Compare(installed, remote);
    }

    public static PartUpdateReport Compare(PartManifest installed, IReadOnlyList<RemotePartInfo> remote)
    {
        var remoteById = remote.ToDictionary(r => r.PartId, StringComparer.OrdinalIgnoreCase);
        var installedById = installed.Parts.ToDictionary(p => p.PartId, StringComparer.OrdinalIgnoreCase);

        var entries = new List<PartUpdateEntry>();

        foreach (var inst in installed.Parts)
        {
            if (!remoteById.TryGetValue(inst.PartId, out var rem))
            {
                // A part with no recorded source fingerprint was added locally,
                // not from this (or any) remote — report it as unmanaged rather
                // than "missing". Parts that WERE remotely sourced but are no
                // longer offered are genuinely missing.
                var absentStatus = string.IsNullOrEmpty(inst.SourceHash)
                    ? PartUpdateStatus.Unmanaged
                    : PartUpdateStatus.MissingRemotely;
                entries.Add(new PartUpdateEntry(inst.PartId, absentStatus, inst.Version, null, null));
                continue;
            }

            var status = CompareOne(inst, rem);
            entries.Add(new PartUpdateEntry(inst.PartId, status, inst.Version, rem.Version, rem));
        }

        foreach (var rem in remote)
        {
            if (!installedById.ContainsKey(rem.PartId))
                entries.Add(new PartUpdateEntry(rem.PartId, PartUpdateStatus.New, null, rem.Version, rem));
        }

        entries.Sort((a, b) => string.Compare(a.PartId, b.PartId, StringComparison.OrdinalIgnoreCase));
        return new PartUpdateReport(entries);
    }

    private static PartUpdateStatus CompareOne(PartManifestEntry installed, RemotePartInfo remote)
    {
        // Preferred: compare the remote-provided fingerprint we recorded at install.
        if (installed.SourceHash is { Length: > 0 } && remote.ContentHash is { Length: > 0 })
            return string.Equals(installed.SourceHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase)
                ? PartUpdateStatus.UpToDate
                : PartUpdateStatus.Updated;

        // Fallback: version comparison when both are known.
        if (installed.Version is { Length: > 0 } && remote.Version is { Length: > 0 })
            return string.Equals(installed.Version, remote.Version, StringComparison.OrdinalIgnoreCase)
                ? PartUpdateStatus.UpToDate
                : PartUpdateStatus.Updated;

        // No comparable fingerprint — installed locally / not remotely managed.
        return PartUpdateStatus.Unmanaged;
    }
}
