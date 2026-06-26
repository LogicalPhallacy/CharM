using CharM.Engine.CharacterModel;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;
using System.Xml.Linq;

namespace CharM.Engine.Creation;

public sealed partial class CharacterSession
{
    // --- Private ---

    private CharacterCreationWizard CreateWizard()
    {
        var wizard = new CharacterCreationWizard(
            _findById, _findByNameAndType, _findByType,
            _findByTypeAndSource, Level,
            autoFillSelectDefaults: _autoFillSelectDefaults);
        wizard.SetEnabledSources(_enabledSources);
        return wizard;
    }

    /// <summary>
    /// Rebuild the wizard from scratch and replay all choices from history.
    /// This is how undo works — the wizard itself doesn't support removal,
    /// but replaying is fast enough (~ms for L1, ~50ms for L30).
    /// </summary>
    private void RebuildFromHistory()
    {
        _wizard = CreateWizard();

        if (_abilityScores is not null)
            _wizard.SetAbilityScores(_abilityScores);

        foreach (var record in _choiceHistory)
        {
            var fullElement = _findById(record.Element.InternalId) ?? record.Element;
            _wizard.MakeChoiceForSlot(fullElement, record.Slot);
        }

        foreach (var grant in _grabbagGrants)
        {
            var fullElement = _findById(grant.InternalId) ?? grant;
            _wizard.AddFreeGrant(fullElement);
        }

        foreach (var supplement in _slotOwnedSupplements)
        {
            var fullElement = _findById(supplement.Element.InternalId) ?? supplement.Element;
            _wizard.AddFreeGrant(fullElement, supplement.AtLevel, supplement.SlotOwnerInternalId);
        }

        foreach (var pick in _userEditPicks)
        {
            var fullElement = _findById(pick.Element.InternalId) ?? pick.Element;
            _wizard.AddFreeGrant(fullElement, pick.AtLevel, pick.SlotOwnerInternalId);
        }

        _wizard.ProcessDeferredChoices();
        InvalidateSnapshot();
        NotifyChanged();
    }

    private void InvalidateSnapshot() => _cachedSnapshot = null;

    private List<ElementChoice>? GetEquipmentChoices()
    {
        if (_equippedItems.Count == 0)
            return null;

        // Each equipped composite contributes every component (Base, Enchantment,
        // Augment) so the engine grants their rules (proficiencies, bonuses, etc.).
        // Worn-state categories captured under equipped loot also participate in
        // requires checks such as WearingOffHandLightBlade.
        var choices = new List<ElementChoice>();
        foreach (var loot in _equippedItems.Values)
        {
            foreach (var element in loot.Components())
                choices.Add(new ElementChoice(element.InternalId, element.Name, element.Type));

            if (!string.IsNullOrWhiteSpace(loot.WornCategoryId)
                && _findById(loot.WornCategoryId) is { } wornCategory)
            {
                choices.Add(new ElementChoice(wornCategory.InternalId, wornCategory.Name, wornCategory.Type));
            }
        }

        return choices;
    }

    /// <summary>
    /// Inventory items (count > 0) whose RulesElement carries any active
    /// directive (GrantDirective or StatAddDirective). These represent boons
    /// (Glory Boon / Grandmaster Training etc., all type=Magic Item),
    /// Wondrous Items with mechanical bonuses (Spyglass of Perception, Bag
    /// of Holding), and Deck of Many Things card-effect items. They live in
    /// the bag but their Phase1 directives still fire in OCB. Items with
    /// only descriptive Power-text fields (Delver's Light, Hunter's Flint)
    /// are excluded — they have no directives so this returns nothing for
    /// them and the engine does no work.
    /// </summary>
    private List<ElementChoice>? GetInventoryDirectiveChoices()
    {
        if (_inventory.Count == 0)
            return null;

        List<ElementChoice>? result = null;
        foreach (var entry in _inventory)
        {
            if (entry.Quantity <= 0) continue;
            foreach (var component in entry.Item.Components())
            {
                if (!IsActiveInventoryDirectiveItem(component)) continue;
                result ??= new List<ElementChoice>();
                result.Add(new ElementChoice(component.InternalId, component.Name, component.Type));
            }
        }
        return result;
    }

