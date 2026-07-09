using CharM.Engine.CharacterModel;
using CharM.Engine.Evaluation;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;

namespace CharM.Engine.Orchestration;

public sealed partial class CharacterBuilder
{
    /// <summary>
    /// Phase 1: Process all build choices for a single level (structural directives only).
    /// Also resolves and processes the Level rules element (ID_INTERNAL_LEVEL_N) which
    /// contains core stat formulas (ability modifiers, defenses, HP, skills, etc.).
    /// </summary>
    private void ProcessLevelSkeleton(int level, IReadOnlyList<ElementChoice> choices)
    {
        // Fire any grants that were deferred to this level (Cat-N, paragon path
        // class features at L12/16/20, theme grants at later tiers, …). These
        // were collected the first time their parent element was processed.
        FireFutureLevelGrants(level);

        // First, process the Level rules element itself (contains core formulas)
        var levelElement = _findById($"ID_INTERNAL_LEVEL_{level}");
        if (levelElement is not null)
        {
            var levelCharElem = ElementTree.Root.AddChild(levelElement, level);
            ExecutePhase1(levelElement, levelCharElem, level);
        }

        // Then process the user's build choices — but skip elements already
        // processed via the Level element's grants (e.g., Level1Rules, SkillRules)
        foreach (var choice in choices)
        {
            // Level1Rules are granted by the Level element itself — skip to avoid double-processing
            if (choice.Type == "Level1Rules" || choice.Type == "Level")
                continue;

            RulesElement? element = null;
            if (choice.InternalId is not null)
                element = _findById(choice.InternalId);
            element ??= _findByNameAndType(choice.Name, choice.Type);

            if (element is null)
            {
                // Choice references an element our rules DB doesn't have
                // (CBLoader .part content, houseruled additions, etc.).
                // Synthesize a Rules-less placeholder so the element still
                // appears in the rebuilt tree (and therefore in the tally
                // + level-tree output) without firing any directives. The
                // importer has already recorded it on session.UnresolvedElements
                // so the UI can surface a ⚠.
                if (string.IsNullOrEmpty(choice.InternalId)) continue;
                element = new RulesElement
                {
                    InternalId = choice.InternalId,
                    Name = choice.Name,
                    Type = choice.Type,
                    Rules = [],
                };
            }

            var charElement = ElementTree.Root.AddChild(element, level);
            charElement.SlotOwnerInternalId = choice.SlotOwnerInternalId;
            ExecutePhase1(element, charElement, level);
        }

        // Retry deferred grants — conditional grants whose requires check
        // failed during processing may now succeed.
        ProcessDeferredGrants(level);
    }

    /// <summary>
    /// Fire grants previously deferred to this character level (recorded by
    /// ExecutePhase1 when their <c>level=N</c> was greater than the current
    /// build level). Each granted element is added to the tree and recursed
    /// into so its own directives — including further future-level grants —
    /// participate in the per-level cascade.
    /// </summary>
    private void FireFutureLevelGrants(int level)
    {
        if (!_futureLevelGrants.TryGetValue(level, out var bucket) || bucket.Count == 0)
            return;
        // Drain into a local copy: ExecutePhase1 below may add NEW entries to
        // _futureLevelGrants (granted elements with their own leveled grants).
        var pending = bucket;
        _futureLevelGrants.Remove(level);

        foreach (var (grant, parent) in pending)
        {
            var child = ElementTree.ProcessGrant(grant, parent, level);
            if (child?.RulesElement is { } granted)
                ExecutePhase1(granted, child, level);
            else if (grant.Requires is not null)
            {
                // Conditional grant: defer to standard requires-retry pipeline so
                // ProcessDeferredGrants picks it up after the rest of this level's
                // skeleton work creates whatever the requires check needed.
                _deferredGrants.Add((grant, parent, level));
            }
        }
    }

