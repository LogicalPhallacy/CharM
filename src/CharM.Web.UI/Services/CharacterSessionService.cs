using CharM.Engine.CharacterModel;
using CharM.Engine.Creation;
using CharM.Engine.Economy;
using CharM.Engine.Export;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;
using CharM.Serialization;

namespace CharM.Web.Services;

/// <summary>
/// Blazor-scoped service wrapping CharacterSession with DI-injected database.
/// One instance per SignalR connection (user browser tab).
/// </summary>
public sealed class CharacterSessionService : IDisposable
{
    private readonly RulesDatabaseService _db;
    private CharacterSession? _session;
    private IReadOnlyList<PowerStatEntry>? _displayPowerStatsCache;
    private long _sessionVersion;

    public event Action? Changed;

    public CharacterSessionService(RulesDatabaseService db)
    {
        _db = db;
    }

    /// <summary>The current character session, or null if none started.</summary>
    public CharacterSession? Session => _session;

    /// <summary>Whether a session is active.</summary>
    public bool HasSession => _session is not null;

    /// <summary>Increments only when the active character session is replaced or cleared.</summary>
    public long SessionVersion => _sessionVersion;

    /// <summary>Whether the rules database is ready for character creation and imports.</summary>
    public bool IsRulesDatabaseReady => _db.IsLoaded;

    /// <summary>Available source books in the database.</summary>
    public IEnumerable<string> GetAvailableSources()
        => _db.IsLoaded ? _db.GetDistinctSources() : [];

    /// <summary>
    /// Sourcebooks enabled for character creation. Null/empty means all are
    /// enabled. Applied to new sessions; sourceless (houserule) elements are
    /// always allowed regardless. Persisting this is handled by the caller.
    /// </summary>
    public IReadOnlySet<string>? EnabledSources { get; set; }

    /// <summary>Start a new character session at the given level.</summary>
    public CharacterSession NewCharacter(int level = 1, string? sourceFilter = null)
    {
        var session = new CharacterSession(
            _db.FindByInternalId,
            _db.FindByNameAndType,
            (type, includeRules) => _db.FindByType(type, includeRules),
            (type, source, includeRules) => _db.FindByTypeAndSource(type, source, includeRules),
            level);

        if (sourceFilter is not null)
            session.SourceFilter = sourceFilter;

        if (EnabledSources is { Count: > 0 })
            session.EnabledSources = EnabledSources;

        // Capture the enabled part layers this character is being built with so
        // it can be audited later against whatever rules DB it is opened under.
        foreach (var layer in _db.GetPartLayers())
            if (layer.Enabled && !layer.IsBase)
                session.BuildProvenance.Add(new RecordedPart(layer.PartId, layer.Version, layer.Category));

        SetSession(session);
        return session;
    }

    /// <summary>Clear the current character session so the UI returns to the new-character flow.</summary>
    public void ClearSession()
    {
        if (_session is not null)
            _session.Changed -= OnSessionChanged;

        _session = null;
        _displayPowerStatsCache = null;
        _sessionVersion++;
        Changed?.Invoke();
    }

    /// <summary>
    /// Audit the current session's recorded build provenance against the loaded
    /// rules database. Returns an empty list when there is no session, no
    /// recorded provenance, or nothing is amiss. Alerts only fire for missing
    /// parts or a database older than what the character was built with.
    /// </summary>
    public IReadOnlyList<PartAuditAlert> AuditCurrentSession()
    {
        if (_session is null || _session.BuildProvenance.Count == 0 || !_db.IsLoaded)
            return [];
        return PartAuditService.Audit(_session.BuildProvenance, _db.GetPartLayers());
    }

    /// <summary>Load a .dnd4e file into a fresh editable session.</summary>
    public CharacterSession ImportFromDnd4e(Stream stream, string? sourceFilter = null)
    {
        var result = CharM.ImportExport.Dnd4eImporter.Import(stream, _db, sourceFilter);
        var session = result.Session;

        // Equipment restoration now lives inside Dnd4eImporter so the CLI and
        // Web stay in sync. (The legacy RestoreImportedEquipment / GetPreferredSlots
        // / ResolveLootElement helpers below are still used by the equip-modal UI
        // for slot matching of newly-picked items.)

        SetSession(session);
        return _session!;
    }

