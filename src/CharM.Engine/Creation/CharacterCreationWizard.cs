using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;
using CharM.Engine.Selection;
using System.Runtime.CompilerServices;

namespace CharM.Engine.Creation;

/// <summary>
/// State-machine wizard for step-by-step character creation.
/// </summary>
public sealed partial class CharacterCreationWizard : ICharacterState
{
    private readonly Func<string, RulesElement?> _findById;
    private readonly Func<string, string, RulesElement?> _findByNameAndType;
    private readonly CandidateFilter _candidateFilter;
    private readonly LegalityChecker _legalityChecker;

    // Cache slot-specific candidate sets for the current character state. The
    // cache is invalidated whenever a choice, score, or skip mutates the tree.
    private readonly Dictionary<string, IReadOnlyList<RulesElement>> _candidateCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly CharacterElementTree _tree = new();
    private readonly HashSet<string> _visitedElements = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ElementChoice> _accumulatedChoices = [];
    private readonly List<int> _choiceLevels = [];
    private readonly List<ElementChoice> _grabbagGrants = [];
    private readonly HashSet<ChoiceSlot> _skippedSlots = new();
    private readonly List<(GrantDirective Grant, CharacterElement Parent, int Level)> _deferredGrants = [];
    private readonly List<(RulesElement Element, ChoiceSlot SlotTemplate)> _deferredChoices = [];
    private readonly HashSet<string> _freebeeIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SuggestDirective targets recorded during element processing, keyed by
    /// the InternalId of the suggesting (slot-owning) element. See
    /// <see cref="SuggestionsBySlotOwner"/>.
    /// </summary>
    private readonly Dictionary<string, List<(string InternalId, string ElementType)>> _suggestionsBySlotOwner =
        new(StringComparer.OrdinalIgnoreCase);
    private AbilityScoreSet? _abilityScores;
    private bool _scoresSet;

    private void InvalidateCandidateCache() => _candidateCache.Clear();

    /// <summary>Current wizard step.</summary>
    public WizardStep CurrentStep { get; private set; } = WizardStep.Race;

    /// <summary>Character level being created.</summary>
    public int Level { get; }

    /// <summary>Whether base ability scores have been set.</summary>
    public bool ScoresSet => _scoresSet;

    /// <summary>
    /// When true (default), <see cref="SelectDirective"/> slots whose
    /// <c>Default</c> attribute is set are auto-filled during phase-1
    /// processing — convenience for the interactive UI that pre-selects
    /// the most likely choice. The importer disables this so that the
    /// authoritative source XML is the only thing that fills slots:
    /// otherwise the auto-fill silently occupies the slot, blocking
    /// <c>AlignChildren</c> from placing the user's explicit pick and
    /// causing the chosen power/feat/etc. to be dropped. 
    /// <c>Grant</c> auto-fills are still honored regardless because 
    /// <c>Grant</c> is a forced engine grant (no real user choice).
    /// </summary>
    public bool AutoFillSelectDefaults { get; set; } = true;

    /// <summary>
    /// Element IDs collected from FREEBEE: statadd names during phase 1.
    /// These represent items the character gets for free (ritual books,
    /// weapons, gear) and should be auto-added to inventory or equipped.
    /// </summary>
    public IReadOnlySet<string> FreebeeIds => _freebeeIds;

    /// <summary>
    /// SuggestDirective targets recorded during phase-1 processing, keyed
    /// by the InternalId of the suggesting (slot-owning) element. The
    /// SelectionModal looks up by <c>ChoiceSlot.OwnerInternalId</c> to
    /// flag matching candidates with a Recommended badge.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string InternalId, string ElementType)>> SuggestionsBySlotOwner
        => _suggestionsBySlotOwner.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<(string, string)>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

    private const string FreebeePrefix = "FREEBEE:";

    /// <summary>The pre-racial base ability scores, if set.</summary>
    public AbilityScoreSet? BaseAbilityScores => _abilityScores;

    // --- ICharacterState implementation ---

    bool ICharacterState.HasElement(string name) => _tree.HasElement(name);
    bool ICharacterState.HasElementOfTypeAndCategory(string type, string category)
        => _tree.HasElementOfTypeAndCategory(type, category);

    int ICharacterState.GetAbilityScore(string abilityName)
    {
        if (_abilityScores is null) return 0;
        return AbilityNames.TryParse(abilityName, out var ability)
            ? _abilityScores[ability]
            : 0;
    }

