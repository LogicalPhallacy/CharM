namespace CharM.Compendium;

/// <summary>
/// Opens an <see cref="ICompendiumSource"/> from a directory, auto-detecting the
/// dump format so the CLI's <c>--compendium</c> flag accepts either the JSONP
/// dump (the <c>4e_database_files</c> folder) or the MySQL-dump export (a
/// directory containing, or containing a <c>sql/</c> subfolder of,
/// <c>ddi*.sql</c> files).
/// </summary>
public static class CompendiumSourceFactory
{
    public enum Format { Jsonp, SqlDump, Sqlite }

    /// <summary>
    /// Open a source from <paramref name="dir"/>, resolving the format. Accepts:
    /// a JSONP <c>4e_database_files</c> dir (or a parent containing it); a SQL
    /// dump <c>sql/</c> dir; a per-category SQLite data dir (or a parent with a
    /// <c>resources/data</c> or <c>data</c> subfolder); or the offline-compendium
    /// root that contains a <c>sql/</c> subfolder.
    /// </summary>
    public static ICompendiumSource Open(string dir, out Format format)
    {
        if (dir is null) throw new ArgumentNullException(nameof(dir));
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Compendium directory not found: {dir}");

        // JSONP: this dir is (or contains) the 4e_database_files folder.
        var jsonpDir = ResolveJsonpDir(dir);
        if (jsonpDir is not null)
        {
            format = Format.Jsonp;
            return new CompendiumReader(jsonpDir);
        }

        // SQLite: this dir is (or contains) a folder of per-category *.db files.
        var sqliteDir = ResolveSqliteDir(dir);
        if (sqliteDir is not null)
        {
            format = Format.Sqlite;
            return new SqliteCompendiumSource(sqliteDir);
        }

        // SQL dump: this dir is (or contains) a folder with ddi*.sql files.
        var sqlDir = ResolveSqlDir(dir);
        if (sqlDir is not null)
        {
            format = Format.SqlDump;
            return new SqlDumpCompendiumSource(sqlDir);
        }

        throw new InvalidDataException(
            $"Could not detect a compendium format in '{dir}'. Expected a JSONP " +
            "'4e_database_files' folder (catalog.js), a per-category SQLite data folder " +
            "(power.db/glossary.db), or a SQL-dump folder with ddi*.sql files.");
    }

    private static string? ResolveJsonpDir(string dir)
    {
        if (File.Exists(Path.Combine(dir, "catalog.js"))) return dir;
        var nested = Path.Combine(dir, "4e_database_files");
        if (File.Exists(Path.Combine(nested, "catalog.js"))) return nested;
        return null;
    }

    private static string? ResolveSqlDir(string dir)
    {
        if (HasDdiFiles(dir)) return dir;
        var nested = Path.Combine(dir, "sql");
        if (HasDdiFiles(nested)) return nested;
        return null;
    }

    private static bool HasDdiFiles(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "ddi*.sql").Any();

    private static string? ResolveSqliteDir(string dir)
    {
        foreach (var candidate in new[] { dir, Path.Combine(dir, "resources", "data"), Path.Combine(dir, "data") })
            if (HasSqliteCategoryDbs(candidate)) return candidate;
        return null;
    }

    // A per-category SQLite data dir has category *.db files that are real
    // SQLite databases (distinguishes it from offlinecompendium/data, which
    // holds a single Java-serialized compendium.db).
    private static bool HasSqliteCategoryDbs(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var probe in new[] { "glossary.db", "power.db", "feat.db" })
        {
            var path = Path.Combine(dir, probe);
            if (File.Exists(path) && SqliteCompendiumSource.IsSqliteFile(path)) return true;
        }
        return false;
    }
}