    /// <summary>
    /// Load a .dnd4e file from an async-only upload stream into a fresh editable session.
    /// </summary>
    public async Task<CharacterSession> ImportFromDnd4eAsync(
        Stream stream,
        string? sourceFilter = null,
        CancellationToken cancellationToken = default)
    {
        using var buffered = new MemoryStream();
        await stream.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return ImportFromDnd4e(buffered, sourceFilter);
    }

    /// <summary>Export the current character to a .dnd4e XML byte array.</summary>
    /// <remarks>
    /// Delegates to the shared <see cref="CharM.ImportExport.Dnd4eExporter"/>
    /// so that the Web export path produces identical output to the CLI
    /// import → export round-trip (equipment-aware tally exclusion, item-set
    /// benefits, magic-item grant cascades, raw-section passthrough,
    /// houserule fields, inventory-weight stat, computed PowerStats, etc.).
    /// Previously this method built <see cref="CharacterExportData"/> inline
    /// and silently dropped most of those features — every fix to the
    /// exporter had to be ported by hand or simply went missing in the Web UI.
    /// </remarks>
    public byte[]? ExportToDnd4e()
    {
        if (_session is null) return null;
        return CharM.ImportExport.Dnd4eExporter.Export(_session, _db);
    }

    /// <summary>
    /// Render the current character as OCB SummaryText (forum-shareable).
    /// </summary>
    public string? ExportToSummaryText()
    {
        if (_session is null) return null;
        return CharM.ImportExport.SummaryText.SummaryTextExporter.Export(_session, _db);
    }

    /// <summary>
    /// Replace the current character session with one parsed from
    /// <paramref name="text"/> (OCB SummaryText). Returns the unconsumed
    /// lines (empty when the entire input was understood).
    /// </summary>
    public IReadOnlyList<string> ImportFromSummaryText(string text)
    {
        var result = CharM.ImportExport.SummaryText.SummaryTextImporter.Import(text, _db);
        SetSession(result.Session);
        return result.UnconsumedLines;
    }

    /// <summary>
    /// Rebuild PowerStats for UI display. Cached until the session changes so
    /// sorting/collapsing/usage toggles on the Powers page don't recompute the
    /// full export pipeline.
    /// </summary>
    public IReadOnlyList<PowerStatEntry> GetRebuiltPowerStatsForDisplay()
    {
        if (_session is null || !_db.IsLoaded)
            return Array.Empty<PowerStatEntry>();

        if (_displayPowerStatsCache is not null)
            return _displayPowerStatsCache;

        _displayPowerStatsCache = CharM.ImportExport.Dnd4eExporter
            .BuildExportData(_session, _db, rebuildPowerStats: true)
            .PowerStats;
        return _displayPowerStatsCache;
    }

    /// <summary>Get a full RulesElement by ID (for detail display in selection modal).</summary>
    public RulesElement? GetElementDetails(string internalId)
        => _db.FindByInternalId(internalId);

    public bool IsCampaignToggleEnabled(string internalId)
        => _session?.HasCampaignSettingGrant(internalId) == true;

    public bool SetCampaignToggle(string internalId, bool enabled)
    {
        if (_session is null)
            return false;

        var element = _db.FindByInternalId(internalId)
            ?? throw new InvalidOperationException($"Campaign setting element '{internalId}' was not found in the rules database.");

        return _session.SetCampaignSettingGrant(element, enabled);
    }

    public void SetDetailField(string field, string? value)
    {
        if (_session is null)
            return;

        _session.SetDetailField(field, value);
    }

    public void SetTextString(string name, string? value)
    {
        if (_session is null)
            return;

        _session.SetTextString(name, value);
    }

