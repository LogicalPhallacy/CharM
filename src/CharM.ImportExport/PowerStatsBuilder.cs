using System.Text.RegularExpressions;
using CharM.Engine.Creation;
using CharM.Engine.Powers;
using CharM.Engine.Rules;
using CharM.Serialization;

namespace CharM.ImportExport;

/// <summary>
/// Builds the precomputed <see cref="PowerStatEntry"/> list that goes into a
/// fresh-built character's <c>&lt;PowerStats&gt;</c> section.
///
/// One entry per active <c>Power</c> RulesElement on the character. Each entry
/// carries OCB-style <c>&lt;specific&gt;</c> metadata (Power Usage, Action Type,
/// Keywords, etc.) and a list of <see cref="PowerStatWeapon"/> children — one
/// per applicable owned weapon candidate, plus a synthetic "Unarmed" entry
/// when OCB's unarmed gate allows it. Equipped state still matters for sort
/// order, offhand/two-weapon requirements, and Ki Focus pair triggering, but
/// OCB's normal candidate list is owned loot with <c>count &gt;= 1</c>.
///
/// Imported characters keep their original verbatim PowerStats block via the
/// writer's <c>RawSections["PowerStats"]</c> passthrough — this builder only
/// runs when no raw section exists, so the 549 community round-trips stay
/// pristine.
/// </summary>
public static partial class PowerStatsBuilder
{
    /// <summary>
    /// Build the list. Returns an empty list when no powers are active or
    /// when an imported PowerStats block will already passthrough verbatim.
    /// </summary>
    /// <param name="snapshot">Built-character snapshot with stats + element tree.</param>
    /// <param name="weaponCandidates">
    /// Owned weapon-or-implement loot candidates with <c>count &gt;= 1</c>,
    /// plus synthetic Ki Focus pairings. Mirrors what OCB iterates via
    /// <c>IterateWeapons</c>.
    /// </param>
    /// <param name="characterLevel">For tier-up Hit text resolution.</param>
    /// <param name="extraPowers">
    /// Powers that should be emitted in addition to those discovered by
    /// walking the engine's element tree. Used for cascaded grants from
    /// equipped magic items (e.g. Harper Pin → Lliira's Grace) where the
    /// importer keeps the grant subtree as opaque round-trip metadata
    /// instead of materialising the grant into the engine tree. Deduped
    /// against tree-discovered powers via the same InternalId/Name key.
    /// </param>
    /// <param name="equippedCompositeKeys">
    /// CompositeKey set of currently-EQUIPPED loot. Used to gate powers
    /// whose Requirement field demands multiple wielded weapons
    /// ("wielding two melee weapons", "wielding both a thrown weapon and
    /// a melee weapon" — Throw and Stab, Quick Throw, etc.). Per the
    /// 4e rules a "wielded" weapon is one currently held, not merely
    /// owned: <paramref name="weaponCandidates"/> includes inventory loot for
    /// the broader power-card iteration, so we need a separate equipped
    /// set to evaluate wielding-state requirements. When null, the gate
    /// is skipped (legacy behaviour).
    /// </param>
    /// <param name="equippedWeaponSlotCount">
    /// Number of equipment slots currently holding a weapon-or-implement.
    /// Distinguishes "wielding two of the same item" (Carrikal in main +
    /// off, slot count 2) from "owning multiple of one item" (count 2 in
    /// inventory but only 1 equipped). Used by the multi-wielding gate
    /// to decide character-level dual-wielding state. When -1, the gate
    /// falls back to <paramref name="equippedCompositeKeys"/> cardinality
    /// (legacy behaviour — undercounts when same-item duplicates fill
    /// multiple slots).
    /// </param>
    /// <param name="equippedCompositeKeyCounts">
    /// Equipped slot count per composite key. This preserves OCB's
    /// <c>CharLootEquip &gt; 1</c> signal for two copies of the same composite
    /// item occupying both weapon slots.
    /// </param>
    public static List<PowerStatEntry> Build(
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyList<LootItem> weaponCandidates,
        int characterLevel,
        IEnumerable<RulesElement>? extraPowers = null,
        IReadOnlyList<LootItem>? allLoot = null,
        IReadOnlyList<RulesElement>? extraSourceElements = null,
        IReadOnlySet<string>? equippedCompositeKeys = null,
        int equippedWeaponSlotCount = -1,
        IReadOnlyDictionary<string, int>? equippedCompositeKeyCounts = null,
        IReadOnlyDictionary<string, string>? textStrings = null,
        int? precomputedBeastAttackBonus = null,
        IReadOnlyDictionary<string, Engine.Creation.ElementSourceMetadata>? sourceMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(weaponCandidates);

        var stats = snapshot.Builder.Stats;
        var entries = new List<PowerStatEntry>();
        var excluded = snapshot.PowerStatsExcludedIds;
        var sourceNameResolver = CreateSourceNameResolver(snapshot, extraSourceElements: extraSourceElements);
        var healingSourceNameResolver = CreateSourceNameResolver(snapshot, allLoot, extraSourceElements);
        var sourceElementResolver = CreateSourceElementResolver(snapshot, extraSourceElements);
        var activeElementNames = snapshot.Builder.ElementTree.GetActiveElements()
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasDualImplementSpellcaster = snapshot.Builder.ElementTree.GetActiveElements()
            .Any(e => string.Equals(e.InternalId, "ID_FMP_FEAT_1127", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Name, "Dual Implement Spellcaster", StringComparison.OrdinalIgnoreCase));

        // Pre-compute the "wielded" loot set used by the multi-wielding
        // gate in BuildEntry. A loot item counts as wielded when:
        //   * Its CompositeKey is in the equipped set (the obvious case:
        //     a worn weapon like Subtle Mace +4), OR
        //   * It's a synthesized Ki Focus pair (loot.Enchantment is a
        //     Ki Focus magic item) whose underlying weapon Base is the
        //     same Base as some equipped weapon. Ki pairs are
        //     synthesized in Dnd4eExporter.BuildKiFocusPairs and never
        //     appear in equippedCompositeKeys directly, but OCB does
        //     emit them under multi-weapon powers when the underlying
        //     weapon is wielded (e.g. Whirling Rend on a dual-wielding
        //     monk with a Rain of Hammers Ki Focus equipped).
        // When equippedCompositeKeys is null (legacy), wieldedLootKeys
        // stays null and the multi-wielding gate is a no-op.
        IReadOnlySet<string>? wieldedLootKeys = null;
        bool characterHasMultiWieldingState = false;
        if (equippedCompositeKeys is not null)
        {
            var equippedBaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasEquippedDouble = false;
            foreach (var loot in weaponCandidates)
            {
                if (!equippedCompositeKeys.Contains(loot.CompositeKey)) continue;
                if (loot.Base is { InternalId: { Length: > 0 } id })
                    equippedBaseIds.Add(id);
                if (!hasEquippedDouble && IsDoubleWeapon(loot, snapshot))
                    hasEquippedDouble = true;
            }
            // Slot-count covers the "Carrikal in main + Carrikal in off"
            // case (two equip slots filled by the same composite item)
            // which equippedCompositeKeys.Count cannot — the HashSet
            // collapses duplicates. Caller passes the raw slot count.
            int slotCount = equippedWeaponSlotCount >= 0
                ? equippedWeaponSlotCount
                : equippedCompositeKeys.Count;
            characterHasMultiWieldingState = slotCount >= 2 || hasEquippedDouble;

            var derived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loot in weaponCandidates)
            {
                if (equippedCompositeKeys.Contains(loot.CompositeKey))
                {
                    derived.Add(loot.CompositeKey);
                    continue;
                }
                if (IsKiFocusEnchantment(loot.Enchantment)
                    && loot.Base is { InternalId: { Length: > 0 } baseId }
                    && equippedBaseIds.Contains(baseId))
                {
                    derived.Add(loot.CompositeKey);
                }
            }
            wieldedLootKeys = derived;
        }

