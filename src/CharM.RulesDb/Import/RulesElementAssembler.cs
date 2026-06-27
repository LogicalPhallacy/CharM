using System.Text;
using CharM.Engine.Rules;
using static CharM.RulesDb.Import.RulesXmlImportHelpers;

namespace CharM.RulesDb.Import;

/// <summary>
/// Abstraction over one child element of a <c>&lt;RulesElement&gt;</c>, so the
/// field-mapping logic can be written once and shared by both the streaming
/// <see cref="RulesXmlReader"/> (base-DB build) and the XElement-based
/// <see cref="PartMerger"/> (part-overlay merge).
/// </summary>
internal interface IRuleChildNode
{
    /// <summary>The child element's local (namespace-stripped) name.</summary>
    string LocalName { get; }

    /// <summary>Case-sensitive attribute lookup; null if absent.</summary>
    string? Attr(string name);

    /// <summary>
    /// The child's trimmed inner text. NOTE: for the streaming reader this
    /// CONSUMES the element (advances the cursor past its end tag), exactly like
    /// the <c>ReadElementContentAsString</c> calls it replaces — so call it at
    /// most once per child and only for text-bearing children.
    /// </summary>
    string GetTextContent();

    /// <summary>Parse this <c>&lt;rules&gt;</c> node's directive children into
    /// <paramref name="rules"/> using the owning reader's directive parser.</summary>
    void ParseRulesBlockInto(List<RuleDirective> rules);
}

/// <summary>
/// Single source of truth for turning a <c>&lt;RulesElement&gt;</c>'s children
/// into a <see cref="ParsedElement"/>. Both readers iterate their children
/// natively (forward-only cursor vs. random-access tree) and route each one
/// through <see cref="HandleChild{TNode}"/>, so the decision of "which child
/// maps to which field" — including the easily-forgotten <c>print-prereqs</c>,
/// <c>Flavor</c> and lowercase <c>prereqs</c> cases — lives in exactly one place.
///
/// Before this existed, <see cref="PartMerger"/> carried a drifted copy of this
/// switch that silently dropped <c>print-prereqs</c>/<c>Flavor</c> on every
/// part-merged element. Keep new field handling HERE, never per-reader.
/// </summary>
internal sealed class RulesElementAssembler
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string>> _fieldEntries = [];
    private readonly List<string> _categories = [];
    private readonly List<RuleDirective> _rules = [];
    private readonly StringBuilder _description = new();
    private string? _prereqs;

    /// <summary>Map one child element onto the accumulating element state.</summary>
    public void HandleChild<TNode>(TNode child) where TNode : IRuleChildNode
    {
        switch (child.LocalName)
        {
            case "specific":
                string? fieldName = child.Attr("name");
                if (fieldName is not null)
                    AddSpecific(fieldName, child.GetTextContent());
                break;

            case "Prereqs":
            case "prereqs":
                _prereqs = child.GetTextContent();
                break;

            case "Category":
                AddCategories(child.GetTextContent());
                break;

            case "rules":
                child.ParseRulesBlockInto(_rules);
                break;

            case "print-prereqs":
                AddNamedTextField("print-prereqs", child.GetTextContent());
                break;

            case "Flavor":
            case "flavor":
                AddNamedTextField("Flavor", child.GetTextContent());
                break;
        }
    }

    /// <summary>
    /// Append raw mixed-content text (the description body that sits as direct
    /// text between/after child elements). Each reader captures these nodes its
    /// own way; normalization and first-wins assignment happen in <see cref="Build"/>.
    /// </summary>
    public void AppendDescriptionText(string text) => _description.Append(text);

    /// <summary>Finalize the description and produce the parsed element + categories.</summary>
    public ParsedElement Build(string internalId, string name, string type, string? source)
    {
        // Mixed-content body text becomes the Description field (first-wins, so an
        // explicit <specific name="Description"> still takes precedence).
        AddNamedTextField("Description", NormalizeDescription(_description.ToString()));

        var element = new RulesElement
        {
            InternalId = internalId,
            Name = name,
            Type = type,
            Source = source,
            Prereqs = _prereqs,
            Fields = _fields,
            FieldEntries = _fieldEntries,
            Rules = _rules,
        };
        return new ParsedElement(element, _categories);
    }

    // NOTE: do NOT trim the field NAME — augmentable powers use
    // <specific name=" Hit"> (leading space) to keep each Augment-N variant
    // distinct. Content is already trimmed by the child node. The lookup
    // Dictionary keeps the FIRST occurrence (OCB RulesElementField behavior);
    // fieldEntries records every occurrence in document order.
    private void AddSpecific(string name, string content)
    {
        _fieldEntries.Add(new(name, content));
        if (!_fields.ContainsKey(name))
            _fields[name] = content;
    }

    // Singleton text fields (print-prereqs, Flavor, Description): skip empty,
    // first-wins, mirror into fieldEntries.
    private void AddNamedTextField(string key, string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        if (_fields.ContainsKey(key)) return;
        _fields[key] = content;
        _fieldEntries.Add(new(key, content));
    }

    private void AddCategories(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        foreach (var cat in content.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            _categories.Add(cat);
    }
}