    /// <summary>
    /// Get available equipment items for a slot picker. Returns mundane bases
    /// (#1) and standalone magic items (#3) only. Enchantments (#4) and
    /// masterwork bases (#2) are excluded — they don't function on their own
    /// and are reachable only through the Magic Item builder. See
    /// <see cref="MagicItemClassifier"/> for bucket definitions.
    /// </summary>
    public IReadOnlyList<RulesElement> GetAvailableEquipment(string? slotFilter = null)
    {
        var results = new Dictionary<string, RulesElement>(StringComparer.OrdinalIgnoreCase);

        void AddMatches(IEnumerable<RulesElement> items)
        {
            foreach (var item in items)
            {
                if (!IsSlotPickerEligibleForCharacter(item))
                    continue;
                if (slotFilter is not null && !MatchesSlot(item, slotFilter))
                    continue;

                string key = item.InternalId ?? $"{item.Type}:{item.Name}";
                results.TryAdd(key, item);
            }
        }

        AddMatches(_db.FindByType("Weapon"));
        AddMatches(_db.FindByType("Armor"));
        AddMatches(_db.FindByType("Magic Item"));

        return results.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Lookup all carriable items the user could add to inventory:
    /// equipment plus mundane gear, rituals, scrolls, and superior implements.
    /// Excludes enchantments and masterwork bases — those have no use on
    /// their own and are reachable through the Magic Item builder.
    /// </summary>
    public IReadOnlyList<RulesElement> GetAvailableInventoryItems()
    {
        var results = new Dictionary<string, RulesElement>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<RulesElement> items)
        {
            foreach (var item in items)
            {
                if (!IsSlotPickerEligibleForCharacter(item))
                    continue;
                string key = item.InternalId ?? $"{item.Type}:{item.Name}";
                results.TryAdd(key, item);
            }
        }

        Add(_db.FindByType("Weapon"));
        Add(_db.FindByType("Armor"));
        Add(_db.FindByType("Magic Item"));
        Add(_db.FindByType("Gear"));
        Add(_db.FindByType("Superior Implement"));

        // Exclude ritual/alchemy/martial practice items — those have their own panel
        return results.Values
            .Where(item => !RitualPracticeAlchemyClassifier.IsRitualPracticeAlchemyElement(item))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static readonly string[] HouseruleElementTypes =
    [
        "Feat",
        "Power",
        "Skill",
        "Class Feature",
        "Racial Trait",
        "Proficiency",
        "Weapon",
        "Armor",
        "Magic Item",
        "Gear",
        "Superior Implement",
    ];

    public IReadOnlyList<RulesElement> GetHouseruleCandidates(
        string elementType,
        string? searchText = null,
        string? source = null,
        int limit = 100)
    {
        if (!_db.IsLoaded || string.IsNullOrWhiteSpace(elementType))
            return [];

        IEnumerable<RulesElement> candidates = string.IsNullOrWhiteSpace(source)
            ? _db.FindByType(elementType, includeRules: true)
            : _db.FindByTypeAndSource(elementType, source, includeRules: true);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var search = searchText.Trim();
            candidates = candidates.Where(e =>
                e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(e.InternalId)
                    && e.InternalId.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return candidates
            .Where(e => !string.IsNullOrWhiteSpace(e.InternalId))
            .OrderBy(e => LevelSortKey(e))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    public void AddHouseruleGrant(string internalId, int? acquisitionLevel = null)
    {
        var session = _session ?? throw new InvalidOperationException("Start or import a character before adding houserules.");
        var element = _db.FindByInternalId(internalId)
            ?? throw new InvalidOperationException($"Rules element '{internalId}' was not found in the rules database.");

        session.AddHouseruleGrant(element, acquisitionLevel ?? session.Level);
    }

    public void AddHouseruleInventoryItem(string internalId, int quantity = 1)
    {
        var session = _session ?? throw new InvalidOperationException("Start or import a character before adding houserules.");
        var element = _db.FindByInternalId(internalId)
            ?? throw new InvalidOperationException($"Rules element '{internalId}' was not found in the rules database.");

        session.AddHouseruleInventoryItem(element, quantity);
    }

    public void EquipHouseruleItem(string slot, string internalId)
    {
        var session = _session ?? throw new InvalidOperationException("Start or import a character before adding houserules.");
        var element = _db.FindByInternalId(internalId)
            ?? throw new InvalidOperationException($"Rules element '{internalId}' was not found in the rules database.");

        session.EquipHouseruleItem(slot, element);
    }

    public bool RemoveHouseruleGrant(string internalId, HouseruleGrantKind kind, string? slot = null)
    {
        var session = _session ?? throw new InvalidOperationException("Start or import a character before removing houserules.");
        return session.RemoveHouseruleGrant(internalId, kind, slot);
    }

    /// <summary>
    /// Eligible for the slot / inventory picker = buckets #1 (Mundane Base) and #3 (Standalone Magic).
    /// Enchantments (#4) and Masterwork Bases (#2) require the Magic Item builder.
    /// </summary>
    private static bool IsSlotPickerEligible(RulesElement item)
    {
        if (MagicItemClassifier.IsAutoGrantedOnly(item))
            return false;

        var bucket = MagicItemClassifier.Classify(item);
        return bucket == EquipmentBucket.MundaneBase
            || bucket == EquipmentBucket.StandaloneMagic;
    }

    private bool IsSlotPickerEligibleForCharacter(RulesElement item)
    {
        if (!IsSlotPickerEligible(item)) return false;
        if (MagicItemClassifier.IsLargeWeapon(item) && !CanWieldLargeWeapons()) return false;
        return true;
    }

    // --- Magic Item builder catalog API ---

    /// <summary>
    /// Bases that can appear in the Magic Item builder's base-selection stage.
    /// Returns mundane bases by default; pass <paramref name="includeMasterwork"/>
    /// to additionally surface masterwork bases (Starleather, Feyleather, …).
    /// <paramref name="baseKind"/> can be "Weapon", "Armor", or null for both.
    /// </summary>
    private const string OneSizeLargerId = "ID_INTERNAL_INTERNAL_ONE_SIZE_LARGER";

    /// <summary>
    /// True when the active character has the One Size Larger marker
    /// (granted by Goliath's Stone's Endurance line, the Warden's Godlike
    /// Stature class feature at L24, certain paragon paths, etc.). The
    /// marker permits wielding Large weapon variants without the normal
    /// size penalty. When false, Large weapon entries
    /// (<c>ID_INTERNAL_WEAPON_LARGE_*</c>) are filtered from the picker.
    /// </summary>
    public bool CanWieldLargeWeapons()
        => _session?.GetAllElementsOfType("Internal")
            .Any(e => string.Equals(e.InternalId, OneSizeLargerId, StringComparison.OrdinalIgnoreCase))
            ?? false;

    public IReadOnlyList<RulesElement> GetAvailableMagicItemBases(string? baseKind = null, bool includeMasterwork = false)
    {
        IEnumerable<RulesElement> source = baseKind?.Trim().ToLowerInvariant() switch
        {
            "weapon" => _db.FindByType("Weapon"),
            "armor" => _db.FindByType("Armor"),
            _ => _db.FindByType("Weapon").Concat(_db.FindByType("Armor")),
        };

        bool allowLarge = CanWieldLargeWeapons();
        return source
            .Where(e =>
            {
                if (MagicItemClassifier.IsAutoGrantedOnly(e)) return false;
                if (!allowLarge && MagicItemClassifier.IsLargeWeapon(e)) return false;
                var b = MagicItemClassifier.Classify(e);
                return b == EquipmentBucket.MundaneBase
                    || (includeMasterwork && b == EquipmentBucket.MasterworkBase);
            })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Enchantments compatible with the given base. Honors weapon/armor
    /// restriction lists and (for masterwork bases) the minimum-enhancement gate.
    /// Sorted by Level then Name.
    /// </summary>
    public IReadOnlyList<RulesElement> GetEnchantmentsForBase(RulesElement baseItem)
        => _db.FindByType("Magic Item")
            .Where(e => !MagicItemClassifier.IsAutoGrantedOnly(e))
            .Where(e => MagicItemClassifier.IsCompatibleEnchantment(baseItem, e))
            .OrderBy(LevelSortKey)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Augments (Dragonshard Augments, Whetstones) compatible with the given
    /// enchantment. Sorted by Level then Name.
    /// </summary>
    public IReadOnlyList<RulesElement> GetAugmentsForEnchantment(RulesElement enchantment)
        => _db.FindByType("Magic Item")
            .Where(e => !MagicItemClassifier.IsAutoGrantedOnly(e))
            .Where(e => MagicItemClassifier.IsCompatibleAugment(enchantment, e))
            .OrderBy(LevelSortKey)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Standalone magic items (no base required). Optionally filtered to a UI
    /// slot — e.g. pass "Neck" to get only neck-slot wearables. Pass null for
    /// every standalone magic item (used by the "Add Magic Item" entry point
    /// when no slot is preselected).
    /// </summary>
    public IReadOnlyList<RulesElement> GetAvailableStandaloneMagicItems(string? slotFilter = null)
        => _db.FindByType("Magic Item")
            .Where(e => !MagicItemClassifier.IsAutoGrantedOnly(e))
            .Where(e => MagicItemClassifier.IsStandaloneMagicItem(e))
            .Where(e => slotFilter is null || MatchesSlot(e, slotFilter))
            .OrderBy(LevelSortKey)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Equip a fully-built composite (Base + optional Enchantment + optional
    /// Augment) into a slot. Thin wrapper over <c>CharacterSession.EquipItem</c>
    /// that the Magic Item builder calls on confirm.
    /// </summary>
    public void EquipComposite(string slot, LootItem composite)
        => _session?.EquipItem(slot, composite);

    /// <summary>
    /// Add a fully-built composite to inventory. Thin wrapper over
    /// <c>CharacterSession.AddInventoryItem</c>.
    /// </summary>
    public void AddCompositeToInventory(LootItem composite, int quantity = 1)
        => _session?.AddInventoryItem(composite, quantity);

    // --- Money / shopping ---

    /// <summary>Current Carried Money, or zero when no session is active.</summary>
    public D20Currency CarriedMoney => _session?.CarriedMoney ?? D20Currency.Zero;

    /// <summary>Current Stored (banked) Money, or zero when no session is active.</summary>
    public D20Currency StoredMoney => _session?.StoredMoney ?? D20Currency.Zero;

    /// <summary>Apply the by-the-book level-appropriate gold allowance (OCB AutoGold).</summary>
    public void AutoGold() => _session?.AutoGold();

    /// <summary>Set Carried Money directly (manual edit).</summary>
    public void SetCarriedMoney(D20Currency money) => _session?.SetCarriedMoney(money);

    /// <summary>Set Stored (banked) Money directly (manual edit).</summary>
    public void SetStoredMoney(D20Currency money) => _session?.SetStoredMoney(money);

    /// <summary>Preview the price of a composite item (× quantity) without buying.</summary>
    public D20Currency PriceOf(LootItem composite, int quantity = 1)
        => _session?.PriceOf(composite, quantity) ?? D20Currency.Zero;

    /// <summary>Free-add a composite to inventory (OCB "Add").</summary>
    public void AddLoot(LootItem composite, int quantity = 1)
        => _session?.AddLoot(composite, quantity);

    /// <summary>
    /// Buy a composite, deducting its price from Carried Money (OCB "Buy").
    /// Overspending is permitted; inspect the result for a negative balance.
    /// Returns null when no session is active.
    /// </summary>
    public PurchaseResult? BuyLoot(LootItem composite, int quantity = 1)
        => _session?.BuyLoot(composite, quantity);

    /// <summary>
    /// Convenience: build a <see cref="LootItem"/> from up to three components.
    /// Caller is responsible for validating compatibility via
    /// <see cref="MagicItemClassifier.IsCompatibleEnchantment"/> /
    /// <see cref="MagicItemClassifier.IsCompatibleAugment"/> first.
    /// </summary>
    public static LootItem BuildComposite(RulesElement baseItem, RulesElement? enchantment = null, RulesElement? augment = null)
        => new()
        {
            Base = baseItem,
            Enchantment = enchantment,
            Augment = augment,
        };

    public IReadOnlyList<RulesElement> GetAvailableRitualPracticeAlchemyItems()
    {
        var results = new Dictionary<string, RulesElement>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<RulesElement> items)
        {
            foreach (var item in items)
            {
                if (!RitualPracticeAlchemyClassifier.IsInventoryAddCandidate(item))
                    continue;

                string key = item.InternalId ?? $"{item.Type}:{item.Name}";
                results.TryAdd(key, item);
            }
        }

        Add(_db.FindByType("Ritual"));
        Add(_db.FindByType("Ritual Scroll"));
        Add(_db.FindByType("Magic Item"));

        return results.Values
            .OrderBy(item => RitualPracticeAlchemyClassifier.KindSortKey(
                RitualPracticeAlchemyClassifier.GetKind(item)!.Value))
            .ThenBy(item => LevelSortKey(item))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Check whether an item can be equipped in a given UI slot.</summary>
    private static bool MatchesSlot(RulesElement item, string uiSlot)
        => GetPreferredSlots(item)
            .Any(slot => string.Equals(slot, uiSlot, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> GetPreferredSlots(RulesElement item)
    {
        if (string.Equals(item.Type, "Weapon", StringComparison.OrdinalIgnoreCase))
            return ["Main Hand", "Off-Hand"];

        if (string.Equals(item.Type, "Armor", StringComparison.OrdinalIgnoreCase))
        {
            if (item.Fields.TryGetValue("Armor Type", out var armorType)
                && string.Equals(armorType?.Trim(), "Shield", StringComparison.OrdinalIgnoreCase))
            {
                return ["Off-Hand"];
            }

            return ["Chest"];
        }

        if (!item.Fields.TryGetValue("Item Slot", out var rawSlot) || string.IsNullOrWhiteSpace(rawSlot))
            return [];

        var result = new List<string>();
        foreach (var slot in rawSlot.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (slot.Trim().ToLowerInvariant())
            {
                case "body":
                    result.Add("Chest");
                    break;
                case "head":
                    result.Add("Head");
                    break;
                case "neck":
                    result.Add("Neck");
                    break;
                case "arms":
                    result.Add("Arms");
                    break;
                case "hands":
                    result.Add("Hands");
                    break;
                case "waist":
                    result.Add("Waist");
                    break;
                case "feet":
                    result.Add("Feet");
                    break;
                case "weapon":
                    result.Add("Main Hand");
                    result.Add("Off-Hand");
                    break;
                case "off hand":
                case "off-hand":
                    result.Add("Off-Hand");
                    break;
                case "ring":
                    result.Add("Ring 1");
                    result.Add("Ring 2");
                    break;
                case "head and neck":
                    // CB original treats this as "fills both" (e.g. Circlet of
                    // Indomitability blocks Head and Neck simultaneously).
                    result.Add("Head");
                    result.Add("Neck");
                    break;
                case "ki focus":
                    result.Add("Ki Focus");
                    break;
                case "tattoo":
                    result.Add("Tattoo");
                    break;
                case "companion":
                    result.Add("Companion");
                    break;
                case "familiar":
                    result.Add("Familiar");
                    break;
                case "mount":
                    result.Add("Mount");
                    break;
                case "primordial shard":
                    result.Add("Primordial Shard");
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Determine whether the character is proficient with the given item.
    /// Returns true for non-Weapon/non-Armor types (proficiency is N/A).
    /// </summary>
    public bool IsProficientWith(RulesElement item)
    {
        if (_session is null) return false;

        bool isWeapon = string.Equals(item.Type, "Weapon", StringComparison.OrdinalIgnoreCase);
        bool isArmor = string.Equals(item.Type, "Armor", StringComparison.OrdinalIgnoreCase);
        if (!isWeapon && !isArmor) return true;

        var (specific, categories) = GetCharacterProficiencyTargets();
        if (specific.Count == 0 && categories.Count == 0) return false;

        // Specific proficiencies (e.g. "Weapon Proficiency (Longsword)") match by item Name only.
        // We deliberately do NOT match these against Group because, e.g., "Weapon Proficiency
        // (Spear)" means the simple Spear weapon — not every weapon in the Spear group (which
        // would falsely include superior weapons like the Tratnyr).
        if (specific.Contains(item.Name)) return true;

        // Armor proficiencies match the armor's category (Cloth / Leather / Hide / Chain /
        // Scale / Plate / shield names). NOT "Armor Type", which stores Light/Heavy/Shield —
        // those are weight classes, not the proficiency keyword. A character with "Armor
        // Proficiency (Cloth)" can wear anything whose Armor Category is "Cloth".
        if (isArmor && item.Fields.TryGetValue("Armor Category", out var acat) && !string.IsNullOrWhiteSpace(acat))
        {
            if (specific.Contains(acat.Trim())) return true;
        }

        // Category proficiencies match the weapon's Weapon Category (e.g., "Simple Melee").
        // Composite forms like "Military Spear" require BOTH the matching tier prefix on the
        // weapon's category AND the named group on the weapon.
        if (isWeapon && item.Fields.TryGetValue("Weapon Category", out var cat) && !string.IsNullOrWhiteSpace(cat))
        {
            var trimmedCat = cat.Trim();
            if (categories.Contains(trimmedCat)) return true;

            string[] groups = item.Fields.TryGetValue("Group", out var grp) && !string.IsNullOrWhiteSpace(grp)
                ? grp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

            // Tier of the weapon: first word of "Simple Melee" / "Military Ranged" / "Superior Melee"
            var tier = trimmedCat.Split(' ').FirstOrDefault();
            if (!string.IsNullOrEmpty(tier))
            {
                foreach (var c in categories)
                {
                    // e.g. "Military Spear" — tier "Military" then group "Spear"
                    if (!c.StartsWith(tier + " ", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var groupPart = c.Substring(tier.Length + 1).Trim();
                    if (groups.Any(g => string.Equals(g, groupPart, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
        }

        return false;
    }

    private (HashSet<string> Specific, HashSet<string> Categories)? _profTargetsCache;
    private int _profTargetsCacheGen = -1;

    private (HashSet<string> Specific, HashSet<string> Categories) GetCharacterProficiencyTargets()
    {
        if (_session is null)
            return (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

        int gen = _session.ChoiceHistory.Count;
        if (_profTargetsCache is { } cached && _profTargetsCacheGen == gen)
            return cached;

        var specific = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in _session.GetAllElementsOfType("Proficiency"))
        {
            int open = el.Name.IndexOf('(');
            int close = el.Name.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                // "Weapon Proficiency (Longsword)" / "Armor Proficiency (Plate)" — specific item.
                var inside = el.Name.Substring(open + 1, close - open - 1).Trim();
                foreach (var t in inside.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    specific.Add(t);
            }
            else if (el.Name.EndsWith(" proficiency", StringComparison.OrdinalIgnoreCase))
            {
                // "Heavy Blade proficiency" — treat the prefix as a category-style target.
                categories.Add(el.Name[..^" proficiency".Length].Trim());
            }
            else
            {
                // Bare-category Proficiency element, e.g. "Simple Melee", "Military Spear".
                categories.Add(el.Name.Trim());
            }
        }

        var result = (specific, categories);
        _profTargetsCache = result;
        _profTargetsCacheGen = gen;
        return result;
    }

    public PendingChoice? FindReplacementChoice(ChoiceRecord record)
    {
        if (_session is null)
            return null;

        return _session.GetAllPendingChoices()
            .Where(choice => string.Equals(choice.Slot.ElementType, record.Slot.ElementType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(choice => ScoreChoiceMatch(choice, record))
            .FirstOrDefault(choice => ScoreChoiceMatch(choice, record) > 0);
    }

    /// <summary>
    /// Resolve a comma-separated list of element IDs to their names and descriptions.
    /// Used for fields like "Racial Traits" that store raw IDs.
    /// Returns empty list if the value doesn't contain ID patterns.
    /// </summary>
    public List<ResolvedElement> ResolveElementIds(string idList)
    {
        var results = new List<ResolvedElement>();
        if (string.IsNullOrWhiteSpace(idList))
            return results;

        foreach (var token in idList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("ID_", StringComparison.OrdinalIgnoreCase))
                continue;

            var element = _db.FindByInternalId(token);
            if (element is not null)
            {
                element.Fields.TryGetValue("Short Description", out var desc);
                results.Add(new ResolvedElement(element.Name, desc, element.Type));
            }
        }

        return results;
    }

    private static int LevelSortKey(RulesElement element)
        => element.Fields.TryGetValue("Level", out var raw)
           && int.TryParse(raw, out var level)
            ? level
            : int.MaxValue;

    /// <summary>Check if a field value contains element ID references.</summary>
    public static bool ContainsElementIds(string? value)
        => value is not null && value.Contains("ID_FMP_", StringComparison.OrdinalIgnoreCase)
           || value is not null && value.Contains("ID_INTERNAL_", StringComparison.OrdinalIgnoreCase);

    private RulesElement? ResolveImportedElement(BuildChoice choice)
    {
        if (choice.InternalId is not null)
        {
            var byId = _db.FindByInternalId(choice.InternalId);
            if (byId is not null)
                return byId;
        }

        return _db.FindByNameAndType(choice.Name, choice.Type);
    }

    private RulesElement? ResolveLootElement(TallyElement item)
    {
        if (item.InternalId is not null)
        {
            var byId = _db.FindByInternalId(item.InternalId);
            if (byId is not null)
                return byId;
        }

        return _db.FindByNameAndType(item.Name, item.Type);
    }

    private void RestoreImportedEquipment(CharacterSession session, CharM.Serialization.CharacterSnapshot snapshot)
    {
        foreach (var entry in snapshot.Equipment.Where(e => e.EquipCount > 0))
        {
            var loot = BuildLootItem(entry);
            if (loot is null) continue;

            int remaining = Math.Max(1, entry.EquipCount);
            var preferredSlots = GetPreferredSlots(loot.Base);
            if (preferredSlots.Count == 0)
            {
                session.AddInventoryItem(loot, remaining);
                continue;
            }

            foreach (var slot in preferredSlots)
            {
                if (remaining == 0) break;
                if (session.GetEquippedLoot(slot) is null)
                {
                    session.EquipItem(slot, loot);
                    remaining--;
                }
            }

            if (remaining > 0)
                session.AddInventoryItem(loot, remaining);
        }

        foreach (var entry in snapshot.Equipment.Where(e => e.EquipCount == 0))
        {
            var loot = BuildLootItem(entry);
            if (loot is null) continue;
            session.AddInventoryItem(loot, Math.Max(0, entry.Count));
        }
    }

    private CharM.Engine.Creation.LootItem? BuildLootItem(LootEntry entry)
    {
        if (entry.Components.Count == 0) return null;

        var resolved = new List<(LootComponent Comp, RulesElement El)>();
        foreach (var c in entry.Components)
        {
            var el = ResolveLootElement(c.Element);
            if (el is null) continue;
            resolved.Add((c, el));
        }
        if (resolved.Count == 0) return null;

        var baseIdx = resolved.FindIndex(r => !string.Equals(r.El.Type, "Magic Item", StringComparison.OrdinalIgnoreCase));
        if (baseIdx < 0) baseIdx = 0;
        var baseEntry = resolved[baseIdx];

        RulesElement? enchant = null;
        RulesElement? augment = null;
        for (int i = 0; i < resolved.Count; i++)
        {
            if (i == baseIdx) continue;
            if (enchant is null) enchant = resolved[i].El;
            else if (augment is null) augment = resolved[i].El;
        }

        return new CharM.Engine.Creation.LootItem
        {
            Base = baseEntry.El,
            Enchantment = enchant,
            Augment = augment,
            WornCategoryId = baseEntry.Comp.WornCategoryId,
            CompositeName = entry.CompositeName,
            ShowPowerCard = entry.ShowPowerCard,
            Weight = entry.Weight,
            AugmentXml = entry.AugmentXml,
        };
    }

    // Whitelist removed: try every non-granted element; the slot matcher
    // is the gate. New element types from .part files Just Work.
    private static bool IsImportableChoiceType(string type) => !string.IsNullOrWhiteSpace(type);

    private static int ScoreChoiceMatch(PendingChoice pending, ChoiceRecord record)
    {
        int score = 0;
        if (string.Equals(pending.Slot.OwnerInternalId, record.Slot.OwnerInternalId, StringComparison.OrdinalIgnoreCase))
            score += 4;
        if (string.Equals(pending.Slot.Category, record.Slot.Category, StringComparison.OrdinalIgnoreCase))
            score += 3;
        if (string.Equals(pending.Slot.Name, record.Slot.Name, StringComparison.OrdinalIgnoreCase))
            score += 2;
        if (string.Equals(pending.Description, record.Slot.Name, StringComparison.OrdinalIgnoreCase))
            score += 1;
        return score;
    }

    public void Dispose()
    {
        if (_session is not null)
            _session.Changed -= OnSessionChanged;

        // Database is singleton, don't dispose it here
    }

    private void SetSession(CharacterSession session)
    {
        if (_session is not null)
            _session.Changed -= OnSessionChanged;

        _session = session;
        _displayPowerStatsCache = null;
        _sessionVersion++;
        _session.Changed += OnSessionChanged;
        Changed?.Invoke();
    }

    private void OnSessionChanged()
    {
        _displayPowerStatsCache = null;
        Changed?.Invoke();
    }
}

/// <summary>A resolved element reference: name + optional description.</summary>
public sealed record ResolvedElement(string Name, string? Description, string Type);
