using System.Collections.Concurrent;
using System.Text.Json;

namespace CharM.Compendium;

/// <summary>
/// Lazy, cached reader over an offline compendium data directory (the
/// <c>4e_database_files</c> folder). Nothing is loaded until requested; each
/// category's listing, index text and body files are parsed on first access
/// and cached. This type is the single source of compendium data shared by the
/// CLI audit tooling and (later) the Web UI lookup panel.
/// </summary>
public sealed class CompendiumReader : ICompendiumSource
{
    private readonly string _root;
    private CompendiumCatalog? _catalog;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _nameIndex;
    private readonly ConcurrentDictionary<string, CompendiumListing> _listings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _dataFiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _indexFiles = new(StringComparer.Ordinal);

    /// <param name="databaseFilesDir">Path to the <c>4e_database_files</c> directory.</param>
    public CompendiumReader(string databaseFilesDir)
    {
        _root = databaseFilesDir ?? throw new ArgumentNullException(nameof(databaseFilesDir));
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"Compendium directory not found: {_root}");
    }

    public string RootDirectory => _root;

    public bool HasCategory(string category) =>
        Directory.Exists(Path.Combine(_root, category));

    /// <summary>Parsed <c>catalog.js</c> (category → count).</summary>
    public CompendiumCatalog Catalog => _catalog ??= LoadCatalog();

    /// <summary>Categories that exist on disk (intersect of catalog + folders).</summary>
    public IReadOnlyList<string> Categories
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var c in Catalog.Categories)
                if (HasCategory(c)) set.Add(c);
            return [.. set];
        }
    }

    /// <summary>Parsed <c>&lt;category&gt;/_listing.js</c>, cached.</summary>
    public CompendiumListing GetListing(string category) =>
        _listings.GetOrAdd(category, LoadListing);

    /// <summary>Global <c>index.js</c> name → id(s) map, normalized keys.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GlobalNameIndex =>
        _nameIndex ??= LoadNameIndex();

    /// <summary>
    /// The HTML body for an entry, from <c>&lt;category&gt;/data{N}.js</c> where
    /// <c>N = numericSuffix % 20</c>. Returns null if not present.
    /// </summary>
    public string? GetBody(string category, string id)
    {
        int suffix = CompendiumIdMapper.NumericSuffix(id);
        if (suffix < 0) return null;
        var key = category + "/" + (suffix % 20);
        var map = _dataFiles.GetOrAdd(key, _ => LoadDataFile(category, suffix % 20));
        return map.TryGetValue(id, out var html) ? html : null;
    }

    /// <summary>
    /// The text-only full-text index string for an entry, from
    /// <c>&lt;category&gt;/_index.js</c> (authoritative plain text with no HTML).
    /// Ideal as compact, self-contained context for parity tooling.
    /// </summary>
    public string? GetIndexText(string category, string id)
    {
        var map = _indexFiles.GetOrAdd(category, LoadIndexFile);
        return map.TryGetValue(id, out var text) ? text : null;
    }

    // --- loaders -----------------------------------------------------------

    private CompendiumCatalog LoadCatalog()
    {
        var path = Path.Combine(_root, "catalog.js");
        using var doc = Jsonp.ParseArguments(File.ReadAllText(path));
        var obj = doc.RootElement[1]; // (timestamp, {counts})
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
            counts[prop.Name] = prop.Value.GetInt32();
        return new CompendiumCatalog(counts);
    }

    private CompendiumListing LoadListing(string category)
    {
        var path = Path.Combine(_root, category, "_listing.js");
        using var doc = Jsonp.ParseArguments(File.ReadAllText(path));
        var root = doc.RootElement; // (timestamp, "category", [columns], [rows])
        var columns = root[2].EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToArray();
        var entries = new List<CompendiumEntry>();
        foreach (var row in root[3].EnumerateArray())
        {
            var cells = row.EnumerateArray().ToArray();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < columns.Length && i < cells.Length; i++)
                fields[columns[i]] = CellDisplay(cells[i]);
            var id = fields.TryGetValue(columns[0], out var idv) ? idv : string.Empty;
            entries.Add(new CompendiumEntry(id, category, fields));
        }
        return new CompendiumListing(category, columns, entries);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> LoadNameIndex()
    {
        var path = Path.Combine(_root, "index.js");
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        using var doc = Jsonp.ParseArguments(File.ReadAllText(path));
        var obj = doc.RootElement[1]; // (timestamp, {name: id | [ids]})
        foreach (var prop in obj.EnumerateObject())
        {
            var key = CompendiumName.Normalize(prop.Name);
            if (key.Length == 0) continue;
            var ids = prop.Value.ValueKind == JsonValueKind.Array
                ? prop.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray()
                : [prop.Value.GetString() ?? string.Empty];
            if (ids.Length == 0) continue;
            if (map.TryGetValue(key, out var existing))
                map[key] = [.. existing, .. ids];
            else
                map[key] = ids;
        }
        return map;
    }

    private IReadOnlyDictionary<string, string> LoadDataFile(string category, int fileIndex)
    {
        var path = Path.Combine(_root, category, $"data{fileIndex}.js");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        using var doc = Jsonp.ParseArguments(File.ReadAllText(path));
        var obj = doc.RootElement[2]; // (timestamp, "category", {id: html})
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = Jsonp.Inflate(prop.Value.GetString() ?? string.Empty);
        return map;
    }

    private IReadOnlyDictionary<string, string> LoadIndexFile(string category)
    {
        var path = Path.Combine(_root, category, "_index.js");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        using var doc = Jsonp.ParseArguments(File.ReadAllText(path));
        var obj = doc.RootElement[2]; // (timestamp, "category", {id: text})
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = Jsonp.Inflate(prop.Value.GetString() ?? string.Empty);
        return map;
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// A listing cell is either a plain string or a range/multi-value array
    /// <c>["display", min, max, ...]</c>. We keep the display text.
    /// </summary>
    private static string CellDisplay(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString() ?? string.Empty,
        JsonValueKind.Number => cell.GetRawText(),
        JsonValueKind.Array => cell.GetArrayLength() > 0 ? CellDisplay(cell[0]) : string.Empty,
        _ => string.Empty,
    };

    /// <summary>Trailing numeric suffix of a compendium id ("power6872" → 6872), or -1.</summary>
    public static int NumericSuffix(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        int i = id.Length;
        while (i > 0 && char.IsDigit(id[i - 1])) i--;
        return i < id.Length && int.TryParse(id.AsSpan(i), out var n) ? n : -1;
    }
}
