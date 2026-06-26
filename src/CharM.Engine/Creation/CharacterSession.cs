using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;
using System.Xml.Linq;

namespace CharM.Engine.Creation;

/// <summary>
/// Manages an interactive character creation session.
/// Wraps CharacterCreationWizard with undo (via replay), live stat snapshots,
/// and choice history tracking. Takes functional callbacks to stay
/// storage-layer independent — any consumer (Blazor, CLI, API) can provide
/// the database lookups.
/// </summary>
public sealed partial class CharacterSession
{
    private readonly Func<string, RulesElement?> _findById;
    private readonly Func<string, string, RulesElement?> _findByNameAndType;
    private readonly Func<string, bool, IEnumerable<RulesElement>> _findByType;
    private readonly Func<string, string, bool, IEnumerable<RulesElement>>? _findByTypeAndSource;

    private CharacterCreationWizard _wizard;
    private readonly List<ChoiceRecord> _choiceHistory = [];
    private readonly List<RulesElement> _grabbagGrants = [];
    private readonly List<RulesElement> _campaignSettingGrants = [];
    private readonly List<UserEditPick> _userEditPicks = [];
    private readonly List<HouseruleGrant> _houseruleGrants = [];
    private readonly List<SlotOwnedSupplement> _slotOwnedSupplements = [];
    private readonly HashSet<string> _userEditPickIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _powerStatsExcludedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _levelNestedOnlyIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<ElementReplacement>> _replacements = new();
    private readonly Dictionary<string, LootItem> _equippedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<InventoryItem> _inventory = new();
    private readonly HashSet<string> _processedFreebeeIds = new(StringComparer.OrdinalIgnoreCase);
    private AbilityScoreSet? _abilityScores;
    private int _sequenceCounter;
    private CharacterSnapshot? _cachedSnapshot;
    private string _name = "New Character";

    /// <summary>Character level being created.</summary>
    public int Level { get; private set; }

    /// <summary>Character name (cosmetic, for export).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
                return;

