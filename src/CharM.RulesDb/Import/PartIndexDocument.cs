using System.Xml.Linq;

namespace CharM.RulesDb.Import;

/// <summary>
/// A parsed CBLoader <c>.index</c> file (<c>&lt;PartIndex&gt;</c>). Beyond the
/// <c>(Filename, PartAddress)</c> pairs that <see cref="PartIndexReader"/>
/// exposes, this captures the metadata needed to categorize the parts it groups:
/// the index's own name, its <c>&lt;Description&gt;</c> <c>category</c> marker
/// (used to identify "official" content), and its <c>&lt;Obsolete&gt;</c> list.
/// </summary>
public sealed record PartIndexDocument
{
    /// <summary>The index's own file name from <c>&lt;UpdateInfo&gt;&lt;Filename&gt;</c> (e.g. <c>76-LibrisMortis1.index</c>).</summary>
    public required string IndexName { get; init; }

    /// <summary>Cleaned, human-friendly category derived from <see cref="IndexName"/> (e.g. <c>LibrisMortis</c>).</summary>
    public required string Category { get; init; }

    /// <summary>The <c>category</c> attribute on <c>&lt;Description&gt;</c>, or null.</summary>
    public string? DescriptionCategory { get; init; }

    /// <summary>The <c>&lt;Description&gt;</c> text, or null.</summary>
    public string? Description { get; init; }

    /// <summary>True when the index is marked official (<c>&lt;Description category="Official"&gt;</c>).</summary>
    public bool IsOfficial =>
        string.Equals(DescriptionCategory, RulePartCategories.OfficialMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>&lt;Part&gt;</c> entries this index groups.</summary>
    public IReadOnlyList<(string Filename, string Address)> Parts { get; init; } = [];

    /// <summary>Part filenames marked <c>&lt;Obsolete&gt;</c> by this index.</summary>
    public IReadOnlyCollection<string> ObsoleteFilenames { get; init; } = [];

    /// <summary>Parse a <c>.index</c> document. Tolerant of missing metadata.</summary>
    public static PartIndexDocument Parse(XDocument doc, string fallbackName)
    {
        var updateInfo = doc.Descendants("UpdateInfo").FirstOrDefault();
        string indexName = updateInfo?.Element("Filename")?.Value?.Trim() is { Length: > 0 } n
            ? n
            : fallbackName;

        var description = updateInfo?.Element("Description");
        string? descCategory = description?.Attribute("category")?.Value?.Trim();

        var obsolete = doc.Descendants("Obsolete")
            .Select(o => o.Element("Filename")?.Value?.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new PartIndexDocument
        {
            IndexName = indexName,
            Category = RulePartCategories.CategoryFromIndexName(indexName) ?? indexName,
            DescriptionCategory = string.IsNullOrWhiteSpace(descCategory) ? null : descCategory,
            Description = string.IsNullOrWhiteSpace(description?.Value) ? null : description!.Value.Trim(),
            Parts = PartIndexReader.ReadPartEntries(doc),
            ObsoleteFilenames = obsolete,
        };
    }
}
