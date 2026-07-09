using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;
using CharM.Engine.Selection;
using System.Runtime.CompilerServices;

namespace CharM.Engine.Creation;

public sealed partial class CharacterCreationWizard
{
    /// <summary>
    /// Enumerate the pending (unfilled, non-skipped, step-available) choice
    /// slots from the tree, paired with their wizard step. Shared by the
    /// label-building <see cref="GetPendingChoicesFromTree"/> and the
    /// label-free <see cref="GetPendingSlotsFromTree"/> so neither duplicates
    /// the filtering rules.
    /// </summary>
    private IEnumerable<(WizardStep Step, ChoiceSlot Slot)> EnumeratePendingSlots()
    {
        foreach (var slot in _tree.GetAllChoices())
        {
            if (slot.SelectedElements.Count >= slot.Number)
                continue;

            if (_skippedSlots.Contains(slot))
                continue;

            var step = MapTypeToStep(slot.ElementType);
            if (!IsStepAvailable(step)) continue;

            if (slot.Optional && !IsWantedOptional(slot))
                continue;

            yield return (step, slot);
        }
    }

    /// <summary>
    /// Get pending choices from the tree, including wanted optional choices
    /// (Paragon Path at level ≥ 11, Epic Destiny at level ≥ 21, Background always).
    /// </summary>
    private IReadOnlyList<PendingChoice> GetPendingChoicesFromTree()
    {
        var result = new List<PendingChoice>();
        foreach (var (step, slot) in EnumeratePendingSlots())
        {
            string desc = SlotLabel.Resolve(slot, _findById);
            result.Add(new PendingChoice(step, desc, slot));
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="GetPendingChoicesFromTree"/> but returns just the slots,
    /// skipping the per-slot <see cref="SlotLabel.Resolve"/> display-label work.
    /// The positional importer matches slots by owner/type only and calls this
    /// many times during alignment, so avoiding the label computation matters.
    /// </summary>
    internal IReadOnlyList<ChoiceSlot> GetPendingSlotsFromTree()
    {
        var result = new List<ChoiceSlot>();
        foreach (var (_, slot) in EnumeratePendingSlots())
            result.Add(slot);
        return result;
    }

    private bool IsWantedOptional(ChoiceSlot slot)
    {
        var type = slot.ElementType.ToLowerInvariant();
        return type switch
        {
            "paragon path" => Level >= 11,
            "epic destiny" => Level >= 21,
            "background" => true,
            // Freeform "details" types: alignment, gender, build hint,
            // deity & domain. The select directives that create these slots
            // are flagged Optional in the source data, but they round-trip
            // as real entries in <Level> + <RulesElementTally> when the user
            // fills them. Expose them so the positional importer can match
            // them against the file's tree (and so the wizard surfaces them
            // during normal creation).
            "alignment" or "gender" or "build" or "build suggestions"
                or "deity" or "domain" => true,
            // Optional but persistent character choices: theme, background
            // choice (sub-select within a background), pseudo class. When
            // the file has them, we must match them against their slot so
            // their grant chains fire (e.g. Werebear theme grants the
            // Werebear Starting Feature → Bear Shape power).
            "theme" or "background choice" or "pseudo class"
                or "race choice" => true,
            _ => false,
        };
    }

    private ChoiceSlot? GetPendingSlotForStep(WizardStep step)
    {
        return GetPendingChoicesFromTree()
            .FirstOrDefault(p => p.Step == step)?.Slot;
    }

    /// <summary>
    /// Find the first pending slot for this step that accepts this element.
    /// Checks type match and category match.
    /// </summary>
    private ChoiceSlot? GetPendingSlotForElement(WizardStep step, RulesElement element)
    {
        var variables = SelectVariables.Resolve(_tree, Level);
        var pending = GetPendingChoicesFromTree()
            .Where(p => p.Step == step
                && string.Equals(p.Slot.ElementType, element.Type, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Slot);

        foreach (var slot in pending)
        {
            // Slot with no category accepts any element of the right type
            if (slot.Category is null)
                return slot;
            // Slot with category — check if this element matches
            if (CategoryMatcher.Matches(slot.Category, element, variables))
                return slot;
        }
        return null;
    }

    /// <summary>
    /// Find the best slot for an element using category matching.
    /// When multiple slots of the same type exist (e.g., class Power at-will
    /// vs racial bonus Power at-will vs Spellbook Power), the slot whose
    /// category filter matches this element wins.
    /// </summary>
    private ChoiceSlot? FindBestSlotForElement(RulesElement element)
    {
        var variables = SelectVariables.Resolve(_tree, Level);
        var candidates = _tree.GetAllChoices()
            .Where(s => s.SelectedElements.Count < s.Number
                && string.Equals(s.ElementType, element.Type, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        // Multiple slots — use category matching to pick the right one
        // Priority 1: slot with category that matches this element
        var categoryMatches = candidates
            .Where(s => s.Category is not null
                && CategoryMatcher.Matches(s.Category, element, variables))
            .ToList();

        if (categoryMatches.Count == 1)
            return categoryMatches[0];

        if (categoryMatches.Count > 1)
        {
            // Multiple category matches — prefer the one with most remaining capacity
            // (fill larger slots first: class x3 before racial bonus x1)
            return categoryMatches.OrderByDescending(s => s.Remaining).First();
        }

        // Priority 2: slot with no category (accepts anything of right type)
        var noCategory = candidates.FirstOrDefault(s => s.Category is null);
        if (noCategory is not null)
            return noCategory;

        // Priority 3: first available (fallback)
        return candidates[0];
    }

    private CharacterElement? FindSlotParent(ChoiceSlot slot)
    {
        return FindParentOfSlot(_tree.Root, slot);

        static CharacterElement? FindParentOfSlot(CharacterElement node, ChoiceSlot slot)
        {
            foreach (var child in node.Children)
            {
                if (ReferenceEquals(child.Choice, slot))
                    return node;
                var found = FindParentOfSlot(child, slot);
                if (found is not null) return found;
            }
            return null;
        }
    }

    private bool IsStepAvailable(WizardStep step) => step switch
    {
        WizardStep.ParagonPath => Level >= 11,
        WizardStep.EpicDestiny => Level >= 21,
        _ => true,
    };

    /// <summary>
    /// Generate a human-readable description for a choice slot when
    /// the select directive didn't provide an explicit name.
    /// Extracts usage keywords from the category expression.
    /// </summary>
    private static string FormatChoiceDescription(ChoiceSlot slot)
    {
        if (slot.Category is not null)
        {
            // Extract meaningful terms from category like "$$CLASS,at-will,1"
            // Skip: variables ($$), numbers, internal IDs, pipe-separated option lists
            var parts = slot.Category.Split(',')
                .Select(p => p.Trim())
                .Where(p =>
                {
                    var bare = p.TrimStart('!');
                    return !bare.StartsWith("$$") && !int.TryParse(bare, out _)
                    && !bare.StartsWith("ID_", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains('|')
                    && bare.Length > 0;
                })
                .ToList();

            if (parts.Count > 0)
            {
                var qualifier = string.Join(" ", parts.Select(p =>
                {
                    bool negated = p.StartsWith('!');
                    var bare = p.TrimStart('!').Replace('-', ' ');
                    var title = System.Globalization.CultureInfo.CurrentCulture.TextInfo
                        .ToTitleCase(bare.ToLowerInvariant());
                    return negated ? $"Not {title}" : title;
                }));
                return $"Choose {qualifier} {slot.ElementType}";
            }
        }

        return $"Choose {slot.ElementType}";
    }

    /// <summary>
    /// Prefix a generated choice description with the owner element's name
    /// so "Choose Power" becomes "Channel Divinity: Choose Power".
    /// Skips owner names that are pure scaffolding (numbers, "DetailsRules",
    /// "ExpansionN", level placeholders) since those add noise without context.
    /// </summary>
    private string PrefixWithOwnerName(string description, string ownerInternalId)
    {
        var owner = _findById(ownerInternalId);
        if (owner is null) return description;

        // Filter scaffolding names that don't help the user
        if (IsScaffoldingOwnerName(owner.Name))
            return description;

        // Avoid redundant prefix when the description already contains the owner name
        if (description.Contains(owner.Name, StringComparison.OrdinalIgnoreCase))
            return description;

        return $"{owner.Name}: {description}";
    }

    private static bool IsScaffoldingOwnerName(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
            return true;

        // Pure numeric names (level markers like "1", "2", "11")
        if (int.TryParse(ownerName.Trim(), out _))
            return true;

        // "DetailsRules" container
        if (ownerName.Equals("DetailsRules", StringComparison.OrdinalIgnoreCase))
            return true;

        // "Expansion1", "Expansion2", etc. — internal grouping nodes
        if (ownerName.StartsWith("Expansion", StringComparison.OrdinalIgnoreCase)
            && ownerName.Length > "Expansion".Length
            && int.TryParse(ownerName.AsSpan("Expansion".Length), out _))
            return true;

        return false;
    }

    /// <summary>
    /// Determine which character level a choice slot belongs to.
    /// Walks the slot's owner chain looking for a Level element (ID_INTERNAL_LEVEL_N).
    /// Falls back to level 1 if no Level ancestor is found.
    /// </summary>
    internal int DetermineSlotLevel(ChoiceSlot? slot)
    {
        if (slot is null)
            return 1;

        // Slot's own Level (sourced from SelectDirective.Level) is the
        // authoritative signal. Owner elements granted at L1 often carry
        // select directives that present their slots at later levels
        // (Psionic Augmentation (Hybrid) at L1 owns the L7 Hybrid Encounter
        // Power select). The captured tree position is grant-chain nesting,
        // not acquisition level.
        if (slot.Level is int directiveLevel && directiveLevel > 0)
            return directiveLevel;

        // Check direct owner first
        if (slot.OwnerInternalId is not null && TryParseLevelId(slot.OwnerInternalId, out int directLevel))
            return directLevel;

        // Walk up the tree from the slot's parent to find a Level element ancestor
        var parent = FindSlotParent(slot);
        while (parent is not null)
        {
            if (parent.RulesElement?.InternalId is { } id && TryParseLevelId(id, out int ancestorLevel))
                return ancestorLevel;
            parent = parent.Parent;
        }

        return 1;
    }

    private static bool TryParseLevelId(string internalId, out int level)
    {
        level = 0;
        const string prefix = "ID_INTERNAL_LEVEL_";
        if (internalId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return int.TryParse(internalId.AsSpan(prefix.Length), out level);
        return false;
    }
}
