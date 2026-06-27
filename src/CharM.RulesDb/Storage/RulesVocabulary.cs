using CharM.Engine.Creation;

namespace CharM.RulesDb.Storage;

/// <summary>
/// Game vocabulary (skills and their key abilities, ...) derived ONCE from the
/// loaded rules database and cached, so the UI tracks the actual loaded ruleset
/// (including part/homebrew overlays) instead of hardcoded lists that silently
/// drift. Build via <see cref="Build"/> and cache the result; rebuild whenever
/// the underlying database is swapped or rebuilt.
///
/// <para>
/// Only data that is a faithful projection of the rules DB lives here. Pure
/// presentation concerns (display ordering of equipment slots, alignment
/// dropdown order, CSS classes) stay in the UI.
/// </para>
/// </summary>
public sealed class RulesVocabulary
{
    /// <summary>The skill names, alphabetical (e.g. "Acrobatics" ... "Thievery").</summary>
    public IReadOnlyList<string> SkillNames { get; }

    /// <summary>Case-insensitive membership set of <see cref="SkillNames"/>.</summary>
    public IReadOnlySet<string> SkillNameSet { get; }

    /// <summary>Skill name → its key ability full name (e.g. "Athletics" → "Strength").</summary>
    public IReadOnlyDictionary<string, string> SkillKeyAbility { get; }

    /// <summary>
    /// Ability full name → the skills keyed off that ability, alphabetical
    /// (e.g. "Dexterity" → ["Acrobatics", "Stealth", "Thievery"]). Every
    /// standard ability is present (empty list when it governs no skills).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SkillsByAbility { get; }

    /// <summary>
    /// Alignment names in canonical good→evil display order
    /// (Lawful Good, Good, Unaligned, Evil, Chaotic Evil), with any unrecognized
    /// (homebrew) alignments appended alphabetically.
    /// </summary>
    public IReadOnlyList<string> Alignments { get; }

    private RulesVocabulary(
        IReadOnlyList<string> skillNames,
        IReadOnlyDictionary<string, string> skillKeyAbility,
        IReadOnlyDictionary<string, IReadOnlyList<string>> skillsByAbility,
        IReadOnlyList<string> alignments)
    {
        SkillNames = skillNames;
        SkillNameSet = new HashSet<string>(skillNames, StringComparer.OrdinalIgnoreCase);
        SkillKeyAbility = skillKeyAbility;
        SkillsByAbility = skillsByAbility;
        Alignments = alignments;
    }

    /// <summary>Canonical display order for the standard alignments (good → evil).</summary>
    private static readonly string[] AlignmentOrder =
        ["Lawful Good", "Good", "Unaligned", "Evil", "Chaotic Evil"];

    /// <summary>
    /// Project the skill vocabulary from the rules database. Skills are the
    /// <c>Skill</c>-type elements; each carries a <c>Key Ability</c> field.
    /// </summary>
    public static RulesVocabulary Build(IRulesDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var skills = database.FindByType("Skill")
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => e.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keyAbility = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in database.FindByType("Skill"))
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
                continue;
            if (skill.Fields.TryGetValue("Key Ability", out var ability)
                && !string.IsNullOrWhiteSpace(ability))
            {
                // Normalize to the canonical full ability name when recognized.
                keyAbility[skill.Name.Trim()] = AbilityNames.Normalize(ability.Trim());
            }
        }

        // Group skills under each standard ability (alphabetical within), keyed
        // by full ability name. Always include every standard ability.
        var byAbility = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ability in AbilityNames.StandardOrder)
        {
            var full = AbilityNames.GetFullName(ability);
            var owned = skills
                .Where(s => keyAbility.TryGetValue(s, out var a)
                    && string.Equals(a, full, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            byAbility[full] = owned;
        }

        // Alignments: project from the DB, ordered by the canonical good→evil
        // sequence with any unrecognized (homebrew) alignments appended A-Z.
        var alignmentSet = database.FindByType("Alignment")
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => e.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var alignments = AlignmentOrder
            .Where(a => alignmentSet.Contains(a, StringComparer.OrdinalIgnoreCase))
            .Concat(alignmentSet
                .Where(a => !AlignmentOrder.Contains(a, StringComparer.OrdinalIgnoreCase))
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new RulesVocabulary(skills, keyAbility, byAbility, alignments);
    }
}
