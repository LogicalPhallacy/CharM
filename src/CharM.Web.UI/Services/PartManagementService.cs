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
    /// Enable or disable a part and rebuild the working database, then reload it.
    /// </summary>
    public Task SetPartEnabledAsync(string partId, bool enabled, CancellationToken cancellationToken = default)
        => SetPartsEnabledAsync([partId], enabled, cancellationToken);

    /// <summary>
    /// Enable or disable many parts at once (e.g. a whole category), rebuilding
    /// the working database a single time, then reload it. Shared by the
    /// single-part toggle so both paths rebuild identically.
    /// </summary>
    public async Task SetPartsEnabledAsync(
        IReadOnlyCollection<string> partIds, bool enabled, CancellationToken cancellationToken = default)
    {
        if (partIds.Count == 0) return;

        // Release the loaded DB before rebuilding it in place (see Unload).
        _db.Unload();
        await Task.Run(() =>
        {
            lock (_sync)
            {
                var store = new RulesDbLayerStore(_workingDirectory);
                if (!store.IsInitialized)
                    throw new InvalidOperationException("This database was not built through the layered pipeline; rebuild it to manage parts.");
                store.SetEnabled(partIds, enabled);
                store.Rebuild(WorkingDbPath);
            }
        }, cancellationToken);

        ReloadDatabase();
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

        // Release the loaded DB before rebuilding it in place (see Unload).
        _db.Unload();
        await store.InstallRemotePartsAsync(source, parts, cancellationToken);
        await Task.Run(() => { lock (_sync) store.Rebuild(WorkingDbPath); }, cancellationToken);
        ReloadDatabase();
    }

    private void ReloadDatabase()
    {
        if (!_db.TryOpen(WorkingDbPath, out var error))
            throw new InvalidOperationException(error);
    }
}
