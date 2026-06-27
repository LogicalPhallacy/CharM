using System.Text.Json;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;

namespace CharM.RulesDb.Import;

/// <summary>
/// Shared serialization of a <see cref="RulesElement"/>'s fields and rules into
/// the JSON columns stored in <c>rules_elements</c>. Used by both the base
/// import (<see cref="RulesDbBuilder"/>) and the part merge
/// (<see cref="PartMerger"/>) so the two writers can't drift.
/// </summary>
internal static class RulesElementJson
{
    /// <summary>
    /// Serialize an element's field entries and rule directives. Returns
    /// (fieldsJson, rulesJson); either may be null when the source collection
    /// is empty. The ordered list-of-pairs view is used so duplicate field
    /// names (e.g. two <c>&lt;specific name="Hit"&gt;</c> children) round-trip.
    /// </summary>
    public static (string? FieldsJson, string? RulesJson) Serialize(RulesElement element)
    {
        IReadOnlyList<KeyValuePair<string, string>> entries = element.FieldEntries.Count > 0
            ? element.FieldEntries
            : element.Fields.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList();

        string? fieldsJson = entries.Count > 0
            ? JsonSerializer.Serialize(entries)
            : null;

        string? rulesJson = element.Rules.Count > 0
            ? JsonSerializer.Serialize(element.Rules, RulesDatabase.SharedJsonOptions)
            : null;

        return (fieldsJson, rulesJson);
    }
}
