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

        var asm = new RulesElementAssembler();

        if (!reader.IsEmptyElement)
        {
            int depth = reader.Depth;
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
                    asm.AppendDescriptionText(reader.Value);
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                asm.HandleChild(new XmlReaderChildNode(reader));
            }
        }

        return asm.Build(internalId, name, type, source);
    }

    /// <summary>
    /// <see cref="IRuleChildNode"/> adapter over the streaming reader positioned
    /// on a child element start tag. <see cref="GetTextContent"/> and
    /// <see cref="ParseRulesBlockInto"/> consume the element (advance the cursor),
    /// matching the inline calls they replaced.
    /// </summary>
    private readonly struct XmlReaderChildNode(XmlReader reader) : IRuleChildNode
    {
        public string LocalName => reader.LocalName;
        public string? Attr(string name) => reader.GetAttribute(name);
        public string GetTextContent() => reader.ReadElementContentAsString()?.Trim() ?? "";
        public void ParseRulesBlockInto(List<RuleDirective> rules) => ReadRulesBlock(reader, rules);
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
