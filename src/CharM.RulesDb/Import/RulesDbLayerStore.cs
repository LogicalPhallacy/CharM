using System.Text;
using System.Text.Json;

namespace CharM.RulesDb.Import;

/// <summary>
/// One layer in the materialized rules database: either the immovable base
/// (WotC compendium XML) or a toggleable part file. The manifest is the source
/// of truth for which layers exist and whether they are enabled; the working
/// <c>rules.db</c> is a materialization of the enabled layers in order.
/// </summary>
public sealed class PartManifestEntry
{
    public required string PartId { get; set; }
    public required string Filename { get; set; }
    public string? Category { get; set; }
    public string? Version { get; set; }
    public string? ContentHash { get; set; }
    public string? SourceUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public int LayerOrder { get; set; }

    /// <summary>
    /// Fingerprint reported by the remote source this part was installed from
    /// (GitHub blob SHA or CBLoader versions2 hash). Used for update detection.
    /// Null for locally-uploaded parts (not remotely managed).
    /// </summary>
    public string? SourceHash { get; set; }

    /// <summary>Archive-relative file name holding the raw part bytes.</summary>
    public required string ArchiveFile { get; set; }
}

/// <summary>
/// Persisted manifest describing the base + all known part layers and their
/// enabled state. Lives next to the cached snapshots in the parts archive.
/// </summary>
public sealed class PartManifest
{
    public string? BaseXmlFilename { get; set; }
    public List<PartManifestEntry> Parts { get; set; } = [];
}

/// <summary>
/// Manages the layered materialization of the rules database from a cached base
/// snapshot plus toggleable part overlays. Read path is unaffected — the output
/// is the same flat <c>rules.db</c>. Toggling a part updates the manifest and
/// rebuilds; a checkpoint of the heavy/stable layers (base + sorted) keeps
/// common overlay toggles fast (only the KB-sized overlays re-merge).
/// </summary>
public sealed class RulesDbLayerStore
{
    // Categories treated as "heavy/stable" — folded into the checkpoint so that
    // toggling a light overlay never re-merges the multi-MB official item parts.
    private static readonly HashSet<string> HeavyCategories =
        new(RulePartCategories.HeavyCategories, StringComparer.OrdinalIgnoreCase);

    private readonly string _archiveDir;
    private readonly string _baseSnapshotPath;
    private readonly string _checkpointPath;
    private readonly string _checkpointKeyPath;
    private readonly string _manifestPath;

    public RulesDbLayerStore(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _archiveDir = Path.Combine(workingDirectory, "parts-archive");
        _baseSnapshotPath = Path.Combine(_archiveDir, "base-snapshot.db");
        _checkpointPath = Path.Combine(_archiveDir, "checkpoint.db");
        _checkpointKeyPath = Path.Combine(_archiveDir, "checkpoint.key");
        _manifestPath = Path.Combine(_archiveDir, "manifest.json");
    }

    public string ArchiveDirectory => _archiveDir;
    public string ManifestPath => _manifestPath;
    public bool IsInitialized => File.Exists(_baseSnapshotPath) && File.Exists(_manifestPath);

    /// <summary>
    /// Build the base snapshot from the WotC XML, archive the supplied part
    /// files, create the manifest (all parts enabled), and materialize the
    /// working database at <paramref name="workingDbPath"/>.
    /// </summary>
    public void Initialize(
        string xmlPath,
        IReadOnlyList<(string Path, string PartId, string? Category)> partFiles,
        string workingDbPath,
        IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(_archiveDir);

        progress?.Report("Importing base rules");
        RulesDbBuilder.Import(xmlPath, _baseSnapshotPath);

        var manifest = new PartManifest { BaseXmlFilename = Path.GetFileName(xmlPath) };
        int order = 1;
        foreach (var (path, partId, category) in partFiles)
        {
            var info = SafeReadInfo(path, partId, category);
            if (info.IsObsolete) continue;

            string archiveFile = SafeArchiveName(partId, order);
            File.Copy(path, Path.Combine(_archiveDir, archiveFile), overwrite: true);

            manifest.Parts.Add(new PartManifestEntry
            {
                PartId = partId,
                Filename = info.Filename,
                Category = category,
                Version = info.Version,
                ContentHash = info.ContentHash,
                SourceUrl = info.PartAddress,
                Enabled = true,
                LayerOrder = order++,
                ArchiveFile = archiveFile,
            });
        }

        SaveManifest(manifest);
        InvalidateCheckpoint();
        Rebuild(workingDbPath, progress);
    }

