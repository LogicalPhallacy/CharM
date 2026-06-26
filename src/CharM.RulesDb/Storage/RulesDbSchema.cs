using Microsoft.Data.Sqlite;

namespace CharM.RulesDb.Storage;

/// <summary>
/// Creates the SQLite schema for the rules database.
/// </summary>
public static class RulesDbSchema
{
    /// <summary>
    /// Current metadata schema version. Bumped when the metadata tables
    /// (db_meta / part_registry / part_provenance) change shape. The content
    /// tables (rules_elements / element_categories) are unaffected; opening an
    /// older DB triggers a non-destructive upconvert (see RulesDbUpconverter).
    /// </summary>
    public const int MetadataSchemaVersion = 1;

    public static void Create(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS rules_elements (
                internal_id TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                type        TEXT NOT NULL,
                source      TEXT,
                prereqs     TEXT,
                fields_json TEXT,
                rules_json  TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_re_name_type ON rules_elements(name, type);
            CREATE INDEX IF NOT EXISTS idx_re_type ON rules_elements(type);
            CREATE INDEX IF NOT EXISTS idx_re_source ON rules_elements(source);
            CREATE INDEX IF NOT EXISTS idx_re_type_source ON rules_elements(type, source);

            CREATE TABLE IF NOT EXISTS element_categories (
                internal_id TEXT NOT NULL,
                category    TEXT NOT NULL,
                PRIMARY KEY (internal_id, category)
            );

            CREATE INDEX IF NOT EXISTS idx_ec_category ON element_categories(category);

            -- ============================================================
            -- Metadata tables (NOT on the read path). These track how the DB
            -- was assembled (base + toggleable part-file overlays), part
            -- versions, and per-element provenance. Query-time element lookups
            -- never touch these — the working rules_elements table stays flat.
            -- ============================================================

            -- Key/value bag: schema_version, base_content_hash, built_at, etc.
            CREATE TABLE IF NOT EXISTS db_meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            );

            -- One row per layer that contributed to the DB: the base snapshot
            -- plus every part file (sorted / UnearthedArcana / Homebrew /
            -- 3rdParty). layer_order is the merge precedence; enabled drives
            -- toggling; is_base marks the cached WotC+sorted base snapshot.
            CREATE TABLE IF NOT EXISTS part_registry (
                part_id      TEXT PRIMARY KEY,   -- stable id, e.g. "sorted/06-races.part"
                filename     TEXT NOT NULL,
                category     TEXT,               -- sorted | UnearthedArcana | Homebrew | 3rdParty | base
                display_name TEXT,
                version      TEXT,               -- <UpdateInfo><Version>
                content_hash TEXT,              -- sha256 of the part bytes
                source_url   TEXT,              -- where it was fetched from
                enabled      INTEGER NOT NULL DEFAULT 1,
                layer_order  INTEGER NOT NULL DEFAULT 0,
                is_base      INTEGER NOT NULL DEFAULT 0,
                applied_at   TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_part_registry_category ON part_registry(category);
            CREATE INDEX IF NOT EXISTS idx_part_registry_enabled ON part_registry(enabled);

            -- Which part last performed which operation on which element. Used
            -- for the toggle/rebuild affected-set, the audit feature, and
            -- "where did this element come from?" display.
            CREATE TABLE IF NOT EXISTS part_provenance (
                internal_id TEXT NOT NULL,
                part_id     TEXT NOT NULL,
                op          TEXT NOT NULL,       -- create | overwrite | append | delete
                PRIMARY KEY (internal_id, part_id)
            );

            CREATE INDEX IF NOT EXISTS idx_prov_part ON part_provenance(part_id);
            """;
        cmd.ExecuteNonQuery();

        SetMetaIfAbsent(connection, "schema_version", MetadataSchemaVersion.ToString());
    }

    /// <summary>Read a value from db_meta, or null if absent.</summary>
    public static string? GetMeta(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM db_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }

    /// <summary>Insert-or-replace a db_meta key.</summary>
    public static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO db_meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Set a db_meta key only when it is not already present.</summary>
    public static void SetMetaIfAbsent(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO db_meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
