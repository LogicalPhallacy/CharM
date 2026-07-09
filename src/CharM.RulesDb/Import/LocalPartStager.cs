namespace CharM.RulesDb.Import;

/// <summary>
/// Stages local <c>.part</c> files for the layered store, categorizing them by
/// the CBLoader <c>.index</c> files found alongside them (recursively, e.g. in
/// an <c>indexes/</c> subfolder) rather than by their physical folder. Parts not
/// referenced by any index fall back to their content-folder name (sorted /
/// UnearthedArcana / Homebrew / 3rdParty) or null. Shared by the CLI
/// <c>parts init --parts</c> path and the web "build from sources" path so the
/// two categorize identically.
/// </summary>
public static class LocalPartStager
{
    private static readonly HashSet<string> KnownFolders =
        new(RulePartCategories.ContentFolders, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Build an index catalog from every <c>.index</c> file under
    /// <paramref name="root"/> (recursive). Empty when no index files exist.
    /// </summary>
    public static PartIndexCatalog BuildCatalog(string root) => BuildCatalogAndIndexes(root).Catalog;

    /// <summary>
    /// Parse every <c>.index</c> under <paramref name="root"/> once, returning
    /// both the categorization catalog and the parsed index documents (the
    /// latter drive part load order). Malformed indexes are skipped.
    /// </summary>
    public static (PartIndexCatalog Catalog, IReadOnlyList<PartIndexDocument> Indexes) BuildCatalogAndIndexes(string root)
    {
        var catalog = new PartIndexCatalog();
        var indexes = new List<PartIndexDocument>();
        if (!Directory.Exists(root)) return (catalog, indexes);

        foreach (var indexPath in Directory.GetFiles(root, "*.index", SearchOption.AllDirectories)
                     .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var doc = PartIndexDocument.Parse(System.Xml.Linq.XDocument.Load(indexPath), Path.GetFileName(indexPath));
                indexes.Add(doc);
                catalog.Add(doc);
            }
            catch { /* malformed index: skip, parts fall back to folder categorization */ }
        }
        return (catalog, indexes);
    }

    /// <summary>
    /// Enumerate every <c>.part</c> file under <paramref name="root"/> (recursive)
    /// as staged tuples for <see cref="RulesDbLayerStore.Initialize"/>, resolving
    /// each part's category + official flag from <paramref name="catalog"/> and
    /// ordering them by the shared index-driven load order
    /// (<see cref="PartLoadOrder"/>).
    /// </summary>
    public static IReadOnlyList<(string Path, string PartId, string? Category, bool IsOfficial)> StageParts(
        string root, PartIndexCatalog catalog, IReadOnlyList<PartIndexDocument> indexes)
    {
        var result = new List<(string Path, string PartId, string? Category, bool IsOfficial)>();
        if (!Directory.Exists(root)) return result;

        foreach (var f in Directory.GetFiles(root, "*.part", SearchOption.AllDirectories))
        {
            string filename = Path.GetFileName(f);
            string? category = catalog.CategoryFor(filename, FolderFallback(f));
            bool official = catalog.IsOfficial(filename);
            result.Add((f, filename, category, official));
        }

        return PartLoadOrder.Order(result, t => t.Path, indexes);
    }

    /// <summary>Convenience: build the catalog and stage in one call.</summary>
    public static IReadOnlyList<(string Path, string PartId, string? Category, bool IsOfficial)> Stage(string root)
    {
        var (catalog, indexes) = BuildCatalogAndIndexes(root);
        return StageParts(root, catalog, indexes);
    }

    /// <summary>The immediate parent folder name when it's a known content folder, else null.</summary>
    private static string? FolderFallback(string partPath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(partPath) ?? "");
        return KnownFolders.Contains(parent) ? parent : null;
    }
}
