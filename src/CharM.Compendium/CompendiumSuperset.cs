using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace CharM.Compendium;

/// <summary>Which of the three offline dumps a superset entry appears in.</summary>
[Flags]
public enum CompendiumSourceFlags
{
    None = 0,
    Reborn = 1,
    SqlDump = 2,
    Jsonp = 4,
}

/// <summary>
/// One entry in the 3-way superset, keyed by its compendium id
/// (<c>&lt;category&gt;&lt;num&gt;</c>). Records which sources contain it, each
/// source's name for it, and the authoritative field payload (Reborn preferred,
/// then the SQL dump, then the JSONP dump). Ids from Compendium Reborn are the
/// most correct, so an entry present in Reborn always takes Reborn's fields.
/// </summary>
public sealed class SupersetEntry
{
    public required string Category { get; init; }
    public required string Id { get; init; }
    public required int Num { get; init; }
    public required string Name { get; init; }

    /// <summary>Bitset of the sources that contain this id.</summary>
    public CompendiumSourceFlags Sources { get; init; }

    /// <summary>The source whose fields were taken as authoritative.</summary>
    public CompendiumSourceFlags AuthoritativeSource { get; init; }

    public string? RebornName { get; init; }
    public string? SqlDumpName { get; init; }
    public string? JsonpName { get; init; }

    /// <summary>True when the sources that contain this id disagree on its name.</summary>
    public bool NameDivergence { get; init; }

    /// <summary>Authoritative field payload (column → value), including the HTML <c>Txt</c> when present.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>Summary counts from a superset build (the consistency audit).</summary>
public sealed class SupersetSummary
{
    public int Total { get; set; }
    public int InAllThree { get; set; }
    public int OnlyReborn { get; set; }
    public int OnlySqlDump { get; set; }
    public int OnlyJsonp { get; set; }
    public int NameDivergences { get; set; }

