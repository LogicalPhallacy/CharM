using System.Collections.Concurrent;
using System.Text.Json;
using CharM.Engine.Rules;
using Microsoft.Data.Sqlite;

namespace CharM.RulesDb.Storage;

/// <summary>
/// Read-only query interface over the rules SQLite database.
/// </summary>
public interface IRulesDatabase : IDisposable
{
    RulesElement? FindByInternalId(string internalId);
    RulesElement? FindByNameAndType(string name, string type);
    IEnumerable<RulesElement> FindByType(string type);
    IEnumerable<RulesElement> FindByType(string type, bool includeRules);
    IEnumerable<RulesElement> FindByCategory(string category);
    IEnumerable<RulesElement> FindByTypeAndCategory(string type, params string[] categories);
    IEnumerable<RulesElement> FindBySource(string source);
    IEnumerable<RulesElement> FindByTypeAndSource(string type, string source);
    IEnumerable<RulesElement> FindByTypeAndSource(string type, string source, bool includeRules);
    IEnumerable<string> GetDistinctSources();
    int Count { get; }

    /// <summary>
    /// Registered part layers (base + overlays) from the metadata tables.
    /// Empty when the DB predates the metadata schema. Off the read path.
    /// </summary>
    IReadOnlyList<PartLayer> GetPartLayers() => [];

    /// <summary>
    /// Category of the highest-precedence part that created/overwrote the given
    /// element (e.g. "Homebrew", "UnearthedArcana", "sorted"), or null when the
    /// element comes only from the base or provenance is unknown.
    /// </summary>
    string? GetElementProvenanceCategory(string internalId) => null;

    /// <summary>
    /// Optional cache pre-warm. Implementations may load all elements at once
    /// so subsequent lookups are lock-free. Safe to call multiple times.
    /// </summary>
    void Preload() { }
}

/// <summary>
/// A registered layer (base snapshot or part overlay) in the rules database.
/// </summary>
public sealed record PartLayer(
    string PartId,
    string Filename,
    string? Category,
    string? DisplayName,
    string? Version,
    bool Enabled,
    int LayerOrder,
    bool IsBase);

