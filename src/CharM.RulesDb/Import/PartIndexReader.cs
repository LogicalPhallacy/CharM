using System.Xml.Linq;

namespace CharM.RulesDb.Import;

/// <summary>
/// Parses CBLoader index XML (<c>WotC.index</c> / <c>index.xml</c>) into its
/// <c>&lt;Part&gt;</c> entries. Shared by <see cref="PartMerger"/>'s
/// download-from-index path and <see cref="CbloaderHostPartSource"/>'s listing
/// path so the two can't drift on the element/attribute names.
/// </summary>
public static class PartIndexReader
{
    /// <summary>Read the (Filename, PartAddress) pairs from an index document.</summary>
    public static IReadOnlyList<(string Filename, string Address)> ReadPartEntries(XDocument doc)
    {
        var result = new List<(string, string)>();
        foreach (var part in doc.Descendants("Part"))
        {
            string? filename = part.Element("Filename")?.Value?.Trim();
            string? address = part.Element("PartAddress")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(filename) && !string.IsNullOrWhiteSpace(address))
                result.Add((filename, address));
        }
        return result;
    }
}
