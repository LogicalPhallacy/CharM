using CharM.RulesDb.Import;

namespace CharM.Web.Services;

/// <summary>
/// Web-layer orchestration over <see cref="RulesDbLayerStore"/>: lists part
/// layers, toggles them on/off and rebuilds the working database (reloading it
/// through <see cref="RulesDatabaseService"/>), and checks/applies updates from
/// a configured remote source. All long-running work runs off the UI thread.
/// </summary>
public sealed class PartManagementService
{
    private readonly RulesDatabaseService _db;
    private readonly string _workingDirectory;
    private readonly object _sync = new();

    public PartManagementService(RulesDatabaseService db)
        : this(db, RulesDatabasePathResolver.GetDefaultWorkingDirectory())
    {
    }

    public PartManagementService(RulesDatabaseService db, string workingDirectory)
    {
        _db = db;
        _workingDirectory = workingDirectory;
    }

    private string WorkingDbPath => Path.Combine(_workingDirectory, "rules.db");

    /// <summary>True when a layered store exists (DB was built through the layered pipeline).</summary>
    public bool IsLayered => new RulesDbLayerStore(_workingDirectory).IsInitialized;

    /// <summary>The part layers in the manifest, ordered, or empty when not layered.</summary>
    public IReadOnlyList<PartManifestEntry> GetLayers()
    {
        var store = new RulesDbLayerStore(_workingDirectory);
        return store.IsInitialized
            ? store.LoadManifest().Parts.OrderBy(p => p.LayerOrder).ToList()
            : [];
    }

    /// <summary>
    /// Enable or disable a single part and rebuild the working database.
    /// </summary>
    public Task SetPartEnabledAsync(string partId, bool enabled, CancellationToken cancellationToken = default)
        => ApplyPartStatesAsync(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [partId] = enabled },
            cancellationToken);

    /// <summary>
    /// Enable or disable many parts to the same state and rebuild once.
    /// </summary>
    public Task SetPartsEnabledAsync(
        IReadOnlyCollection<string> partIds, bool enabled, CancellationToken cancellationToken = default)
        => ApplyPartStatesAsync(
            partIds.ToDictionary(id => id, _ => enabled, StringComparer.OrdinalIgnoreCase),
            cancellationToken);

    /// <summary>
    /// Apply a batch of part-id → desired enabled states (a mix of enables and
    /// disables) and rebuild the working database a single time, then reopen it.
    /// The single shared apply path for the UI's "Apply changes" button and the
    /// single/category convenience helpers.
    /// </summary>
    public async Task ApplyPartStatesAsync(
        IReadOnlyDictionary<string, bool> states, CancellationToken cancellationToken = default)
    {
        if (states.Count == 0) return;

        var store = new RulesDbLayerStore(_workingDirectory);
        if (!store.IsInitialized)
            throw new InvalidOperationException("This database was not built through the layered pipeline; rebuild it to manage parts.");

        // Manifest-only change (does not touch the working rules.db).
        store.SetEnabled(states);

        // Re-materialize rules.db without taking it offline: build into a temp
        // file (DB stays loaded + serving reads), then swap. Progress flows to
        // RulesDatabaseService.CurrentProgress.
        await _db.RebuildInPlaceAsync(
            WorkingDbPath,
            (tempPath, progress) => Task.Run(() => { lock (_sync) store.Rebuild(tempPath, progress); }, cancellationToken));
    }

    /// <summary>Check the configured remote source for new/updated parts.</summary>
    public async Task<PartUpdateReport> CheckForUpdatesAsync(
        PartSourceConfig config, CancellationToken cancellationToken = default)
    {
        var store = new RulesDbLayerStore(_workingDirectory);
        if (!store.IsInitialized)
            throw new InvalidOperationException("No layered store to update.");

        var source = PartSourceFactory.Create(config);
        return await PartUpdateChecker.CheckAsync(source, store.LoadManifest(), cancellationToken);
    }

    /// <summary>
    /// Download and install the given remote parts, rebuild, and reload the DB.
    /// </summary>
    public async Task ApplyUpdatesAsync(
        PartSourceConfig config, IReadOnlyList<RemotePartInfo> parts, CancellationToken cancellationToken = default)
    {
        if (parts.Count == 0) return;

        var source = PartSourceFactory.Create(config);
        var store = new RulesDbLayerStore(_workingDirectory);
        if (!store.IsInitialized)
            throw new InvalidOperationException("No layered store to update.");

        // Download + archive the parts first (writes to the archive/manifest, not
        // the working rules.db), so the DB stays open during the network I/O.
        await store.InstallRemotePartsAsync(source, parts, cancellationToken);

        // Then re-materialize rules.db without taking it offline (build to temp,
        // swap). DB stays "loaded" throughout; progress flows to CurrentProgress.
        await _db.RebuildInPlaceAsync(
            WorkingDbPath,
            (tempPath, progress) => Task.Run(() => { lock (_sync) store.Rebuild(tempPath, progress); }, cancellationToken));
    }
}
