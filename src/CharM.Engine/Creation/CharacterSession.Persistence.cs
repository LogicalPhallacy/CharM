using CharM.Engine.CharacterModel;
using CharM.Engine.Creation.Persistence;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;

namespace CharM.Engine.Creation;

public sealed partial class CharacterSession
{
    /// <summary>
    /// True when this session can be losslessly persisted via replay state
    /// (<see cref="ToReplayState"/>). False for characters imported from a
    /// .dnd4e file — those carry verbatim round-trip metadata (raw sections,
    /// captured level trees, source metadata) that the replay DTO doesn't model,
    /// so they continue to persist as .dnd4e bytes (which is already lossless and
    /// re-derives their pending choices on re-import). App-created characters
    /// have none of that metadata and lose their pending choices through a
    /// .dnd4e round-trip, which is exactly what replay persistence fixes.
    /// </summary>
    public bool IsReplayPersistable =>
        RawSections.Count == 0
        && CapturedLevelTrees.Count == 0
        && SourceMetadata.Count == 0
        && UnresolvedElements.Count == 0
        && SourceFlatTallyIds.Count == 0;

    /// <summary>
    /// Capture the full editable state of an app-created session for lossless
    /// replay restore. See <see cref="CharacterSessionStateDto"/> and
    /// <see cref="IsReplayPersistable"/>.
    /// </summary>
    public CharacterSessionStateDto ToReplayState()
    {
        var dto = new CharacterSessionStateDto
        {
            Level = Level,
            Name = _name,
            AbilityScores = _abilityScores?.ToArray(),
            SourceFilter = SourceFilter,
            EnabledSources = _enabledSources?.ToList(),
            AutoFillSelectDefaults = _autoFillSelectDefaults,
            IsCharacterHouseruled = IsCharacterHouseruled,
        };

        foreach (var (key, value) in Details)
            dto.Details[key] = value;
        foreach (var (key, value) in TextStrings)
            dto.TextStrings[key] = value;

        foreach (var record in _choiceHistory)
        {
            dto.Choices.Add(new ChoiceRecordDto
            {
                ElementId = record.Element.InternalId,
                Sequence = record.SequenceNumber,
                Level = record.Level,
                Slot = ToSlotDto(record.Slot),
            });
        }

        foreach (var grant in _grabbagGrants)
            dto.GrabbagGrantIds.Add(grant.InternalId);

        foreach (var supplement in _slotOwnedSupplements)
            dto.Supplements.Add(ToFreeGrantDto(supplement.Element.InternalId, supplement.AtLevel, supplement.SlotOwnerInternalId));

        foreach (var pick in _userEditPicks)
            dto.UserEditPicks.Add(ToFreeGrantDto(pick.Element.InternalId, pick.AtLevel, pick.SlotOwnerInternalId));

        foreach (var (slot, loot) in _equippedItems)
            dto.Equipped.Add(new EquippedItemDto { Slot = slot, Item = ToLootDto(loot) });

        foreach (var entry in _inventory)
            dto.Inventory.Add(new InventoryItemDto { Item = ToLootDto(entry.Item), Quantity = entry.Quantity });

        foreach (var (level, list) in _replacements)
        {
            var levelDto = new ReplacementLevelDto { Level = level };
            foreach (var r in list)
            {
                levelDto.Replacements.Add(new ReplacementDto
                {
                    OldInternalId = r.OldInternalId,
                    NewInternalId = r.NewInternalId,
                    NewName = r.NewName,
                    NewType = r.NewType,
                    PreserveOld = r.PreserveOld,
                    SwapOwnerInternalId = r.SwapOwnerInternalId,
                });
            }
            dto.Replacements.Add(levelDto);
        }

        foreach (var grant in _houseruleGrants)
        {
            dto.HouseruleGrants.Add(new HouseruleGrantDto
            {
                ElementId = grant.Element.InternalId,
                AtLevel = grant.AtLevel,
                Kind = grant.Kind.ToString(),
                Slot = grant.Slot,
                Quantity = grant.Quantity,
            });
        }

        return dto;
    }

    /// <summary>
    /// Reconstruct a session from replay state. Rebuilds the wizard tree (and the
    /// pending-choice slots) by replaying the choice history, then restores
    /// equipment, retraining swaps and houserule metadata. The supplied callbacks
    /// are the same rules-database lookups a fresh session uses.
    /// </summary>
    public static CharacterSession RestoreFromReplayState(
        CharacterSessionStateDto dto,
        Func<string, RulesElement?> findById,
        Func<string, string, RulesElement?> findByNameAndType,
        Func<string, bool, IEnumerable<RulesElement>> findByType,
        Func<string, string, bool, IEnumerable<RulesElement>>? findByTypeAndSource = null)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var session = new CharacterSession(
            findById, findByNameAndType, findByType, findByTypeAndSource,
            level: dto.Level <= 0 ? 1 : dto.Level,
            autoFillSelectDefaults: dto.AutoFillSelectDefaults);

        session._name = string.IsNullOrEmpty(dto.Name) ? "New Character" : dto.Name;
        session.SourceFilter = dto.SourceFilter;
        if (dto.EnabledSources is { Count: > 0 })
            session.EnabledSources = new HashSet<string>(dto.EnabledSources, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in dto.Details)
            session.Details[key] = value;
        foreach (var (key, value) in dto.TextStrings)
            session.TextStrings[key] = value;

