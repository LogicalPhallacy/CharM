using System.Xml;
using System.Xml.Linq;
using CharM.Engine.Rules;
using static CharM.RulesDb.Import.RulesXmlImportHelpers;

namespace CharM.RulesDb.Import;

/// <summary>
/// Abstraction over a single <c>&lt;rules&gt;</c> directive element so the
/// attribute-only directive parsers can be written once and shared by both the
/// streaming <see cref="RulesXmlReader"/> (base-DB build) and the XElement-based
/// <see cref="PartMerger"/> (part-overlay merge). Both previously carried
/// byte-identical copies of these parsers, differing only in how an attribute is
/// read (<c>XmlReader.GetAttribute</c> vs <c>XElement.Attribute(..).Value</c>).
/// </summary>
internal interface IRuleXmlNode
{
    /// <summary>Case-sensitive attribute lookup; null if absent.</summary>
    string? Attr(string name);

    /// <summary>
    /// Try the upper-cased attribute name first, then the lower-cased form.
    /// The rules XML mixes casing for a few attributes (Level/Category/Prepare).
    /// </summary>
    string? AttrCI(string upper, string lower);
}

/// <summary>Adapter over a streaming <see cref="XmlReader"/> positioned on a directive start element.</summary>
internal readonly struct XmlReaderRuleNode(XmlReader reader) : IRuleXmlNode
{
    public string? Attr(string name) => reader.GetAttribute(name);
    public string? AttrCI(string upper, string lower) => reader.GetAttribute(upper) ?? reader.GetAttribute(lower);
}

/// <summary>Adapter over a LINQ-to-XML <see cref="XElement"/> directive node.</summary>
internal readonly struct XElementRuleNode(XElement el) : IRuleXmlNode
{
    public string? Attr(string name) => el.Attribute(name)?.Value;
    public string? AttrCI(string upper, string lower) => el.Attribute(upper)?.Value ?? el.Attribute(lower)?.Value;
}

/// <summary>
/// Single source of truth for the eight attribute-only <c>&lt;rules&gt;</c>
/// directive parsers (statadd, grant, modify, replace, drop, suggest,
/// textstring, statalias).
///
/// The ninth directive — <c>&lt;select&gt;</c> — is intentionally NOT unified
/// here: its inner-text capture genuinely differs between the two readers
/// (streaming subtree-text concatenation vs <c>el.Value</c> only when the
/// element is childless), so <see cref="RulesXmlReader"/> and
/// <see cref="PartMerger"/> each keep their own ParseSelect to preserve exact
/// behavior. The methods are generic over <see cref="IRuleXmlNode"/> to avoid
/// boxing the adapter struct on every directive during the ~38K-element import.
/// </summary>
internal static class RuleDirectiveParser
{
    public static StatAddDirective? ParseStatAdd<TNode>(TNode n) where TNode : IRuleXmlNode
    {
        string? name = n.Attr("name");
        string? valueStr = n.Attr("value");
        if (name is null || valueStr is null) return null;

        return new StatAddDirective
        {
            Name = name,
            Value = ValueExpression.Parse(valueStr),
            BonusType = n.Attr("type"),
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
            Condition = n.Attr("condition"),
            Wearing = n.Attr("wearing"),
            NotWearing = n.Attr("not-wearing"),
            Zero = ParseBool(n.Attr("zero")),
            NonZero = ParseBool(n.Attr("non-zero")),
            HalfPoint = ParseBool(n.Attr("half-point")),
            StatMin = n.Attr("statmin"),
        };
    }

    public static GrantDirective? ParseGrant<TNode>(TNode n) where TNode : IRuleXmlNode
    {
        string? name = n.Attr("name");
        string? type = n.Attr("type");
        if (name is null || type is null) return null;

        return new GrantDirective
        {
            Name = name,
            ElementType = type,
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
        };
    }

    public static ModifyDirective? ParseModify<TNode>(TNode n) where TNode : IRuleXmlNode
    {
        // Field attribute is case-insensitive: both "Field" and "field" are used.
        string? field = n.Attr("Field") ?? n.Attr("field");
        if (field is null) return null;

        return new ModifyDirective
        {
            Field = field,
            Name = n.Attr("name"),
            ElementType = n.Attr("type"),
            Value = n.Attr("value"),
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
            ListAddition = n.Attr("list-addition"),
            SelectSlot = n.Attr("select"),
            Wearing = n.Attr("wearing"),
            DieIncrease = ParseIntOrNull(n.Attr("die-increase")),
        };
    }

    public static ReplaceDirective ParseReplace<TNode>(TNode n) where TNode : IRuleXmlNode
        => new()
        {
            Name = n.Attr("name"),
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Multiclass = n.Attr("multiclass"),
            PowerSwap = n.Attr("powerswap"),
            PowerReplace = n.Attr("power-replace"),
            Optional = ParseBool(n.Attr("optional")),
            Requires = n.Attr("requires"),
        };

    public static DropDirective ParseDrop<TNode>(TNode n) where TNode : IRuleXmlNode
        => new()
        {
            SelectSlot = n.Attr("select"),
            Name = n.Attr("name"),
            ElementType = n.Attr("type"),
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
        };

    public static SuggestDirective? ParseSuggest<TNode>(TNode n) where TNode : IRuleXmlNode
    {
        string? name = n.Attr("name");
        string? type = n.Attr("type");
        if (name is null || type is null) return null;

        return new SuggestDirective
        {
            Name = name,
            ElementType = type,
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
        };
    }

    public static TextStringDirective? ParseTextString<TNode>(TNode n) where TNode : IRuleXmlNode
    {
        string? name = n.Attr("name");
        string? value = n.Attr("value");
        if (name is null || value is null) return null;

        return new TextStringDirective
        {
            Name = name,
            Value = value,
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
            Condition = n.Attr("condition"),
        };
    }

    public static StatAliasDirective? ParseStatAlias<TNode>(TNode n) where TNode : IRuleXmlNode
    {
        string? name = n.Attr("name");
        string? alias = n.Attr("alias");
        if (name is null || alias is null) return null;

        return new StatAliasDirective
        {
            Name = name,
            Alias = alias,
            Level = ParseIntOrNull(n.AttrCI("Level", "level")),
            Requires = n.Attr("requires"),
        };
    }
}
