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

    /// <summary>
    /// Deserialize a <c>fields_json</c> column value into its ordered list of
    /// (name, value) field entries, accepting both the current array-of-pairs
    /// format and the legacy object format. Shared so every reader/writer of
    /// the column agrees on the two on-disk shapes (prevents parser drift).
    /// </summary>
    public static List<KeyValuePair<string, string>> DeserializeEntries(string? fieldsJson)
    {
        var entries = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(fieldsJson)) return entries;

        int i = 0;
        while (i < fieldsJson.Length && char.IsWhiteSpace(fieldsJson[i])) i++;
        bool isArray = i < fieldsJson.Length && fieldsJson[i] == '[';

        if (isArray)
        {
            var pairs = JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(fieldsJson) ?? [];
            entries.AddRange(pairs);
        }
        else
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(fieldsJson) ?? new();
            foreach (var kv in dict)
                entries.Add(new(kv.Key, kv.Value));
        }

        return entries;
    }

    /// <summary>Serialize an ordered list of field entries back to the array-of-pairs column format, or null when empty.</summary>
    public static string? SerializeEntries(IReadOnlyList<KeyValuePair<string, string>> entries) =>
        entries.Count > 0 ? JsonSerializer.Serialize(entries) : null;
}
