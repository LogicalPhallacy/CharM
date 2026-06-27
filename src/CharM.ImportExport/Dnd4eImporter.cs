using CharM.Engine.CharacterModel;
using CharM.Engine.Creation;
using CharM.Engine.Orchestration;
using CharM.Engine.Rules;
using CharM.RulesDb.Storage;
using CharM.Serialization;
using CharacterSnapshot = CharM.Serialization.CharacterSnapshot;

namespace CharM.ImportExport;

/// <summary>
/// Loads a parsed .dnd4e snapshot into a <see cref="CharacterSession"/> by
/// positional alignment of the file's <c>&lt;Level&gt;</c> tree against the
/// wizard's slot list.
///
/// The .dnd4e Level subtree is a structured choice journal: each
/// <c>&lt;RulesElement&gt;</c> position corresponds either to an auto-grant
/// produced by the parent's directives or to a user-pick consuming a select
/// directive's slot. By walking the file's tree in document order and matching
/// each child against the next pending slot under its tree-parent, we end up
/// with the wizard in the exact state OCB describes — including empty
/// placeholders that mark deliberately-unfilled slots.
/// </summary>
public static partial class Dnd4eImporter
{
    public sealed record ImportResult(
        CharacterSession Session,
        CharacterSnapshot Snapshot,
        IReadOnlyList<string> UnresolvedElements);

    /// <summary>Read a .dnd4e file from <paramref name="stream"/> and produce a populated session.</summary>
    public static ImportResult Import(
        Stream stream,
        IRulesDatabase database,
        string? sourceFilter = null)
    {
        var snapshot = Dnd4eReader.Read(stream);
        return Import(snapshot, database, sourceFilter);
    }

    /// <summary>Build a session from an already-parsed snapshot.</summary>
    public static ImportResult Import(
        CharacterSnapshot snapshot,
        IRulesDatabase database,
        string? sourceFilter = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(database);

        // Pre-warm the element cache: the importer performs thousands of cold
        // FindByInternalId / FindBy* lookups during alignment and replay. Without
        // preload each miss serializes on the DB lock + JSON deserialize. Preload
        // is idempotent, so repeated imports only pay the single table scan once.
        database.Preload();
        ImportPerfTrace.Mark("session-ctor");

        int level = snapshot.Level > 0
            ? snapshot.Level
            : (snapshot.LevelTrees.Count > 0
                ? snapshot.LevelTrees.Max(l => l.Level)
                : (snapshot.BuildChoices.Count > 0 ? snapshot.BuildChoices.Keys.Max() : 1));

        var session = new CharacterSession(
            database.FindByInternalId,
            database.FindByNameAndType,
            (type, includeRules) => database.FindByType(type, includeRules),
            (type, source, includeRules) => database.FindByTypeAndSource(type, source, includeRules),
            level,
            // Construct with auto-fill already OFF so InitializeLevel (run in the
            // ctor) does not do candidate filtering to silently occupy slots — the
            // importer overlays the source's explicit picks via AlignChildren and
            // disables auto-fill anyway. This also avoids ~200ms of wasted
            // per-level autofill work on high-level characters.
            autoFillSelectDefaults: false);
        ImportPerfTrace.Mark("copy");

        // Importing an already-built character: the source XML is the
        // authoritative record of every user pick. Disable the wizard's
        // SelectDirective.Default auto-fill (a UX preselect convenience)
        // so it doesn't silently occupy slots before AlignChildren can
        // place the source's explicit pick — which would otherwise drop
        // the user's choice (e.g. Healing Infusion: Resistive Formula).
        // Forced engine grants (SelectDirective.Grant) remain honored.
        session.AutoFillSelectDefaults = false;

        // Suppress FREEBEE auto-grants during replay. Class-feature FREEBEEs
        // (Wizard's Spellbook gear, etc.) would otherwise add their target
        // item to inventory while the source loot is still empty, then the
        // RestoreEquipment pass below adds the same item again from the
        // source XML and we end up with duplicates. After RestoreEquipment
        // populates inventory we lift suppression and ProcessFreebees
        // dedupes against the now-present source items.
        session.SuppressFreebees = true;

        // Suppress level-tree dirty marking during the import replay. The
        // pick-replay loop below calls MakeChoice / AddReplacement many times,
        // and each call would otherwise invalidate the captured tree we just
        // copied onto the session. Cleared at end of import so genuine UI
        // mutations dirty levels normally.
        session.SuppressLevelTreeDirty = true;

        if (sourceFilter is not null)
            session.SourceFilter = sourceFilter;

        // Use the source's verbatim name even when it's whitespace-only or
        // empty — many community files emit <name>  </name> with literal
        // spaces, and our default "New Character" would otherwise stomp that
        // on round-trip. Only fall back to the default when the source had no
        // <name> element at all (snapshot.Name == null).
        if (snapshot.Name is not null)
            session.Name = snapshot.Name;

        foreach (var (key, value) in snapshot.Details)
        {
            if (!string.IsNullOrWhiteSpace(value))
                session.Details[key] = value;
        }

        foreach (var (key, value) in snapshot.TextStrings)
        {
            session.TextStrings[key] = value;
        }

        foreach (var (id, meta) in snapshot.SourceMetadata)
        {
            session.SourceMetadata[id] = meta;
        }
        foreach (var tally in snapshot.ElementTally)
        {
            if (!string.IsNullOrEmpty(tally.InternalId))
                session.SourceFlatTallyIds.Add(tally.InternalId);
        }

        foreach (var (name, element) in snapshot.RawSections)
        {
            session.RawSections[name] = element;
        }

        ImportPerfTrace.Mark("clone-trees");
        foreach (var levelTree in snapshot.LevelTrees)
        {
            if (levelTree.Root.SourceElement is { } src)
                session.CapturedLevelTrees[levelTree.Level] = new System.Xml.Linq.XElement(src);

            if (levelTree.SourceLoot.Count == 0) continue;
            session.SourceLevelLoot[levelTree.Level] = levelTree.SourceLoot
                .Select(loot => new System.Xml.Linq.XElement(loot))
                .ToList();
        }

        // Houserule overlay (Forms A/B/C; Form D rides RawSections).
        session.IsCharacterHouseruled = snapshot.Houserules.IsCharacterHouseruled;
        foreach (var (lvl, ueList) in snapshot.Houserules.LevelUserEdits)
            session.HouseruleLevelUserEdits[lvl] = new List<System.Xml.Linq.XElement>(ueList);
        session.HouseruleFormATallyMirror.AddRange(snapshot.Houserules.FormATallyMirror);
        session.HouseruleLegacyTallyRows.AddRange(snapshot.Houserules.LegacyTallyRows);
        foreach (var iid in snapshot.Houserules.HouseruledElementIds)
            session.HouseruledElementIds.Add(iid);

        ImportPerfTrace.Mark("scores");
        if (snapshot.BaseAbilityScores.Count > 0)
        {
            var scores = new AbilityScoreSet
            {
                [Ability.Strength] = snapshot.BaseAbilityScores.GetValueOrDefault("Strength", 10),
                [Ability.Constitution] = snapshot.BaseAbilityScores.GetValueOrDefault("Constitution", 10),
                [Ability.Dexterity] = snapshot.BaseAbilityScores.GetValueOrDefault("Dexterity", 10),
                [Ability.Intelligence] = snapshot.BaseAbilityScores.GetValueOrDefault("Intelligence", 10),
                [Ability.Wisdom] = snapshot.BaseAbilityScores.GetValueOrDefault("Wisdom", 10),
                [Ability.Charisma] = snapshot.BaseAbilityScores.GetValueOrDefault("Charisma", 10),
            };
            session.SetAbilityScores(scores);
        }
        ImportPerfTrace.Mark("maps");

        var unresolved = new List<string>();
        var deferredPicks = new List<DeferredPick>();

        // Pre-pass: build a charelem -> (InternalId, Level) map by walking
        // every level tree. Used to resolve `replaces=` retraining swaps below.
        var charelemMap = new Dictionary<string, (string InternalId, int Level)>(StringComparer.OrdinalIgnoreCase);
        foreach (var lt in snapshot.LevelTrees)
            CollectCharelems(lt.Root, lt.Level, charelemMap);

        // Authoritative per-element acquisition level: the flat
        // <RulesElementTally> walks elements in document order with
        // <Level N> markers between them. ParseRulesElementTally now
        // stamps each TallyElement with its AcquisitionLevel; we
        // mirror that into a charelem-keyed map for AlignChildren.
        // Without this, swap rows nested deep in the L1 captured subtree
        // (Hybrid Vampire's Improved Blood Drinker chain) get recorded
        // at L1 instead of L13/L17/L23.
        var tallyAcquisitionLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var te in snapshot.ElementTally)
        {
            if (!string.IsNullOrEmpty(te.Charelem) && te.AcquisitionLevel is int lvl)
                tallyAcquisitionLevels[te.Charelem!] = lvl;
        }

