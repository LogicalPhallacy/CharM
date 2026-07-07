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
    public static PartIndexCatalog BuildCatalog(string root)
    {
        var catalog = new PartIndexCatalog();
        if (!Directory.Exists(root)) return catalog;

        foreach (var indexPath in Directory.GetFiles(root, "*.index", SearchOption.AllDirectories)
                     .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(indexPath);
                catalog.Add(PartIndexDocument.Parse(doc, Path.GetFileName(indexPath)));
            }
            catch { /* malformed index: skip, parts fall back to folder categorization */ }
        }
        return catalog;
    }

    /// <summary>
    /// Enumerate every <c>.part</c> file under <paramref name="root"/> (recursive)
    /// as staged tuples for <see cref="RulesDbLayerStore.Initialize"/>, resolving
    /// each part's category + official flag from <paramref name="catalog"/>.
    /// </summary>
    public static IReadOnlyList<(string Path, string PartId, string? Category, bool IsOfficial)> StageParts(
        string root, PartIndexCatalog catalog)
    {
        var result = new List<(string, string, string?, bool)>();
        if (!Directory.Exists(root)) return result;

        foreach (var f in Directory.GetFiles(root, "*.part", SearchOption.AllDirectories)
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            string filename = Path.GetFileName(f);
            string? category = catalog.CategoryFor(filename, FolderFallback(f));
            bool official = catalog.IsOfficial(filename);
            result.Add((f, filename, category, official));
        }
        return result;
    }

    /// <summary>Convenience: build the catalog and stage in one call.</summary>
    public static IReadOnlyList<(string Path, string PartId, string? Category, bool IsOfficial)> Stage(string root)
        => StageParts(root, BuildCatalog(root));

    /// <summary>The immediate parent folder name when it's a known content folder, else null.</summary>
    private static string? FolderFallback(string partPath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(partPath) ?? "");
        return KnownFolders.Contains(parent) ? parent : null;
    }
}
