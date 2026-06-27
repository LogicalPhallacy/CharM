using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;
using CharM.Engine.Selection;
using System.Runtime.CompilerServices;

namespace CharM.Engine.Export;

/// <summary>
/// Builds a correctly-nested CharacterElementTree for .dnd4e export
/// by replaying user choices through the grant/select directive chain.
///
/// Unlike the wizard (which builds the tree incrementally during choice-making)
/// or CharacterBuilder (which adds all choices flat under Root), this builder
/// places each user selection under the element whose select directive created
/// the matching choice slot — producing the nested hierarchy the CB expects.
///
/// This separation means the wizard only needs to be a "choice collector"
/// while the export tree is always structurally correct regardless of how
/// choices were gathered (interactive, script, import, future GUI).
/// </summary>
public sealed class ExportTreeBuilder
{
    private readonly Func<string, RulesElement?> _findById;
    private readonly Func<string, string, RulesElement?> _findByNameAndType;
    private CharacterElementTree _tree = null!;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(GrantDirective Grant, CharacterElement Parent, int Level)> _deferredGrants = [];

    public ExportTreeBuilder(
        Func<string, RulesElement?> findById,
        Func<string, string, RulesElement?> findByNameAndType)
    {
        _findById = findById;
        _findByNameAndType = findByNameAndType;
    }

    /// <summary>
    /// Build a correctly-nested element tree from user choices (single level).
    /// </summary>
    public CharacterElementTree Build(int level, IReadOnlyList<ElementChoice> userChoices)
    {
        return Build(new Dictionary<int, IReadOnlyList<ElementChoice>> { [level] = userChoices });
    }

    /// <summary>
    /// Build a correctly-nested element tree from per-level user choices.
    /// Each level gets its own Level element and grant chain in the tree.
    /// </summary>
    public CharacterElementTree Build(IReadOnlyDictionary<int, IReadOnlyList<ElementChoice>> choicesByLevel)
        => Build(choicesByLevel, replacements: null, sourceCharelemByInternalId: null);

    /// <summary>
    /// Build a correctly-nested element tree from per-level user choices,
    /// honoring per-level <see cref="ElementReplacement"/> swaps.
    /// </summary>
    /// <param name="choicesByLevel">User picks grouped by acquisition level.</param>
    /// <param name="replacements">
    /// Retraining swaps grouped by retrain level. The NEW element of each
    /// swap is placed under its swap-owner's <see cref="CharacterElement"/>
    /// (or under the Level node when the owner can't be found) with
    /// <see cref="CharacterElement.ReplacesCharelem"/> set so the writer
    /// emits <c>replaces="..."</c>. When the OLD element has a known
    /// source-file charelem (via <paramref name="sourceCharelemByInternalId"/>)
    /// it's used verbatim; otherwise a stable synthesized charelem is
    /// generated from the OLD InternalId via the writer's same hashing
    /// function.
    /// </param>
    /// <param name="sourceCharelemByInternalId">
    /// Optional map of element InternalId → source-file charelem (from
    /// <see cref="CharM.Engine.Creation.CharacterSession.SourceMetadata"/>).
    /// When an OLD element has no entry in this map, a deterministic
    /// charelem is synthesized from its InternalId so import re-reads
    /// the swap as the same retrain on the next round trip.
    /// </param>
    /// <param name="generateCharelem">
    /// Delegate that produces a stable charelem from an InternalId. The
    /// writer uses the same hash so synthesized charelems round-trip
    /// deterministically. Required when <paramref name="replacements"/>
    /// contains any swap whose OLD element has no source-file charelem.
    /// </param>
    public CharacterElementTree Build(
        IReadOnlyDictionary<int, IReadOnlyList<ElementChoice>> choicesByLevel,
        IReadOnlyDictionary<int, IReadOnlyList<ElementReplacement>>? replacements,
        IReadOnlyDictionary<string, string>? sourceCharelemByInternalId,
        Func<string, string>? generateCharelem = null)
    {
        _tree = new CharacterElementTree();
        _tree.ElementResolver = (name, type) =>
            _findById(name) ?? _findByNameAndType(name, type);
        _visited.Clear();
        _deferredGrants.Clear();
        int characterLevel = choicesByLevel.Count == 0 ? 1 : choicesByLevel.Keys.Max();

        foreach (var (level, userChoices) in choicesByLevel.OrderBy(kv => kv.Key))
        {
            // 1. Process Level element — creates grant chain and select slots for this level
            var levelElement = _findById($"ID_INTERNAL_LEVEL_{level}");
            if (levelElement is not null)
            {
                var levelCharElem = _tree.Root.AddChild(levelElement, level);
                ExecutePhase1(levelElement, levelCharElem, characterLevel);
            }

            // 2. Place each user choice into the correct slot in the tree
            foreach (var choice in userChoices)
            {
                if (choice.Type is "Level1Rules" or "Level")
                    continue;

                var element = ResolveChoice(choice);
                if (element is null)
                    continue;

                if (IsAlreadyInTree(element.InternalId)
                    && string.IsNullOrWhiteSpace(choice.SlotOwnerInternalId))
                    continue;

                PlaceUserChoice(element, choice.SlotOwnerInternalId, acquisitionLevel: level, characterLevel);

                if (_deferredGrants.Count > 0)
                    ProcessDeferredGrants(characterLevel);
            }

            // Retry deferred grants after each level's choices
            ProcessDeferredGrants(characterLevel);

            // 3. Apply retraining swaps for this level. Each NEW element is
            //    placed under its swap-owner with ReplacesCharelem set so
            //    the writer emits replaces="<old charelem>".
            if (replacements is not null && replacements.TryGetValue(level, out var levelReplacements))
            {
                foreach (var rep in levelReplacements)
                {
                    PlaceReplacement(rep, level, characterLevel, sourceCharelemByInternalId, generateCharelem);
                }
            }
        }

        return _tree;
    }

