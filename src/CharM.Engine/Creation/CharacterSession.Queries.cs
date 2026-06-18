using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;
using System.Xml.Linq;

namespace CharM.Engine.Creation;

public sealed partial class CharacterSession
{
    // --- Querying ---

    /// <summary>Get all pending (unmade) choices across all steps.</summary>
    public IReadOnlyList<PendingChoice> GetAllPendingChoices()
        => _wizard.GetPendingChoices();

    /// <summary>Get candidate elements for a specific choice slot.</summary>
    public IReadOnlyList<RulesElement> GetCandidatesForSlot(
        ChoiceSlot slot, string? sourceFilter = null, bool skipPrereqs = false)
        => _wizard.GetCandidatesForSlot(slot, sourceFilter ?? SourceFilter, skipPrereqs);

    /// <summary>
    /// Get the currently selected element of a given type (Race, Class, etc.).
    /// Returns null if no selection of that type has been made.
    /// </summary>
    public RulesElement? GetSelectedElement(string type)
        => _choiceHistory
            .LastOrDefault(r =>
                string.Equals(r.Element.Type, type, StringComparison.OrdinalIgnoreCase))
            ?.Element;

    /// <summary>Get all selected elements of a given type.</summary>
    public IReadOnlyList<RulesElement> GetSelectedElements(string type)
        => _choiceHistory
            .Where(r => string.Equals(r.Element.Type, type, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Element)
            .ToList();

    /// <summary>Get choice history entries for a specific level.</summary>
    public IReadOnlyList<ChoiceRecord> GetChoicesAtLevel(int level)
        => _choiceHistory.Where(r => r.Level == level).ToList();

    /// <summary>Get accumulated export choices partitioned by character level.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<ElementChoice>> GetChoicesByLevel()
    {
        var result = _wizard.GetChoicesByLevel()
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToList());

        foreach (var supplement in _slotOwnedSupplements)
        {
            int level = supplement.AtLevel > 0 ? supplement.AtLevel : Level;
            if (!result.TryGetValue(level, out var list))
            {
                list = [];
                result[level] = list;
            }

            list.Add(new ElementChoice(
                supplement.Element.InternalId,
                supplement.Element.Name,
                supplement.Element.Type,
                supplement.SlotOwnerInternalId));
        }

        return result.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ElementChoice>)kv.Value);
    }

    /// <summary>
    /// Get all elements of a given type on the character, including auto-granted ones.
    /// Searches the wizard's internal tree, not just user choice history.
    /// </summary>
    public IReadOnlyList<RulesElement> GetAllElementsOfType(string type)
        => _wizard.ElementTree.GetActiveElements()
            .Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// True when an element should be presented as houseruled in UI/export
    /// surfaces. This intentionally preserves explicit source/modal markers
    /// only; it does not recompute prerequisite failures and promote otherwise
    /// rules-legal active elements to houserules.
    /// </summary>
    public bool IsHouseruledElement(string? internalId)
    {
        if (string.IsNullOrWhiteSpace(internalId))
            return false;

        if (_userEditPickIds.Contains(internalId))
            return true;

        if (HouseruledElementIds.Contains(internalId))
            return true;

        if (SourceMetadata.TryGetValue(internalId, out var metadata)
            && string.Equals(metadata.Legality, "houserule", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _unresolvedElements.Any(u =>
            string.Equals(u.InternalId, internalId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(u.Legality, "houserule", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Build structured companion data from live session state. Each active
    /// base-tier Companion element (e.g. Cat, Bear, Wolf — the one with the
    /// rich stat fields) becomes a <see cref="CompanionData"/> with the
    /// current ability scores from the StatBlock, the level-corrected HP/
    /// defenses from the matching <c>Companion: X</c> Power card via overlay,
    /// and the user-set name/appearance from the OCB text strings.
    /// Also surfaces Animal Master's Companion minion powers as separate
    /// CompanionData entries (those store their stats inline on the power).
    /// </summary>
    public IReadOnlyList<CompanionData> GetCompanionData()
    {
        var snapshot = GetPartialSnapshot();
        if (snapshot is null)
            return Array.Empty<CompanionData>();

        var allElements = _wizard.ElementTree.GetActiveElements().ToList();
        var baseCompanions = allElements
            .Where(e => string.Equals(e.Type, "Companion", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Fields.ContainsKey("Hit Points at 1st Level"))
            .ToList();

        var companionPowers = allElements
            .Where(e => string.Equals(e.Type, "Power", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Name.StartsWith("Companion:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var animalMasterPowers = allElements
            .Where(e => string.Equals(e.Type, "Power", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Name.StartsWith("Animal Master's Companion:", StringComparison.OrdinalIgnoreCase)
                || e.Name.StartsWith("Animal Companion:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Summoning powers with embedded creature stat blocks (Hit Points + Defenses
        // fields) — Summon Angel of Fire, Summoned Sidhe Ally, Skeletal Warrior, etc.
        // Detected by name prefix and the presence of stat-block fields. Excluded
        // when already covered as animal companions.
        var animalIds = new HashSet<string>(animalMasterPowers.Select(p => p.InternalId), StringComparer.OrdinalIgnoreCase);
        var summonPowers = allElements
            .Where(e => string.Equals(e.Type, "Power", StringComparison.OrdinalIgnoreCase))
            .Where(e => !animalIds.Contains(e.InternalId))
            .Where(e => (e.Name.StartsWith("Summon ", StringComparison.OrdinalIgnoreCase)
                         || e.Name.StartsWith("Summoned ", StringComparison.OrdinalIgnoreCase))
                     && e.Fields.ContainsKey("Hit Points")
                     && e.Fields.ContainsKey("Defenses"))
            .ToList();

        // Familiar power cards (e.g. Disembodied Hand, Pseudodragon, Owl).
        // Granted by Arcane Familiar feat → Familiar element → Power element.
        // Detected by Name prefix or the (Constant Benefits + Secondary Speed)
        // field pair. Rendered as a stat-light mini-sheet (no abilities, no
        // defenses) to mirror OCB's familiar card layout.
        var familiarPowers = allElements
            .Where(CompanionData.IsFamiliarPower)
            .ToList();

        // Associate-typed inventory entries (Sentinel druid Living Zephyr,
        // Heroes of the Feywild Dire Bear, etc.). OCB stores these as
        // <RulesElement type="Associate"> inside <LootTally> — NOT in the
        // active element tree — so we have to scan _inventory instead of
        // allElements. The structured stat block lives on a matching
        // Power card (same Name, typically with internal-id
        // ID_TIV_COMPANION_<NAME> or similar) which IS in the element
        // tree when the granting class feature is active.
        var associates = _inventory
            .Select(i => i.Item.Base)
            .Where(e => string.Equals(e.Type, "Associate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (baseCompanions.Count == 0 && animalMasterPowers.Count == 0
            && summonPowers.Count == 0 && familiarPowers.Count == 0
            && associates.Count == 0)
            return Array.Empty<CompanionData>();

        string? name = TextStrings.GetValueOrDefault("_COMPANION_NAME");
        string? appearance = TextStrings.GetValueOrDefault("_COMPANION_APPEARANCE");

        var result = new List<CompanionData>();

        foreach (var companion in baseCompanions)
        {
            var powerCard = companionPowers.FirstOrDefault(p =>
                p.Name.EndsWith(": " + companion.Name, StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Companion: " + companion.Name, StringComparison.OrdinalIgnoreCase));

            result.Add(CompanionData.From(
                companion,
                powerCard,
                snapshot,
                Level,
                name,
                appearance,
                _findById));
        }

        foreach (var animal in animalMasterPowers)
        {
            result.Add(CompanionData.FromAnimalCompanionPower(
                animal,
                snapshot.Builder.Overlay,
                Level,
                customName: null,
                customAppearance: null));
        }

        foreach (var summon in summonPowers)
        {
            result.Add(CompanionData.FromSummonPower(
                summon,
                snapshot.Builder.Overlay,
                Level));
        }

        foreach (var familiar in familiarPowers)
        {
            result.Add(CompanionData.FromFamiliarPower(familiar));
        }

        foreach (var associate in associates)
        {
            // Match the Associate to its structured Power card by Name.
            // ID_TIV_COMPANION_LIVING_ZEPHYR (Power) ↔ ID_FMP_ASSOCIATE_64
            // (Associate, "Living Zephyr") — only the Name is reliable
            // across the two id families.
            var powerCard = allElements.FirstOrDefault(e =>
                string.Equals(e.Type, "Power", StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Name, associate.Name, StringComparison.OrdinalIgnoreCase));

            // Resolve character stats for template substitution
            int? charHp = snapshot.Builder.Stats.TryGetStat("Hit Points")
                ?.ComputeValue(snapshot.Builder.Stats);
            int? charInit = snapshot.Builder.Stats.TryGetStat("Initiative")
                ?.ComputeValue(snapshot.Builder.Stats);
            int? charPerception = snapshot.Builder.Stats.TryGetStat("Perception")
                ?.ComputeValue(snapshot.Builder.Stats);

            // CHARACTER's ability scores for "your X modifier" resolution
            var charAbilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (_abilityScores is not null)
            {
                charAbilities["Strength"] = _abilityScores[Ability.Strength];
                charAbilities["Constitution"] = _abilityScores[Ability.Constitution];
                charAbilities["Dexterity"] = _abilityScores[Ability.Dexterity];
                charAbilities["Intelligence"] = _abilityScores[Ability.Intelligence];
                charAbilities["Wisdom"] = _abilityScores[Ability.Wisdom];
                charAbilities["Charisma"] = _abilityScores[Ability.Charisma];
            }

            result.Add(CompanionData.FromAssociate(
                associate,
                powerCard,
                snapshot.Builder.Overlay,
                customName: name,
                customAppearance: appearance,
                characterHp: charHp,
                characterLevel: Level,
                characterAbilities: charAbilities,
                characterInitiative: charInit,
                characterPerception: charPerception));
        }

        // OCB iterates ALL active type="Companion" CharElements when emitting
        // <Companions>/<Beast> blocks (see decompiled
        // D20RulesEngine -Module-.cs WriteCompanions, BeastBlock.ToXML). The
        // base creature template (Cat, Bear, Wolf, ...) emits a fully populated
        // Beast block; any other active Companion element (typically per-level
        // overlays like Tivaan's "Companion Cat 2", "Companion Cat 3" etc.)
        // emits a "placeholder" Beast block sharing the active creature's
        // identity, ability scores, HP, attack bonus, and damage, but with
        // empty Size/Speed/TrainedSkills, an empty attack name, and
        // level-only Defenses (AC=Fort=Reflex=Will=character level since the
        // overlay element has no Armor Class / Fortitude Defense / etc.
        // fields and no Companion.* defensive stat exists). We mirror that
        // here by cloning the first base companion into placeholder entries
        // for each extra Companion CharElement.
        var firstBase = result.FirstOrDefault(c => !c.IsMinion && !c.IsSummon && !c.IsFamiliar);
        if (firstBase is not null)
        {
            var coveredIds = new HashSet<string>(
                baseCompanions.Select(c => c.InternalId),
                StringComparer.OrdinalIgnoreCase);
            var extraCompanionElements = allElements
                .Where(e => string.Equals(e.Type, "Companion", StringComparison.OrdinalIgnoreCase))
                .Where(e => !coveredIds.Contains(e.InternalId));
            foreach (var _ in extraCompanionElements)
            {
                result.Add(firstBase with { IsPlaceholderForActiveBeast = true });
            }
        }

        return result;
    }

    /// <summary>
    /// Build and cache a snapshot of the character's current state.
    /// The snapshot contains computed stats, element tree, and choice data.
    /// Invalidated automatically when choices change.
    /// </summary>
    public CharacterSnapshot? GetSnapshot()
    {
        if (_cachedSnapshot is not null)
            return _cachedSnapshot;

        if (!_wizard.IsComplete)
            return null;

        var result = _wizard.Build(equippedItems: GetEquipmentChoices(),
            replacements: GetReplacementsByLevel(),
            inventoryDirectiveItems: GetInventoryDirectiveChoices());
        if (!result.Success || result.Builder is null)
            return null;

        _cachedSnapshot = new CharacterSnapshot
        {
            Builder = result.Builder,
            AccumulatedChoices = _wizard.AccumulatedChoices.ToList(),
            AbilityScores = _abilityScores,
            Level = Level,
            PowerStatsExcludedIds = new HashSet<string>(_powerStatsExcludedIds, StringComparer.OrdinalIgnoreCase),
            LevelNestedOnlyIds = new HashSet<string>(_levelNestedOnlyIds, StringComparer.OrdinalIgnoreCase),
            UserEditPickIds = new HashSet<string>(_userEditPickIds, StringComparer.OrdinalIgnoreCase),
        };

        return _cachedSnapshot;
    }

    /// <summary>
    /// Force-build a snapshot even if IsComplete is false.
    /// Useful for showing partial stats during character creation.
    /// </summary>
    public CharacterSnapshot? GetPartialSnapshot()
    {
        if (_cachedSnapshot is not null)
            return _cachedSnapshot;

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

            var buildChoices = new Dictionary<int, IReadOnlyList<ElementChoice>>();
            var choicesByLevel = _wizard.GetChoicesByLevel();
            for (int lvl = 1; lvl <= Level; lvl++)
            {
                buildChoices[lvl] = choicesByLevel.TryGetValue(lvl, out var list)
                    ? list
                    : (IReadOnlyList<ElementChoice>)Array.Empty<ElementChoice>();
            }

            builder.Build(baseScores, buildChoices,
                elementTally: _wizard.GrabbagGrants.Count > 0 ? _wizard.GrabbagGrants : null,
                equippedItems: GetEquipmentChoices(),
                replacements: GetReplacementsByLevel(),
                inventoryDirectiveItems: GetInventoryDirectiveChoices());

            _cachedSnapshot = new CharacterSnapshot
            {
                Builder = builder,
                AccumulatedChoices = _wizard.AccumulatedChoices.ToList(),
                AbilityScores = _abilityScores,
                Level = Level,
                PowerStatsExcludedIds = new HashSet<string>(_powerStatsExcludedIds, StringComparer.OrdinalIgnoreCase),
                LevelNestedOnlyIds = new HashSet<string>(_levelNestedOnlyIds, StringComparer.OrdinalIgnoreCase),
                UserEditPickIds = new HashSet<string>(_userEditPickIds, StringComparer.OrdinalIgnoreCase),
            };

            return _cachedSnapshot;
        }
        catch
        {
            return null;
        }
    }
}