    /// <summary>
    /// True for inventory items that should fire Phase1 directives even
    /// while sitting in the bag: type=Magic Item, no Item Slot, and at least
    /// one GrantDirective or StatAddDirective in its Rules.
    /// <list type="bullet">
    /// <item><description>type=Magic Item filter excludes plain Armor/Weapon/Gear sitting unequipped — those should be inert until worn.</description></item>
    /// <item><description>"No Item Slot" filter excludes magic items meant to be worn (rings, head, etc.) that happen to be in inventory because the slot is full.</description></item>
    /// <item><description>The directive filter avoids work for Wondrous Items that are pure-text (Delver's Light, Hunter's Flint).</description></item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Magic Item Type values that attach to a Base equipped item (armor /
    /// weapon / implement / ammunition) rather than living independently in
    /// the inventory. These items only have effect when their Base is currently
    /// worn/wielded, so they must NOT fire Phase1 directives while sitting in
    /// the bag — that would double-count enhancement bonuses (e.g. emit two
    /// <c>Enhancement</c> contributions to AC when the character owns two
    /// different magic-armor enchantments but is only wearing one).
    /// </summary>
    private static readonly HashSet<string> AttachedMagicItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Armor",
        "Weapon",
        "Ammunition",
        "Whetstones",
        "Holy Symbol",
        "Ki Focus",
        "Rod",
        "Staff",
        "Tome",
        "Totem",
        "Wand",
        "Orb", // not in the observed type list but symmetric with the other implement types
    };

    private static bool IsActiveInventoryDirectiveItem(RulesElement element)
    {
        if (!string.Equals(element.Type, "Magic Item", StringComparison.OrdinalIgnoreCase))
            return false;
        if (element.Fields.TryGetValue("Item Slot", out var slot)
            && !string.IsNullOrWhiteSpace(slot))
            return false;
        // Magic items that attach to a Base (armor/weapon/implement enchantments)
        // only fire when the Base is equipped — handled by ProcessEquipment.
        // Skip them in the inventory-directive path so a non-equipped magic
        // armor doesn't double-stack its Enhancement bonus on AC.
        if (element.Fields.TryGetValue("Magic Item Type", out var mit)
            && !string.IsNullOrWhiteSpace(mit)
            && AttachedMagicItemTypes.Contains(mit.Trim()))
            return false;
        return HasActiveDirective(element);
    }

    private static bool HasActiveDirective(RulesElement element)
    {
        foreach (var rule in element.Rules)
        {
            if (rule is GrantDirective || rule is StatAddDirective)
                return true;
        }
        return false;
    }

    private static int LevelSortKey(RulesElement element)
        => element.Fields.TryGetValue("Level", out var raw)
           && int.TryParse(raw, out var level)
            ? level
            : int.MaxValue;

    /// <summary>
    /// When true, <see cref="ProcessFreebees"/> defers all FREEBEE work
    /// until the suppression is lifted. The importer sets this during
    /// replay so that class-feature FREEBEEs (e.g. Wizard's Spellbook
    /// gear) don't add duplicate inventory items before the source file's
    /// own loot has been restored. The first non-suppressed call after
    /// the source loot is in place can then dedup against the existing
    /// inventory.
    /// </summary>
    public bool SuppressFreebees { get; set; }

    private void NotifyChanged()
    {
        if (!SuppressFreebees)
            ProcessFreebees();
        Changed?.Invoke();
    }

    /// <summary>
    /// Public entry point to flush deferred FREEBEE grants when
    /// <see cref="SuppressFreebees"/> was true during the work that
    /// queued them. Used by the importer to re-run FREEBEE processing
    /// after the source loot has been restored, so the dedup check in
    /// <see cref="ProcessFreebees"/> can compare against the already-
    /// imported inventory.
    /// </summary>
    /// <param name="grantMissing">
    /// When true (default), grants any FREEBEE whose target item isn't
    /// already on the character. When false, only marks the FREEBEEs as
    /// processed (and stamps sentinels for items already present);
    /// the importer passes false so that re-import of a source missing
    /// both the item and the sentinel matches the source verbatim instead
    /// of synthesizing a granted item the user had intentionally declined.
    /// </param>
    public void RunPendingFreebees(bool grantMissing = true) => ProcessFreebees(grantMissing);

    /// <summary>
    /// Check for new FREEBEE item IDs from the wizard and auto-add them to
    /// inventory. Mirrors the OCB's GiveFreebees() workflow: each FREEBEE is
    /// processed once and tracked so it isn't re-added on subsequent calls.
    /// On import this is also called after the source file's loot has been
    /// loaded, so we additionally skip any FREEBEE whose target item is
    /// already equipped or inventoried — otherwise re-importing a wizard
    /// adds a duplicate spellbook each time the Spellbook class feature
    /// re-processes its FREEBEE: directive.
    /// </summary>
    private void ProcessFreebees(bool grantMissing = true)
    {
        foreach (var id in _wizard.FreebeeIds)
        {
            if (!_processedFreebeeIds.Add(id))
                continue;

            // Skip empty FREEBEE: values (e.g., "Vistani Heritage" has FREEBEE: with no ID)
            if (string.IsNullOrWhiteSpace(id))
                continue;

            // OCB convention: when Freebee() fires, MainWindow.cs writes a
            // textstring named after the FREEBEE item id with a non-empty
            // sentinel value ("Given" in observed corpus, originally "..."
            // in the decompile commentary). The sentinel survives across
            // saves, so OCB never re-fires the FREEBEE even if the user
            // manually removes the item from inventory. On import we honor
            // the same convention: if the sentinel is present, the source's
            // loot is authoritative — do not re-add a deleted item.
            if (TextStrings.TryGetValue(id, out var sentinel) && !string.IsNullOrWhiteSpace(sentinel))
                continue;

            var element = _findById(id);
            if (element is null)
                continue;

            if (CharacterAlreadyHasItem(id))
            {
                // Item already present from the source — preserve the
                // source's sentinel state verbatim (do NOT stamp). OCB only
                // writes the sentinel from MainWindow.GiveFreebees, which is
                // invoked from the AutoLevel/Gear/Rituals pages, not from
                // the file-save path. A character saved without visiting
                // those pages keeps the FREEBEE alias + the granted item
                // but no sentinel; round-trip fidelity requires us not to
                // synthesize one. Duplication is still prevented by this
                // CharacterAlreadyHasItem gate, so skipping the stamp is
                // safe across re-imports.
                continue;
            }

            // No item, no sentinel. In the post-import (round-trip) case
            // this signals "user declined the FREEBEE in OCB" — either
            // they removed the item after a page-transition grant, or
            // they never visited the page that would have granted it.
            // Either way, OCB's saved state lacks the item, and parity
            // requires we don't synthesize one. The importer passes
            // grantMissing:false to enforce this. Fresh-wizard flows
            // (web UI character creation) still grant via grantMissing:true.
            if (!grantMissing)
                continue;

            // Weapons get equipped in the appropriate slot if possible
            if (element.Type.Equals("Weapon", StringComparison.OrdinalIgnoreCase))
            {
                if (!_equippedItems.ContainsKey("Main Hand"))
                    EquipItem("Main Hand", element);
                else if (!_equippedItems.ContainsKey("Off-Hand"))
                    EquipItem("Off-Hand", element);
                else
                    AddInventoryItem(element, 1);
            }
            else
            {
                AddInventoryItem(element, 1);
            }

            // We just granted the FREEBEE — stamp the OCB sentinel so
            // subsequent reloads don't re-grant a user-removed item.
            TextStrings[id] = "Given";
        }
    }

    /// <summary>
    /// True if the given item InternalId is already present in either an
    /// equipped slot, the open inventory list (matched against any loot
    /// component, so an Enhancement or Augment InternalId still counts as
    /// "the character has the base"), OR anywhere in the active rules-element
    /// tree (granted by a class feature, magic item, recipe, etc). Used to
    /// suppress FREEBEE re-grants on a re-import where the source file
    /// already carries the item — including the case where the item was
    /// added via a class-feature grant cascade (e.g. Carrion Crawler Brain
    /// Juice granted by the Recipe class feature, where the source has
    /// the item in the tally + level tree but NOT in LootTally).
    /// </summary>
    private bool CharacterAlreadyHasItem(string internalId)
    {
        foreach (var (_, loot) in _equippedItems)
        {
            foreach (var component in loot.Components())
            {
                if (string.Equals(component.InternalId, internalId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        foreach (var entry in _inventory)
        {
            if (entry.Quantity <= 0) continue;
            foreach (var component in entry.Item.Components())
            {
                if (string.Equals(component.InternalId, internalId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        // Rules-element tree: catches items granted via class-feature /
        // magic-item / recipe grant cascades that don't surface as loot.
        foreach (var element in _wizard.ElementTree.GetActiveElements())
        {
            if (string.Equals(element.InternalId, internalId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
