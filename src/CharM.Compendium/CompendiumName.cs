using System.Text;

namespace CharM.Compendium;

/// <summary>
/// Name normalization shared by listing indexing and DB→compendium matching.
/// Rules-DB element names and compendium names diverge in predictable ways
/// (trailing enchantment bonuses like "+3", parenthetical qualifiers like
/// "(Cursed)" or "(RPGA)", bracketed technique tags like "[Movement
/// Technique]", possessive apostrophe styles). Normalizing both sides the same
/// way lets us match part-file elements that have no id-level compendium link.
/// </summary>
public static class CompendiumName
{
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var s = name.Trim();

        // Drop trailing bracketed/parenthetical qualifiers, repeatedly:
        //   "Foo [Movement Technique]" -> "Foo"
        //   "Bar (Cursed)"             -> "Bar"
        bool changed = true;
        while (changed)
        {
            changed = false;
            s = s.TrimEnd();
            if (s.EndsWith(']') && s.LastIndexOf('[') is var lb and >= 0)
            {
                s = s[..lb];
                changed = true;
            }
            else if (s.EndsWith(')') && s.LastIndexOf('(') is var lp and >= 0)
            {
                s = s[..lp];
                changed = true;
            }
        }

        // Drop trailing magic-item enchantment bonus: "Flaming Weapon +3" -> "Flaming Weapon".
        s = s.TrimEnd();
        int plus = s.LastIndexOf('+');
        if (plus > 0 && plus < s.Length - 1)
        {
            var tail = s[(plus + 1)..].Trim();
            if (tail.Length > 0 && tail.All(char.IsDigit))
                s = s[..plus];
        }

        // Fold to a canonical form: lowercase, unify apostrophes/whitespace,
        // strip non-alphanumeric so punctuation styling can't block a match.
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastSpace = false;
            }
            else if (!lastSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Prefixes the rules DB sometimes prepends to an element name that the
    /// compendium omits (it categorizes them differently, e.g. a Channel
    /// Divinity feat power is listed as ClassName "Feat Power" named plainly).
    /// Matching tries the name both with and without these prefixes.
    /// </summary>
    public static readonly string[] StripablePrefixes = ["Channel Divinity: "];

    /// <summary>
    /// The distinct normalized forms to try when matching a DB name: the full
    /// normalized name first, then variants with a known prefix removed.
    /// </summary>
    public static IEnumerable<string> NormalizedVariants(string? name)
    {
        var primary = Normalize(name);
        if (primary.Length > 0) yield return primary;
        if (string.IsNullOrEmpty(name)) yield break;

        foreach (var prefix in StripablePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = Normalize(name[prefix.Length..]);
                if (stripped.Length > 0 && stripped != primary) yield return stripped;
            }
        }
    }

    /// <summary>
    /// True when two names refer to the same element, tolerating the stripable
    /// prefixes above (e.g. "Channel Divinity: X" agrees with "X").
    /// </summary>
    public static bool Matches(string? dbName, string? compendiumName)
    {
        var target = Normalize(compendiumName);
        if (target.Length == 0) return false;
        foreach (var variant in NormalizedVariants(dbName))
            if (variant == target) return true;
        return false;
    }
}
