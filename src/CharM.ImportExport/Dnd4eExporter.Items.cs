namespace CharM.ImportExport;
using CharM.Engine.Creation;
using CharM.Engine.Evaluation;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;
using CharM.Serialization;
public static partial class Dnd4eExporter
{
    /// <summary>
    /// Replicates OCB's <c>weapon_sort</c>: equipped first, main-hand
    /// first, higher Level first, enchanted-first as tiebreaker, otherwise
    /// stable. Sort runs once globally; the same ordering is reused across
    /// all powers
    /// </summary>
    private sealed class OcbWeaponSortComparer : IComparer<(LootItem loot, int idx)>
    {
        private readonly HashSet<string> _equippedKeys;
        private readonly Dictionary<string, string> _slotByKey;

        public OcbWeaponSortComparer(HashSet<string> equippedKeys,
            Dictionary<string, string> slotByKey)
        {
            _equippedKeys = equippedKeys;
            _slotByKey = slotByKey;
        }

        public int Compare((LootItem loot, int idx) x, (LootItem loot, int idx) y)
        {
            // 1. Equipped first.
            bool xEq = _equippedKeys.Contains(x.loot.CompositeKey);
            bool yEq = _equippedKeys.Contains(y.loot.CompositeKey);
            if (xEq != yEq) return xEq ? -1 : 1;

            // 2. Main-hand first (when slots differ — Ki composites would
            //    inherit their parent's slot; we don't synthesize those yet
            //    so the parent==self collapse is fine).
            _slotByKey.TryGetValue(x.loot.CompositeKey, out var xSlot);
            _slotByKey.TryGetValue(y.loot.CompositeKey, out var ySlot);
            if (!string.Equals(xSlot, ySlot, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(xSlot, "Main Hand", StringComparison.OrdinalIgnoreCase)) return -1;
                if (string.Equals(ySlot, "Main Hand", StringComparison.OrdinalIgnoreCase)) return 1;
            }

            // 3. Higher Level first.
            int xLvl = LevelOf(x.loot);
            int yLvl = LevelOf(y.loot);
            if (xLvl != yLvl) return yLvl - xLvl;

            // 4. Enchanted-first as tiebreaker.
            bool xEnch = x.loot.Enchantment is not null;
            bool yEnch = y.loot.Enchantment is not null;
            if (xEnch != yEnch) return xEnch ? -1 : 1;

            // 5. Stable: keep original input order.
            return x.idx - y.idx;
        }

        private static int LevelOf(LootItem loot)
        {
            int baseLvl = ParseLevel(loot.Base);
            int enchLvl = loot.Enchantment is null ? 0 : ParseLevel(loot.Enchantment);
            return Math.Max(baseLvl, enchLvl);
        }

