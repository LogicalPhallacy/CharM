using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;
using CharM.Engine.Selection;
using System.Runtime.CompilerServices;

namespace CharM.Engine.Creation;

public sealed partial class CharacterCreationWizard
{
    // --- Static mapping helpers ---

    /// <summary>
    /// Map an element type string to the wizard step it belongs to.
    /// Based on the ScreenOfChoice routing from the original CB.
    /// </summary>
    internal static WizardStep MapTypeToStep(string elementType) =>
        elementType.ToLowerInvariant() switch
        {
            "race" or "racial trait" or "countsasrace" => WizardStep.Race,
            "class" or "class feature" or "hybrid class" or "proficiency"
                or "class build" or "trait package" or "god fragment" or "magic item"
                or "companion" or "theme"
                => WizardStep.Class,
            "paragon path" => WizardStep.ParagonPath,
            "epic destiny" => WizardStep.EpicDestiny,
            "background" or "background choice" or "campaign setting" => WizardStep.Background,
            "skill training" => WizardStep.Skills,
            "feat" => WizardStep.Feats,
            "power" => WizardStep.Powers,
            "race ability bonus" or "ability scores" => WizardStep.AbilityScores,
            "language" => WizardStep.Race,
            _ when elementType.StartsWith("Ability Score", StringComparison.OrdinalIgnoreCase)
                => WizardStep.AbilityScores,
            _ => WizardStep.Details,
        };

    // --- Private implementation ---

    /// <summary>
    /// Type priority ordering from the original CB's type_order array.
    /// Lower index = higher priority in NextStep routing.
    /// </summary>
    private static readonly string[] TypeOrder =
    [
        "race",               // 0
        "hybrid class",       // 1
        "paragon path",       // 2
        "epic destiny",       // 3
        "class feature",      // 4
        "class",              // 5
        "class build",        // 6
        "trait package",      // 7
        "proficiency",        // 8
        "ability scores",     // 9
        "skill training",     // 10
        "racial trait",       // 11
        "race ability bonus", // 12
        "background",         // 13
        "feat",               // 14
        "power",              // 15
        "language",           // 16
    ];

