namespace CharM.Compendium;

/// <summary>Category → entry-count map from <c>catalog.js</c>.</summary>
public sealed class CompendiumCatalog(IReadOnlyDictionary<string, int> counts)
{
    public IReadOnlyDictionary<string, int> Counts { get; } = counts;

    public IEnumerable<string> Categories => Counts.Keys;

    public int CountFor(string category) =>
        Counts.TryGetValue(category, out var n) ? n : 0;
}

/// <summary>
/// One row from a category's <c>_listing.js</c>, keyed by the listing's column
/// schema. The first column (<c>ID</c>) is the compendium id, e.g. "power6872".
/// </summary>
public sealed class CompendiumEntry(string id, string category, IReadOnlyDictionary<string, string> fields)
{
    public string Id { get; } = id;
    public string Category { get; } = category;

    /// <summary>Column name → display string. Range/array cells collapse to their display text.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; } = fields;

    public string Name => Get("Name") ?? string.Empty;

    public string? Get(string column) =>
        Fields.TryGetValue(column, out var v) ? v : null;
}

/// <summary>Parsed <c>_listing.js</c> for a single category.</summary>
public sealed class CompendiumListing
{
    public CompendiumListing(string category, IReadOnlyList<string> columns, IReadOnlyList<CompendiumEntry> entries)
    {
        Category = category;
        Columns = columns;
        Entries = entries;

        var byId = new Dictionary<string, CompendiumEntry>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<CompendiumEntry>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            byId[e.Id] = e;
            var norm = CompendiumName.Normalize(e.Name);
            if (norm.Length == 0) continue;
            if (!byName.TryGetValue(norm, out var list))
                byName[norm] = list = [];
            list.Add(e);
        }
        _byId = byId;
        _byNormalizedName = byName;
    }

    public string Category { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<CompendiumEntry> Entries { get; }

    private readonly Dictionary<string, CompendiumEntry> _byId;
    private readonly Dictionary<string, List<CompendiumEntry>> _byNormalizedName;

    public CompendiumEntry? FindById(string id) =>
        _byId.TryGetValue(id, out var e) ? e : null;

    public bool ContainsId(string id) => _byId.ContainsKey(id);

    /// <summary>All entries whose normalized name matches <paramref name="name"/>.</summary>
    public IReadOnlyList<CompendiumEntry> FindByNormalizedName(string normalizedName) =>
        _byNormalizedName.TryGetValue(normalizedName, out var list) ? list : [];
}