        // Multiclass-utility-swap detection: if the source emits the OLD
        // element as its own standalone tally row alongside a swap, OCB
        // intentionally keeps both so the user can retrain back. We mark
        // those swaps with PreserveOld=true so CharacterBuilder records the
        // dropped element for re-emission in PowerStats / tally.
        var preservedSwapTargets = CollectPreservedSwapTargets(snapshot.LevelTrees, charelemMap);

        // Build tally-driven sets used by AlignChildren to gate `replaces=`
        // processing. OCB's `<RulesElementTally>` is the authoritative flat
        // list of active elements; the per-`<Level>` blocks duplicate each
        // element at its structural placement, which means a `replaces=` row
        // can appear nested deep inside a class-feature subtree as a
        // structural REFLECTION of a real top-level retrain (legitimate),
        // or as an intermediate link in a chain (legitimate — Artificer
        // Smokepowder->Lightning Sigil->Hellfire Sigil, where only the
        // final link is in tally), or as the only copy of a phantom retrain
        // OCB itself never applied (noise — Aislin (2)'s nested Foolhardy
        // Fighting where Dishearten survives in tally).
        //
        // Distinguishing rule (verified against the corpus):
        //   - swap is in tally           => apply
        //   - swap NOT in tally:
        //       OLD standalone in tally  => skip (OCB preserved OLD)
        //       OLD NOT in tally         => apply (chain intermediate link)
        //
        // tallyReplaces: HashSet of (NewInternalId, OldCharelem) — every
        //   swap row in flat `<RulesElementTally>`.
        // tallyStandaloneCharelems: charelems of NON-replaced tally rows.
        //   When OLD's charelem is in this set, OCB confirmed OLD survived
        //   the tally pass — so any nested swap targeting it is noise.
        // tallySwapperInternalIds: InternalIds of NEWs in tally swap rows.
        //   Used for chain-PreserveOld: if OLD itself is the NEW of another
        //   tally swap row, OCB keeps the intermediate visible in PowerStats
        //   (Dishearten -> FF -> Dark Gathering preserves FF).
        var tallyReplaces = new HashSet<(string NewId, string OldCharelem)>();
        var tallyStandaloneCharelems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tallySwapperInternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var te in snapshot.ElementTally)
        {
            if (string.IsNullOrEmpty(te.Replaces))
            {
                // Standalone (non-replaced) tally row. We use the snapshot's
                // own charelem map: TallyElement currently doesn't carry
                // charelem, but the same element appears in a Level subtree
                // and the charelem map walks every Level subtree. We use
                // InternalId as the key and pull all matching charelems from
                // the inverse view of charelemMap.
                continue;
            }
            if (string.IsNullOrEmpty(te.InternalId)) continue;
            tallyReplaces.Add((te.InternalId!, te.Replaces!));
            tallySwapperInternalIds.Add(te.InternalId!);
        }
        // Build standalone-charelem set: every charelem in the level-tree map
        // whose owning element appears as a NON-replaced row in the tally.
        // (This is OCB's "survived all retrains" set.)
        var tallyStandaloneInternalIds = new HashSet<string>(
            snapshot.ElementTally
                .Where(te => string.IsNullOrEmpty(te.Replaces) && !string.IsNullOrEmpty(te.InternalId))
                .Select(te => te.InternalId!),
            StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in charelemMap)
        {
            if (tallyStandaloneInternalIds.Contains(kvp.Value.InternalId))
                tallyStandaloneCharelems.Add(kvp.Key);
        }

        var alignCtx = new ImportAlignmentContext(
            Session: session,
            Database: database,
            CharelemMap: charelemMap,
            PreservedSwapTargets: preservedSwapTargets,
            TallyReplaces: tallyReplaces,
            TallyStandaloneCharelems: tallyStandaloneCharelems,
            TallySwapperInternalIds: tallySwapperInternalIds,
            Unresolved: unresolved,
            DeferredPicks: deferredPicks,
            TallyAcquisitionLevels: tallyAcquisitionLevels);

        foreach (var levelTree in snapshot.LevelTrees.OrderBy(l => l.Level))
        {
            // Each Level root carries a known internal id (ID_INTERNAL_LEVEL_N).
            // Slots created by the Level element's own selects (Race, Class,
            // feats, powers at this level) have OwnerInternalId == that id.
            string levelOwner = levelTree.Root.InternalId
                ?? $"ID_INTERNAL_LEVEL_{levelTree.Level}";

            ImportPerfTrace.Mark("level-walk");
            AlignChildren(
                levelTree.Root,
                parentInternalId: levelOwner,
                currentLevel: levelTree.Level,
                alignCtx);
        }

        // Retry user-picks that couldn't find a slot during the initial walk.
        // Typically these are picks under a conditional grant whose Requires
        // only passes after a sibling subtree (e.g. the second hybrid class)
        // is processed. Loop until no further progress.
        ImportPerfTrace.Mark("deferred");
        ProcessDeferredPicks(
            alignCtx,
            dp => FindNextPendingSlot(alignCtx.Session, dp.ParentInternalId, dp.Element.Type));

        // Fallback pass: for any deferred picks still unplaced, drop the
        // strict owner constraint and match by ElementType alone. The OCB
        // file occasionally serializes picks under the wrong parent context
        // (e.g. wrapped in an empty placeholder), but the type+pending-order
        // is enough to land them in the right slot. Loop until stable.
        ProcessDeferredPicks(
            alignCtx,
            dp => FindAnyPendingSlotByType(alignCtx.Session, dp.Element.Type));

        // Deity / Domain picks have no required rules slot for non-clerics
        // (and Domain doesn't legitimately appear at all outside of a
        // Warpriest-style Trait Package grant). When the OCB file carries
        // them as freeform tally rows, route them through the existing
        // grabbag channel so they round-trip without polluting the wizard.
        // Picking a named Deity also fires its Domain GrantDirectives, so
        // a single Sehanine pick auto-cascades Love/Moon/Trickery.
        for (int i = deferredPicks.Count - 1; i >= 0; i--)
        {
            var dp = deferredPicks[i];
            if (IsFreeformPickType(dp.Element.Type))
            {
                session.AddGrabbagGrant(dp.Element);
                deferredPicks.RemoveAt(i);
            }
        }

        // Power-leaf fallback: a deferred Power that survived both the
        // strict-owner pass AND the type-only pass is almost always a real
        // user pick the engine doesn't model a slot for (Hybrid cross-class
        // daily/encounter/utility, retraining quirks, multi-pick wizards
        // that exceed our slot count). The OCB file lists it explicitly with
        // a charelem; just force-grant it via grabbag at its source level so
        // it round-trips into PowerStats and the tally. Skip if the same
        // InternalId is already present in the active tree (e.g. a sibling
        // grant chain already produced it) to avoid duplicates.
        // Limited to leaf Power for now -- container-ish types (Class
        // Feature, Theme, Background) have nested picks that need the
        // alignment recursion this fallback doesn't perform.
        //
        // Distinguish "real cross-slot pick" (powers OCB writes a power
        // card for) from "orphan tally vestige" (powers OCB lists in the
        // tally but excludes from PowerStats — typically the tally row that
        // immediately follows an empty <RulesElement name="" type=""/>
        // placeholder, indicating the user opted out of the slot). Use the
        // source's PowerStats <Power name="..."> set as ground truth: if
        // the source emitted a power card, we should too; otherwise treat
        // as a vestige and exclude from our rebuilt PowerStats.
        var sourcePowerNames = CollectSourcePowerStatsNames(session);
        for (int i = deferredPicks.Count - 1; i >= 0; i--)
        {
            var dp = deferredPicks[i];
            if (!string.Equals(dp.Element.Type, "Power", StringComparison.OrdinalIgnoreCase))
                continue;

            bool already = session.HasActiveElementOfType("Power", dp.Element.InternalId);
            if (already)
            {
                if (!string.IsNullOrWhiteSpace(dp.ParentInternalId))
                    session.AddSlotOwnedSupplement(dp.Element, atLevel: dp.Level, slotOwnerInternalId: dp.ParentInternalId);
                deferredPicks.RemoveAt(i);
                continue;
            }

            bool isVestige = sourcePowerNames.Count > 0
                && !string.IsNullOrEmpty(dp.Element.Name)
                && !sourcePowerNames.Contains(dp.Element.Name);

            if (!string.IsNullOrWhiteSpace(dp.ParentInternalId)
                && ParentHasSelectForType(dp.ParentInternalId, dp.Element.Type, database))
            {
                session.AddSlotOwnedSupplement(dp.Element, atLevel: dp.Level, slotOwnerInternalId: dp.ParentInternalId);
            }

            session.AddGrabbagGrant(dp.Element, atLevel: dp.Level, tallyVestige: isVestige);
            deferredPicks.RemoveAt(i);
        }

        // Class-Feature-leaf fallback: a deferred Class Feature whose owner
        // is a swap-NEW (e.g. Twofold Pact's Eldritch-Pact pick of Dark/Fey
        // Pact) never gets a slot opened, because the swap-NEW's
        // SelectDirective only fires later during ApplyReplacements in the
        // build phase — long after import finishes. The user's pick is in
        // the source's flat <RulesElementTally>, so promote it to a grabbag
        // grant so its own GrantDirectives (the pact powers) can fire.
        // Gate strictly on "InternalId is in the source flat tally as a
        // standalone (non-replaced) row" so we never invent picks the user
        // didn't make. Also skip if already present in the engine tree.
        for (int i = deferredPicks.Count - 1; i >= 0; i--)
        {
            var dp = deferredPicks[i];
            if (!string.Equals(dp.Element.Type, "Class Feature", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(dp.Element.InternalId)) continue;
            if (!tallyStandaloneInternalIds.Contains(dp.Element.InternalId)) continue;

            bool already = session.HasActiveElementOfType("Class Feature", dp.Element.InternalId);
            if (already)
            {
                deferredPicks.RemoveAt(i);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(dp.ParentInternalId))
                session.AddSlotOwnedSupplement(dp.Element, atLevel: dp.Level, slotOwnerInternalId: dp.ParentInternalId);
            else
                session.AddGrabbagGrant(dp.Element, atLevel: dp.Level);
            deferredPicks.RemoveAt(i);
        }

        // Deferred picks that never found a slot but ARE confirmed in the
        // source's flat <RulesElementTally>: force-grant them so the tally
        // round-trips. Common causes are slot owners that materialize
        // differently than expected (Hybrid Shaman / Druid sometimes don't
        // produce the Skill Training slot under ID_INTERNAL_CLASS_HYBRID
        // that OCB's saved file references) and Arena Training category
        // markers that gate weapon-group expertise statadds.
        //
        // We trust the source's flat tally as the authoritative "this
        // element is active" signal — if OCB wrote a row for it, the
        // element belongs in the character. The slotless graft via
        // <see cref="CharacterSession.AddGrabbagGrant"/> attaches it to the
        // level so its directives fire and its stats apply.
        for (int i = deferredPicks.Count - 1; i >= 0; i--)
        {
            var dp = deferredPicks[i];
            if (string.IsNullOrEmpty(dp.Element.InternalId)) continue;
            if (!tallyStandaloneInternalIds.Contains(dp.Element.InternalId)) continue;

            bool already = session.HasActiveElementOfType(dp.Element.Type, dp.Element.InternalId);
            if (!already)
                session.AddGrabbagGrant(dp.Element, atLevel: dp.Level);
            deferredPicks.RemoveAt(i);
        }

        foreach (var dp in deferredPicks)
            unresolved.Add($"Deferred pick (no slot ever appeared): {dp.Element.Type}::{dp.Element.Name} (id={dp.Element.InternalId}) under owner={dp.ParentInternalId}");

        ImportPerfTrace.Mark("finalize");
        RestoreEquipment(session, snapshot, database);
        ApplyGrabbagGrants(session, snapshot, database, unresolved);
        ApplyUserEditPicks(session, snapshot, database, unresolved);

        MarkLevelTreeOnlyElements(session, snapshot);

        // Source loot is now in place; lift FREEBEE suppression and run a
        // dedup-only pass. We mark every FREEBEE id as processed and stamp
        // sentinels for items the source already had, but we do NOT grant
        // items missing from the source — that would synthesize items the
        // OCB user intentionally declined (or never granted by not visiting
        // the AutoLevel/Gear/Rituals page). Fresh-wizard flows (web UI
        // character creation) keep the default grantMissing:true behavior.
        session.SuppressFreebees = false;
        session.RunPendingFreebees(grantMissing: false);
        session.SuppressLevelTreeDirty = false;

        return new ImportResult(session, snapshot, unresolved);
    }

    /// <summary>
    /// Mirror OCB's <c>SaveElementTally</c> behavior (-Module-.cs:11781):
    /// the flat <c>&lt;RulesElementTally&gt;</c> only contains elements that
    /// the source emitted in that flat list. Any element present elsewhere
    /// in the level structure but absent from the flat tally is structural
    /// nesting only: wizard spellbook powers, class-feature choice wrappers
    /// such as Arcane Implement Mastery under Hybrid Talent, and
    /// chain-intermediate retrain links left visible by an earlier swap.
    /// <para>
    /// We replicate that behavior structurally: any source-level element
    /// whose InternalId did NOT appear in the source's flat tally is marked
    /// level-tree-only, suppressing it from rebuilt
    /// <c>&lt;RulesElementTally&gt;</c> while leaving its
    /// <c>&lt;Level&gt;</c> nesting intact for round-trip fidelity. For
    /// powers, level-tree-only also suppresses rebuilt
    /// <c>&lt;PowerStats&gt;</c>.
    /// </para>
    /// <para>
    /// Skipped when the source had no flat tally (fresh-built or stub
    /// files) — the absence of a tally isn't evidence the powers are
    /// vestiges, and aggressively excluding them would erase the entire
    /// PowerStats section.
    /// </para>
    /// </summary>
    private static void MarkLevelTreeOnlyElements(CharacterSession session, CharacterSnapshot snapshot)
    {
        if (snapshot.ElementTally is null || snapshot.ElementTally.Count == 0) return;

        var sourceTallyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in snapshot.ElementTally)
        {
            if (!string.IsNullOrEmpty(t.InternalId))
                sourceTallyIds.Add(t.InternalId);
        }

        // Pass 1: scan SOURCE level tree (snapshot.LevelTrees). The engine
        // moves swap-targets out of its own tree into ReplacedElements
        // (e.g. Bramble Hide replaced by Thief's Getaway via Versatile
        // Master), so a pure engine-tree scan would miss them. Any
        // source-level node whose InternalId isn't in the source's flat
        // tally is structural-only — mark it.
        foreach (var lt in snapshot.LevelTrees)
            ScanImportedNodeForLevelTreeOnlyElements(lt.Root, sourceTallyIds, session);

        // Pass 2: scan the wizard's element tree for engine-synthesised
        // elements that the source never recorded anywhere (neither in the
        // flat tally nor in any Level subtree). Examples:
        //   - "Melf's Minute Meteors Secondary Power" — sub-power our
        //     materialiser creates from MMM's GrantDirective
        //   - "Level 1 Apprentice Mage" — Class Feature slot-shell granted
        //     by the "School of Magic Apprentice" feat that opens the
        //     Pyromancy/Cryomancy/etc. school select; OCB writes the chosen
        //     school but suppresses the empty wrapper CF
        //
        // We walk BOTH the wizard's tree AND the snapshot's CharacterBuilder
        // tree. The snapshot tree is only built when the character has
        // replacements (retraining swaps) — Melf's Minute Meteors's
        // Secondary Power grant only fires after CharacterBuilder applies
        // the swap, which happens during Build() rather than wizard
        // construction. Forcing snapshot for swap-bearing characters costs
        // ~150ms per affected file; the rest of the corpus stays on the
        // fast wizard-only path.
        foreach (var elementType in EnumerableElementTypes)
        {
            foreach (var re in session.GetAllElementsOfType(elementType))
            {
                if (string.IsNullOrEmpty(re.InternalId)) continue;
                if (sourceTallyIds.Contains(re.InternalId)) continue;
                session.MarkAsLevelNestedOnly(re.InternalId);
            }
        }

        bool hasReplacements = snapshot.LevelTrees.Any(lt =>
            ScanForReplaces(lt.Root));
        if (hasReplacements)
        {
            var built = session.GetSnapshot();
            if (built is not null)
            {
                foreach (var node in built.Builder.ElementTree.Root.GetAllDescendants())
                {
                    if (node.RulesElement is not { } re) continue;
                    if (string.IsNullOrEmpty(re.InternalId)) continue;
                    if (sourceTallyIds.Contains(re.InternalId)) continue;
                    if (!IsEnumerableTypeForLevelTreeOnly(re.Type)) continue;
                    session.MarkAsLevelNestedOnly(re.InternalId);
                }
            }
        }
    }

    private static bool ScanForReplaces(CharM.Serialization.ImportedRulesElement node)
    {
        if (!string.IsNullOrEmpty(node.Replaces)) return true;
        foreach (var child in node.Children)
            if (ScanForReplaces(child)) return true;
        return false;
    }

    /// <summary>
    /// Resolve the level at which a retraining swap should be recorded.
    /// The flat <c>&lt;RulesElementTally&gt;</c> has <c>&lt;Level N&gt;</c>
    /// markers between rows in document order, which is OCB's
    /// authoritative per-element acquisition journal.
    /// <see cref="Dnd4eReader.ParseRulesElementTally"/> stamps each row
    /// with its preceding marker as <c>AcquisitionLevel</c>; we mirror
    /// that into a charelem-keyed map and look up the swap row here.
    ///
    /// Without this, swaps nested deep in the L1 captured subtree
    /// (Hybrid Vampire's Improved Blood Drinker chain) all collapse to
    /// L1 because <c>AlignChildren</c> walks the L1 root with
    /// <c>currentLevel=1</c> and never updates as it descends. Falls
    /// back to <paramref name="currentLevel"/> defensively when the
    /// charelem isn't in the map (synthetic snapshots / pre-tally-stamp
    /// imports).
    /// </summary>
    private static int ResolveSwapLevel(
        CharM.Serialization.ImportedRulesElement child,
        int currentLevel,
        IReadOnlyDictionary<string, int>? tallyAcquisitionLevels)
    {
        if (tallyAcquisitionLevels is null) return currentLevel;
        if (string.IsNullOrEmpty(child.Charelem)) return currentLevel;
        return tallyAcquisitionLevels.TryGetValue(child.Charelem!, out var level)
            ? level
            : currentLevel;
    }

    /// <summary>
    /// Pair the captured swap rows (<c>replaces=</c> children) under
    /// <paramref name="parentNode"/> with the owner element's
    /// <see cref="ReplaceDirective"/>s in document/level order.
    /// <para>
    /// This is the authoritative signal for swap-acquisition level: a
    /// class feature granted at L1 (e.g.
    /// <c>Psionic Augmentation (Hybrid)</c>) can carry ReplaceDirectives
    /// with explicit <c>Level=13/17/23/27</c> attributes. The captured
    /// tree position puts every swap row under the L1 ancestor and the
    /// flat tally only stamps the LAST swap of each chain — the
    /// directive's <c>Level</c> is the only signal that survives the
    /// chain intermediate links.
    /// </para>
    /// <para>
    /// Pairing strategy: walk swap rows in document order, walk
    /// ReplaceDirectives in <c>Level</c> ascending order, zip them
    /// 1-to-1. Same-level swap-rows (rare, only seen on multi-multiclass
    /// Power Swap feats) collapse to the directive's level naturally.
    /// </para>
    /// <para>
    /// Returns a charelem-keyed map. Returns an empty dictionary when
    /// the owner is unresolvable or carries no ReplaceDirectives — the
    /// caller then falls back to <see cref="ResolveSwapLevel"/>'s
    /// tally-acquisition-level lookup.
    /// </para>
    /// </summary>
    private static Dictionary<string, int> TryResolveSwapLevelsFromDirectives(
        ImportedRulesElement parentNode,
        string? parentInternalId,
        IRulesDatabase database)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(parentInternalId)) return result;

        // Cheap guard: only owners whose children carry retraining swaps
        // (replaces= + charelem) can have swap levels to resolve. Skipping the
        // DB lookup + ReplaceDirective LINQ for the common non-swap node avoids
        // per-node work across the whole AlignChildren walk.
        bool hasSwapChild = false;
        foreach (var child in parentNode.Children)
        {
            if (!string.IsNullOrEmpty(child.Replaces) && !string.IsNullOrEmpty(child.Charelem))
            {
                hasSwapChild = true;
                break;
            }
        }
        if (!hasSwapChild) return result;

        var ownerElement = database.FindByInternalId(parentInternalId);
        if (ownerElement is null) return result;

        var directiveLevels = ownerElement.Rules
            .OfType<ReplaceDirective>()
            .Where(rd => rd.Level.HasValue && rd.Level.Value > 0)
            .Select(rd => rd.Level!.Value)
            .OrderBy(l => l)
            .ToList();
        if (directiveLevels.Count == 0) return result;

        int directiveIdx = 0;
        foreach (var child in parentNode.Children)
        {
            if (string.IsNullOrEmpty(child.Replaces)) continue;
            if (string.IsNullOrEmpty(child.Charelem)) continue;
            if (directiveIdx >= directiveLevels.Count) break;
            result[child.Charelem!] = directiveLevels[directiveIdx++];
        }
        return result;
    }

    private static bool IsEnumerableTypeForLevelTreeOnly(string type)
    {
        foreach (var t in EnumerableElementTypes)
            if (string.Equals(type, t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Element types whose engine-synthesised members should be checked
    /// against the source's flat tally and demoted to level-tree-only when
    /// missing. Keep this in sync with the most common slot-wrapper /
    /// cascade-child cases — adding a type that the source genuinely
    /// expects in tally would silently drop real rows on round-trip.
    /// </summary>
    private static readonly string[] EnumerableElementTypes =
    [
        "Power",
        "Class Feature",
    ];

    private static void ScanImportedNodeForLevelTreeOnlyElements(
        CharM.Serialization.ImportedRulesElement node,
        HashSet<string> sourceTallyIds,
        CharacterSession session)
    {
        if (!string.IsNullOrEmpty(node.InternalId)
            && !sourceTallyIds.Contains(node.InternalId))
        {
            session.MarkAsLevelNestedOnly(node.InternalId);
        }
        foreach (var child in node.Children)
            ScanImportedNodeForLevelTreeOnlyElements(child, sourceTallyIds, session);
    }

    /// <summary>
    /// Element types that resolve to a real RulesElement but have no required
    /// rules-engine slot: the OCB lets the user pick one freely as a tally
    /// row without an attached select directive. When such a pick can't be
    /// matched to a slot, treat it as a grabbag-style detached grant so it
    /// round-trips and any cascades (e.g. Sehanine -&gt; her three Domains)
    /// fire naturally via the existing grant chain.
    /// </summary>
    private static bool IsFreeformPickType(string? type) =>
        type is not null
        && (string.Equals(type, "Deity", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "Domain", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A user pick that couldn't be placed because the destination slot
    /// didn't exist yet (typically because the slot's owner is granted by a
    /// conditional directive whose Requires only passes after a sibling
    /// branch — e.g. Hybrid Vampire — is processed). Retried after the full
    /// level walk so the conditional grant has had a chance to fire.
    /// </summary>
    private sealed record DeferredPick(
        ImportedRulesElement Node,
        RulesElement Element,
        string? ParentInternalId,
        int Level);

    /// <summary>
    /// Apply force-grants from the source file's <c>&lt;Grabbag&gt;&lt;rules&gt;</c>
    /// block (Inherent Bonuses, Spellscarred, House Vadalis, etc.). These are
    /// OCB campaign-settings entries that live outside the normal Level subtree
    /// and need to be injected as detached root-level grants so the round-tripped
    /// tally matches the source.
    /// </summary>
    private static void ApplyGrabbagGrants(
        CharacterSession session,
        CharacterSnapshot snapshot,
        IRulesDatabase database,
        List<string> unresolved)
    {
        foreach (var grant in snapshot.GrabbagGrants)
        {
            if (string.IsNullOrEmpty(grant.InternalId)) continue;
            var element = database.FindByInternalId(grant.InternalId);
            if (element is null)
            {
                unresolved.Add($"Grabbag grant: {grant.InternalId} ({grant.Name})");
                continue;
            }
            session.AddCampaignSettingGrant(element);
        }
    }

    /// <summary>
    /// Collect the set of <c>&lt;Power name="..."&gt;</c> names emitted in
    /// the source's PowerStats section. Returns an empty set when the
    /// source had no PowerStats block (e.g. mid-creation files), in which
    /// case the caller should NOT use the set to gate vestige decisions —
    /// the absence of evidence isn't evidence of absence.
    /// </summary>
    private static HashSet<string> CollectSourcePowerStatsNames(CharacterSession session)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!session.RawSections.TryGetValue("PowerStats", out var raw) || raw is null)
            return names;
        foreach (var p in raw.Elements("Power"))
        {
            var n = p.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(n))
                names.Add(n);
        }
        return names;
    }

    /// <summary>
    /// Replay the source <c>&lt;LootTally&gt;</c> into the session: equipped
    /// items into their preferred slot, the rest into the inventory list.
    /// Mirrors <c>CharacterSessionService.RestoreImportedEquipment</c> so the
    /// CLI roundtrip + tests get the same loot fidelity as the web UI.
    /// </summary>
    private static void RestoreEquipment(
        CharacterSession session,
        CharacterSnapshot snapshot,
        IRulesDatabase database)
    {
        foreach (var entry in snapshot.Equipment.Where(e => e.EquipCount > 0))
        {
            var loot = BuildLootItem(entry, database, session);
            if (loot is null) continue;

            int remaining = Math.Max(1, entry.EquipCount);
            var preferredSlots = GetPreferredSlots(loot.Base, entry.IsInAlternateSlot);

            if (preferredSlots.Count == 0)
            {
                // No rules-data slot resolution — but the source insists
                // it's equipped. Synthesize a slot key from the base id so
                // equip-sensitive behavior (sort priority, offhand checks,
                // Ki Focus trigger) can still see equip-count > 0.
                var fallbackSlot = "_Unmapped:" + (loot.Base.InternalId ?? loot.Base.Name);
                while (remaining > 0 && session.GetEquippedLoot(fallbackSlot) is not null)
                    fallbackSlot += "+";
                if (remaining > 0)
                {
                    session.EquipItem(fallbackSlot, loot);
                    remaining--;
                }
                if (remaining > 0)
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

            // Source-of-truth fallback: if equip-count > 0 but every
            // preferred slot was already occupied (e.g. character
            // dual-wields TWO weapons that both prefer Main Hand/Off-Hand
            // and a THIRD weapon also marked equipped, OR multiple
            // alt-slot items competing for the same alt slot), park the
            // remainder under a synthesized slot key so the equipped
            // dictionary still reflects equip-count for equip-sensitive
            // behavior. Normal OCB weapon-candidate iteration is owned-loot
            // based, but sort/offhand/Ki-trigger logic still needs the
            // equipped signal.
            if (remaining > 0)
            {
                var fallbackSlot = "_Overflow:" + (loot.Base.InternalId ?? loot.Base.Name);
                while (session.GetEquippedLoot(fallbackSlot) is not null)
                    fallbackSlot += "+";
                session.EquipItem(fallbackSlot, loot);
                remaining--;
            }

            if (remaining > 0)
                session.AddInventoryItem(loot, remaining);
        }

        foreach (var entry in snapshot.Equipment.Where(e => e.EquipCount == 0))
        {
            var loot = BuildLootItem(entry, database, session);
            if (loot is null) continue;
            // Pass entry.Count verbatim — count="0" means "in reference
            // list but owns zero" and must round-trip as such (and must NOT
            // contribute statadds via inventory-directive processing).
            session.AddInventoryItem(loot, Math.Max(0, entry.Count));
        }
    }

    /// <summary>
    /// Classify a <see cref="LootEntry"/>'s ordered components into a composite
    /// <see cref="LootItem"/>: the first non-Magic-Item child is the Base
    /// (Weapon/Armor/Implement); subsequent Magic Items become Enchantment then
    /// Augment. If every child is a Magic Item (e.g. Cloak of Resistance), the
    /// first one is the Base.
    /// </summary>
    private static LootItem? BuildLootItem(LootEntry entry, IRulesDatabase database, CharacterSession? session = null)
    {
        if (entry.Components.Count == 0) return null;

        var resolved = new List<(LootComponent Comp, RulesElement El)>();
        foreach (var c in entry.Components)
        {
            var el = ResolveLootElement(c.Element, database, session);
            if (el is null) continue;
            resolved.Add((c, el));
        }
        if (resolved.Count == 0) return null;

        // Classify by type. Anything other than "Magic Item" is treated as a
        // physical base; Magic Items are enchant/augment in source order.
        var baseIdx = resolved.FindIndex(r => !string.Equals(r.El.Type, "Magic Item", StringComparison.OrdinalIgnoreCase));
        if (baseIdx < 0) baseIdx = 0; // all magic items — first is base

        var baseEntry = resolved[baseIdx];
        RulesElement? enchant = null;
        RulesElement? augment = null;
        foreach (var (i, r) in resolved.Select((r, i) => (i, r)))
        {
            if (i == baseIdx) continue;
            if (enchant is null) enchant = r.El;
            else if (augment is null) augment = r.El;
        }

        // Prefer the base component's WornCategoryId, but fall back to any
        // other component (enchantment, augment) that carries one. Most loot
        // pins the worn-state Category to the base, but a handful — Hexblade's
        // "Blade of Annihilation" Magic Item / Infernal Pact Weapon, with
        // <c>WearingPactBlade</c> nested under the enchantment instead of the
        // weapon base — only have it on a non-base component, and dropping
        // it leaves the requires-gated statadds inactive on round-trip.
        string? wornCategoryId = baseEntry.Comp.WornCategoryId;
        if (string.IsNullOrEmpty(wornCategoryId))
        {
            foreach (var rc in resolved)
            {
                if (rc.Comp == baseEntry.Comp) continue;
                if (!string.IsNullOrEmpty(rc.Comp.WornCategoryId))
                {
                    wornCategoryId = rc.Comp.WornCategoryId;
                    break;
                }
            }
        }

        return new LootItem
        {
            Base = baseEntry.El,
            Enchantment = enchant,
            Augment = augment,
            WornCategoryId = wornCategoryId,
            CompositeName = entry.CompositeName,
            DamageOverride = entry.DamageOverride,
            ShowPowerCard = entry.ShowPowerCard,
            Weight = entry.Weight,
            AugmentXml = entry.AugmentXml,
            IsInAlternateSlot = entry.IsInAlternateSlot,
            CascadedGrantsByComponentId = BuildCascadeMap(resolved),
            WornCategoryIdByComponentId = BuildWornCategoryMap(resolved),
        };
    }

    private static IReadOnlyDictionary<string, string> BuildWornCategoryMap(
        List<(LootComponent Comp, RulesElement El)> resolved)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (comp, el) in resolved)
        {
            if (string.IsNullOrEmpty(comp.WornCategoryId)) continue;
            if (string.IsNullOrEmpty(el.InternalId)) continue;
            map[el.InternalId] = comp.WornCategoryId;
        }
        return map;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<System.Xml.Linq.XElement>> BuildCascadeMap(
        List<(LootComponent Comp, RulesElement El)> resolved)
    {
        var map = new Dictionary<string, IReadOnlyList<System.Xml.Linq.XElement>>(StringComparer.Ordinal);
        foreach (var (comp, el) in resolved)
        {
            if (comp.CascadedGrants.Count == 0) continue;
            if (string.IsNullOrEmpty(el.InternalId)) continue;
            map[el.InternalId] = comp.CascadedGrants.ToList();
        }
        return map;
    }

    private static RulesElement? ResolveLootElement(TallyElement item, IRulesDatabase database, CharacterSession? session = null)
    {
        if (!string.IsNullOrEmpty(item.InternalId))
        {
            var byId = database.FindByInternalId(item.InternalId);
            if (byId is not null) return byId;
        }
        var byName = database.FindByNameAndType(item.Name, item.Type);
        if (byName is not null) return byName;

        // Source references a loot rules-element our DB doesn't have
        // (CBLoader .part content or a houseruled magic item like
        // Althaea's Starjewel). Synthesise a placeholder RulesElement
        // and record an UnresolvedElement so the loot row round-trips
        // verbatim instead of being silently dropped.
        if (string.IsNullOrEmpty(item.InternalId)) return null;

        session?.AddUnresolvedElement(new UnresolvedElement(
            InternalId: item.InternalId,
            Name: item.Name ?? string.Empty,
            Type: item.Type ?? string.Empty,
            Location: UnresolvedLocation.Loot));

        return new RulesElement
        {
            InternalId = item.InternalId,
            Name = item.Name ?? string.Empty,
            Type = item.Type ?? string.Empty,
            Rules = [],
        };
    }

    /// <summary>
    /// Map a rules element to the UI equip slot(s) it can occupy, in
    /// preference order. Mirrors <c>CharacterSessionService.GetPreferredSlots</c>.
    /// When <paramref name="prefersAlternateSlot"/> is true (set when the
    /// source loot row had <c>_AlternateSlot="1"</c>), the rules element's
    /// <c>_AlternateSlot</c> field value is preferred over the primary
    /// Item Slot. Mirrors OCB <c>CharLootItemSlot</c> which appends the 
    /// <c>_AlternateSlot</c> field to the slot name when the loot has the bit set.
    /// </summary>
    private static IReadOnlyList<string> GetPreferredSlots(RulesElement item, bool prefersAlternateSlot = false)
    {
        // Alternate-slot signal trumps primary slot (and trumps the
        // Weapon-type default of Main Hand/Off-Hand). Wrist Razors
        // primary=Off-hand, _AlternateSlot=Arms; with the bit set the
        // item belongs in Arms.
        if (prefersAlternateSlot
            && item.Fields.TryGetValue("_AlternateSlot", out var altSlot)
            && !string.IsNullOrWhiteSpace(altSlot))
        {
            var altMapped = MapSlotName(altSlot);
            if (altMapped.Count > 0) return altMapped;
        }

        if (string.Equals(item.Type, "Weapon", StringComparison.OrdinalIgnoreCase))
        {
            // For weapons, honor the rules-data Item Slot when it pins the
            // weapon to a specific slot (e.g. Wrist Razors → Off-hand).
            // Fall back to Main Hand/Off-Hand otherwise.
            if (item.Fields.TryGetValue("Item Slot", out var weaponSlot)
                && !string.IsNullOrWhiteSpace(weaponSlot))
            {
                var trimmed = weaponSlot.Trim().ToLowerInvariant();
                if (trimmed is "off hand" or "off-hand")
                    return ["Off-Hand", "Main Hand"];
                // Other explicit Item Slot values map normally; weapons
                // without an Item Slot (or with a generic "weapon" slot)
                // get the canonical hand pair.
                var mapped = MapSlotName(weaponSlot);
                if (mapped.Count > 0) return mapped;
            }
            return ["Main Hand", "Off-Hand"];
        }

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

        return MapSlotName(rawSlot);
    }

    /// <summary>
    /// Translate one or more comma-separated rules-data slot names into
    /// the equipped-slot dictionary keys used by <see cref="CharacterSession"/>.
    /// </summary>
    private static List<string> MapSlotName(string rawSlot)
    {
        var result = new List<string>();
        foreach (var slot in rawSlot.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (slot.Trim().ToLowerInvariant())
            {
                case "body": result.Add("Chest"); break;
                case "head": result.Add("Head"); break;
                case "neck": result.Add("Neck"); break;
                case "arms": result.Add("Arms"); break;
                case "hands": result.Add("Hands"); break;
                case "waist": result.Add("Waist"); break;
                case "feet": result.Add("Feet"); break;
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
    /// Walk <paramref name="parentNode"/>'s children in document order, feeding
    /// each non-grant child into the next compatible pending slot under
    /// <paramref name="parentInternalId"/>.
    /// </summary>
    /// <summary>
    /// Shared body for the two deferred-pick retry passes. Drains
    /// <see cref="ImportAlignmentContext.DeferredPicks"/> by repeatedly asking
    /// <paramref name="findSlot"/> for a landing slot, looping until a full
    /// sweep makes no progress. The only thing that differed between the two
    /// original copies was the slot finder (owner-scoped vs type-only).
    /// </summary>
    private static void ProcessDeferredPicks(
        ImportAlignmentContext ctx,
        Func<DeferredPick, ChoiceSlot?> findSlot)
    {
        var deferredPicks = ctx.DeferredPicks;
        bool progress = true;
        int safety = (deferredPicks.Count + 4) * 4;
        while (progress && deferredPicks.Count > 0 && safety-- > 0)
        {
            progress = false;
            for (int i = deferredPicks.Count - 1; i >= 0; i--)
            {
                var dp = deferredPicks[i];
                var slot = findSlot(dp);
                if (slot is null) continue;

                deferredPicks.RemoveAt(i);
                ctx.Session.MakeChoice(slot, dp.Element);
                progress = true;

                AlignChildren(dp.Node, dp.Element.InternalId, dp.Level, ctx);
            }
        }
    }

    private static void AlignChildren(
        ImportedRulesElement parentNode,
        string? parentInternalId,
        int currentLevel,
        ImportAlignmentContext ctx)
    {
        var session = ctx.Session;
        var database = ctx.Database;
        var charelemMap = ctx.CharelemMap;
        var preservedSwapTargets = ctx.PreservedSwapTargets;
        var tallyReplaces = ctx.TallyReplaces;
        var tallyStandaloneCharelems = ctx.TallyStandaloneCharelems;
        var tallySwapperInternalIds = ctx.TallySwapperInternalIds;
        var unresolved = ctx.Unresolved;
        var deferredPicks = ctx.DeferredPicks;
        var tallyAcquisitionLevels = ctx.TallyAcquisitionLevels;
        // Per-owner ReplaceDirective level assignment: when the owner has
        // ReplaceDirectives (e.g. Psionic Augmentation (Hybrid) carries
        // ReplaceDirectives at Level=13/17/23/27 for the Hybrid swap chain),
        // pair the captured swap rows under this owner with the directive
        // Levels in document/level order. This is THE authoritative signal
        // for swap-acquisition level — overrides the captured-tree position
        // and the tally-acquisition-level lookup.
        var swapLevelByCharelem = TryResolveSwapLevelsFromDirectives(
            parentNode, parentInternalId, database);

        foreach (var child in parentNode.Children)
        {
            if (child.IsEmptyPlaceholder && string.IsNullOrEmpty(child.Replaces))
            {
                // Empty placeholder => "user deliberately did not fill this slot".
                // Consume the next pending slot under the current parent regardless of type.
                var skipSlot = FindNextPendingSlot(session, parentInternalId, type: null);
                if (skipSlot is not null)
                    session.SkipSlot(skipSlot);
                continue;
            }

            // Try to resolve the element from the rules DB.
            RulesElement? element = null;
            if (!string.IsNullOrEmpty(child.InternalId))
                element = database.FindByInternalId(child.InternalId);
            element ??= database.FindByNameAndType(child.Name, child.Type);

            // Retraining swap: this element replaces an earlier-acquired one
            // via OCB's `replaces=<charelem>` attribute. It does NOT consume a
            // normal pending slot — the engine applies the swap during Build
            // by dropping the old element and granting the new one. The
            // element's own grant cascade (e.g., a power's secondary attack)
            // is handled by ApplyReplacements -> ProcessDeferredGrants, so we
            // also do not recurse into the file's literal child list here.
            //
            // Tally-corroboration filter:
            //   - swap is present in flat `<RulesElementTally>`     => apply
            //   - swap NOT in tally:
            //       OLD's charelem appears as a STANDALONE tally row => skip
            //         (OCB preserved OLD without applying the swap —
            //          e.g. `Aislin (2)`'s nested FF replaces=Dishearten
            //          where Dishearten survives in tally)
            //       OLD NOT in tally as standalone                   => apply
            //         (chain intermediate link — Artificer Smokepowder
            //          Blast -> Lightning Sigil -> Hellfire Sigil where
            //          only the final link is in tally but OCB still
            //          drops the early links)
            if (!string.IsNullOrEmpty(child.Replaces)
                && charelemMap.TryGetValue(child.Replaces!, out var oldEntry))
            {
                if (child.IsEmptyPlaceholder)
                {
                    // Empty-placeholder swap with `replaces=<charelem>`. Two
                    // legitimate interpretations:
                    //   (a) User retrained a previous pick OUT and didn't
                    //       choose a new one — drop the previous element.
                    //   (b) Structural noise: when a chain of swaps applied
                    //       in OCB (Force Shard L1 grant -> retrain to
                    //       Scorching Sands at L13), OCB emits the empty
                    //       placeholder as a chain-link marker. The "dropped"
                    //       element is in fact the NEW of an earlier-applied
                    //       swap (Scorching Sands itself), and OCB does NOT
                    //       remove it from the active tally — it's the
                    //       current state.
                    //
                    // Gate (b) on tally corroboration: if the element being
                    // "dropped" by the empty placeholder is itself a NEW in
                    // the flat tally's swap rows (tallySwapperInternalIds),
                    // OCB kept it active — the empty placeholder is just
                    // chain bookkeeping. Don't record the drop.
                    if (tallySwapperInternalIds.Contains(oldEntry.InternalId))
                        continue;

                    int dropLevel = swapLevelByCharelem.TryGetValue(child.Charelem ?? "", out var dDirLvl)
                        ? dDirLvl
                        : ResolveSwapLevel(child, currentLevel, tallyAcquisitionLevels);
                    session.AddReplacement(dropLevel, new ElementReplacement(
                        OldInternalId: oldEntry.InternalId,
                        NewInternalId: string.Empty,
                        SwapOwnerInternalId: parentInternalId));
                    continue;
                }

                if (element is null || string.IsNullOrEmpty(child.InternalId))
                {
                    unresolved.Add($"Retrain target: {child.Type}::{child.Name} (id={child.InternalId})");
                    continue;
                }

                bool swapInTally = tallyReplaces.Contains((child.InternalId!, child.Replaces!));
                bool oldStandaloneInTally = tallyStandaloneCharelems.Contains(child.Replaces!);
                if (!swapInTally && oldStandaloneInTally)
                {
                    // Structural-noise: nested swap targets an OLD that OCB
                    // confirmed survived. Drop the swap entirely.
                    continue;
                }

                // PreserveOld: keep OLD visible in PowerStats / tally if ANY
                //   (a) multiclass-utility-swap pattern (OLD also stands alone
                //       at the same level under a Feat — Acolyte Power), OR
                //   (b) chain pattern: OLD is itself the NEW of another tally
                //       swap row (intermediate retrain step). OCB keeps the
                //       intermediate visible in PowerStats (Dishearten ->
                //       Foolhardy Fighting -> Dark Gathering preserves FF;
                //       contrast with Smokepowder->Lightning Sigil->Hellfire
                //       where Lightning Sigil is NOT in tally so it's not
                //       in tallySwapperInternalIds and gets dropped), OR
                //   (c) explicit-keep-both pattern: OCB emitted BOTH the
                //       swap row AND the OLD as a standalone tally row in
                //       the same flat tally (e.g. Bladesinger L19 Mass Charm
                //       retrained to Azure Talons — both kept in tally and
                //       PowerStats). The standalone OLD row IS OCB telling
                //       us to keep it; mirror that decision.
                bool preserveOld = preservedSwapTargets.Contains(oldEntry.InternalId)
                    || tallySwapperInternalIds.Contains(oldEntry.InternalId)
                    || (swapInTally && oldStandaloneInTally);

                int swapLevel = swapLevelByCharelem.TryGetValue(child.Charelem ?? "", out var sDirLvl)
                    ? sDirLvl
                    : ResolveSwapLevel(child, currentLevel, tallyAcquisitionLevels);
                session.AddReplacement(swapLevel, new ElementReplacement(
                    OldInternalId: oldEntry.InternalId,
                    NewInternalId: child.InternalId!,
                    NewName: child.Name,
                    NewType: child.Type,
                    PreserveOld: preserveOld,
                    SwapOwnerInternalId: parentInternalId));

                // Recurse into the swap-NEW's children. We don't process
                // them as user picks (the element's grant cascade is
                // handled by ApplyReplacements -> ProcessDeferredGrants),
                // but children may themselves carry nested `replaces=` rows
                // that need recording — e.g. Thief Expert (Feat, swap)
                // contains Thief's Getaway (Power, replaces=Bramble Hide).
                // Without this recursion the nested swap row, even when
                // present in tally, is never reached.
                AlignChildren(
                    child,
                    parentInternalId: child.InternalId,
                    currentLevel: currentLevel,
                    ctx);
                continue;
            }

            // Distinguish auto-grants from user picks. If the parent has a
            // GrantDirective targeting this exact element id, the engine
            // already added it during phase-1 — we must NOT consume a select
            // slot with it. Otherwise an auto-grant of the same ElementType
            // (e.g. Fighter's auto-granted Combat Challenge) will eat the
            // slot meant for the real user pick (Combat Superiority).
            bool isAutoGrant = !string.IsNullOrEmpty(child.InternalId)
                && IsGrantedByParent(parentInternalId, child.InternalId, database);

            ChoiceSlot? slot = isAutoGrant
                ? null
                : FindNextPendingSlot(session, parentInternalId, type: child.Type);

            if (slot is not null)
            {
                if (element is null)
                {
                    // Source references a rules-element our DB doesn't have
                    // (CBLoader .part-file content, houseruled additions like
                    // Lawful Evil, etc.). Synthesise a placeholder
                    // RulesElement and place it in the slot so the tally and
                    // level-tree round-trip carries the row verbatim, and
                    // record it on the session so the UI can surface a ⚠
                    // wherever it would naturally render. The placeholder
                    // has no Rules so the engine ignores it for stats and
                    // power computation.
                    var legality = child.Legality ?? "";
                    if (!string.IsNullOrEmpty(child.InternalId))
                    {
                        session.AddUnresolvedElement(new UnresolvedElement(
                            InternalId: child.InternalId,
                            Name: child.Name,
                            Type: child.Type,
                            Location: UnresolvedLocation.LevelTree,
                            Legality: legality,
                            ParentInternalId: parentInternalId,
                            AtLevel: currentLevel,
                            SourceXml: child.SourceElement));

                        if (legality.Equals("houserule", StringComparison.OrdinalIgnoreCase))
                            session.HouseruledElementIds.Add(child.InternalId);

                        var placeholder = new RulesElement
                        {
                            InternalId = child.InternalId,
                            Name = child.Name,
                            Type = child.Type,
                            Rules = [],
                        };
                        session.MakeChoice(slot, placeholder);
                    }
                    else
                    {
                        // No internal-id to anchor a placeholder against —
                        // fall back to the old skip-slot behaviour. (Rare:
                        // we don't see these in the corpus.)
                        unresolved.Add($"{child.Type}::{child.Name} (id=)");
                        session.SkipSlot(slot);
                    }
                }
                else
                {
                    session.MakeChoice(slot, element);
                }
            }
            else if (!isAutoGrant && element is not null)
            {
                // No slot found and not an auto-grant: this is likely a user
                // pick whose target slot doesn't exist yet (the slot's owner
                // is created by a conditional grant that hasn't fired). Defer
                // for the end-of-walk retry pass.
                //
                // We fall through to the recursion below instead of `continue`ing
                // so deferred elements still get their CHILDREN walked — those
                // children may be the real user picks whose slot only opens
                // once the parent is later resolved (e.g. Signs of Influence
                // gets deferred at the Bard cascade, but Welcome Guest /
                // Travel in Style live nested inside it and would otherwise
                // never enter the importer's pick journal).
                deferredPicks.Add(new DeferredPick(child, element, parentInternalId, currentLevel));
            }
            // else: no matching slot and no resolvable element — granted or
            // already-filled. No-op; recurse anyway to capture deeper state.

            // Recurse using the child's own internal-id as the new parent. If
            // the child has no id (rare for non-empty nodes), keep the current
            // parent so deeper user picks still match against the right owner.
            string? nextParent = !string.IsNullOrEmpty(child.InternalId)
                ? child.InternalId
                : parentInternalId;

            AlignChildren(child, nextParent, currentLevel, ctx);
        }
    }

    /// <summary>
    /// Walk the imported level tree and record every element's
    /// <c>charelem</c> attribute alongside its InternalId and the level it
    /// belongs to. Used to resolve <c>replaces=</c> targets back to a
    /// concrete InternalId for retraining swaps.
    /// </summary>
    private static void CollectCharelems(
        ImportedRulesElement node,
        int level,
        Dictionary<string, (string InternalId, int Level)> map)
    {
        if (!string.IsNullOrEmpty(node.Charelem)
            && !string.IsNullOrEmpty(node.InternalId)
            && !map.ContainsKey(node.Charelem!))
        {
            map[node.Charelem!] = (node.InternalId!, level);
        }

        foreach (var child in node.Children)
            CollectCharelems(child, level, map);
    }

    /// <summary>
    /// Walk every level tree to find swap chains where OCB intentionally
    /// kept BOTH the swapped-out and swapped-in element in the file (the
    /// multiclass-utility-swap pattern, e.g. Acolyte Power feat → Protective
    /// Scroll keeping Astral Refuge alongside). OCB's distinction lives on
    /// the ReplaceDirective: a directive with <c>Multiclass=Utility</c> or
    /// <c>PowerSwap=</c> set keeps both, all other replaces drop the old.
    /// In the file this manifests structurally: only multiclass-utility
    /// feats nest the swap entry under themselves AND retain the original
    /// pick as a standalone row at the same level. Regular retraining puts
    /// the swap directly under the Level node and drops the old entry.
    ///
    /// Heuristic: at the SAME level, an element has <c>replaces="X"</c> AND
    /// is nested under a parent of type="Feat", AND a separate non-replaces
    /// element has <c>charelem="X"</c>. Returns the set of OldInternalIds
    /// whose original tally row should stay visible after the swap.
    /// </summary>
    private static HashSet<string> CollectPreservedSwapTargets(
        IEnumerable<CharM.Serialization.ImportedLevel> levelTrees,
        IReadOnlyDictionary<string, (string InternalId, int Level)> charelemMap)
    {
        // Per-level sets: { level -> set of charelems referenced by replaces=
        // from inside a Feat }, { level -> set of charelems that appear as a
        // non-replaces= standalone row in that level }.
        var replacedByLevelInFeat = new Dictionary<int, HashSet<string>>();
        var standaloneByLevel = new Dictionary<int, HashSet<string>>();

        foreach (var lt in levelTrees)
        {
            if (!replacedByLevelInFeat.TryGetValue(lt.Level, out var rep))
                replacedByLevelInFeat[lt.Level] = rep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!standaloneByLevel.TryGetValue(lt.Level, out var standalone))
                standaloneByLevel[lt.Level] = standalone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSwapInfo(lt.Root, parentType: null, rep, standalone);
        }

        var preserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (level, replacedAtLevel) in replacedByLevelInFeat)
        {
            if (!standaloneByLevel.TryGetValue(level, out var standaloneAtLevel)) continue;
            foreach (var ce in replacedAtLevel)
            {
                if (!standaloneAtLevel.Contains(ce)) continue;
                if (charelemMap.TryGetValue(ce, out var entry))
                    preserved.Add(entry.InternalId);
            }
        }
        return preserved;
    }

    private static void CollectSwapInfo(
        ImportedRulesElement node,
        string? parentType,
        HashSet<string> replacedCharelemsInFeat,
        HashSet<string> standaloneCharelems)
    {
        if (!string.IsNullOrEmpty(node.Replaces))
        {
            // Only treat the swap as "preserve both" when it's nested
            // directly under a Feat — that mirrors OCB's Multiclass / PowerSwap
            // ReplaceDirective shape, which only appears on feats.
            if (string.Equals(parentType, "Feat", StringComparison.OrdinalIgnoreCase))
                replacedCharelemsInFeat.Add(node.Replaces!);
        }
        else if (!string.IsNullOrEmpty(node.Charelem))
        {
            standaloneCharelems.Add(node.Charelem!);
        }

        foreach (var child in node.Children)
            CollectSwapInfo(child, node.Type, replacedCharelemsInFeat, standaloneCharelems);
    }

    /// <summary>
    /// First pending slot whose <c>OwnerInternalId</c> matches
    /// <paramref name="parentInternalId"/> and (when <paramref name="type"/> is
    /// non-null) whose <c>ElementType</c> matches.
    /// </summary>
    private static ChoiceSlot? FindAnyPendingSlotByType(
        CharacterSession session,
        string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;

        foreach (var slot in session.GetPendingSlots())
        {
            if (string.Equals(slot.ElementType, type, StringComparison.OrdinalIgnoreCase))
                return slot;
        }
        return null;
    }

    private static ChoiceSlot? FindNextPendingSlot(
        CharacterSession session,
        string? parentInternalId,
        string? type)
    {
        foreach (var slot in session.GetPendingSlots())
        {
            if (!string.Equals(slot.OwnerInternalId, parentInternalId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (type is not null
                && !string.Equals(slot.ElementType, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return slot;
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="parentInternalId"/>'s rules contain a
    /// <see cref="GrantDirective"/> targeting <paramref name="childInternalId"/>.
    /// Used to distinguish auto-grants from user picks during positional
    /// import so a grant of the same ElementType doesn't accidentally
    /// consume a select slot meant for the actual user choice.
    /// </summary>
    private static bool IsGrantedByParent(
        string? parentInternalId,
        string childInternalId,
        IRulesDatabase database)
    {
        if (string.IsNullOrEmpty(parentInternalId)) return false;

        var parent = database.FindByInternalId(parentInternalId);
        if (parent is null) return false;

        foreach (var directive in parent.Rules)
        {
            if (directive is GrantDirective grant
                && string.Equals(grant.Name, childInternalId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ParentHasSelectForType(
        string? parentInternalId,
        string? elementType,
        IRulesDatabase database)
    {
        if (string.IsNullOrWhiteSpace(parentInternalId) || string.IsNullOrWhiteSpace(elementType))
            return false;

        var parent = database.FindByInternalId(parentInternalId);
        if (parent is null) return false;

        return parent.Rules.Any(rule =>
            rule is SelectDirective select
            && string.Equals(select.ElementType, elementType, StringComparison.OrdinalIgnoreCase));
    }

}