    private static int GetTypeSortOrder(string type)
    {
        int idx = Array.FindIndex(TypeOrder, t =>
            string.Equals(t, type, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : TypeOrder.Length;
    }

    private void InitializeLevel()
    {
        // Process ALL level elements up to the target level
        for (int lvl = 1; lvl <= Level; lvl++)
        {
            var levelElement = _findById($"ID_INTERNAL_LEVEL_{lvl}");
            if (levelElement is not null)
            {
                var charElem = _tree.Root.AddChild(levelElement, lvl);
                ExecutePhase1(levelElement, charElem, lvl);
            }
        }
    }

    /// <summary>
    /// Phase 1 (skeleton) directive execution — grants, drops, and selects.
    /// </summary>
    private void ExecutePhase1(RulesElement element, CharacterElement parent, int characterLevel)
    {
        if (!_visitedElements.Add(element.InternalId))
            return;

        try
        {
            int selectIndex = 0;
            foreach (var directive in element.Rules)
            {
                if (directive.Level.HasValue && directive.Level.Value > characterLevel)
                    continue;

                switch (directive)
                {
                    case GrantDirective grant:
                        // Defer grants whose requires condition fails — a later
                        // user choice may add the prerequisite element.
                        if (grant.Requires is not null
                            && !(grant.Level.HasValue && grant.Level.Value > characterLevel)
                            && !RequiresEvaluator.Evaluate(
                                grant.Requires, _tree.HasElement, _tree.HasElementOfTypeAndCategory))
                        {
                            _deferredGrants.Add((grant, parent, characterLevel));
                            break;
                        }
                        var child = _tree.ProcessGrant(grant, parent, characterLevel);
                        if (child?.RulesElement is { } grantedElement)
                            ExecutePhase1(grantedElement, child, characterLevel);
                        break;

                    case DropDirective drop:
                        _tree.ProcessDrop(drop, parent);
                        break;

                    case SelectDirective select:
                        var directiveKey = BuildDirectiveKey(parent, select, selectIndex++);
                        if (!RequiresEvaluator.Evaluate(
                            select.Requires, _tree.HasElement, _tree.HasElementOfTypeAndCategory))
                            break;
                        var slot = _tree.ProcessSelect(select, parent, directiveKey);
                        RecordDefaultSuggestion(element, select);
                        AutoFillSelectIfPossible(select, slot, parent, characterLevel);
                        break;

                    case StatAddDirective statAdd when statAdd.Name.StartsWith(FreebeePrefix, StringComparison.OrdinalIgnoreCase):
                        _freebeeIds.Add(statAdd.Name.Substring(FreebeePrefix.Length));
                        break;

                    case SuggestDirective suggest:
                        // Record per-owner so the picker can flag candidates
                        // suggested by THIS slot's owner with a Recommended
                        // badge. Engine still does not auto-add anything.
                        if (!string.IsNullOrEmpty(element.InternalId))
                        {
                            if (!_suggestionsBySlotOwner.TryGetValue(element.InternalId, out var bucket))
                                _suggestionsBySlotOwner[element.InternalId] = bucket = new();
                            bucket.Add((suggest.Name, suggest.ElementType));
                        }
                        break;
                }
            }
        }
        finally
        {
            _visitedElements.Remove(element.InternalId);
        }
    }

    /// <summary>
    /// Auto-fill a SelectDirective when there is no real decision to make:
    /// <list type="bullet">
    /// <item><description><c>Grant</c> attribute set → forced engine grant, always honored regardless of <see cref="AutoFillSelectDefaults"/>.</description></item>
    /// <item><description>Candidate filter resolves to ≤ <c>Number</c> elements → the slot has no ambiguity, fill it. Gated by <see cref="AutoFillSelectDefaults"/> so the importer (which sets it false) preserves source files verbatim.</description></item>
    /// </list>
    ///
    /// <para>The previous "auto-fill the named Default" behavior silently picked
    /// one of multiple legal candidates (e.g., Eldritch Blast over Eldritch
    /// Strike for Warlock), hiding the choice from the user entirely. The new
    /// rule surfaces real multi-option selects as pending choices; the named
    /// <c>Default</c> is instead promoted to the Suggest mechanism in
    /// <see cref="RecordDefaultSuggestion"/> so the picker hoists it to a
    /// "★ Recommended" group.</para>
    ///
    /// <para>Auto-filled picks are recorded in the wizard's
    /// <c>_accumulatedChoices</c> so the snapshot's <c>CharacterBuilder</c>
    /// rebuild and the exporter's <c>ExportTreeBuilder</c> rebuild both
    /// receive them as user-style choices and produce the same tree the
    /// wizard sees.</para>
    /// </summary>
    private void AutoFillSelectIfPossible(SelectDirective select, ChoiceSlot slot,
        CharacterElement parent, int characterLevel)
    {
        // Forced engine grant — always honored.
        if (!string.IsNullOrEmpty(select.Grant))
        {
            var granted = _findById(select.Grant);
            if (granted is null) return;

            ApplyAutoFill(granted, slot, parent, characterLevel);
            return;
        }

        if (!AutoFillSelectDefaults) return;

        // No-decision case: candidate filter resolved to <= Number elements.
        // skipPrereqs:false matches what the picker UI shows the user — if
        // an element fails prereqs the user couldn't pick it either, so it
        // shouldn't count toward "no real choice" either.
        var candidates = GetCandidatesForSlot(slot, sourceFilter: null, skipPrereqs: false);

        if (candidates.Count > 0 && candidates.Count <= slot.Number)
        {
            foreach (var candidate in candidates)
                ApplyAutoFill(candidate, slot, parent, characterLevel);
            return;
        }

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(select.Default))
        {
            // Data-quality fallback: a number of rules elements declare a
            // Category that the CategoryMatcher does not resolve OCB tolerated these
            // by using Default as the de-facto "the engine should grant
            // this" hint. We do the same: when the candidate filter returns
            // zero, fall back to the Default-named element. Multi-candidate
            // slots with a Default (Eldritch Blast) are
            // unaffected — they hit the > Number branch above and are
            // surfaced as pending choices with Default promoted to a
            // Suggestion (see RecordDefaultSuggestion).
            var fallback = _findById(select.Default) ??
                _findByNameAndType(select.Default, select.ElementType);
            if (fallback is not null)
                ApplyAutoFill(fallback, slot, parent, characterLevel);
        }
    }

    /// <summary>
    /// Place an auto-fill pick into the slot, run its directives, and record
    /// it in the choice list so downstream rebuilds replay it. Bypasses
    /// PlaceInSlot's recursion (we're already inside ExecutePhase1) but
    /// preserves the same accounting.
    /// </summary>
    private void ApplyAutoFill(RulesElement element, ChoiceSlot slot,
        CharacterElement parent, int characterLevel)
    {
        var slotParent = FindSlotParent(slot) ?? parent;
        var child = _tree.MakeChoice(slot, element, slotParent);
        if (child is null) return;

        _accumulatedChoices.Add(new ElementChoice(
            element.InternalId, element.Name, element.Type, slot.OwnerInternalId));
        _choiceLevels.Add(characterLevel);

        ExecutePhase1(element, child, characterLevel);
    }

    /// <summary>
    /// When a SelectDirective has a non-empty Default name, register it as
    /// a Suggestion under the owning element. The Web UI's SelectionModal
    /// reads <c>SuggestionsBySlotOwner</c> and surfaces matching candidates
    /// in a leading "★ Recommended" group with a badge — the OCB-style
    /// "this is the default" hint, without committing the user to it.
    ///
    /// <para>The Suggest store keys by candidate <see cref="RulesElement.InternalId"/>,
    /// so we resolve the Default's name (or already-ID literal) to its
    /// real InternalId before recording.</para>
    /// </summary>
    private void RecordDefaultSuggestion(RulesElement owner, SelectDirective select)
    {
        if (string.IsNullOrEmpty(owner.InternalId)) return;
        if (string.IsNullOrWhiteSpace(select.Default)) return;

        // Resolve to an InternalId — Default may already be an internal id
        // (rules-data inconsistency: e.g. Healing Infusion uses
        // "ID_FMP_POWER_7635" while Eldritch Blast uses the display name
        // "Eldritch Blast"). Sentinel placeholders like "[Default Paragon
        // Path]" never resolve and are simply skipped.
        var resolved = _findById(select.Default) ??
            _findByNameAndType(select.Default, select.ElementType);
        if (resolved is null || string.IsNullOrEmpty(resolved.InternalId)) return;

        if (!_suggestionsBySlotOwner.TryGetValue(owner.InternalId, out var bucket))
            _suggestionsBySlotOwner[owner.InternalId] = bucket = new();

        // Avoid duplicates if the same SelectDirective is processed again
        // (rare, but rebuild paths can revisit the same element).
        foreach (var existing in bucket)
        {
            if (string.Equals(existing.InternalId, resolved.InternalId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.ElementType, select.ElementType, StringComparison.OrdinalIgnoreCase))
                return;
        }

        bucket.Add((resolved.InternalId, select.ElementType));
    }

    /// <summary>
    /// Retry grants whose requires condition failed during initial processing.
    /// Keeps retrying until no more progress is made (fixed-point iteration).
    /// </summary>
    private bool ProcessDeferredGrants(int characterLevel)
        => Phase1Cascade.ProcessDeferredGrants(_tree, _deferredGrants, ExecutePhase1);

    private void ProcessStateChanges(int characterLevel)
    {
        bool progress = true;
        int maxIterations = Math.Max(4, _deferredGrants.Count + 4);

        while (progress && maxIterations-- > 0)
        {
            progress = false;

            if (ProcessDeferredGrants(characterLevel))
                progress = true;

            if (ReconcileChoiceSlots(characterLevel))
                progress = true;
        }
    }

    private bool ReconcileChoiceSlots(int characterLevel)
    {
        var desiredSlots = new Dictionary<string, (SelectDirective Select, CharacterElement Parent)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var node in _tree.Root.GetAllDescendants())
        {
            if (!node.IsActive || node.RulesElement is not { } element)
                continue;

            foreach (var select in GetActiveSelectDirectives(element, node, characterLevel))
            {
                desiredSlots[select.Key] = (select.Directive, node);
            }
        }

        var existingSlots = new Dictionary<string, ChoiceSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in _tree.GetAllChoices())
        {
            if (string.IsNullOrWhiteSpace(slot.DirectiveKey))
                continue;
            // First-wins per directive key (matches the previous
            // GroupBy(...).ToDictionary(g => g.First()) semantics), without the
            // intermediate IGrouping allocations — this runs on the full tree
            // after every pick.
            existingSlots.TryAdd(slot.DirectiveKey!, slot);
        }

