using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace CharM.Compendium;

/// <summary>
/// <see cref="ICompendiumSource"/> backed by the "Compendium Reborn" data
/// directory: one SQLite database per category (<c>power.db</c>,
/// <c>glossary.db</c>, <c>item.db</c>, …), each with a single table whose columns
/// are <c>ID, Name, …, Txt</c> (the same shape as the MySQL-dump export). The
/// <c>ID</c> column is the compendium numeric id, so entry ids are
/// <c>&lt;category&gt;&lt;ID&gt;</c> (e.g. "glossary681"). Loading is lazy and cached per
/// category.
/// </summary>
public sealed class SqliteCompendiumSource : ICompendiumSource
{
    private readonly string _dataDir;
    private CompendiumCatalog? _catalog;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _nameIndex;
    private readonly ConcurrentDictionary<string, CompendiumListing> _listings = new(StringComparer.Ordinal);

    /// <summary>Files that are not category data (ignored when enumerating categories).</summary>
    private static readonly HashSet<string> _nonCategoryFiles =
        new(StringComparer.OrdinalIgnoreCase) { "bookmarks" };

    public SqliteCompendiumSource(string dataDir)
    {
        _dataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        if (!Directory.Exists(_dataDir))
            throw new DirectoryNotFoundException($"Compendium data directory not found: {_dataDir}");
    }

    public string RootDirectory => _dataDir;

    private string? DbPathForCategory(string category)
    {
        var path = Path.Combine(_dataDir, category + ".db");
        return File.Exists(path) ? path : null;
    }

    public bool HasCategory(string category) =>
        !_nonCategoryFiles.Contains(category) && DbPathForCategory(category) is not null;

    public IReadOnlyList<string> Categories
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_dataDir, "*.db"))
            {
                var cat = Path.GetFileNameWithoutExtension(file);
                if (!_nonCategoryFiles.Contains(cat)) set.Add(cat);
            }
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
        var path = DbPathForCategory(category)
            ?? throw new InvalidOperationException($"No SQLite db for category '{category}'.");

        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();

        var table = SingleUserTable(conn);
        var columns = TableColumns(conn, table);

        var entries = new List<CompendiumEntry>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\"";
        using var reader = cmd.ExecuteReader();
        int idOrd = OrdinalOf(reader, "ID");
        int nameOrd = OrdinalOf(reader, "Name");
        while (reader.Read())
        {
            string idNum = idOrd >= 0 ? reader.GetValue(idOrd)?.ToString() ?? "" : "";
            if (idNum.Length == 0) continue;
            var id = category + idNum;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < reader.FieldCount; i++)
                fields[reader.GetName(i)] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i)?.ToString() ?? string.Empty;
            fields["ID"] = id;
            if (nameOrd >= 0)
                fields["Name"] = reader.IsDBNull(nameOrd) ? string.Empty : reader.GetValue(nameOrd)?.ToString() ?? string.Empty;

            entries.Add(new CompendiumEntry(id, category, fields));
        }
        return new CompendiumListing(category, columns, entries);
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

    private static string SingleUserTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid LIMIT 1";
        return cmd.ExecuteScalar() as string
            ?? throw new InvalidDataException($"No data table found in {conn.DataSource}.");
    }

    private static IReadOnlyList<string> TableColumns(SqliteConnection conn, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1)); // column 1 = name
        return cols;
    }

    private static int OrdinalOf(SqliteDataReader reader, string name)
    {
        for (int i = 0; i < reader.FieldCount; i++)
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>True if <paramref name="file"/> begins with the SQLite format-3 magic header.</summary>
    public static bool IsSqliteFile(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> hdr = stackalloc byte[16];
            if (fs.Read(hdr) < 16) return false;
            return hdr[..15].SequenceEqual("SQLite format 3"u8);
        }
        catch { return false; }
    }
}
