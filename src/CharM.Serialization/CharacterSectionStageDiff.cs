using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CharM.Serialization;

/// <summary>
/// Extracts normalized, staged views of regenerated .dnd4e sections so
/// round-trip tests can gate diffs from broad structure down to values.
/// </summary>
public static partial class CharacterSectionStageDiff
{
    public static readonly IReadOnlyList<string> SupportedSections =
    [
        "Details",
        "AbilityScores",
        "StatBlock",
        "RulesElementTally",
        "LootTally",
        "LevelTree",
        "TextStrings",
        "Companions",
    ];

    public static SectionStageSnapshot Extract(byte[] bytes, IEnumerable<string>? sections = null)
    {
        using var ms = new MemoryStream(bytes);
        var doc = XDocument.Load(ms);
        return Extract(doc, sections);
    }

    public static SectionStageSnapshot Extract(XDocument document, IEnumerable<string>? sections = null)
    {
        var requested = BuildRequestedSet(sections);
        var items = new List<SectionStageItem>();

        if (requested.Contains("Details"))
            ExtractDetails(document, items);
        if (requested.Contains("AbilityScores"))
            ExtractAbilityScores(document, items);
        if (requested.Contains("StatBlock"))
            ExtractStatBlock(document, items);
        if (requested.Contains("RulesElementTally"))
            ExtractRulesElementTally(document, items);
        if (requested.Contains("LootTally"))
            ExtractLootTally(document, items);
        if (requested.Contains("LevelTree"))
            ExtractLevelTree(document, items);
        if (requested.Contains("TextStrings"))
            ExtractTextStrings(document, items);
        if (requested.Contains("Companions"))
            ExtractCompanions(document, items);

        return new SectionStageSnapshot(items);
    }

