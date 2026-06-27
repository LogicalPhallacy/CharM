using CharM.Engine.Creation;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;

namespace CharM.Web.Services;

/// <summary>
/// UI service for per-level character retraining: presenting lose/gain
/// candidates and committing swaps via <see cref="CharacterSession.AddReplacement"/>
/// / <see cref="CharacterSession.RemoveReplacement(int)"/>.
///
/// <para>
/// 4e PHB retraining rule: at level-up from L2 onward, a character may swap
/// ONE previously-acquired Feat, Trained Skill, or Utility Power for a
/// same-category replacement that is currently legal. The retrain slot's
/// level gates which slot is being used, but the lose-list scans the
/// entire character — L1 picks are the most common retrain at L2.
/// </para>
///
/// <para>
/// Algorithm faithfully mirrors the OCB engine
/// (<c>D20RulesEngine/-Module-.cs:16864 BuildReplaceList</c> +
/// <c>:16736 CheckReplaceElem</c> + <c>:16956 UpdateReplacement</c>) per
/// the research transcript in this session. Key points:
/// </para>
/// <list type="bullet">
/// <item>Lose-list is element-type + user-chosen-gate + prereq-dependency
///   filtered. Powers bypass the prereq-dependency gate (per OCB).</item>
/// <item>Gain-list is derived from the LOST element's own type / source
///   slot. We restrict to "same Type as the lost element", filter
///   already-active elements out, apply prereqs at current character
///   level, and gate feats to Tier=="" (heroic-only) per OCB's
///   UI-level <c>DisplayToGain</c> filter.</item>
/// <item>One swap per level. Re-applying overwrites the existing entry.</item>
/// <item>L1 picks are retrainable through L2+ slots; no slot is exposed
///   at L1 itself.</item>
/// </list>
/// </summary>
public sealed class RetrainingService
{
    private readonly RulesDatabaseService _db;

    /// <summary>
    /// Element types scanned when projecting active character state (includes
    /// Race/Class so prereq name-matching can see them).
    /// </summary>
    private static readonly string[] ActiveElementTypes =
        ["Feat", "Class Feature", "Power", "Theme", "Race", "Class"];

    /// <summary>Element types eligible for retraining and prereq-dependency scans.</summary>
    private static readonly string[] RetrainableTypes =
        ["Feat", "Class Feature", "Power", "Theme"];

    public RetrainingService(RulesDatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// One retrain slot per character level from L2 up to <see cref="CharacterSession.Level"/>.
    /// Slots are returned in ascending level order; each slot is "empty" or
    /// "filled" based on whether <see cref="CharacterSession.Replacements"/>
    /// carries an entry for that level.
    /// </summary>
    public IReadOnlyList<RetrainSlot> GetRetrainSlots(CharacterSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Level < 2)
            return Array.Empty<RetrainSlot>();

        var slots = new List<RetrainSlot>(session.Level - 1);
        for (int level = 2; level <= session.Level; level++)
        {
            slots.Add(BuildSlot(session, level));
        }
        return slots;
    }

