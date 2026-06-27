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
}
