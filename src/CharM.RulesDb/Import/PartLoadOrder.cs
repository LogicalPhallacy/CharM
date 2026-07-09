namespace CharM.RulesDb.Import;

/// <summary>
/// Shared ordering for the layered part merge, so every part source (local
/// folder, GitHub, CBLoader host, web page) agrees on load order. Index files
/// (content packs) load <b>WotC first, Unearthed Arcana second, then every
/// other index alphabetically</b> by index name; within each index its parts
/// load <b>alphabetically by filename</b>. Parts referenced by no index load
/// last, alphabetically.
///
/// The merge order becomes the layer order, so this fixes which content pack
/// overrides which (later layers patch earlier ones).
/// </summary>
public static class PartLoadOrder
{
    /// <summary>Rank an index by its category: WotC = 0, Unearthed Arcana = 1, everything else = 2.</summary>
    public static int IndexRank(string? indexCategory)
    {
        if (string.Equals(indexCategory, RulePartCategories.Wotc, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(indexCategory, RulePartCategories.UnearthedArcana, StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    /// <summary>Order parsed indexes: WotC, Unearthed Arcana, then alphabetical by index name.</summary>
    public static IEnumerable<PartIndexDocument> OrderIndexes(IEnumerable<PartIndexDocument> documents) =>
        documents
            .OrderBy(d => IndexRank(d.Category))
            .ThenBy(d => d.IndexName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The load rank (0-based) of each part filename referenced by the indexes:
    /// indexes in WotC/UA/alphabetical order, parts alphabetical within each
    /// index, first index wins on duplicate filenames.
    /// </summary>
    public static IReadOnlyDictionary<string, int> PartRanks(IEnumerable<PartIndexDocument> documents)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int next = 0;
        foreach (var doc in OrderIndexes(documents))
            foreach (var (filename, _) in doc.Parts.OrderBy(p => p.Filename, StringComparer.OrdinalIgnoreCase))
            {
                var key = filename.Trim();
                if (key.Length > 0 && !ranks.ContainsKey(key))
                    ranks[key] = next++;
            }
        return ranks;
    }

    /// <summary>
    /// Order arbitrary part items by the index-driven load order, appending
    /// items referenced by no index after the indexed ones, alphabetically by
    /// filename. Stable and deterministic.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(
        IEnumerable<T> parts, Func<T, string> filenameOf, IEnumerable<PartIndexDocument> documents)
    {
        var ranks = PartRanks(documents);
        return parts
            .Select(p => (Part: p, Name: Path.GetFileName(filenameOf(p) ?? string.Empty)))
            .OrderBy(x => ranks.TryGetValue(x.Name, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Part)
            .ToList();
    }
}
