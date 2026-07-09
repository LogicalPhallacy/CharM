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

    /// <summary>
    /// True when this part belongs to an "official" index (WotC / Unearthed
    /// Arcana — <c>&lt;Description category="Official"&gt;</c>). Official parts
    /// are treated as heavy layers (folded into the base checkpoint) and are
    /// enabled by default.
    /// </summary>
    public bool IsOfficial { get; set; }

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
        IReadOnlyList<(string Path, string PartId, string? Category, bool IsOfficial)> partFiles,
        string workingDbPath,
        IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(_archiveDir);

        progress?.Report("Importing base rules");
        RulesDbBuilder.Import(xmlPath, _baseSnapshotPath);

        var manifest = new PartManifest { BaseXmlFilename = Path.GetFileName(xmlPath) };
        int order = 1;
        foreach (var (path, partId, category, isOfficial) in partFiles)
        {
            var info = SafeReadInfo(path, partId, category, isOfficial);
            if (info.IsObsolete) continue;

            string archiveFile = SafeArchiveName(partId, order);
            File.Copy(path, Path.Combine(_archiveDir, archiveFile), overwrite: true);

            manifest.Parts.Add(new PartManifestEntry
            {
                PartId = partId,
                Filename = info.Filename,
                Category = category,
                IsOfficial = isOfficial,
                Version = info.Version,
                ContentHash = info.ContentHash,
                SourceUrl = info.PartAddress,
                // Official content packs (WotC / Unearthed Arcana) are on by
                // default; other content packs are opt-in.
                Enabled = isOfficial,
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
    /// Apply a map of part-id → desired enabled state in one manifest write
    /// (does not rebuild). Unknown ids are ignored. Use this for "apply all
    /// pending changes" so a mixed enable/disable set rebuilds the working DB
    /// only once afterwards.
    /// </summary>
    public PartManifest SetEnabled(IReadOnlyDictionary<string, bool> states)
    {
        var manifest = LoadManifest();
        foreach (var entry in manifest.Parts)
        {
            // Manifest part-ids are case-insensitive; probe with the entry's id.
            foreach (var kvp in states)
            {
                if (string.Equals(kvp.Key, entry.PartId, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Enabled = kvp.Value;
                    break;
                }
            }
        }
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
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(_archiveDir);
        var manifest = LoadManifest();
        int nextOrder = manifest.Parts.Count > 0 ? manifest.Parts.Max(p => p.LayerOrder) + 1 : 1;
        var changed = new List<string>();

        var partList = parts as IReadOnlyList<RemotePartInfo> ?? parts.ToList();
        int total = partList.Count;
        int index = 0;
        foreach (var remote in partList)
        {
            progress?.Report($"Downloading {remote.PartId} ({++index}/{total})");
            byte[] bytes = await source.DownloadAsync(remote, cancellationToken);
            var info = PartMetadataReader.Read(bytes, remote.Filename, remote.PartId, remote.Category, remote.IsOfficial);
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
                    IsOfficial = remote.IsOfficial,
                    Version = info.Version ?? remote.Version,
                    ContentHash = info.ContentHash,
                    SourceHash = remote.ContentHash,
                    SourceUrl = remote.DownloadUrl,
                    // Official packs on by default; others opt-in.
                    Enabled = remote.IsOfficial,
                    LayerOrder = nextOrder++,
                    ArchiveFile = archiveFile,
                });
            }
            else
            {
                existing.Filename = info.Filename;
                existing.Category = remote.Category ?? existing.Category;
                existing.IsOfficial = remote.IsOfficial;
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

    private static bool IsHeavy(PartManifestEntry p) => p.IsOfficial;

    private IReadOnlyList<PartSourceFile> ToSourceFiles(IEnumerable<PartManifestEntry> parts) =>
        parts.Select(p => new PartSourceFile(
            Path.Combine(_archiveDir, p.ArchiveFile), p.PartId, p.Category, p.IsOfficial)).ToList();

    private PartFileInfo SafeReadInfo(string path, string partId, string? category, bool isOfficial = false)
    {
        try { return PartMetadataReader.Read(path, partId, category, isOfficial); }
        catch
        {
            return new PartFileInfo
            {
                PartId = partId,
                Filename = Path.GetFileName(path),
                Category = category,
                IsOfficial = isOfficial,
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