    /// <summary>
    /// Place an <see cref="ElementReplacement"/>'s NEW element under its
    /// swap-owner in the tree. The OLD element stays in the tree at its
    /// original acquisition level — OCB emits both for round-trip,
    /// distinguishing them via the NEW's <c>replaces=</c> attribute.
    /// </summary>
    private void PlaceReplacement(
        ElementReplacement rep,
        int retrainLevel,
        int characterLevel,
        IReadOnlyDictionary<string, string>? sourceCharelemByInternalId,
        Func<string, string>? generateCharelem)
    {
        // Empty-NEW means a drop-only retrain (user retrained out without
        // picking anything new). Nothing to add to the tree; the OLD
        // element's drop is recorded by the writer via the tally.
        if (string.IsNullOrEmpty(rep.NewInternalId)) return;

        var newElement = _findById(rep.NewInternalId);
        if (newElement is null && !string.IsNullOrEmpty(rep.NewName) && !string.IsNullOrEmpty(rep.NewType))
            newElement = _findByNameAndType(rep.NewName!, rep.NewType!);
        if (newElement is null) return;

        // If the NEW element is already in the export tree (typical for
        // round-tripped characters where the swap NEW also appears in
        // ChoiceHistory via the captured XML walk), find the existing
        // node and tag it with ReplacesCharelem rather than adding a
        // second copy. Adding a duplicate would re-run ExecutePhase1
        // and double-count stat grants.
        var existing = !string.IsNullOrEmpty(newElement.InternalId)
            ? _tree.Root.FindDescendant(newElement.InternalId)
            : null;
        if (existing is not null)
        {
            existing.ReplacesCharelem ??= ResolveOldCharelem(rep, sourceCharelemByInternalId, generateCharelem);
            return;
        }

        string? oldCharelem = ResolveOldCharelem(rep, sourceCharelemByInternalId, generateCharelem);

        // Find the swap-owner element in the tree (where the NEW should nest).
        // We look up by SwapOwnerInternalId but only accept the match when
        // it's actually located under the retrain level's subtree — owners
        // that live at an earlier level (e.g. the L1 Assassin Class element
        // for a power retrained at L11) would otherwise pull the NEW out
        // of the retrain Level's serialization scope. Fall back to placing
        // directly under the retrain Level node so the writer emits the
        // swap inside the correct <Level> block.
        CharacterElement? owner = null;
        if (!string.IsNullOrEmpty(rep.SwapOwnerInternalId))
        {
            var candidate = _tree.Root.FindDescendant(rep.SwapOwnerInternalId);
            if (candidate is not null && IsUnderLevel(candidate, retrainLevel))
                owner = candidate;
        }

        // Fall back to placing under the retrain Level node so the swap
        // still serializes in the right level bucket.
        if (owner is null)
        {
            owner = FindLevelNode(retrainLevel) ?? FindLevelNode() ?? _tree.Root;
        }

        var child = owner.AddChild(newElement, retrainLevel);
        child.ReplacesCharelem = oldCharelem;
        ExecutePhase1(newElement, child, characterLevel);
    }

    private static string? ResolveOldCharelem(
        ElementReplacement rep,
        IReadOnlyDictionary<string, string>? sourceCharelemByInternalId,
        Func<string, string>? generateCharelem)
    {
        if (string.IsNullOrEmpty(rep.OldInternalId)) return null;
        if (sourceCharelemByInternalId is not null
            && sourceCharelemByInternalId.TryGetValue(rep.OldInternalId, out var sourceCharelem))
        {
            return sourceCharelem;
        }
        return generateCharelem?.Invoke(rep.OldInternalId);
    }

