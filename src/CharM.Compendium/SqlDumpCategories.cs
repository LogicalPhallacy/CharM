namespace CharM.Compendium;

/// <summary>
/// Maps between the MySQL-dump table/file names (<c>ddi&lt;Table&gt;.sql</c>) and
/// the lowercase compendium category names used across the tooling. The dump's
/// table names are PascalCase (Power, ParagonPath, EpicDestiny); the compendium
/// categories are the lowercase, concatenated forms (power, paragonpath,
/// epicdestiny) that also prefix entry ids (power10, paragonpath12).
/// </summary>
public static class SqlDumpCategories
{
    /// <summary>Table name → compendium category. Only data tables are listed.</summary>
    public static readonly IReadOnlyDictionary<string, string> TableToCategory =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Power"] = "power",
            ["Feat"] = "feat",
            ["Item"] = "item",
            ["Race"] = "race",
            ["Ritual"] = "ritual",
            ["Background"] = "background",
            ["Class"] = "class",
            ["ParagonPath"] = "paragonpath",
            ["EpicDestiny"] = "epicdestiny",
            ["Theme"] = "theme",
            ["Deity"] = "deity",
            ["Companion"] = "companion",
            ["Disease"] = "disease",
            ["Poison"] = "poison",
            ["Glossary"] = "glossary",
            ["Monster"] = "monster",
            ["Trap"] = "trap",
            ["Associate"] = "associate",
            ["Skill"] = "skill",
            ["Terrain"] = "terrain",
        };

    /// <summary>The <c>ddi&lt;Table&gt;.sql</c> filename for a table.</summary>
    public static string FileName(string table) => "ddi" + table + ".sql";
}
