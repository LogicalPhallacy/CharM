namespace CharM.Compendium;

/// <summary>
/// A source of offline-compendium rules data. Abstracts over the concrete
/// storage format so the CLI audit tooling and the matcher can run against
/// either the JSONP dump (<see cref="CompendiumReader"/>) or the MySQL-dump
/// export (<see cref="SqlDumpCompendiumSource"/>) interchangeably.
///
/// Categories and ids are the common denominator across formats: a category is
/// a lowercase name (power, feat, item, race, …) and an entry id is
/// <c>&lt;category&gt;&lt;num&gt;</c> (e.g. "power10"), matching the compendium's own id
/// scheme in both dumps.
/// </summary>
public interface ICompendiumSource
{
    /// <summary>The root directory the source was opened from (for diagnostics).</summary>
    string RootDirectory { get; }

    /// <summary>True if the source contains the given compendium category.</summary>
    bool HasCategory(string category);

    /// <summary>Categories available in this source.</summary>
    IReadOnlyList<string> Categories { get; }

    /// <summary>Category → entry-count catalog.</summary>
    CompendiumCatalog Catalog { get; }

    /// <summary>Parsed listing (id/name/fields) for a category, cached.</summary>
    CompendiumListing GetListing(string category);

    /// <summary>Global normalized-name → id(s) index across categories.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> GlobalNameIndex { get; }

    /// <summary>The HTML body for an entry, or null if absent.</summary>
    string? GetBody(string category, string id);

    /// <summary>The plain-text (HTML-stripped) content for an entry, or null if absent.</summary>
    string? GetIndexText(string category, string id);
}