    /// <summary>True when <paramref name="node"/> sits under the
    /// <c>ID_INTERNAL_LEVEL_&lt;level&gt;</c> CharacterElement.</summary>
    private static bool IsUnderLevel(CharacterElement node, int level)
    {
        string levelId = $"ID_INTERNAL_LEVEL_{level}";
        var cur = node;
        while (cur is not null)
        {
            if (string.Equals(cur.RulesElement?.InternalId, levelId, StringComparison.OrdinalIgnoreCase))
                return true;
            cur = cur.Parent;
        }
        return false;
    }

    /// <summary>Find the CharacterElement for a specific Level (e.g. "ID_INTERNAL_LEVEL_13").</summary>
    private CharacterElement? FindLevelNode(int level)
    {
        string levelId = $"ID_INTERNAL_LEVEL_{level}";
        foreach (var child in _tree.Root.Children)
        {
            if (string.Equals(child.RulesElement?.InternalId, levelId, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private RulesElement? ResolveChoice(ElementChoice choice)
    {
        RulesElement? element = null;
        if (choice.InternalId is not null)
            element = _findById(choice.InternalId);
        element ??= _findByNameAndType(choice.Name, choice.Type);

        if (element is null && !string.IsNullOrEmpty(choice.InternalId))
        {
            // Element isn't in the local rules DB (CBLoader .part content,
            // houserule additions, etc.). Synthesize a Rules-less
            // placeholder so the choice still places into the export tree
            // verbatim — losing it would break round-trip parity for
            // files that depend on out-of-DB content. The importer's
            // session.UnresolvedElements collection has the full record.
            element = new RulesElement
            {
                InternalId = choice.InternalId,
                Name = choice.Name,
                Type = choice.Type,
                Rules = [],
            };
        }

        return element;
    }

    private bool IsAlreadyInTree(string internalId)
    {
        return _tree.Root.FindDescendant(internalId) is not null;
    }

    /// <summary>
    /// Find the correct ChoiceSlot for a user selection and place it under
    /// the slot's owning parent in the tree.
    /// </summary>
    private void PlaceUserChoice(
        RulesElement element,
        string? slotOwnerInternalId,
        int acquisitionLevel,
        int characterLevel)
    {
        // Resolve $$CLASS and other variables from current tree state.
        var variables = SelectVariables.Resolve(_tree, characterLevel);

        // Find the matching slot — prefer the one whose owner matches
        var match = FindBestSlot(_tree.Root, element, variables, slotOwnerInternalId);

        CharacterElement? charElement;

        if (match is not null)
        {
            // Place under the slot's parent — this is the correct nesting
            charElement = _tree.MakeChoice(match.Value.Slot, element, match.Value.Parent);
        }
        else
        {
            // Fallback: place under the Level element (direct child)
            var levelNode = FindLevelNode();
            charElement = (levelNode ?? _tree.Root).AddChild(element, acquisitionLevel);
        }

        if (charElement is not null)
            ExecutePhase1(element, charElement, characterLevel);
    }

    /// <summary>
    /// Find the best matching ChoiceSlot for an element.
    /// Uses SlotOwnerInternalId for deterministic placement when available,
    /// falls back to category matching.
    /// </summary>
    private (ChoiceSlot Slot, CharacterElement Parent)? FindBestSlot(
        CharacterElement node, RulesElement element,
        Dictionary<string, string> variables, string? slotOwnerInternalId)
    {
        // If we know the slot owner, find that specific slot first
        if (slotOwnerInternalId is not null)
        {
            var ownerMatch = FindSlotByOwner(node, element, slotOwnerInternalId);
            if (ownerMatch is not null)
                return ownerMatch;
        }

        // Fallback: category-based matching (for choices without owner tracking)
        var categoryMatch = FindSlotInTree(node, element, variables, requireCategoryMatch: true);
        if (categoryMatch is not null)
            return categoryMatch;

        return FindSlotInTree(node, element, variables, requireCategoryMatch: false);
    }

    /// <summary>
    /// Find an unfilled slot owned by a specific element (by InternalId).
    /// </summary>
    private (ChoiceSlot Slot, CharacterElement Parent)? FindSlotByOwner(
        CharacterElement node, RulesElement element, string ownerInternalId)
    {
        foreach (var child in node.Children)
        {
            if (child.Choice is { } slot
                && slot.SelectedElements.Count < slot.Number
                && string.Equals(slot.ElementType, element.Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(slot.OwnerInternalId, ownerInternalId, StringComparison.OrdinalIgnoreCase))
            {
                return (slot, node);
            }

            var result = FindSlotByOwner(child, element, ownerInternalId);
            if (result is not null)
                return result;
        }

        return null;
    }

    private (ChoiceSlot Slot, CharacterElement Parent)? FindSlotInTree(
        CharacterElement node, RulesElement element,
        Dictionary<string, string> variables, bool requireCategoryMatch)
    {
        foreach (var child in node.Children)
        {
            if (child.Choice is { } slot
                && slot.SelectedElements.Count < slot.Number
                && string.Equals(slot.ElementType, element.Type, StringComparison.OrdinalIgnoreCase))
            {
                if (requireCategoryMatch)
                {
                    // Slot must have a category, and element must match it
                    if (slot.Category is not null
                        && CategoryMatcher.Matches(slot.Category, element, variables))
                    {
                        return (slot, node);
                    }
                }
                else
                {
                    // Accept any type-matching slot (category is null or we're in fallback)
                    if (slot.Category is null)
                        return (slot, node);
                }
            }

            // Depth-first recurse — grants chain creates the correct hierarchy
            var result = FindSlotInTree(child, element, variables, requireCategoryMatch);
            if (result is not null)
                return result;
        }

        return null;
    }

    private CharacterElement? FindLevelNode()
    {
        return _tree.Root.Children
            .FirstOrDefault(c => string.Equals(
                c.RulesElement?.Type, "Level", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Phase 1 directive execution: grants, drops, selects.
    /// Auto-fills selects that have Grant/Default attributes.
    /// </summary>
    private void ExecutePhase1(RulesElement element, CharacterElement parent, int level)
    {
        if (!_visited.Add(element.InternalId))
            return;

        try
        {
            int selectIndex = 0;
            foreach (var directive in element.Rules)
            {
                if (directive.Level.HasValue && directive.Level.Value > level)
                    continue;

                switch (directive)
                {
                    case GrantDirective grant:
                        // Defer grants whose requires condition fails — a later
                        // user choice may add the prerequisite element, allowing
                        // the grant to succeed on the retry pass.
                        if (grant.Requires is not null
                            && !(grant.Level.HasValue && grant.Level.Value > level)
                            && !RequiresEvaluator.Evaluate(
                                grant.Requires, _tree.HasElement, _tree.HasElementOfTypeAndCategory))
                        {
                            _deferredGrants.Add((grant, parent, level));
                            break;
                        }
                        var child = _tree.ProcessGrant(grant, parent, level);
                        if (child?.RulesElement is { } grantedElement)
                            ExecutePhase1(grantedElement, child, level);
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
                        AutoFillIfPossible(select, slot, parent, level);
                        break;
                }
            }
        }
        finally
        {
            _visited.Remove(element.InternalId);
        }
    }

    /// <summary>
    /// Auto-fill select directives that have a Grant attribute.
    /// Default is a UI preselection, not a persisted forced choice, so export
    /// replay must wait for the explicit imported/user choice.
    /// </summary>
    private void AutoFillIfPossible(
        SelectDirective select, ChoiceSlot slot, CharacterElement parent, int level)
    {
        var targetId = select.Grant;
        if (targetId is null) return;

        var autoElement = _findById(targetId) ??
            (select.Default is not null
                ? _findByNameAndType(select.Default, select.ElementType)
                : null);

        if (autoElement is null) return;

        var slotParent = FindSlotParent(slot) ?? parent;
        var child = _tree.MakeChoice(slot, autoElement, slotParent);
        if (child is not null)
            ExecutePhase1(autoElement, child, level);
    }

    private static string BuildDirectiveKey(CharacterElement parent, SelectDirective select, int ordinal = 0)
    {
        string owner = parent.RulesElement?.InternalId ?? "ROOT";
        int identity = RuntimeHelpers.GetHashCode(parent);
        return $"{owner}\0{identity}\0{ordinal}\0{select.ElementType}\0{select.Name}\0{select.Category}";
    }

    private CharacterElement? FindSlotParent(ChoiceSlot slot)
    {
        return Search(_tree.Root);

        CharacterElement? Search(CharacterElement node)
        {
            foreach (var child in node.Children)
            {
                if (ReferenceEquals(child.Choice, slot))
                    return node;
                var found = Search(child);
                if (found is not null) return found;
            }
            return null;
        }
    }

    /// <summary>
    /// Retry grants whose requires condition failed during initial processing.
    /// Keeps retrying until no more progress is made (fixed-point iteration).
    /// </summary>
    private void ProcessDeferredGrants(int level)
        => Phase1Cascade.ProcessDeferredGrants(_tree, _deferredGrants, ExecutePhase1);
}
