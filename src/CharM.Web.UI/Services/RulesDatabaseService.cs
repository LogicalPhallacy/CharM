using CharM.Engine.Rules;
using CharM.Engine.Selection;
using CharM.RulesDb.Import;
using CharM.RulesDb.Storage;

namespace CharM.Web.Services;

public sealed class RulesDatabaseService : IRulesDatabase
{
    /// <summary>
    /// Cap on uploaded payload size. Sized to comfortably hold the largest
    /// realistic file we accept (OCB update executable ~80 MB), with headroom
    /// for slightly newer installer revisions. 512 MB used to be the cap but
    /// only made wrong-file mistakes slow to fail.
    /// </summary>
    public const long MaxUploadBytes = 128L * 1024L * 1024L;

    private readonly object _sync = new();
    private readonly string _workingDirectory;
    private RulesDatabase? _current;
    private string? _databasePath;

    public event Action? Changed;

    public RulesDatabaseService()
        : this(RulesDatabasePathResolver.GetDefaultWorkingDirectory())
    {
    }

    public RulesDatabaseService(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        Directory.CreateDirectory(_workingDirectory);
    }

    public bool IsLoaded
    {
        get
        {
            lock (_sync)
                return _current is not null;
        }
    }

    public string? DatabasePath
    {
        get
        {
            lock (_sync)
                return _databasePath;
        }
    }

    /// <summary>
    /// Deterministic SHA-256 fingerprint of the loaded database's content
    /// (not its file bytes — see <see cref="RulesDbContentHasher"/>). Null
    /// while still computing on a background thread after the DB is loaded.
    /// Subscribers should listen to <see cref="Changed"/> to observe the
    /// transition from null to populated.
    /// </summary>
    public string? ContentHash { get; private set; }

    /// <summary>True while the content hash is still being computed in the background.</summary>
    public bool ContentHashComputing { get; private set; }

    /// <summary>File size in bytes of the currently loaded database (for display).</summary>
    public long? SizeBytes { get; private set; }

    /// <summary>UTC timestamp when the current database was loaded by this service.</summary>
    public DateTime? LoadedAt { get; private set; }

    public string? StatusMessage { get; private set; }
    public bool StatusIsError { get; private set; }

    /// <summary>
    /// True when the user explicitly requested to re-open the setup wizard
    /// from the status badge while a DB is already loaded. Routes.razor
    /// renders the wizard whenever this is true (in addition to the normal
    /// "no DB loaded" case). Cleared on successful load or explicit cancel.
    /// </summary>
    public bool IsManageMode { get; private set; }

    public void RequestManageMode()
    {
        if (IsManageMode) return;
        IsManageMode = true;
        Changed?.Invoke();
    }

    public void ExitManageMode()
    {
        if (!IsManageMode) return;
        IsManageMode = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Current import/merge progress. Null when no long-running operation is in
    /// flight. UI consumers re-render on <see cref="Changed"/>.
    /// </summary>
    public DbBuildProgress? CurrentProgress { get; private set; }

    public bool TryOpenFirstAvailable(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryOpen(candidate, out _))
                return true;
        }