/// <summary>
/// SQLite-backed implementation of <see cref="IRulesDatabase"/>.
/// </summary>
public sealed class RulesDatabase : IRulesDatabase
{
    private readonly SqliteConnection _connection;
    private readonly object _queryLock = new();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ConcurrentDictionary<string, RulesElement> _byInternalId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RulesElement> _byNameAndType =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<RulesElement>> _byType =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<RulesElement>> _byTypeAndSource =
        new(StringComparer.OrdinalIgnoreCase);
    private string[]? _distinctSourcesCache;
    private int? _countCache;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new RuleDirectiveJsonConverter());
        return options;
    }

    /// <summary>
    /// Open an existing rules database file.
    /// </summary>
    public RulesDatabase(string dbPath)
    {
        // Pooling=False so disposing this connection actually releases the OS
        // file handle (and the WAL/SHM sidecars) immediately. With the default
        // pooling on, Dispose() returns the connection to the pool and keeps the
        // file open, which corrupts the database when the working rules.db is
        // later overwritten by a rebuild ("database disk image is malformed").
        _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        _connection.Open();
    }

    /// <summary>
    /// Wrap an already-open connection (used by RulesDbBuilder after import).
    /// </summary>
    internal RulesDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    private readonly ConcurrentDictionary<string, byte> _missingInternalIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _missingNameType =
        new(StringComparer.OrdinalIgnoreCase);

    public RulesElement? FindByInternalId(string internalId)
    {
        if (_byInternalId.TryGetValue(internalId, out var cached))
            return cached;
        // After Preload(), every existing id is in the cache. Any miss against
        // the warmed cache must therefore reference an id not in the database
        // (typehouseruled / typo / synthesised) — short-circuit without
        // re-querying SQLite, which used to dominate batch-import perf.
        if (_categoriesPreloaded || _missingInternalIds.ContainsKey(internalId))
            return null;

        lock (_queryLock)
        {
            if (_byInternalId.TryGetValue(internalId, out cached))
                return cached;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT internal_id, name, type, source, prereqs, fields_json, rules_json
                FROM rules_elements
                WHERE internal_id = $id COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$id", internalId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                _missingInternalIds[internalId] = 1;
                return null;
            }

            cached = MapElement(reader);
            LoadCategories(cached);
            _byInternalId[internalId] = cached;
            return cached;
        }
    }

    public RulesElement? FindByNameAndType(string name, string type)
    {
        var cacheKey = $"{name}\0{type}";
        if (_byNameAndType.TryGetValue(cacheKey, out var cached))
            return cached;
        if (_categoriesPreloaded || _missingNameType.ContainsKey(cacheKey))
            return null;

        lock (_queryLock)
        {
            if (_byNameAndType.TryGetValue(cacheKey, out cached))
                return cached;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT internal_id, name, type, source, prereqs, fields_json, rules_json
                FROM rules_elements
                WHERE name = $name AND type = $type
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$type", type);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                _missingNameType[cacheKey] = 1;
                return null;
            }

            cached = MapElement(reader);
            LoadCategories(cached);
            _byNameAndType[cacheKey] = cached;
            return cached;
        }
    }

    public IEnumerable<RulesElement> FindByType(string type) => FindByType(type, includeRules: true);

    public IEnumerable<RulesElement> FindByType(string type, bool includeRules)
    {
        var cacheKey = $"{type}\0{includeRules}";
        if (_byType.TryGetValue(cacheKey, out var cached))
            return cached;

        lock (_queryLock)
        {
            if (_byType.TryGetValue(cacheKey, out cached))
                return cached;

            var elements = new List<RulesElement>();
            using var cmd = _connection.CreateCommand();

            if (includeRules)
            {
                cmd.CommandText = """
                    SELECT internal_id, name, type, source, prereqs, fields_json, rules_json
                    FROM rules_elements
                    WHERE type = $type
                    """;
                cmd.Parameters.AddWithValue("$type", type);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    elements.Add(MapElement(reader));
                LoadCategoriesBatch(elements);
            }
            else
            {
                // Lightweight: include prereqs + fields_json + categories, but skip rules_json.
                // This keeps candidate enumeration fast while preserving level/category data.
                cmd.CommandText = """
                    SELECT re.internal_id, re.name, re.type, re.source, re.prereqs,
                           re.fields_json, GROUP_CONCAT(ec.category) as cats
                    FROM rules_elements re
                    LEFT JOIN element_categories ec ON re.internal_id = ec.internal_id
                    WHERE re.type = $type
                    GROUP BY re.internal_id
                    """;
                cmd.Parameters.AddWithValue("$type", type);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    elements.Add(MapLightweightElement(reader));
            }

            cached = elements.ToArray();
            _byType[cacheKey] = cached;
            return cached;
        }
    }

    public IEnumerable<RulesElement> FindByCategory(string category)
    {
        var elements = new List<RulesElement>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT re.internal_id, re.name, re.type, re.source, re.prereqs, re.fields_json, re.rules_json
            FROM rules_elements re
            INNER JOIN element_categories ec ON re.internal_id = ec.internal_id
            WHERE ec.category = $cat
            """;
        cmd.Parameters.AddWithValue("$cat", category);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            elements.Add(MapElement(reader));

        LoadCategoriesBatch(elements);
        return elements;
    }

    public IEnumerable<RulesElement> FindByTypeAndCategory(string type, params string[] categories)
    {
        var elements = new List<RulesElement>();
        using var cmd = _connection.CreateCommand();

        if (categories.Length == 0)
        {
            cmd.CommandText = """
                SELECT internal_id, name, type, source, prereqs, fields_json, rules_json
                FROM rules_elements
                WHERE type = $type
                """;
            cmd.Parameters.AddWithValue("$type", type);
        }
        else
        {
            var joins = new System.Text.StringBuilder();
            for (int i = 0; i < categories.Length; i++)
            {
                joins.Append($" INNER JOIN element_categories ec{i} ON re.internal_id = ec{i}.internal_id AND ec{i}.category = $cat{i}");
                cmd.Parameters.AddWithValue($"$cat{i}", categories[i]);
            }

            cmd.CommandText = $"""
                SELECT DISTINCT re.internal_id, re.name, re.type, re.source, re.prereqs, re.fields_json, re.rules_json
                FROM rules_elements re{joins}
                WHERE re.type = $type
                """;
            cmd.Parameters.AddWithValue("$type", type);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            elements.Add(MapElement(reader));

        LoadCategoriesBatch(elements);
        return elements;
    }

    public IEnumerable<RulesElement> FindBySource(string source)
    {
        var elements = new List<RulesElement>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT internal_id, name, type, source, prereqs, fields_json, rules_json
            FROM rules_elements
            WHERE source = $source OR source LIKE $sourceStart OR source LIKE $sourceMid OR source LIKE $sourceEnd
            """;
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$sourceStart", source + ",%");
        cmd.Parameters.AddWithValue("$sourceMid", "%," + source + ",%");
        cmd.Parameters.AddWithValue("$sourceEnd", "%," + source);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            elements.Add(MapElement(reader));

        LoadCategoriesBatch(elements);
        return elements;
    }

    public IEnumerable<RulesElement> FindByTypeAndSource(string type, string source) =>
        FindByTypeAndSource(type, source, includeRules: true);

    public IEnumerable<RulesElement> FindByTypeAndSource(string type, string source, bool includeRules)
    {
        var cacheKey = $"{type}\0{source}\0{includeRules}";
        if (_byTypeAndSource.TryGetValue(cacheKey, out var cached))
            return cached;

        lock (_queryLock)
        {
            if (_byTypeAndSource.TryGetValue(cacheKey, out cached))
                return cached;

            var elements = new List<RulesElement>();
            using var cmd = _connection.CreateCommand();

            if (includeRules)
            {
                cmd.CommandText = """
                    SELECT internal_id, name, type, source, prereqs, fields_json, rules_json
                    FROM rules_elements
                    WHERE type = $type AND (source = $source OR source LIKE $sourceStart OR source LIKE $sourceMid OR source LIKE $sourceEnd)
                    """;
            }
            else
            {
                cmd.CommandText = """
                    SELECT re.internal_id, re.name, re.type, re.source, re.prereqs,
                           re.fields_json, GROUP_CONCAT(ec.category) as cats
                    FROM rules_elements re
                    LEFT JOIN element_categories ec ON re.internal_id = ec.internal_id
                    WHERE re.type = $type AND (re.source = $source OR re.source LIKE $sourceStart OR re.source LIKE $sourceMid OR re.source LIKE $sourceEnd)
                    GROUP BY re.internal_id
                    """;
            }

            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$sourceStart", source + ",%");
            cmd.Parameters.AddWithValue("$sourceMid", "%," + source + ",%");
            cmd.Parameters.AddWithValue("$sourceEnd", "%," + source);

            using var reader = cmd.ExecuteReader();
            if (includeRules)
            {
                while (reader.Read())
                    elements.Add(MapElement(reader));
                LoadCategoriesBatch(elements);
            }
            else
            {
                while (reader.Read())
                    elements.Add(MapLightweightElement(reader));
            }

            cached = elements.ToArray();
            _byTypeAndSource[cacheKey] = cached;
            return cached;
        }
    }

    public IEnumerable<string> GetDistinctSources()
    {
        if (_distinctSourcesCache is not null)
            return _distinctSourcesCache;

        lock (_queryLock)
        {
            if (_distinctSourcesCache is not null)
                return _distinctSourcesCache;

            var sources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT source FROM rules_elements WHERE source IS NOT NULL";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetString(0);
                foreach (var part in raw.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0)
                        sources.Add(trimmed);
                }
            }

            _distinctSourcesCache = [.. sources];
            return _distinctSourcesCache;
        }
    }

    public IReadOnlyList<PartLayer> GetPartLayers()
    {
        lock (_queryLock)
        {
            if (!TableExists("part_registry"))
                return [];

            var layers = new List<PartLayer>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT part_id, filename, category, display_name, version, enabled, layer_order, is_base
                FROM part_registry ORDER BY layer_order
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                layers.Add(new PartLayer(
                    PartId: reader.GetString(0),
                    Filename: reader.GetString(1),
                    Category: reader.IsDBNull(2) ? null : reader.GetString(2),
                    DisplayName: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Version: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Enabled: reader.GetInt64(5) != 0,
                    LayerOrder: (int)reader.GetInt64(6),
                    IsBase: reader.GetInt64(7) != 0));
            }
            return layers;
        }
    }

    public string? GetElementProvenanceCategory(string internalId)
    {
        lock (_queryLock)
        {
            if (!TableExists("part_provenance"))
                return null;

            // Highest-precedence (largest layer_order) non-base part that
            // created or overwrote the element wins.
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT r.category
                FROM part_provenance p
                JOIN part_registry r ON r.part_id = p.part_id
                WHERE p.internal_id = $id AND r.is_base = 0 AND r.category IS NOT NULL
                ORDER BY r.layer_order DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", internalId);
            return cmd.ExecuteScalar() as string;
        }
    }

    private bool TableExists(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    public int Count
    {
        get
        {
            if (_countCache.HasValue)
                return _countCache.Value;

            lock (_queryLock)
            {
                if (_countCache.HasValue)
                    return _countCache.Value;

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM rules_elements";
                _countCache = Convert.ToInt32(cmd.ExecuteScalar());
                return _countCache.Value;
            }
        }
    }

    /// <summary>
    /// Search for elements matching optional filters. All filters are ANDed.
    /// <paramref name="namePattern"/> uses SQL LIKE syntax; pass a bare substring
    /// (it will be wrapped with %) or include explicit % wildcards yourself.
    /// </summary>
    public IReadOnlyList<RulesElement> Search(
        string? namePattern = null,
        string? type = null,
        string? source = null,
        string? category = null,
        bool includeRules = false,
        int limit = 0)
    {
        var elements = new List<RulesElement>();
        lock (_queryLock)
        {
            using var cmd = _connection.CreateCommand();
            var sql = new System.Text.StringBuilder();
            if (includeRules)
                sql.Append("SELECT DISTINCT re.internal_id, re.name, re.type, re.source, re.prereqs, re.fields_json, re.rules_json FROM rules_elements re");
            else
                sql.Append("SELECT re.internal_id, re.name, re.type, re.source, re.prereqs, re.fields_json, GROUP_CONCAT(ec2.category) as cats FROM rules_elements re LEFT JOIN element_categories ec2 ON re.internal_id = ec2.internal_id");

            if (!string.IsNullOrEmpty(category))
                sql.Append(" INNER JOIN element_categories ec ON re.internal_id = ec.internal_id");

            sql.Append(" WHERE 1=1");
            if (!string.IsNullOrEmpty(namePattern))
            {
                var p = namePattern.Contains('%') ? namePattern : $"%{namePattern}%";
                sql.Append(" AND re.name LIKE $name");
                cmd.Parameters.AddWithValue("$name", p);
            }
            if (!string.IsNullOrEmpty(type))
            {
                sql.Append(" AND re.type = $type");
                cmd.Parameters.AddWithValue("$type", type);
            }
            if (!string.IsNullOrEmpty(source))
            {
                sql.Append(" AND (re.source = $source OR re.source LIKE $sStart OR re.source LIKE $sMid OR re.source LIKE $sEnd)");
                cmd.Parameters.AddWithValue("$source", source);
                cmd.Parameters.AddWithValue("$sStart", source + ",%");
                cmd.Parameters.AddWithValue("$sMid", "%," + source + ",%");
                cmd.Parameters.AddWithValue("$sEnd", "%," + source);
            }
            if (!string.IsNullOrEmpty(category))
            {
                sql.Append(" AND ec.category = $cat");
                cmd.Parameters.AddWithValue("$cat", category);
            }

            if (!includeRules)
                sql.Append(" GROUP BY re.internal_id");
            sql.Append(" ORDER BY re.type, re.name");
            if (limit > 0)
            {
                sql.Append(" LIMIT $limit");
                cmd.Parameters.AddWithValue("$limit", limit);
            }

            cmd.CommandText = sql.ToString();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                elements.Add(includeRules ? MapElement(reader) : MapLightweightElement(reader));
            if (includeRules)
                LoadCategoriesBatch(elements);
        }
        return elements;
    }

    /// <summary>Returns distinct element types present in the database, sorted.</summary>
    public IReadOnlyList<string> GetDistinctTypes()
    {
        var types = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_queryLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT type FROM rules_elements WHERE type IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) types.Add(reader.GetString(0));
        }
        return [.. types];
    }

    /// <summary>
    /// Pre-warm the FindByInternalId cache by reading every element in a single
    /// table scan. After preload, lookups by internal-id hit the lock-free
    /// ConcurrentDictionary path and never touch SQLite. Run this at startup
    /// when many parallel callers will be hammering the database — without
    /// preload, every cache miss serializes on _queryLock and concurrent
    /// imports degrade to single-threaded throughput.
    /// </summary>
    public void Preload()
    {
        lock (_queryLock)
        {
            if (_byInternalId.Count > 0 && _categoriesPreloaded)
                return;

            var loaded = new List<RulesElement>(capacity: 8192);
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT internal_id, name, type, source, prereqs, fields_json, rules_json FROM rules_elements";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var element = MapElement(reader);
                    loaded.Add(element);
                    _byInternalId[element.InternalId] = element;
                }
            }

            // Single scan of element_categories — much cheaper than per-element
            // lookups inside FindByInternalId / FindBy* on cache miss.
            using (var catCmd = _connection.CreateCommand())
            {
                catCmd.CommandText = "SELECT internal_id, category FROM element_categories";
                using var reader = catCmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (_byInternalId.TryGetValue(id, out var element))
                        element.Categories.Add(reader.GetString(1));
                }
            }
            _categoriesPreloaded = true;

            // Populate _byNameAndType from the same pass so name+type lookups
            // are also lock-free hits.
            foreach (var el in loaded)
            {
                var key = $"{el.Name}\0{el.Type}";
                _byNameAndType.TryAdd(key, el);
            }
        }
    }

    private bool _categoriesPreloaded;

    private RulesElement MapElement(SqliteDataReader reader)
    {
        string? fieldsJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        string? rulesJson = reader.IsDBNull(6) ? null : reader.GetString(6);

        var (fields, fieldEntries) = ParseFieldsJson(fieldsJson);

        var rules = rulesJson is not null
            ? JsonSerializer.Deserialize<List<RuleDirective>>(rulesJson, JsonOptions) ?? []
            : new List<RuleDirective>();

        return new RulesElement
        {
            InternalId = reader.GetString(0),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            Source = reader.IsDBNull(3) ? null : reader.GetString(3),
            Prereqs = reader.IsDBNull(4) ? null : reader.GetString(4),
            Fields = fields,
            FieldEntries = fieldEntries,
            Rules = rules,
        };
    }

    /// <summary>
    /// Map a lightweight result row: id, name, type, source, prereqs, fields_json, GROUP_CONCAT(categories).
    /// Skips rules_json deserialization, which is the expensive part for candidate enumeration.
    /// </summary>
    private static RulesElement MapLightweightElement(SqliteDataReader reader)
    {
        var (fields, fieldEntries) = ParseFieldsJson(reader.IsDBNull(5) ? null : reader.GetString(5));

        var element = new RulesElement
        {
            InternalId = reader.GetString(0),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            Source = reader.IsDBNull(3) ? null : reader.GetString(3),
            Prereqs = reader.IsDBNull(4) ? null : reader.GetString(4),
            Fields = fields,
            FieldEntries = fieldEntries,
        };

        // Parse GROUP_CONCAT categories (comma-separated)
        if (!reader.IsDBNull(6))
        {
            var cats = reader.GetString(6);
            foreach (var cat in cats.Split(','))
            {
                var trimmed = cat.Trim();
                if (trimmed.Length > 0)
                    element.Categories.Add(trimmed);
            }
        }

        return element;
    }

    /// <summary>
    /// Parse fields_json, accepting both formats:
    ///   * Legacy: a JSON object <c>{"Hit": "1d6"}</c> — used by databases
    ///     built before the array-of-pairs migration.
    ///   * Current: a JSON array of <c>[key, value]</c> pairs that preserves
    ///     duplicates and order — needed for elements like Ravening Thought
    ///     that emit two <c>&lt;specific name="Hit"&gt;</c> children (one for
    ///     the primary attack at 2d6, one for the secondary at 1d6).
    /// Returns the first-wins lookup dictionary alongside the full ordered list.
    /// </summary>
    private static (Dictionary<string, string> Fields, IReadOnlyList<KeyValuePair<string, string>> Entries) ParseFieldsJson(string? fieldsJson)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<KeyValuePair<string, string>>();

        if (string.IsNullOrEmpty(fieldsJson))
            return (fields, entries);

        // Sniff first non-whitespace character to disambiguate the two formats.
        int i = 0;
        while (i < fieldsJson.Length && char.IsWhiteSpace(fieldsJson[i])) i++;
        bool isArray = i < fieldsJson.Length && fieldsJson[i] == '[';

        if (isArray)
        {
            var pairs = JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(fieldsJson) ?? [];
            foreach (var pair in pairs)
            {
                entries.Add(pair);
                if (!fields.ContainsKey(pair.Key))
                    fields[pair.Key] = pair.Value;
            }
        }
        else
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(fieldsJson) ?? new();
            foreach (var kv in dict)
            {
                entries.Add(new(kv.Key, kv.Value));
                if (!fields.ContainsKey(kv.Key))
                    fields[kv.Key] = kv.Value;
            }
        }

        return (fields, entries);
    }

    private void LoadCategories(RulesElement element)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT category FROM element_categories WHERE internal_id = $id";
        cmd.Parameters.AddWithValue("$id", element.InternalId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            element.Categories.Add(reader.GetString(0));
    }

    /// <summary>
    /// Batch-load categories for multiple elements in a single query.
    /// Avoids N+1 problem when loading many elements at once.
    /// </summary>
    private void LoadCategoriesBatch(IReadOnlyList<RulesElement> elements)
    {
        if (elements.Count == 0) return;

        var lookup = new Dictionary<string, RulesElement>(elements.Count);
        foreach (var e in elements)
            lookup.TryAdd(e.InternalId, e);

        using var cmd = _connection.CreateCommand();
        var idParams = new System.Text.StringBuilder();
        for (int i = 0; i < elements.Count; i++)
        {
            if (i > 0) idParams.Append(',');
            idParams.Append($"$id{i}");
            cmd.Parameters.AddWithValue($"$id{i}", elements[i].InternalId);
        }

        cmd.CommandText = $"SELECT internal_id, category FROM element_categories WHERE internal_id IN ({idParams})";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (lookup.TryGetValue(id, out var element))
                element.Categories.Add(reader.GetString(1));
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    /// <summary>Shared JSON options for serialization (used by RulesDbBuilder).</summary>
    internal static JsonSerializerOptions SharedJsonOptions => JsonOptions;
}