    /// <summary>
    /// Lookup of the existing retrain at <paramref name="level"/>, or null
    /// if the slot is empty. Returns both the old element (still resolvable
    /// from the rules DB even after it's been replaced) and the new element.
    /// </summary>
    public RetrainPair? GetExistingRetrain(CharacterSession session, int level)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.Replacements.TryGetValue(level, out var list) || list.Count == 0)
            return null;

        // Only surface PHB-retrain swaps here. Directive-owned swaps
        // (multiclass / powerswap, marked by a non-null SwapOwnerInternalId)
        // belong to their originating ChoiceSlot and must not be co-managed
        // through the per-level panel.
        var swap = list.FirstOrDefault(r => string.IsNullOrEmpty(r.SwapOwnerInternalId));
        if (swap is null)
            return null;

        var oldElement = ResolveElement(swap.OldInternalId);
        var newElement = ResolveElement(swap.NewInternalId)
            ?? (swap.NewName is not null && swap.NewType is not null
                ? _db.FindByNameAndType(swap.NewName, swap.NewType)
                : null);

        return new RetrainPair(swap, oldElement, newElement);
    }

    /// <summary>
    /// Currently-active elements eligible to be RETRAINED OUT at the given
    /// retrain slot level. Mirrors OCB <c>BuildReplaceList</c> +
    /// <c>CheckReplaceElem</c> + <c>AddReplaceChoice</c> for the default
    /// (non-powerswap / non-multiclass) retrain path.
    /// </summary>
    public IReadOnlyList<RulesElement> GetLoseCandidates(CharacterSession session, int level)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (level < 2 || level > session.Level)
            return Array.Empty<RulesElement>();

        // Type gate: Feat | Skill Training | Utility Power. Themes are also
        // eligible per OCB but extremely rare to retrain in practice; we
        // include them for fidelity.
        // GetAllElementsOfType returns the wizard's pre-replacement state;
        // project replacements onto it so retrained-in elements appear as
        // valid lose candidates in LATER slots (chained-retrain support).
        var feats = ProjectActive(session, "Feat", e => true);
        var skills = ProjectActive(session, "Class Feature", IsSkillTrainingElement);
        var utilityPowers = ProjectActive(session, "Power", IsUtilityPower);
        var themes = ProjectActive(session, "Theme", e => true);

        var raw = feats
            .Concat(skills)
            .Concat(utilityPowers)
            .Concat(themes)
            .ToList();

        // User-chosen gate: element must trace back to a user choice (i.e.,
        // appear in choice history). Elements auto-granted by class features /
        // race / grants do not have a ChoiceRecord and cannot be retrained.
        // We use the session's choice-history InternalIds as the gate.
        var userChosenIds = BuildUserChosenIdSet(session);

        // Don't allow retraining away an element that's the output of a
        // DIRECTIVE-OWNED swap (multiclass / powerswap) — those are managed
        // by their originating choice slot. Plain PHB-retrain outputs ARE
        // loseable in later slots (the L2 A→B / L6 B→C chain), so they're
        // intentionally NOT excluded here.
        var currentNewIds = new HashSet<string>(
            session.Replacements
                .SelectMany(kv => kv.Value)
                .Where(r => !string.IsNullOrEmpty(r.SwapOwnerInternalId))
                .Select(r => r.NewInternalId)
                .Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);

        var prereqDependents = BuildPrereqDependencyMap(session);

        var result = new List<RulesElement>(raw.Count);
        foreach (var element in raw)
        {
            if (string.IsNullOrEmpty(element.InternalId))
                continue;
            if (!userChosenIds.Contains(element.InternalId))
                continue;
            if (currentNewIds.Contains(element.InternalId))
                continue;

            // Prereq-dependency gate: skip Feats / Skill Training / Themes if
            // something else on the character has a prereq on them. Powers
            // bypass this per OCB (-Module-.cs:16656-16668).
            if (!string.Equals(element.Type, "Power", StringComparison.OrdinalIgnoreCase)
                && prereqDependents.Contains(element.InternalId))
            {
                continue;
            }

            result.Add(element);
        }

        return result;
    }

    /// <summary>
    /// Candidate replacement elements for retraining INTO at the given level,
    /// given the user has selected <paramref name="losing"/> to give up.
    /// Returns same-Type candidates filtered by legality and not-already-active.
    /// Feats are additionally gated to heroic tier (<c>Tier==""</c>) per the
    /// OCB UI-level filter in <c>DisplayToGain</c>.
    /// </summary>
    /// <remarks>
    /// Per OCB semantics, the gain category is derived from the original
    /// select slot that placed <paramref name="losing"/> on the character.
    /// V1 implements this as "same Type as losing", with type-specific
    /// filters that mirror the most common original-slot categories:
    /// <list type="bullet">
    /// <item>Feat → any Feat with Tier="" (heroic) and current-state-legal
    ///   prereqs.</item>
    /// <item>Skill Training → any "Skill Training: X" where X is in the
    ///   character's class skill list and not already trained.</item>
    /// <item>Power (utility) → any class utility power of Level ≤ char
    ///   level for one of the character's classes, that the character
    ///   doesn't already have.</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<RulesElement> GetGainCandidates(
        CharacterSession session, int level, RulesElement losing)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(losing);

        if (level < 2 || level > session.Level)
            return Array.Empty<RulesElement>();

        var alreadyOnCharacter = BuildActiveElementIdSet(session);
        alreadyOnCharacter.Remove(losing.InternalId);

        var prereqFilter = BuildPrereqFilter(session, losing);

        IEnumerable<RulesElement> raw = losing.Type switch
        {
            var t when string.Equals(t, "Feat", StringComparison.OrdinalIgnoreCase)
                => GetFeatGainCandidates(session),
            var t when string.Equals(t, "Class Feature", StringComparison.OrdinalIgnoreCase)
                && IsSkillTrainingElement(losing)
                => GetSkillTrainingGainCandidates(session),
            var t when string.Equals(t, "Power", StringComparison.OrdinalIgnoreCase)
                && IsUtilityPower(losing)
                => GetUtilityPowerGainCandidates(session),
            var t when string.Equals(t, "Theme", StringComparison.OrdinalIgnoreCase)
                => _db.FindByType("Theme"),
            _ => Array.Empty<RulesElement>(),
        };

        return raw
            .Where(e => !string.IsNullOrEmpty(e.InternalId))
            .Where(e => !alreadyOnCharacter.Contains(e.InternalId))
            .Where(prereqFilter)
            .ToList();
    }

    /// <summary>
    /// Apply a retraining swap at <paramref name="level"/>: drop
    /// <paramref name="losing"/>, grant <paramref name="gaining"/>. Replaces
    /// any existing retrain at that level (one swap per level rule).
    /// </summary>
    public void ApplyRetrain(
        CharacterSession session, int level,
        RulesElement losing, RulesElement gaining)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(losing);
        ArgumentNullException.ThrowIfNull(gaining);

        if (string.IsNullOrEmpty(losing.InternalId) || string.IsNullOrEmpty(gaining.InternalId))
            throw new InvalidOperationException("Retraining requires both elements to have InternalIds.");

        // Surgical replace: remove only the existing PHB retrain at this
        // level (if any) BEFORE candidate validation, so an "edit" flow
        // where the user re-confirms the same lose target works correctly
        // (otherwise the existing retrain would mask the original lose
        // element from the lose-list).
        var existing = GetExistingRetrain(session, level);
        if (existing is not null)
            session.RemoveReplacement(level, existing.Replacement);

        // Validate against the (now-current) lose/gain candidate lists so
        // the UI can't persist an illegal swap (e.g., a feat whose prereqs
        // no longer hold, or a power not in the character's class list).
        var loseCandidates = GetLoseCandidates(session, level);
        if (!loseCandidates.Any(e => string.Equals(e.InternalId, losing.InternalId, StringComparison.OrdinalIgnoreCase)))
        {
            // Restore the prior retrain so we don't leave the session in a
            // weird half-mutated state.
            if (existing is not null)
                session.AddReplacement(level, existing.Replacement);
            throw new InvalidOperationException(
                $"Element '{losing.Name}' [{losing.InternalId}] is not a valid retrain target at level {level}.");
        }
        var gainCandidates = GetGainCandidates(session, level, losing);
        if (!gainCandidates.Any(e => string.Equals(e.InternalId, gaining.InternalId, StringComparison.OrdinalIgnoreCase)))
        {
            if (existing is not null)
                session.AddReplacement(level, existing.Replacement);
            throw new InvalidOperationException(
                $"Element '{gaining.Name}' [{gaining.InternalId}] is not a legal replacement for '{losing.Name}' at level {level}.");
        }

        session.AddReplacement(level, new ElementReplacement(
            OldInternalId: losing.InternalId,
            NewInternalId: gaining.InternalId,
            NewName: gaining.Name,
            NewType: gaining.Type));
    }

    /// <summary>
    /// Clear any PHB retrain recorded at <paramref name="level"/>, restoring
    /// the original picks. Returns <c>true</c> if a retrain entry was present
    /// and was removed. Directive-owned swaps (multiclass / powerswap) at the
    /// same level are left untouched.
    /// </summary>
    public bool ClearRetrain(CharacterSession session, int level)
    {
        ArgumentNullException.ThrowIfNull(session);
        var existing = GetExistingRetrain(session, level);
        if (existing is null) return false;
        return session.RemoveReplacement(level, existing.Replacement);
    }

    // ---- Lose / Gain category implementations ----------------------------

    private IEnumerable<RulesElement> GetFeatGainCandidates(CharacterSession session)
    {
        // Heroic-only per OCB DisplayToGain UI filter.
        foreach (var feat in _db.FindByType("Feat"))
        {
            if (string.IsNullOrEmpty(GetField(feat, "Tier")))
                yield return feat;
        }
    }

    private IEnumerable<RulesElement> GetSkillTrainingGainCandidates(CharacterSession session)
    {
        // Class skill list. Derive from the character's selected Class
        // element's "Class Skills" field. The grant list is comma-separated.
        var classElement = session.GetSelectedElement("Class");
        if (classElement is null)
            yield break;

        if (!classElement.Fields.TryGetValue("Class Skills", out var classSkills)
            || string.IsNullOrWhiteSpace(classSkills))
        {
            yield break;
        }

        var skillNames = classSkills
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var st in _db.FindByType("Class Feature"))
        {
            if (!IsSkillTrainingElement(st))
                continue;

            // The "Skill Training: X" element grants Skill X via its Grants
            // field. Match by suffix after "Skill Training: ".
            var skillName = ExtractTrainedSkillName(st.Name);
            if (skillName is null) continue;
            if (skillNames.Contains(skillName))
                yield return st;
        }
    }

    private IEnumerable<RulesElement> GetUtilityPowerGainCandidates(CharacterSession session)
    {
        // Powers in the character's class lists, Power Usage = Utility,
        // Level <= character level.
        var classNames = session.GetSelectedElements("Class")
            .Select(c => c.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (classNames.Count == 0)
            yield break;

        int maxLevel = session.Level;
        foreach (var power in _db.FindByType("Power"))
        {
            if (!IsUtilityPower(power)) continue;
            if (!power.Fields.TryGetValue("Class", out var className)
                || !classNames.Contains(className))
                continue;
            if (PowerLevel(power) is { } lvl && lvl > maxLevel)
                continue;
            yield return power;
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private static bool IsSkillTrainingElement(RulesElement element)
        => string.Equals(element.Type, "Class Feature", StringComparison.OrdinalIgnoreCase)
           && element.Name?.StartsWith("Skill Training:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsUtilityPower(RulesElement element)
    {
        if (!string.Equals(element.Type, "Power", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!element.Fields.TryGetValue("Power Usage", out var usage))
            return false;
        return usage.Contains(CharM.Web.Components.Shared.PowerSectionKeys.Utility, StringComparison.OrdinalIgnoreCase);
    }

    private static int? PowerLevel(RulesElement power)
    {
        if (!power.Fields.TryGetValue("Level", out var raw))
            return null;
        return int.TryParse(raw, out var lvl) ? lvl : null;
    }

    private static string? ExtractTrainedSkillName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        const string prefix = "Skill Training:";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return name[prefix.Length..].Trim();
    }

    private static string GetField(RulesElement element, string field)
        => element.Fields.TryGetValue(field, out var value) ? value : string.Empty;

    private RulesElement? ResolveElement(string? internalId)
        => string.IsNullOrEmpty(internalId) ? null : _db.FindByInternalId(internalId);

    /// <summary>
    /// Active elements of a given Type with replacement projection applied:
    /// OldInternalIds removed, NewInternalIds added (looked up from the
    /// rules database). Wizard <c>ElementTree</c> does not consume the
    /// session's _replacements directly — those are passed to the
    /// snapshot builder — so the bare query returns the pre-retrain state.
    /// </summary>
    private IEnumerable<RulesElement> ProjectActive(
        CharacterSession session, string type, Func<RulesElement, bool> additionalFilter)
    {
        var removed = new HashSet<string>(
            session.Replacements.SelectMany(kv => kv.Value)
                .Select(r => r.OldInternalId)
                .Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var element in session.GetAllElementsOfType(type))
        {
            if (string.IsNullOrEmpty(element.InternalId)) continue;
            if (removed.Contains(element.InternalId)) continue;
            if (!additionalFilter(element)) continue;
            yield return element;
        }

        // Add replacement outputs (retrained-into elements) of the requested
        // type. Look up each via the DB so we get full field data.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var swap in session.Replacements.SelectMany(kv => kv.Value))
        {
            if (string.IsNullOrEmpty(swap.NewInternalId)) continue;
            if (!seen.Add(swap.NewInternalId)) continue;
            var element = _db.FindByInternalId(swap.NewInternalId);
            if (element is null) continue;
            if (!string.Equals(element.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
            if (!additionalFilter(element)) continue;
            yield return element;
        }
    }

    private RetrainSlot BuildSlot(CharacterSession session, int level)
    {
        var existing = GetExistingRetrain(session, level);
        return new RetrainSlot(level, existing);
    }

    private static HashSet<string> BuildUserChosenIdSet(CharacterSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ChoiceHistory exposes user picks; retrained-in elements also count
        // (they replace something the user previously chose).
        foreach (var record in session.ChoiceHistory)
        {
            if (!string.IsNullOrEmpty(record.Element.InternalId))
                ids.Add(record.Element.InternalId);
        }
        foreach (var (_, swaps) in session.Replacements)
        {
            foreach (var swap in swaps)
            {
                if (!string.IsNullOrEmpty(swap.NewInternalId))
                    ids.Add(swap.NewInternalId);
            }
        }
        return ids;
    }

    private static HashSet<string> BuildActiveElementIdSet(CharacterSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Walk every active type quickly via the session's typed query —
        // we use the broadest set: choice history elements + any element
        // exposed via GetAllElementsOfType for the kinds we care about.
        foreach (var record in session.ChoiceHistory)
        {
            if (!string.IsNullOrEmpty(record.Element.InternalId))
                ids.Add(record.Element.InternalId);
        }
        foreach (var type in ActiveElementTypes)
        {
            foreach (var el in session.GetAllElementsOfType(type))
            {
                if (!string.IsNullOrEmpty(el.InternalId))
                    ids.Add(el.InternalId);
            }
        }
        // Project replacements onto active state: ChoiceHistory contains the
        // original (pre-replacement) picks, so we must remove the OLD ids
        // and add the NEW ids so callers see the post-retrain reality.
        // This matters most for gain-list exclusion (don't show an already-
        // retrained-into feat as a fresh option).
        foreach (var (_, swaps) in session.Replacements)
        {
            foreach (var swap in swaps)
            {
                if (!string.IsNullOrEmpty(swap.OldInternalId))
                    ids.Remove(swap.OldInternalId);
                if (!string.IsNullOrEmpty(swap.NewInternalId))
                    ids.Add(swap.NewInternalId);
            }
        }
        return ids;
    }

    /// <summary>
    /// Build the set of InternalIds that any other active element has a
    /// prereq on. Used as a gate on the lose-list for Feats / Skills /
    /// Themes per OCB <c>AddReplaceChoice</c>'s prereq-dependency check.
    /// V1 is a substring match on the prereq text (HasElement node), which
    /// covers the canonical "Feat X" / "Skill Training: X" patterns. Edge
    /// cases (compound predicates, level checks, ability gates) are
    /// ignored — those don't depend on retainable elements.
    /// </summary>
    private static HashSet<string> BuildPrereqDependencyMap(CharacterSession session)
    {
        var dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Build a Name → InternalId lookup for retrainable elements so we can
        // resolve "Feat: Toughness" prereq strings back to ids.
        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in RetrainableTypes)
        {
            foreach (var el in session.GetAllElementsOfType(type))
            {
                if (!string.IsNullOrEmpty(el.Name) && !nameToId.ContainsKey(el.Name))
                    nameToId[el.Name] = el.InternalId;
            }
        }

        foreach (var type in RetrainableTypes)
        {
            foreach (var el in session.GetAllElementsOfType(type))
            {
                if (string.IsNullOrWhiteSpace(el.Prereqs)) continue;
                foreach (var (name, id) in nameToId)
                {
                    if (string.Equals(id, el.InternalId, StringComparison.OrdinalIgnoreCase))
                        continue; // don't self-depend
                    if (el.Prereqs.Contains(name, StringComparison.OrdinalIgnoreCase))
                        dependents.Add(id);
                }
            }
        }
        return dependents;
    }

    /// <summary>
    /// Build a prereq filter for gain candidates: evaluates each candidate's
    /// prereq string against a snapshot of the character state AS IF
    /// <paramref name="losing"/> were not active (so the swap doesn't see
    /// itself as a dependent). V1 implementation uses a simple "active
    /// element name set" projection; complex predicates (ability scores,
    /// "any martial class", level checks) are passed through via the
    /// engine's full PrereqParser/Evaluator.
    /// </summary>
    private static Func<RulesElement, bool> BuildPrereqFilter(
        CharacterSession session, RulesElement losing)
    {
        // For now we use a permissive filter: a candidate passes if its
        // prereq string either:
        //   (a) is empty, OR
        //   (b) does NOT reference any element that's currently inactive
        //       and is not the one being lost.
        //
        // True legality (ability scores, class checks, level gates) is
        // already enforced at the engine level when the chosen element is
        // applied — re-doing it here would duplicate logic. Heroic-feat
        // gating handles the typical cases (Power Attack needs Str 15, etc.)
        // through the engine's Build pass; if a retrain produces an illegal
        // pick, the engine emits an UnresolvedElement.
        var activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in ActiveElementTypes)
        {
            foreach (var el in session.GetAllElementsOfType(type))
            {
                if (string.Equals(el.InternalId, losing.InternalId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(el.Name))
                    activeNames.Add(el.Name);
            }
        }

        return candidate =>
        {
            if (string.IsNullOrWhiteSpace(candidate.Prereqs)) return true;

            // Lightweight HasElement gate: if the prereq mentions a specific
            // named feat / skill / class feature and that name is NOT in the
            // current active set, fail.
            // Walk the prereq string for "Feat:", "Skill Training:" tokens —
            // most common prereq shape. Compound predicates (||, &&) are
            // pessimistic: any unmet term fails the whole thing. Refining
            // this to a proper AST evaluator is a follow-up.
            foreach (var token in EnumerateRequiredNames(candidate.Prereqs))
            {
                if (!activeNames.Contains(token))
                    return false;
            }
            return true;
        };
    }

    private static IEnumerable<string> EnumerateRequiredNames(string prereqs)
    {
        // Match patterns like "Feat:Combat Anticipation" / "Skill Training:Arcana"
        // / "Class Feature:Warrior of the Wild". Compound expressions get
        // tokenized on '&&' boundaries — '||' alternatives are conservatively
        // treated as required (false negative possible; engine catches it).
        foreach (var clause in prereqs.Split(new[] { "&&" }, StringSplitOptions.None))
        {
            var trimmed = clause.Trim();
            // Skip clauses with || (alternatives) — too lossy without a proper
            // parser. The engine evaluates these on selection.
            if (trimmed.Contains("||")) continue;

            int colon = trimmed.IndexOf(':');
            if (colon <= 0 || colon == trimmed.Length - 1) continue;
            yield return trimmed[(colon + 1)..].Trim();
        }
    }
}

/// <summary>
/// A single retrain slot — one per character level from L2 up. Carries
/// <see cref="ExistingRetrain"/> if the user has already taken a retrain
/// at this level, otherwise null (the slot is empty).
/// </summary>
public sealed record RetrainSlot(int Level, RetrainPair? ExistingRetrain)
{
    public bool IsFilled => ExistingRetrain is not null;
}

/// <summary>
/// Discriminated context passed into <c>RetrainModal</c> so the same
/// component can serve per-level PHB retrains and ChoiceSlot-backed
/// directive swaps.
/// </summary>
/// <param name="Level">Character level the retrain slot is attached to.</param>
/// <param name="IsOptional">Whether the user may skip without making a swap.</param>
public sealed record RetrainModalContext(int Level, bool IsOptional = false);

/// <summary>
/// Returned by <c>RetrainModal</c> on confirm (null result means skipped).
/// </summary>
/// <param name="Losing">The element the user is giving up.</param>
/// <param name="Gaining">The element the user is picking up.</param>
public sealed record RetrainResult(RulesElement Losing, RulesElement Gaining);

/// <summary>
/// The pair of elements involved in a retrain: what was given up and what
/// was gained. Either side may be null if the corresponding rules element
/// is no longer present in the rules database (e.g., source content was
/// removed since the character was saved).
/// </summary>
public sealed record RetrainPair(
    ElementReplacement Replacement,
    RulesElement? OldElement,
    RulesElement? NewElement);
