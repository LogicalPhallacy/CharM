using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;
using CharM.Engine.Selection;
using System.Runtime.CompilerServices;

namespace CharM.Engine.Creation;

public sealed partial class CharacterCreationWizard
{
    public IReadOnlyList<PendingChoice> GetPendingChoices()
    {
        var result = new List<PendingChoice>();

        if (!_scoresSet)
        {
            result.Add(new PendingChoice(
                WizardStep.AbilityScores,
                "Set base ability scores",
                new ChoiceSlot { ElementType = "Ability Scores" }));
        }

        result.AddRange(GetPendingChoicesFromTree());
        return result;
    }

    /// <summary>Make a selection for the current step's pending slot.</summary>
    public void MakeChoice(RulesElement element)
    {
        var fullElement = _findById(element.InternalId) ?? element;

        // Use the current pending slot — what the wizard is presenting.
        // The script lists choices in wizard presentation order, so the
        // first pending slot of the right type IS the correct slot.
        // Only fall back to category matching if the pending slot doesn't
        // accept this element (type mismatch).
        var slot = GetPendingSlotForStep(CurrentStep);
        if (slot is not null
            && !string.Equals(slot.ElementType, fullElement.Type, StringComparison.OrdinalIgnoreCase))
        {
            slot = GetPendingSlotForElement(CurrentStep, fullElement);
        }
        slot ??= FindBestSlotForElement(fullElement);

        CharacterElement? charElement;
        string? slotOwner = null;

        if (slot is not null)
        {
            slotOwner = slot.OwnerInternalId;
            var parent = FindSlotParent(slot) ?? _tree.Root;
            charElement = _tree.MakeChoice(slot, fullElement, parent);
        }
        else
        {
            charElement = _tree.Root.AddChild(fullElement, Level);
        }

        // Snapshot choice-list size BEFORE the cascade so any auto-fills
        // appended by ExecutePhase1 land AFTER the parent in the list.
        // We insert the parent at the saved index after ExecutePhase1 returns;
        // every choice the cascade pushed is therefore positioned correctly
        // for downstream replay (CharacterBuilder, ExportTreeBuilder), which
        // process choices in list order and need the granting parent to be
        // present in the tree before its cascaded picks fire their directives.
        int insertIndex = _accumulatedChoices.Count;

        if (charElement is not null)
            ExecutePhase1(fullElement, charElement, Level);

        ProcessStateChanges(Level);

        _accumulatedChoices.Insert(insertIndex, new ElementChoice(
            fullElement.InternalId, fullElement.Name, fullElement.Type, slotOwner));
        _choiceLevels.Insert(insertIndex, DetermineSlotLevel(slot));

        InvalidateCandidateCache();
    }

    /// <summary>
    /// Apply an element as a detached root-level grant — used for OCB
    /// "Grabbag" entries (Inherent Bonuses etc.)
    /// that the source file force-grants outside the normal Level subtree.
    /// The element is added directly under the tree root and its grant chain
    /// is executed so any cascading statadds / sub-grants take effect.
    /// Does NOT register in the choice history (these aren't user picks).
    /// </summary>
    /// <param name="atLevel">
    /// Optional explicit acquisition level. When omitted (or 0), defaults to
    /// the wizard's current <see cref="Level"/>. Specify when force-granting
    /// an element that the source file recorded at a particular character
    /// level (e.g. a deferred Power pick that originated under Level 1 in
    /// the source) so the export emits it under the right &lt;Level&gt; bucket.
    /// </param>
    public void AddFreeGrant(RulesElement element, int atLevel = 0, string? slotOwnerInternalId = null)
    {
        var fullElement = _findById(element.InternalId) ?? element;
        int lvl = atLevel > 0 ? atLevel : Level;
        var charElement = _tree.Root.AddChild(fullElement, lvl);
        if (charElement is not null)
            charElement.SlotOwnerInternalId = slotOwnerInternalId;
        if (charElement is not null)
            ExecutePhase1(fullElement, charElement, lvl);

        ProcessStateChanges(Level);
        _grabbagGrants.Add(new ElementChoice(
            fullElement.InternalId, fullElement.Name, fullElement.Type,
            slotOwnerInternalId, AcquiredAtLevel: lvl));
        InvalidateCandidateCache();
    }

    /// <summary>Detached grants applied via <see cref="AddFreeGrant"/> (Grabbag entries).</summary>
    public IReadOnlyList<ElementChoice> GrabbagGrants => _grabbagGrants;

