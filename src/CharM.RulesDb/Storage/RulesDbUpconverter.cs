using Microsoft.Data.Sqlite;

namespace CharM.RulesDb.Storage;

/// <summary>
/// Non-destructive, idempotent migration of a rules database to the current
/// metadata schema. Legacy databases (built before the part_registry /
/// part_provenance / db_meta tables existed) are brought forward in place:
/// the metadata tables are created and, when the DB clearly already contains
/// content but has no registered layers, a single synthetic "base" layer is
/// backfilled so the audit / toggle features have a starting point.
///
/// This never rewrites or deletes content rows. Provenance for an upconverted
/// DB is unknown until it is next rebuilt through the layered pipeline, which
/// is recorded via <c>db_meta.provenance_known = false</c>.
/// </summary>
public static class RulesDbUpconverter
{
    /// <summary>
    /// Ensure the database at <paramref name="dbPath"/> has the current
    /// metadata schema. Opens the file read-write. Returns true when an
    /// upconvert backfill was performed (i.e. the DB was a legacy one).
    /// </summary>
    public static bool Upconvert(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is required.", nameof(dbPath));
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("Rules database not found.", dbPath);

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
        connection.Open();

        return Upconvert(connection);
    }

    /// <summary>
    /// Ensure metadata schema on an already-open read-write connection.
    /// </summary>
    public static bool Upconvert(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        bool hadRegistry = TableHasRows(connection, "part_registry");

        // Idempotent: creates missing metadata tables, seeds schema_version.
        RulesDbSchema.Create(connection);

        // Already migrated (registry populated) — nothing to backfill.
        if (hadRegistry)
            return false;

        // Empty content DB (freshly created, about to be imported) — let the
        // import pipeline register layers; don't synthesize a base layer.
        if (!TableHasRows(connection, "rules_elements"))
            return false;

        BackfillSyntheticBaseLayer(connection);
        return true;
    }

    private static void BackfillSyntheticBaseLayer(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO part_registry
                    (part_id, filename, category, display_name, version,
                     content_hash, source_url, enabled, layer_order, is_base, applied_at)
                VALUES
                    ('base', 'base', 'base', 'Imported base (provenance unknown)', NULL,
                     NULL, NULL, 1, 0, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        RulesDbSchema.SetMeta(connection, "provenance_known", "false");
        RulesDbSchema.SetMetaIfAbsent(
            connection, "upconverted_at", DateTimeOffset.UtcNow.ToString("o"));

        tx.Commit();
    }

    private static bool TableHasRows(SqliteConnection connection, string table)
    {
        if (!TableExists(connection, table))
            return false;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM \"{table}\" LIMIT 1";
        return cmd.ExecuteScalar() is not null;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }
}