    public PartManifest LoadManifest()
    {
        if (!File.Exists(_manifestPath))
            throw new InvalidOperationException("Layer store is not initialized.");
        return JsonSerializer.Deserialize(File.ReadAllText(_manifestPath), RulesDbJsonContext.Default.PartManifest)
            ?? new PartManifest();
    }

    public void SaveManifest(PartManifest manifest)
    {
        Directory.CreateDirectory(_archiveDir);
        File.WriteAllText(_manifestPath, JsonSerializer.Serialize(manifest, RulesDbJsonContext.Default.PartManifest));
    }

    /// <summary>Flip a part's enabled flag in the manifest (does not rebuild).</summary>
    public PartManifest SetEnabled(string partId, bool enabled)
    {
        var manifest = LoadManifest();
        var entry = manifest.Parts.FirstOrDefault(p =>
            string.Equals(p.PartId, partId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown part: {partId}", nameof(partId));
        entry.Enabled = enabled;
        SaveManifest(manifest);
        return manifest;
    }

    /// <summary>
    /// Download and install (or update) parts from a remote source: writes the
    /// bytes into the archive, records the source fingerprint + version for
    /// later update detection, and adds/updates the manifest entry. Does NOT
    /// rebuild — call <see cref="Rebuild"/> afterwards. New parts are appended
    /// after existing layers (enabled by default); updated parts keep their
    /// order and enabled state. Returns the parts that changed.
    /// </summary>
    public async Task<IReadOnlyList<string>> InstallRemotePartsAsync(
        IPartSource source,
        IEnumerable<RemotePartInfo> parts,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_archiveDir);
        var manifest = LoadManifest();
        int nextOrder = manifest.Parts.Count > 0 ? manifest.Parts.Max(p => p.LayerOrder) + 1 : 1;
        var changed = new List<string>();

        foreach (var remote in parts)
        {
            byte[] bytes = await source.DownloadAsync(remote, cancellationToken);
            var info = PartMetadataReader.Read(bytes, remote.Filename, remote.PartId, remote.Category);
            if (info.IsObsolete) continue;

            var existing = manifest.Parts.FirstOrDefault(p =>
                string.Equals(p.PartId, remote.PartId, StringComparison.OrdinalIgnoreCase));

            string archiveFile = existing?.ArchiveFile
                ?? SafeArchiveName(remote.PartId, existing?.LayerOrder ?? nextOrder);
            await File.WriteAllBytesAsync(Path.Combine(_archiveDir, archiveFile), bytes, cancellationToken);

            if (existing is null)
            {
                manifest.Parts.Add(new PartManifestEntry
                {
                    PartId = remote.PartId,
                    Filename = info.Filename,
                    Category = remote.Category,
                    Version = info.Version ?? remote.Version,
                    ContentHash = info.ContentHash,
                    SourceHash = remote.ContentHash,
                    SourceUrl = remote.DownloadUrl,
                    Enabled = true,
                    LayerOrder = nextOrder++,
                    ArchiveFile = archiveFile,
                });
            }
            else
            {
                existing.Filename = info.Filename;
                existing.Category = remote.Category ?? existing.Category;
                existing.Version = info.Version ?? remote.Version ?? existing.Version;
                existing.ContentHash = info.ContentHash;
                existing.SourceHash = remote.ContentHash;
                existing.SourceUrl = remote.DownloadUrl;
                existing.ArchiveFile = archiveFile;
            }

            changed.Add(remote.PartId);
        }

        if (changed.Count > 0)
        {
            SaveManifest(manifest);
            InvalidateCheckpoint(); // installed/updated parts may be heavy
        }

        return changed;
    }

    /// <summary>
    /// Materialize the working database from the base snapshot + currently
    /// enabled parts. Uses the heavy-layer checkpoint when the heavy set is
    /// unchanged so only light overlays re-merge.
    /// </summary>
    public MergeResult Rebuild(string workingDbPath, IProgress<string>? progress = null)
    {
        var manifest = LoadManifest();
        var enabled = manifest.Parts
            .Where(p => p.Enabled)
            .OrderBy(p => p.LayerOrder)
            .ToList();

        var heavy = enabled.Where(IsHeavy).ToList();
        var light = enabled.Where(p => !IsHeavy(p)).ToList();

        string startingDb = EnsureCheckpoint(heavy, progress);

        // Materialize: copy the checkpoint (base + heavy) then merge light overlays.
        CopyDb(startingDb, workingDbPath);

        MergeResult result = light.Count > 0
            ? PartMerger.MergeFiles(workingDbPath, ToSourceFiles(light), progress)
            : new MergeResult(0, 0, 0, 0, 0);

        // Record disabled parts in the registry too, so the UI can list/toggle
        // them even though they were not merged.
        RegistryWriter.WriteDisabledParts(workingDbPath, manifest.Parts.Where(p => !p.Enabled));

        return result;
    }

    // ----- checkpoint -----

    private string EnsureCheckpoint(IReadOnlyList<PartManifestEntry> heavy, IProgress<string>? progress)
    {
        string key = ComputeCheckpointKey(heavy);

        if (File.Exists(_checkpointPath) && File.Exists(_checkpointKeyPath)
            && File.ReadAllText(_checkpointKeyPath) == key)
        {
            return _checkpointPath; // reuse — heavy set unchanged
        }

        progress?.Report("Rebuilding base checkpoint");
        CopyDb(_baseSnapshotPath, _checkpointPath);
        if (heavy.Count > 0)
            PartMerger.MergeFiles(_checkpointPath, ToSourceFiles(heavy), progress);

        File.WriteAllText(_checkpointKeyPath, key);
        return _checkpointPath;
    }

    private void InvalidateCheckpoint()
    {
        TryDelete(_checkpointPath);
        TryDelete(_checkpointKeyPath);
        TryDelete(_checkpointPath + "-wal");
        TryDelete(_checkpointPath + "-shm");
    }

    private string ComputeCheckpointKey(IReadOnlyList<PartManifestEntry> heavy)
    {
        var sb = new StringBuilder();
        foreach (var p in heavy)
            sb.Append(p.PartId).Append('@').Append(p.ContentHash ?? p.Version ?? "?").Append('\n');
        return sb.ToString();
    }

    private static bool IsHeavy(PartManifestEntry p) =>
        p.Category is not null && HeavyCategories.Contains(p.Category);

    private IReadOnlyList<PartSourceFile> ToSourceFiles(IEnumerable<PartManifestEntry> parts) =>
        parts.Select(p => new PartSourceFile(
            Path.Combine(_archiveDir, p.ArchiveFile), p.PartId, p.Category)).ToList();

    private PartFileInfo SafeReadInfo(string path, string partId, string? category)
    {
        try { return PartMetadataReader.Read(path, partId, category); }
        catch
        {
            return new PartFileInfo
            {
                PartId = partId,
                Filename = Path.GetFileName(path),
                Category = category,
                ContentHash = "",
            };
        }
    }

    private static void CopyDb(string source, string destination)
    {
        // Fold any pending WAL pages into the main db file so a plain file copy
        // is a complete, self-contained database. Without this, rows written
        // under WAL journaling can live only in the -wal sidecar and be lost
        // when we copy the .db file alone.
        CheckpointWal(source);

        TryDelete(destination);
        TryDelete(destination + "-wal");
        TryDelete(destination + "-shm");
        File.Copy(source, destination, overwrite: true);
    }

    private static void CheckpointWal(string dbPath)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteNonQuery();
        }
        catch { /* best-effort; copy still proceeds */ }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    private static string SafeArchiveName(string partId, int order)
    {
        var sb = new StringBuilder();
        foreach (char c in partId)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return $"{order:D4}_{sb}";
    }
}
