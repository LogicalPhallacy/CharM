using System.Xml;
using CharM.Engine.Rules;
using static CharM.RulesDb.Import.RulesXmlImportHelpers;

namespace CharM.RulesDb.Import;

/// <summary>
/// A parsed rules element together with its category tags.
/// </summary>
public sealed record ParsedElement(RulesElement Element, IReadOnlyList<string> Categories);

/// <summary>
/// Streaming XML parser for D20Rules XML files.
/// Uses XmlReader to avoid loading the entire 47 MB file into memory.
/// </summary>
public static partial class RulesXmlReader
{
    /// <summary>
    /// Parse all RulesElement entries from a D20Rules XML file.
    /// </summary>
    public static IEnumerable<RulesElement> Read(string xmlPath)
    {
        foreach (var parsed in ReadAll(xmlPath))
            yield return parsed.Element;
    }

    /// <summary>
    /// Parse all elements with their category lists (used by the import pipeline).
    /// </summary>
    public static IEnumerable<ParsedElement> ReadAll(string xmlPath)
    {
        using var stream = File.OpenRead(xmlPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
        });

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "RulesElement")
            {
                var parsed = ReadRulesElement(reader);
                if (parsed is not null)
                    yield return parsed;
            }
        }
    }

    private static ParsedElement? ReadRulesElement(XmlReader reader)
    {
        string? name = reader.GetAttribute("name");
        string? type = reader.GetAttribute("type");
        string? internalId = reader.GetAttribute("internal-id");
        string? source = reader.GetAttribute("source");

        if (name is null || type is null || internalId is null)
            return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fieldEntries = new List<KeyValuePair<string, string>>();
        string? prereqs = null;
        var categories = new List<string>();
        var rules = new List<RuleDirective>();

        if (!reader.IsEmptyElement)
        {
            int depth = reader.Depth;
            var description = new System.Text.StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;

                // Capture mixed-content text nodes that sit between/after child elements
                // at the RulesElement level (e.g. body description after </rules>).
                if ((reader.NodeType == XmlNodeType.Text
                     || reader.NodeType == XmlNodeType.CDATA
                     || reader.NodeType == XmlNodeType.SignificantWhitespace)
                    && reader.Depth == depth + 1)
                {
                    description.Append(reader.Value);
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                switch (reader.LocalName)
                {
                    case "specific":
                        ReadSpecific(reader, fields, fieldEntries);
                        break;

                    case "Prereqs":
                    case "prereqs":
                        prereqs = reader.ReadElementContentAsString()?.Trim();
                        break;

                    case "Category":
                        ReadCategories(reader, categories);
                        break;

                    case "rules":
                        ReadRulesBlock(reader, rules);
                        break;

                    case "print-prereqs":
                        var printPrereqs = reader.ReadElementContentAsString()?.Trim();
                        if (!string.IsNullOrEmpty(printPrereqs)
                            && !fields.ContainsKey("print-prereqs"))
                        {
                            fields["print-prereqs"] = printPrereqs;
                            fieldEntries.Add(new("print-prereqs", printPrereqs));
                        }
                        break;

                    case "Flavor":
                    case "flavor":
                        var flavor = reader.ReadElementContentAsString()?.Trim();
                        if (!string.IsNullOrEmpty(flavor)
                            && !fields.ContainsKey("Flavor"))
                        {
                            fields["Flavor"] = flavor;
                            fieldEntries.Add(new("Flavor", flavor));
                        }
                        break;
                }
            }

            string descText = NormalizeDescription(description.ToString());
            if (descText.Length > 0 && !fields.ContainsKey("Description"))
            {
                fields["Description"] = descText;
                fieldEntries.Add(new("Description", descText));
            }
        }

        var element = new RulesElement
        {
            InternalId = internalId,
            Name = name,
            Type = type,
            Source = source,
            Prereqs = prereqs,
            Fields = fields,
            FieldEntries = fieldEntries,
            Rules = rules,
        };

        return new ParsedElement(element, categories);
    }

    private static void ReadSpecific(
        XmlReader reader,
        Dictionary<string, string> fields,
        List<KeyValuePair<string, string>> fieldEntries)
    {
        string? fieldName = reader.GetAttribute("name");
        if (fieldName is null) return;

        // NOTE: do NOT trim leading whitespace on the field name. Augmentable
        // powers intentionally use `<specific name=" Hit">` (leading space) to
        // distinguish each Augment-N variant's Hit/Effect/Target lines from the
        // base power's. Collapsing them would let the last augment overwrite
        // the base. Match OCB's behavior and treat each variant as a distinct key.

        string content = reader.ReadElementContentAsString()?.Trim() ?? "";

        // Always record the raw entry so callers that need duplicates (e.g.
        // primary vs secondary attack panes — Ravening Thought emits two
        // <specific name="Hit"> children, one for the primary attack at 2d6
        // and one for the secondary at 1d6) can recover both. The lookup
        // Dictionary holds the FIRST occurrence to match OCB's
        // RulesElementField behavior for single-named queries.
        fieldEntries.Add(new(fieldName, content));
        if (!fields.ContainsKey(fieldName))
            fields[fieldName] = content;
    }

    private static void ReadCategories(XmlReader reader, List<string> categories)
    {
        string content = reader.ReadElementContentAsString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(content)) return;

        foreach (var cat in content.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            categories.Add(cat);
    }

    private static void ReadRulesBlock(XmlReader reader, List<RuleDirective> rules)
    {
        if (reader.IsEmptyElement) return;

        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            var node = new XmlReaderRuleNode(reader);
            RuleDirective? directive = reader.LocalName switch
            {
                "statadd" => RuleDirectiveParser.ParseStatAdd(node),
                "grant" => RuleDirectiveParser.ParseGrant(node),
                "modify" => RuleDirectiveParser.ParseModify(node),
                "select" => ParseSelect(reader),
                "replace" => RuleDirectiveParser.ParseReplace(node),
                "drop" => RuleDirectiveParser.ParseDrop(node),
                "suggest" => RuleDirectiveParser.ParseSuggest(node),
                "textstring" => RuleDirectiveParser.ParseTextString(node),
                "statalias" => RuleDirectiveParser.ParseStatAlias(node),
                _ => null,
            };

            if (directive is not null)
                rules.Add(directive);
        }
    }

    private static SelectDirective? ParseSelect(XmlReader reader)
    {
        string? type = reader.GetAttribute("type");
        if (type is null) return null;

        // IMPORTANT: capture ALL attributes BEFORE walking inner content.
        // Once we Read() past the start element to capture text, the reader
        // is positioned on the EndElement and GetAttribute() returns null
        // for everything. (This is the bug that silently dropped requires=
        // on Beast Mastery's Ability-Increase selects.)
        int number = ParseIntOrNull(reader.GetAttribute("number")) ?? 1;
        string? category = GetAttrCI(reader, "Category", "category");
        string? attrName = reader.GetAttribute("name");
        int? level = ParseIntOrNull(GetAttrCI(reader, "Level", "level"));
        string? requires = reader.GetAttribute("requires");
        string? prepare = GetAttrCI(reader, "Prepare", "prepare");
        string? spellbook = reader.GetAttribute("spellbook");
        bool optional = ParseBool(reader.GetAttribute("optional"));
        bool existing = ParseBool(reader.GetAttribute("existing"));
        string? defaultAttr = reader.GetAttribute("default");
        string? grant = reader.GetAttribute("grant");
        bool isEmpty = reader.IsEmptyElement;

        // The select label is either the name="..." attribute OR the
        // element's inner text content (whitespace-trimmed). Example:
        //   <select type="Racial Trait" number="1" Category="...">
        //   Dragon Breath Key Ability
        //   </select>
        // Without inner-text capture, Dragon Breath choices come back
        // labelled only by their Type ("Racial Trait"), and OCB-compatible
        // SummaryText round-trips lose the section header.
        string? innerText = null;
        if (!isEmpty)
        {
            int depth = reader.Depth;
            var sb = new System.Text.StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    sb.Append(reader.Value);
            }
            string trimmed = sb.ToString().Trim();
            if (trimmed.Length > 0) innerText = trimmed;
        }

        return new SelectDirective
        {
            ElementType = type,
            Number = number,
            Category = category,
            // Prefer the attribute as the slot identifier; fall back to inner
            // text only when the attribute is absent. See SelectDirective.Name
            // doc comment for rationale (Arcane Admixture pattern: both signals
            // can be present and they MEAN different things — modify/replace
            // directives target the attribute string).
            Name = attrName ?? innerText,
            DisplayLabel = innerText,
            Level = level,
            Requires = requires,
            Prepare = prepare,
            Spellbook = spellbook,
            Optional = optional,
            Existing = existing,
            Default = defaultAttr,
            Grant = grant,
        };
    }

    /// <summary>
    /// Case-insensitive attribute lookup. The XML uses mixed casing for attributes
    /// (e.g., "Level"/"level", "Category"/"category"). XmlReader.GetAttribute() is
    /// case-sensitive, so we check both forms.
    /// </summary>
    private static string? GetAttrCI(XmlReader reader, string upper, string lower) =>
        reader.GetAttribute(upper) ?? reader.GetAttribute(lower);
}
