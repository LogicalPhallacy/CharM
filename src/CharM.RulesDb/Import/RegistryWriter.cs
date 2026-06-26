using CharM.RulesDb.Storage;
using Microsoft.Data.Sqlite;

namespace CharM.RulesDb.Import;

/// <summary>
/// Writes part_registry rows for parts that exist in the manifest but were not
/// merged (disabled). This keeps disabled parts visible in the registry so the
/// management UI can list and re-enable them. Enabled parts are registered by
/// <see cref="PartMerger"/> during merge; this fills in the rest.
/// </summary>
internal static class RegistryWriter
{
    public static void WriteDisabledParts(string dbPath, IEnumerable<PartManifestEntry> disabled)
    {
        var list = disabled.ToList();
        if (list.Count == 0) return;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        RulesDbSchema.Create(connection);

        using var tx = connection.BeginTransaction();
        foreach (var p in list)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO part_registry
                    (part_id, filename, category, display_name, version,
                     content_hash, source_url, enabled, layer_order, is_base, applied_at)
                VALUES ($id, $fn, $cat, $disp, $ver, $hash, $url, 0, $order, 0, NULL)
                ON CONFLICT(part_id) DO UPDATE SET
                    enabled = 0,
                    version = excluded.version,
                    content_hash = excluded.content_hash,
                    layer_order = excluded.layer_order
                """;
            cmd.Parameters.AddWithValue("$id", p.PartId);
            cmd.Parameters.AddWithValue("$fn", p.Filename);
            cmd.Parameters.AddWithValue("$cat", (object?)p.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$disp", (object?)p.Filename ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ver", (object?)p.Version ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)p.ContentHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)p.SourceUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", p.LayerOrder);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