        bool changed = false;

        foreach (var slot in existingSlots.Values)
        {
            if (slot.DirectiveKey is null || desiredSlots.ContainsKey(slot.DirectiveKey))
                continue;

            // Don't remove slots after the user has already filled them; at that
            // point the selected feature/power is part of the character state.
            if (slot.SelectedElements.Count > 0)
                continue;

            if (_tree.RemoveChoiceSlot(slot))
            {
                _skippedSlots.Remove(slot);
                changed = true;
            }
        }

        foreach (var (key, definition) in desiredSlots)
        {
            if (existingSlots.ContainsKey(key))
                continue;

            var slot = _tree.ProcessSelect(definition.Select, definition.Parent, key);
            if (definition.Parent.RulesElement is { } definitionOwner)
                RecordDefaultSuggestion(definitionOwner, definition.Select);
            AutoFillSelectIfPossible(definition.Select, slot, definition.Parent, characterLevel);
            changed = true;
        }

        if (changed)
            InvalidateCandidateCache();

        return changed;
    }

    private IEnumerable<(string Key, SelectDirective Directive)> GetActiveSelectDirectives(
        RulesElement element,
        CharacterElement parent,
        int characterLevel)
    {
        int selectIndex = 0;
        foreach (var directive in element.Rules)
        {
            if (directive is not SelectDirective select)
                continue;

            string key = BuildDirectiveKey(parent, select, selectIndex++);

            if (directive.Level.HasValue && directive.Level.Value > characterLevel)
                continue;

            if (!RequiresEvaluator.Evaluate(
                select.Requires,
                _tree.HasElement,
                _tree.HasElementOfTypeAndCategory,
                characterLevel))
            {
                continue;
            }

            yield return (key, select);
        }
    }

    private static string BuildDirectiveKey(CharacterElement parent, SelectDirective select, int ordinal = 0)
    {
        string owner = parent.RulesElement?.InternalId ?? "ROOT";
        int identity = RuntimeHelpers.GetHashCode(parent);
        return $"{owner}\0{identity}\0{ordinal}\0{select.ElementType}\0{select.Name}\0{select.Category}";
    }
}
