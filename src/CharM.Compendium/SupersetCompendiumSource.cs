using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CharM.Compendium;

/// <summary>
/// <see cref="ICompendiumSource"/> backed by a superset SQLite DB produced by
/// <see cref="CompendiumSuperset.WriteSqlite"/> (table <c>superset_entries</c>).
/// Lets the matcher resolve rules-DB elements against the reborn-authoritative
/// 3-way union, so generated compendium ids prefer Reborn's (most-correct) ids
/// while still covering entries only the other dumps had. Loading is lazy and
/// cached per category.
/// </summary>
public sealed class SupersetCompendiumSource : ICompendiumSource, IDisposable
{
    private readonly string _dbPath;
    private CompendiumCatalog? _catalog;
    private IReadOnlyList<string>? _categories;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _nameIndex;
    private readonly ConcurrentDictionary<string, CompendiumListing> _listings = new(StringComparer.Ordinal);

    public SupersetCompendiumSource(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        if (!File.Exists(_dbPath))
            throw new FileNotFoundException($"Superset DB not found: {_dbPath}");
    }

    public string RootDirectory => _dbPath;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }

    public IReadOnlyList<string> Categories => _categories ??= LoadCategories();

    public bool HasCategory(string category) => Categories.Contains(category);

    public CompendiumCatalog Catalog => _catalog ??= BuildCatalog();

    public CompendiumListing GetListing(string category) =>
        _listings.GetOrAdd(category, LoadListing);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GlobalNameIndex =>
        _nameIndex ??= BuildNameIndex();

    public string? GetBody(string category, string id)
    {
        var entry = HasCategory(category) ? GetListing(category).FindById(id) : null;
        if (entry is null) return null;
        return entry.Get("Txt") is { Length: > 0 } txt ? CompendiumHtml.ExtractDetail(txt) : null;
    }

    public string? GetIndexText(string category, string id)
    {
        var body = GetBody(category, id);
        return body is null ? null : CompendiumHtml.HtmlToText(body);
    }

    // --- loaders -----------------------------------------------------------

    private IReadOnlyList<string> LoadCategories()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT category FROM superset_entries ORDER BY category";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private CompendiumListing LoadListing(string category)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, fields_json FROM superset_entries WHERE category = $cat";
        cmd.Parameters.AddWithValue("$cat", category);
        using var r = cmd.ExecuteReader();

        var entries = new List<CompendiumEntry>();
        while (r.Read())
        {
            var id = r.GetString(0);
            var name = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!r.IsDBNull(2))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2));
                if (parsed is not null)
                    foreach (var kv in parsed) fields[kv.Key] = kv.Value;
            }
            fields["ID"] = id;
            fields["Name"] = name;
            entries.Add(new CompendiumEntry(id, category, fields));
        }
        return new CompendiumListing(category, ["ID", "Name"], entries);
    }

    private CompendiumCatalog BuildCatalog()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category, COUNT(*) FROM superset_entries GROUP BY category";
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
        return new CompendiumCatalog(counts);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildNameIndex()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM superset_entries";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (r.Read())
        {
            var id = r.GetString(0);
            var norm = CompendiumName.Normalize(r.IsDBNull(1) ? "" : r.GetString(1));
            if (norm.Length == 0) continue;
            if (!map.TryGetValue(norm, out var list)) map[norm] = list = [];
            list.Add(id);
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
