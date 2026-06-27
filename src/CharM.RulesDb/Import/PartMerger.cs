using System.Text.Json;
using System.Xml.Linq;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;
using Microsoft.Data.Sqlite;
using static CharM.RulesDb.Import.RulesXmlImportHelpers;

namespace CharM.RulesDb.Import;

/// <summary>
/// Merges CBLoader .part files into an existing SQLite rules database.
/// Supports: RulesElement (create/overwrite), AppendNodes, DeleteElement, MassAppend.
/// </summary>
public static partial class PartMerger
{
    /// <summary>
    /// Download and merge all .part files from an index file Url
    /// </summary>
    /// <param name="dbPath"></param>
    /// <param name="indexFileUrl"></param>
    /// <param name="progress"></param>
    /// <returns></returns>
    public static MergeResult MergeFromIndex(string dbPath, string indexFileUrl, IProgress<string>? progress = null)
    {
        var tempPath = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        _ = Directory.CreateDirectory(tempPath);
        try
        {
            DownloadIndexParts(indexFileUrl, tempPath, progress);
            return Merge(dbPath, tempPath, progress);
        }
        finally
        {
            Directory.Delete(tempPath, recursive: true);
        }
    }

    /// <summary>
    /// Download every part referenced by a CBLoader index file into
    /// <paramref name="destDirectory"/> (also saving the index as WotC.index).
    /// Does not merge — used by the layered store so downloaded parts can be
    /// archived and toggled like local ones.
    /// </summary>
    public static void DownloadIndexParts(string indexFileUrl, string destDirectory, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(destDirectory);
        using var downloadClient = new HttpClient();
        var indexUri = new Uri(indexFileUrl, UriKind.Absolute);

        progress?.Report($"Downloading part index {indexUri}");
        using var indexResp = downloadClient.GetAsync(indexUri).GetAwaiter().GetResult();
        indexResp.EnsureSuccessStatusCode();

        using var indexStream = indexResp.Content.ReadAsStream();
        var doc = XDocument.Load(indexStream);
        doc.Save(Path.Combine(destDirectory, "WotC.index"));

        List<Task> tasks = new();
        foreach (var part in doc.Descendants("Part"))
        {
            string? filename = part.Element("Filename")?.Value?.Trim();
            string? address = part.Element("PartAddress")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(address))
                continue;

            var partUri = new Uri(indexUri, address);
            tasks.Add(DownloadPart(downloadClient, partUri, filename, destDirectory, progress));
        }

        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }

    private static async Task DownloadPart(
        HttpClient downloader,
        Uri url,
        string fileName,
        string tempDir,
        IProgress<string>? progress)
    {
        progress?.Report($"Downloading {fileName}");
        using var partResp = await downloader.GetAsync(url);
        partResp.EnsureSuccessStatusCode();

        var safeFileName = Path.GetFileName(fileName);
        await using var destination = File.Open(Path.Join(tempDir, safeFileName), FileMode.Create);
        await partResp.Content.CopyToAsync(destination);
    }
    /// <summary>
    /// Merge all .part files from a directory into the rules database.
    /// </summary>
    /// <param name="dbPath">Path to existing SQLite rules database.</param>
    /// <param name="partsDirectory">Directory containing .part files and optional WotC.index.</param>
    /// <param name="progress">Progress callback (status messages).</param>
    public static MergeResult Merge(string dbPath, string partsDirectory, IProgress<string>? progress = null)
    {
        var obsolete = LoadObsoleteSet(partsDirectory);
        string? category = DeriveCategory(partsDirectory);

        var partFiles = Directory.GetFiles(partsDirectory, "*.part")
            .Where(f => !obsolete.Contains(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(f => new PartSourceFile(f, Path.GetFileName(f), category))
            .ToList();

        return MergeFiles(dbPath, partFiles, progress);
    }

    /// <summary>
    /// Merge an explicit, ordered list of part files into the database. Unlike
    /// <see cref="Merge(string,string,IProgress{string})"/> this does not glob a
    /// directory — the caller controls exactly which parts apply and in what
    /// order. Used by the layered rebuild service to materialize a working DB
    /// from a chosen enabled set.
    /// </summary>
    public static MergeResult MergeFiles(
        string dbPath, IReadOnlyList<PartSourceFile> orderedParts, IProgress<string>? progress = null)
    {
        int filesProcessed = 0, added = 0, updated = 0, deleted = 0, appended = 0;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        Execute(connection, "PRAGMA journal_mode = WAL");
        Execute(connection, "PRAGMA synchronous = NORMAL");

        RulesDbSchema.Create(connection);

        var jsonOptions = RulesDatabase.SharedJsonOptions;
        int layerOrder = NextLayerOrder(connection);

        foreach (var part in orderedParts)
        {
            string fileName = Path.GetFileName(part.Path);
            progress?.Report($"  {fileName}");

            XDocument doc;
            try { doc = XDocument.Load(part.Path); }
            catch { continue; }

            var root = doc.Root;
            if (root is null) continue;

            PartFileInfo partInfo;
            try { partInfo = PartMetadataReader.Read(part.Path, partId: part.PartId, category: part.Category); }
            catch { partInfo = null!; }

            using var tx = connection.BeginTransaction();

            if (partInfo is not null)
                RegisterPart(connection, tx, partInfo, layerOrder++);

            string partId = partInfo?.PartId ?? part.PartId;

            foreach (var el in root.Elements())
            {
                switch (el.Name.LocalName)
                {
                    case "RulesElement":
                    {
                        var parsed = ParseRulesElement(el);
                        if (parsed is null) continue;
                        bool exists = ElementExists(connection, tx, parsed.Element.InternalId);
                        UpsertElement(connection, tx, parsed, jsonOptions);
                        RecordProvenance(connection, tx, parsed.Element.InternalId, partId,
                            exists ? "overwrite" : "create");
                        if (exists) updated++; else added++;
                        break;
                    }
                    case "AppendNodes":
                    {
                        string? id = Attr(el, "internal-id");
                        if (id is null) continue;
                        appended += AppendToElement(connection, tx, id, el, jsonOptions);
                        RecordProvenance(connection, tx, id, partId, "append");
                        break;
                    }
                    case "DeleteElement":
                    {
                        string? id = Attr(el, "internal-id");
                        if (id is null) continue;
                        deleted += DeleteElement(connection, tx, id);
                        RecordProvenance(connection, tx, id, partId, "delete");
                        break;
                    }
                    case "MassAppend":
                    {
                        string? ids = Attr(el, "ids");
                        if (ids is null) continue;
                        foreach (var id in ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            appended += AppendToElement(connection, tx, id, el, jsonOptions);
                            RecordProvenance(connection, tx, id, partId, "append");
                        }
                        break;
                    }
                }
            }

            tx.Commit();
            filesProcessed++;
        }

        return new MergeResult(filesProcessed, added, updated, deleted, appended);
    }

    private static readonly HashSet<string> KnownCategories =
        new(StringComparer.OrdinalIgnoreCase) { "sorted", "UnearthedArcana", "Homebrew", "3rdParty" };

    private static string? DeriveCategory(string partsDirectory)
    {
        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(partsDirectory));
        return KnownCategories.Contains(folder) ? folder : null;
    }

    private static int NextLayerOrder(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(layer_order), 0) + 1 FROM part_registry";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 1;
    }

    private static void RegisterPart(SqliteConnection conn, SqliteTransaction tx, PartFileInfo info, int layerOrder)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO part_registry
                (part_id, filename, category, display_name, version,
                 content_hash, source_url, enabled, layer_order, is_base, applied_at)
            VALUES ($id, $fn, $cat, $disp, $ver, $hash, $url, 1, $order, 0, $now)
            ON CONFLICT(part_id) DO UPDATE SET
                filename = excluded.filename,
                category = excluded.category,
                display_name = excluded.display_name,
                version = excluded.version,
                content_hash = excluded.content_hash,
                source_url = excluded.source_url,
                layer_order = excluded.layer_order,
                applied_at = excluded.applied_at
            """;
        cmd.Parameters.AddWithValue("$id", info.PartId);
        cmd.Parameters.AddWithValue("$fn", info.Filename);
        cmd.Parameters.AddWithValue("$cat", (object?)info.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$disp", (object?)(info.Description ?? info.Filename) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", (object?)info.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", info.ContentHash);
        cmd.Parameters.AddWithValue("$url", (object?)info.PartAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$order", layerOrder);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void RecordProvenance(SqliteConnection conn, SqliteTransaction tx, string internalId, string partId, string op)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Last writer wins per (element, part): a part that touches an element
        // multiple times records its final op.
        cmd.CommandText = """
            INSERT INTO part_provenance (internal_id, part_id, op)
            VALUES ($id, $part, $op)
            ON CONFLICT(internal_id, part_id) DO UPDATE SET op = excluded.op
            """;
        cmd.Parameters.AddWithValue("$id", internalId);
        cmd.Parameters.AddWithValue("$part", partId);
        cmd.Parameters.AddWithValue("$op", op);
        cmd.ExecuteNonQuery();
    }

    // ========================================================================
    // WotC.index parsing
    // ========================================================================

    private static HashSet<string> LoadObsoleteSet(string directory)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string indexPath = Path.Combine(directory, "WotC.index");
        if (!File.Exists(indexPath)) return set;

        try
        {
            var doc = XDocument.Load(indexPath);
            foreach (var obs in doc.Descendants("Obsolete"))
            {
                string? filename = obs.Element("Filename")?.Value?.Trim();
                if (filename is not null)
                    set.Add(filename);
            }
        }
        catch { /* index is optional */ }

        return set;
    }

    // ========================================================================
    // SQLite operations
    // ========================================================================

    private static bool ElementExists(SqliteConnection conn, SqliteTransaction tx, string internalId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM rules_elements WHERE internal_id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", internalId);
        return cmd.ExecuteScalar() is not null;
    }

    private static void UpsertElement(
        SqliteConnection conn, SqliteTransaction tx, ParsedElement parsed, JsonSerializerOptions jsonOptions)
    {
        var element = parsed.Element;

        var (fieldsJson, rulesJson) = RulesElementJson.Serialize(element);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO rules_elements (internal_id, name, type, source, prereqs, fields_json, rules_json)
            VALUES ($id, $name, $type, $source, $prereqs, $fields, $rules)
            """;
        cmd.Parameters.AddWithValue("$id", element.InternalId);
        cmd.Parameters.AddWithValue("$name", element.Name);
        cmd.Parameters.AddWithValue("$type", element.Type);
        cmd.Parameters.AddWithValue("$source", (object?)element.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prereqs", (object?)element.Prereqs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fields", (object?)fieldsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rules", (object?)rulesJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        foreach (var cat in parsed.Categories)
        {
            using var catCmd = conn.CreateCommand();
            catCmd.Transaction = tx;
            catCmd.CommandText = "INSERT OR IGNORE INTO element_categories (internal_id, category) VALUES ($id, $cat)";
            catCmd.Parameters.AddWithValue("$id", element.InternalId);
            catCmd.Parameters.AddWithValue("$cat", cat);
            catCmd.ExecuteNonQuery();
        }
    }

    private static int AppendToElement(
        SqliteConnection conn, SqliteTransaction tx, string internalId, XElement appendEl, JsonSerializerOptions jsonOptions)
    {
        int count = 0;

        var newDirectives = new List<RuleDirective>();
        var newCategories = new List<string>();

        foreach (var child in appendEl.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "rules":
                    ParseRulesBlock(child, newDirectives);
                    break;
                case "Category":
                    ParseCategories(child, newCategories);
                    break;
            }
        }

        if (newDirectives.Count > 0)
        {
            string? existingJson = ReadRulesJson(conn, tx, internalId);
            var existing = existingJson is not null
                ? JsonSerializer.Deserialize<List<RuleDirective>>(existingJson, jsonOptions) ?? []
                : new List<RuleDirective>();

            existing.AddRange(newDirectives);

            // Deduplicate: remove identical directives that may have been appended
            // from both the base DB and a .part file (same statadd appearing twice)
            var deduplicated = DeduplicateDirectives(existing, jsonOptions);
            string updatedJson = JsonSerializer.Serialize(deduplicated, jsonOptions);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE rules_elements SET rules_json = $rules WHERE internal_id = $id";
            cmd.Parameters.AddWithValue("$rules", updatedJson);
            cmd.Parameters.AddWithValue("$id", internalId);
            if (cmd.ExecuteNonQuery() > 0)
                count += newDirectives.Count;
        }

        foreach (var cat in newCategories)
        {
            using var catCmd = conn.CreateCommand();
            catCmd.Transaction = tx;
            catCmd.CommandText = "INSERT OR IGNORE INTO element_categories (internal_id, category) VALUES ($id, $cat)";
            catCmd.Parameters.AddWithValue("$id", internalId);
            catCmd.Parameters.AddWithValue("$cat", cat);
            catCmd.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private static string? ReadRulesJson(SqliteConnection conn, SqliteTransaction tx, string internalId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT rules_json FROM rules_elements WHERE internal_id = $id";
        cmd.Parameters.AddWithValue("$id", internalId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    private static int DeleteElement(SqliteConnection conn, SqliteTransaction tx, string internalId)
    {
        using var catCmd = conn.CreateCommand();
        catCmd.Transaction = tx;
        catCmd.CommandText = "DELETE FROM element_categories WHERE internal_id = $id";
        catCmd.Parameters.AddWithValue("$id", internalId);
        catCmd.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM rules_elements WHERE internal_id = $id";
        cmd.Parameters.AddWithValue("$id", internalId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Remove duplicate directives by comparing their JSON serialization.
    /// This handles cases where a .part file AppendNodes adds a directive
    /// that already exists on the element (from the base DB or a previous merge).
    /// </summary>
    private static List<RuleDirective> DeduplicateDirectives(List<RuleDirective> directives, JsonSerializerOptions jsonOptions)
    {
        var seen = new HashSet<string>();
        var result = new List<RuleDirective>();
        foreach (var d in directives)
        {
            string key = JsonSerializer.Serialize(d, d.GetType(), jsonOptions);
            if (seen.Add(key))
                result.Add(d);
        }
        return result;
    }

    // ========================================================================
    // XML → domain model parsing (mirrors RulesXmlReader but uses XElement)
    // ========================================================================

    private static ParsedElement? ParseRulesElement(XElement el)
    {
        string? name = Attr(el, "name");
        string? type = Attr(el, "type");
        string? internalId = Attr(el, "internal-id");
        string? source = Attr(el, "source");

        if (name is null || type is null || internalId is null)
            return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fieldEntries = new List<KeyValuePair<string, string>>();
        string? prereqs = null;
        var categories = new List<string>();
        var rules = new List<RuleDirective>();

        foreach (var child in el.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "specific":
                    string? fieldName = Attr(child, "name");
                    if (fieldName is not null)
                    {
                        string value = child.Value.Trim();
                        // Always record the raw entry so duplicates are
                        // recoverable (e.g. Ravening Thought emits two
                        // <specific name="Hit"> children). The lookup
                        // Dictionary keeps the FIRST occurrence to match
                        // OCB's RulesElementField behavior.
                        fieldEntries.Add(new(fieldName, value));
                        if (!fields.ContainsKey(fieldName))
                            fields[fieldName] = value;
                    }
                    break;
                case "Prereqs":
                    prereqs = child.Value.Trim();
                    break;
                case "Category":
                    ParseCategories(child, categories);
                    break;
                case "rules":
                    ParseRulesBlock(child, rules);
                    break;
            }
        }

        // Capture mixed-content text (description body) that sits as direct XText
        // children of the RulesElement, e.g. body description after </rules>.
        var descBuilder = new System.Text.StringBuilder();
        foreach (var node in el.Nodes().OfType<System.Xml.Linq.XText>())
            descBuilder.Append(node.Value);
        string descText = NormalizeDescription(descBuilder.ToString());
        if (descText.Length > 0 && !fields.ContainsKey("Description"))
        {
            fields["Description"] = descText;
            fieldEntries.Add(new("Description", descText));
        }

        var element = new RulesElement
        {
            InternalId = internalId,
            Name = name,
            Type = type,
            Source = source,
            Prereqs = prereqs,
            Fields = fields,
            FieldEntries = fieldEntries,
            Rules = rules,
        };

        return new ParsedElement(element, categories);
    }

    private static void ParseCategories(XElement categoryEl, List<string> categories)
    {
        string content = categoryEl.Value.Trim();
        if (string.IsNullOrWhiteSpace(content)) return;
        foreach (var cat in content.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            categories.Add(cat);
    }

    private static void ParseRulesBlock(XElement rulesEl, List<RuleDirective> rules)
    {
        foreach (var child in rulesEl.Elements())
        {
            RuleDirective? directive = child.Name.LocalName switch
            {
                "statadd" => ParseStatAdd(child),
                "grant" => ParseGrant(child),
                "modify" => ParseModify(child),
                "select" => ParseSelect(child),
                "replace" => ParseReplace(child),
                "drop" => ParseDrop(child),
                "suggest" => ParseSuggest(child),
                "textstring" => ParseTextString(child),
                "statalias" => ParseStatAlias(child),
                _ => null,
            };
            if (directive is not null)
                rules.Add(directive);
        }
    }

    // ---- Individual directive parsers (match RulesXmlReader logic exactly) ----

    private static StatAddDirective? ParseStatAdd(XElement el)
    {
        string? name = Attr(el, "name");
        string? valueStr = Attr(el, "value");
        if (name is null || valueStr is null) return null;

        return new StatAddDirective
        {
            Name = name,
            Value = ValueExpression.Parse(valueStr),
            BonusType = Attr(el, "type"),
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
            Condition = Attr(el, "condition"),
            Wearing = Attr(el, "wearing"),
            NotWearing = Attr(el, "not-wearing"),
            Zero = ParseBool(Attr(el, "zero")),
            NonZero = ParseBool(Attr(el, "non-zero")),
            HalfPoint = ParseBool(Attr(el, "half-point")),
            StatMin = Attr(el, "statmin"),
        };
    }

    private static GrantDirective? ParseGrant(XElement el)
    {
        string? name = Attr(el, "name");
        string? type = Attr(el, "type");
        if (name is null || type is null) return null;

        return new GrantDirective
        {
            Name = name,
            ElementType = type,
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
        };
    }

    private static ModifyDirective? ParseModify(XElement el)
    {
        string? field = Attr(el, "Field") ?? Attr(el, "field");
        if (field is null) return null;

        return new ModifyDirective
        {
            Field = field,
            Name = Attr(el, "name"),
            ElementType = Attr(el, "type"),
            Value = Attr(el, "value"),
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
            ListAddition = Attr(el, "list-addition"),
            SelectSlot = Attr(el, "select"),
            Wearing = Attr(el, "wearing"),
            DieIncrease = ParseIntOrNull(Attr(el, "die-increase")),
        };
    }

    private static SelectDirective? ParseSelect(XElement el)
    {
        string? type = Attr(el, "type");
        if (type is null) return null;

        // The attribute is the stable slot IDENTIFIER (referenced by
        // modify/replace directives). The inner text is a separate UI
        // DISPLAY LABEL. Keep them distinct — never let inner text
        // overwrite a present attribute.
        string? innerText = null;
        if (!el.HasElements)
        {
            string trimmed = el.Value.Trim();
            if (trimmed.Length > 0) innerText = trimmed;
        }

        return new SelectDirective
        {
            ElementType = type,
            Number = ParseIntOrNull(Attr(el, "number")) ?? 1,
            Category = AttrCI(el, "Category", "category"),
            Name = Attr(el, "name") ?? innerText,
            DisplayLabel = innerText,
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
            Prepare = AttrCI(el, "Prepare", "prepare"),
            Spellbook = Attr(el, "spellbook"),
            Optional = ParseBool(Attr(el, "optional")),
            Existing = ParseBool(Attr(el, "existing")),
            Default = Attr(el, "default"),
            Grant = Attr(el, "grant"),
        };
    }

    private static ReplaceDirective ParseReplace(XElement el)
    {
        return new ReplaceDirective
        {
            Name = Attr(el, "name"),
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Multiclass = Attr(el, "multiclass"),
            PowerSwap = Attr(el, "powerswap"),
            PowerReplace = Attr(el, "power-replace"),
            Optional = ParseBool(Attr(el, "optional")),
            Requires = Attr(el, "requires"),
        };
    }

    private static DropDirective ParseDrop(XElement el)
    {
        return new DropDirective
        {
            SelectSlot = Attr(el, "select"),
            Name = Attr(el, "name"),
            ElementType = Attr(el, "type"),
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
        };
    }

    private static SuggestDirective? ParseSuggest(XElement el)
    {
        string? name = Attr(el, "name");
        string? type = Attr(el, "type");
        if (name is null || type is null) return null;

        return new SuggestDirective
        {
            Name = name,
            ElementType = type,
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
        };
    }

    private static TextStringDirective? ParseTextString(XElement el)
    {
        string? name = Attr(el, "name");
        string? value = Attr(el, "value");
        if (name is null || value is null) return null;

        return new TextStringDirective
        {
            Name = name,
            Value = value,
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
            Condition = Attr(el, "condition"),
        };
    }

    private static StatAliasDirective? ParseStatAlias(XElement el)
    {
        string? name = Attr(el, "name");
        string? alias = Attr(el, "alias");
        if (name is null || alias is null) return null;

        return new StatAliasDirective
        {
            Name = name,
            Alias = alias,
            Level = ParseIntOrNull(AttrCI(el, "Level", "level")),
            Requires = Attr(el, "requires"),
        };
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    private static string? AttrCI(XElement el, string upper, string lower) =>
        el.Attribute(upper)?.Value ?? el.Attribute(lower)?.Value;

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Result of merging .part files into the rules database.
/// </summary>
public sealed record MergeResult(
    int FilesProcessed,
    int ElementsAdded,
    int ElementsUpdated,
    int ElementsDeleted,
    int NodesAppended);

/// <summary>
/// A single part file to merge, with its stable id and category. Used by
/// <see cref="PartMerger.MergeFiles"/> so callers control the exact set and
/// order of parts applied (e.g. the layered rebuild service).
/// </summary>
public sealed record PartSourceFile(string Path, string PartId, string? Category);
