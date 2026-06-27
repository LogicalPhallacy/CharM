using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;

namespace CharM.Engine.CharacterModel;

/// <summary>
/// Manages the character's element tree. Processes structural directives 
/// (grant, select, drop, replace) and maintains the tree of active elements.
/// </summary>
public sealed class CharacterElementTree
{
    /// <summary>Root element (represents the character itself).</summary>
    public CharacterElement Root { get; }

    /// <summary>All active text strings set by textstring directives.</summary>
    public Dictionary<string, string> TextStrings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a rules element by (name, type). Used by grant directives to find
    /// target elements in the rules database.
    /// </summary>
    public Func<string, string, RulesElement?>? ElementResolver { get; set; }

    public CharacterElementTree()
    {
        Root = new CharacterElement();
    }

    /// <summary>
    /// Check if the character has a named element (for requires evaluation).
    /// Matches against RulesElement.Name, InternalId, and "Name Type" composite
    /// (e.g., "Arcana Skill Training" matches Name="Arcana" Type="Skill Training").
    /// </summary>
    public bool HasElement(string name)
    {
        return MatchesElement(Root, name);

        static bool MatchesElement(CharacterElement node, string name)
        {
            if (node.IsActive && node.RulesElement is { } re)
            {
                if (string.Equals(re.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(re.InternalId, name, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Check "Name Type" composite (e.g., "Arcana Skill Training" matches
                // an element with Name="Arcana" and Type="Skill Training") without
                // allocating a temporary composite for every node visited.
                if (CompositeNameAndTypeEquals(re.Name, re.Type, name))
                    return true;
            }

            foreach (var child in node.Children)
            {
                if (MatchesElement(child, name))
                    return true;
            }
            return false;
        }
    }

    private static bool CompositeNameAndTypeEquals(string elementName, string elementType, string candidate)
    {
        int expectedLength = elementName.Length + 1 + elementType.Length;
        return candidate.Length == expectedLength
            && candidate.AsSpan(0, elementName.Length).Equals(elementName, StringComparison.OrdinalIgnoreCase)
            && candidate[elementName.Length] == ' '
            && candidate.AsSpan(elementName.Length + 1).Equals(elementType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if character has an element of given type matching category.
    /// For now, checks Type match and does a substring match against the element's
    /// Fields values for the category string.
    /// </summary>
    public bool HasElementOfTypeAndCategory(string type, string category)
    {
        return SearchTypeAndCategory(Root, type, category);

        static bool SearchTypeAndCategory(CharacterElement node, string type, string category)
        {
            if (node.IsActive && node.RulesElement is { } re
                && string.Equals(re.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                // Check if any field value contains the category (substring match)
                foreach (var kvp in re.Fields)
                {
                    if (kvp.Value.Contains(category, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                // Also check element name as a fallback
                if (re.Name.Contains(category, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            foreach (var child in node.Children)
            {
                if (SearchTypeAndCategory(child, type, category))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Get all active RulesElements on the character (flattened).</summary>
    public IEnumerable<RulesElement> GetActiveElements()
    {
        foreach (var node in Root.GetAllDescendants())
        {
            if (node.IsActive && node.RulesElement is { } element)
                yield return element;
        }
    }

    /// <summary>
    /// Process a GrantDirective: resolve the target element, create a child,
    /// check level gating and requires conditions.
    /// </summary>
    public CharacterElement? ProcessGrant(GrantDirective grant, CharacterElement parent, int characterLevel)
    {
        // Level gating: skip if grant's level exceeds character level
        if (grant.Level.HasValue && grant.Level.Value > characterLevel)
            return null;

        // Requires check
        if (!EvaluateRequires(grant.Requires))
            return null;

        // Resolve the target element from the rules database.
        RulesElement? resolved = ResolveGrantTarget(grant);

        var child = parent.AddChild(resolved, grant.Level ?? 1);
        return child;
    }

    private RulesElement? ResolveGrantTarget(GrantDirective grant)
    {
        if (string.Equals(grant.Name, "[Dilettante]", StringComparison.OrdinalIgnoreCase)
            && string.Equals(grant.ElementType, "CountsAsClass", StringComparison.OrdinalIgnoreCase))
        {
            var dilettanteCountsAs = ResolveDilettanteCountsAsClass();
            if (dilettanteCountsAs is not null)
                return dilettanteCountsAs;
        }

        return ElementResolver?.Invoke(grant.Name, grant.ElementType);
    }

    private RulesElement? ResolveDilettanteCountsAsClass()
    {
        if (ElementResolver is null) return null;

        var power = Root.GetAllDescendants()
            .FirstOrDefault(node => node.IsActive && node.RulesElement is not null && IsDilettantePower(node))
            ?.RulesElement;
        if (power is null) return null;

        foreach (var category in power.Categories)
        {
            var classElement = ElementResolver(category, "Class");
            if (classElement is null
                || (!string.Equals(classElement.Type, "Class", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(classElement.Type, "Hybrid Class", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var countsAs = ElementResolver(classElement.Name, "CountsAsClass");
            if (countsAs is not null)
                return countsAs;
        }

        return null;

        static bool IsDilettantePower(CharacterElement node)
            => string.Equals(node.RulesElement!.Type, "Power", StringComparison.OrdinalIgnoreCase)
               && (string.Equals(node.Choice?.Name, "Dilettante", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(node.SlotOwnerInternalId, "ID_FMP_RACIAL_TRAIT_643", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Process a SelectDirective: create a choice slot on a child element.
    /// The actual selection happens later via MakeChoice().
    /// </summary>
    public ChoiceSlot ProcessSelect(SelectDirective select, CharacterElement parent, string? directiveKey = null)
    {
        var slot = new ChoiceSlot
        {
            Name = select.Name,
            DisplayLabel = select.DisplayLabel,
            ElementType = select.ElementType,
            Number = select.Number,
            Category = select.Category,
            Requires = select.Requires,
            Optional = select.Optional,
            Existing = select.Existing,
            OwnerInternalId = parent.RulesElement?.InternalId,
            DirectiveKey = directiveKey,
            Level = select.Level,
        };

        // Create N placeholder children — one per required selection.
        // This preserves directive order in the tree so that multi-select
        // slots (e.g., Power x2, Skill Training x3) fill consecutive
        // positions rather than appending at the end.
        for (int i = 0; i < select.Number; i++)
        {
            var child = parent.AddChild(null, select.Level ?? 1);
            if (i == 0)
                child.Choice = slot;  // only first placeholder carries the slot
        }

        return slot;
    }

    /// <summary>
    /// Remove a choice slot from the tree by reference. Intended for pending,
    /// unfilled slots that become invalid after later grants alter the state.
    /// </summary>
    public bool RemoveChoiceSlot(ChoiceSlot slot)
    {
        return RemoveChoiceSlot(Root, slot);

        static bool RemoveChoiceSlot(CharacterElement node, ChoiceSlot slot)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                if (ReferenceEquals(child.Choice, slot))
                {
                    int removeCount = 1;
                    for (int j = i + 1; j < node.Children.Count && removeCount < slot.Number; j++)
                    {
                        if (node.Children[j].Choice is null && node.Children[j].RulesElement is null)
                            removeCount++;
                        else
                            break;
                    }

                    for (int k = 0; k < removeCount; k++)
                    {
                        node.Children[i].Parent = null;
                        node.Children.RemoveAt(i);
                    }

                    return true;
                }

                if (RemoveChoiceSlot(child, slot))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Process a DropDirective: find and remove the target element or select slot.
    /// </summary>
    public bool ProcessDrop(DropDirective drop, CharacterElement parent)
    {
        // Drop by select slot name — search within parent's subtree
        if (drop.SelectSlot is not null)
        {
            return RemoveChoiceSlot(parent, drop.SelectSlot);
        }

        // Drop by name + type — search within parent's subtree
        if (drop.Name is not null)
        {
            // Try finding by InternalId first
            var target = parent.FindDescendant(drop.Name);
            if (target is not null)
            {
                var actualParent = target.Parent ?? parent;
                return actualParent.RemoveChild(drop.Name);
            }

            // Try by name+type
            if (drop.ElementType is not null)
            {
                target = parent.FindDescendantByNameAndType(drop.Name, drop.ElementType);
                if (target?.RulesElement is not null)
                {
                    var actualParent = target.Parent ?? parent;
                    return actualParent.RemoveChild(target.RulesElement.InternalId);
                }
            }
        }

        return false;

        static bool RemoveChoiceSlot(CharacterElement node, string slotName)
        {
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (child.Choice is not null
                    && string.Equals(child.Choice.Name, slotName, StringComparison.OrdinalIgnoreCase))
                {
                    child.Parent = null;
                    node.Children.RemoveAt(i);
                    return true;
                }

                if (RemoveChoiceSlot(child, slotName))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Process a TextStringDirective: store the key-value pair.</summary>
    public void ProcessTextString(TextStringDirective textString)
    {
        TextStrings[textString.Name] = textString.Value;
    }

    /// <summary>
    /// Make a choice for a choice slot: set the selected element and 
    /// return the newly created CharacterElement.
    /// </summary>
    public CharacterElement? MakeChoice(ChoiceSlot slot, RulesElement chosen, CharacterElement parent)
    {
        slot.SelectedElements.Add(chosen);

        // Find the placeholder child that holds this slot (first selection)
        // or the next empty sibling placeholder (subsequent selections)
        var slotElement = FindChoiceElement(parent, slot);
        if (slotElement is not null)
        {
            slotElement.RulesElement = chosen;
            return slotElement;
        }

        // For subsequent selections: find the next null-RulesElement sibling
        // after the slot placeholder (created by ProcessSelect for multi-select)
        var slotIndex = FindChoiceElementIndex(parent, slot);
        if (slotIndex >= 0)
        {
            for (int i = slotIndex + 1; i < parent.Children.Count; i++)
            {
                if (parent.Children[i].RulesElement is null && parent.Children[i].Choice is null)
                {
                    parent.Children[i].RulesElement = chosen;
                    return parent.Children[i];
                }
            }
        }

        // Final fallback: append as new child
        var child = parent.AddChild(chosen, parent.Level);
        return child;

        static CharacterElement? FindChoiceElement(CharacterElement node, ChoiceSlot slot)
        {
            foreach (var child in node.Children)
            {
                if (ReferenceEquals(child.Choice, slot) && child.RulesElement is null)
                    return child;

                var found = FindChoiceElement(child, slot);
                if (found is not null)
                    return found;
            }
            return null;
        }

        static int FindChoiceElementIndex(CharacterElement node, ChoiceSlot slot)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                if (ReferenceEquals(node.Children[i].Choice, slot))
                    return i;
            }
            return -1;
        }
    }

    /// <summary>Get all pending (incomplete, non-optional) choice slots.</summary>
    public IEnumerable<ChoiceSlot> GetPendingChoices()
    {
        return GetAllChoices().Where(s => !s.IsComplete);
    }

    /// <summary>Get all choice slots (including completed ones).</summary>
    public IEnumerable<ChoiceSlot> GetAllChoices()
    {
        return Root.GetAllDescendants()
            .Where(ce => ce.Choice is not null)
            .Select(ce => ce.Choice!);
    }

    private bool EvaluateRequires(string? requires)
    {
        return RequiresEvaluator.Evaluate(
            requires,
            HasElement,
            HasElementOfTypeAndCategory);
    }
}
