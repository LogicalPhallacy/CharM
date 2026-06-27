using CharM.Engine.Rules;

namespace CharM.ImportExport;

/// <summary>
/// Shared clone helpers for the short-lived <see cref="RulesElement"/>
/// projections that <see cref="PowerStatsBuilder"/> builds while overlaying
/// modify-directives, per-instance specifics, synthetic categories, and
/// weapon-only marker fields onto a base element.
///
/// IMPORTANT — faithful to the pre-existing inline clones these replace:
/// only the six stable properties (<c>InternalId</c>, <c>Name</c>, <c>Type</c>,
/// <c>Source</c>, <c>Prereqs</c>, <c>Rules</c>) are carried, plus the supplied
/// <c>Fields</c>/<c>Categories</c>. <see cref="RulesElement.FieldEntries"/> is
/// intentionally NOT carried — every projection site already dropped the
/// document-order multiset, and reproducing that exactly preserves PowerStats
/// parity. Do not "fix" this to copy FieldEntries without a parity re-validation.
/// </summary>
internal static class RulesElementProjection
{
    /// <summary>
    /// Clone <paramref name="source"/> replacing its <c>Fields</c> with
    /// <paramref name="fields"/>. By default the <c>Categories</c> list
    /// reference is shared with the source (matching the inline clones that
    /// wrote <c>Categories = source.Categories</c>); pass an explicit
    /// <paramref name="categories"/> (e.g. <c>[.. source.Categories]</c>) for
    /// sites that copied or augmented the list.
    /// </summary>
    public static RulesElement WithFields(
        RulesElement source,
        Dictionary<string, string> fields,
        List<string>? categories = null)
        => new()
        {
            InternalId = source.InternalId,
            Name = source.Name,
            Type = source.Type,
            Source = source.Source,
            Prereqs = source.Prereqs,
            Rules = source.Rules,
            Fields = fields,
            Categories = categories ?? source.Categories,
        };
}
