namespace CharM.RulesDb.Import;

/// <summary>
/// Canonical part/layer category names, shared so the merge pipeline, the
/// remote sources, the layer store, and the legality classifier can't drift on
/// the spelling/casing of "sorted" / "UnearthedArcana" / etc. These mirror the
/// cbparts repository folder layout.
/// </summary>
public static class RulePartCategories
{
    public const string Base = "base";
    public const string Sorted = "sorted";
    public const string UnearthedArcana = "UnearthedArcana";
    public const string Homebrew = "Homebrew";
    public const string ThirdParty = "3rdParty";

    /// <summary>The cbparts content folders (excluding the synthetic <c>base</c>).</summary>
    public static readonly string[] ContentFolders = [Sorted, UnearthedArcana, Homebrew, ThirdParty];

    /// <summary>
    /// Categories folded into the cached base checkpoint ("heavy/stable" layers).
    /// </summary>
    public static readonly string[] HeavyCategories = [Base, Sorted];

    /// <summary>
    /// The value of a <c>&lt;Description category="..."&gt;</c> attribute (in a
    /// CBLoader <c>.index</c> file) that marks a content pack as "official"
    /// (WotC / Unearthed Arcana in the cbparts layout). Official packs are
    /// treated as heavy layers and are enabled by default.
    /// </summary>
    public const string OfficialMarker = "Official";

    /// <summary>
    /// Derive a human-friendly category name from a CBLoader index file name.
    /// Strips the <c>.index</c> extension, a leading <c>NN-</c> ordering prefix,
    /// and a trailing version integer, so <c>76-LibrisMortis1.index</c> becomes
    /// <c>LibrisMortis</c>, <c>Buck-A-Batch1.index</c> becomes <c>Buck-A-Batch</c>,
    /// and <c>WotC.index</c> stays <c>WotC</c>. Returns null for blank input.
    /// </summary>
    public static string? CategoryFromIndexName(string? indexFilename)
    {
        if (string.IsNullOrWhiteSpace(indexFilename)) return null;

        string name = indexFilename.Trim();

        // Strip the .index extension (case-insensitive).
        if (name.EndsWith(".index", StringComparison.OrdinalIgnoreCase))
            name = name[..^".index".Length];

        // Strip a leading numeric ordering prefix like "76-" or "98-".
        int dash = name.IndexOf('-');
        if (dash > 0 && name[..dash].All(char.IsDigit))
            name = name[(dash + 1)..];

        // Strip a single trailing version integer (the cbparts index sequence
        // number, e.g. the "1" in "LibrisMortis1"). Only when the name doesn't
        // end in a digit that is part of a hyphen/paren group we still want.
        int end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        // Keep at least one non-digit char; don't reduce to empty.
        if (end > 0 && end < name.Length)
            name = name[..end];

        name = name.Trim();
        return name.Length == 0 ? indexFilename.Trim() : name;
    }
}
