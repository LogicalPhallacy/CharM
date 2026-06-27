using System.Text.RegularExpressions;
using System.Xml.Linq;
using CharM.Engine.CharacterModel;
using CharM.Engine.Creation;
using CharM.Engine.Evaluation;
using CharM.Engine.Export;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;
using CharM.Serialization;

namespace CharM.ImportExport;

/// <summary>
/// Builds a .dnd4e XML payload from a populated <see cref="CharacterSession"/>.
///
/// This is the symmetric counterpart to <see cref="Dnd4eImporter"/> — together
/// the two should round-trip a character without losing data we don't ourselves
/// model (handled via <see cref="CharacterSession.TextStrings"/> and the
/// snapshot-fluff transferred at import).
/// </summary>
public static partial class Dnd4eExporter
{
    /// <summary>Serialize <paramref name="session"/> to a .dnd4e byte array.</summary>
    /// <param name="rebuildPowerStats">
    /// When true, ignore any captured raw &lt;PowerStats&gt; section and emit a
    /// freshly computed one. This is what we want for testing — round-tripping
    /// an OCB-saved character with verbatim passthrough never exercises the
    /// power-card logic. Production single-character exports default to false
    /// so byte-fidelity wins over computed-value drift for users.
    /// </param>
    public static byte[] Export(CharacterSession session, IRulesDatabase database,
        bool rebuildPowerStats = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(database);

        var data = BuildExportData(session, database, rebuildPowerStats);
        using var ms = new MemoryStream();
        Dnd4eWriter.Write(ms, data);
        return ms.ToArray();
    }

    /// <summary>Write <paramref name="session"/> as a .dnd4e file at <paramref name="path"/>.</summary>
    public static void ExportToFile(CharacterSession session, IRulesDatabase database, string path,
        bool rebuildPowerStats = false)
    {
        File.WriteAllBytes(path, Export(session, database, rebuildPowerStats));
    }

    /// <summary>Build the raw <see cref="CharacterExportData"/> for <paramref name="session"/>.</summary>
    public static CharacterExportData BuildExportData(CharacterSession session, IRulesDatabase database,
        bool rebuildPowerStats = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(database);

        var snapshot = session.GetSnapshot() ?? session.GetPartialSnapshot();

        var itemSetBenefitSources = snapshot is not null
            ? ApplyItemSetStats(snapshot.Builder.Stats, session, database)
            : [];

        // The synthetic Unarmed weapon block emitted under powers that
        // pass AllowUnarmed needs to resolve the canonical Unarmed attack
        // weapon (ID_FMP_WEAPON_34, Damage=1d4, no Proficiency Bonus) so
        // PowerStatCalculator gets real 1d4 weapon dice instead of leaving
        // the literal "1[W]" token. The element isn't in the character's
        // tree (nothing grants it), and the source-element resolver only
        // sees in-tree elements, so we inject it here from the database.
        // Characters with their own unarmed weapon — Monk Unarmed Strike
        // (1d8, granted via FREEBEE on the Unarmed Combatant class
        // feature), Razorclaw / Wilden natural-weapon overrides, Spiked
        // Gauntlets (1d6) — already carry that weapon in their loot, so
        // the normal weapon-iteration emits a per-loot block AND the
        // synthetic Unarmed slot here. OCB inconsistency: it emits the
        // canonical 1d4 Unarmed for everyone, including monks; we mirror
        // by always providing the canonical resolution.
        var canonicalUnarmed = database.FindByInternalId("ID_FMP_WEAPON_34");
        if (canonicalUnarmed is not null)
            itemSetBenefitSources = itemSetBenefitSources.Concat([canonicalUnarmed]).ToList();

        var exportBuilder = new ExportTreeBuilder(
            database.FindByInternalId,
            database.FindByNameAndType);

        var choicesByLevel = session.GetChoicesByLevel();

        // Build the source-charelem map so the export tree can reuse
        // round-trip-stable charelems for any OLD element of a swap that
        // came from the source file. New retrains (added via UI) fall
        // through to deterministic synthesis (SHA256-of-internal-id).
        Dictionary<string, string>? sourceCharelems = null;
        foreach (var (id, meta) in session.SourceMetadata)
        {
            if (string.IsNullOrEmpty(meta.Charelem)) continue;
            sourceCharelems ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sourceCharelems[id] = meta.Charelem!;
        }

        var exportTree = exportBuilder.Build(
            choicesByLevel,
            session.Replacements,
            sourceCharelems,
            generateCharelem: CharM.Serialization.Dnd4eWriter.GenerateCharelem);

        var stats = snapshot is not null
            ? BuildStatExportData(snapshot)
            : new Dictionary<string, StatExportData>();

        // Inventory weight stat: sum (Base.Fields["Weight"] × count) across
        // every equipped + inventoried loot. OCB emits this as the character's
        // total carried weight in pounds. User-override loot.Weight wins when
        // present; otherwise we read Weight from the rules-DB Base component.
        InjectInventoryWeightStat(stats, session);

        var baseAbilityScores = session.AbilityScores is not null
            ? new Dictionary<string, int>
            {
                ["Strength"] = session.AbilityScores[Ability.Strength],
                ["Constitution"] = session.AbilityScores[Ability.Constitution],
                ["Dexterity"] = session.AbilityScores[Ability.Dexterity],
                ["Intelligence"] = session.AbilityScores[Ability.Intelligence],
                ["Wisdom"] = session.AbilityScores[Ability.Wisdom],
                ["Charisma"] = session.AbilityScores[Ability.Charisma],
            }
            : new Dictionary<string, int>();

        // OCB's RulesElementTally is deduplicated AND excludes anything that
        // came from equipped/inventory loot (weapons, armor, magic items,
        // and any sub-grants like proficiencies that those items added). The
        // LootTally is the authoritative listing for equipment.
        // Strategy: identify CharacterElement *nodes* that live inside an
        // equipment subtree, then keep only RulesElements that appear at least
        // once OUTSIDE the equipment subtrees (so e.g. Weapon Proficiency (Mace)
        // granted by both a class feature and a Mace stays tallied).
        var tally = new List<TallyElement>();
        if (snapshot is not null)
        {
            var equipmentRootIds = CollectEquipmentInternalIds(session);
            var equipmentNodes = new HashSet<CharM.Engine.CharacterModel.CharacterElement>();
            foreach (var node in snapshot.Builder.ElementTree.Root.GetAllDescendants())
            {
                if (node.RulesElement?.InternalId is null) continue;
                if (!equipmentRootIds.Contains(node.RulesElement.InternalId)) continue;
                equipmentNodes.Add(node);
                foreach (var sub in node.GetAllDescendants())
                    equipmentNodes.Add(sub);
            }

            // Pre-build the session swap map so BuildTallyEntry can fall
            // back to it when source metadata has no replaces= (UI-built
            // characters with no source file behind them, or wizard-added
            // swaps on an imported character). Maps each NEW element id
            // to the OLD element's charelem.
            var sessionSwapReplaces = BuildSessionSwapReplacesMap(session);

            tally = DedupeTally(snapshot.Builder.ElementTree.Root.GetAllDescendants()
                .Where(node => node.RulesElement is not null && !equipmentNodes.Contains(node))
                .Where(node => node.RulesElement!.InternalId is null
                    || !snapshot.LevelNestedOnlyIds.Contains(node.RulesElement!.InternalId))
                .Where(node => node.RulesElement!.InternalId is null
                    || !snapshot.UserEditPickIds.Contains(node.RulesElement!.InternalId))
                .Select(node => node.RulesElement!))
                .Select(el => BuildTallyEntry(el, session, sessionSwapReplaces))
                .ToList();

            tally = FilterTierVariants(tally, session.Level);

            // Bucket D: emit one flat <Category> tally row per unique
            // worn-state (e.g., WearingOffHandLightBlade) on currently
            // equipped loot. Source files always emit these alongside the
            // nested-child Category attached to the equipped weapon.
            var existingTallyIds = new HashSet<string>(
                tally.Where(t => t.InternalId is not null).Select(t => t.InternalId!),
                StringComparer.OrdinalIgnoreCase);
            var seenWornIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Build a quick lookup of equipped-only root component InternalIds
            // (a single id can appear on both equipped + inventory loot when
            // the character carries multiple of the same base; we only want
            // to surface categories that came from EQUIPPED items).
            var equippedOnlyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, equippedLoot) in session.GetEquippedLoot())
            {
                foreach (var c in equippedLoot.Components())
                    if (!string.IsNullOrEmpty(c.InternalId))
                        equippedOnlyIds.Add(c.InternalId);
            }

