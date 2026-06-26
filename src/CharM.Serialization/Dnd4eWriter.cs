using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Xml.Linq;
using CharM.Engine.CharacterModel;
using CharM.Engine.Creation;

namespace CharM.Serialization;

/// <summary>
/// All data needed to write a .dnd4e file.
/// </summary>
public sealed class CharacterExportData
{
    public required string Name { get; init; }
    public required int Level { get; init; }
    public Dictionary<string, StatExportData> Stats { get; init; } = new();
    public List<TallyElement> RulesElementTally { get; init; } = [];
    public List<LootEntry> LootTally { get; init; } = [];
    public Dictionary<string, int> BaseAbilityScores { get; init; } = new();
    public Dictionary<string, string> Details { get; init; } = new();
    public Dictionary<string, string> TextStrings { get; init; } = new();
    public List<PowerStatEntry> PowerStats { get; init; } = [];
    public List<TallyElement> GrabbagGrants { get; init; } = [];
    public bool RebuildGrabbag { get; init; }

    /// <summary>
    /// Structured companion data for emitting the <c>&lt;Companions&gt;</c> block.
    /// When non-empty, the writer rebuilds the section structurally instead of
    /// using <see cref="RawSections"/> passthrough. Each entry is one beast.
    /// </summary>
    public List<CompanionData> Companions { get; init; } = [];

    /// <summary>
    /// Verbatim XML pass-through for sections we don't structurally model
    /// yet: <c>D20CampaignSetting</c>, <c>Grabbag</c> (root level), and
    /// <c>Companions</c>, <c>Journal</c>, <c>PowerStats</c> (CharacterSheet
    /// level). When present here, these elements are emitted as-is and
    /// override the writer's hard-coded placeholders. Keyed by element
    /// local-name. Captured at import via <c>Dnd4eReader</c>.
    /// </summary>
    public Dictionary<string, XElement> RawSections { get; init; }
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Houserule overlay re-emission data — see <see cref="HouseruleOverlay"/>.
    /// When <see cref="IsCharacterHouseruled"/> is true the writer cascades
    /// <c>legality="houserule"</c> on <c>D20Character</c> + <c>AbilityScores</c>.
    /// </summary>
    public bool IsCharacterHouseruled { get; init; }
    public Dictionary<int, List<XElement>> HouseruleLevelUserEdits { get; init; }
        = new();
    public List<XElement> HouseruleFormATallyMirror { get; init; } = new();
    public List<XElement> HouseruleLegacyTallyRows { get; init; } = new();