    public static SectionStageComparison Compare(
        SectionStageSnapshot reference,
        SectionStageSnapshot actual,
        int stage)
    {
        if (stage is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(stage), "Stage must be 1-4.");

        var itemDiffs = DiffItems(reference.Items, actual.Items);
        var childDiffs = new List<SectionStageChildDiff>();
        var fieldDiffs = new List<SectionStageFieldDiff>();
        var valueDiffs = new List<SectionStageValueDiff>();

        if (stage < 2)
            return new SectionStageComparison(itemDiffs, childDiffs, fieldDiffs, valueDiffs);

        var refItemsByKey = reference.Items.GroupBy(ItemGroupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var actualItemsByKey = actual.Items.GroupBy(ItemGroupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var key in refItemsByKey.Keys.Intersect(actualItemsByKey.Keys, StringComparer.Ordinal))
        {
            var refGroup = refItemsByKey[key];
            var actualGroup = actualItemsByKey[key];
            if (refGroup.Count != actualGroup.Count)
                continue;

            var childMultisetDiffs = DiffChildren(refGroup, actualGroup);
            childDiffs.AddRange(childMultisetDiffs);
            if (stage < 3 || childMultisetDiffs.Count > 0)
                continue;

            for (int i = 0; i < refGroup.Count; i++)
                CompareChildFields(refGroup[i], actualGroup[i], stage, fieldDiffs, valueDiffs);
        }

        return new SectionStageComparison(itemDiffs, childDiffs, fieldDiffs, valueDiffs);
    }

    public static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    private static HashSet<string> BuildRequestedSet(IEnumerable<string>? sections)
    {
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sections is null)
        {
            foreach (var section in SupportedSections)
                requested.Add(section);
            return requested;
        }

        foreach (var section in sections)
        {
            if (string.Equals(section, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var supported in SupportedSections)
                    requested.Add(supported);
                continue;
            }

            var canonical = SupportedSections.FirstOrDefault(s =>
                string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
                throw new ArgumentException($"Unsupported section '{section}'. Supported: {string.Join(", ", SupportedSections)}");
            requested.Add(canonical);
        }

        return requested;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static void ExtractDetails(XDocument document, List<SectionStageItem> items)
    {
        var details = document.Root?.Element("CharacterSheet")?.Element("Details");
        if (details is null)
            return;

        foreach (var field in details.Elements())
        {
            var name = field.Name.LocalName;
            items.Add(Item(
                "Details",
                Key("detail", name),
                name,
                Child("value", "Value", ("value", field.Value))));
        }
    }

    private static void ExtractAbilityScores(XDocument document, List<SectionStageItem> items)
    {
        var section = document.Root?.Element("CharacterSheet")?.Element("AbilityScores");
        if (section is null)
            return;

        items.Add(Item(
            "AbilityScores",
            "section",
            "AbilityScores",
            Child("attributes", "Attributes", AttributeFields(section, includeLegality: true))));

        foreach (var ability in section.Elements())
        {
            var name = ability.Name.LocalName;
            items.Add(Item(
                "AbilityScores",
                Key("ability", name),
                name,
                Child("score", "Score", AttributeFields(ability, includeLegality: true))));
        }
    }

    private static void ExtractStatBlock(XDocument document, List<SectionStageItem> items)
    {
        var statBlock = document.Root?.Element("CharacterSheet")?.Element("StatBlock");
        if (statBlock is null)
            return;

        foreach (var stat in statBlock.Elements("Stat"))
        {
            var statName = stat.Attribute("name")?.Value
                ?? stat.Elements("alias").FirstOrDefault()?.Attribute("name")?.Value
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(statName))
                continue;

            var children = new List<SectionStageChild>
            {
                Child("stat", "Stat", AttributeFields(stat, includeLegality: true)),
            };

            foreach (var alias in stat.Elements("alias"))
            {
                var aliasName = alias.Attribute("name")?.Value ?? string.Empty;
                children.Add(Child(
                    Key("alias", aliasName),
                    $"alias:{aliasName}",
                    AttributeFields(alias, includeLegality: true)));
            }

            foreach (var add in stat.Elements("statadd"))
            {
                children.Add(Child(
                    StatAddKey(add),
                    "statadd",
                    AttributeFields(add, includeLegality: true)));
            }

            items.Add(new SectionStageItem("StatBlock", Key("stat", statName), statName, children));
        }
    }

    private static void ExtractRulesElementTally(XDocument document, List<SectionStageItem> items)
    {
        var tally = document.Root?.Element("CharacterSheet")?.Element("RulesElementTally");
        if (tally is null)
            return;

        foreach (var re in tally.Elements("RulesElement"))
        {
            var identity = RulesElementIdentity(re);
            var children = new List<SectionStageChild>
            {
                Child("element", "RulesElement", AttributeFields(re, includeLegality: true)),
            };

            foreach (var specific in re.Elements("specific"))
            {
                var name = specific.Attribute("name")?.Value ?? string.Empty;
                children.Add(Child(
                    Key("specific", name),
                    $"specific:{name}",
                    ("name", name),
                    ("value", specific.Value)));
            }

            items.Add(new SectionStageItem(
                "RulesElementTally",
                Key("rules-element", identity.Key),
                identity.Label,
                children));
        }
    }

    private static void ExtractLootTally(XDocument document, List<SectionStageItem> items)
    {
        var lootTally = document.Root?.Element("CharacterSheet")?.Element("LootTally");
        if (lootTally is null)
            return;

        foreach (var loot in lootTally.Elements("loot"))
        {
            var directComponents = loot.Elements("RulesElement").ToList();
            var (componentKey, componentLabel) = LootComponentIdentity(loot);
            // Incorporate ALL components into the pairing key so composite
            // items that share a base (e.g. Carrikal +2 vs Battlecrazed
            // Weapon +4 Carrikal) hash to distinct keys instead of getting
            // positionally paired by the diff harness. This eliminates the
            // Stage 4 value-diff cascade that happens when our exporter and
            // OCB emit same-base-item composites in different orders.
            var compositeName = loot.Attribute("name")?.Value ?? string.Empty;
            var itemKey = Key("loot", componentKey, "name", compositeName);
            var itemLabel = string.IsNullOrWhiteSpace(compositeName)
                ? componentLabel
                : $"{compositeName} [{componentLabel}]";

            var children = new List<SectionStageChild>
            {
                Child("loot", "loot", AttributeFields(loot, includeLegality: true)),
            };

            foreach (var component in directComponents)
                AddRulesElementChild(children, "component", "component", component, includeDescendants: true);

            items.Add(new SectionStageItem("LootTally", itemKey, itemLabel, children));
        }
    }

    private static void ExtractLevelTree(XDocument document, List<SectionStageItem> items)
    {
        var root = document.Root;
        if (root is null)
            return;

        int ordinal = 0;
        foreach (var level in root.Elements("Level"))
        {
            ordinal++;
            var top = level.Element("RulesElement");
            var levelName = top?.Attribute("name")?.Value;
            var itemKey = Key("level", levelName ?? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var itemLabel = levelName is null ? $"Level #{ordinal}" : $"Level {levelName}";
            var children = new List<SectionStageChild>();

            if (top is not null)
                AddLevelRuleElementChildren(children, "RulesElement", "RulesElement", top);

            foreach (var loot in level.Elements("loot"))
            {
                var (componentKey, componentLabel) = LootComponentIdentity(loot);
                children.Add(Child(
                    Key("loot", componentKey),
                    "loot:" + componentLabel,
                    AttributeFields(loot, includeLegality: true)));
            }

            int userEditOrdinal = 0;
            foreach (var userEdit in level.Elements("UserEdit"))
            {
                userEditOrdinal++;
                var prefix = $"UserEdit[{userEditOrdinal}]";
                children.Add(Child(Key(prefix), prefix, AttributeFields(userEdit, includeLegality: true)));
                foreach (var wrapper in userEdit.Elements("RulesElement"))
                    AddLevelRuleElementChildren(children, prefix, prefix, wrapper);
            }

            items.Add(new SectionStageItem("LevelTree", itemKey, itemLabel, children));
        }
    }

    private static void ExtractTextStrings(XDocument document, List<SectionStageItem> items)
    {
        var root = document.Root;
        if (root is null)
            return;

        foreach (var textString in root.Elements("textstring"))
        {
            var name = textString.Attribute("name")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                continue;

            items.Add(Item(
                "TextStrings",
                Key("textstring", name),
                name,
                Child("value", "Value", ("value", textString.Value))));
        }
    }

    private static void ExtractCompanions(XDocument document, List<SectionStageItem> items)
    {
        var root = document.Root;
        var charSheet = root?.Element("CharacterSheet");
        var companions = charSheet?.Element("Companions");
        if (companions is null)
            return;

        int beastIndex = 0;
        foreach (var beast in companions.Elements("Beast"))
        {
            var idText = NormalizeWhitespace(beast.Element("ID")?.Value);
            // Use the ID line as the item label so power-level field diffs are
            // grouped per-beast. Fall back to a positional key when ID is blank.
            var key = !string.IsNullOrEmpty(idText) ? idText : $"#{beastIndex}";
            var children = new List<SectionStageChild>();

            foreach (var fieldName in BeastFieldOrder)
            {
                var fieldEl = beast.Element(fieldName);
                if (fieldEl is null) continue;
                var value = NormalizeWhitespace(fieldEl.Value);
                children.Add(Child(
                    Key("beast-field", fieldName),
                    fieldName,
                    ("value", value)));
            }

            // Capture any non-standard fields too so diffs surface them
            foreach (var fieldEl in beast.Elements())
            {
                var fname = fieldEl.Name.LocalName;
                if (Array.IndexOf(BeastFieldOrder, fname) >= 0) continue;
                children.Add(Child(
                    Key("beast-field", fname),
                    fname,
                    ("value", NormalizeWhitespace(fieldEl.Value))));
            }

            items.Add(Item(
                "Companions",
                Key("beast", key),
                $"Beast/{key}",
                children.ToArray()));
            beastIndex++;
        }
    }

    private static readonly string[] BeastFieldOrder =
    [
        "ID", "AbilityScores", "BeastPower", "BeastPowerText",
        "Size", "Speed", "Defenses", "HitPoints", "Surges",
        "AttackBonus", "BasicAttack", "TrainedSkills", "Damage",
    ];

    private static string NormalizeWhitespace(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : WhitespaceRegex().Replace(text.Trim(), " ");

    private static void AddRulesElementChild(
        List<SectionStageChild> children,
        string keyPrefix,
        string labelPrefix,
        XElement element,
        bool includeDescendants)
    {
        var identity = RulesElementIdentity(element);
        var path = $"{keyPrefix}/{identity.Key}";
        var label = $"{labelPrefix}/{identity.Label}";
        var fields = AttributeFields(element, includeLegality: true);
        AddSpecificFields(fields, element);
        children.Add(Child(Key(path), label, fields));

        if (!includeDescendants)
            return;

        foreach (var child in element.Elements("RulesElement"))
            AddRulesElementChild(children, path, label, child, includeDescendants: true);
    }

    private static void AddLevelRuleElementChildren(
        List<SectionStageChild> children,
        string keyPrefix,
        string labelPrefix,
        XElement element)
    {
        if (IsLeafEmptyPlaceholder(element))
            return;

        var identity = RulesElementIdentity(element);
        var path = $"{keyPrefix}/{identity.Key}";
        var label = $"{labelPrefix}/{identity.Label}";
        var fields = AttributeFields(element, includeLegality: true);
        AddSpecificFields(fields, element);
        children.Add(Child(Key(path), label, fields));

        foreach (var child in element.Elements("RulesElement"))
            AddLevelRuleElementChildren(children, path, label, child);
    }

    private static bool IsLeafEmptyPlaceholder(XElement element)
        => string.Equals(element.Name.LocalName, "RulesElement", StringComparison.Ordinal)
           && string.IsNullOrEmpty(element.Attribute("name")?.Value)
           && string.IsNullOrEmpty(element.Attribute("type")?.Value)
           && !element.Elements("RulesElement").Any();

    private static List<SectionStageItemSetDiff> DiffItems(
        IReadOnlyList<SectionStageItem> reference,
        IReadOnlyList<SectionStageItem> actual)
    {
        var refCounts = CountItems(reference);
        var actualCounts = CountItems(actual);
        var labels = BuildItemLabels(reference, actual);
        var diffs = new List<SectionStageItemSetDiff>();

        foreach (var key in refCounts.Keys.Union(actualCounts.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            int refCount = refCounts.GetValueOrDefault(key);
            int actualCount = actualCounts.GetValueOrDefault(key);
            if (refCount == actualCount)
                continue;

            var (section, itemKey) = SplitGroupKey(key);
            diffs.Add(new SectionStageItemSetDiff(
                section,
                itemKey,
                labels.GetValueOrDefault(key, itemKey),
                refCount,
                actualCount));
        }

        return diffs;
    }

    private static List<SectionStageChildDiff> DiffChildren(
        IReadOnlyList<SectionStageItem> reference,
        IReadOnlyList<SectionStageItem> actual)
    {
        var refCounts = CountChildren(reference);
        var actualCounts = CountChildren(actual);
        var itemLabel = reference.FirstOrDefault()?.Label ?? actual.FirstOrDefault()?.Label ?? string.Empty;
        var section = reference.FirstOrDefault()?.Section ?? actual.FirstOrDefault()?.Section ?? string.Empty;
        var itemKey = reference.FirstOrDefault()?.Key ?? actual.FirstOrDefault()?.Key ?? string.Empty;
        var labels = BuildChildLabels(reference, actual);
        var diffs = new List<SectionStageChildDiff>();

        foreach (var key in refCounts.Keys.Union(actualCounts.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            int refCount = refCounts.GetValueOrDefault(key);
            int actualCount = actualCounts.GetValueOrDefault(key);
            if (refCount == actualCount)
                continue;

            diffs.Add(new SectionStageChildDiff(
                section,
                itemKey,
                itemLabel,
                key,
                labels.GetValueOrDefault(key, key),
                refCount,
                actualCount));
        }

        return diffs;
    }

    private static void CompareChildFields(
        SectionStageItem reference,
        SectionStageItem actual,
        int stage,
        List<SectionStageFieldDiff> fieldDiffs,
        List<SectionStageValueDiff> valueDiffs)
    {
        var refByChild = reference.Children.GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var actualByChild = actual.Children.GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var childKey in refByChild.Keys.Intersect(actualByChild.Keys, StringComparer.Ordinal))
        {
            var refChildren = refByChild[childKey];
            var actualChildren = actualByChild[childKey];
            if (refChildren.Count != actualChildren.Count)
                continue;

            for (int i = 0; i < refChildren.Count; i++)
            {
                var refChild = refChildren[i];
                var actualChild = actualChildren[i];
                var refFields = refChild.Fields;
                var actualFields = actualChild.Fields;
                bool fieldSetsMatch = true;

                foreach (var field in refFields.Keys.Except(actualFields.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    fieldSetsMatch = false;
                    fieldDiffs.Add(new SectionStageFieldDiff(
                        reference.Section, reference.Key, reference.Label,
                        childKey, refChild.Label, field, "missing"));
                }

                foreach (var field in actualFields.Keys.Except(refFields.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    fieldSetsMatch = false;
                    fieldDiffs.Add(new SectionStageFieldDiff(
                        reference.Section, reference.Key, reference.Label,
                        childKey, refChild.Label, field, "extra"));
                }

                if (stage < 4 || !fieldSetsMatch)
                    continue;

                foreach (var field in refFields.Keys.Intersect(actualFields.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    var refValue = NormalizeForComparison(refFields[field]);
                    var actualValue = NormalizeForComparison(actualFields[field]);
                    if (!string.Equals(refValue, actualValue, StringComparison.Ordinal))
                    {
                        valueDiffs.Add(new SectionStageValueDiff(
                            reference.Section, reference.Key, reference.Label,
                            childKey, refChild.Label, field, refValue, actualValue));
                    }
                }
            }
        }
    }

    private static Dictionary<string, int> CountItems(IEnumerable<SectionStageItem> items)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = ItemGroupKey(item);
            result[key] = result.GetValueOrDefault(key) + 1;
        }

        return result;
    }

    private static Dictionary<string, int> CountChildren(IEnumerable<SectionStageItem> items)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in items.SelectMany(item => item.Children))
            result[child.Key] = result.GetValueOrDefault(child.Key) + 1;
        return result;
    }

    private static Dictionary<string, string> BuildItemLabels(
        IReadOnlyList<SectionStageItem> reference,
        IReadOnlyList<SectionStageItem> actual)
        => reference.Concat(actual)
            .GroupBy(ItemGroupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);

    private static Dictionary<string, string> BuildChildLabels(
        IReadOnlyList<SectionStageItem> reference,
        IReadOnlyList<SectionStageItem> actual)
        => reference.Concat(actual)
            .SelectMany(i => i.Children)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);

    private static string ItemGroupKey(SectionStageItem item) => item.Section + "\0" + item.Key;

    private static (string Section, string ItemKey) SplitGroupKey(string groupKey)
    {
        var parts = groupKey.Split('\0', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (string.Empty, groupKey);
    }

    private static SectionStageItem Item(
        string section,
        string key,
        string label,
        params SectionStageChild[] children)
        => new(section, key, label, children);

    private static SectionStageChild Child(
        string key,
        string label,
        params (string Name, string Value)[] fields)
        => new(key, label, fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.OrdinalIgnoreCase));

    private static SectionStageChild Child(
        string key,
        string label,
        IReadOnlyDictionary<string, string> fields)
        => new(key, label, fields);

    private static Dictionary<string, string> AttributeFields(XElement element, bool includeLegality)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in element.Attributes())
        {
            var name = attr.Name.LocalName;
            if (name.Equals("charelem", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!includeLegality && name.Equals("legality", StringComparison.OrdinalIgnoreCase))
                continue;
            fields["@" + name] = attr.Value;
        }

        return fields;
    }

    private static void AddSpecificFields(Dictionary<string, string> fields, XElement element)
    {
        foreach (var specific in element.Elements("specific"))
        {
            var name = specific.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
                continue;
            fields["specific:" + name] = specific.Value;
        }
    }

    private static string StatAddKey(XElement add)
    {
        string Part(string name) => add.Attribute(name)?.Value ?? string.Empty;
        return Key(
            "statadd",
            "type", Part("type"),
            "String", Part("String"),
            "Level", Part("Level"),
            "requires", Part("requires"),
            "wearing", Part("wearing"),
            "not-wearing", Part("not-wearing"),
            "conditional", Part("conditional"),
            "statlink", Part("statlink"),
            "abilmod", Part("abilmod"));
    }

    private static (string Key, string Label) LootComponentIdentity(XElement loot)
    {
        var components = loot.Elements("RulesElement").Select(RulesElementIdentity).ToList();
        if (components.Count == 0)
            return ("empty", "(empty)");
        return (
            Key(components.Select(c => c.Key).ToArray()),
            string.Join(" + ", components.Select(c => c.Label)));
    }

    private static (string Key, string Label) RulesElementIdentity(XElement element)
    {
        var name = element.Attribute("name")?.Value ?? string.Empty;
        var type = element.Attribute("type")?.Value ?? string.Empty;
        var id = element.Attribute("internal-id")?.Value ?? string.Empty;
        var key = Key("rules", type, id, name);
        var label = string.IsNullOrWhiteSpace(type)
            ? name
            : $"{type}: {name}";
        if (!string.IsNullOrWhiteSpace(id))
            label += $" [{id}]";
        return (key, label);
    }

    private static string Key(params string[] parts)
        => string.Join("\u001f", parts.Select(p => NormalizeKeyPart(p)));

    private static string NormalizeKeyPart(string? value)
        => NormalizeForComparison(value).ToLowerInvariant();
}

public sealed record SectionStageSnapshot(IReadOnlyList<SectionStageItem> Items);

public sealed record SectionStageItem(
    string Section,
    string Key,
    string Label,
    IReadOnlyList<SectionStageChild> Children);

public sealed record SectionStageChild(
    string Key,
    string Label,
    IReadOnlyDictionary<string, string> Fields);

public sealed record SectionStageComparison(
    IReadOnlyList<SectionStageItemSetDiff> ItemDiffs,
    IReadOnlyList<SectionStageChildDiff> ChildDiffs,
    IReadOnlyList<SectionStageFieldDiff> FieldDiffs,
    IReadOnlyList<SectionStageValueDiff> ValueDiffs);

public sealed record SectionStageItemSetDiff(
    string Section,
    string ItemKey,
    string ItemLabel,
    int RefCount,
    int ActualCount);

public sealed record SectionStageChildDiff(
    string Section,
    string ItemKey,
    string ItemLabel,
    string ChildKey,
    string ChildLabel,
    int RefCount,
    int ActualCount);

public sealed record SectionStageFieldDiff(
    string Section,
    string ItemKey,
    string ItemLabel,
    string ChildKey,
    string ChildLabel,
    string Field,
    string Direction);

public sealed record SectionStageValueDiff(
    string Section,
    string ItemKey,
    string ItemLabel,
    string ChildKey,
    string ChildLabel,
    string Field,
    string RefValue,
    string ActualValue);