            foreach (var (_, loot) in session.GetEquippedLoot())
            {
                var wid = loot.WornCategoryId;
                if (string.IsNullOrEmpty(wid)) continue;
                if (!seenWornIds.Add(wid)) continue;
                if (existingTallyIds.Contains(wid)) continue;
                tally.Add(new TallyElement(
                    wid,
                    Dnd4eWriter.WornCategoryName(wid),
                    "Category"));
                existingTallyIds.Add(wid);
            }

            // Some source files save the equipped weapon's 
            // <rules><grant name="WearingX" type="Category"/>
            // block without nesting a captured <RulesElement> for the worn
            // category — so loot.WornCategoryId is null. But the engine still
            // executes the grant when the equipment subtree fires Phase 1, so
            // the WearingX Category lives in the element tree as a child of
            // the equipped base. Surface those granted categories as flat
            // tally rows too. Filter to type="Category" only — intrinsic
            // weapon categories (Light Blade, One-Handed, etc.) live in
            // RulesElement.Categories rather than as tree children, so they
            // aren't picked up here.
            foreach (var node in snapshot.Builder.ElementTree.Root.GetAllDescendants())
            {
                if (node.RulesElement is not { Type: "Category" } cat) continue;
                if (string.IsNullOrEmpty(cat.InternalId)) continue;
                if (!seenWornIds.Add(cat.InternalId)) continue;
                if (existingTallyIds.Contains(cat.InternalId)) continue;

                // Only emit if an ancestor is an EQUIPPED loot root.
                bool fromEquipped = false;
                for (var anc = node.Parent; anc is not null; anc = anc.Parent)
                {
                    if (anc.RulesElement?.InternalId is { } ancId
                        && equippedOnlyIds.Contains(ancId))
                    {
                        fromEquipped = true;
                        break;
                    }
                }
                if (!fromEquipped) continue;

                tally.Add(new TallyElement(cat.InternalId, cat.Name, "Category"));
                existingTallyIds.Add(cat.InternalId);
            }

            AddInventoryAlchemicalTally(tally, session, existingTallyIds);