    /// <summary>
    /// Apply retraining swaps for a level: drop the old element and grant the new one.
    /// Removes the old CharacterElement from anywhere in the tree, removes its
    /// pending phase-2 entry, and processes the new element via the standard
    /// grant pipeline so its directives fire and its phase-2 work is queued.
    /// </summary>
    private void ApplyReplacements(int level, IReadOnlyList<ElementReplacement> replacements)
    {
        foreach (var swap in replacements)
        {
            // Capture the RE we're about to drop so PowerStatsBuilder can
            // still emit it as an "alternate" card. OCB keeps swapped-out
            // utility powers in <PowerStats> (the user can retrain back).
            var oldNode = ElementTree.Root.FindDescendant(swap.OldInternalId);
            var oldElement = oldNode?.RulesElement;

            DropElementById(swap.OldInternalId);

            if (oldElement is not null && swap.PreserveOld)
                _replacedElements.Add(oldElement);

            var replacement = _findById(swap.NewInternalId);
            if (replacement is null && swap.NewName is not null && swap.NewType is not null)
                replacement = _findByNameAndType(swap.NewName, swap.NewType);
            if (replacement is null)
                continue;

            var grantedChild = ElementTree.Root.AddChild(replacement, level);
            _appliedReplacements.Add(new AppliedReplacement(swap, oldElement, replacement));
            ExecutePhase1(replacement, grantedChild, level);
        }

        // Replacements may unblock deferred grants whose requires now succeed
        // (or, conversely, drop something a deferred grant relied on — those
        // simply stay deferred and will be discarded after the loop).
        ProcessDeferredGrants(level);
    }

    /// <summary>
    /// Remove an element from the character tree by InternalId. Also strips
    /// its pending phase-2 entry so its statadds / modifies don't apply after
    /// the drop. Searches the tree depth-first; stops at the first match.
    /// Returns false if the element wasn't present (silently tolerated).
    /// </summary>
    private bool DropElementById(string internalId)
    {
        var target = ElementTree.Root.FindDescendant(internalId);
        if (target is null)
            return false;

        var parent = target.Parent ?? ElementTree.Root;
        bool removed = parent.RemoveChild(internalId);

        // Strip from pending phase-2 work so dropped elements don't contribute
        // their statadds / modifies later. Match by InternalId.
        _pendingPhase2.RemoveAll(e =>
            string.Equals(e.Element.InternalId, internalId, StringComparison.OrdinalIgnoreCase));

        return removed;
    }

    /// <summary>
    /// Execute Phase 1 (skeleton) directives on an element and recursively on granted children.
    /// Collects elements for Phase 2 processing.
    /// </summary>
    private void ExecutePhase1(RulesElement element, CharacterElement parent, int characterLevel)
    {
        // Cycle guard
        if (!_visitedElements.Add(element.InternalId))
            return;

        try
        {
            _pendingPhase2.Add((element, parent, characterLevel));

            foreach (var directive in element.Rules)
            {
                if (directive.Level.HasValue && directive.Level.Value > characterLevel)
                {
                    // Defer leveled grants so that elements like Cat-3, Cat-4, … fire
                    // when the build reaches that level. Other directive types are
                    // handled in Phase 2 with maxCharLevel and don't need deferral.
                    if (directive is GrantDirective futureGrant)
                    {
                        if (!_futureLevelGrants.TryGetValue(directive.Level.Value, out var bucket))
                            _futureLevelGrants[directive.Level.Value] = bucket = new();
                        bucket.Add((futureGrant, parent));
                    }
                    continue;
                }

                switch (directive)
                {
                    case GrantDirective grant:
                        // Defer grants whose requires condition fails — a later
                        // choice may add the prerequisite element.
                        if (grant.Requires is not null
                            && !(grant.Level.HasValue && grant.Level.Value > characterLevel)
                            && !RequiresEvaluator.Evaluate(
                                grant.Requires, ElementTree.HasElement, ElementTree.HasElementOfTypeAndCategory))
                        {
                            _deferredGrants.Add((grant, parent, characterLevel));
                            break;
                        }
                        var grantedChild = ElementTree.ProcessGrant(grant, parent, characterLevel);
                        if (grantedChild?.RulesElement is { } grantedElement)
                            ExecutePhase1(grantedElement, grantedChild, characterLevel);
                        break;

                    case DropDirective drop:
                        ElementTree.ProcessDrop(drop, parent);
                        break;

                    case SelectDirective select:
                        ElementTree.ProcessSelect(select, parent);
                        break;

                    case ReplaceDirective:
                        // Retraining slot — surfaced via the wizard / script
                        // `replacements` parameter; no skeleton work to do here.
                        // We acknowledge the directive explicitly so it isn't
                        // silently swallowed, and so future wizard work can
                        // attach a ChoiceSlot in the same place.
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
    /// Retry grants whose requires condition failed during initial processing.
    /// Keeps retrying until no more progress is made (fixed-point iteration).
    /// </summary>
    private void ProcessDeferredGrants(int characterLevel)
        => Phase1Cascade.ProcessDeferredGrants(ElementTree, _deferredGrants, ExecutePhase1);
}
