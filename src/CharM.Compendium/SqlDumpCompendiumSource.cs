using System.Collections.Concurrent;

namespace CharM.Compendium;

/// <summary>
/// <see cref="ICompendiumSource"/> backed by the Portable D&amp;D Compendium's
/// MySQL-dump export (a <c>sql/</c> directory of <c>ddi*.sql</c> files). Each
/// table's <c>ID</c> column is the compendium numeric id, so entry ids are
/// <c>&lt;category&gt;&lt;ID&gt;</c> (e.g. "power10"), matching the JSONP dump's id scheme.
/// Loading is lazy and cached per category.
/// </summary>
public sealed class SqlDumpCompendiumSource : ICompendiumSource
{
    private readonly string _sqlDir;
    private CompendiumCatalog? _catalog;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _nameIndex;
    private readonly ConcurrentDictionary<string, CompendiumListing> _listings = new(StringComparer.Ordinal);

    /// <param name="sqlDir">Path to the directory containing the <c>ddi*.sql</c> files.</param>
    public SqlDumpCompendiumSource(string sqlDir)
    {
        _sqlDir = sqlDir ?? throw new ArgumentNullException(nameof(sqlDir));
        if (!Directory.Exists(_sqlDir))
            throw new DirectoryNotFoundException($"SQL dump directory not found: {_sqlDir}");
    }

    public string RootDirectory => _sqlDir;

    private string? FilePathForCategory(string category)
    {
        var table = SqlDumpCategories.TableToCategory
            .FirstOrDefault(kv => kv.Value == category).Key;
        if (table is null) return null;
        var path = Path.Combine(_sqlDir, SqlDumpCategories.FileName(table));
        return File.Exists(path) ? path : null;
    }

    public bool HasCategory(string category) => FilePathForCategory(category) is not null;

    public IReadOnlyList<string> Categories
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var cat in SqlDumpCategories.TableToCategory.Values)
                if (HasCategory(cat)) set.Add(cat);
            return [.. set];
        }
    }

    public CompendiumCatalog Catalog => _catalog ??= BuildCatalog();

    public CompendiumListing GetListing(string category) =>
        _listings.GetOrAdd(category, LoadListing);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GlobalNameIndex =>
        _nameIndex ??= BuildNameIndex();

    public string? GetBody(string category, string id)
    {
        var entry = HasCategory(category) ? GetListing(category).FindById(id) : null;
        if (entry is null) return null;
        // The full HTML page is stored in the Txt column; return its detail body.
        return entry.Get("Txt") is { Length: > 0 } txt ? CompendiumHtml.ExtractDetail(txt) : null;
    }

    public string? GetIndexText(string category, string id)
    {
        var body = GetBody(category, id);
        return body is null ? null : CompendiumHtml.HtmlToText(body);
    }

    // --- loaders -----------------------------------------------------------

    private CompendiumListing LoadListing(string category)
    {
        var path = FilePathForCategory(category)
            ?? throw new InvalidOperationException($"No SQL dump for category '{category}'.");
        var table = MySqlDump.ParseFile(path);
        int idIdx = IndexOf(table.Columns, "ID");
        int nameIdx = IndexOf(table.Columns, "Name");

        var entries = new List<CompendiumEntry>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            string idNum = idIdx >= 0 && idIdx < row.Count ? row[idIdx] ?? string.Empty : string.Empty;
            if (idNum.Length == 0) continue;
            var id = category + idNum;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < table.Columns.Count && i < row.Count; i++)
                fields[table.Columns[i]] = row[i] ?? string.Empty;
            // Provide a canonical "ID"/"Name" the shared models expect.
            fields["ID"] = id;
            if (nameIdx >= 0 && nameIdx < row.Count)
                fields["Name"] = row[nameIdx] ?? string.Empty;

            entries.Add(new CompendiumEntry(id, category, fields));
        }
        return new CompendiumListing(category, table.Columns, entries);
    }

    private CompendiumCatalog BuildCatalog()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cat in Categories)
            counts[cat] = GetListing(cat).Entries.Count;
        return new CompendiumCatalog(counts);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildNameIndex()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var cat in Categories)
        {
            foreach (var e in GetListing(cat).Entries)
            {
                var norm = CompendiumName.Normalize(e.Name);
                if (norm.Length == 0) continue;
                if (!map.TryGetValue(norm, out var list)) map[norm] = list = [];
                list.Add(e.Id);
            }
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    // --- helpers -----------------------------------------------------------

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