        private static int ParseLevel(RulesElement re)
        {
            if (re.Fields.TryGetValue("Level", out var raw)
                && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var lvl))
            {
                return lvl;
            }
            return 0;
        }
    }

    /// <summary>
    /// True if the loot's base RulesElement should be considered a usable
    /// weapon-or-implement and surfaced to PowerStatsBuilder. Mirrors what
    /// OCB's BuildWeaponsList enumerates: the canonical Weapon and
    /// Implement type-buckets, plus Superior Implements (modern variants
    /// like Mindwarp staff, Accurate Ki Focus), plus the small set of
    /// Gear-typed "basic" implement bases (Holy Symbol, Ki Focus, Totem,
    /// Orb/Rod/Staff/Tome/Wand Implement), plus baseless Magic Items
    /// whose <c>Magic Item Type</c> identifies them as a self-contained
    /// weapon or implement (Aversion Staff, Wand of Shield, Holy
    /// Avenger, etc.). Per-power validity (Strength vs. Wisdom, ranged
    /// vs. melee, etc.) is enforced later by <c>IsValidPowerCombo</c> /
    /// <c>IsValidImplement</c>; this filter only decides whether the
    /// loot enters the candidate pool at all.
    /// </summary>
    private static bool IsWeaponOrImplementBase(RulesElement baseElem)
    {
        var type = baseElem.Type ?? string.Empty;
        if (string.Equals(type, "Weapon", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Implement", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Superior Implement", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(type, "Gear", StringComparison.OrdinalIgnoreCase))
        {
            return BasicImplementGearNames.Contains(baseElem.Name ?? string.Empty);
        }
        if (string.Equals(type, "Magic Item", StringComparison.OrdinalIgnoreCase))
        {
            // Baseless magic items: a Magic Item appearing alone in a <loot>
            // block (no underlying Weapon / Implement base) IS the
            // wieldable. The "Magic Item Type" field tags it as Weapon,
            // Staff, Wand, Holy Symbol, etc.
            if (baseElem.Fields.TryGetValue("Magic Item Type", out var mit) && !string.IsNullOrWhiteSpace(mit))
            {
                if (MagicItemImplementOrWeaponTypes.Contains(mit.Trim()))
                    return true;
            }
            // Wondrous Items / Artifacts with `_ImplementEquiv` (e.g. the
            // Bard instrument family: Vistani Tambourine, Anstruth Harp,
            // Fochlucan Bandore, etc.) are baseless magic items that count
            // as implements for any implement power. OCB exposes them via
            // the same ImplementName/IsValidImplement path as dedicated ones.
            if (baseElem.Fields.TryGetValue("_ImplementEquiv", out var equiv)
                && !string.IsNullOrWhiteSpace(equiv))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> BasicImplementGearNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Holy Symbol",
        "Ki Focus",
        "Totem",
        "Orb Implement",
        "Rod Implement",
        "Staff Implement",
        "Tome Implement",
        "Wand Implement",
    };

    private static readonly HashSet<string> MagicItemImplementOrWeaponTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weapon",
        "Staff",
        "Wand",
        "Rod",
        "Orb",
        "Tome",
        "Totem",
        "Holy Symbol",
        "Symbol",
        "Ki Focus",
    };
    /// <summary>
    /// Collect every InternalId belonging to a currently-equipped or
    /// inventoried loot composite (Base + Enchantment + Augment for each).
    /// Used by the tally builder to exclude equipment subtrees.
    /// </summary>
    private static HashSet<string> CollectEquipmentInternalIds(CharacterSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, loot) in session.GetEquippedLoot())
            AddLootIds(loot, ids);
        foreach (var inv in session.GetInventory())
            AddLootIds(inv.Item, ids);
        return ids;

        static void AddLootIds(CharM.Engine.Creation.LootItem loot, HashSet<string> ids)
        {
            foreach (var c in loot.Components())
                if (!string.IsNullOrEmpty(c.InternalId))
                    ids.Add(c.InternalId);
        }
    }

    private static void AddInventoryAlchemicalTally(
        List<TallyElement> tally,
        CharacterSession session,
        HashSet<string> existingIds)
    {
        foreach (var inventory in session.GetInventory())
        {
            if (inventory.Quantity <= 0) continue;

            var item = inventory.Item.Base;
            if (!IsAlchemicalInventoryTallyItem(item)) continue;
            if (string.IsNullOrEmpty(item.InternalId)) continue;
            if (!session.SourceFlatTallyIds.Contains(item.InternalId)) continue;
            if (!existingIds.Add(item.InternalId)) continue;

            tally.Add(BuildTallyEntry(item, session, sessionSwapReplaces: null));
        }
    }

    private static bool IsAlchemicalInventoryTallyItem(RulesElement element)
    {
        if (!string.Equals(element.Type, "Magic Item", StringComparison.OrdinalIgnoreCase))
            return false;
        if (element.Fields.TryGetValue("Item Slot", out var slot)
            && !string.IsNullOrWhiteSpace(slot))
            return false;
        return element.Fields.TryGetValue("Magic Item Type", out var magicItemType)
            && string.Equals(magicItemType.Trim(), "Alchemical", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// inventory weapons.
    /// </summary>
    private static IEnumerable<LootItem> BuildKiFocusPairs(CharacterSession session)
    {
        var equippedOnly = session.GetEquippedLoot().Values.ToList();

        var equippedKiFocuses = equippedOnly
            .Where(IsKiFocusLoot)
            .ToList();
        if (equippedKiFocuses.Count == 0) yield break;

        // OCB iterates ALL loot with count >= 1 for the weapon-base pool —
        // both equipped and inventory weapons get paired with an equipped
        // Ki Focus. 
        var weaponBasesPool = equippedOnly
            .Concat(session.GetInventory()
                .Where(inv => inv.Quantity >= 1)
                .Select(inv => inv.Item))
            .Where(l => string.Equals(l.Base.Type, "Weapon", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Base)
            .DistinctBy(b => b.InternalId ?? b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (weaponBasesPool.Count == 0) yield break;

        foreach (var ki in equippedKiFocuses)
        {
            // OCB's BuildKiList passes the Ki Focus RulesElement as the
            // synthetic enchant. For our model, prefer the loot's
            // Enchantment (Ki Focus magic-item layered on a base "Ki
            // Focus" Gear) when present, falling back to the loot's Base
            // (the baseless "Rain of Hammers Ki Focus +1" Magic Item case).
            var kiElem = ki.Enchantment ?? ki.Base;
            foreach (var weaponBase in weaponBasesPool)
            {
                yield return new LootItem
                {
                    Base = weaponBase,
                    Enchantment = kiElem,
                };
            }
        }
    }

    /// <summary>
    /// Returns true when the loot's BASE element IS a Ki Focus
    /// (the Gear-typed "Ki Focus" base, OR a Magic Item with
    /// <c>Magic Item Type = "Ki Focus"</c>). OCB only checks the base
    /// (via <c>WeaponBase</c>), NOT the enchantment — a Fluid Ki Focus
    /// (Superior Implement) enchanted with a Ki Focus magic item like
    /// "Blazing Arc Ki Focus +3" does NOT trigger weapon pairing,
    /// because the base is a Superior Implement, not a Ki Focus.
    /// </summary>
    private static bool IsKiFocusLoot(LootItem loot)
    {
        return IsKiFocusElement(loot.Base);
    }

    private static bool IsKiFocusElement(RulesElement? elem)
    {
        if (elem is null) return false;
        if (string.Equals(elem.Name, "Ki Focus", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(elem.Type, "Magic Item", StringComparison.OrdinalIgnoreCase)
            && elem.Fields.TryGetValue("Magic Item Type", out var mit)
            && string.Equals(mit?.Trim(), "Ki Focus", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }
    private static List<LootEntry> BuildLootTally(CharacterSession session)
    {
        var entries = new List<LootEntry>();

        // Group equipped loot by composite key: when multiple slots hold the
        // same composite item (e.g. dual-wielded Carrikals + Battlecrazed
        // Weapon +4, two equipped wand implements), OCB collapses them into
        // a single <loot count="N" equip-count="N"> entry. Emitting per-slot
        // duplicates produces phantom LootTally extras.
        var equippedGroups = session.GetEquippedLoot()
            .GroupBy(kv => kv.Value.CompositeKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in equippedGroups)
        {
            var loot = group.First().Value;
            int n = group.Count();
            entries.Add(BuildLootEntry(loot, equipCount: n, count: n, session));
        }

        foreach (var inv in session.GetInventory())
            entries.Add(BuildLootEntry(inv.Item, equipCount: 0, count: inv.Quantity, session));

        return entries;
    }

    private static LootEntry BuildLootEntry(
        CharM.Engine.Creation.LootItem loot,
        int equipCount,
        int count,
        CharacterSession session)
    {
        var entry = new LootEntry
        {
            Count = count,
            EquipCount = equipCount,
            ShowPowerCard = loot.ShowPowerCard,
            CompositeName = loot.CompositeName,
            DamageOverride = loot.DamageOverride,
            Weight = loot.Weight,
            AugmentXml = loot.AugmentXml,
            IsInAlternateSlot = loot.IsInAlternateSlot,
        };

        // Per-component worn category placement preserves OCB's nested
        // <RulesElement type="Category"> attachment pattern (e.g.
        // <c>WearingRod</c> under each component of a Deathbone rod +
        // Rod of Cursed Honor composite). Falls back to legacy
        // first-component-only attachment when the per-component map
        // is empty (programmatically-built loot, not imported).
        bool first = true;
        bool hasPerComponentMap = loot.WornCategoryIdByComponentId.Count > 0;
        foreach (var component in loot.Components())
        {
            var cascades = (component.InternalId is not null
                && loot.CascadedGrantsByComponentId.TryGetValue(component.InternalId, out var g))
                ? new List<System.Xml.Linq.XElement>(g)
                : new List<System.Xml.Linq.XElement>();
            string? wornForComponent;
            if (hasPerComponentMap)
            {
                wornForComponent = component.InternalId is not null
                    && loot.WornCategoryIdByComponentId.TryGetValue(component.InternalId, out var wid)
                        ? wid
                        : null;
            }
            else
            {
                wornForComponent = first ? loot.WornCategoryId : null;
            }
            entry.Components.Add(new LootComponent(
                BuildLootTallyEntry(component, session),
                WornCategoryId: wornForComponent)
            {
                CascadedGrants = cascades,
            });
            first = false;
        }

        return entry;
    }

    private static TallyElement BuildLootTallyEntry(
        CharM.Engine.Rules.RulesElement item,
        CharacterSession session)
    {
        string? url = null;
        string? internalId = item.InternalId;
        string name = item.Name;
        Dictionary<string, string>? specifics = null;

        if (!string.IsNullOrEmpty(item.InternalId)
            && session.SourceMetadata.TryGetValue(item.InternalId, out var meta))
        {
            if (!string.IsNullOrEmpty(meta.InternalId))
                internalId = meta.InternalId;
            url = meta.Url;
            // Same rationale as BuildTallyEntry: prefer the source's recorded
            // name over the rules-DB canonical name when the DB has renamed
            // the element between content versions (e.g. ID_FMP_MAGIC_ITEM_8339
            // is "Chaos Shard Rod +3" in the current DB but "Chaos Shard
            // Implement +3" in older saved files).
            if (!string.IsNullOrEmpty(meta.Name))
                name = meta.Name;
            if (meta.Specifics.Count > 0)
                specifics = new Dictionary<string, string>(meta.Specifics, StringComparer.OrdinalIgnoreCase);
        }

        return new TallyElement(internalId, name, item.Type, specifics, url);
    }

    private static IEnumerable<CharM.Engine.Creation.LootItem> EnumerateAllLoot(CharacterSession session)
    {
        foreach (var (_, loot) in session.GetEquippedLoot())
            yield return loot;
        foreach (var inv in session.GetInventory())
            yield return inv.Item;
    }
    /// <summary>
    /// For each equipped Magic Item that belongs to one or more
    /// Item Sets, count equipped pieces and emit any Item Set Benefit whose
    /// <c>Piece Count</c> &lt;= equipped count. Source files emit these as
    /// flat <c>RulesElementTally</c> rows. Lookup tables are built from
    /// rules.db on demand.
    /// </summary>
    private static void AddItemSetBenefits(
        List<TallyElement> tally,
        CharacterSession session,
        IRulesDatabase database,
        HashSet<string> existingTallyIds)
    {
        // Equipped magic items (by InternalId). Item sets only count
        // equipped pieces, not inventoried ones.
        var equippedMagicItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, loot) in session.GetEquippedLoot())
        {
            foreach (var component in loot.Components())
            {
                if (string.IsNullOrEmpty(component.InternalId)) continue;
                if (string.Equals(component.Type, "Magic Item", StringComparison.OrdinalIgnoreCase))
                    equippedMagicItemIds.Add(component.InternalId);
            }
        }
        if (equippedMagicItemIds.Count == 0) return;

        // For each Item Set, intersect with equipped pieces.
        foreach (var itemSet in database.FindByType("Item Set"))
        {
            if (!itemSet.Fields.TryGetValue("Set Items", out var setItemsCsv)
                || string.IsNullOrWhiteSpace(setItemsCsv)) continue;

            var setPieces = setItemsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
            int equipped = setPieces.Count(p => equippedMagicItemIds.Contains(p));
            if (equipped < 2) continue; // Item set bonuses start at 2 pieces.

            if (!itemSet.Fields.TryGetValue("Benefits", out var benefitsCsv)
                || string.IsNullOrWhiteSpace(benefitsCsv)) continue;

            foreach (var benefitId in benefitsCsv.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (existingTallyIds.Contains(benefitId)) continue;
                var benefit = database.FindByInternalId(benefitId);
                if (benefit is null) continue;
                if (!benefit.Fields.TryGetValue("Piece Count", out var pieceCountStr)
                    || !int.TryParse(pieceCountStr, out var pieceCount)) continue;
                if (equipped < pieceCount) continue;

                tally.Add(new TallyElement(benefit.InternalId, benefit.Name, benefit.Type));
                existingTallyIds.Add(benefitId);
            }
        }
    }

    /// <summary>
    /// OCB maintains hidden item-set count stats (for example
    /// <c>ID_FMP_ITEM_SET_19 Set Count</c>) and evaluates Item Set Benefit
    /// rules from that count. Item Set Benefits are flat tally rows, not normal
    /// level-tree choices, so synthesize the same stat overlay from equipped
    /// set pieces before exporting stats and rebuilt PowerStats.
    /// </summary>
    private static IReadOnlyList<RulesElement> ApplyItemSetStats(
        StatBlock stats,
        CharacterSession session,
        IRulesDatabase database)
    {
        var equippedMagicItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, loot) in session.GetEquippedLoot())
        {
            foreach (var component in loot.Components())
            {
                if (string.IsNullOrEmpty(component.InternalId)) continue;
                if (string.Equals(component.Type, "Magic Item", StringComparison.OrdinalIgnoreCase))
                    equippedMagicItemIds.Add(component.InternalId);
            }
        }
        if (equippedMagicItemIds.Count == 0) return [];

        var appliedBenefits = new List<RulesElement>();
        foreach (var itemSet in database.FindByType("Item Set"))
        {
            if (string.IsNullOrEmpty(itemSet.InternalId)) continue;
            if (!itemSet.Fields.TryGetValue("Set Items", out var setItemsCsv)
                || string.IsNullOrWhiteSpace(setItemsCsv)) continue;

            var setPieces = setItemsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
            int equipped = setPieces.Count(p => equippedMagicItemIds.Contains(p));
            if (equipped < 2) continue;

            string setCountStatName = itemSet.InternalId + " Set Count";
            if (stats.TryGetStat(setCountStatName) is not { Contributions.Count: > 0 })
            {
                stats.GetOrCreateStat(setCountStatName).AddContribution(new CharM.Engine.Evaluation.StatContribution
                {
                    Value = equipped,
                    SourceElementId = itemSet.InternalId + "#SetCount",
                    Level = session.Level,
                });
            }

            if (!itemSet.Fields.TryGetValue("Benefits", out var benefitsCsv)
                || string.IsNullOrWhiteSpace(benefitsCsv)) continue;

            foreach (var benefitId in benefitsCsv.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var benefit = database.FindByInternalId(benefitId);
                if (benefit is null) continue;
                if (!benefit.Fields.TryGetValue("Piece Count", out var pieceCountStr)
                    || !int.TryParse(pieceCountStr, out var pieceCount)) continue;
                if (equipped < pieceCount) continue;

                appliedBenefits.Add(benefit);
                if (StatsHaveAnyContributionFromSource(stats, benefit.InternalId))
                    continue;

                foreach (var directive in benefit.Rules.OfType<StatAddDirective>())
                    StatAddProcessor.Process(directive, stats, benefit.InternalId, session.Level);
            }
        }

        return appliedBenefits;
    }
}