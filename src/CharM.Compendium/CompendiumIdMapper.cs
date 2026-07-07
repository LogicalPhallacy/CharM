using System.Text.RegularExpressions;

namespace CharM.Compendium;

/// <summary>
/// Maps rules-DB <c>internal-id</c>s to candidate compendium ids using the same
/// heuristic OCB used to emit compendium <c>url=</c> attributes:
/// <c>ID_&lt;NS&gt;_&lt;CATEGORY&gt;_&lt;num&gt;</c> → <c>&lt;category&gt;.aspx?id=&lt;num&gt;</c> →
/// compendium entry <c>&lt;category&gt;&lt;num&gt;</c>.
///
/// The heuristic only holds cleanly for the official <c>ID_FMP_*</c> namespace.
/// Part-file namespaces (<c>ID_TIV</c>, <c>ID_WOG</c>, <c>ID_HF</c>, …) carry a
/// part-local number that is not a compendium id, so a computed candidate must
/// be verified against the loaded listing (and preferably corroborated by a
/// name match) before it is trusted.
/// </summary>
public static partial class CompendiumIdMapper
{
    [GeneratedRegex(@"^ID_[A-Z0-9]+_(.+)_(\d+)$")]
    private static partial Regex InternalIdPattern();

    /// <summary>
    /// The category token embedded in an internal-id (e.g. <c>POWER</c>,
    /// <c>EPIC_DESTINY</c>, <c>MAGIC_ITEM</c>) → compendium category folder name.
    /// Tokens with no compendium analogue are intentionally absent.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryTokenToCategory =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["POWER"] = "power",
            ["FEAT"] = "feat",
            ["RITUAL"] = "ritual",
            ["DEITY"] = "deity",
            ["WEAPON"] = "weapon",
            ["THEME"] = "theme",
            ["ARMOR"] = "armor",
            ["CLASS"] = "class",
            ["HYBRID_CLASS"] = "class",
            ["PSEUDO_CLASS"] = "class",
            ["RACE"] = "race",
            ["COMPANION"] = "companion",
            ["BACKGROUND"] = "background",
            ["EPIC_DESTINY"] = "epicdestiny",
            ["PARAGON_PATH"] = "paragonpath",
            ["MAGIC_ITEM"] = "item",
            ["GEAR"] = "item",
            ["IMPLEMENT"] = "implement",
            ["MONSTER"] = "monster",
            ["TRAP"] = "trap",
            ["POISON"] = "poison",
            ["DISEASE"] = "disease",
            ["GLOSSARY"] = "glossary",
        };

    /// <summary>
    /// Rules-DB element <c>type</c> → compendium category, used to scope
    /// name-based matching for elements whose id doesn't map. Types with no
    /// compendium analogue (Internal, Category, Build, source, tag, Grants,
    /// CountsAs*, ability/skill scaffolding, …) are intentionally absent.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DbTypeToCategory =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Power"] = "power",
            ["Feat"] = "feat",
            ["Ritual"] = "ritual",
            ["Ritual Scroll"] = "ritual",
            ["Deity"] = "deity",
            ["Weapon"] = "weapon",
            ["Superior Implement"] = "implement",
            ["Theme"] = "theme",
            ["Armor"] = "armor",
            ["Class"] = "class",
            ["Hybrid Class"] = "class",
            ["Pseudo Class"] = "class",
            ["Race"] = "race",
            ["Companion"] = "companion",
            ["Background"] = "background",
            ["Epic Destiny"] = "epicdestiny",
            ["Paragon Path"] = "paragonpath",
            ["Magic Item"] = "item",
            ["Gear"] = "item",
            ["Item Set"] = "item",
        };

    /// <summary>
    /// Compute the candidate compendium id for an internal-id, or null if the
    /// id doesn't fit the pattern or the category token has no compendium
    /// mapping. The result is a <em>candidate</em> only — verify existence.
    /// </summary>
    public static string? TryMapToCompendiumId(string internalId) =>
        TryMap(internalId, out var category, out var number)
            ? category + number
            : null;

    /// <summary>
    /// Decompose an internal-id into its compendium category and numeric id.
    /// Returns false when the id doesn't match the pattern or the embedded
    /// category token isn't a known compendium category.
    /// </summary>
    public static bool TryMap(string internalId, out string category, out string number)
    {
        category = string.Empty;
        number = string.Empty;
        if (string.IsNullOrEmpty(internalId)) return false;

        var m = InternalIdPattern().Match(internalId);
        if (!m.Success) return false;

        var token = m.Groups[1].Value;
        if (!CategoryTokenToCategory.TryGetValue(token, out var cat))
            return false;

        category = cat;
        number = m.Groups[2].Value;
        return true;
    }

    /// <summary>Trailing numeric suffix of a compendium id ("power6872" → 6872), or -1.</summary>
    public static int NumericSuffix(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        int i = id.Length;
        while (i > 0 && char.IsDigit(id[i - 1])) i--;
        return i < id.Length && int.TryParse(id.AsSpan(i), out var n) ? n : -1;
    }

    /// <summary>The <c>ID_&lt;NS&gt;</c> namespace prefix of an internal-id (e.g. <c>ID_FMP</c>).</summary>
    public static string Namespace(string internalId)
    {
        if (string.IsNullOrEmpty(internalId)) return string.Empty;
        var parts = internalId.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[1] : internalId;
    }

    /// <summary>
    /// Compendium categories where an element's internal-id number is expected
    /// to equal its compendium id (the OCB convention). These are the "rules"
    /// categories. The "catalog" categories (item, weapon, armor, implement,
    /// companion) maintain an independent DB id-space that was never
    /// compendium-numbered, so a number "mismatch" there is by design, not an
    /// error — they are excluded so an id-mismatch report stays actionable.
    /// </summary>
    public static readonly IReadOnlySet<string> IdConventionCategories =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "power", "feat", "ritual", "deity", "theme", "class", "race",
            "epicdestiny", "paragonpath", "background",
        };

    public static bool IsIdConventionCategory(string? category) =>
        category is not null && IdConventionCategories.Contains(category);
}