        // Mirror OCB's LinkCharElement (-Module-.cs:16257): the character's
        // flat element list (ws.character[7]) is deduplicated — when two
        // CharElements point to the same RulesElement, only one node is
        // linked. WritePowers walks that deduped list, so each unique Power
        // RulesElement gets exactly one <Power> block in <PowerStats>.
        // GetAllDescendants returns the level-tree nodes as-is (with
        // duplicates whenever a power is granted via multiple paths or
        // appears multiple times under a class feature), so we have to
        // dedupe here. Key on InternalId (with a Name fallback for
        // synthesized elements that lack one), preserving first-occurrence
        // order for stable diffs.
        var buildContext = new PowerStatsBuildContext(
            Stats: stats,
            WeaponCandidates: weaponCandidates,
            Snapshot: snapshot,
            CharacterLevel: characterLevel,
            SourceNameResolver: sourceNameResolver,
            HealingSourceNameResolver: healingSourceNameResolver,
            SourceElementResolver: sourceElementResolver,
            WieldedLootKeys: wieldedLootKeys,
            CharacterHasMultiWieldingState: characterHasMultiWieldingState,
            EquippedCompositeKeyCounts: equippedCompositeKeyCounts,
            HasDualImplementSpellcaster: hasDualImplementSpellcaster,
            TextStrings: textStrings,
            PrecomputedBeastAttackBonus: precomputedBeastAttackBonus);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in snapshot.Builder.ElementTree.Root.GetAllDescendants())
        {
            if (node.RulesElement is not { } re) continue;
            if (!string.Equals(re.Type, "Power", StringComparison.OrdinalIgnoreCase)) continue;

            // Tally vestiges (orphan powers preserved for round-trip) get
            // a tally row but no PowerStats card -- mirrors OCB which
            // excludes powers without a live build slot from <PowerStats>.
            if (!string.IsNullOrEmpty(re.InternalId) && excluded.Contains(re.InternalId)) continue;

            var key = !string.IsNullOrEmpty(re.InternalId) ? re.InternalId : "name:" + (re.Name ?? "");
            if (!seen.Add(key)) continue;

            var effectivePower = ResolveEffectivePower(re, sourceElementResolver);
            effectivePower = ApplyOverlayFields(effectivePower, snapshot.Builder.Overlay, sourceMetadata);
            effectivePower = MarkActiveNamedOptionFields(effectivePower, activeElementNames);
            if (PowerIsDilettanteChoice(node))
                effectivePower = WithSyntheticCategory(effectivePower, "dilettante");
            entries.Add(BuildEntry(effectivePower, buildContext));
        }

        // Cascaded grants from equipped magic items (Harper Pin → Lliira's
        // Grace, etc.). These never land in the engine's element tree
        // because the importer captures the grant subtree as opaque
        // passthrough metadata. Re-emit them as power cards so OCB sees
        // the same set of powers it would have written itself.
        if (extraPowers is not null)
        {
            foreach (var re in extraPowers)
            {
                if (re is null) continue;
                if (!string.Equals(re.Type, "Power", StringComparison.OrdinalIgnoreCase)) continue;
                var key = !string.IsNullOrEmpty(re.InternalId) ? re.InternalId : "name:" + (re.Name ?? "");
                if (!seen.Add(key)) continue;
                var effectivePower = ResolveEffectivePower(re, sourceElementResolver);
                effectivePower = ApplyOverlayFields(effectivePower, snapshot.Builder.Overlay, sourceMetadata);
                entries.Add(BuildEntry(effectivePower, buildContext));
            }
        }

        return entries;
    }

    private static RulesElement ResolveEffectivePower(
        RulesElement power,
        Func<string, RulesElement?> sourceElementResolver)
    {
        if (string.IsNullOrWhiteSpace(power.InternalId))
            return power;

        return sourceElementResolver(power.InternalId) ?? power;
    }

    private static RulesElement ApplyOverlayFields(
        RulesElement element,
        Engine.Evaluation.ModifyOverlay overlay,
        IReadOnlyDictionary<string, Engine.Creation.ElementSourceMetadata>? sourceMetadata = null)
    {
        Dictionary<string, string>? fields = null;
        foreach (var (field, value) in overlay.GetModificationsFor(element.InternalId))
        {
            fields ??= new Dictionary<string, string>(element.Fields, StringComparer.OrdinalIgnoreCase);
            fields[field] = value;
        }

        // Grant-node <specific name="..."> children carry per-instance field
        // overrides (e.g. Ring of Borrowed Spells grants Dancing Flames with
        // <specific name="Power Usage">Daily</specific>, downgrading the
        // base Encounter power to Daily for this character). These ride
        // along on the source RulesElementTally row via ElementSourceMetadata
        // and aren't represented as engine ModifyDirectives. Apply them
        // after the modify-driven overlay so per-instance overrides win,
        // but only for keys that already exist as power fields — that
        // skips display-only specifics (Short Description, Url, etc.).
        if (sourceMetadata is not null
            && !string.IsNullOrEmpty(element.InternalId)
            && sourceMetadata.TryGetValue(element.InternalId, out var meta)
            && meta.Specifics.Count > 0)
        {
            foreach (var (key, value) in meta.Specifics)
            {
                if (!element.Fields.ContainsKey(key))
                    continue;
                fields ??= new Dictionary<string, string>(element.Fields, StringComparer.OrdinalIgnoreCase);
                fields[key] = value;
            }
        }

        if (fields is null)
            return element;

        return RulesElementProjection.WithFields(element, fields, [.. element.Categories]);
    }

    private static bool PowerIsDilettanteChoice(Engine.CharacterModel.CharacterElement node)
    {
        if (string.Equals(node.SlotOwnerInternalId, "ID_FMP_RACIAL_TRAIT_643", StringComparison.OrdinalIgnoreCase))
            return true;

        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (string.Equals(parent.RulesElement?.InternalId, "ID_FMP_RACIAL_TRAIT_643", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static RulesElement WithSyntheticCategory(RulesElement power, string category)
    {
        if (power.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
            return power;

        return RulesElementProjection.WithFields(
            power,
            new Dictionary<string, string>(power.Fields, StringComparer.OrdinalIgnoreCase),
            [.. power.Categories, category]);
    }

    private static RulesElement MarkActiveNamedOptionFields(RulesElement power, IReadOnlySet<string> activeElementNames)
    {
        var activeOptionFields = power.Fields.Keys
            .Select(k => k.Trim())
            .Where(k => k.Length > 0 && activeElementNames.Contains(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activeOptionFields.Length == 0)
            return power;

        var fields = new Dictionary<string, string>(power.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["_Active Option Fields"] = string.Join("\n", activeOptionFields)
        };

        return RulesElementProjection.WithFields(power, fields, [.. power.Categories]);
    }

    private static PowerStatEntry BuildEntry(
        RulesElement power,
        PowerStatsBuildContext ctx)
    {
        var stats = ctx.Stats;
        var weaponCandidates = ctx.WeaponCandidates;
        var snapshot = ctx.Snapshot;
        var characterLevel = ctx.CharacterLevel;
        var sourceNameResolver = ctx.SourceNameResolver;
        var healingSourceNameResolver = ctx.HealingSourceNameResolver;
        var sourceElementResolver = ctx.SourceElementResolver;
        var wieldedLootKeys = ctx.WieldedLootKeys;
        var characterHasMultiWieldingState = ctx.CharacterHasMultiWieldingState;
        var equippedCompositeKeyCounts = ctx.EquippedCompositeKeyCounts;
        var hasDualImplementSpellcaster = ctx.HasDualImplementSpellcaster;
        var textStrings = ctx.TextStrings;
        var precomputedBeastAttackBonus = ctx.PrecomputedBeastAttackBonus;
        // OCB emits exactly two <specific> children per <Power>: Power Usage
        // and Action Type. The 9-field list we previously emitted was wrong
        // and floods the diff with extras for every power on every character.
        // Empty/missing values fall back to a single space (matches OCB
        // convention -- avoids self-closing <specific name="..." />).
        var specifics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fieldName in PowerSpecificFields)
        {
            specifics[fieldName] = power.Fields.TryGetValue(fieldName, out var val)
                && !string.IsNullOrWhiteSpace(val)
                    ? val
                    : " ";
        }

        var entry = new PowerStatEntry
        {
            Name = power.Name,
            Specifics = specifics,
        };

        // OCB skips per-weapon iteration for "reactive" powers — those
        // exposing a non-empty Trigger field whose Attack field is either
        // missing/empty or contains free-text prose (rather than the
        // structured "<Ability> vs. <Defense>" attack roll). Empirically
        // across the 549-file corpus:
        //   * Trigger + no structured Attack -> 0 weapon blocks emitted.
        //     Examples: Spirits' Rebuke, Combat Challenge, Opportunity
        //     Attack, Inevitable Shot, Fox's Cunning (Attack is prose).
        //   * Trigger + structured "Ability vs. Defense" Attack -> weapons
        //     emitted as normal. Examples: Avenging Smite ("Charisma vs.
        //     AC"), Warden's Fury, Death Surge, Guardian's Pounce, Slaad's
        //     Gambit, Killing Flames, Arrow of Warning.
        // The narrative semantics: reactive powers without a structured
        // attack roll re-invoke a basic attack (or apply pure effect text)
        // rather than rolling their own attack against any specific
        // weapon, so OCB's PowerStats writer has nothing per-weapon to
        // emit. The AllowUnarmed gate still runs below — many of these
        // skip-per-weapon powers do emit a single Unarmed block.
        bool powerHasTrigger = power.Fields.TryGetValue("Trigger", out var trig)
            && !string.IsNullOrWhiteSpace(trig);
        bool hasStructuredAttack = HasStructuredAttackField(power, "Attack")
            || HasStructuredAttackField(power, "Primary Attack")
            || HasStructuredAttackField(power, "Secondary Attack");
        // Reactive-action powers without a structured roll (Combat
        // Challenge, Combat Superiority, etc.) describe their trigger
        // condition in Effect prose. They never roll their own attack —
        // they let the player make a basic attack as the response — so
        // OCB emits zero <Weapon> blocks under them.
        bool actionType = power.Fields.TryGetValue("Action Type", out var at)
            && !string.IsNullOrWhiteSpace(at)
            && (at.IndexOf("Immediate", StringComparison.OrdinalIgnoreCase) >= 0
                || at.IndexOf("Opportunity", StringComparison.OrdinalIgnoreCase) >= 0);
        // Parent zone-creation powers (Fey Sinkhole, Wall of Fire, etc.)
        // expose a _ChildPower field pointing to the actual attack power.
        // The parent's Effect describes zone setup; the child does the
        // roll. OCB emits zero <Weapon> blocks under the parent and emits
        // weapons under the child. Only treat as no-emit when the parent
        // also lacks a structured Attack field — parents like Ensnaring
        // Shot both grant a child AND roll their own Primary Attack, and
        // those still emit weapons.
        bool hasChildPower = power.Fields.TryGetValue("_ChildPower", out var cp)
            && !string.IsNullOrWhiteSpace(cp);
        // Pure utility / effect-only powers (Punishing Eye conjuration,
        // Cloud of Daggers' parent zone, etc.) have no Attack field, no
        // Hit-line dice expression, and no Trigger — only an Effect
        // describing setup or terrain. OCB's PowerStats(weapon, power)
        // returns null for these and emits zero <Weapon> blocks.
        bool hasHitDice = power.Fields.TryGetValue("Hit", out var hit)
            && !string.IsNullOrWhiteSpace(hit)
            && HitDiceRegex().IsMatch(hit);
        bool isPureUtility = !hasStructuredAttack && !powerHasTrigger
            && !hasChildPower && !hasHitDice;
        // Hardcoded internal-id suppression. OCB's IsValidPowerCombo
        // (D20Workspace.cs:3969) compares the candidate power's internal-id
        // against ONE specific unnamed-global string constant and returns
        // false (no weapon block emitted) when it matches. Empirically that
        // ID is ID_FMP_POWER_11615 (Coordinated Assault Attack): the parent
        // power "Coordinated Assault" hands its attack to an ally (Effect:
        // "The target can use the power..."), and OCB suppresses any
        // weapon-stat emission because the actual weapon comes from the
        // ally's inventory on their turn, not the caster's. Note that other
        // structurally-identical "delegated attack" powers (Destructive
        // Surprise Attack ID_FMP_POWER_11604, Beckoning Strike Attack etc.)
        // are NOT on OCB's blacklist — OCB emits weapon blocks for them
        // even though, by 4e rules-as-written, the ally should be the
        // attacker too. This is a literal one-id hardcode in OCB, not a
        // generic predicate. See docs/engine-special-cases.md §1.
        //
        // The rules-correct behaviour would gate on the broader proxy
        // predicate instead — suppress weapons for EVERY power whose
        // parent says "the target/ally can use the power X". That call
        // would look like:
        //
        //     bool isProxyDelegatedAttack =
        //         PowerStatCalculator.IsProxyDelegatedAttack(power, sourceElementResolver);
        //     // ... include `|| isProxyDelegatedAttack` in skipPerWeapon below.
        //
        // We deliberately do NOT use the broader predicate because every
        // existing community character was built against the OCB
        // half-suppressed behaviour (weapons emitted with caster's attack
        // stats, Damage / DamageComponents empty). Tracking corpus parity
        // requires matching the OCB output. PowerStatCalculator's existing
        // ShouldSuppressProxyAttackDamage already produces that
        // half-suppressed output for the 228 non-hardcoded proxy powers.
        bool isHardcodedNoWeapons = string.Equals(
            power.InternalId,
            "ID_FMP_POWER_11615",
            StringComparison.OrdinalIgnoreCase);
        bool skipPerWeapon = (powerHasTrigger && !hasStructuredAttack)
            || (actionType && !hasStructuredAttack)
            || (hasChildPower && !hasStructuredAttack)
            || isPureUtility
            || isHardcodedNoWeapons;

        // Replicate OCB's per-power export loop (decompiled WritePower at
        // -Module-.cs:11899 — see docs/ocb-powerstats-algorithm.md). For each
        // owned weapon-or-implement candidate, run IsValidPowerCombo and emit
        // a <Weapon> block when it passes. Then attempt the unarmed/null slot
        // last, which is gated by AllowUnarmed.
        if (!skipPerWeapon)
        {
            // Multi-wielding gate: powers whose Requirement field
            // unambiguously demands two wielded weapons ("wielding both
            // ... and ...", "wielding two ...") only emit blocks for
            // currently-WIELDED items, and only when the character is
            // actually dual-wielding (>= 2 equipped weapons, or one
            // equipped double weapon — Spiked Chain Training etc.). 4e
            // treats "wielding" as "currently held in a hand"; OCB's
            // per-power IsRestricted path uses DetermineOffhand
            // (-Module-.cs:22477) to decide whether two-wielding
            // requirements are satisfied. Without this gate we emit phantom
            // Throw and Stab / Quick Throw rows for every dagger in
            // inventory. wieldedLootKeys covers worn weapons AND Ki
            // Focus pairs whose underlying base is worn (computed in
            // Build); see RequiresMultipleWieldedWeapons for the broad
            // pre-gate exclusions. OCB's exact
            // "two melee weapons or a ranged weapon" branch is handled
            // inside IsValidPowerCombo where each candidate's ranged-vs-melee
            // classification is known.
            bool requiresMultiWielding = wieldedLootKeys is not null
                && RequiresMultipleWieldedWeapons(power);

            var validWeaponCandidates = new List<(LootItem Loot, int? DualImplementOtherEnhancementBonus)>();
            foreach (var loot in weaponCandidates)
            {
                if (requiresMultiWielding)
                {
                    if (!characterHasMultiWieldingState) continue;
                    if (!wieldedLootKeys!.Contains(loot.CompositeKey)) continue;
                }
                if (IsValidPowerCombo(power, loot, snapshot, weaponCandidates,
                        wieldedLootKeys, equippedCompositeKeyCounts))
                {
                    int? dualImplementOtherEnhancementBonus = GetDualImplementOtherEnhancementBonus(
                        power,
                        loot,
                        snapshot,
                        weaponCandidates,
                        wieldedLootKeys,
                        equippedCompositeKeyCounts,
                        hasDualImplementSpellcaster,
                        textStrings);
                    validWeaponCandidates.Add((loot, dualImplementOtherEnhancementBonus));
                }
            }

            var builtWeaponBlocks = new List<(LootItem Loot, int? DualImplementOtherEnhancementBonus, PowerStatWeapon Block)>();
            foreach (var (loot, dualImplementOtherEnhancementBonus) in validWeaponCandidates)
            {
                var block = BuildWeaponBlock(power, loot, ctx, dualImplementOtherEnhancementBonus);
                if (HasMeaningfulStats(block, power))
                    builtWeaponBlocks.Add((loot, dualImplementOtherEnhancementBonus, block));
            }

            bool displayZeroUnselectedModeVariant = ShouldDisplayZeroUnselectedModeVariant(power, builtWeaponBlocks.Count);
            foreach (var (loot, dualImplementOtherEnhancementBonus, block) in builtWeaponBlocks)
            {
                entry.Weapons.Add(displayZeroUnselectedModeVariant
                    ? BuildWeaponBlock(power, loot, ctx, dualImplementOtherEnhancementBonus, displayZeroUnselectedModeVariant)
                    : block);
            }
        }
        if (PowerFieldParser.AllowUnarmed(power)
            && !IsVerboseUnsatisfiableRequirement(power)
            && !isHardcodedNoWeapons)
            TryAddWeaponBlock(entry, power, null, ctx);

        return entry;
    }

    private static int? GetDualImplementOtherEnhancementBonus(
        RulesElement power,
        LootItem currentLoot,
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyList<LootItem> weaponCandidates,
        IReadOnlySet<string>? wieldedLootKeys,
        IReadOnlyDictionary<string, int>? equippedCompositeKeyCounts,
        bool hasDualImplementSpellcaster,
        IReadOnlyDictionary<string, string>? textStrings)
    {
        if (!hasDualImplementSpellcaster
            || wieldedLootKeys is null
            || !wieldedLootKeys.Contains(currentLoot.CompositeKey)
            || !IsDualImplementSpellcasterPower(power))
        {
            return null;
        }

        if (TryGetHandedDualImplementEnhancement(
                power,
                currentLoot,
                snapshot,
                weaponCandidates,
                wieldedLootKeys,
                equippedCompositeKeyCounts,
                textStrings,
                out int handedEnhancement))
        {
            return handedEnhancement;
        }

        var candidates = new List<int>();
        if (equippedCompositeKeyCounts is not null
            && equippedCompositeKeyCounts.TryGetValue(currentLoot.CompositeKey, out int currentCount)
            && currentCount > 1
            && TryGetLootEnhancement(currentLoot, out int duplicateEnhancement))
        {
            candidates.Add(duplicateEnhancement);
        }

        foreach (var otherLoot in weaponCandidates)
        {
            if (string.Equals(otherLoot.CompositeKey, currentLoot.CompositeKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!wieldedLootKeys.Contains(otherLoot.CompositeKey))
                continue;
            if (!IsValidPowerCombo(power, otherLoot, snapshot, weaponCandidates, wieldedLootKeys, equippedCompositeKeyCounts))
                continue;
            if (TryGetLootEnhancement(otherLoot, out int enhancement))
                candidates.Add(enhancement);
        }

        // Mirror OCB's implicit off-hand selection when the character wields
        // 3+ implements (e.g. Hybrid Monk/Sorcerer with main hand weapon +
        // Ki Focus + Ki Weapon). OCB designates the lowest-enhancement
        // wielded non-main implement as the conceptual off-hand. When the
        // current weapon block IS that designated off-hand, the off-hand
        // bonus comes from the MAIN HAND instead (so the off-hand still
        // contributes its own enhancement, just from the other slot).
        // For the common 2-implement case (main + one other), Min equals
        // Max so this is no behavior change.
        var mainHand = FindMainHandLoot(textStrings, weaponCandidates);
        if (mainHand is not null
            && !string.Equals(mainHand.CompositeKey, currentLoot.CompositeKey, StringComparison.OrdinalIgnoreCase)
            && TryGetLootEnhancement(mainHand, out int mainHandEnhancement))
        {
            (LootItem loot, int enh)? designated = null;
            foreach (var otherLoot in weaponCandidates)
            {
                if (string.Equals(otherLoot.CompositeKey, mainHand.CompositeKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!wieldedLootKeys.Contains(otherLoot.CompositeKey))
                    continue;
                if (!IsValidPowerCombo(power, otherLoot, snapshot, weaponCandidates, wieldedLootKeys, equippedCompositeKeyCounts))
                    continue;
                if (!TryGetLootEnhancement(otherLoot, out int otherEnh))
                    continue;
                if (designated is null || otherEnh < designated.Value.enh)
                    designated = (otherLoot, otherEnh);
            }

            if (designated is not null
                && string.Equals(designated.Value.loot.CompositeKey, currentLoot.CompositeKey, StringComparison.OrdinalIgnoreCase))
            {
                return mainHandEnhancement;
            }
        }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    private static bool TryGetHandedDualImplementEnhancement(
        RulesElement power,
        LootItem currentLoot,
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyList<LootItem> weaponCandidates,
        IReadOnlySet<string> wieldedLootKeys,
        IReadOnlyDictionary<string, int>? equippedCompositeKeyCounts,
        IReadOnlyDictionary<string, string>? textStrings,
        out int enhancement)
    {
        enhancement = 0;

        if (IsOffHandPreferredLoot(currentLoot))
        {
            var mainHand = FindMainHandLoot(textStrings, weaponCandidates);
            return mainHand is not null
                && !string.Equals(mainHand.CompositeKey, currentLoot.CompositeKey, StringComparison.OrdinalIgnoreCase)
                && wieldedLootKeys.Contains(mainHand.CompositeKey)
                && IsValidPowerCombo(power, mainHand, snapshot, weaponCandidates, wieldedLootKeys, equippedCompositeKeyCounts)
                && TryGetLootEnhancement(mainHand, out enhancement);
        }

        var offHandEnhancements = new List<int>();
        foreach (var otherLoot in weaponCandidates)
        {
            if (string.Equals(otherLoot.CompositeKey, currentLoot.CompositeKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsOffHandPreferredLoot(otherLoot))
                continue;
            if (!IsValidPowerCombo(power, otherLoot, snapshot, weaponCandidates, wieldedLootKeys, equippedCompositeKeyCounts))
                continue;
            if (TryGetLootEnhancement(otherLoot, out int otherEnhancement))
                offHandEnhancements.Add(otherEnhancement);
        }

        if (offHandEnhancements.Count == 0)
            return false;

        enhancement = offHandEnhancements.Max();
        return true;
    }

    private static bool IsOffHandPreferredLoot(LootItem loot)
        => (!string.IsNullOrWhiteSpace(loot.WornCategoryId)
                && loot.WornCategoryId.Contains("OFF_HAND", StringComparison.OrdinalIgnoreCase))
            || HasOffHandProperty(loot.Enchantment)
            || HasOffHandProperty(loot.Base);

    private static bool HasOffHandProperty(RulesElement? element)
        => element is not null
            && element.Fields.TryGetValue("Property", out var property)
            && !string.IsNullOrWhiteSpace(property)
            && (property.Contains("off hand", StringComparison.OrdinalIgnoreCase)
                || property.Contains("off-hand", StringComparison.OrdinalIgnoreCase));

    private static LootItem? FindMainHandLoot(
        IReadOnlyDictionary<string, string>? textStrings,
        IReadOnlyList<LootItem> weaponCandidates)
    {
        if (textStrings is null
            || !textStrings.TryGetValue("_INTERNAL_MainHandWeapon", out string? mainHandName)
            || string.IsNullOrWhiteSpace(mainHandName))
        {
            return null;
        }

        mainHandName = mainHandName.Trim();
        return weaponCandidates.FirstOrDefault(loot =>
            string.Equals(LootNaming.Compose(loot), mainHandName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDualImplementSpellcasterPower(RulesElement power)
    {
        var keywords = PowerFieldParser.GetKeywords(power).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return keywords.Contains("Arcane") && keywords.Contains("Implement");
    }

    private static bool TryGetLootEnhancement(LootItem loot, out int enhancement)
    {
        enhancement = 0;
        if (loot.Augment is not null
            && loot.Augment.Fields.TryGetValue("Enhancement", out string? augmentEnhancement)
            && TryParseEnhancement(augmentEnhancement, out enhancement))
        {
            return true;
        }

        if (loot.Enchantment is not null
            && loot.Enchantment.Fields.TryGetValue("Enhancement", out string? enchantmentEnhancement)
            && TryParseEnhancement(enchantmentEnhancement, out enhancement))
        {
            return true;
        }

        return loot.Base.Fields.TryGetValue("Enhancement", out string? baseEnhancement)
            && TryParseEnhancement(baseEnhancement, out enhancement);
    }

    private static bool TryParseEnhancement(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.AsSpan().TrimStart();
        int sign = 1;
        if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
        {
            if (span[0] == '-') sign = -1;
            span = span[1..];
        }

        int i = 0;
        while (i < span.Length && char.IsDigit(span[i]))
            i++;
        if (i == 0) return false;
        value = sign * int.Parse(span[..i], System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static Func<string, string?> CreateSourceNameResolver(
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyList<LootItem>? allLoot = null,
        IReadOnlyList<RulesElement>? extraSourceElements = null)
    {
        var namesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in snapshot.Builder.ElementTree.GetActiveElements())
        {
            if (string.IsNullOrWhiteSpace(element.InternalId)) continue;
            namesById.TryAdd(element.InternalId, element.Name);
        }

        foreach (var loot in allLoot ?? [])
        {
            foreach (var component in loot.Components())
            {
                if (string.IsNullOrWhiteSpace(component.InternalId)) continue;
                namesById[component.InternalId] = component.Name;
            }
        }

        foreach (var element in extraSourceElements ?? [])
        {
            if (string.IsNullOrWhiteSpace(element.InternalId)) continue;
            namesById[element.InternalId] = element.Name;
        }

        return id => namesById.TryGetValue(id, out string? name) ? name : null;
    }

    private static Func<string, RulesElement?> CreateSourceElementResolver(
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyList<RulesElement>? extraSourceElements = null)
    {
        var elementsById = new Dictionary<string, RulesElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in snapshot.Builder.ElementTree.GetActiveElements())
        {
            if (string.IsNullOrWhiteSpace(element.InternalId)) continue;
            elementsById.TryAdd(element.InternalId, ApplyOverlay(element));
        }

        foreach (var element in extraSourceElements ?? [])
        {
            if (string.IsNullOrWhiteSpace(element.InternalId)) continue;
            elementsById[element.InternalId] = element;
        }

        return id => elementsById.TryGetValue(id, out RulesElement? element) ? element : null;

        RulesElement ApplyOverlay(RulesElement element)
        {
            Dictionary<string, string>? fields = null;
            foreach (var (field, value) in snapshot.Builder.Overlay.GetModificationsFor(element.InternalId))
            {
                fields ??= new Dictionary<string, string>(element.Fields, StringComparer.OrdinalIgnoreCase);
                fields[field] = value;
            }

            if (fields is null)
                return element;

            return RulesElementProjection.WithFields(element, fields);
        }
    }

    /// <summary>
    /// Mirrors OCB's second gate at <c>-Module-.cs:11827-11830</c>:
    /// <c>WritePowerStat</c> calls <c>PowerStats(weapon, power)</c> and bails
    /// silently if it returns null. <c>PowerStats</c> returns null when the
    /// power has no computable attack/damage/defense/healing for that
    /// (weapon, power) pair — typically utility powers (Mage Hand, Wild Shape),
    /// healing-only powers without an Attack field (Second Wind), and the
    /// unarmed-null case for powers that strictly require a weapon
    /// (Opportunity Attack with no valid weapon, implement powers when
    /// the unarmed slot is attempted, etc.). Without this gate every utility
    /// power floods the export with empty Unarmed blocks.
    /// </summary>
    private static void TryAddWeaponBlock(
        PowerStatEntry entry,
        RulesElement power,
        LootItem? weaponLoot,
        PowerStatsBuildContext ctx,
        int? dualImplementOtherEnhancementBonus = null,
        bool displayZeroUnselectedModeVariant = false)
    {
        var block = BuildWeaponBlock(power, weaponLoot, ctx, dualImplementOtherEnhancementBonus, displayZeroUnselectedModeVariant);
        if (HasMeaningfulStats(block, power))
            entry.Weapons.Add(block);
    }

    private static bool ShouldDisplayZeroUnselectedModeVariant(RulesElement power, int validWeaponCandidateCount)
    {
        if (validWeaponCandidateCount <= 1)
            return false;
        return power.Fields.TryGetValue("Hit", out var hit)
            && hit.Contains("Increase damage to ", StringComparison.OrdinalIgnoreCase)
            && hit.Contains(" (melee) or ", StringComparison.OrdinalIgnoreCase)
            && hit.Contains(" (ranged)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMeaningfulStats(PowerStatWeapon block, RulesElement power)
    {
        // OCB's PowerStats returns null when there's nothing useful to write.
        // We approximate by checking whether any output field carries real
        // content. Powers with the Healing keyword always get a block with
        // HealingComponents (OCB emits placeholder AttackBonus/AttackStat
        // "Unknown" defenses for them) — see Healing Word vs. Second Wind:
        // both heal, but only Healing Word has "Healing" in Keywords and
        // only it gets a Weapon block in OCB exports.
        if (PowerFieldParser.IsHealingPower(power)) return true;

        if (!string.IsNullOrWhiteSpace(block.Damage)) return true;
        if (!string.IsNullOrWhiteSpace(block.AttackStat)
            && !string.Equals(block.AttackStat, "Unknown", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(block.Defense)) return true;
        if (!string.IsNullOrWhiteSpace(block.HitComponents)) return true;
        if (!string.IsNullOrWhiteSpace(block.DamageComponents)) return true;
        if (!string.IsNullOrWhiteSpace(block.Healing)) return true;
        if (!string.IsNullOrWhiteSpace(block.HealingComponents)) return true;
        if (!string.IsNullOrWhiteSpace(block.Conditions)) return true;
        return false;
    }

    /// <summary>
    /// Replicates OCB's <c>IsValidPowerCombo(weapon, power)</c> at
    /// <c>D20Workspace.cs:3963</c>. This is the master gate that decides
    /// whether a (weapon, power) pair gets a <c>&lt;Weapon&gt;</c> block in
    /// the saved file. The null-weapon case is handled in
    /// <see cref="BuildEntry"/> via <see cref="PowerFieldParser.AllowUnarmed"/>;
    /// this method only handles the non-null cases.
    /// </summary>
    private static bool IsValidPowerCombo(
        RulesElement power,
        LootItem loot,
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyList<LootItem> weaponCandidates,
        IReadOnlySet<string>? wieldedLootKeys,
        IReadOnlyDictionary<string, int>? equippedCompositeKeyCounts)
    {
        // Implement-power dispatch: any power whose Keywords contains
        // "Implement" only emits weapon blocks for valid implements.
        if (PowerFieldParser.IsImplementPower(power))
            return IsValidImplement(loot, power, snapshot);

        // Weapon-power dispatch: melee/ranged matching against the weapon's
        // FigureWeaponType result. We approximate FigureWeaponType using the
        // base weapon's Range field (non-empty Range → ranged-capable).
        if (PowerFieldParser.IsWeaponPower(power))
        {
            // Accept either a true Weapon-typed base, a baseless Magic Item
            // with Magic Item Type = "Weapon", OR a Superior Implement /
            // Implement base whose enchantment's Magic Item Type is
            // "Weapon" (Lancing dagger + Lightning Weapon, etc.: the
            // enchant tags the combo as weapon-usable). Also accept any
            // base that exposes a WeaponEquiv field — this is the
            // staff-as-quarterstaff path that lets Mindwarp staff /
            // Staff of Corrosion / etc. show up under Melee Basic Attack
            // and other weapon-keyword powers (mirrors OCB's WeaponEquiv
            // resolution in -Module-.cs:18728).
            RulesElement? weaponEquiv = ResolveWeaponEquiv(loot, snapshot);
            bool isWeaponOrEquivalent =
                string.Equals(loot.Base.Type, "Weapon", StringComparison.OrdinalIgnoreCase)
                || IsWeaponMagicItem(loot.Base)
                || IsWeaponEnchanted(loot.Enchantment)
                || weaponEquiv is not null;
            if (!isWeaponOrEquivalent) return false;

            var weaponType = FigureWeaponType(loot, weaponEquiv, snapshot);
            bool melee = weaponType.Melee;
            bool ranged = weaponType.Ranged;

            // Conservative IsRestricted port (-Module-.cs:21472, gated):
            // OCB's IsRestricted parses the Requirement field and prunes
            // weapons that don't match it. Most token branches require
            // WeaponSwap/stat-driven exception lookups we don't model
            // (e.g. "wielding a light blade" succeeds for a Mace because
            // some character-feature stat allows the swap). To stay safe
            // we only trigger on Requirements that OCB itself can never
            // satisfy via the substring fallback — verbose prose
            // requirements with no comma break, where the entire trailing
            // clause becomes one giant token that no weapon name contains
            // (e.g. Bonds of Moonlight: "wielding a light thrown weapon
            // or a heavy thrown weapon to make a melee attack with this
            // power"). For those, OCB returns true (= restricted) and
            // emits zero weapons; we mirror by pruning here.
            if (IsVerboseUnsatisfiableRequirement(power))
                return false;

            // Selective IsRestricted port for "safe" Requirement tokens —
            // tokens whose OCB predicate is a pure structural test against
            // the weapon (no WeaponSwap rescue path needed). Currently:
            // "dagger" → require base = Dagger, "thrown weapon" → require
            // melee weapon with a Range field. See
            // <see cref="IsRestrictedBySafeRequirementToken"/> for the full
            // list and rationale. All other tokens (light blade, axe,
            // crossbow, "two melee weapons", etc.) are deliberately left
            // un-gated because OCB rescues them via stat-driven
            // WeaponSwap exceptions we don't model.
            if (IsRestrictedBySafeRequirementToken(power, loot.Base, weaponEquiv, snapshot))
                return false;

            if (RequiresTwoMeleeWeaponsOrRangedWeapon(power)
                && !SatisfiesTwoMeleeWeaponsOrRangedWeapon(
                    loot, weaponCandidates, wieldedLootKeys,
                    equippedCompositeKeyCounts, snapshot, melee, ranged))
            {
                return false;
            }

            if (RequiresBothThrownWeaponAndMeleeWeapon(power)
                && !SatisfiesBothThrownWeaponAndMeleeWeapon(
                    loot, weaponCandidates, equippedCompositeKeyCounts, snapshot))
            {
                return false;
            }

            if (melee && PowerFieldParser.IsMeleePower(power)) return true;
            if (ranged && PowerFieldParser.IsRangedPower(power)) return true;
            return false;
        }

        // Power has neither Weapon nor Implement keyword — no per-weapon
        // block (matches OCB falling through to the final `return false`).
        return false;
    }

    private readonly record struct WeaponType(bool Melee, bool Ranged);

    /// <summary>
    /// Mirrors OCB's <c>FigureWeaponType</c> (-Module-.cs:22336): classify
    /// the effective weapon by its rules text, then treat melee weapons with
    /// a Range field as ranged-capable too.
    /// </summary>
    private static WeaponType FigureWeaponType(
        LootItem loot,
        RulesElement? weaponEquiv,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        // Range source: consult the engine's ModifyOverlay first so that
        // feats like SimpleMilitaryAsThrown (which add Range="5/10" to
        // every simple/military weapon) get respected. Fall back to the
        // resolved WeaponEquiv (Mindwarp staff defers to Quarterstaff,
        // which has no Range -- staff is melee-only).
        string? range = snapshot.Builder.Overlay.GetField(loot.Base, "Range");
        bool weaponHasRange = !string.IsNullOrWhiteSpace(range);
        if (!weaponHasRange && weaponEquiv is not null)
        {
            string? equivRange = snapshot.Builder.Overlay.GetField(weaponEquiv, "Range");
            if (!string.IsNullOrWhiteSpace(equivRange))
                weaponHasRange = true;
        }

        string fullText = snapshot.Builder.Overlay.GetField(loot.Base, "Full Text") ?? string.Empty;
        if (string.IsNullOrEmpty(fullText) && weaponEquiv is not null)
            fullText = snapshot.Builder.Overlay.GetField(weaponEquiv, "Full Text") ?? string.Empty;

        bool melee = fullText.IndexOf("melee weapon", StringComparison.OrdinalIgnoreCase) >= 0;
        bool ranged = fullText.IndexOf("ranged weapon", StringComparison.OrdinalIgnoreCase) >= 0;
        if (melee && weaponHasRange) ranged = true;

        // Magic-item-only bases (no Full Text classifier; Vistani Tambourine
        // path arrives here too via IsWeaponMagicItem). Default to
        // melee-capable so we don't lose existing emissions while still
        // gating ranged on Range presence.
        if (!melee && !ranged)
        {
            melee = true;
            ranged = weaponHasRange;
        }

        return new WeaponType(melee, ranged);
    }

    /// <summary>
    /// Replicates OCB's <c>IsValidImplement(weapon, power)</c> at
    /// <c>-Module-.cs:22084</c>. Practical port covering the cases that show
    /// up in the community corpus:
    /// <list type="number">
    ///   <item>Base is an Implement RE → universally valid for implement powers.</item>
    ///   <item>Enchantment exposes <c>_ImplementEquiv</c> (e.g. Vistani Tambourine)
    ///     and the character has the matching <c>Implement Proficiency (X)</c>.</item>
    ///   <item>Weapon's or enchant's <c>Class</c> field lists a class the
    ///     character has (e.g. Ki Focus weapons that are Monk-only).</item>
    ///   <item>Weapon's or enchant's <c>Power</c> field lists this specific
    ///     power by name (item-grants-power pattern).</item>
    /// </list>
    /// The full WotC algorithm consults
    /// <c>ImplementName / ImplementProficiency / ImplementWeaponEquiv /
    /// ForClass / ForPower</c>; see <c>docs/ocb-powerstats-algorithm.md</c>.
    /// </summary>
    /// <summary>
    /// Convenience wrapper kept only for the `(power, loot)` argument order seen
    /// at <see cref="ResolveImplementMatch"/>. Forwards to the canonical
    /// `(loot, power)` overload.
    /// </summary>
    private static bool IsValidImplement(
        RulesElement power,
        LootItem loot,
        Engine.Creation.CharacterSnapshot snapshot)
        => IsValidImplement(loot, power, snapshot);

    private static bool IsValidImplement(
        LootItem loot,
        RulesElement power,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        // Case 1: a true Implement-typed item is always valid for an
        // implement power (the rest of the chain narrows to specific group
        // proficiency, which we don't need at this level for the round-trip
        // shape — OCB shows every equipped implement on every implement
        // power for proficient classes; non-proficient is rare in built data).
        // Includes the modern Superior Implement type (Mindwarp staff,
        // Accurate Ki Focus, etc.) and the Gear-typed basic implement bases
        // (Holy Symbol, Ki Focus, Totem, Orb/Rod/Staff/Tome/Wand Implement).
        if (IsImplementTypedBase(loot.Base))
            return true;

        var tree = snapshot.Builder.ElementTree;

        // Case 2: enchantment's _ImplementEquiv (Vistani Tambourine pattern).
        // OCB's ImplementName at -Module-.cs:21937-21945 reads the field and
        // returns the value when non-empty, then IsValidImplement at
        // -Module-.cs:22054-22057 short-circuits true via the global-194
        // (empty-string) compare for any item that surfaces a non-trivial
        // implement name. Empirical match: Azula (Hybrid Monk/Sorcerer with
        // no Instrument prof) emits Vistani Tambourine +1 under sorcerer
        // implement powers; QingQing emits it under cleric/wizard powers.
        // So `_ImplementEquiv` non-empty is sufficient to mark the item as
        // a valid implement carrier regardless of matching proficiency.
        if (loot.Enchantment is { } ench
            && ench.Fields.TryGetValue("_ImplementEquiv", out var equiv)
            && !string.IsNullOrWhiteSpace(equiv))
        {
            return true;
        }
        if (loot.Base.Fields.TryGetValue("_ImplementEquiv", out var baseEquiv)
            && !string.IsNullOrWhiteSpace(baseEquiv))
        {
            return true;
        }

        // Case 2b: weapon-as-implement via Implement Proficiency (Swordmage,
        // Eldritch Knight, Sorcerer-with-implement-feat, etc.). The character
        // has an Implement Proficiency (X) element where X is either the
        // weapon's exact name (e.g. "Longsword") or one of its groups
        // (e.g. "Heavy Blade group" for any Heavy Blade weapon). Group field
        // is comma-separated; match each group as "{group} group".
        if (string.Equals(loot.Base.Type, "Weapon", StringComparison.OrdinalIgnoreCase))
        {
            if (tree.HasElement($"Implement Proficiency ({loot.Base.Name})"))
                return true;
            if (loot.Base.Fields.TryGetValue("Group", out var groups) && !string.IsNullOrWhiteSpace(groups))
            {
                foreach (var raw in groups.Split(','))
                {
                    var g = raw.Trim();
                    if (g.Length == 0) continue;
                    if (tree.HasElement($"Implement Proficiency ({g} group)"))
                        return true;
                }
            }
        }

        // Case 3: ForClass — weapon's or enchant's `Class` field lists a
        // class on the character. Field is comma-separated; tokens may be
        // class names or class internal-ids.
        if (MatchesClassList(loot.Base, tree)) return true;
        if (loot.Enchantment is not null && MatchesClassList(loot.Enchantment, tree)) return true;

        // Case 4: ForPower — weapon's or enchant's `Power` field lists this
        // specific power name. Comma-separated.
        if (MatchesPowerList(loot.Base, power)) return true;
        if (loot.Enchantment is not null && MatchesPowerList(loot.Enchantment, power)) return true;

        // Case 5: OCB's ImplementName fallback (-Module-.cs:21952) returns
        // global-185, which appears to be the empty string. IsValidImplement
        // (-Module-.cs:22054) compares against global-194 — also empty —
        // making the wcscmp succeed. Net effect: any non-staff/non-ki-focus
        // weapon base whose ImplementName falls through is silently accepted
        // as an implement carrier for any implement-keyword power. The
        // empirical pattern across the corpus matches: PreGenDruid (no Light
        // Blade implement prof) emits Dagger under druid implement powers;
        // Insublock (no Light Blade prof) emits Wrist Razors under warlock
        // implement powers; Dizzymace emits Rhythm Blade Dagger under
        // hybrid invoker powers. Restrict to Weapon-typed bases so we don't
        // accidentally accept stray armor / gear.
        if (string.Equals(loot.Base.Type, "Weapon", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if the loot's base RulesElement is a recognized
    /// implement-typed item (Implement, Superior Implement, one of the
    /// Gear-typed basic implement bases, or a baseless Magic Item whose
    /// <c>Magic Item Type</c> is an implement keyword like Staff/Wand/Rod).
    /// Mirrors the equipped-loot filter in
    /// <c>Dnd4eExporter.IsWeaponOrImplementBase</c> minus the Weapon case.
    /// </summary>
    private static bool IsImplementTypedBase(RulesElement baseElem)
    {
        var type = baseElem.Type ?? string.Empty;
        if (string.Equals(type, "Implement", StringComparison.OrdinalIgnoreCase)
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
            if (baseElem.Fields.TryGetValue("Magic Item Type", out var mit) && !string.IsNullOrWhiteSpace(mit))
            {
                return ImplementMagicItemTypes.Contains(mit.Trim());
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the loot's base RulesElement is a baseless Magic
    /// Item that IS a weapon. The strict case is <c>Magic Item Type =
    /// "Weapon"</c>; we also admit <c>"Staff"</c> because per the PHB
    /// every staff doubles as a Quarterstaff (Staff of Corrosion +4 etc.
    /// emit weapon blocks under Melee Basic Attack in OCB output).
    /// </summary>
    private static bool IsWeaponMagicItem(RulesElement baseElem)
    {
        if (!string.Equals(baseElem.Type, "Magic Item", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!baseElem.Fields.TryGetValue("Magic Item Type", out var mit) || mit is null)
            return false;
        var t = mit.Trim();
        return string.Equals(t, "Weapon", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Staff", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the power's <c>Requirement</c> field is a
    /// "wielding ..." clause whose tail is a multi-clause prose
    /// expression that OCB's <c>IsRestricted</c> tokenizer cannot
    /// satisfy via its weapon-name substring fallback (every token
    /// would need a <c>WeaponSwap</c> stat-driven exception which we
    /// don't model). Concretely: a single token (no comma split)
    /// that contains an action verb / connective phrase ("to make",
    /// "with this", "instead of", " and ", " or " followed by another
    /// "weapon") which guarantees no weapon-name substring match. The
    /// exemplar is <c>Bonds of Moonlight</c> ("a light thrown weapon
    /// or a heavy thrown weapon to make a melee attack with this
    /// power.") — OCB emits zero weapons because the giant token
    /// fails every comparison and no swap rule applies.
    /// </summary>
    private static bool IsVerboseUnsatisfiableRequirement(RulesElement power)
    {
        if (!power.Fields.TryGetValue("Requirement", out var req) || string.IsNullOrWhiteSpace(req))
            return false;
        int idx = req.IndexOf("wielding", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var tail = req.Substring(idx + "wielding".Length);
        // Stop at first sentence terminator (matches OCB's per-comma
        // tokenization with trailing-period strip).
        int dot = tail.IndexOf('.');
        if (dot >= 0) tail = tail.Substring(0, dot);
        // OCB tokenizes by comma. If there are commas, individual
        // tokens are short and the field-164 substring match is
        // viable per token — leave it alone.
        if (tail.IndexOf(',') >= 0) return false;
        var t = tail.Trim().ToLowerInvariant();
        // Phrases that signal a verbose "wielding X to Y" clause that
        // collapses into one token. None of these can match a weapon
        // name as a substring; OCB's IsRestricted falls through to the
        // field-164 substring check at -Module-.cs:21701, which fails
        // on every weapon, and OCB then consults WeaponSwap (a
        // stat-driven exception lookup we don't model). For powers
        // shipped in the official rules (ID_FMP_* / ID_INTERNAL_*),
        // OCB has built-in WeaponSwap stats / character features that
        // succeed (e.g. Blurring Offensive PHB3 shares the "to make a
        // melee attack with this power" prose with Bonds of Moonlight
        // but emits weapons in source). For community-only powers
        // (ID_TIV_*, ID_DRAG427_*, ID_DRAGON411_*, ID_HF_*, ID_LFR_*,
        // etc. — content shipped via CBLoader part-files that OCB has
        // no built-in swap rules for), the substring fallback fails
        // with no rescue and OCB emits zero weapons. Mirror that
        // behavior here: only prune when both the verbose-prose
        // condition AND the non-official-prefix condition hold.
        bool isVerbose = t.Contains("to make ")
            || t.Contains("with this ")
            || t.Contains("instead of");
        if (!isVerbose) return false;
        if (string.IsNullOrEmpty(power.InternalId)) return false;
        if (power.InternalId.StartsWith("ID_FMP_", StringComparison.Ordinal)) return false;
        if (power.InternalId.StartsWith("ID_INTERNAL_", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// Conservative port of OCB's <c>IsRestricted</c> per-token dispatch
    /// (-Module-.cs:21472). Only handles tokens whose predicate is an
    /// INTRINSIC structural test against the weapon (no
    /// <c>WeaponSwap</c> stat-driven rescue path possible). Returns
    /// true if at least one such intrinsic token is present in the
    /// Requirement AND none allow this weapon (mirroring OCB's loop
    /// semantics: any token returning <c>false</c> = allow; reach end
    /// of loop = restricted).
    ///
    /// "Intrinsic" means the predicate would never be rescued by
    /// OCB's <c>WeaponSwap</c> dictionary — the token tests a property
    /// the weapon either has or doesn't, with no per-character /
    /// per-feat exception. Currently:
    /// <list type="bullet">
    /// <item><description><c>dagger</c> → OCB <c>IsDagger</c>
    ///   (-Module-.cs:21219): NormalizedName == "Dagger".</description></item>
    /// <item><description><c>thrown weapon</c> → OCB DEFAULT branch
    ///   (-Module-.cs:21694): <c>Properties</c> field substring contains
    ///   "thrown".</description></item>
    /// <item><description><c>light thrown weapon</c>,
    ///   <c>heavy thrown weapon</c> → DEFAULT branch with the
    ///   GLOBAL-156/159 prefix.</description></item>
    /// <item><description><c>two-handed weapon</c> →
    ///   <c>IsTwoHandedWeapon</c>: <c>Hands Required</c>
    ///   == "Two-Handed".</description></item>
    /// <item><description><c>two-handed melee weapon</c>,
    ///   <c>melee weapon in two hands</c> → two-handed AND
    ///   melee.</description></item>
    /// <item><description><c>two-handed reach weapon</c> → two-handed
    ///   AND <c>Properties</c> contains "reach".</description></item>
    /// </list>
    ///
    /// Group-token / weapon-name tokens (e.g. <c>light blade</c>,
    /// <c>axe</c>, <c>crossbow</c>, <c>monk weapon</c>,
    /// <c>two melee weapons</c>) are intentionally NOT gated here
    /// because OCB's <c>WeaponSwap</c> (-Module-.cs:21357) routinely
    /// rescues those via per-character / per-feat stat-text dictionaries
    /// we don't model (e.g. Rogue's "Light Blade Mastery" treats Maces
    /// as light blades for Sneak Attack and rogue powers, so Mace
    /// emissions under "wielding a light blade" Riposte Strike etc.
    /// are valid in source).
    /// </summary>
    private static bool IsRestrictedBySafeRequirementToken(
        RulesElement power,
        RulesElement baseElem,
        RulesElement? weaponEquiv,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        if (!power.Fields.TryGetValue("Requirement", out var req) || string.IsNullOrWhiteSpace(req))
            return false;
        int idx = req.IndexOf("wielding", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var tail = req.Substring(idx + "wielding".Length);
        int dot = tail.IndexOf('.');
        if (dot >= 0) tail = tail.Substring(0, dot);

        // Pre-fetch fields we'll consult for multiple tokens. Prefer
        // the base's field; fall back to the WeaponEquiv (Mindwarp
        // staff → Quarterstaff, etc.) when the base is empty.
        string properties = GetWeaponField(baseElem, weaponEquiv, snapshot, "Properties");
        string weaponGroups = GetWeaponField(baseElem, weaponEquiv, snapshot, "Group");
        string handsRequired = GetWeaponField(baseElem, weaponEquiv, snapshot, "Hands Required");
        string fullText = GetWeaponField(baseElem, weaponEquiv, snapshot, "Full Text");
        bool isTwoHanded = string.Equals(handsRequired.Trim(), "Two-Handed", StringComparison.OrdinalIgnoreCase);
        bool isMelee = fullText.IndexOf("melee weapon", StringComparison.OrdinalIgnoreCase) >= 0;
        bool baseIsDagger = string.Equals(baseElem.Name, "Dagger", StringComparison.OrdinalIgnoreCase)
            || (weaponEquiv is not null && string.Equals(weaponEquiv.Name, "Dagger", StringComparison.OrdinalIgnoreCase));

        bool sawIntrinsic = false;
        bool anyIntrinsicMatched = false;
        bool sawDeferredToken = false;

        foreach (var rawToken in tail.Split(','))
        {
            // Mirror OCB's per-token strip (-Module-.cs:21513-21520):
            // skip whitespace, strip leading "or ", then leading
            // "a "/"an ".
            var token = rawToken.Trim();
            if (token.StartsWith("or ", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(3).TrimStart();
            if (token.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(2).TrimStart();
            else if (token.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(3).TrimStart();
            if (token.Length == 0) continue;

            switch (token.ToLowerInvariant())
            {
                case "dagger":
                    sawIntrinsic = true;
                    if (baseIsDagger) anyIntrinsicMatched = true;
                    break;
                case "thrown weapon":
                    sawIntrinsic = true;
                    if (properties.IndexOf("thrown", StringComparison.OrdinalIgnoreCase) >= 0)
                        anyIntrinsicMatched = true;
                    else if (TryAssociationRescue(power, baseElem, weaponEquiv, snapshot, "thrown"))
                        anyIntrinsicMatched = true;
                    break;
                case "light thrown weapon":
                    sawIntrinsic = true;
                    if (properties.IndexOf("light thrown", StringComparison.OrdinalIgnoreCase) >= 0)
                        anyIntrinsicMatched = true;
                    else if (TryAssociationRescue(power, baseElem, weaponEquiv, snapshot, "light thrown"))
                        anyIntrinsicMatched = true;
                    break;
                case "heavy thrown weapon":
                    sawIntrinsic = true;
                    if (properties.IndexOf("heavy thrown", StringComparison.OrdinalIgnoreCase) >= 0)
                        anyIntrinsicMatched = true;
                    else if (TryAssociationRescue(power, baseElem, weaponEquiv, snapshot, "heavy thrown"))
                        anyIntrinsicMatched = true;
                    break;
                case "thrown weapon if you make a ranged attack":
                    // This official XML typo is a single comma-token in OCB.
                    // It never reaches the exact "thrown weapon" branch, so
                    // the default substring/WeaponSwap path restricts every
                    // candidate. Form of the First Hunter Attack is the only
                    // shipped occurrence in the merged rules.
                    sawIntrinsic = true;
                    break;
                case "bow or a crossbow":
                case "bow or crossbow":
                    sawIntrinsic = true;
                    if (GroupListContains(weaponGroups, "Bow")
                        || GroupListContains(weaponGroups, "Crossbow"))
                    {
                        anyIntrinsicMatched = true;
                    }
                    break;
                case "two-handed weapon":
                    sawIntrinsic = true;
                    if (isTwoHanded) anyIntrinsicMatched = true;
                    break;
                case "two-handed melee weapon":
                case "melee weapon in two hands":
                    sawIntrinsic = true;
                    if (isTwoHanded && isMelee) anyIntrinsicMatched = true;
                    break;
                case "two-handed reach weapon":
                    sawIntrinsic = true;
                    if (isTwoHanded
                        && properties.IndexOf("reach", StringComparison.OrdinalIgnoreCase) >= 0)
                        anyIntrinsicMatched = true;
                    break;
                default:
                    // EXCEPTION: tokens containing off-hand-state
                    // phrases ("have a hand free", "in your off hand")
                    // are NEVER rescued by WeaponSwap (no character
                    // stat names them), so OCB structurally restricts
                    // them. Empirical: Stab and Grab, Garrote Grip,
                    // Dance of Knives, Sweeping Lure all emit zero
                    // weapons across the 549-file corpus.
                    if (token.IndexOf("hand free", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sawIntrinsic = true; // anyIntrinsicMatched stays false
                        break;
                    }

                    // Standard 4e weapon group tokens ("light blade",
                    // "mace", "hammer", etc.): OCB's DEFAULT branch
                    // (-Module-.cs:21694-21711) checks the weapon's
                    // Group field for verbatim equality, then consults
                    // the WeaponSwap stat dictionary
                    // (-Module-.cs:21357) keyed by
                    // <PowerOwningClassId>-<RequestedGroup>. Per-feat /
                    // per-class-feature TextStringDirectives populate
                    // that dictionary (e.g. Street Thug emits
                    // ID_FMP_CLASS_6-Light Blade = "Mace group" so any
                    // Mace-group weapon counts as a light blade for
                    // Rogue powers; Ruthless Ruffian emits the same
                    // key with values "Mace" and "Club" — the literal
                    // names — so only those specific weapons are
                    // rescued, not other Mace-group weapons).
                    if (GroupTokenToTitleCase.TryGetValue(token, out var requestedGroup))
                    {
                        sawIntrinsic = true;
                        if (WeaponSatisfiesGroupToken(
                                power, baseElem, weaponEquiv,
                                requestedGroup, snapshot))
                        {
                            anyIntrinsicMatched = true;
                        }
                        break;
                    }

                    // Anything else (composite phrases like "two melee
                    // weapons", "monk weapon", weapon-name aliases like
                    // "shuriken") is left as a deferred token: we don't
                    // know how to evaluate it structurally, so it can't
                    // be the cause of a structural restriction here.
                    sawDeferredToken = true;
                    break;
            }
        }

        // OCB end-of-loop = return true (restricted). We only commit
        // to that verdict when (a) at least one intrinsic token was
        // present, (b) none of the intrinsic tokens matched, AND (c)
        // there are no deferred tokens that might have rescued via
        // WeaponSwap. A Requirement with mixed intrinsic + deferred
        // tokens (e.g. Snap Shot: "crossbow, a light thrown weapon,
        // or a sling") falls through to "not restricted" because the
        // deferred crossbow/sling tokens could allow the weapon.
        return sawIntrinsic && !anyIntrinsicMatched && !sawDeferredToken;
    }

    private static string GetWeaponField(
        RulesElement baseElem,
        RulesElement? weaponEquiv,
        Engine.Creation.CharacterSnapshot snapshot,
        string fieldName)
    {
        var v = snapshot.Builder.Overlay.GetField(baseElem, fieldName);
        if (string.IsNullOrEmpty(v) && weaponEquiv is not null)
            v = snapshot.Builder.Overlay.GetField(weaponEquiv, fieldName);
        return v ?? string.Empty;
    }

    /// <summary>
    /// True when the power's Requirement field unambiguously demands the
    /// wielder hold MULTIPLE weapons simultaneously: "wielding two ...",
    /// "wielding both ... and ...", or "wielding multiple ...". Catches
    /// Throw and Stab, Quick Throw, Whirling Rend, Two-Wolf Pounce,
    /// Howling Strike (NO — see below), etc.
    ///
    /// Two important EXCLUSIONS:
    ///   * "wielding a melee weapon in two hands" / "in both hands" —
    ///     a SINGLE two-handed weapon, not dual-wielding. Howling
    ///     Strike, Sundering Blow, Cleaving Brawler all match this.
    ///   * Any <c>or</c> clause — a single alternative might satisfy the
    ///     requirement, so this broad pre-gate cannot decide. The common
    ///     OCB exact token <c>two melee weapons or a ranged weapon</c>
    ///     is evaluated per candidate in <see cref="IsValidPowerCombo"/>.
    /// Single-weapon-type requirements ("wielding a light blade") are
    /// intentionally NOT matched here — those get gated by
    /// IsRestrictedBySafeRequirementToken downstream.
    /// </summary>
    private static bool RequiresMultipleWieldedWeapons(RulesElement power)
    {
        if (!power.Fields.TryGetValue("Requirement", out var req)
            || string.IsNullOrWhiteSpace(req))
            return false;
        int idx = req.IndexOf("wielding", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var tail = req.Substring(idx + "wielding".Length);
        int dot = tail.IndexOf('.');
        if (dot >= 0) tail = tail.Substring(0, dot);
        var lower = " " + tail.ToLowerInvariant() + " ";

        // EXCLUSION 1: "in two hands" / "in both hands" — singular
        // weapon held two-handed, not multi-weapon.
        if (lower.Contains(" in two hands ", StringComparison.Ordinal)
            || lower.Contains(" in both hands ", StringComparison.Ordinal))
            return false;

        // EXCLUSION 2: any " or " in the wielding tail. Some alternatives
        // allow a single weapon, so the broad pre-gate cannot safely prune.
        // Exact OCB branches that need per-candidate type information are
        // handled downstream in IsValidPowerCombo.
        if (lower.Contains(" or ", StringComparison.Ordinal))
            return false;

        return lower.Contains(" two ", StringComparison.Ordinal)
            || lower.Contains(" both ", StringComparison.Ordinal)
            || lower.Contains(" multiple ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detect a Ki Focus magic-item enchantment (the synthesized loot
    /// pairs from <c>Dnd4eExporter.BuildKiFocusPairs</c> attach the Ki
    /// Focus magic item as the loot's <see cref="LootItem.Enchantment"/>).
    /// Mirrors the enchant-side detection used in
    /// <c>Dnd4eExporter.IsKiFocusElement</c> (the Magic Item Type =
    /// "Ki Focus" branch). Returns false for the implement BASE itself
    /// (the Gear-typed "Ki Focus") since that's never an enchantment.
    /// </summary>
    private static bool IsKiFocusEnchantment(RulesElement? enchant)
    {
        if (enchant is null) return false;
        if (string.Equals(enchant.Name, "Ki Focus", StringComparison.OrdinalIgnoreCase))
            return true;
        return enchant.Fields.TryGetValue("Magic Item Type", out var mit)
            && string.Equals(mit?.Trim(), "Ki Focus", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirrors OCB's IsDualWielding character-level gate (D20Workspace.cs:3649)
    /// restricted to the equipped-weapon set. Currently unused — the
    /// dual-wielding flag is precomputed once in <see cref="Build"/> alongside
    /// the wielded-loot derivation. Retained as a reference for the OCB
    /// algorithm; delete if no UI/diagnostic ever consumes it.
    /// </summary>
    private static bool HasMultiWieldingState(
        IReadOnlyList<LootItem> equippedWeapons,
        IReadOnlySet<string>? equippedCompositeKeys,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        if (equippedCompositeKeys is null) return true;
        int equippedCount = 0;
        foreach (var loot in equippedWeapons)
        {
            if (!equippedCompositeKeys.Contains(loot.CompositeKey)) continue;
            equippedCount++;
            if (equippedCount >= 2) return true;
            if (IsDoubleWeapon(loot, snapshot)) return true;
        }
        return false;
    }

    /// <summary>
    /// Port of OCB's double-weapon callers plus IsDoubleWeapon:
    ///   * <c>IsDualWielding</c> (D20Workspace.cs:3649) maps staff
    ///     implements/magic staffs to Quarterstaff before calling
    ///     <c>IsDoubleWeapon</c>, so Staff Fighting's Quarterstaff
    ///     <c>_Secondary End</c> modify applies to Staff of Ruin, superior
    ///     staff implements, etc.
    ///   * <c>IsDoubleWeapon</c> itself (D20Workspace.cs:3814) then checks:
    ///   1. Properties contains "Double" AND Hands Required = "Two-Handed"
    ///      (native double weapons: Mordenkrad, Double sword, Double axe).
    ///   2. _Secondary End field is non-empty (an internal pointer to the
    ///      synthesized off-hand-end weapon).
    /// We additionally honour _SecondaryEnd (no space) because Spiked
    /// Chain Training (ID_FMP_FEAT_1252) ships its ModifyDirective with
    /// the no-space spelling — both spellings appear in the merged XML.
    /// All field reads go through the ModifyOverlay so feat directives
    /// (Spiked Chain Training adds "Stout, Off-hand" to Properties and
    /// _SecondaryEnd to Spiked chain) take effect.
    /// </summary>
    private static bool IsDoubleWeapon(
        LootItem loot,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        var baseElem = ResolveWeaponEquiv(loot, snapshot) ?? loot.Base;
        if (baseElem is null) return false;

        string properties = snapshot.Builder.Overlay.GetField(baseElem, "Properties") ?? string.Empty;
        string handsRequired = snapshot.Builder.Overlay.GetField(baseElem, "Hands Required") ?? string.Empty;
        bool isTwoHandedDouble =
            string.Equals(handsRequired.Trim(), "Two-Handed", StringComparison.OrdinalIgnoreCase)
            && properties.IndexOf("Double", StringComparison.OrdinalIgnoreCase) >= 0;
        if (isTwoHandedDouble) return true;

        string secEndSpaced = snapshot.Builder.Overlay.GetField(baseElem, "_Secondary End") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(secEndSpaced)) return true;

        string secEndCompact = snapshot.Builder.Overlay.GetField(baseElem, "_SecondaryEnd") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(secEndCompact);
    }

    /// <summary>
    /// Standard 4e weapon-group tokens that may appear in a "wielding a X"
    /// Requirement, mapped from lowercase Requirement form to the TitleCase
    /// form used in weapon Group fields and in WeaponSwap dictionary keys
    /// (<c>ID_FMP_CLASS_&lt;id&gt;-&lt;Group&gt;</c>). Mirrors the GLOBAL
    /// string ids dispatched in OCB's IsRestricted (-Module-.cs:21472-21715).
    /// </summary>
    private static readonly Dictionary<string, string> GroupTokenToTitleCase
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ["light blade"] = "Light Blade",
            ["heavy blade"] = "Heavy Blade",
            ["mace"] = "Mace",
            ["hammer"] = "Hammer",
            ["axe"] = "Axe",
            ["bow"] = "Bow",
            ["crossbow"] = "Crossbow",
            ["spear"] = "Spear",
            ["pick"] = "Pick",
            ["sling"] = "Sling",
            ["polearm"] = "Polearm",
            ["staff"] = "Staff",
            ["flail"] = "Flail",
        };

    /// <summary>
    /// Decide whether a weapon satisfies a "wielding a &lt;group&gt;"
    /// requirement, mirroring OCB's IsRestricted DEFAULT branch
    /// (-Module-.cs:21694-21711). Two-stage check:
    /// <list type="number">
    /// <item><description>Structural: weapon's <c>Group</c> field
    ///   (comma-separated) contains the requested group.</description></item>
    /// <item><description>WeaponSwap rescue: scan active elements for any
    ///   <c>TextStringDirective</c> named
    ///   <c>ID_FMP_CLASS_&lt;PowerOwningClassId&gt;-&lt;Group&gt;</c>. The
    ///   directive's value names a substitute. Two value formats:
    ///   <list type="bullet">
    ///   <item><description>"<c>X group</c>" (with " group" suffix) →
    ///     match against weapon's Group field.</description></item>
    ///   <item><description>"<c>X</c>" (bare) → match against weapon's
    ///     Name (or its <c>WeaponEquiv</c> resolution's name).</description></item>
    ///   </list>
    /// </description></item>
    /// </list>
    /// Examples:
    /// <list type="bullet">
    /// <item><description><b>Street Thug</b> emits
    ///   <c>ID_FMP_CLASS_6-Light Blade = "Mace group"</c>: Subtle
    ///   Morningstar (Group = Mace) is rescued under Rogue light-blade
    ///   powers.</description></item>
    /// <item><description><b>Ruthless Ruffian (Hybrid)</b> emits the
    ///   same key with values "Mace" and "Club" (literal weapon names):
    ///   only weapons literally named Mace or Club are rescued, not other
    ///   Mace-group weapons like Morningstar.</description></item>
    /// <item><description><b>Piercing Palm</b> emits
    ///   <c>ID_FMP_CLASS_6-Light Blade = "Monk Unarmed Strike"</c>: the
    ///   Monk Unarmed Strike weapon is rescued under Rogue light-blade
    ///   powers.</description></item>
    /// </list>
    /// </summary>
    private static bool WeaponSatisfiesGroupToken(
        RulesElement power,
        RulesElement baseElem,
        RulesElement? weaponEquiv,
        string requestedGroup,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        // Stage 1: structural Group field match.
        string weaponGroups = GetWeaponField(baseElem, weaponEquiv, snapshot, "Group");
        if (GroupListContains(weaponGroups, requestedGroup))
            return true;

        // Stage 1b: case-insensitive substring match over the whole Group
        // field. OCB's IsRestricted default branch (-Module-.cs:21701-21705)
        // uses `stristr(weaponGroupField, token)` — a raw substring search —
        // not a tokenised exact match. The only practical effect in shipped
        // 4e data is that "wielding a bow" is satisfied by a Crossbow
        // (Group="Crossbow" contains "Bow" as a substring). No other 4e
        // weapon group has a substring collision with another group token,
        // so this single rescue is the entire behavioural difference.
        if (!string.IsNullOrEmpty(weaponGroups)
            && weaponGroups.IndexOf(requestedGroup, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        // Stage 2: WeaponSwap stat dictionary rescue. Lookup key uses the
        // power's owning class id (its Class field), e.g. ID_FMP_CLASS_6
        // for Rogue powers — matches the dictionary key emitted by
        // Street Thug / Ruthless Ruffian / Piercing Palm even when the
        // character is a Hybrid Rogue.
        if (!power.Fields.TryGetValue("Class", out var classId)
            || string.IsNullOrWhiteSpace(classId))
        {
            return false;
        }
        string lookupKey = $"{classId.Trim()}-{requestedGroup}";

        string baseName = baseElem.Name ?? string.Empty;
        string equivName = weaponEquiv?.Name ?? string.Empty;
        // Magic-item bases with Magic Item Type = "Staff" function as a
        // magic quarterstaff (Staff Implement Description: "A staff
        // implement can also function as a magic quarterstaff"). When
        // checking name-based WeaponSwap rescues (e.g. Sneaky Staff:
        // "ID_FMP_CLASS_6-Light Blade = Quarterstaff") we have to compare
        // the implied weapon name, not the magic item's display name.
        string impliedWeaponName = string.Empty;
        if (string.Equals(baseElem.Type, "Magic Item", StringComparison.OrdinalIgnoreCase)
            && baseElem.Fields.TryGetValue("Magic Item Type", out var mit)
            && string.Equals(mit?.Trim(), "Staff", StringComparison.OrdinalIgnoreCase))
        {
            impliedWeaponName = "Quarterstaff";
        }
        const string GroupSuffix = " group";

        foreach (var elem in snapshot.ElementTree.GetActiveElements())
        {
            if (elem.Rules is null) continue;
            foreach (var directive in elem.Rules)
            {
                if (directive is not TextStringDirective ts) continue;
                if (!string.Equals(ts.Name, lookupKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = (ts.Value ?? string.Empty).Trim();
                if (value.Length == 0) continue;

                if (value.EndsWith(GroupSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    string groupName = value
                        .Substring(0, value.Length - GroupSuffix.Length)
                        .Trim();
                    if (GroupListContains(weaponGroups, groupName))
                        return true;
                }
                else
                {
                    if (string.Equals(value, baseName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (equivName.Length > 0
                        && string.Equals(value, equivName, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (impliedWeaponName.Length > 0
                        && string.Equals(value, impliedWeaponName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        // Stage 3: per-power association rescue. Some feats (e.g.
        // Harrowing Swarm Stalker, ID_FMP_FEAT_2338) emit a textstring
        // keyed by "<feat-id> association-<requested-group>" instead of
        // the class-keyed WeaponSwap dictionary, restricted to the
        // power's _Associated Feats list. Mirrors OCB's per-power
        // " association" InsertList (-Module-.cs:25777).
        if (TryAssociationRescue(power, baseElem, weaponEquiv, snapshot, requestedGroup))
            return true;

        return false;
    }

    /// <summary>
    /// Comma-separated Group list contains the named group (case-insensitive,
    /// whitespace-trimmed)?
    /// </summary>
    private static bool GroupListContains(string commaSeparatedGroups, string group)
    {
        if (string.IsNullOrEmpty(commaSeparatedGroups) || string.IsNullOrEmpty(group))
            return false;
        foreach (var raw in commaSeparatedGroups.Split(','))
        {
            if (string.Equals(raw.Trim(), group, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Per-power association rescue: when a power has a
    /// <c>_Associated Feats</c> field listing one or more feat ids, each
    /// such feat may emit a TextStringDirective keyed by
    /// <c>"&lt;feat-id&gt; association-&lt;suffix&gt;"</c> whose value is
    /// a "+"-separated list of weapon-group tokens (e.g. "crossbow group"
    /// or "bow group"). When the requested suffix matches such a key on
    /// any active feat in the power's associated list AND the wielded
    /// weapon's <c>Group</c> field contains one of those group names, the
    /// requirement is rescued.
    ///
    /// Empirical examples in the rules data:
    /// <list type="bullet">
    /// <item><description>Harrowing Swarm Scout (ID_FMP_FEAT_2337) emits
    ///   <c>"ID_FMP_FEAT_2337 association-thrown" = "crossbow group+bow
    ///   group"</c>. Rescues Quick Throw / Upending Throw / Surprising
    ///   Throw / Ricochet Throw / Skewering Shot for crossbow + bow
    ///   wielders even though "thrown weapon" requirement would
    ///   normally only match Properties containing "thrown".</description></item>
    /// <item><description>Harrowing Swarm Stalker (ID_FMP_FEAT_2338) emits
    ///   <c>"ID_FMP_FEAT_2338 association-Light Blade" = "crossbow
    ///   group"</c>. Rescues Setup Strike / Dismaying Slash /
    ///   Unbalancing Attack / Audacious Strike / Felling Gash /
    ///   Skirmishing Strike for crossbow wielders under "wielding a
    ///   light blade" requirements.</description></item>
    /// </list>
    ///
    /// Mirrors OCB's per-power " association" InsertList in
    /// <c>-Module-.cs:25777</c> which prefixes each id in the power's
    /// <c>_Associated Feats</c> with the literal " association" before
    /// stat-text lookup.
    /// </summary>
    private static bool TryAssociationRescue(
        RulesElement power,
        RulesElement baseElem,
        RulesElement? weaponEquiv,
        Engine.Creation.CharacterSnapshot snapshot,
        string keySuffix)
    {
        if (!power.Fields.TryGetValue("_Associated Feats", out var feats)
            || string.IsNullOrWhiteSpace(feats))
            return false;
        if (string.IsNullOrEmpty(keySuffix)) return false;

        var textStrings = snapshot.Builder.ElementTree.TextStrings;
        if (textStrings.Count == 0) return false;

        string weaponGroups = GetWeaponField(baseElem, weaponEquiv, snapshot, "Group");
        if (string.IsNullOrEmpty(weaponGroups)) return false;

        const string GroupSuffix = " group";

        foreach (var rawFeat in feats.Split(','))
        {
            var featId = rawFeat.Trim();
            if (featId.Length == 0) continue;
            string key = featId + " association-" + keySuffix;
            if (!textStrings.TryGetValue(key, out var value)
                || string.IsNullOrWhiteSpace(value))
                continue;
            // Value is one or more weapon-group tokens joined with '+'.
            // Each token is "<group> group" (e.g. "crossbow group");
            // strip the trailing " group" suffix and group-list-contains
            // against the weapon's Group field.
            foreach (var rawPart in value.Split('+'))
            {
                var part = rawPart.Trim();
                if (part.EndsWith(GroupSuffix, StringComparison.OrdinalIgnoreCase))
                    part = part.Substring(0, part.Length - GroupSuffix.Length).TrimEnd();
                if (part.Length == 0) continue;
                if (GroupListContains(weaponGroups, part))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the loot's enchantment is a Magic Item whose
    /// <c>Magic Item Type</c> is "Weapon" — meaning the (base, enchant)
    /// pair functions as a weapon for weapon-keyword powers even when
    /// the base is e.g. a Superior Implement (Lancing dagger + Lightning
    /// Weapon, Mindwarp staff + Bloodthirst Weapon, etc.).
    /// </summary>
    private static bool IsWeaponEnchanted(RulesElement? enchant)
    {
        if (enchant is null) return false;
        return enchant.Fields.TryGetValue("Magic Item Type", out var mit)
            && string.Equals(mit?.Trim(), "Weapon", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve a loot composite's <c>WeaponEquiv</c> to the underlying
    /// weapon RulesElement (e.g. Mindwarp staff → Quarterstaff,
    /// Ironscar Rod → Mace). Mirrors
    /// OCB's <c>WeaponEquiv(loot)</c> at <c>-Module-.cs:18728</c> which
    /// allows a Superior Implement / Implement base to count as a weapon
    /// for weapon-keyword power dispatch (Melee Basic Attack, Howling
    /// Strike, Mighty Hew, etc.). Returns null when no <c>WeaponEquiv</c>
    /// field is present or when the referenced element can't be resolved
    /// from the rules database.
    ///
    /// Also handles two implicit equivalences not represented as XML
    /// fields, mirroring OCB's <c>IsStaff(elem)</c> predicate at
    /// <c>D20Workspace.cs:3779</c>: in OCB <c>ImplementWeaponEquiv</c>
    /// (-Module-.cs:21957) returns Quarterstaff (<c>ID_FMP_WEAPON_10</c>)
    /// for any base where IsStaff returns true. IsStaff matches on:
    /// <list type="bullet">
    /// <item><description>Gear named "Staff Implement"
    ///   (<c>db[41]</c> branch, name match).</description></item>
    /// <item><description>Magic Item with <c>Magic Item Type =
    ///   "Staff"</c> (<c>db[44]</c> branch, field match).</description></item>
    /// <item><description>Weapon with <c>Group = "Staff"</c> (i.e. an
    ///   actual Quarterstaff — already handled by structural Group
    ///   check, no synthesis needed).</description></item>
    /// <item><description>Superior Implement with <c>Group = "Staff"</c>
    ///   (Mindwarp / Guardian / Accurate / Quickbeam staff — these
    ///   already have explicit <c>WeaponEquiv = ID_FMP_WEAPON_10</c>
    ///   so they resolve via the field above without needing the
    ///   implicit fallback).</description></item>
    /// </list>
    /// </summary>
    private static RulesElement? ResolveWeaponEquiv(
        LootItem loot,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        var resolved =
            ResolveExplicitWeaponEquiv(loot.Base, snapshot)
            ?? ResolveExplicitWeaponEquiv(loot.Enchantment, snapshot)
            ?? ResolveExplicitWeaponEquiv(loot.Augment, snapshot);
        if (resolved is not null) return resolved;

        // Implicit "functions as a magic quarterstaff" rule mirroring
        // OCB's IsStaff(base) → ImplementWeaponEquiv → Quarterstaff
        // dispatch (-Module-.cs:21957). Covers Staff Implement Gear,
        // Magic Item Type=Staff, Quarterstaff weapons, and Superior
        // Implement Group=Staff. There is intentionally no analogous
        // rule for other implement types — OCB only branches on
        // IsStaff in ImplementWeaponEquiv.
        if (IsStaff(loot.Base))
        {
            var resolver = snapshot.Builder.ElementTree.ElementResolver;
            return resolver?.Invoke("ID_FMP_WEAPON_10", "Weapon");
        }

        return null;
    }

    private static RulesElement? ResolveExplicitWeaponEquiv(
        RulesElement? element,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        if (element is null) return null;
        if (!element.Fields.TryGetValue("WeaponEquiv", out var equivId)
            || string.IsNullOrWhiteSpace(equivId))
        {
            return null;
        }

        var resolver = snapshot.Builder.ElementTree.ElementResolver;
        if (resolver is null) return null;

        string token = equivId.Trim();
        string targetType = "Weapon";
        int colon = token.IndexOf(':');
        if (colon > 0)
        {
            targetType = NormalizeElementType(token[(colon + 1)..].Trim());
            token = token[..colon].Trim();
        }

        return resolver(token, targetType)
            ?? ResolveTitleCasedToken(token, targetType, resolver);
    }

    private static RulesElement? ResolveTitleCasedToken(
        string token,
        string targetType,
        Func<string, string, RulesElement?> resolver)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!token.Any(char.IsLower)) return null;

        string title = System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(token.ToLowerInvariant());
        if (string.Equals(title, token, StringComparison.Ordinal))
            return null;

        return resolver(title, targetType);
    }

    private static string NormalizeElementType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Weapon";
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(type.ToLowerInvariant());
    }

    /// <summary>
    /// True when the base item is a <b>staff</b> for purposes of OCB's
    /// <c>ImplementWeaponEquiv</c> rule (which redirects any "staff"
    /// implement to Quarterstaff for weapon-keyword power dispatch).
    /// Mirrors OCB's <c>IsStaff(RulesElement*)</c> predicate at
    /// <c>D20Workspace.cs:3779</c>, which has four branches keyed by the
    /// element type:
    /// <list type="bullet">
    /// <item><description><b>Gear</b> (<c>db[41]</c>) named "Staff
    ///   Implement" (the basic staff implement).</description></item>
    /// <item><description><b>Magic Item</b> (<c>db[44]</c>) with
    ///   <c>Magic Item Type = "Staff"</c>.</description></item>
    /// <item><description><b>Weapon</b> (<c>db[42]</c>) with
    ///   <c>Group = "Staff"</c> (i.e. the Quarterstaff weapon
    ///   itself).</description></item>
    /// <item><description><b>Superior Implement</b> (<c>db[43]</c>) with
    ///   <c>Group = "Staff"</c> (Mindwarp / Guardian / Accurate /
    ///   Quickbeam staff). These already carry an explicit
    ///   <c>WeaponEquiv = ID_FMP_WEAPON_10</c> field, so they normally
    ///   resolve via <see cref="ResolveWeaponEquiv"/>'s field path
    ///   without needing this fallback — but mirroring OCB's branch
    ///   structurally guards against errata or houseruled superior
    ///   staffs that omit the field.</description></item>
    /// </list>
    /// Implements (Holy Symbol, Ki Focus, Orb, Rod, Tome, Totem, Wand)
    /// have no analogous weapon-equivalent rule in OCB —
    /// <c>ImplementWeaponEquiv</c> only branches on <c>IsStaff</c>, so
    /// this is the only Is-predicate the export gate needs to mirror.
    /// </summary>
    private static bool IsStaff(RulesElement baseElem)
    {
        // db[41] = Gear: name match against "Staff Implement".
        if (string.Equals(baseElem.Type, "Gear", StringComparison.OrdinalIgnoreCase)
            && string.Equals(baseElem.Name, "Staff Implement", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // db[44] = Magic Item: field "Magic Item Type" == "Staff".
        if (string.Equals(baseElem.Type, "Magic Item", StringComparison.OrdinalIgnoreCase)
            && baseElem.Fields.TryGetValue("Magic Item Type", out var mit)
            && string.Equals(mit?.Trim(), "Staff", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // db[42] = Weapon: Group contains "Staff" (i.e. an actual
        // quarterstaff / staff-group weapon). Recognising it here is
        // mostly defensive — the structural Group check on the gating
        // side already accepts staff-group weapons under staff-token
        // requirements, so this branch rarely fires through
        // ImplementWeaponEquiv. Mirrored from OCB for completeness.
        if (string.Equals(baseElem.Type, "Weapon", StringComparison.OrdinalIgnoreCase)
            && baseElem.Fields.TryGetValue("Group", out var weapGroup)
            && GroupListContains(weapGroup, "Staff"))
        {
            return true;
        }

        // db[43] = Superior Implement: Group == "Staff" (Mindwarp,
        // Guardian, Accurate, Quickbeam). Defence-in-depth — these
        // also carry an explicit WeaponEquiv field.
        if (string.Equals(baseElem.Type, "Superior Implement", StringComparison.OrdinalIgnoreCase)
            && baseElem.Fields.TryGetValue("Group", out var siGroup)
            && GroupListContains(siGroup, "Staff"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the named field carries a structured "Ability vs.
    /// Defense" attack-roll directive (e.g. <c>Wisdom vs. Reflex</c>) rather
    /// than a freeform prose description (e.g. Fox's Cunning whose Attack
    /// reads "You can shift 1 square..."). Used to discriminate reactive /
    /// zone-parent powers that carry no roll from those that do.
    /// </summary>
    private static bool HasStructuredAttackField(RulesElement power, string fieldName)
    {
        string? v = null;
        if (!power.Fields.TryGetValue(fieldName, out v) || string.IsNullOrWhiteSpace(v))
        {
            // Some secondary-attack powers store their attack line under a
            // leading-space key (`" Attack"`) rather than `Secondary Attack`.
            // Do not teach the general attack parser to consume these wholesale:
            // that wakes many secondary/repeat rows OCB doesn't render. For
            // this local skip gate, only the healing-primary shape needs this
            // signal (Miraculous Intervention: primary healing Effect,
            // secondary implement Attack). Non-healing reactive secondary
            // powers such as Startling Offensive remain effect-only here; OCB's
            // PowerStats returns null for those rows in the corpus.
            if (!PowerFieldParser.IsHealingPower(power))
                return false;
            foreach (var (key, value) in power.Fields)
            {
                if (!string.Equals(key.Trim(), fieldName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                v = value;
                break;
            }
            if (string.IsNullOrWhiteSpace(v))
                return false;
        }
        // Structured attack rolls take the shape "<expr> vs. <defense>"
        // where <expr> may include arithmetic ("Charisma + 2 vs. Will"
        // for Vampire's Domineering Gaze, "Strength + 2 vs. AC" for
        // Brutal Strike, etc.). Match any " vs. <word>" pattern; prose
        // attack descriptions don't naturally contain that token.
        return StructuredAttackRegex().IsMatch(v);
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

    private static readonly HashSet<string> ImplementMagicItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
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

    private static bool MatchesClassList(RulesElement re, Engine.CharacterModel.CharacterElementTree tree)
    {
        if (!re.Fields.TryGetValue("Class", out var classList) || string.IsNullOrWhiteSpace(classList))
            return false;
        foreach (var raw in classList.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            // Match by name or by internal id (OCB checks both).
            if (tree.HasElement(token)) return true;
        }
        return false;
    }

    private static bool MatchesPowerList(RulesElement re, RulesElement power)
    {
        if (!re.Fields.TryGetValue("Power", out var powerList) || string.IsNullOrWhiteSpace(powerList))
            return false;
        foreach (var raw in powerList.Split(','))
        {
            var token = raw.Trim().TrimEnd('.');
            if (token.Length == 0) continue;
            if (string.Equals(token, power.Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(token, power.InternalId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsVersatileUsedTwoHanded(
        RulesElement weapon,
        Engine.Creation.CharacterSnapshot snapshot,
        IReadOnlyDictionary<string, string>? textStrings)
    {
        if (textStrings is null
            || !textStrings.TryGetValue("_INTERNAL_VersatileUsedTwoHanded", out string? raw)
            || !int.TryParse(raw.Trim(), out int enabled)
            || enabled == 0)
        {
            return false;
        }

        if (!WeaponHasProperty(weapon, "Versatile"))
            return false;

        return !SmallWithNonSmallWeapon(weapon, snapshot);
    }

    private static bool SmallWithNonSmallWeapon(
        RulesElement weapon,
        Engine.Creation.CharacterSnapshot snapshot)
    {
        if (!snapshot.Builder.ElementTree.HasElement("Small"))
            return false;

        if (snapshot.Builder.ElementTree.HasElement("Oversized")
            || snapshot.Builder.ElementTree.HasElement("One Size Larger"))
        {
            return false;
        }

        return !WeaponHasProperty(weapon, "Small");
    }

    private static bool WeaponHasProperty(RulesElement weapon, string property)
    {
        string? properties = GetMergedWeaponFieldPreferBase(weapon, "Properties");
        return properties is not null
            && properties.Contains(property, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetMergedWeaponFieldPreferBase(RulesElement weapon, string fieldName)
    {
        if (weapon.Fields.TryGetValue("_Base " + fieldName, out string? baseValue)
            && !string.IsNullOrWhiteSpace(baseValue))
        {
            return baseValue;
        }

        return weapon.Fields.TryGetValue(fieldName, out string? value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    private static PowerStatWeapon BuildWeaponBlock(
        RulesElement power,
        LootItem? weaponLoot,
        PowerStatsBuildContext ctx,
        int? dualImplementOtherEnhancementBonus = null,
        bool displayZeroUnselectedModeVariant = false)
    {
        var snapshot = ctx.Snapshot;
        var stats = ctx.Stats;
        var overlay = ctx.Overlay;
        var characterLevel = ctx.CharacterLevel;
        var sourceNameResolver = ctx.SourceNameResolver;
        var healingSourceNameResolver = ctx.HealingSourceNameResolver;
        var sourceElementResolver = ctx.SourceElementResolver;
        var textStrings = ctx.TextStrings;
        var precomputedBeastAttackBonus = ctx.PrecomputedBeastAttackBonus;
        // Merge fields from Base + Enchantment (+ Augment) so the calculator
        // sees the full picture: Base contributes Damage / Proficiency Bonus /
        // Group / Properties; Enchantment contributes Enhancement; Augment
        // can contribute either. Without this merge we miss the +N
        // enhancement bonus on every magic weapon. Enchantment fields
        // override Base on collision (matches OCB layering).
        var weaponEl = MergeWeaponFields(weaponLoot, snapshot, sourceElementResolver, overlay);
        if (weaponEl is not null && IsVersatileUsedTwoHanded(weaponEl, snapshot, textStrings))
        {
            var fields = new Dictionary<string, string>(weaponEl.Fields, StringComparer.OrdinalIgnoreCase)
            {
                ["_Versatile Used Two-Handed"] = "1"
            };
            weaponEl = RulesElementProjection.WithFields(weaponEl, fields, [.. weaponEl.Categories]);
        }
        if (weaponEl is not null && dualImplementOtherEnhancementBonus is { } otherEnhancement && otherEnhancement != 0)
        {
            var fields = new Dictionary<string, string>(weaponEl.Fields, StringComparer.OrdinalIgnoreCase)
            {
                ["_Dual Implement Spellcaster Other Enhancement"] = otherEnhancement.ToString()
            };
            weaponEl = RulesElementProjection.WithFields(weaponEl, fields, [.. weaponEl.Categories]);
        }
        string? ResolveSourceName(string sourceId)
        {
            if (weaponLoot is not null)
            {
                if (string.Equals(sourceId, weaponLoot.Base.InternalId, StringComparison.OrdinalIgnoreCase))
                    return weaponLoot.Base.Name;
                if (weaponLoot.Enchantment is not null
                    && string.Equals(sourceId, weaponLoot.Enchantment.InternalId, StringComparison.OrdinalIgnoreCase))
                    return weaponLoot.Enchantment.Name;
                if (weaponLoot.Augment is not null
                    && string.Equals(sourceId, weaponLoot.Augment.InternalId, StringComparison.OrdinalIgnoreCase))
                    return weaponLoot.Augment.Name;
            }

            return sourceNameResolver(sourceId);
        }

        string? ResolveHealingSourceName(string sourceId)
            => healingSourceNameResolver(sourceId) ?? ResolveSourceName(sourceId);

        var calc = PowerStatCalculator.Calculate(
            power,
            stats,
            weaponEl,
            characterLevel,
            ResolveSourceName,
            sourceElementResolver,
            ResolveHealingSourceName,
            displayZeroUnselectedModeVariant,
            overlay,
            precomputedBeastAttackBonus);

        // Inner RulesElement components: base weapon + magic-item enchantment
        // (and augment) layers, in OCB order. None for the synthetic Unarmed
        // entry — OCB also omits inner refs for it.
        var components = new List<TallyElement>();
        if (weaponLoot is not null)
        {
            void Add(RulesElement re) =>
                components.Add(new TallyElement(re.InternalId, re.Name, re.Type));
            Add(weaponLoot.Base);
            if (weaponLoot.Enchantment is not null) Add(weaponLoot.Enchantment);
            if (weaponLoot.Augment is not null) Add(weaponLoot.Augment);
        }

        string weaponName = weaponLoot is null
            ? "Unarmed"
            : LootNaming.Compose(weaponLoot);

        return new PowerStatWeapon
        {
            Name = weaponName,
            AttackBonus = calc.AttackBonus,
            Damage = calc.DamageExpression,
            DamageType = calc.DamageType,
            AttackStat = calc.ResolvedAttackStat,
            Defense = calc.Defense ?? string.Empty,
            Healing = calc.Healing,
            HitComponents = calc.AttackComponents,
            DamageComponents = calc.DamageComponents,
            HealingComponents = calc.HealingComponents,
            Conditions = calc.Conditions,
            Components = components,
        };
    }

    /// <summary>
    /// Synthesise a single RulesElement carrying the combined fields of
    /// Base + Enchantment (+ Augment) from a LootItem. Returns null when
    /// loot is null (synthetic Unarmed case). Identity is taken from Base
    /// (name, internal id, type) so callers checking IsWeaponPower etc.
    /// see the original weapon. Fields from later layers override earlier
    /// ones on collision.
    /// </summary>
    private static RulesElement? MergeWeaponFields(
        LootItem? loot,
        Engine.Creation.CharacterSnapshot snapshot,
        Func<string, RulesElement?>? sourceElementResolver,
        Engine.Evaluation.ModifyOverlay overlay)
    {
        // For the synthetic Unarmed slot (loot is null), look up the canonical
        // Unarmed attack weapon from the rules DB so the active overlay can
        // apply any ModifyDirective updates to its Damage / Group / etc. fields
        // (Improved Monk Unarmed Strike, racial natural-weapon overrides, etc.).
        if (loot is null)
        {
            var unarmed = sourceElementResolver?.Invoke("ID_FMP_WEAPON_34");
            if (unarmed is null) return null;
            loot = new LootItem { Base = unarmed };
        }
        var baseElement = ResolveActiveLayer(loot.Base);
        var baseFields = CreateEffectiveFields(baseElement);
        var weaponEquiv = ResolveWeaponEquiv(loot, snapshot);
        var equivElement = weaponEquiv is null ? null : ResolveActiveLayer(weaponEquiv);
        var equivFields = equivElement is null ? null : CreateEffectiveFields(equivElement);
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        OverlayFields(equivFields);
        OverlayFields(baseFields);
        if (equivElement is not null)
        {
            merged["_WeaponEquiv Name"] = equivElement.Name;
            merged["_WeaponEquiv InternalId"] = equivElement.InternalId;
        }
        if (!string.IsNullOrWhiteSpace(loot.CompositeName))
            merged["_Composite Name"] = loot.CompositeName;
        if (!string.IsNullOrWhiteSpace(loot.DamageOverride))
        {
            merged["Damage"] = loot.DamageOverride;
            merged["_Loot Damage Override"] = "1";
        }
        if (merged.TryGetValue("_SupportsID", out string? supportsId)
            && !string.IsNullOrWhiteSpace(supportsId))
        {
            if (sourceElementResolver?.Invoke(supportsId) is { } supportedWeapon)
                merged["_Supports Name"] = supportedWeapon.Name;
            else if (TryGetLargeSupportedWeaponName(baseElement.Name, out string supportedName))
                merged["_Supports Name"] = supportedName;
        }
        if (loot.Enchantment is not null)
        {
            merged["_Enchantment Name"] = loot.Enchantment.Name;
            merged["_Enchantment InternalId"] = loot.Enchantment.InternalId;
        }
        var augmentForLimiter = loot.Augment ?? ResolveAugmentFromXml(loot.AugmentXml, sourceElementResolver);
        if (augmentForLimiter is not null)
        {
            merged["_Augment Name"] = augmentForLimiter.Name;
            merged["_Augment InternalId"] = augmentForLimiter.InternalId;
        }
        PreserveBaseField("Item Slot");
        PreserveBaseField("Hands Required");
        PreserveBaseField("Properties");

        void PreserveBaseField(string fieldName)
        {
            string? value = FirstNonBlankField(baseFields, fieldName)
                ?? FirstNonBlankField(equivFields, fieldName);
            if (!string.IsNullOrWhiteSpace(value))
                merged["_Base " + fieldName] = value;
        }

        static string? FirstNonBlankField(IReadOnlyDictionary<string, string>? fields, string fieldName)
            => fields is not null
                && fields.TryGetValue(fieldName, out string? value)
                && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : null;

        static bool TryGetLargeSupportedWeaponName(string name, out string supportedName)
        {
            const string largeSuffix = " (Large)";
            if (name.EndsWith(largeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                supportedName = name[..^largeSuffix.Length];
                return true;
            }

            supportedName = string.Empty;
            return false;
        }

        void OverlayFields(IReadOnlyDictionary<string, string>? fields)
        {
            if (fields is null) return;
            foreach (var (k, v) in fields)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                merged[k] = v;
            }
        }

        Dictionary<string, string> CreateEffectiveFields(RulesElement element)
        {
            var fields = new Dictionary<string, string>(element.Fields, StringComparer.OrdinalIgnoreCase);
            foreach (var (field, value) in overlay.GetModificationsFor(element.InternalId))
                fields[field] = value;
            return fields;
        }

        RulesElement ResolveActiveLayer(RulesElement layer)
            => !string.IsNullOrWhiteSpace(layer.InternalId)
                ? sourceElementResolver?.Invoke(layer.InternalId) ?? layer
                : layer;

        void Overlay(RulesElement? layer)
        {
            if (layer is null) return;
            OverlayFields(CreateEffectiveFields(ResolveActiveLayer(layer)));
        }
        Overlay(loot.Enchantment);
        Overlay(loot.Augment);
        return RulesElementProjection.WithFields(loot.Base, merged);

        static RulesElement? ResolveAugmentFromXml(
            string? augmentXml,
            Func<string, RulesElement?>? sourceElementResolver)
        {
            if (string.IsNullOrWhiteSpace(augmentXml) || sourceElementResolver is null)
                return null;

            var root = System.Xml.Linq.XElement.Parse(augmentXml);
            string? baseId = root.Element("baseID")?.Value;
            return string.IsNullOrWhiteSpace(baseId)
                ? null
                : sourceElementResolver(baseId);
        }
    }

    /// <summary>
    /// (Removed) <c>PickApplicableWeapons</c> + <c>IsMeleeBasicAttack</c> +
    /// <c>IsRangedBasicAttack</c> were heuristic predates of the OCB-faithful
    /// gate now implemented in <see cref="IsValidPowerCombo"/>. The Melee /
    /// Ranged Basic Attack universal-basic special cases are subsumed by
    /// <see cref="PowerFieldParser.IsMeleePower"/> / <c>IsRangedPower</c>
    /// reading the power's <c>Attack Type</c> field directly (MBA's
    /// "Strength vs. AC, Melee weapon" is detected by IsMeleePower; RBA
    /// similarly).
    /// </summary>

    // The fixed set of <specific> names OCB emits per <Power>, in canonical
    // order. OCB only ever emits these two -- previous longer list flooded
    // every power export with seven extras per power.
    private static readonly string[] PowerSpecificFields =
    {
        "Power Usage",
        "Action Type",
    };

    [GeneratedRegex(@"\d+\s*\[\s*W\s*\]|\d*d\d+")]
    private static partial Regex HitDiceRegex();

    [GeneratedRegex(@"\bvs\.\s+\w", RegexOptions.IgnoreCase)]
    private static partial Regex StructuredAttackRegex();
}