        if (dto.AbilityScores is { Length: 6 })
        {
            var scores = new AbilityScoreSet();
            for (int i = 0; i < 6; i++)
                scores[(Ability)i] = dto.AbilityScores[i];
            session._abilityScores = scores;
        }

        int maxSequence = -1;
        foreach (var choice in dto.Choices)
        {
            var element = findById(choice.ElementId);
            if (element is null)
                continue; // element no longer in the loaded ruleset — skip gracefully
            session._choiceHistory.Add(new ChoiceRecord(element, FromSlotDto(choice.Slot), choice.Sequence, choice.Level));
            if (choice.Sequence > maxSequence)
                maxSequence = choice.Sequence;
        }
        session._sequenceCounter = maxSequence + 1;

        foreach (var id in dto.GrabbagGrantIds)
        {
            if (findById(id) is { } element)
                session._grabbagGrants.Add(element);
        }

        foreach (var supplement in dto.Supplements)
        {
            if (findById(supplement.ElementId) is { } element)
                session._slotOwnedSupplements.Add(new SlotOwnedSupplement(element, supplement.AtLevel, supplement.SlotOwnerInternalId));
        }

        foreach (var pick in dto.UserEditPicks)
        {
            if (findById(pick.ElementId) is { } element)
            {
                session._userEditPicks.Add(new UserEditPick(element, pick.AtLevel, pick.SlotOwnerInternalId));
                session._userEditPickIds.Add(element.InternalId);
            }
        }

        // Rebuild the wizard tree + pending slots from the replay inputs.
        session.RebuildFromHistory();

        // Restore equipment / inventory directly (already-known good state).
        foreach (var equipped in dto.Equipped)
        {
            if (FromLootDto(equipped.Item, findById) is { } loot)
                session._equippedItems[equipped.Slot] = loot;
        }
        foreach (var inv in dto.Inventory)
        {
            if (FromLootDto(inv.Item, findById) is { } loot)
                session._inventory.Add(new InventoryItem { Item = loot, Quantity = inv.Quantity });
        }

        foreach (var levelDto in dto.Replacements)
        {
            var list = new List<ElementReplacement>();
            foreach (var r in levelDto.Replacements)
            {
                list.Add(new ElementReplacement(
                    r.OldInternalId, r.NewInternalId, r.NewName, r.NewType, r.PreserveOld, r.SwapOwnerInternalId));
            }
            if (list.Count > 0)
                session._replacements[levelDto.Level] = list;
        }

        foreach (var grant in dto.HouseruleGrants)
        {
            if (findById(grant.ElementId) is not { } element)
                continue;
            var kind = Enum.TryParse<HouseruleGrantKind>(grant.Kind, out var k) ? k : HouseruleGrantKind.RulesElement;
            session._houseruleGrants.Add(new HouseruleGrant(element, grant.AtLevel, kind, grant.Slot, grant.Quantity));
        }

        session.IsCharacterHouseruled = dto.IsCharacterHouseruled;
        session.InvalidateSnapshot();
        return session;
    }

    private static ChoiceSlotDto ToSlotDto(ChoiceSlot slot) => new()
    {
        ElementType = slot.ElementType,
        Name = slot.Name,
        DisplayLabel = slot.DisplayLabel,
        Category = slot.Category,
        OwnerInternalId = slot.OwnerInternalId,
        Requires = slot.Requires,
        Number = slot.Number,
        Optional = slot.Optional,
        Existing = slot.Existing,
        Level = slot.Level,
    };

    private static ChoiceSlot FromSlotDto(ChoiceSlotDto dto) => new()
    {
        ElementType = dto.ElementType,
        Name = dto.Name,
        DisplayLabel = dto.DisplayLabel,
        Category = dto.Category,
        OwnerInternalId = dto.OwnerInternalId,
        Requires = dto.Requires,
        Number = dto.Number,
        Optional = dto.Optional,
        Existing = dto.Existing,
        Level = dto.Level,
        // DirectiveKey intentionally omitted — replay re-matches by owner/category/name.
    };

    private static FreeGrantDto ToFreeGrantDto(string id, int atLevel, string? slotOwner)
        => new() { ElementId = id, AtLevel = atLevel, SlotOwnerInternalId = slotOwner };

    private LootItemDto ToLootDto(LootItem loot) => new()
    {
        BaseId = loot.Base.InternalId,
        EnchantmentId = loot.Enchantment?.InternalId,
        AugmentId = loot.Augment?.InternalId,
        WornCategoryId = loot.WornCategoryId,
        CompositeName = loot.CompositeName,
        DamageOverride = loot.DamageOverride,
        ShowPowerCard = loot.ShowPowerCard,
        Weight = loot.Weight,
        AugmentXml = loot.AugmentXml,
        IsInAlternateSlot = loot.IsInAlternateSlot,
    };

    private static LootItem? FromLootDto(LootItemDto dto, Func<string, RulesElement?> findById)
    {
        var baseElement = findById(dto.BaseId);
        if (baseElement is null)
            return null;
        return new LootItem
        {
            Base = baseElement,
            Enchantment = dto.EnchantmentId is { Length: > 0 } e ? findById(e) : null,
            Augment = dto.AugmentId is { Length: > 0 } a ? findById(a) : null,
            WornCategoryId = dto.WornCategoryId,
            CompositeName = dto.CompositeName,
            DamageOverride = dto.DamageOverride,
            ShowPowerCard = dto.ShowPowerCard,
            Weight = dto.Weight,
            AugmentXml = dto.AugmentXml,
            IsInAlternateSlot = dto.IsInAlternateSlot,
        };
    }
}
