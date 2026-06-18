using CharM.Engine.Creation;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;

namespace CharM.Web.Services;

/// <summary>
/// Buckets a character's feats (and racial traits) into display groups for
/// the sheet. The grouping is loosely inspired by OCB's hierarchy but
/// deliberately simpler: only three buckets, in a fixed display order.
///
/// <list type="number">
///   <item><term>Class Feats</term> — feats whose prerequisites reference
///     one of the character's <c>Class</c> / <c>CountsAsClass</c> /
///     <c>Power Source</c> / <c>Role</c> elements (by name OR by
///     internal-id), or whose <c>any X class</c> gate matches one of
///     those identifiers. Sorted to the top because they're the most
///     thematically tied to the build.</item>
///   <item><term>General feats &amp; racial traits</term> — everything
///     else, including racial traits, which display in their original
///     selection order.</item>
///   <item><term>Weapon &amp; Implement Mastery</term> — Expertise feats,
///     weapon Mastery feats, and weapon/implement Specialization variants.
///     Pushed to the bottom because they're effectively passive
///     attack/damage bonuses with no roleplay flavor.</item>
/// </list>
///
/// <para>Detection runs the parsed prereq tree against the character's
/// granted rules elements, never against free-text fields. The four
/// element types listed above are the canonical class-identity
/// elements: every class auto-grants its <c>Class</c> /
/// <c>CountsAsClass</c> / <c>Power Source</c> / <c>Role</c> set, so
/// any prereq that gates on class membership will reference one of
/// them either by name or by internal-id.</para>
/// </summary>
public static class FeatGrouper
{
    public sealed record FeatBucket(string Label, IReadOnlyList<RulesElement> Items);

    // The four canonical element types that constitute class identity.
    // CountsAsClass is the primary signal (every class auto-grants its
    // own CAC plus any inherited parent-class CACs, e.g. Sentinel grants
    // both Sentinel and Druid). Class / Power Source / Role round out
    // the cases where a feat's prereq names one of those instead.
    private static readonly string[] _classIdentityTypes =
    [
        "Class",
        "CountsAsClass",
        "Power Source",
        "Role",
    ];

    public static IReadOnlyList<FeatBucket> Group(
        CharacterSession session,
        IReadOnlyList<RulesElement> feats,
        IReadOnlyList<RulesElement> racialTraits)
    {
        var classIdentity = CollectClassIdentity(session);

        var classFeats = new List<RulesElement>();
        var expertiseFeats = new List<RulesElement>();
        var generalFeats = new List<RulesElement>();

        foreach (var feat in feats)
        {
            if (IsExpertiseOrMasteryFeat(feat))
                expertiseFeats.Add(feat);
            else if (IsClassFeat(feat, classIdentity))
                classFeats.Add(feat);
            else
                generalFeats.Add(feat);
        }

        // Racial traits always land in the general bucket; their thematic
        // home is the racial subsection but the existing sheet renders
        // them in the same card as general feats, so preserve that.
        var generalBucket = new List<RulesElement>(generalFeats);
        generalBucket.AddRange(racialTraits);

        var buckets = new List<FeatBucket>();
        if (classFeats.Count > 0)
            buckets.Add(new FeatBucket("Class Feats", classFeats));
        if (generalBucket.Count > 0)
            buckets.Add(new FeatBucket("Feats & Traits", generalBucket));
        if (expertiseFeats.Count > 0)
            buckets.Add(new FeatBucket("Weapon & Implement Mastery", expertiseFeats));

        return buckets;
    }

    /// <summary>
    /// Collect every name and internal-id from the character's
    /// class-identity elements (see <see cref="_classIdentityTypes"/>).
    /// Match is by exact name OR by internal-id — no substring or
    /// free-text parsing.
    /// </summary>
    private static HashSet<string> CollectClassIdentity(CharacterSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in _classIdentityTypes)
        {
            foreach (var element in session.GetAllElementsOfType(type))
            {
                if (!string.IsNullOrWhiteSpace(element.Name)) ids.Add(element.Name);
                if (!string.IsNullOrWhiteSpace(element.InternalId)) ids.Add(element.InternalId);
            }
        }
        return ids;
    }

    private static bool IsClassFeat(RulesElement feat, HashSet<string> classIdentity)
    {
        if (classIdentity.Count == 0) return false;
        var node = PrereqParser.Parse(feat.Prereqs);
        if (node is null) return false;
        return ContainsClassGate(node, classIdentity);
    }

    /// <summary>
    /// Walk the parsed prereq tree looking for any node that gates on
    /// class identity. <see cref="PrereqNode.HasElement"/> matches by
    /// name or internal-id against the character's identity set;
    /// <see cref="PrereqNode.AnyClassCheck"/> ("any primal class") is
    /// resolved by checking whether its keyword matches one of the
    /// character's Power Source / Role / Class identifiers.
    /// </summary>
    private static bool ContainsClassGate(PrereqNode node, HashSet<string> classIdentity)
    {
        switch (node)
        {
            case PrereqNode.HasElement has:
                // Negated has-element gates (e.g. "not Arena Fighter")
                // are restriction clauses, not class gates — skip them.
                if (has.Negate) return false;
                return classIdentity.Contains(has.Name);

            case PrereqNode.AnyClassCheck any:
                // "any martial class" → keyword "martial"; matches if
                // the character has a Role / Power Source / Class
                // element with that name. The identity set is already
                // built from those types so a direct lookup is enough.
                return classIdentity.Contains(any.Keyword);

            case PrereqNode.Compound compound:
                return ContainsClassGate(compound.Left, classIdentity)
                    || ContainsClassGate(compound.Right, classIdentity);

            default:
                return false;
        }
    }

    private static bool IsExpertiseOrMasteryFeat(RulesElement feat)
    {
        var name = feat.Name;
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Expertise / Mastery are always weapon or implement bonus feats.
        if (name.EndsWith(" Expertise", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.EndsWith(" Mastery", StringComparison.OrdinalIgnoreCase))
            return true;

        // Specialization is shared with armor / shield feats, which are
        // defensive and don't belong in this bucket. Keep only weapon /
        // implement variants by excluding the armor / shield prefixes.
        if (name.EndsWith(" Specialization", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Armor ", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Shield ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