    int ICharacterState.Level => Level;

    /// <summary>All choices accumulated during character creation, in order.</summary>
    public IReadOnlyList<ElementChoice> AccumulatedChoices => _accumulatedChoices;

    /// <summary>Per-choice level tracking, parallel to AccumulatedChoices.</summary>
    public IReadOnlyList<int> ChoiceLevels => _choiceLevels;

    /// <summary>
    /// Get accumulated choices partitioned by the level they belong to.
    /// </summary>
    public Dictionary<int, List<ElementChoice>> GetChoicesByLevel()
    {
        var result = new Dictionary<int, List<ElementChoice>>();
        for (int lvl = 1; lvl <= Level; lvl++)
            result[lvl] = [];

        for (int i = 0; i < _accumulatedChoices.Count; i++)
        {
            int lvl = i < _choiceLevels.Count ? _choiceLevels[i] : 1;
            if (!result.ContainsKey(lvl))
                result[lvl] = [];
            result[lvl].Add(_accumulatedChoices[i]);
        }

        return result;
    }

    /// <summary>
    /// Create a new wizard for interactive character creation.
    /// Uses functional callbacks to stay storage-layer independent.
    /// </summary>
    public CharacterCreationWizard(
        Func<string, RulesElement?> findById,
        Func<string, string, RulesElement?> findByNameAndType,
        Func<string, IEnumerable<RulesElement>> findByType,
        Func<string, string, IEnumerable<RulesElement>>? findByTypeAndSource = null,
        int level = 1,
        bool autoFillSelectDefaults = true)
    {
        _findById = findById;
        _findByNameAndType = findByNameAndType;
        _candidateFilter = new CandidateFilter(findByType, findByTypeAndSource);
        _legalityChecker = new LegalityChecker();
        Level = level;
        AutoFillSelectDefaults = autoFillSelectDefaults;

        _tree.ElementResolver = (name, type) =>
            findById(name) ?? findByNameAndType(name, type);

        InitializeLevel();
    }

    /// <summary>
    /// Create a new wizard for interactive character creation.
    /// This overload allows the caller to choose whether database
    /// queries should load full rules for candidates. When
    /// <c>includeRules</c> is false, implementations may return
    /// lightweight elements that skip rules_json deserialization.
    /// </summary>
    public CharacterCreationWizard(
        Func<string, RulesElement?> findById,
        Func<string, string, RulesElement?> findByNameAndType,
        Func<string, bool, IEnumerable<RulesElement>> findByType,
        Func<string, string, bool, IEnumerable<RulesElement>>? findByTypeAndSource = null,
        int level = 1,
        bool autoFillSelectDefaults = true)
    {
        _findById = findById;
        _findByNameAndType = findByNameAndType;
        _candidateFilter = new CandidateFilter(findByType, findByTypeAndSource);
        _legalityChecker = new LegalityChecker();
        Level = level;
        AutoFillSelectDefaults = autoFillSelectDefaults;

        _tree.ElementResolver = (name, type) =>
            findById(name) ?? findByNameAndType(name, type);

        InitializeLevel();
    }

    /// <summary>Get available choices for the current step, filtered by optional source.</summary>
    /// <param name="sourceFilter">Only return elements from this source book.</param>
    /// <param name="skipPrereqs">Skip prerequisite checking for faster enumeration (validate on selection instead).</param>
    public IReadOnlyList<RulesElement> GetAvailableChoices(string? sourceFilter = null, bool skipPrereqs = false)
    {
        var slot = GetPendingSlotForStep(CurrentStep);
        if (slot is null) return [];
        return GetCandidatesForSlot(slot, sourceFilter, skipPrereqs);
    }