            // Magic-item grant cascades (Harper Pin blessings, Echo of
            // Ty'h'kadi Elemental Origin, Firepulse racial-power select,
            // etc.). Verbatim-passthrough: surface every nested cascade
            // grant captured under loot components as a flat tally row.
            // De-dupe by InternalId across all loot (equipped + inventory).
            var seenGrantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lootSource in EnumerateAllLoot(session))
            {
                foreach (var grants in lootSource.CascadedGrantsByComponentId.Values)
                {
                    foreach (var grant in grants)
                        AddCascadedGrantTally(tally, grant, existingTallyIds, seenGrantIds);
                }
            }

            // Item Set Benefits. For each item set with N+ equipped
            // pieces, emit any benefit RulesElement whose Piece Count <= N.
            AddItemSetBenefits(tally, session, database, existingTallyIds);

            // Replaced (swapped-out) elements. OCB's WritePowers (and its
            // tally writer) iterate the flat character element list, which
            // keeps swapped-out CharElements in place after a multiclass-feat
            // power swap or retraining just clears the active flag but 
            // leaves the entry in the list).
            // Re-emit them so our output mirrors OCB's "user can retrain
            // back" behavior. Tally first; PowerStats picks them up below
            // via extraPowers.
            foreach (var re in snapshot.Builder.ReplacedElements)
            {
                if (string.IsNullOrEmpty(re.InternalId)) continue;
                if (snapshot.LevelNestedOnlyIds.Contains(re.InternalId)) continue;
                if (!existingTallyIds.Add(re.InternalId)) continue;
                tally.Add(BuildTallyEntry(re, session, sessionSwapReplaces: null));
            }
        }

        // The "Name" textstring is asymmetric in OCB: it's only written when
        // the user typed into the wizard's Name field (or on chars that
        // round-tripped through OCB after a name edit). Many source files
        // lack it entirely — only <Details><name> carries the display name.
        // We respect that contract: if the source had Name in TextStrings,
        // we re-emit it with the current session.Name (covers user renames
        // via the web UI); if the source did NOT have it, we don't
        // synthesize one. New characters built via the wizard seed
        // TextStrings["Name"] explicitly at creation time.
        var textStrings = new Dictionary<string, string>(session.TextStrings, StringComparer.Ordinal);
        if (textStrings.ContainsKey("Name"))
            textStrings["Name"] = session.Name ?? string.Empty;

        // Computed power stats.
        // Important: we also run the builder when the captured raw PowerStats
        // has zero <Power> children — this is the "imported a stub file"
        // case (e.g. a freshly-saved web-built character that round-tripped
        // through OCB once with an empty PowerStats). The empty raw block
        // wins via passthrough in the writer, defeating the whole point of
        // having computed numbers, so we drop it from the export's raw
        // section snapshot when it's effectively empty.
        var powerStats = new List<PowerStatEntry>();
        bool hasUsableRawPowerStats =
            !rebuildPowerStats
            && session.RawSections.TryGetValue("PowerStats", out var rawPower)
            && rawPower.Elements("Power").Any();
        if (snapshot is not null && !hasUsableRawPowerStats)
        {
            // Candidate weapons come from owned loot with count >= 1,
            // not just currently-equipped loot. equip-count remains
            // significant for sorting, offhand/two-weapon gates, and
            // triggering Ki Focus synthetic pair generation, but normal
            // per-power candidate iteration includes valid inventory
            // weapons too.
            //
            // Sort: equipped first (main-hand before other slots), then
            // higher Level first, then enchanted-first as tiebreaker.
            // Stable for equal items.
            var equippedLoot = session.GetEquippedLoot().Values.ToList();
            var equippedKeys = new HashSet<string>(
                equippedLoot.Select(l => l.CompositeKey),
                StringComparer.OrdinalIgnoreCase);
            var equippedKeyCounts = equippedLoot
                .GroupBy(l => l.CompositeKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            // Distinguish "Carrikal in main + Carrikal in off" (slot
            // count 2) from "owning multiple in inventory" (slot count
            // 1, equippedKeys size 1). Counts equipment slots holding
            // a weapon-or-implement base.
            int equippedWeaponSlotCount = equippedLoot.Count(l => IsWeaponOrImplementBase(l.Base));
            var slotByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slot, loot) in session.GetEquippedLoot())
                slotByKey[loot.CompositeKey] = slot;

            var allLoot = session.GetEquippedLoot()
                .Select(kv => kv.Value)
                .Concat(session.GetInventory()
                    .Where(inv => inv.Quantity >= 1)
                    .Select(inv => inv.Item))
                .ToList();

            var weaponCandidates = allLoot
                .Where(loot => IsWeaponOrImplementBase(loot.Base))
                .DistinctBy(loot => loot.CompositeKey, StringComparer.OrdinalIgnoreCase)
                .Concat(BuildKiFocusPairs(session))
                .Select((loot, idx) => (loot, idx))
                .OrderBy(x => x, new OcbWeaponSortComparer(equippedKeys, slotByKey))
                .Select(x => x.loot)
                .ToList();
            powerStats = PowerStatsBuilder.Build(
                snapshot, weaponCandidates, session.Level,
                extraPowers: CollectExtraPowers(session, database, snapshot),
                allLoot: allLoot,
                extraSourceElements: itemSetBenefitSources,
                equippedCompositeKeys: equippedKeys,
                equippedWeaponSlotCount: equippedWeaponSlotCount,
                equippedCompositeKeyCounts: equippedKeyCounts,
                textStrings: textStrings,
                precomputedBeastAttackBonus: TryReadPrecomputedBeastAttackBonus(session),
                sourceMetadata: session.SourceMetadata);
        }

        // Build the writer's raw-section snapshot. Drop an empty PowerStats
        // entry if present so the writer emits our structured output instead
        // of the empty placeholder. (Mutating session.RawSections directly
        // would leak through to subsequent exports — we copy first.)
        var rawSections = new Dictionary<string, System.Xml.Linq.XElement>(
            session.RawSections, StringComparer.Ordinal);
        if (!hasUsableRawPowerStats)
            rawSections.Remove("PowerStats");
        bool rebuildGrabbag = session.CampaignSettingsDirty
            || (!rawSections.ContainsKey("Grabbag") && session.CampaignSettingGrants.Count > 0);

        var implicitLevelTreeChildren = BuildImplicitLevelTreeChildren(exportTree.Root, database);
        var generatedUserEdits = BuildGeneratedHouseruleUserEdits(session, snapshot);
        var houseruleLevelUserEdits = session.HouseruleLevelUserEdits.ToDictionary(
            kv => kv.Key,
            kv => new List<XElement>(kv.Value));
        foreach (var (level, userEdits) in generatedUserEdits)
        {
            if (!houseruleLevelUserEdits.TryGetValue(level, out var list))
            {
                list = [];
                houseruleLevelUserEdits[level] = list;
            }
            list.AddRange(userEdits);
        }

        var houseruleTallyMirror = EnrichTallyMirrorWithShortDescription(
            FilterMirrorAgainstEngineTally(session.HouseruleFormATallyMirror, session.SourceFlatTallyIds),
            database,
            session.SourceMetadata);
        houseruleTallyMirror.AddRange(EnrichTallyMirrorWithShortDescription(
            BuildGeneratedHouseruleTallyMirror(session),
            database));

        return new CharacterExportData
        {
            Name = session.Name ?? string.Empty,
            Level = session.Level,
            Stats = stats,
            BaseAbilityScores = baseAbilityScores,
            RulesElementTally = tally,
            LootTally = BuildLootTally(session),
            Details = new Dictionary<string, string>(session.Details),
            ElementTreeRoot = exportTree.Root,
            SourceMetadata = new Dictionary<string, ElementSourceMetadata>(
                session.SourceMetadata, StringComparer.OrdinalIgnoreCase),
            TextStrings = textStrings,
            PowerStats = powerStats,
            RawSections = rawSections,
            GrabbagGrants = session.CampaignSettingGrants
                .Select(e => new TallyElement(e.InternalId, e.Name, e.Type))
                .ToList(),
            RebuildGrabbag = rebuildGrabbag,
            IsCharacterHouseruled = session.IsCharacterHouseruled,
            HouseruleLevelUserEdits = houseruleLevelUserEdits,
            SourceLevelLoot = session.SourceLevelLoot.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(loot => new System.Xml.Linq.XElement(loot)).ToList()),
            CapturedLevelXml = session.CapturedLevelTrees
                .Where(kv => !session.LevelTreeDirty.Contains(kv.Key))
                .ToDictionary(
                    kv => kv.Key,
                    kv => new System.Xml.Linq.XElement(kv.Value)),
            HouseruleFormATallyMirror = houseruleTallyMirror,
            HouseruleLegacyTallyRows = EnrichTallyMirrorWithShortDescription(
                session.HouseruleLegacyTallyRows, database, session.SourceMetadata),
            HouseruledElementIds = new HashSet<string>(
                session.HouseruledElementIds, StringComparer.OrdinalIgnoreCase),
            UserEditPickIds = snapshot is not null
                ? new HashSet<string>(snapshot.UserEditPickIds, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Companions = OverlayPrecomputedBeastAttackBonus(
                session.GetCompanionData().ToList(),
                TryReadPrecomputedBeastAttackBonus(session)),
            ImplicitLevelTreeChildren = implicitLevelTreeChildren,
        };
    }

    /// <summary>
    /// Mirror OCB's Beast block behavior: the canonical AttackBonus shown
    /// in every <c>&lt;Companions&gt;&lt;Beast&gt;</c> entry (both the
    /// fully-populated active block AND each placeholder overlay block)
    /// is the *character-side* computed total (base + half-level +
    /// expertise/feat bonuses), not the static "Attack Bonus" field on
    /// the base companion. OCB reads that total via the same path
    /// PowerStats uses — the source file's prior
    /// <c>&lt;Beast&gt;&lt;AttackBonus&gt;</c> value — so for round-trip
    /// we overlay it onto every beast (real + placeholder) entry. See
    /// <see cref="TryReadPrecomputedBeastAttackBonus"/>.
    /// </summary>
    private static List<CompanionData> OverlayPrecomputedBeastAttackBonus(
        List<CompanionData> companions, int? precomputedBonus)
    {
        if (precomputedBonus is null) return companions;
        for (int i = 0; i < companions.Count; i++)
        {
            var c = companions[i];
            if (c.IsSummon || c.IsMinion || c.IsFamiliar) continue;
            companions[i] = c with { AttackBonus = precomputedBonus };
        }
        return companions;
    }

    /// <summary>
    /// Compute the per-parent map of synthetic children that should appear
    /// in the LevelTree cascade beyond what the rules engine produced.
    /// Mirrors OCB-specific cascade behaviors where a parent element emits
    /// ALL its canonical <c>GrantDirective</c> targets in the LevelTree,
    /// bypassing the per-grant <c>Requires</c> gate. The flat
    /// <c>&lt;RulesElementTally&gt;</c> still respects <c>Requires</c>
    /// (it is computed independently from the active-element walk).
    ///
    /// Currently handles <c>Implement Proficiency (Proficient Weapons)</c>:
    /// OCB emits all 87 implement-prof child markers regardless of whether
    /// the character has the corresponding <c>Weapon Proficiency (X)</c>.
    /// See <c>docs/engine-special-cases.md</c> §11.
    /// </summary>
    private static Dictionary<string, List<TallyElement>> BuildImplicitLevelTreeChildren(
        CharM.Engine.CharacterModel.CharacterElement root,
        IRulesDatabase database)
    {
        var result = new Dictionary<string, List<TallyElement>>(StringComparer.OrdinalIgnoreCase);
        const string ProficientWeaponsId =
            "ID_INTERNAL_PROFICIENCY_IMPLEMENT_PROFICIENCY_(PROFICIENT_WEAPONS)";

        var profWeaponsNode = root.GetAllDescendants().FirstOrDefault(n =>
            string.Equals(n.RulesElement?.InternalId, ProficientWeaponsId,
                StringComparison.OrdinalIgnoreCase));
        if (profWeaponsNode is null) return result;

        var parentElement = database.FindByInternalId(ProficientWeaponsId);
        if (parentElement is null) return result;

        var existingChildIds = new HashSet<string>(
            profWeaponsNode.Children
                .Select(c => c.RulesElement?.InternalId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!),
            StringComparer.OrdinalIgnoreCase);

        var implicits = new List<TallyElement>();
        foreach (var directive in parentElement.Rules)
        {
            if (directive is not GrantDirective grant) continue;
            if (existingChildIds.Contains(grant.Name)) continue;
            var target = database.FindByInternalId(grant.Name);
            if (target is null) continue;
            implicits.Add(new TallyElement(target.InternalId, target.Name, target.Type));
        }

        if (implicits.Count > 0)
            result[ProficientWeaponsId] = implicits;

        return result;
    }

    private static Regex TierSuffix => TierSuffixRegex();

    [GeneratedRegex(@"^(?<base>.+?) - (?<tier>Heroic|Paragon|Epic)\)$")]
    private static partial Regex TierSuffixRegex();

    /// <summary>
    /// Synthesize Ki Focus pairings: for each equipped Ki Focus, pair it with
    /// each distinct owned Type=Weapon base. 
    /// The synthetic pair gets named via <c>LootNaming.Compose</c> (the
    /// "ki focus" substitution path turns "Rain of Hammers Ki Focus +1" +
    /// "Mace" into "Rain of Hammers Ki Focused Mace +1"), and feeds
    /// <c>PowerStats</c> for the weapon-base damage dice with the Ki
    /// Focus's enhancement bonus and crit/conditional behavior.
    ///
    /// The Ki Focus itself must be equipped to trigger pairing, but the
    /// paired weapon base pool is owned loot with count &gt;= 1, including
    /// <summary>
    /// Read OCB's precomputed beast Attack Bonus from the source file's
    /// <c>&lt;Companions&gt;&lt;Beast&gt;&lt;AttackBonus&gt;</c> child, if
    /// present. OCB uses that value verbatim when emitting Beast Melee
    /// Basic Attack / Synchronized Strike / Companion: X power stats:
    /// it's the canonical "total beast to-hit" the player can override
    /// (homebrew beasts, retuned ability scores, etc.). Falls back to
    /// the level + highest companion ability mod formula when the field
    /// is absent or unparseable. Returns null when no Companions section
    /// exists at all (no beast master / familiar character).
    /// </summary>
    private static int? TryReadPrecomputedBeastAttackBonus(CharacterSession session)
    {
        if (!session.RawSections.TryGetValue("Companions", out var companions)
            || companions is null)
            return null;
        var beast = companions.Element("Beast");
        var ab = beast?.Element("AttackBonus");
        if (ab is null) return null;
        return int.TryParse(ab.Value.Trim(), out int v) ? v : null;
    }

    /// <summary>
    /// (Hybrid Executioner - Heroic/Paragon/Epic)") that don't match the
    /// character's current tier, even though the engine grants all three
    /// unconditionally. Drop non-matching tier variants when at least one
    /// other variant of the same base name is present.
    /// </summary>
    private static List<TallyElement> FilterTierVariants(List<TallyElement> tally, int level)
    {
        var currentTier = level switch { <= 10 => "Heroic", <= 20 => "Paragon", _ => "Epic" };

        var groupsByBase = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var el in tally)
        {
            var m = TierSuffix.Match(el.Name);
            if (!m.Success) continue;
            var key = el.Type + "::" + m.Groups["base"].Value;
            groupsByBase[key] = groupsByBase.GetValueOrDefault(key) + 1;
        }

        return tally.Where(el =>
        {
            var m = TierSuffix.Match(el.Name);
            if (!m.Success) return true;
            var key = el.Type + "::" + m.Groups["base"].Value;
            if (groupsByBase[key] < 2) return true;
            return string.Equals(m.Groups["tier"].Value, currentTier, StringComparison.Ordinal);
        }).ToList();
    }

    /// <summary>
    /// Yield each rules element at most once, keyed by (Type, Name, InternalId).
    /// Preserves first-occurrence order so the resulting tally matches the
    /// element-tree traversal order — important because the writer emits the
    /// tally in iteration order and OCB tools display it that way.
    /// </summary>
    private static IEnumerable<CharM.Engine.Rules.RulesElement> DedupeTally(
        IEnumerable<CharM.Engine.Rules.RulesElement> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in source)
        {
            var key = (el.Type ?? "") + "\0" + (el.Name ?? "") + "\0" + (el.InternalId ?? "");
            if (seen.Add(key))
                yield return el;
        }
    }

    /// <summary>
    /// Build a <see cref="TallyElement"/> for an in-engine rules element,
    /// enriching it with pass-through metadata captured at import (url and
    /// child <c>&lt;specific&gt;</c> values). New elements added via the
    /// wizard since import simply have no metadata entry.
    ///
    /// <para>OCB always emits <c>&lt;specific name="Short Description"&gt;</c>
    /// on tally rows when the rules element has that field — synthesized at
    /// save time from the rules DB rather than carried from the input file.
    /// We mirror that behavior so houseruled / engine-granted elements that
    /// never had a verbatim tally row in the source still get the field.
    /// </summary>
    private static TallyElement BuildTallyEntry(
        CharM.Engine.Rules.RulesElement element,
        CharacterSession session,
        IReadOnlyDictionary<string, string>? sessionSwapReplaces)
    {
        string? url = null;
        string? replaces = null;
        string? internalId = element.InternalId;
        string name = element.Name;
        Dictionary<string, string>? specifics = null;
        // Did the source file actually carry this element (in any of: flat tally,
        // level tree, granted subtree)? If so, we treat the source as authoritative
        // for whether the tally row had a <specific name="Short Description"> child:
        // if source omitted it, we omit it too (e.g. Class Feature: Primal Swarm in
        // older Druid files that pre-date the part-file Short Description override).
        // If source NEVER mentioned this element (engine-granted, houseruled), we
        // fall back to synthesizing from the rules DB so houseruled / inherent-bonus
        // grants still get the field.
        bool sourceHadElement = false;
        bool sourceHadShortDescription = false;

        if (!string.IsNullOrEmpty(element.InternalId)
            && session.SourceMetadata.TryGetValue(element.InternalId, out var meta))
        {
            sourceHadElement = true;
            if (!string.IsNullOrEmpty(meta.InternalId))
                internalId = meta.InternalId;
            url = meta.Url;
            replaces = meta.Replaces;
            // Prefer the source's recorded name over the rules-DB canonical
            // name. The DB occasionally renames elements between content
            // versions (Sensate → The Society of Sensation; Corellon →
            // Corellon (Forgotten Realms)); silently rewriting on
            // round-trip would break tally parity for any file authored
            // against an older build. Re-emit verbatim when we have it.
            if (!string.IsNullOrEmpty(meta.Name))
                name = meta.Name;
            if (meta.Specifics.Count > 0)
            {
                foreach (var (key, value) in meta.Specifics)
                {
                    // Tally rows mirror OCB's WriteElemFields whitelist:
                    // Short Description only. Other specifics
                    // (Action Type / Power Usage / etc.) are captured from
                    // wherever the same internal-id appears in source —
                    // typically a granted-power subtree under loot — and
                    // belong on PowerStats, not on the flat tally row.
                    if (!string.Equals(key, "Short Description",
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    sourceHadShortDescription = true;
                    specifics ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    specifics[key] = value;
                }
            }
        }

        List<KeyValuePair<string, string>>? extraSpecifics = null;
        // Only synthesize a Short Description from the rules DB when the source
        // either DID emit one already, OR never carried this element at all.
        // Skipping the synth when source had the element but omitted Short
        // Description respects OCB's exact serialization for that file (most
        // commonly: rules-content drift between when the file was saved and
        // when a later .part file backfilled the Short Description field).
        bool allowSynthesis = !sourceHadElement || sourceHadShortDescription;
        if (allowSynthesis
            && element.Fields.TryGetValue("Short Description", out var shortDesc)
            && !string.IsNullOrEmpty(shortDesc))
        {
            specifics ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!specifics.ContainsKey("Short Description"))
                specifics["Short Description"] = shortDesc;

            // Some elements legitimately emit MULTIPLE <specific name="Short Description">
            // children on a tally row — currently only seen on a handful of
            // Heroes-of-the-Feywild races (Hamadryad ID_FMP_RACE_60, Satyr
            // ID_FMP_RACE_62) where OCB serializes the field twice with the
            // SAME long blurb text (despite the merged rules XML carrying two
            // distinct Short Description values — OCB seems to source both
            // copies from the same field). When source had N occurrences of
            // a specific (N > 1), emit the FIRST value N times to match.
            if (sourceHadElement
                && session.SourceMetadata.TryGetValue(element.InternalId!, out var sdMeta)
                && sdMeta.SpecificCounts.TryGetValue("Short Description", out var sdCount)
                && sdCount > 1
                && specifics.TryGetValue("Short Description", out var firstSd))
            {
                for (int i = 1; i < sdCount; i++)
                {
                    extraSpecifics ??= new List<KeyValuePair<string, string>>();
                    extraSpecifics.Add(new KeyValuePair<string, string>("Short Description", firstSd));
                }
            }
        }

        // Fallback: when the source had no replaces metadata, consult the
        // session swap map. This is the UI-built-character path — swaps
        // recorded via session.AddReplacement that have no source-file
        // origin still need a replaces= attribute on the tally row.
        if (string.IsNullOrEmpty(replaces)
            && sessionSwapReplaces is not null
            && !string.IsNullOrEmpty(element.InternalId)
            && sessionSwapReplaces.TryGetValue(element.InternalId, out var sessionReplaces))
        {
            replaces = sessionReplaces;
        }

        return new TallyElement(internalId, name, element.Type, specifics, url, replaces, extraSpecifics);
    }

    /// <summary>
    /// Build a <c>Dictionary&lt;NewInternalId, OldCharelem&gt;</c> from
    /// <see cref="CharacterSession.Replacements"/> for swaps whose NEW
    /// element was NOT present in the source file. This is the
    /// UI-built-character path — swaps where the NEW power was added via
    /// <see cref="CharacterSession.AddReplacement"/> and has no source-file
    /// row to inherit a <c>replaces=</c> attribute from.
    ///
    /// Swaps whose NEW element ALSO came from the source file are skipped:
    /// the canonical answer for those tally rows lives in the source file
    /// (and <c>BuildTallyEntry</c> already pulled it from <c>meta.Replaces</c>
    /// when applicable). The Mess Blood Drinker / Thirst for Blood case is
    /// the canonical example — Blood Drinker's L3 tally row legitimately
    /// has NO <c>replaces=</c> (OCB encodes the L18 feat swap only in the
    /// level tree), and we mustn't add one.
    /// </summary>
    private static Dictionary<string, string> BuildSessionSwapReplacesMap(CharacterSession session)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, list) in session.Replacements)
        {
            foreach (var rep in list)
            {
                if (string.IsNullOrEmpty(rep.NewInternalId) || string.IsNullOrEmpty(rep.OldInternalId))
                    continue;

                // Skip swaps whose NEW element already has source-file
                // metadata. Source-file is authoritative for those rows;
                // meta.Replaces already wins in BuildTallyEntry.
                if (session.SourceMetadata.ContainsKey(rep.NewInternalId))
                    continue;

                // Pick OLD's charelem: source-file charelem if available,
                // else deterministic synthesis. Both options round-trip
                // through the same hash so re-import + re-export is stable.
                string oldCharelem;
                if (session.SourceMetadata.TryGetValue(rep.OldInternalId, out var meta)
                    && !string.IsNullOrEmpty(meta.Charelem))
                {
                    oldCharelem = meta.Charelem!;
                }
                else
                {
                    oldCharelem = CharM.Serialization.Dnd4eWriter.GenerateCharelem(rep.OldInternalId);
                }
                map[rep.NewInternalId] = oldCharelem;
            }
        }
        return map;
    }

    /// <summary>
    /// Walk the verbatim Form A / Form B tally mirror XElements and inject a
    /// <c>&lt;specific name="Short Description"&gt;</c> child on any element
    /// whose <c>internal-id</c> resolves in the rules DB to one with a
    /// non-empty Short Description field. Mirrors the synthesized-at-save
    /// behavior of OCB's <c>WriteElemFields</c>: the source UserEdit subtree
    /// rarely contains specifics, but the OCB-generated tally row does.
    /// Pure XElement clone so the caller's list isn't mutated; missing /
    /// unknown ids silently pass through unchanged.
    /// </summary>
    /// <summary>
    /// Drop mirror entries whose internal-id is NOT in the source's original
    /// flat tally. The UserEdit subtree contains OCB's runtime cascade
    /// record (e.g. Shadow Initiate → Assassin Implements → every Implement
    /// Proficiency the cascade granted at save time, including some OCB
    /// excluded from its own flat tally). The mirror's job is to surface
    /// picks the engine wouldn't otherwise reach, not to faithfully echo
    /// the UserEdit cascade. <see cref="CharacterSession.SourceFlatTallyIds"/>
    /// is the authoritative "what OCB actually wrote to the flat tally"
    /// set — if an id isn't there, neither should our mirror entry be.
    /// (A legitimate mirror entry like sm_art-shyr's Magic Weapon under
    /// Arcane Admixture survives because OCB did flat-tally it.)
    /// </summary>
    private static List<System.Xml.Linq.XElement> FilterMirrorAgainstEngineTally(
        IEnumerable<System.Xml.Linq.XElement> mirror,
        HashSet<string> sourceFlatTallyIds)
    {
        var result = new List<System.Xml.Linq.XElement>();
        foreach (var el in mirror)
        {
            var iid = el.Attribute("internal-id")?.Value;
            if (!string.IsNullOrEmpty(iid) && !sourceFlatTallyIds.Contains(iid)) continue;
            result.Add(el);
        }
        return result;
    }

    private static List<System.Xml.Linq.XElement> EnrichTallyMirrorWithShortDescription(
        IEnumerable<System.Xml.Linq.XElement> source,
        IRulesDatabase database)
        => EnrichTallyMirrorWithShortDescription(source, database, null);

    private static List<System.Xml.Linq.XElement> EnrichTallyMirrorWithShortDescription(
        IEnumerable<System.Xml.Linq.XElement> source,
        IRulesDatabase database,
        IReadOnlyDictionary<string, ElementSourceMetadata>? sourceMetadata)
    {
        var result = new List<System.Xml.Linq.XElement>();
        foreach (var src in source)
        {
            var clone = new System.Xml.Linq.XElement(src);
            var iid = clone.Attribute("internal-id")?.Value;
            if (string.IsNullOrEmpty(iid)) { result.Add(clone); continue; }

            // Override the verbatim legality with the source flat-tally row's
            // value. Form A mirror entries are cloned from inside <UserEdit>
            // subtrees where the picked element can carry legality="houserule"
            // (user override marker), but the flat tally row in the same file
            // typically has legality="rules-legal". Without this re-write, the
            // mirror passthrough emits the UserEdit's houserule attribute on
            // the flat tally row, which never matched source.
            if (sourceMetadata is not null
                && sourceMetadata.TryGetValue(iid, out var meta)
                && !string.IsNullOrEmpty(meta.Legality))
            {
                clone.SetAttributeValue("legality", meta.Legality);
            }

            // Skip Short Description synth when the source already supplied a
            // specific (preserves user-edited Backgrounds and any other custom
            // values).
            bool alreadyHas = clone.Elements("specific")
                .Any(s => string.Equals(s.Attribute("name")?.Value,
                    "Short Description", StringComparison.OrdinalIgnoreCase));
            if (alreadyHas) { result.Add(clone); continue; }

            var element = database.FindByInternalId(iid);
            if (element is null) { result.Add(clone); continue; }
            if (!element.Fields.TryGetValue("Short Description", out var sd)
                || string.IsNullOrEmpty(sd)) { result.Add(clone); continue; }

            clone.Add(new System.Xml.Linq.XElement("specific",
                new System.Xml.Linq.XAttribute("name", "Short Description"), sd));
            result.Add(clone);
        }
        return result;
    }

    /// <summary>
    /// Recursively walk a cascaded grant XElement subtree and append a flat
    /// TallyElement for each unique <c>RulesElement</c> found (skipping the
    /// worn-state Category, We handle that separately).
    /// </summary>
    private static void AddCascadedGrantTally(
        List<TallyElement> tally,
        System.Xml.Linq.XElement grant,
        HashSet<string> existingIds,
        HashSet<string> seenIds)
    {
        var id = grant.Attribute("internal-id")?.Value;
        var name = grant.Attribute("name")?.Value ?? string.Empty;
        var type = grant.Attribute("type")?.Value ?? string.Empty;
        if (!string.IsNullOrEmpty(id)
            && seenIds.Add(id)
            && !existingIds.Contains(id))
        {
            Dictionary<string, string>? specifics = null;
            foreach (var sp in grant.Elements("specific"))
            {
                var spName = sp.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(spName)) continue;
                // OCB's WriteElemFields tally-row whitelist is Short
                // Description only
                // Source loot subtrees inline power-card specifics (Action
                // Type / Power Usage / Attack Type / etc.) on the cascaded
                // <RulesElement> for the granted power so the in-app card
                // can render without re-resolving the power; OCB does NOT
                // echo those onto the flat-tally row it writes for the
                // same id. Mirror that - anything except Short Description
                // belongs on PowerStats, not the tally row.
                if (!string.Equals(spName, "Short Description",
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                specifics ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                specifics[spName] = sp.Value;
            }
            var url = grant.Attribute("url")?.Value;
            tally.Add(new TallyElement(id, name, type, specifics, url));
        }
        foreach (var nested in grant.Elements("RulesElement"))
            AddCascadedGrantTally(tally, nested, existingIds, seenIds);
    }

    /// <summary>
    /// Collect all "extra" Power-typed RulesElements that should appear in
    /// PowerStats but aren't in the engine's element tree:
    ///
    /// 1. Cascaded magic-item grants (Harper Pin -> Lliira's Grace, etc.) —
    ///    captured as opaque XElement passthrough metadata at import time.
    /// 2. Replaced (swapped-out) elements (Acolyte Power feat replaces
    ///    Astral Refuge with Protective Scroll) — OCB keeps both in
    ///    PowerStats so the user can retrain back.
    /// </summary>
    private static IEnumerable<RulesElement> CollectExtraPowers(
        CharacterSession session, IRulesDatabase database, Engine.Creation.CharacterSnapshot snapshot)
    {
        foreach (var re in CollectCascadedGrantPowers(session, database))
        {
            if (!string.IsNullOrEmpty(re.InternalId)
                && snapshot.LevelNestedOnlyIds.Contains(re.InternalId))
                continue;
            yield return re;
        }

        foreach (var re in snapshot.Builder.ReplacedElements)
        {
            if (!string.Equals(re.Type, "Power", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(re.InternalId)
                && snapshot.LevelNestedOnlyIds.Contains(re.InternalId))
                continue;
            yield return re;
        }
    }

    /// <summary>
    /// Walk every loot item's cascaded grant subtrees and resolve any
    /// Power-typed <c>RulesElement</c> nodes against the rules database.
    /// These powers ARE part of the character (Magic Item → Class Feature
    /// → Power chains like Harper Pin's Lliira's Grace) but the importer
    /// keeps them as opaque XElement passthrough metadata, so they never
    /// enter the engine's element tree. Without this collection,
    /// <see cref="PowerStatsBuilder"/> would silently drop them and their
    /// <c>&lt;Power&gt;</c> blocks would vanish on export.
    /// </summary>
    private static IEnumerable<RulesElement> CollectCascadedGrantPowers(
        CharacterSession session, IRulesDatabase database)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lootSource in EnumerateAllLoot(session))
        {
            foreach (var grants in lootSource.CascadedGrantsByComponentId.Values)
            {
                foreach (var grant in grants)
                    foreach (var re in WalkCascadedPowers(grant, database, seen))
                        yield return re;
            }
        }
    }

    private static IEnumerable<RulesElement> WalkCascadedPowers(
        XElement grant,
        IRulesDatabase database,
        HashSet<string> seen)
    {
        var id = grant.Attribute("internal-id")?.Value;
        var type = grant.Attribute("type")?.Value;
        if (!string.IsNullOrEmpty(id)
            && string.Equals(type, "Power", StringComparison.OrdinalIgnoreCase)
            && seen.Add(id))
        {
            var re = database.FindByInternalId(id);
            if (re is not null) yield return re;
        }
        foreach (var nested in grant.Elements("RulesElement"))
            foreach (var re in WalkCascadedPowers(nested, database, seen))
                yield return re;
    }

    private static bool StatsHaveAnyContributionFromSource(StatBlock stats, string? sourceElementId)
    {
        if (string.IsNullOrWhiteSpace(sourceElementId))
            return false;

        foreach (var statName in stats.AllStatNames)
        {
            if (StatHasContributionFromSource(stats, statName, sourceElementId))
                return true;
        }

        return false;
    }

    private static bool StatHasContributionFromSource(StatBlock stats, string statName, string sourceElementId)
    {
        var stat = stats.TryGetStat(statName);
        return stat is not null
            && stat.Contributions.Any(c => string.Equals(c.SourceElementId, sourceElementId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Compute the carried-weight stat (sum of <c>Weight</c> field × count
    /// across every loot composite) and inject it into the stats dictionary
    /// as <c>Weight</c>. Mirrors OCB's <c>&lt;Stat value="N"&gt;&lt;alias
    /// name="Weight" /&gt;&lt;statadd value="N" /&gt;&lt;/Stat&gt;</c> output.
    /// </summary>
    private static void InjectInventoryWeightStat(
        Dictionary<string, StatExportData> stats,
        CharacterSession session)
    {
        double total = 0;

        foreach (var (_, loot) in session.GetEquippedLoot())
            total += LootWeight(loot, count: 1);
        foreach (var inv in session.GetInventory())
            total += LootWeight(inv.Item, count: inv.Quantity);

        // OCB writes Weight as an integer (truncate toward zero — matches the
        // baseline behavior on items with fractional pounds).
        int totalInt = (int)total;

        stats["Weight"] = new StatExportData
        {
            Value = totalInt,
            Contributions = new List<CharM.Serialization.StatContribution>
            {
                new(Type: null, Value: totalInt, SourceId: null),
            },
        };

        static double LootWeight(CharM.Engine.Creation.LootItem loot, int count)
        {
            if (count <= 0) return 0;
            double w = loot.Weight ?? BaseWeight(loot.Base);
            return w * count;
        }

        static double BaseWeight(CharM.Engine.Rules.RulesElement baseEl)
        {
            if (!baseEl.Fields.TryGetValue("Weight", out var raw) || string.IsNullOrWhiteSpace(raw))
                return 0;
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
    }

    /// <summary>
    /// Convert engine stat contributions into the serialization DTO,
    /// including <c>Level</c>, <c>requires</c>, <c>statlink</c>, and
    /// <c>abilmod</c> derived from <see cref="ValueExpression"/>.
    /// </summary>
    public static Dictionary<string, StatExportData> BuildStatExportData(CharM.Engine.Creation.CharacterSnapshot snapshot)
    {
        var result = new Dictionary<string, StatExportData>(StringComparer.OrdinalIgnoreCase);
        var statBlock = snapshot.Builder.Stats;
        foreach (var (name, _) in snapshot.GetAllStats())
        {
            var stat = statBlock.TryGetStat(name);
            int value = stat?.ComputeValue(statBlock) ?? 0;
            var contribs = new List<CharM.Serialization.StatContribution>();
            if (stat is not null)
            {
                foreach (var c in stat.Contributions)
                    contribs.Add(ToExportContribution(c, statBlock));
            }
            result[name] = new StatExportData
            {
                Value = value,
                Contributions = contribs,
            };
        }
        return result;
    }

    private static CharM.Serialization.StatContribution ToExportContribution(CharM.Engine.Evaluation.StatContribution c, CharM.Engine.Evaluation.StatBlock statBlock)
    {
        // Decode the value expression into emit-ready (value, statlink, abilmod) triple.
        // Literal           → value=Value
        // StatReference     → value=ScaleFactor, statlink=StatName
        // AbilityModifier   → value=1, statlink=StatName, abilmod=true
        // AbilityModFunction→ value=±1, statlink=StatName, abilmod=true
        int outValue = c.Value;
        string? statLink = null;
        bool abilMod = false;

        switch (c.Expression)
        {
            case ValueExpression.StatReference sref:
                outValue = sref.ScaleFactor;
                statLink = sref.StatName;
                break;
            case ValueExpression.AbilityModifier am:
                // From source "+X modifier" syntax. OCB emits these as a
                // stat reference to "X modifier" (no abilmod attribute) — the
                // "X modifier" stat is registered by Level 1 and computes the
                // ability modifier value. Distinct from AbilityModFunction
                // (source "ABILITYMOD(X)") which OCB emits as
                // statlink="X" abilmod="true".
                outValue = 1;
                statLink = am.StatName + " modifier";
                abilMod = false;
                break;
            case ValueExpression.AbilityModFunction amf:
                outValue = amf.Negate ? -1 : 1;
                statLink = amf.StatName;
                abilMod = true;
                break;
        }

        // Canonicalize statlink to the Stat's actual registered name. Resolves
        // aliases (dex → Dexterity) and case differences (LEVEL → Level) so the
        // emitted attribute matches OCB. Falls back to the directive's verbatim
        // name when the stat hasn't been created (e.g., references to stats that
        // only ever exist via lazy resolution).
        if (statLink is not null)
        {
            var resolved = statBlock.TryGetStat(statLink);
            if (resolved is not null)
                statLink = resolved.Name;
        }

        return new CharM.Serialization.StatContribution(c.BonusType, outValue, c.SourceElementId)
        {
            Level = c.Level,
            Requires = c.RequiresText,
            Wearing = c.Wearing,
            NotWearing = c.NotWearing,
            Conditional = c.Condition,
            StringPayload = c.StringPayload,
            StatLink = statLink,
            AbilMod = abilMod,
        };
    }
}