    /// <summary>
    /// Internal-ids the source file tagged with <c>legality="houserule"</c>
    /// on individual <c>&lt;RulesElement&gt;</c> rows (Level tree, tally, or
    /// UserEdit subtree). Used by the writer to re-emit per-element legality
    /// so round-trips don't silently promote houseruled feats / powers to
    /// rules-legal.
    /// </summary>
    public HashSet<string> HouseruledElementIds { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Source per-level loot journal rows captured from imported files.
    /// The authoritative modeled equipment list remains <see cref="LootTally"/>;
    /// these rows preserve OCB's duplicated acquisition history under
    /// <c>&lt;Level&gt;</c> containers.
    /// </summary>
    public Dictionary<int, List<XElement>> SourceLevelLoot { get; init; }
        = new();

    /// <summary>
    /// Verbatim per-level <c>&lt;RulesElement type="Level"&gt;</c> XML captured
    /// from the source file. When an entry exists for level N, the writer
    /// splices it as the inner element of the <c>&lt;Level&gt;</c> container
    /// instead of rebuilding from the engine's CharacterElement tree. This
    /// mirrors OCB's load+save behavior (CharElement tree preserved verbatim
    /// across the round-trip) and closes the LevelTree S2 stale-residue +
    /// Decider-deep-nesting + Implement-Proficiency-cascade buckets. Dirty
    /// levels (UI-mutated) are excluded by the exporter so the engine-rebuild
    /// path takes over for them. See <c>docs/dnd4e-file-format.md</c> and
    /// <see cref="CharM.Engine.Creation.CharacterSession.CapturedLevelTrees"/>.
    /// </summary>
    public Dictionary<int, XElement> CapturedLevelXml { get; init; }
        = new();

    /// <summary>
    /// InternalIds of elements added to the engine tree via
    /// UserEdit-pick processing. The writer's level-tree builder skips
    /// these (and their subtrees) so the verbatim
    /// <see cref="HouseruleLevelUserEdits"/> blocks aren't double-emitted.
    /// </summary>
    public HashSet<string> UserEditPickIds { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Imported per-element metadata used to re-emit LevelTree attributes that
    /// are not part of the rules model, such as compendium URLs.
    /// </summary>
    public Dictionary<string, ElementSourceMetadata> SourceMetadata { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The character element tree root. Used to serialize the nested Level section.
    /// When null, the Level section is omitted.
    /// </summary>
    public CharacterElement? ElementTreeRoot { get; init; }

    /// <summary>
    /// Synthetic children to inject into the LevelTree cascade when serializing
    /// the parent node identified by the dictionary key (an InternalId).
    /// Used to mirror OCB-specific cascade behaviors where the rules-DB's
    /// per-grant <c>Requires</c> gate is bypassed in the LevelTree but
    /// retained in the flat <c>&lt;RulesElementTally&gt;</c>.
    /// Currently populated only for
    /// <c>ID_INTERNAL_PROFICIENCY_IMPLEMENT_PROFICIENCY_(PROFICIENT_WEAPONS)</c>;
    /// see <c>docs/engine-special-cases.md</c> for the catalog entry.
    /// </summary>
    public Dictionary<string, List<TallyElement>> ImplicitLevelTreeChildren { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PowerStatEntry
{
    public required string Name { get; init; }
    public Dictionary<string, string> Specifics { get; init; } = new();

    /// <summary>
    /// Computed weapon-specific stat lines (one per applicable weapon, or one
    /// "Unarmed" entry for implement-only powers when no weapon is wielded).
    /// Mirrors OCB's <c>&lt;Weapon&gt;</c> children inside each <c>&lt;Power&gt;</c>.
    /// Empty list means the writer emits only the <c>&lt;specific&gt;</c> lines
    /// (matches OCB output for non-attack powers like utilities).
    /// </summary>
    public List<PowerStatWeapon> Weapons { get; init; } = [];
}

/// <summary>
/// One <c>&lt;Weapon&gt;</c> child under a <c>&lt;Power&gt;</c> in the
/// <c>&lt;PowerStats&gt;</c> section. Holds the precomputed attack/damage
/// numbers OCB displays on the power card. <see cref="Components"/> emits the
/// inner <c>&lt;RulesElement&gt;</c> references (base weapon + any enchanted
/// magic-item layer); empty for the synthetic "Unarmed" entry.
/// </summary>
public sealed class PowerStatWeapon
{
    public required string Name { get; init; }
    public int AttackBonus { get; init; }
    public string Damage { get; init; } = string.Empty;
    public string DamageType { get; init; } = string.Empty;
    public string AttackStat { get; init; } = string.Empty;
    public string Defense { get; init; } = string.Empty;
    public string Healing { get; init; } = string.Empty;
    public string HitComponents { get; init; } = string.Empty;
    public string DamageComponents { get; init; } = string.Empty;
    public string HealingComponents { get; init; } = string.Empty;
    public string Conditions { get; init; } = string.Empty;
    public List<TallyElement> Components { get; init; } = [];
}

public sealed class StatExportData
{
    public required int Value { get; init; }
    public List<StatContribution> Contributions { get; init; } = [];
}

public sealed record StatContribution(
    string? Type,
    int Value,
    string? SourceId = null)
{
    /// <summary>Character level at which this contribution applies. Emitted as <c>Level=</c>.</summary>
    public int? Level { get; init; }

    /// <summary>Verbatim requires gate text. Emitted as <c>requires=</c>.</summary>
    public string? Requires { get; init; }

    /// <summary>Verbatim wearing predicate. Emitted as <c>wearing=</c>.</summary>
    public string? Wearing { get; init; }

    /// <summary>Verbatim not-wearing predicate. Emitted as <c>not-wearing=</c>.</summary>
    public string? NotWearing { get; init; }

    /// <summary>Display-only conditional notes (e.g. "while bloodied"). Emitted as <c>conditional=</c>.</summary>
    public string? Conditional { get; init; }

    /// <summary>
    /// Literal text payload from a textstring directive. Emitted as
    /// <c>String=</c> on a <c>value="0"</c> statadd.
    /// </summary>
    public string? StringPayload { get; init; }

    /// <summary>
    /// Cross-stat link. When set, OCB resolves the value at compute time by
    /// looking up the named stat (or its ability modifier when
    /// <see cref="AbilMod"/> is true). Emitted as <c>statlink=</c>.
    /// </summary>
    public string? StatLink { get; init; }

    /// <summary>
    /// When true, the value is the ability modifier of <see cref="StatLink"/>
    /// (rather than the raw stat value). Emitted as <c>abilmod="true"</c>.
    /// </summary>
    public bool AbilMod { get; init; }
}

/// <summary>
/// Serializes character data to .dnd4e XML format.
/// Produces XML structurally matching the original Character Builder output
/// so the CB can open and recalculate these files.
/// </summary>
public static class Dnd4eWriter
{
    private static readonly ConcurrentDictionary<string, string> CharelemCache =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> StatAliases = new()
    {
        ["Strength"] = "str",
        ["Constitution"] = "con",
        ["Dexterity"] = "dex",
        ["Intelligence"] = "int",
        ["Wisdom"] = "wis",
        ["Charisma"] = "cha",
        ["AC"] = "Armor Class",
        ["Fortitude Defense"] = "Fortitude",
        ["Reflex Defense"] = "Reflex",
        ["Will Defense"] = "Will",
    };

    private static readonly string[] InternalStats =
    [
        "AC Defense Class Bonus",
        // "Action Point" is intentionally NOT in this fallback list. Unlike
        // the other entries here, Action Point is only emitted by the
        // original CB/OCB when there are actual textstring contributions
        // (action-point trigger powers, paragon path action-point features,
        // etc.). Including it as a default produced a phantom
        // <statadd value="0"/> in characters with no such triggers and
        // diverged from OCB output. The actual action-point resource pool
        // lives under "_BaseActionPoints", which is unaffected.
        "Average Height",
        "Average Weight",
        "Hybrid Power Points",
        "PowersAsClass",
        "Size",
        "Weight",
        "_CLASSNAME",
    ];
    /// <summary>
    /// Subset of <see cref="InternalStats"/> that, when fallback-emitted (the
    /// engine produced no contributions), must NOT carry a synthetic
    /// <c>&lt;statadd value="0"/&gt;</c> child. CB/OCB-saved baselines emit
    /// <c>&lt;Stat value="0"&gt;&lt;alias name="..." /&gt;&lt;/Stat&gt;</c>
    /// for these — adding the child diverged 131 community files.
    /// </summary>
    private static readonly HashSet<string> InternalStatsNoFallbackStatadd =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "AC Defense Class Bonus",
    };

    /// <summary>
    /// Writes the character to <paramref name="output"/>.
    /// </summary>
    /// <param name="includeXmlDeclaration">
    /// When <c>true</c>, prepend the standard <c>&lt;?xml version="1.0"
    /// encoding="utf-8"?&gt;</c> processing instruction. Defaults to
    /// <c>false</c> to match the original CB output exactly (CB/OCB do not
    /// emit a declaration, and we want byte-for-byte parity for round-trip
    /// validation). OCB tolerates the declaration when present.
    /// </param>
    public static void Write(Stream output, CharacterExportData data,
        bool includeXmlDeclaration = false)
    {
        var doc = BuildDocument(data);
        SaveDocument(doc, output, includeXmlDeclaration);
    }

    public static void WriteToFile(string path, CharacterExportData data,
        bool includeXmlDeclaration = false)
    {
        var doc = BuildDocument(data);
        using var fs = File.Create(path);
        SaveDocument(doc, fs, includeXmlDeclaration);
    }

    private static void SaveDocument(XDocument doc, Stream output, bool includeXmlDeclaration)
    {
        // OCB does NOT normalize attribute values on write -- it interns the
        // raw string from the rules data and emits it verbatim via
        // SaveAttribute -> WriteEscaped (-Module-.cs:11017, D20CharIO.cs:25).
        // We deliberately do NOT trim or collapse runs here. A small handful
        // of trailing-whitespace diffs (Out of Sight, Guardian's Defense,
        // Disappear, Defender's Gambit) trace back to source-XML differences
        // between our merged rules and OCB's, not the writer.

        if (includeXmlDeclaration)
        {
            doc.Save(output);
            return;
        }

        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            Encoding = new UTF8Encoding(false),
        };
        using var xw = System.Xml.XmlWriter.Create(output, settings);
        doc.Save(xw);
    }

    private static XDocument BuildDocument(CharacterExportData data)
    {
        var legality = data.IsCharacterHouseruled ? "houserule" : "rules-legal";
        var root = new XElement("D20Character",
            new XAttribute("game-system", "D&D4E"),
            new XAttribute("Version", "0.07a"),
            new XAttribute("legality", legality));

        var charSheet = new XElement("CharacterSheet");
        root.Add(charSheet);

        // Order must match real CB output exactly:
        // Details → AbilityScores → StatBlock → RulesElementTally → LootTally → PowerStats → Companions → Journal
        charSheet.Add(BuildDetails(data));
        charSheet.Add(BuildAbilityScores(data));
        charSheet.Add(data.RawSections.TryGetValue("StatBlock", out var rawStat)
            ? new XElement(rawStat)
            : BuildStatBlock(data));
        charSheet.Add(BuildRulesElementTally(data));
        charSheet.Add(BuildLootTally(data));
        charSheet.Add(data.RawSections.TryGetValue("PowerStats", out var rawPower)
            ? new XElement(rawPower)
            : BuildPowerStats(data));
        charSheet.Add(data.Companions.Count > 0
            ? BuildCompanions(data)
            : data.RawSections.TryGetValue("Companions", out var rawComp)
                ? new XElement(rawComp)
                : ForceOpenClose("Companions"));
        charSheet.Add(data.RawSections.TryGetValue("Journal", out var rawJournal)
            ? new XElement(rawJournal)
            : ForceOpenClose("Journal"));

        // D20CampaignSetting — sibling of CharacterSheet. Pass through the
        // captured element if available so any <campaign-setting>/<houserule>
        // children survive the round-trip.
        root.Add(data.RawSections.TryGetValue("D20CampaignSetting", out var rawCs)
            ? new XElement(rawCs)
            : new XElement("D20CampaignSetting", new XAttribute("name", ""), new XText("\n")));

        if (data.RebuildGrabbag)
        {
            if (data.GrabbagGrants.Count > 0)
                root.Add(BuildGrabbag(data.GrabbagGrants));
        }
        // Grabbag is rare (~10% of files) — only emit if captured.
        else if (data.RawSections.TryGetValue("Grabbag", out var rawGrabbag))
            root.Add(new XElement(rawGrabbag));

        // Level trees — siblings of CharacterSheet, one per character level
        if (data.ElementTreeRoot is not null)
        {
            foreach (var levelTree in BuildLevelTrees(
                data.ElementTreeRoot,
                data.UserEditPickIds,
                data.SourceMetadata,
                data.HouseruledElementIds,
                data.ImplicitLevelTreeChildren,
                data.CapturedLevelXml))
            {
                // Inject <UserEdit> blocks at the END of the matching <Level>
                // container. Match by index then by parsed level number from
                // the inner Level RulesElement so we line up with the source.
                var inner = levelTree.Element("RulesElement");
                int? levelNum = null;
                if (inner is not null
                    && string.Equals(inner.Attribute("type")?.Value, "Level",
                        StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(inner.Attribute("name")?.Value, out var ln))
                {
                    levelNum = ln;
                }

                if (levelNum is { } lootLevel
                    && data.SourceLevelLoot.TryGetValue(lootLevel, out var levelLoot))
                {
                    foreach (var loot in levelLoot)
                        levelTree.Add(new XElement(loot));
                }

                if (levelNum is { } lvl
                    && data.HouseruleLevelUserEdits.TryGetValue(lvl, out var ueList))
                {
                    foreach (var ue in ueList)
                        levelTree.Add(new XElement(ue));
                }

                root.Add(levelTree);
            }
        }

        // Root-level textstrings — sibling of CharacterSheet, after Level tree
        foreach (var (name, value) in data.TextStrings)
            root.Add(new XElement("textstring", new XAttribute("name", name), value));

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            root);
    }

    /// <summary>Create an element that is never self-closing (CB rejects those).</summary>
    private static XElement ForceOpenClose(string name) => new(name, new XText("\n"));

    public static XElement CreateRulesElementXml(
        string name,
        string type,
        string? internalId = null,
        string? url = null,
        string? charelem = null,
        string? replaces = null,
        string legality = "rules-legal",
        bool forceOpenClose = false)
    {
        var element = new XElement("RulesElement",
            new XAttribute("name", name),
            new XAttribute("type", type));

        if (!string.IsNullOrEmpty(internalId))
        {
            element.Add(new XAttribute("internal-id", internalId));
            if (!string.IsNullOrEmpty(url))
                element.Add(new XAttribute("url", url));
            element.Add(new XAttribute("charelem", charelem ?? GenerateCharelem(internalId)));
            if (!string.IsNullOrEmpty(replaces))
                element.Add(new XAttribute("replaces", replaces));
        }
        else if (!string.IsNullOrEmpty(charelem))
        {
            element.Add(new XAttribute("charelem", charelem));
        }

        element.Add(new XAttribute("legality", legality));
        if (forceOpenClose)
            element.Add(new XText("\n"));
        return element;
    }

    private static XElement BuildGrabbag(IEnumerable<TallyElement> grants)
    {
        var grantList = grants
            .Where(g => !string.IsNullOrWhiteSpace(g.InternalId))
            .ToList();

        var wrapper = CreateRulesElementXml(
            string.Empty,
            string.Empty,
            charelem: GenerateCharelem("Grabbag"));

        foreach (var grant in grantList)
        {
            wrapper.Add(CreateRulesElementXml(
                grant.Name,
                grant.Type,
                grant.InternalId));
        }

        var rules = new XElement("rules");
        foreach (var grant in grantList)
        {
            rules.Add(new XElement("grant",
                new XAttribute("name", grant.InternalId!),
                new XAttribute("type", grant.Type)));
        }

        return new XElement("Grabbag", wrapper, rules);
    }

    private static XElement BuildDetails(CharacterExportData data)
    {
        var details = new XElement("Details");
        details.Add(new XElement("name", $" {data.Name} "));
        details.Add(new XElement("Level", $" {data.Level} "));

        // Standard detail fields — emit with whitespace padding like the real CB
        var fields = new[] { "Player", "Height", "Weight", "Gender", "Age", "Alignment",
            "Company", "Portrait", "Experience", "CarriedMoney", "StoredMoney",
            "Traits", "Appearance", "Companions", "Notes" };
        foreach (var field in fields)
        {
            var value = data.Details.GetValueOrDefault(field, "");
            details.Add(new XElement(field, $" {value} "));
        }

        return details;
    }

    private static XElement BuildAbilityScores(CharacterExportData data)
    {
        var abilityOrder = new[] { "Strength", "Constitution", "Dexterity", "Intelligence", "Wisdom", "Charisma" };
        var scores = abilityOrder
            .Select(a => data.BaseAbilityScores.GetValueOrDefault(a, 10))
            .ToArray();

        var section = new XElement("AbilityScores",
            new XAttribute("legality",
                IsLegalAbilityScores(scores) ? "rules-legal" : "houserule"));

        for (int i = 0; i < abilityOrder.Length; i++)
        {
            section.Add(new XElement(abilityOrder[i], new XAttribute("score", scores[i])));
        }

        return section;
    }

    /// <summary>
    /// PHB1 4e point-buy cost table indexed by (score - 8). Score range 8..18.
    /// Decoded from D20RulesEngine.dll's _003FA0x4fcca1c9_002Ecost array (see
    /// docs/d20-engine-analysis.md "AbilityScores" section).
    /// </summary>
    private static readonly int[] AbilityPointBuyCost =
    [
        0,  // 8
        1,  // 9
        2,  // 10
        3,  // 11
        4,  // 12
        5,  // 13
        7,  // 14
        9,  // 15
        11, // 16
        14, // 17
        18, // 18
    ];

    /// <summary>
    /// Replicates <c>D20RulesEngine.LegalAbilityScores</c> (decompile lines
    /// 7017-7024). An AbilityScores block is "rules-legal" iff:
    /// <list type="number">
    ///   <item>Any score is 0 (uninitialized) — auto-passes; OR</item>
    ///   <item>All scores are in [8..18], at most one score has cost &lt; 2
    ///         (i.e. at most one stat is dumped to 8 or 9), and the total
    ///         point-buy cost minus 10 equals exactly 22.</item>
    /// </list>
    /// Otherwise the user changed the array from a legal point-buy distribution
    /// (manual edit, errata, houserule) and the block is tagged "houserule".
    /// This is independent of <c>D20Character/@legality</c>, which reflects
    /// houseruled feats/powers rather than ability-score edits.
    /// </summary>
    private static bool IsLegalAbilityScores(int[] scores)
    {
        if (scores.Any(s => s == 0)) return true;

        int countCheap = 0;
        int total = 0;
        foreach (var score in scores)
        {
            if (score < 8 || score > 18) return false;
            int cost = AbilityPointBuyCost[score - 8];
            if (cost < 2) countCheap++;
            total += cost;
        }
        if (countCheap > 1) return false;
        return total - 10 == 22;
    }

    /// <summary>
    /// Canonical leading stat order observed in every CB/OCB-saved baseline
    /// character. Stats listed here are emitted first in this exact order
    /// (when present); everything else trails in insertion order. Matters
    /// only for diff readability — OCB doesn't care about Stat order.
    /// </summary>
    private static readonly string[] StatBlockLeadingOrder =
    [
        "Strength",
        "Constitution",
        "Dexterity",
        "Intelligence",
        "Wisdom",
        "Charisma",
        "Strength modifier",
        "Dexterity modifier",
        "Constitution modifier",
        "Intelligence modifier",
        "Wisdom modifier",
        "Charisma modifier",
        "AC",
        "Fortitude Defense",
        "Reflex Defense",
        "Will Defense",
        "Death Saves Count",
        "Level",
        "Hit Points",
        "_LEVEL-ONE-HPS",
        "Healing Surges",
        "HALF-LEVEL",
        "Fortitude Defense Class Bonus",
        "Reflex Defense Class Bonus",
        "Will Defense Class Bonus",
        "AC Defense Class Bonus",
        "Initiative",
        "Initiative Misc",
        "Ring Slots",
        "_BaseActionPoints",
        "XP Needed",
        "PowersAsClass",
    ];

    private static XElement BuildStatBlock(CharacterExportData data)
    {
        var statBlock = new XElement("StatBlock");

        var leadingIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < StatBlockLeadingOrder.Length; i++)
            leadingIndex[StatBlockLeadingOrder[i]] = i;

        var ordered = data.Stats
            .Select((kv, i) => (kv.Key, kv.Value, i))
            .OrderBy(x => leadingIndex.TryGetValue(x.Key, out var idx) ? idx : int.MaxValue)
            .ThenBy(x => x.i);

        foreach (var (name, stat, _) in ordered)
        {
            var statEl = new XElement("Stat",
                new XAttribute("value", stat.Value));

            statEl.Add(new XElement("alias", new XAttribute("name", name)));

            if (StatAliases.TryGetValue(name, out var alias))
                statEl.Add(new XElement("alias", new XAttribute("name", alias)));

            // OCB attribute order for <statadd>:
            //   type → String → Level → requires → wearing → not-wearing →
            //   conditional → value → statlink → abilmod → charelem
            // Sort stable by Level (nulls first, matching the bare base
            // <statadd value="N" /> from AbilityScores).
            var sorted = stat.Contributions
                .Select((c, i) => (c, i))
                .OrderBy(x => x.c.Level ?? -1)
                .ThenBy(x => x.i)
                .Select(x => x.c);

            foreach (var contrib in sorted)
            {
                var statadd = new XElement("statadd");

                if (contrib.Type is not null)
                    statadd.Add(new XAttribute("type", contrib.Type));

                if (!string.IsNullOrEmpty(contrib.StringPayload))
                    statadd.Add(new XAttribute("String", contrib.StringPayload));

                if (contrib.Level is { } lvl)
                    statadd.Add(new XAttribute("Level", lvl));

                if (!string.IsNullOrEmpty(contrib.Requires))
                    statadd.Add(new XAttribute("requires", contrib.Requires));

                if (!string.IsNullOrEmpty(contrib.Wearing))
                    statadd.Add(new XAttribute("wearing", contrib.Wearing));

                if (!string.IsNullOrEmpty(contrib.NotWearing))
                    statadd.Add(new XAttribute("not-wearing", contrib.NotWearing));

                if (!string.IsNullOrEmpty(contrib.Conditional))
                    statadd.Add(new XAttribute("conditional", contrib.Conditional));

                statadd.Add(new XAttribute("value", contrib.Value));

                if (!string.IsNullOrEmpty(contrib.StatLink))
                    statadd.Add(new XAttribute("statlink", contrib.StatLink));

                if (contrib.AbilMod)
                    statadd.Add(new XAttribute("abilmod", "true"));

                if (contrib.SourceId is not null)
                    statadd.Add(new XAttribute("charelem", GenerateCharelem(contrib.SourceId)));

                statEl.Add(statadd);
            }

            statBlock.Add(statEl);
        }

        // Append internal/display stats expected by the CB
        foreach (var internalName in InternalStats)
        {
            if (!data.Stats.ContainsKey(internalName))
            {
                var stat = new XElement("Stat",
                    new XAttribute("value", 0),
                    new XElement("alias", new XAttribute("name", internalName)));
                if (!InternalStatsNoFallbackStatadd.Contains(internalName))
                    stat.Add(new XElement("statadd", new XAttribute("value", 0)));
                statBlock.Add(stat);
            }
        }

        if (!statBlock.HasElements)
            statBlock.Add(new XText("\n"));

        return statBlock;
    }

    private static XElement BuildRulesElementTally(CharacterExportData data)
    {
        var tally = new XElement("RulesElementTally");

        foreach (var el in data.RulesElementTally)
        {
            // Prefer the source's verbatim per-occurrence legality (captured
            // first-wins from the flat tally row in document order) when we
            // have it. The HouseruledElementIds fallback is a file-wide flag
            // that fires when ANY occurrence is tagged houserule — but a
            // power retrained at higher level can carry legality="houserule"
            // on its swap node while the original tally row stays
            // legality="rules-legal" (e.g. ID_FMP_POWER_5207 Magic Weapon
            // in Artificer_Seeker.dnd4e). Fall back to the file-wide flag
            // only for elements we never saw in source (engine-granted /
            // synthesized rows that have no per-occurrence legality).
            string tallyLegality;
            if (el.InternalId is not null
                && data.SourceMetadata.TryGetValue(el.InternalId, out var legalityMeta)
                && !string.IsNullOrEmpty(legalityMeta.Legality))
            {
                tallyLegality = legalityMeta.Legality;
            }
            else
            {
                tallyLegality = el.InternalId is not null && data.HouseruledElementIds.Contains(el.InternalId)
                    ? "houserule"
                    : "rules-legal";
            }
            var re = CreateRulesElementXml(
                el.Name,
                el.Type,
                el.InternalId,
                el.Url,
                replaces: el.Replaces,
                legality: tallyLegality);

            if (el.Specifics is { Count: > 0 })
            {
                foreach (var (key, value) in el.Specifics)
                    re.Add(new XElement("specific", new XAttribute("name", key), value));
            }

            if (el.ExtraSpecifics is { Count: > 0 })
            {
                foreach (var (key, value) in el.ExtraSpecifics)
                    re.Add(new XElement("specific", new XAttribute("name", key), value));
            }

            tally.Add(re);
        }

        // Form C: Form A picks (descendants of UserEdit wrappers) re-emitted
        // verbatim into the tally with their captured charelem so cross-refs
        // with the UserEdit subtree stay intact.
        foreach (var hr in data.HouseruleFormATallyMirror)
            tally.Add(new XElement(hr));

        // Form B: legacy inline tally houserule rows re-emitted verbatim.
        foreach (var lb in data.HouseruleLegacyTallyRows)
            tally.Add(new XElement(lb));

        if (!tally.HasElements)
            tally.Add(new XText("\n"));

        return tally;
    }

    private static XElement BuildLootTally(CharacterExportData data)
    {
        var lootTally = new XElement("LootTally");

        foreach (var entry in data.LootTally)
        {
            var loot = new XElement("loot",
                new XAttribute("count", entry.Count));

            // Source files put Weight + name BEFORE equip-count when augmented.
            // We don't try to faithfully reorder; consumers that diff treat <loot>
            // as an unordered attribute bag.
            if (entry.Weight is { } w)
                loot.Add(new XAttribute("Weight",
                    w.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
            if (entry.CompositeName is { } cn)
                loot.Add(new XAttribute("name", cn));
            if (!string.IsNullOrWhiteSpace(entry.DamageOverride))
                loot.Add(new XAttribute("Damage", entry.DamageOverride));

            loot.Add(new XAttribute("equip-count", entry.EquipCount));
            loot.Add(new XAttribute("ShowPowerCard", entry.ShowPowerCard ? "1" : "0"));
            if (entry.IsInAlternateSlot)
                loot.Add(new XAttribute("_AlternateSlot", "1"));

            if (entry.AugmentXml is { } ax)
                loot.Add(new XAttribute("augment", ax));

            foreach (var component in entry.Components)
            {
                var item = component.Element;
                // RulesElement in LootTally must NOT be self-closing
                var re = CreateRulesElementXml(
                    item.Name,
                    item.Type,
                    item.InternalId,
                    item.Url,
                    legality: item.InternalId is not null && data.HouseruledElementIds.Contains(item.InternalId)
                        ? "houserule"
                        : "rules-legal");

                // Worn-state Category (only on the base when equipped).
                if (component.WornCategoryId is { } wid)
                {
                    var worn = CreateRulesElementXml(
                        WornCategoryName(wid),
                        "Category",
                        wid,
                        forceOpenClose: true);
                    re.Add(worn);
                }
                else if (item.Specifics is { Count: > 0 })
                {
                    foreach (var (key, value) in item.Specifics)
                        re.Add(new XElement("specific", new XAttribute("name", key), value));
                }

                // Cascaded grants (verbatim passthrough — Harper Pin
                // blessings, Echo of Ty'h'kadi Elemental Origin, etc.).
                foreach (var cascade in component.CascadedGrants)
                    re.Add(new XElement(cascade));

                if (!re.HasElements)
                    re.Add(new XText("\n")); // force open/close pair

                loot.Add(re);
            }

            lootTally.Add(loot);
        }

        if (!lootTally.HasElements)
            lootTally.Add(new XText("\n"));

        return lootTally;
    }

    /// <summary>
    /// Reconstruct the display name for a worn-state Category from its InternalId.
    /// E.g., <c>ID_INTERNAL_WOG_WEARING_OFF_HAND_LIGHT_BLADE</c> →
    /// <c>WearingOffHandLightBlade</c>. Used when re-emitting equipped loot.
    /// </summary>
    public static string WornCategoryName(string internalId)
    {
        // Strip the conventional ID_INTERNAL_WOG_ prefix and PascalCase the rest.
        const string prefix = "ID_INTERNAL_WOG_";
        var s = internalId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? internalId[prefix.Length..]
            : internalId;
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build the &lt;Companions&gt; block from structured CompanionData. Each
    /// companion becomes a &lt;Beast&gt; element with the OCB field schema:
    /// ID, AbilityScores, BeastPower(Text), Size, Speed, Defenses, HitPoints,
    /// Surges, AttackBonus, BasicAttack, TrainedSkills, Damage. Whitespace
    /// padding inside element text mirrors OCB's serialization habit.
    /// </summary>
    private static XElement BuildCompanions(CharacterExportData data)
    {
        var section = new XElement("Companions");
        foreach (var companion in data.Companions)
        {
            // Skip summons / minions / familiars — OCB only emits Beast entries for
            // the Beast Mastery / Animal Master full-stat companions. Familiars
            // belong in their own <Familiar> block, summons and minions don't
            // round-trip through the Companions section at all.
            if (companion.IsSummon || companion.IsMinion || companion.IsFamiliar) continue;

            var beast = new XElement("Beast");
            beast.Add(PaddedText("ID", FormatBeastId(companion, data.Level)));
            beast.Add(PaddedText("AbilityScores", FormatBeastAbilityScores(companion)));
            if (companion.IsPlaceholderForActiveBeast)
            {
                // Placeholder Beast block: shares identity + global stats with
                // the active companion but has no category-specific fields.
                // See GetCompanionData() for the OCB BeastBlock contract.
                beast.Add(PaddedText("BeastPower", string.Empty));
                beast.Add(PaddedText("BeastPowerText", string.Empty));
                beast.Add(PaddedText("Size", string.Empty));
                beast.Add(PaddedText("Speed", string.Empty));
                beast.Add(PaddedText("Defenses", FormatPlaceholderBeastDefenses(data.Level)));
                beast.Add(PaddedText("HitPoints", companion.HitPoints?.ToString() ?? string.Empty));
                beast.Add(PaddedText("Surges", PadOrEmpty(companion.HealingSurgeText)));
                beast.Add(PaddedText("AttackBonus", companion.AttackBonus?.ToString() ?? string.Empty));
                beast.Add(PaddedText("BasicAttack", FormatPlaceholderBeastBasicAttack(companion)));
                beast.Add(PaddedText("TrainedSkills", string.Empty));
                beast.Add(PaddedText("Damage", PadOrEmpty(companion.Damage)));
            }
            else
            {
                beast.Add(PaddedText("BeastPower", PadOrEmpty(companion.PowerName)));
                beast.Add(PaddedText("BeastPowerText", PadOrEmpty(companion.PowerText)));
                beast.Add(PaddedText("Size", PadOrEmpty(companion.Size)));
                beast.Add(PaddedText("Speed", PadOrEmpty(companion.Speed)));
                beast.Add(PaddedText("Defenses", FormatBeastDefenses(companion)));
                beast.Add(PaddedText("HitPoints", companion.HitPoints?.ToString() ?? string.Empty));
                beast.Add(PaddedText("Surges", PadOrEmpty(companion.HealingSurgeText)));
                beast.Add(PaddedText("AttackBonus", companion.AttackBonus?.ToString() ?? string.Empty));
                beast.Add(PaddedText("BasicAttack", FormatBeastBasicAttack(companion)));
                beast.Add(PaddedText("TrainedSkills", PadOrEmpty(string.Join(", ", companion.TrainedSkills))));
                beast.Add(PaddedText("Damage", PadOrEmpty(companion.Damage)));
            }
            section.Add(beast);
        }
        return section;
    }

    private static XElement PaddedText(string name, string value)
        => new(name, new XText(string.IsNullOrEmpty(value) ? "  " : $" {value} "));

    private static string PadOrEmpty(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

    private static string FormatBeastId(CompanionData companion, int charLevel)
    {
        // Sterling's cat: "Sheila - Level 1 A particularly ill tempered ocelot"
        // Sentinel's bear: "Level 5 Bear Companion"
        var name = companion.Name?.Trim();
        var appearance = companion.Appearance?.Trim();
        if (!string.IsNullOrEmpty(name))
            return string.IsNullOrEmpty(appearance)
                ? $"{name} - Level {charLevel} {companion.Category}"
                : $"{name} - Level {charLevel} {appearance}";

        // OCB's beast name is "Level N <BeastName>". For FMP mounts/animal
        // companions the Category is a bare creature noun ("Cat", "Bear"),
        // so OCB appends " Companion" → "Cat Companion". For the HoFK
        // Sentinel companions the element name already carries the suffix
        // ("Wolf Companion", "Bear Companion"), so appending again would
        // double it. Only append when the Category isn't already a
        // "... Companion" string.
        var category = companion.Category?.Trim() ?? string.Empty;
        if (category.EndsWith("Companion", StringComparison.OrdinalIgnoreCase))
            return $"Level {charLevel} {category}";
        return $"Level {charLevel} {category} Companion";
    }

    private static string FormatBeastAbilityScores(CompanionData companion)
        => $"Strength: {companion.Strength}, Constitution: {companion.Constitution}, "
         + $"Dexterity: {companion.Dexterity}, Intelligence: {companion.Intelligence}, "
         + $"Wisdom: {companion.Wisdom}, Charisma: {companion.Charisma}";

    private static string FormatBeastDefenses(CompanionData companion)
    {
        var parts = new List<string>();
        if (companion.Ac is { } ac) parts.Add($"AC {ac}");
        if (companion.Fortitude is { } fort) parts.Add($"Fortitude {fort}");
        if (companion.Reflex is { } refl) parts.Add($"Reflex {refl}");
        if (companion.Will is { } will) parts.Add($"Will {will}");
        return parts.Count > 0 ? string.Join(", ", parts) : (companion.DefensesText ?? string.Empty);
    }

    private static string FormatBeastBasicAttack(CompanionData companion)
    {
        // Sterling: "Claw; 5 vs. AC; 1d8 + Dexterity modifier damage."
        // OCB Beast block: "<attack>; <bonus> vs. AC; <damage> + <ability> modifier damage."
        if (string.IsNullOrWhiteSpace(companion.AttackText)) return string.Empty;
        var attackName = companion.AttackText.Trim();
        var bonus = companion.AttackBonus?.ToString() ?? "0";
        var damage = companion.Damage ?? string.Empty;
        var ability = companion.AttackAbility?.Trim() ?? string.Empty;
        return $"{attackName}; {bonus} vs. AC; {damage} + {ability} modifier damage.";
    }

    private static string FormatPlaceholderBeastDefenses(int level)
        // Per OCB BeastBlock.SetDefenses: AC/Fort/Reflex/Will all default to
        // `<category-field-as-int> + Companion.<Stat> + Level`. Placeholder
        // overlay elements have no Armor Class / Fortitude Defense / etc.
        // fields, and the Companion.AC / Companion.Fortitude / etc. stats do
        // not exist in any directive — so the result for placeholders is
        // just the character level repeated four times.
        => $"AC {level}, Fortitude {level}, Reflex {level}, Will {level}";

    private static string FormatPlaceholderBeastBasicAttack(CompanionData companion)
    {
        // Per OCB BeastBlock.SetBasicAttack on a placeholder category:
        //     "" + "; " + <attack bonus> + " vs. AC; " + <damage> + " + "
        //         + "" + " modifier damage."
        // The two empty interpolations (attack name and ability name) produce
        // the leading "; " and the double space before "modifier damage."
        // that the reference files preserve verbatim.
        var bonus = companion.AttackBonus?.ToString() ?? "0";
        var damage = companion.Damage ?? string.Empty;
        return $"; {bonus} vs. AC; {damage} +  modifier damage.";
    }

    private static XElement BuildPowerStats(CharacterExportData data)
    {
        var section = new XElement("PowerStats");
        foreach (var power in data.PowerStats)
        {
            var powerEl = new XElement("Power", new XAttribute("name", power.Name));
            foreach (var (key, value) in power.Specifics)
                powerEl.Add(new XElement("specific", new XAttribute("name", key), value));

            // Per-weapon blocks under the power. OCB emits one <Weapon> per
            // applicable weapon (including a synthesized "Unarmed" entry for
            // implement-only / empty-handed cases). Inner <RulesElement>
            // refs identify the base weapon + any magic-item layer; absent
            // for the synthetic Unarmed entry. Numeric children are emitted
            // with OCB's habitual space-padding around their text values.
            foreach (var weapon in power.Weapons)
            {
                var weaponEl = new XElement("Weapon", new XAttribute("name", weapon.Name));
                foreach (var comp in weapon.Components)
                {
                    var re = new XElement("RulesElement",
                        new XAttribute("name", comp.Name),
                        new XAttribute("type", comp.Type));
                    if (comp.InternalId is not null)
                    {
                        re.Add(new XAttribute("internal-id", comp.InternalId));
                        if (comp.Url is not null)
                            re.Add(new XAttribute("url", comp.Url));
                        re.Add(new XAttribute("charelem", GenerateCharelem(comp.InternalId)));
                    }
                    re.Add(new XAttribute("legality", "rules-legal"));
                    weaponEl.Add(re);
                }
                weaponEl.Add(new XElement("AttackBonus", $" {weapon.AttackBonus} "));
                weaponEl.Add(new XElement("Damage", $" {weapon.Damage} "));
                if (!string.IsNullOrWhiteSpace(weapon.DamageType))
                    weaponEl.Add(new XElement("DamageType", $" {weapon.DamageType} "));
                weaponEl.Add(new XElement("AttackStat", $" {weapon.AttackStat} "));
                weaponEl.Add(new XElement("Defense", $" {weapon.Defense} "));
                if (!string.IsNullOrWhiteSpace(weapon.Healing))
                    weaponEl.Add(new XElement("Healing", $" {weapon.Healing} "));
                weaponEl.Add(new XElement("HitComponents", $" {weapon.HitComponents} "));
                weaponEl.Add(new XElement("DamageComponents", $" {weapon.DamageComponents} "));
                if (!string.IsNullOrWhiteSpace(weapon.HealingComponents))
                    weaponEl.Add(new XElement("HealingComponents", $" {weapon.HealingComponents} "));
                // OCB omits <Conditions> entirely when there are no conditional
                // bonuses (e.g. Bull Rush / Grab on a vanilla character) -- it
                // does NOT emit an empty <Conditions> element. Match that to
                // avoid spurious "extra" diffs.
                if (!string.IsNullOrWhiteSpace(weapon.Conditions))
                    weaponEl.Add(new XElement("Conditions", $" {weapon.Conditions} "));
                powerEl.Add(weaponEl);
            }

            section.Add(powerEl);
        }
        if (!section.HasElements)
            section.Add(new XText("\n"));
        return section;
    }

    /// <summary>
    /// Build the Level tree sections from the CharacterElement hierarchy.
    /// Each Level element gets its own &lt;Level&gt; container.
    /// Non-Level children (Race, Class, user selections) are placed under
    /// the Level element they belong to (determined by their Level property).
    /// </summary>
    private static IEnumerable<XElement> BuildLevelTrees(
        CharacterElement root,
        IReadOnlySet<string>? userEditPickIds = null,
        IReadOnlyDictionary<string, ElementSourceMetadata>? sourceMetadata = null,
        IReadOnlySet<string>? houseruledIds = null,
        IReadOnlyDictionary<string, List<TallyElement>>? implicitChildren = null,
        IReadOnlyDictionary<int, XElement>? capturedLevelXml = null)
    {
        userEditPickIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sourceMetadata ??= new Dictionary<string, ElementSourceMetadata>(StringComparer.OrdinalIgnoreCase);
        houseruledIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        implicitChildren ??= new Dictionary<string, List<TallyElement>>(StringComparer.OrdinalIgnoreCase);
        capturedLevelXml ??= new Dictionary<int, XElement>();

        // OCB serializes proficiency cascades exactly ONCE in the LevelTree.
        // When a class's <Grants> block grants e.g. "Simple Melee", the engine
        // cascades all 24 weapon proficiencies it grants and nests them under
        // the Simple Melee RulesElement. If a SECOND class (typically a hybrid
        // pair) also grants Simple Melee, OCB emits the proficiency element
        // self-closing — it does NOT re-emit the weapon-proficiency children,
        // even though our engine tree contains them as a fresh cascade. Mirror
        // that behavior with a shared set tracking which Proficiency InternalIds
        // have already been emitted with non-empty children in this LevelTree.
        // See community-unfixed/Yumiko2-1.dnd4e for a representative hybrid
        // Bard/Ranger character: Simple Melee expanded under Hybrid Bard
        // Grants (L1369), self-closing under Hybrid Ranger Grants (L1431).
        var expandedProficiencyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Group children by their associated Level element
        var levelNodes = new List<CharacterElement>();
        var otherNodes = new List<CharacterElement>();

        foreach (var child in root.Children)
        {
            // UserEdit picks live in the engine tree (so cascade fires) but
            // are re-emitted via the verbatim <UserEdit> blocks; skip them
            // and their subtrees from the rebuilt level tree to avoid
            // double-emission.
            if (child.RulesElement?.InternalId is { } cid
                && userEditPickIds.Contains(cid))
            {
                continue;
            }

            if (child.RulesElement is not null
                && string.Equals(child.RulesElement.Type, "Level", StringComparison.OrdinalIgnoreCase))
            {
                levelNodes.Add(child);
            }
            else if (child.RulesElement is not null)
            {
                otherNodes.Add(child);
            }
        }

        if (levelNodes.Count == 0)
        {
            // Fallback: no Level elements found, emit everything in one container
            var container = new XElement("Level");
            foreach (var child in root.Children)
            {
                if (child.RulesElement?.InternalId is { } cid
                    && userEditPickIds.Contains(cid))
                {
                    continue;
                }

                if (child.RulesElement is not null)
                    container.Add(SerializeCharacterElement(child, userEditPickIds, sourceMetadata, houseruledIds, implicitChildren, expandedProficiencyIds));
            }
            if (!container.HasElements)
                container.Add(new XText("\n"));
            yield return container;
            yield break;
        }

        // Sort Level elements by their level number
        levelNodes.Sort((a, b) =>
        {
            int.TryParse(a.RulesElement!.Name, out var la);
            int.TryParse(b.RulesElement!.Name, out var lb);
            return la.CompareTo(lb);
        });

        // Build per-level containers
        for (int i = 0; i < levelNodes.Count; i++)
        {
            var levelNode = levelNodes[i];
            int.TryParse(levelNode.RulesElement!.Name, out var levelNum);
            int nextLevelNum = (i + 1 < levelNodes.Count)
                ? (int.TryParse(levelNodes[i + 1].RulesElement!.Name, out var nl) ? nl : int.MaxValue)
                : int.MaxValue;

            var container = new XElement("Level");

            // Captured-tree fast path: splice the verbatim source <RulesElement
            // type="Level"> as the inner element. Bypass engine rebuild plus
            // otherNodes injection — the captured tree already contains every
            // child in its source position. See CharacterExportData.CapturedLevelXml.
            if (capturedLevelXml.TryGetValue(levelNum, out var capturedInner))
            {
                container.Add(new XElement(capturedInner));
                yield return container;
                continue;
            }

            var levelXml = SerializeCharacterElement(levelNode, userEditPickIds, sourceMetadata, houseruledIds, implicitChildren, expandedProficiencyIds);

            // Add non-Level children that belong to this level range
            foreach (var other in otherNodes)
            {
                if (other.Level >= levelNum && other.Level < nextLevelNum)
                    levelXml.Add(SerializeCharacterElement(other, userEditPickIds, sourceMetadata, houseruledIds, implicitChildren, expandedProficiencyIds));
            }

            container.Add(levelXml);
            yield return container;
        }
    }

    /// <summary>
    /// Recursively serialize a CharacterElement into nested RulesElement XML,
    /// matching the grant chain hierarchy from the original CB.
    /// </summary>
    private static XElement SerializeCharacterElement(
        CharacterElement ce,
        IReadOnlySet<string>? userEditPickIds = null,
        IReadOnlyDictionary<string, ElementSourceMetadata>? sourceMetadata = null,
        IReadOnlySet<string>? houseruledIds = null,
        IReadOnlyDictionary<string, List<TallyElement>>? implicitChildren = null,
        HashSet<string>? expandedProficiencyIds = null,
        string? parentType = null)
    {
        userEditPickIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sourceMetadata ??= new Dictionary<string, ElementSourceMetadata>(StringComparer.OrdinalIgnoreCase);
        houseruledIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        implicitChildren ??= new Dictionary<string, List<TallyElement>>(StringComparer.OrdinalIgnoreCase);
        expandedProficiencyIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var re = ce.RulesElement!;
        // Prefer the source's recorded name if we have one — keeps round-trip
        // stable when the rules-DB has renamed the element between content
        // versions. See ElementSourceMetadata.Name for the full rationale.
        string elementName = re.Name;
        if (re.InternalId is not null
            && sourceMetadata.TryGetValue(re.InternalId, out var nameMeta)
            && !string.IsNullOrEmpty(nameMeta.Name))
        {
            elementName = nameMeta.Name;
        }

        string? url = null;
        if (re.InternalId is not null
            && sourceMetadata.TryGetValue(re.InternalId, out var meta)
            && !string.IsNullOrWhiteSpace(meta.Url))
        {
            url = meta.Url;
        }

        var xml = CreateRulesElementXml(
            elementName,
            re.Type,
            re.InternalId,
            url,
            replaces: ce.ReplacesCharelem,
            legality: re.InternalId is not null && houseruledIds.Contains(re.InternalId)
                ? "houserule"
                : "rules-legal");

        // OCB cascade dedup: when two class-level <Grants> blocks both grant
        // the same Proficiency category (e.g. a hybrid Bard/Ranger character
        // where each Grants block includes Simple Melee), OCB expands the
        // weapon-proficiency cascade only on the FIRST occurrence; the second
        // emits self-closing. This dedup only applies when BOTH occurrences
        // are under Grants parents — proficiency cascades originating from a
        // Class Feature (e.g. Paladin Armor Proficiency granted by the
        // Hybrid Talent feat) get fully expanded on EVERY occurrence, even
        // when a class-level Grants block already expanded the same prof.
        // See community-unfixed/Yumiko2-1.dnd4e (hybrid dedup case) and
        // community-unfixed/1-ArdentBlackguard-Feystep.dnd4e (Class Feature
        // re-expansion case) for representative examples.
        bool isProficiency = string.Equals(re.Type, "Proficiency", StringComparison.OrdinalIgnoreCase);
        bool parentIsGrants = string.Equals(parentType, "Grants", StringComparison.OrdinalIgnoreCase);
        bool suppressChildren = false;
        if (isProficiency && parentIsGrants && !string.IsNullOrEmpty(re.InternalId))
        {
            if (expandedProficiencyIds.Contains(re.InternalId))
            {
                suppressChildren = true;
            }
            else if (ce.Children.Any(c => c.RulesElement is not null)
                || (implicitChildren.TryGetValue(re.InternalId, out var implicitsForCheck)
                    && implicitsForCheck.Count > 0))
            {
                expandedProficiencyIds.Add(re.InternalId);
            }
        }

        if (!suppressChildren)
        {
            // Recurse into children — include empty choice placeholders, but
            // skip UserEdit-pick descendants (re-emitted verbatim elsewhere).
            foreach (var child in ce.Children)
            {
                if (child.RulesElement?.InternalId is { } cid
                    && userEditPickIds.Contains(cid))
                {
                    continue;
                }

                if (child.RulesElement is not null)
                {
                    xml.Add(SerializeCharacterElement(child, userEditPickIds, sourceMetadata, houseruledIds, implicitChildren, expandedProficiencyIds, re.Type));
                }
                else
                {
                    // Unfilled select slot — emit as empty placeholder
                    xml.Add(new XElement("RulesElement",
                        new XAttribute("name", ""),
                        new XAttribute("type", ""),
                        new XAttribute("charelem", "deadbeef"),
                        new XAttribute("legality", "rules-legal"),
                        new XText("\n")));
                }
            }

            // OCB-cascade special case: certain parent elements (e.g.
            // Implement Proficiency (Proficient Weapons)) emit ALL their canonical
            // GrantDirective targets in the LevelTree, bypassing the per-grant
            // Requires gate. The flat RulesElementTally still respects Requires.
            // See docs/engine-special-cases.md §11.
            if (re.InternalId is not null
                && implicitChildren.TryGetValue(re.InternalId, out var implicits)
                && implicits.Count > 0)
            {
                foreach (var implicitChild in implicits)
                {
                    var implicitXml = new XElement("RulesElement",
                        new XAttribute("name", implicitChild.Name),
                        new XAttribute("type", implicitChild.Type));
                    if (implicitChild.InternalId is not null)
                    {
                        implicitXml.Add(new XAttribute("internal-id", implicitChild.InternalId));
                        implicitXml.Add(new XAttribute("charelem", GenerateCharelem(implicitChild.InternalId)));
                    }
                    implicitXml.Add(new XAttribute("legality", "rules-legal"));
                    xml.Add(implicitXml);
                }
            }
        }

        return xml;
    }

    /// <summary>
    /// Generate a stable charelem value from an internal ID.
    /// The original CB uses memory addresses; we use a hash substring
    /// for reproducibility across saves.
    /// </summary>
    public static string GenerateCharelem(string internalId)
    {
        return CharelemCache.GetOrAdd(internalId, static id =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        });
    }
}