        return false;
    }

    public bool TryOpen(string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose a rules database file.";
            SetStatus(error, isError: true);
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            error = $"Rules database not found: {fullPath}";
            SetStatus(error, isError: true);
            return false;
        }

        try
        {
            // Best-effort: bring legacy databases (built before the metadata
            // tables existed) forward in place. Requires write access; if the
            // file is read-only or locked we proceed without metadata — the DB
            // still works for read-only querying.
            try
            {
                RulesDbUpconverter.Upconvert(fullPath);
            }
            catch
            {
                // non-fatal: metadata features degrade, querying still works
            }

            ReplaceDatabase(new RulesDatabase(fullPath), fullPath);
            SetStatus($"Loaded rules database: {Path.GetFileName(fullPath)}", isError: false);
            // A successful load exits manage mode automatically — wizard goes away.
            if (IsManageMode)
            {
                IsManageMode = false;
                Changed?.Invoke();
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not open rules database: {ex.Message}";
            SetStatus(error, isError: true);
            return false;
        }
    }

    /// <summary>
    /// Opens a shared read stream over the currently loaded database file.
    /// SQLite's WAL journal mode (which the importer enables) supports
    /// concurrent readers, so this is safe to use while the engine connection
    /// also has the file open. The caller is responsible for disposing the
    /// stream.
    /// </summary>
    /// <exception cref="InvalidOperationException">No database is loaded.</exception>
    public Stream OpenBackupReadStream()
    {
        string? path;
        lock (_sync)
        {
            if (_current is null || _databasePath is null)
                throw new InvalidOperationException("Load a rules database before requesting a backup.");
            path = _databasePath;
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    public async Task LoadUploadedDatabaseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(_workingDirectory, "rules.db");
        await CopyToFileAsync(stream, destination, cancellationToken);

        if (!TryOpen(destination, out var error))
            throw new InvalidOperationException(error);
    }

    public async Task BuildFromUploadedSourcesAsync(
        Stream rulesXmlStream,
        string rulesXmlFileName,
        IEnumerable<UploadedRulesSourceFile> partFiles,
        string? partIndexUrl = null,
        PartSourceConfig? partSource = null,
        CancellationToken cancellationToken = default)
    {
        var sourceDirectory = Path.Combine(_workingDirectory, "sources");
        Directory.CreateDirectory(sourceDirectory);

        var xmlPath = Path.Combine(sourceDirectory, MakeSafeFileName(rulesXmlFileName, "rules.xml"));
        await CopyToFileAsync(rulesXmlStream, xmlPath, cancellationToken);

        await BuildFromXmlPathAsync(xmlPath, partFiles, partIndexUrl, partSource, cancellationToken);
    }

    /// <summary>
    /// Builds a rules database from an uploaded OCB update executable stream.
    /// Persists the executable to a working-directory temp path, extracts the
    /// merged rules XML, runs the import pipeline, then deletes both temp
    /// files. The stream is consumed but not disposed (caller owns it).
    /// </summary>
    public async Task BuildFromUpdateExecutableAsync(
        Stream updateExecutableStream,
        string updateExecutableFileName,
        IEnumerable<UploadedRulesSourceFile> partFiles,
        string? partIndexUrl = null,
        PartSourceConfig? partSource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateExecutableStream);
        if (string.IsNullOrWhiteSpace(updateExecutableFileName))
            throw new ArgumentException("Update executable file name is required.", nameof(updateExecutableFileName));

        var sourceDirectory = Path.Combine(_workingDirectory, "sources");
        Directory.CreateDirectory(sourceDirectory);

        var exePath = Path.Combine(sourceDirectory, MakeSafeFileName(updateExecutableFileName, "CharacterBuilder_Update.exe"));
        await CopyToFileAsync(updateExecutableStream, exePath, cancellationToken);

        try
        {
            SetProgress(new DbBuildProgress("Extracting rules XML from update executable", Detail: updateExecutableFileName));
            var extractedXmlPath = await Task.Run(() => XMLExtractor.ExtractXML(exePath), cancellationToken);
            if (string.IsNullOrWhiteSpace(extractedXmlPath) || !File.Exists(extractedXmlPath))
                throw new InvalidOperationException("Could not extract rules XML from the update executable.");

            try
            {
                await BuildFromXmlPathAsync(extractedXmlPath, partFiles, partIndexUrl, partSource, cancellationToken);
            }
            finally
            {
                TryDelete(extractedXmlPath);
            }
        }
        finally
        {
            TryDelete(exePath);
        }
    }

    private async Task BuildFromXmlPathAsync(
        string xmlPath,
        IEnumerable<UploadedRulesSourceFile> partFiles,
        string? partIndexUrl,
        PartSourceConfig? partSource,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.Combine(_workingDirectory, "sources");
        var partsDirectory = Path.Combine(sourceDirectory, "parts");
        var dbPath = Path.Combine(_workingDirectory, "rules.db");

        // Release any currently-loaded database first: we are about to overwrite
        // the working rules.db on disk, so it must not be held open (a lingering
        // read connection + its stale -wal corrupts the rebuilt file).
        Unload();

        Directory.CreateDirectory(sourceDirectory);
        if (Directory.Exists(partsDirectory))
            Directory.Delete(partsDirectory, recursive: true);
        Directory.CreateDirectory(partsDirectory);

        foreach (var part in partFiles)
        {
            await using var partStream = part.Content;
            var partPath = Path.Combine(partsDirectory, MakeSafeFileName(part.FileName, "rules.part"));
            await CopyToFileAsync(partStream, partPath, cancellationToken);
        }

        try
        {
            await Task.Run(async () =>
            {
                // Pull indexed parts (if any) into the same directory so they
                // are archived and toggleable alongside uploaded parts.
                if (!string.IsNullOrWhiteSpace(partIndexUrl))
                {
                    SetProgress(new DbBuildProgress("Downloading indexed part files"));
                    PartMerger.DownloadIndexParts(partIndexUrl.Trim(), partsDirectory,
                        new Progress<string>(message =>
                            SetProgress(new DbBuildProgress("Downloading indexed part files", Detail: message.Trim()))));
                }

                // Build through the layered store: imports the base snapshot,
                // archives every part, writes the manifest, and materializes the
                // working rules.db. This enables later enable/disable + rebuild.
                var store = new RulesDbLayerStore(_workingDirectory);
                var stagedParts = Directory.GetFiles(partsDirectory, "*.part")
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Select(f => (Path: f, PartId: Path.GetFileName(f), Category: (string?)null))
                    .ToList();

                SetProgress(new DbBuildProgress("Importing rules elements"));
                store.Initialize(xmlPath, stagedParts, dbPath,
                    new Progress<string>(message =>
                        SetProgress(new DbBuildProgress("Building rules database", Detail: message.Trim()))));

                // Pull parts directly from a configured remote source (e.g. a
                // GitHub repo) and layer them on top of the freshly built base.
                if (partSource is { IsComplete: true })
                {
                    var source = PartSourceFactory.Create(partSource);
                    SetProgress(new DbBuildProgress("Listing parts", Detail: source.DisplayName));
                    var remoteParts = await source.ListAsync(cancellationToken);
                    if (remoteParts.Count > 0)
                    {
                        await store.InstallRemotePartsAsync(source, remoteParts, cancellationToken,
                            new Progress<string>(message =>
                                SetProgress(new DbBuildProgress("Downloading parts", Detail: message.Trim()))));
                        SetProgress(new DbBuildProgress("Building rules database", Detail: "merging remote parts"));
                        store.Rebuild(dbPath,
                            new Progress<string>(message =>
                                SetProgress(new DbBuildProgress("Building rules database", Detail: message.Trim()))));
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            ClearProgress();
        }

        if (!TryOpen(dbPath, out var error))
            throw new InvalidOperationException(error);
    }

    public RulesElement? FindByInternalId(string internalId)
        => Current.FindByInternalId(internalId);

    public RulesElement? FindByNameAndType(string name, string type)
        => Current.FindByNameAndType(name, type);

    public IEnumerable<RulesElement> FindByType(string type)
        => Current.FindByType(type);

    public IEnumerable<RulesElement> FindByType(string type, bool includeRules)
        => Current.FindByType(type, includeRules);

    public IEnumerable<RulesElement> FindByCategory(string category)
        => Current.FindByCategory(category);

    public IEnumerable<RulesElement> FindByTypeAndCategory(string type, params string[] categories)
        => Current.FindByTypeAndCategory(type, categories);

    public IEnumerable<RulesElement> FindBySource(string source)
        => Current.FindBySource(source);

    public IEnumerable<RulesElement> FindByTypeAndSource(string type, string source)
        => Current.FindByTypeAndSource(type, source);

    public IEnumerable<RulesElement> FindByTypeAndSource(string type, string source, bool includeRules)
        => Current.FindByTypeAndSource(type, source, includeRules);

    public IEnumerable<string> GetDistinctSources()
        => Current.GetDistinctSources();

    public IReadOnlyList<PartLayer> GetPartLayers()
        => Current.GetPartLayers();

    public string? GetElementProvenanceCategory(string internalId)
        => Current.GetElementProvenanceCategory(internalId);

    /// <summary>
    /// Build a provenance-based legality classifier for the loaded DB: elements
    /// introduced/modified by a Homebrew part are HouseRule, by other overlay
    /// parts (UnearthedArcana / sorted / 3rdParty) are PartFile, otherwise
    /// RulesLegal. Returns null when no DB is loaded.
    /// </summary>
    public Func<RulesElement, LegalitySource?>? CreateProvenanceClassifier()
    {
        if (!IsLoaded) return null;
        return element =>
        {
            var category = GetElementProvenanceCategory(element.InternalId);
            if (category is null) return null; // base-only → fall back to heuristic
            return string.Equals(category, RulePartCategories.Homebrew, StringComparison.OrdinalIgnoreCase)
                ? LegalitySource.HouseRule
                : LegalitySource.PartFile;
        };
    }

    public int Count => Current.Count;

    /// <summary>
    /// Fully release the currently-loaded database: dispose the read connection
    /// (which, with pooling disabled, releases the OS file handle plus the
    /// WAL/SHM sidecars) and reset all derived metadata. After this returns the
    /// working <c>rules.db</c> file is no longer held open, so it is safe to
    /// overwrite/rebuild. Safe to call when nothing is loaded.
    ///
    /// This is the single shared "close cleanly" path — reused by
    /// <see cref="Dispose"/> and by every flow that rebuilds the working
    /// database in place (initial build, part toggle, remote update) so none of
    /// them overwrite a file we are still holding open.
    /// </summary>
    public void Unload()
    {
        RulesDatabase? old;
        bool wasLoaded;
        lock (_sync)
        {
            old = _current;
            wasLoaded = old is not null;
            _current = null;
            _databasePath = null;
            ContentHash = null;
            ContentHashComputing = false;
            SizeBytes = null;
            LoadedAt = null;
        }

        old?.Dispose();
        // Belt-and-suspenders: drop any pooled handle that some other code path
        // may have opened against the file, so the rebuild's delete/overwrite of
        // rules.db (and its -wal/-shm) can't race a lingering connection.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (wasLoaded)
            Changed?.Invoke();
    }

    public void Dispose() => Unload();

    private IRulesDatabase Current
    {
        get
        {
            lock (_sync)
                return _current ?? throw new InvalidOperationException("Load a rules database before using the character builder.");
        }
    }

    private static async Task CopyToFileAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var destinationStream = File.Create(destination);
        await source.CopyToAsync(destinationStream, cancellationToken);
    }

    private void ReplaceDatabase(RulesDatabase database, string databasePath)
    {
        RulesDatabase? old;
        long? size = null;
        try
        {
            size = new FileInfo(databasePath).Length;
        }
        catch
        {
            // best-effort metadata; if the file vanished between open and stat we just skip
        }

        lock (_sync)
        {
            old = _current;
            _current = database;
            _databasePath = databasePath;
            ContentHash = null;
            ContentHashComputing = true;
            SizeBytes = size;
            LoadedAt = DateTime.UtcNow;
        }

        old?.Dispose();
        Changed?.Invoke();

        // Compute the content hash off the UI thread — a ~50 MB DB takes a
        // couple of seconds to fingerprint. Fire-and-forget; the UI watches
        // ContentHashComputing + Changed to know when it lands.
        _ = Task.Run(() =>
        {
            try
            {
                var hash = RulesDbContentHasher.ComputeContentHash(databasePath);
                lock (_sync)
                {
                    if (!ReferenceEquals(_current, database))
                        return; // a newer database was loaded while we were hashing
                    ContentHash = hash;
                    ContentHashComputing = false;
                }
                Changed?.Invoke();
            }
            catch
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_current, database))
                        return;
                    ContentHashComputing = false;
                }
                Changed?.Invoke();
            }
        });
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
        Changed?.Invoke();
    }

    private void SetProgress(DbBuildProgress progress)
    {
        CurrentProgress = progress;
        // Mirror to StatusMessage for any UI that still listens to it
        StatusMessage = progress.Detail is null
            ? progress.Phase
            : $"{progress.Phase}: {progress.Detail}";
        StatusIsError = false;
        Changed?.Invoke();
    }

    private void ClearProgress()
    {
        CurrentProgress = null;
        Changed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string MakeSafeFileName(string fileName, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? fallback : Path.GetFileName(fileName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }
}

public sealed record UploadedRulesSourceFile(string FileName, Stream Content);