    /// <summary>Get candidate elements for a specific choice slot.</summary>
    /// <param name="slot">The choice slot to find candidates for.</param>
    /// <param name="sourceFilter">Only return elements from this source book.</param>
    /// <param name="skipPrereqs">Skip prerequisite checking for faster enumeration.</param>
    public IReadOnlyList<RulesElement> GetCandidatesForSlot(ChoiceSlot slot, string? sourceFilter = null, bool skipPrereqs = false)
    {
        var variables = SelectVariables.Resolve(_tree, Level);

        var select = new SelectDirective
        {
            ElementType = slot.ElementType,
            Category = slot.Category,
            Number = slot.Number,
            Optional = slot.Optional,
            Existing = slot.Existing,
        };

        // Cache slot-specific candidate sets, but invalidate the cache whenever
        // the character state changes so $$CLASS / CountsAsClass / granted
        // elements don't go stale.
        string cacheKey = $"{slot.ElementType}\0{slot.Category}\0{sourceFilter}\0{slot.Existing}";

        // Resolve category names to IDs (e.g., "Elf Subrace" → ID_DRU_RACIAL_TRAIT_004)
        // by checking if an element with that name exists in the character's tree.
        // Build the name->id map ONCE per call: CategoryMatcher invokes the
        // resolver per (candidate × term), so a per-call lookup that re-walks
        // the entire tree dominates the test profile (>1s per slot scaled out).
        Dictionary<string, string>? nameToIdMap = null;
        Func<string, string?> resolveNameToId = name =>
        {
            if (nameToIdMap is null)
            {
                nameToIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in _tree.GetActiveElements())
                {
                    // Keep first occurrence (matches old foreach-first-match semantics).
                    if (!nameToIdMap.ContainsKey(el.Name))
                        nameToIdMap[el.Name] = el.InternalId;
                }
            }
            return nameToIdMap.TryGetValue(name, out var id) ? id : null;
        };

        if (!_candidateCache.TryGetValue(cacheKey, out var cachedRaw))
        {
            // First query: type + category + source filtering.
            // Use the lightweight rules DB path here; it still includes prereqs,
            // categories, and fields needed for level/category matching without
            // deserializing each element's full rules payload.
            cachedRaw = _candidateFilter.FindCandidates(
                select,
                variables,
                sourceFilter,
                prereqCheck: null,
                _tree.HasElement,
                characterLevel: Level,
                resolveNameToId);
            _candidateCache[cacheKey] = cachedRaw;
        }

        IEnumerable<RulesElement> candidates = cachedRaw;

        if (!skipPrereqs)
        {
            // Slot-level-aware prereq filter: feat slots for L1/L2/L4/...
            // on a paragon+ character must NOT offer paragon-tier feats.
            // The "Paragon Tier" / "Epic Tier" Internal elements are
            // granted globally at L11/L21 and stay granted, so a naive
            // HasElement check passes them for every slot. Wrap the
            // wizard state in a SlotLevelState that pins Level and
            // gates the tier elements to the slot's effective level.
            int effectiveLevel = slot.Level is int lvl && lvl > 0
                ? Math.Min(lvl, Level)
                : Level;
            ICharacterState prereqState = effectiveLevel < Level
                ? new SlotLevelState(this, effectiveLevel)
                : this;
            var prereqFilter = _legalityChecker.CreatePrereqFilter(prereqState);
            candidates = candidates.Where(prereqFilter);
        }

        // Filter out elements already selected via other choice slots OR
        // already granted through grant chains. The original CB excludes any
        // element already on the character from select candidate lists — most
        // visible for Skill Training: a Wizard auto-grants Arcana via
        // <c>Grants: Wizard</c>, then opens 3 separate Skill Training picks;
        // the picker must not offer Arcana again or the user spends one of
        // their 3 picks on a skill they already have. The same applies to
        // Language, Proficiency, etc.
        var alreadyOnCharacter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _tree.GetAllChoices())
        {
            foreach (var sel in s.SelectedElements)
                alreadyOnCharacter.Add(sel.InternalId);
        }
        foreach (var el in _tree.GetActiveElements())
        {
            if (!string.IsNullOrEmpty(el.InternalId))
                alreadyOnCharacter.Add(el.InternalId);
        }

        if (alreadyOnCharacter.Count > 0)
            candidates = candidates.Where(c => !alreadyOnCharacter.Contains(c.InternalId));

        // Exclude the slot's owner from its own select results. CategoryMatcher
        // will Name-match the owner against a Category like subraces,
        // which would otherwise let the user pick the parent intermediate trait
        // that just opens this same select again recursively.
        if (!string.IsNullOrEmpty(slot.OwnerInternalId))
            candidates = candidates.Where(c =>
                !string.Equals(c.InternalId, slot.OwnerInternalId, StringComparison.OrdinalIgnoreCase));

        return candidates.ToList();
    }

    /// <summary>Get all pending (unmade) choices across all steps.</summary>
}
