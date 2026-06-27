using System.Text.RegularExpressions;

namespace CharM.RulesDb.Import;

/// <summary>
/// Small parsing helpers shared by the two D20Rules XML importers
/// (<see cref="RulesXmlReader"/> using a streaming <c>XmlReader</c> and
/// <see cref="PartMerger"/> using <c>XElement</c>). Keeping a single copy
/// prevents the two from drifting on boolean/int parsing or description
/// whitespace normalization, which would silently change imported content.
/// </summary>
internal static partial class RulesXmlImportHelpers
{
    public static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static int? ParseIntOrNull(string? value) =>
        int.TryParse(value, out int result) ? result : null;

    /// <summary>
    /// Collapse whitespace runs (including the leading tab/newline indentation
    /// present in CB XML) and split paragraph-like breaks into single newlines
    /// so rendering stays predictable.
    /// </summary>
    public static string NormalizeDescription(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var lines = raw.Replace('\t', ' ')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => WhitespaceRegex().Replace(l, " ").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