            _name = value;
            NotifyChanged();
        }
    }

    /// <summary>Character details (alignment, gender, etc.).</summary>
    public Dictionary<string, string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loose root-level <c>&lt;textstring&gt;</c> values from the source file
    /// that we don't otherwise model (Player Name, Carried Money, Stored
    /// Money, Residuum, _PER_LEVEL_*_*, Notes, etc.). Carried verbatim through
    /// import/export so round-trips don't lose user fluff content.
    /// </summary>
    public Dictionary<string, string> TextStrings { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// OCB campaign-setting grants that should live in the root
    /// <c>&lt;Grabbag&gt;</c> section when export needs to rebuild that section.
    /// Kept separate from <see cref="GrabbagGrants"/> because the importer also
    /// uses detached grants for non-campaign structural fallbacks.
    /// </summary>
    public IReadOnlyList<RulesElement> CampaignSettingGrants => _campaignSettingGrants;

    /// <summary>
    /// True after campaign-setting grants have been edited interactively. The
    /// exporter uses this to replace stale imported <c>&lt;Grabbag&gt;</c>
    /// passthrough XML with the current campaign-setting state.
    /// </summary>
    public bool CampaignSettingsDirty { get; private set; }

    /// <summary>
    /// Per-element pass-through metadata captured at import (url, charelem,
    /// replaces, child <c>&lt;specific&gt;</c> values), keyed by internal-id.
    /// The exporter consults this dictionary to re-emit attributes and power-
    /// card text for elements unchanged since import. New elements added via
    /// the wizard simply have no entry and export with defaults.
    /// </summary>
    public Dictionary<string, ElementSourceMetadata> SourceMetadata { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// InternalIds that appeared in the imported flat <c>RulesElementTally</c>.
    /// Used for round-trip-only cases where OCB echoes a loot item in both the
    /// flat tally and <c>LootTally</c> (notably granted alchemical poisons).
    /// </summary>
    public HashSet<string> SourceFlatTallyIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verbatim XML for unmodeled top-level sections captured at import
    /// (D20CampaignSetting, Grabbag, Companions, Journal, PowerStats).
    /// Re-emitted as-is so round-trips don't drop user fluff. Structured
    /// modeling for each is tracked under separate todos.
    /// </summary>
    public Dictionary<string, System.Xml.Linq.XElement> RawSections { get; }
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Source per-level loot journal rows. OCB writes these under
    /// <c>&lt;Level&gt;</c> as an acquisition/history mirror of LootTally.
    /// They are preserved verbatim for imported characters.
    /// </summary>
    public Dictionary<int, List<System.Xml.Linq.XElement>> SourceLevelLoot { get; }
        = new();

    /// <summary>
    /// Captured per-level <c>&lt;RulesElement type="Level"&gt;</c> trees from
    /// the source file (the inner topRE of each <c>&lt;Level&gt;</c>
    /// container, with all nested children). The exporter emits these verbatim
    /// for any level that has NOT been marked dirty via
    /// <see cref="LevelTreeDirty"/>, matching OCB's load+save behavior — load
    /// builds an in-memory CharElement tree from the XML and save walks it
    /// back out unchanged. This preserves stale-predicate grants, deeply
    /// nested Decider slot-owner placement, and the full Implement
    /// Proficiency cascade exactly as the source had them.
    ///
    /// UI mutations that change the level structure (retraining via
    /// <see cref="AddReplacement"/> / <see cref="RemoveReplacement"/>, choice
    /// edits, undo, skip-slot, new picks) add the affected level to
    /// <see cref="LevelTreeDirty"/>, which causes the exporter to fall back
    /// to <see cref="CharM.Engine.Export.ExportTreeBuilder"/> rebuild for
    /// that level only — other levels keep their verbatim captures.
    ///
    /// Empty for characters built from scratch via the wizard (no source).
    /// </summary>
    public Dictionary<int, System.Xml.Linq.XElement> CapturedLevelTrees { get; }
        = new();

    /// <summary>
    /// Levels whose captured tree in <see cref="CapturedLevelTrees"/> has
    /// been invalidated by a UI mutation and must be re-emitted from engine
    /// state (<see cref="CharM.Engine.Export.ExportTreeBuilder"/>) on the
    /// next export. See <see cref="CapturedLevelTrees"/> for the full
    /// rationale.
    /// </summary>
    public HashSet<int> LevelTreeDirty { get; } = new();

    /// <summary>
    /// Houserule overlay: per-Level <c>&lt;UserEdit&gt;</c> blocks captured
    /// verbatim. Re-emitted by the exporter at end of matching
    /// <c>&lt;Level&gt;</c> container.
    /// </summary>
    public Dictionary<int, List<System.Xml.Linq.XElement>> HouseruleLevelUserEdits { get; }
        = new();

    /// <summary>
    /// Houserule overlay: Form A picks (descendants of UserEdit wrappers) for
    /// re-emission as Form C tally rows.
    /// </summary>
    public List<System.Xml.Linq.XElement> HouseruleFormATallyMirror { get; }
        = new();

    /// <summary>
    /// Houserule overlay: Form B legacy inline tally rows preserved verbatim.
    /// </summary>
    public List<System.Xml.Linq.XElement> HouseruleLegacyTallyRows { get; }
        = new();

    /// <summary>
    /// Internal-ids of every <c>&lt;RulesElement&gt;</c> originally tagged
    /// <c>legality="houserule"</c> in the source file. Drives per-element
    /// legality re-emission so round-trips preserve OCB's classification of
    /// houseruled feats / powers (taken without meeting prereqs).
    /// </summary>
    public HashSet<string> HouseruledElementIds { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Elements in the source file whose <c>internal-id</c> could not be
    /// resolved against the local rules database. Preserved verbatim so
    /// round-trip parity holds and the UI can surface a ⚠ wherever the
    /// element would naturally appear plus a complete list on the details
    /// page. See <see cref="UnresolvedElement"/> for full design rationale.
    /// </summary>
    public IReadOnlyList<UnresolvedElement> UnresolvedElements => _unresolvedElements;

    private readonly List<UnresolvedElement> _unresolvedElements = new();

    /// <summary>
    /// Record a source-file element that didn't resolve against the rules
    /// database. Called by <c>Dnd4eImporter</c> as it walks the level tree,
    /// tally, and other sections. Duplicates are de-deduplicated by
    /// <c>InternalId + Location</c> so a tally row mirroring a level-tree
    /// pick doesn't show up twice.
    /// </summary>
    public void AddUnresolvedElement(UnresolvedElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (string.IsNullOrEmpty(element.InternalId)) return;
        foreach (var existing in _unresolvedElements)
        {
            if (existing.Location == element.Location
                && string.Equals(existing.InternalId, element.InternalId, StringComparison.OrdinalIgnoreCase))
                return;
        }
        _unresolvedElements.Add(element);
    }

    /// <summary>
    /// Whether the character has any Form A/B houserule picks. When true the
    /// exporter cascades <c>legality="houserule"</c> on <c>D20Character</c> +
    /// <c>AbilityScores</c>.
    /// </summary>
    public bool IsCharacterHouseruled { get; set; }

    /// <summary>
    /// Houserule grants added through this app's explicit houserule workflow.
    /// Imported source UserEdit blocks are preserved separately in the raw
    /// houserule overlay.
    /// </summary>
    public IReadOnlyList<HouseruleGrant> HouseruleGrants => _houseruleGrants;

    /// <summary>Optional source book filter for candidate queries.</summary>
    public string? SourceFilter { get; set; }

    /// <summary>
    /// Part layers (id + version) the character was built with. Populated from
    /// the rules database at build/import time and persisted in the CharM
    /// extensions block so the character can be audited against the rules DB it
    /// is later opened under (missing or newer parts raise an alert).
    /// </summary>
    public List<RecordedPart> BuildProvenance { get; } = [];

    /// <summary>
    /// Enabled sourcebooks for candidate filtering (multi-source enable/disable
    /// in the creator session). Null or empty means all sources are allowed.
    /// Setting this updates the current wizard and persists across internal
    /// wizard rebuilds (Replay / RebuildFromHistory). Sourceless elements
    /// (houserules) are always allowed regardless of this set.
    /// </summary>
    public IReadOnlySet<string>? EnabledSources
    {
        get => _enabledSources;
        set
        {
            _enabledSources = value is { Count: > 0 } ? value : null;
            _wizard.SetEnabledSources(_enabledSources);
        }
    }
    private IReadOnlySet<string>? _enabledSources;

    /// <summary>Full ordered history of user choices (for undo and display).</summary>
    public IReadOnlyList<ChoiceRecord> ChoiceHistory => _choiceHistory;

    /// <summary>Raised when the session state changes and UI should refresh.</summary>
    public event Action? Changed;

    /// <summary>Whether ability scores have been assigned.</summary>
    public bool ScoresSet => _abilityScores is not null;

    /// <summary>The pre-racial base ability scores, if set.</summary>
    public AbilityScoreSet? AbilityScores => _abilityScores;

    /// <summary>Whether all mandatory choices are made.</summary>
    public bool IsComplete => _wizard.IsComplete;

    /// <summary>The underlying wizard's accumulated choices (for export).</summary>
    public IReadOnlyList<ElementChoice> AccumulatedChoices => _wizard.AccumulatedChoices;

    /// <summary>
    /// SuggestDirective targets the engine recorded for each slot owner.
    /// The picker UI looks up by <see cref="ChoiceSlot.OwnerInternalId"/>
    /// to surface a Recommended badge on matching candidates.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string InternalId, string ElementType)>> SuggestionsBySlotOwner
        => _wizard.SuggestionsBySlotOwner;

    /// <summary>
    /// Convenience helper: returns the set of suggested candidate InternalIds
    /// for the given slot, drawing from <see cref="SuggestionsBySlotOwner"/>.
    /// Empty set when the slot's owner has no recorded suggestions.
    /// </summary>
    public IReadOnlySet<string> GetSuggestedIdsForSlot(ChoiceSlot slot)
    {
        if (string.IsNullOrEmpty(slot.OwnerInternalId)) return _emptySuggestions;
        if (!_wizard.SuggestionsBySlotOwner.TryGetValue(slot.OwnerInternalId, out var list))
            return _emptySuggestions;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, type) in list)
        {
            if (!string.Equals(type, slot.ElementType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(id))
                set.Add(id);
        }
        return set;
    }

    private static readonly HashSet<string> _emptySuggestions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// See <see cref="CharacterCreationWizard.AutoFillSelectDefaults"/>.
    /// Setting this propagates to the current wizard and is preserved across
    /// internal wizard rebuilds (Replay / RebuildFromHistory).
    /// </summary>
    public bool AutoFillSelectDefaults
    {
        get => _autoFillSelectDefaults;
        set
        {
            _autoFillSelectDefaults = value;
            _wizard.AutoFillSelectDefaults = value;
        }
    }
    private bool _autoFillSelectDefaults = true;

    public CharacterSession(
        Func<string, RulesElement?> findById,
        Func<string, string, RulesElement?> findByNameAndType,
        Func<string, bool, IEnumerable<RulesElement>> findByType,
        Func<string, string, bool, IEnumerable<RulesElement>>? findByTypeAndSource = null,
        int level = 1,
        bool autoFillSelectDefaults = true)
    {
        _findById = findById;
        _findByNameAndType = findByNameAndType;
        _findByType = findByType;
        _findByTypeAndSource = findByTypeAndSource;
        Level = level;
        _autoFillSelectDefaults = autoFillSelectDefaults;
        _wizard = CreateWizard();
    }
}
