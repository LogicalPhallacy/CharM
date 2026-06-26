using System.Text.Json;
using CharM.RulesDb.Storage;
using Microsoft.Data.Sqlite;

namespace CharM.RulesDb.Import;

/// <summary>
/// Import pipeline: D20Rules XML → SQLite database.
/// </summary>
public static class RulesDbBuilder
{
    private const int BatchSize = 1000;

    /// <summary>
    /// Import a D20Rules XML file into a new SQLite database.
    /// </summary>
    public static void Import(string xmlPath, string dbPath, IProgress<int>? progress = null)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Pragmas for bulk-insert performance
        Execute(connection, "PRAGMA journal_mode = WAL");
        Execute(connection, "PRAGMA synchronous = NORMAL");

        RulesDbSchema.Create(connection);

        int count = 0;
        SqliteTransaction? tx = null;
        SqliteCommand? insertElement = null;
        SqliteCommand? insertCategory = null;

        try
        {
            tx = connection.BeginTransaction();
            insertElement = CreateInsertElementCommand(connection, tx);
            insertCategory = CreateInsertCategoryCommand(connection, tx);

            foreach (var parsed in RulesXmlReader.ReadAll(xmlPath))
            {
                InsertElement(insertElement, insertCategory, parsed);
                count++;

                if (count % BatchSize == 0)
                {
                    tx.Commit();
                    progress?.Report(count);
                    tx.Dispose();
                    insertElement.Dispose();
                    insertCategory.Dispose();

                    tx = connection.BeginTransaction();
                    insertElement = CreateInsertElementCommand(connection, tx);
                    insertCategory = CreateInsertCategoryCommand(connection, tx);
                }
            }

            // Commit remaining rows
            tx.Commit();
            progress?.Report(count);

            RegisterBaseLayer(connection, xmlPath, count);
        }
        finally
        {
            insertElement?.Dispose();
            insertCategory?.Dispose();
            tx?.Dispose();
        }
    }

    private static SqliteCommand CreateInsertElementCommand(SqliteConnection connection, SqliteTransaction tx)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO rules_elements (internal_id, name, type, source, prereqs, fields_json, rules_json)
            VALUES ($id, $name, $type, $source, $prereqs, $fields, $rules)
            """;
        cmd.Parameters.Add(new SqliteParameter("$id", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$name", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$type", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$source", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$prereqs", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$fields", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$rules", SqliteType.Text));
        return cmd;
    }

    private static SqliteCommand CreateInsertCategoryCommand(SqliteConnection connection, SqliteTransaction tx)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO element_categories (internal_id, category)
            VALUES ($id, $cat)
            """;
        cmd.Parameters.Add(new SqliteParameter("$id", SqliteType.Text));
        cmd.Parameters.Add(new SqliteParameter("$cat", SqliteType.Text));
        return cmd;
    }

    private static void InsertElement(SqliteCommand insertElement, SqliteCommand insertCategory, ParsedElement parsed)
    {
        var element = parsed.Element;
        var jsonOptions = RulesDatabase.SharedJsonOptions;

        // Serialize the ordered list-of-pairs view so duplicates (e.g. two
        // <specific name="Hit"> children for primary/secondary attack) round-
        // trip through the DB. Fall back to Fields if FieldEntries wasn't
        // populated by the caller. Reader accepts both legacy object format
        // and the array-of-pairs format.
        IReadOnlyList<KeyValuePair<string, string>> entries = element.FieldEntries.Count > 0
            ? element.FieldEntries
            : element.Fields.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();
        string? fieldsJson = entries.Count > 0
            ? JsonSerializer.Serialize(entries)
            : null;

        string? rulesJson = element.Rules.Count > 0
            ? JsonSerializer.Serialize(element.Rules, jsonOptions)
            : null;

        insertElement.Parameters["$id"].Value = element.InternalId;
        insertElement.Parameters["$name"].Value = element.Name;
        insertElement.Parameters["$type"].Value = element.Type;
        insertElement.Parameters["$source"].Value = (object?)element.Source ?? DBNull.Value;
        insertElement.Parameters["$prereqs"].Value = (object?)element.Prereqs ?? DBNull.Value;
        insertElement.Parameters["$fields"].Value = (object?)fieldsJson ?? DBNull.Value;
        insertElement.Parameters["$rules"].Value = (object?)rulesJson ?? DBNull.Value;
        insertElement.ExecuteNonQuery();

        foreach (var category in parsed.Categories)
        {
            insertCategory.Parameters["$id"].Value = element.InternalId;
            insertCategory.Parameters["$cat"].Value = category;
            insertCategory.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Record the base (WotC compendium XML) as layer 0 in part_registry, and
    /// stamp db_meta with build provenance. Provenance for individual base
    /// elements is implicit (everything present after this point with no part
    /// provenance row came from the base).
    /// </summary>
    private static void RegisterBaseLayer(SqliteConnection connection, string xmlPath, int elementCount)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO part_registry
                (part_id, filename, category, display_name, version,
                 content_hash, source_url, enabled, layer_order, is_base, applied_at)
            VALUES ('base', $fn, 'base', 'WotC compendium base', NULL,
                    NULL, NULL, 1, 0, 1, $now)
            ON CONFLICT(part_id) DO UPDATE SET
                filename = excluded.filename,
                applied_at = excluded.applied_at
            """;
        cmd.Parameters.AddWithValue("$fn", Path.GetFileName(xmlPath));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        RulesDbSchema.SetMeta(connection, "built_at", DateTimeOffset.UtcNow.ToString("o"));
        RulesDbSchema.SetMeta(connection, "base_element_count", elementCount.ToString());
        RulesDbSchema.SetMeta(connection, "provenance_known", "true");
    }
}