    /// <summary>Set ability scores.</summary>
    public void SetAbilityScores(AbilityScoreSet scores)
    {
        _abilityScores = scores;
        _scoresSet = true;
        ProcessStateChanges(Level);
        InvalidateCandidateCache();
    }

    /// <summary>
    /// Make a selection for a specific slot identified by its owner's InternalId.
    /// Used by script mode when the script tags which slot a choice belongs to.
    /// </summary>
    public void MakeChoiceForSlot(RulesElement element, string slotOwnerInternalId)
    {
        MakeChoiceForSlot(element, new ChoiceSlot
        {
            ElementType = element.Type,
            OwnerInternalId = slotOwnerInternalId,
        });
    }

    /// <summary>
    /// Make a selection for a specific choice slot.
    /// Uses the slot's directive key/category/name to avoid misplacing same-type
    /// powers or feats that share the same owner element.
    /// </summary>
    public void MakeChoiceForSlot(RulesElement element, ChoiceSlot requestedSlot)
    {
        var fullElement = _findById(element.InternalId) ?? element;

        var slot = FindMatchingSlot(requestedSlot, fullElement);

        if (slot is null)
        {
            // Slot not found yet — defer and retry after more grants cascade
            _deferredChoices.Add((fullElement, requestedSlot));
            return;
        }

        PlaceInSlot(fullElement, slot);
    }

    /// <summary>Retry deferred choices whose target slots may now exist.</summary>
    public void ProcessDeferredChoices()
    {
        bool progress = true;
        int maxIter = _deferredChoices.Count + 1;
        while (progress && _deferredChoices.Count > 0 && maxIter-- > 0)
        {
            progress = false;
            for (int i = _deferredChoices.Count - 1; i >= 0; i--)
            {
                var (element, slotTemplate) = _deferredChoices[i];
                var slot = FindMatchingSlot(slotTemplate, element);

                if (slot is not null)
                {
                    _deferredChoices.RemoveAt(i);
                    PlaceInSlot(element, slot);
                    progress = true;
                }
            }
        }

        // Any remaining deferred choices — fall back to normal placement
        foreach (var (element, _) in _deferredChoices)
            MakeChoice(element);
        _deferredChoices.Clear();
    }