    /// <summary>Ids the authoritative source (Reborn) lacks but another source has (union additions).</summary>
    public int AddedFromSqlDump { get; set; }
    public int AddedFromJsonp { get; set; }
    public SortedDictionary<string, int> ByCategory { get; } = new(StringComparer.Ordinal);
}

/// <summary>Result of a superset build: entries + audit summary.</summary>
public sealed class SupersetResult(IReadOnlyList<SupersetEntry> entries, SupersetSummary summary)
{
    public IReadOnlyList<SupersetEntry> Entries { get; } = entries;
    public SupersetSummary Summary { get; } = summary;
}

/// <summary>
/// Builds a 3-way union ("superset") of the three offline compendium dumps.
/// Reborn is authoritative for ids and field content (it is the most complete
/// and has the most-correct ids); the SQL dump and JSONP dump contribute only
/// entries Reborn lacks, so nothing any source contains is lost.
///
/// Entries are keyed on the compendium id string (<c>&lt;category&gt;&lt;num&gt;</c>),
/// which already embeds the category, so the id-space divergences between dumps
/// (e.g. the SQL dump folding weapon/armor/implement into <c>item</c>) surface
/// as distinct ids rather than silent collisions.
/// </summary>
public static class CompendiumSuperset
{
    public static SupersetResult Build(
        ICompendiumSource? reborn, ICompendiumSource? sqlDump, ICompendiumSource? jsonp)
    {
        var byId = new Dictionary<string, SupersetEntry>(StringComparer.Ordinal);
        var summary = new SupersetSummary();

        // Order matters: authoritative source first so it wins on field payload.
        var ordered = new (ICompendiumSource? Src, CompendiumSourceFlags Flag)[]
        {
            (reborn, CompendiumSourceFlags.Reborn),
            (sqlDump, CompendiumSourceFlags.SqlDump),
            (jsonp, CompendiumSourceFlags.Jsonp),
        };

        foreach (var (src, flag) in ordered)
        {
            if (src is null) continue;
            foreach (var cat in src.Categories)
            {
                foreach (var e in src.GetListing(cat).Entries)
                {
                    if (byId.TryGetValue(e.Id, out var existing))
                    {
                        byId[e.Id] = Merge(existing, e, flag);
                    }
                    else
                    {
                        byId[e.Id] = new SupersetEntry
                        {
                            Category = cat,
                            Id = e.Id,
                            Num = CompendiumIdMapper.NumericSuffix(e.Id),
                            Name = e.Name,
                            Sources = flag,
                            AuthoritativeSource = flag,
                            RebornName = flag == CompendiumSourceFlags.Reborn ? e.Name : null,
                            SqlDumpName = flag == CompendiumSourceFlags.SqlDump ? e.Name : null,
                            JsonpName = flag == CompendiumSourceFlags.Jsonp ? e.Name : null,
                            NameDivergence = false,
                            Fields = e.Fields,
                        };
                        if (flag == CompendiumSourceFlags.SqlDump) summary.AddedFromSqlDump++;
                        else if (flag == CompendiumSourceFlags.Jsonp) summary.AddedFromJsonp++;
                    }
                }
            }
        }

        var entries = new List<SupersetEntry>(byId.Values);
        entries.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Category, b.Category);
            return c != 0 ? c : a.Num.CompareTo(b.Num);
        });

        foreach (var e in entries)
        {
            summary.Total++;
            summary.ByCategory[e.Category] = summary.ByCategory.GetValueOrDefault(e.Category) + 1;
            if (e.Sources == (CompendiumSourceFlags.Reborn | CompendiumSourceFlags.SqlDump | CompendiumSourceFlags.Jsonp))
                summary.InAllThree++;
            if (e.Sources == CompendiumSourceFlags.Reborn) summary.OnlyReborn++;
            else if (e.Sources == CompendiumSourceFlags.SqlDump) summary.OnlySqlDump++;
            else if (e.Sources == CompendiumSourceFlags.Jsonp) summary.OnlyJsonp++;
            if (e.NameDivergence) summary.NameDivergences++;
        }

        return new SupersetResult(entries, summary);
    }

    private static SupersetEntry Merge(SupersetEntry existing, CompendiumEntry incoming, CompendiumSourceFlags flag)
    {
        var rebornName = flag == CompendiumSourceFlags.Reborn ? incoming.Name : existing.RebornName;
        var sqlName = flag == CompendiumSourceFlags.SqlDump ? incoming.Name : existing.SqlDumpName;
        var jsonpName = flag == CompendiumSourceFlags.Jsonp ? incoming.Name : existing.JsonpName;

        // Name divergence: any two present names that aren't equivalent.
        var names = new[] { rebornName, sqlName, jsonpName };
        bool divergence = false;
        for (int i = 0; i < names.Length && !divergence; i++)
            for (int j = i + 1; j < names.Length; j++)
                if (names[i] is { } x && names[j] is { } y && !CompendiumName.Equivalent(x, y))
                { divergence = true; break; }

        return new SupersetEntry
        {
            Category = existing.Category,
            Id = existing.Id,
            Num = existing.Num,
            // Authoritative name/fields stay with the higher-priority source
            // that was seen first (existing), so incoming never overwrites them.
            Name = existing.Name,
            Sources = existing.Sources | flag,
            AuthoritativeSource = existing.AuthoritativeSource,
            RebornName = rebornName,
            SqlDumpName = sqlName,
            JsonpName = jsonpName,
            NameDivergence = divergence,
            Fields = existing.Fields,
        };
    }

    // ---- writers ----------------------------------------------------------

    /// <summary>Write the full superset (incl. authoritative field payloads) to a SQLite working DB.</summary>
    public static void WriteSqlite(SupersetResult result, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE superset_entries (
                    id            TEXT PRIMARY KEY,
                    category      TEXT NOT NULL,
                    num           INTEGER NOT NULL,
                    name          TEXT NOT NULL,
                    sources       INTEGER NOT NULL,
                    authoritative INTEGER NOT NULL,
                    reborn_name   TEXT,
                    sqldump_name  TEXT,
                    jsonp_name    TEXT,
                    name_divergence INTEGER NOT NULL,
                    fields_json   TEXT
                );
                CREATE INDEX idx_superset_category ON superset_entries(category);
                CREATE INDEX idx_superset_num ON superset_entries(category, num);
                """;
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO superset_entries
                    (id, category, num, name, sources, authoritative,
                     reborn_name, sqldump_name, jsonp_name, name_divergence, fields_json)
                VALUES ($id, $cat, $num, $name, $src, $auth,
                        $rn, $sn, $jn, $nd, $fields)
                """;
            var pId = insert.Parameters.Add("$id", SqliteType.Text);
            var pCat = insert.Parameters.Add("$cat", SqliteType.Text);
            var pNum = insert.Parameters.Add("$num", SqliteType.Integer);
            var pName = insert.Parameters.Add("$name", SqliteType.Text);
            var pSrc = insert.Parameters.Add("$src", SqliteType.Integer);
            var pAuth = insert.Parameters.Add("$auth", SqliteType.Integer);
            var pRn = insert.Parameters.Add("$rn", SqliteType.Text);
            var pSn = insert.Parameters.Add("$sn", SqliteType.Text);
            var pJn = insert.Parameters.Add("$jn", SqliteType.Text);
            var pNd = insert.Parameters.Add("$nd", SqliteType.Integer);
            var pFields = insert.Parameters.Add("$fields", SqliteType.Text);

            foreach (var e in result.Entries)
            {
                pId.Value = e.Id;
                pCat.Value = e.Category;
                pNum.Value = e.Num;
                pName.Value = e.Name;
                pSrc.Value = (int)e.Sources;
                pAuth.Value = (int)e.AuthoritativeSource;
                pRn.Value = (object?)e.RebornName ?? DBNull.Value;
                pSn.Value = (object?)e.SqlDumpName ?? DBNull.Value;
                pJn.Value = (object?)e.JsonpName ?? DBNull.Value;
                pNd.Value = e.NameDivergence ? 1 : 0;
                pFields.Value = e.Fields.Count > 0
                    ? JsonSerializer.Serialize(e.Fields)
                    : (object)DBNull.Value;
                insert.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Write a lean JSON manifest (no HTML bodies) for review/diffing.</summary>
    public static void WriteJson(SupersetResult result, string path)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();

        writer.WritePropertyName("summary");
        JsonSerializer.Serialize(writer, result.Summary, SupersetJson.Options);

        writer.WritePropertyName("entries");
        JsonSerializer.Serialize(writer, result.Entries, SupersetJson.Options);

        writer.WriteEndObject();
    }

    /// <summary>Read a superset DB back into its entries (incl. field payloads and source flags).</summary>
    public static IReadOnlyList<SupersetEntry> ReadSqlite(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, category, num, name, sources, authoritative,
                   reborn_name, sqldump_name, jsonp_name, name_divergence, fields_json
            FROM superset_entries
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<SupersetEntry>();
        while (r.Read())
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!r.IsDBNull(10))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(10));
                if (parsed is not null)
                    foreach (var kv in parsed) fields[kv.Key] = kv.Value;
            }
            list.Add(new SupersetEntry
            {
                Id = r.GetString(0),
                Category = r.GetString(1),
                Num = r.GetInt32(2),
                Name = r.GetString(3),
                Sources = (CompendiumSourceFlags)r.GetInt32(4),
                AuthoritativeSource = (CompendiumSourceFlags)r.GetInt32(5),
                RebornName = r.IsDBNull(6) ? null : r.GetString(6),
                SqlDumpName = r.IsDBNull(7) ? null : r.GetString(7),
                JsonpName = r.IsDBNull(8) ? null : r.GetString(8),
                NameDivergence = r.GetInt32(9) != 0,
                Fields = fields,
            });
        }
        return list;
    }
}

internal static class SupersetJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