    private ChoiceSlot? FindMatchingSlot(ChoiceSlot requestedSlot, RulesElement element)
    {
        var candidates = _tree.GetAllChoices()
            .Where(s => s.SelectedElements.Count < s.Number
                && string.Equals(s.ElementType, element.Type, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (requestedSlot.DirectiveKey is not null)
        {
            var byDirective = candidates.FirstOrDefault(s =>
                string.Equals(s.DirectiveKey, requestedSlot.DirectiveKey, StringComparison.OrdinalIgnoreCase));
            if (byDirective is not null)
                return byDirective;
        }

        if (requestedSlot.OwnerInternalId is not null)
        {
            var byOwnerAndShape = candidates.FirstOrDefault(s =>
                string.Equals(s.OwnerInternalId, requestedSlot.OwnerInternalId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Category, requestedSlot.Category, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Name, requestedSlot.Name, StringComparison.OrdinalIgnoreCase));
            if (byOwnerAndShape is not null)
                return byOwnerAndShape;

            var byOwnerAndCategory = candidates.FirstOrDefault(s =>
                string.Equals(s.OwnerInternalId, requestedSlot.OwnerInternalId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Category, requestedSlot.Category, StringComparison.OrdinalIgnoreCase));
            if (byOwnerAndCategory is not null)
                return byOwnerAndCategory;

            var byOwner = candidates.FirstOrDefault(s =>
                string.Equals(s.OwnerInternalId, requestedSlot.OwnerInternalId, StringComparison.OrdinalIgnoreCase));
            if (byOwner is not null)
                return byOwner;
        }

        return candidates.FirstOrDefault(s =>
            string.Equals(s.Category, requestedSlot.Category, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Name, requestedSlot.Name, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s =>
                string.Equals(s.Category, requestedSlot.Category, StringComparison.OrdinalIgnoreCase));
    }

    private void PlaceInSlot(RulesElement element, ChoiceSlot slot)
    {
        var parent = FindSlotParent(slot) ?? _tree.Root;
        var charElement = _tree.MakeChoice(slot, element, parent);

        // Same insert-at-saved-index pattern as MakeChoice — keeps cascaded
        // auto-fills positioned after their owning parent in the choice list
        // for downstream replay correctness.
        int insertIndex = _accumulatedChoices.Count;

        if (charElement is not null)
            ExecutePhase1(element, charElement, Level);

        ProcessStateChanges(Level);

        _accumulatedChoices.Insert(insertIndex, new ElementChoice(
            element.InternalId, element.Name, element.Type, slot.OwnerInternalId));
        _choiceLevels.Insert(insertIndex, DetermineSlotLevel(slot));

        InvalidateCandidateCache();
    }

    /// <summary>Mark the current step's pending slot as skipped (unfillable with current sources).</summary>
    public void SkipCurrentSlot()
    {
        var slot = GetPendingSlotForStep(CurrentStep);
        if (slot is not null)
        {
            _skippedSlots.Add(slot);
            InvalidateCandidateCache();
        }
    }

    /// <summary>Mark a specific slot as skipped.</summary>
    public void SkipSlot(ChoiceSlot slot)
    {
        _skippedSlots.Add(slot);
        InvalidateCandidateCache();
    }

    /// <summary>Advance to the next step that needs input.</summary>
    public WizardStep NextStep()
    {
        // Priority 1: Ability scores not set
        if (!_scoresSet)
        {
            CurrentStep = WizardStep.AbilityScores;
            return CurrentStep;
        }

        // Priority 2: Find earliest pending choice by type_order
        var earliest = GetPendingChoicesFromTree()
            .MinBy(p => GetTypeSortOrder(p.Slot.ElementType));

        if (earliest is not null)
        {
            CurrentStep = earliest.Step;
            return CurrentStep;
        }

        CurrentStep = WizardStep.Complete;
        return CurrentStep;
    }

    /// <summary>Check if all mandatory choices are made (excluding skipped slots).</summary>
    public bool IsComplete =>
        _scoresSet && !_tree.GetPendingChoices()
            .Any(s => !_skippedSlots.Contains(s) && IsStepAvailable(MapTypeToStep(s.ElementType)));

    /// <summary>Get the computed character after all choices are made.</summary>
    public CharacterBuildResult Build(
        IReadOnlyList<ElementChoice>? equippedItems = null,
        IReadOnlyList<ElementChoice>? additionalElements = null,
        IReadOnlyDictionary<int, IReadOnlyList<ElementReplacement>>? replacements = null,
        IReadOnlyList<ElementChoice>? inventoryDirectiveItems = null)
    {
        if (!IsComplete)
            return new CharacterBuildResult(false, Error: "Not all mandatory choices have been made.");

        try
        {
            var builder = new CharacterBuilder(_findById, _findByNameAndType);

            var baseScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (_abilityScores is not null)
            {
                baseScores["Strength"] = _abilityScores[Ability.Strength];
                baseScores["Constitution"] = _abilityScores[Ability.Constitution];
                baseScores["Dexterity"] = _abilityScores[Ability.Dexterity];
                baseScores["Intelligence"] = _abilityScores[Ability.Intelligence];
                baseScores["Wisdom"] = _abilityScores[Ability.Wisdom];
                baseScores["Charisma"] = _abilityScores[Ability.Charisma];
            }

            // Partition choices by their tracked level
            var buildChoices = new Dictionary<int, IReadOnlyList<ElementChoice>>();
            var choicesByLevel = GetChoicesByLevel();
            for (int lvl = 1; lvl <= Level; lvl++)
            {
                buildChoices[lvl] = choicesByLevel.TryGetValue(lvl, out var list)
                    ? list
                    : (IReadOnlyList<ElementChoice>)Array.Empty<ElementChoice>();
            }

            builder.Build(baseScores, buildChoices,
                elementTally: MergeElementTally(additionalElements, _grabbagGrants),
                equippedItems: equippedItems,
                replacements: replacements,
                inventoryDirectiveItems: inventoryDirectiveItems);
            return new CharacterBuildResult(true, Builder: builder);
        }
        catch (Exception ex)
        {
            return new CharacterBuildResult(false, Error: ex.Message);
        }
    }

    // --- Internal accessors for testing ---

    internal CharacterElementTree ElementTree => _tree;

    private static IReadOnlyList<ElementChoice>? MergeElementTally(
        IReadOnlyList<ElementChoice>? primary,
        IReadOnlyList<ElementChoice>? extras)
    {
        if (extras is null || extras.Count == 0) return primary;
        if (primary is null || primary.Count == 0) return extras;
        var merged = new List<ElementChoice>(primary.Count + extras.Count);
        merged.AddRange(primary);
        merged.AddRange(extras);
        return merged;
    }
}
