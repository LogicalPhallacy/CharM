using System.Globalization;
using System.Text.RegularExpressions;
using CharM.Engine.Creation;
using CharM.Engine.Evaluation;
using CharM.Engine.Rules;

namespace CharM.Engine.CharacterModel;

/// <summary>
/// Live, computed companion data assembled from session state for UI display.
/// Sourced from the active base Companion element's static fields, the matching
/// <c>Companion: X</c> Power card's overlaid level-current fields, the
/// aggregated <c>Companion.*</c> ability stats, and the OCB
/// <c>_COMPANION_NAME</c> / <c>_COMPANION_APPEARANCE</c> text strings.
/// </summary>
public sealed record CompanionData(
    string Category,
    string? Name,
    string? Appearance,
    int Strength,
    int Constitution,
    int Dexterity,
    int Intelligence,
    int Wisdom,
    int Charisma,
    int? Ac,
    int? Fortitude,
    int? Reflex,
    int? Will,
    string? DefensesText,
    int? HitPoints,
    string? HitPointsText,
    string? HitPointsNote,
    string? HealingSurgeText,
    int? AttackBonus,
    string? AttackText,
    string? Damage,
    string? Size,
    string? Speed,
    string? Vision,
    IReadOnlyList<string> TrainedSkills,
    string? PowerName,
    string? PowerText,
    IReadOnlyList<CompanionExtraPower> ExtraPowers,
    bool IsMinion,
    bool IsSummon,
    string? AnchorPowerInternalId,
    bool IsFamiliar = false,
    bool IsPlaceholderForActiveBeast = false,
    string? AttackAbility = null)
{
    /// <summary>
    /// Build a <see cref="CompanionData"/> from a base Companion element + its
    /// matching <c>Companion: X</c> Power card.
    /// </summary>
    public static CompanionData From(
        RulesElement baseCompanion,
        RulesElement? powerCard,
        CharacterSnapshot snapshot,
        int characterLevel,
        string? customName,
        string? customAppearance,
        Func<string, RulesElement?>? findById = null)
    {
        var overlay = snapshot.Builder.Overlay;
        var stats = snapshot.Builder.Stats;

        int Score(string ability) =>
            stats?.TryGetStat($"Companion.{ability}")?.ComputeValue(stats)
                ?? ParseInt(baseCompanion.Fields.GetValueOrDefault(ability))
                ?? 10;

        var defenses = ParseDefenses(GetOverlaidField(powerCard, overlay, "Defenses"));
        if (defenses.IsEmpty)
        {
            defenses = new ParsedDefenses(
                ParseInt(baseCompanion.Fields.GetValueOrDefault("Armor Class")),
                ParseInt(baseCompanion.Fields.GetValueOrDefault("Fortitude Defense")),
                ParseInt(baseCompanion.Fields.GetValueOrDefault("Reflex Defense")),
                ParseInt(baseCompanion.Fields.GetValueOrDefault("Will Defense")));
        }

        int? hp = ParseInt(GetOverlaidField(powerCard, overlay, "Hit Points"))
            ?? ParseInt(baseCompanion.Fields.GetValueOrDefault("Hit Points at 1st Level"));
        string? surge = StripPrefix(GetOverlaidField(powerCard, overlay, "Healing Surge Value"),
            "Healing Surge Value:");
        // OCB lower-cases "Surges per day" when rendering the Beast block,
        // even though the rules-DB stores it capitalized in the Healing
        // Surge Value field. Mirror that for parity (Sterling pair).
        if (surge is not null)
            surge = surge.Replace(" Surges per day", " surges per day", StringComparison.Ordinal);

        return new CompanionData(
            Category: baseCompanion.Name,
            Name: NullIfBlank(customName),
            Appearance: NullIfBlank(customAppearance),
            Strength: Score("Strength"),
            Constitution: Score("Constitution"),
            Dexterity: Score("Dexterity"),
            Intelligence: Score("Intelligence"),
            Wisdom: Score("Wisdom"),
            Charisma: Score("Charisma"),
            Ac: defenses.Ac,
            Fortitude: defenses.Fortitude,
            Reflex: defenses.Reflex,
            Will: defenses.Will,
            DefensesText: null,
            HitPoints: hp,
            HitPointsText: null,
            HitPointsNote: null,
            HealingSurgeText: surge,
            AttackBonus: ParseInt(baseCompanion.Fields.GetValueOrDefault("Attack Bonus")),
            // OCB's Beast block reads the attack-name (e.g. "Claw") from the
            // BASE companion's "Attack" field — not from the powerCard's
            // overlaid "Attack" field, which holds the verbose "Beast's
            // attack bonus vs. AC" display string used inside the power
            // card itself. Prefer base; fall back to overlay only when base
            // is blank.
            AttackText: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Attack")
                ?? GetOverlaidField(powerCard, overlay, "Attack")),
            Damage: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Damage")
                ?? GetOverlaidField(powerCard, overlay, "Damage")),
            Size: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Size")),
            Speed: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Speed")),
            Vision: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Vision")
                ?? baseCompanion.Fields.GetValueOrDefault("Senses")),
            TrainedSkills: ResolveTrainedSkillNames(baseCompanion.Fields.GetValueOrDefault("Trained Skills"), findById),
            PowerName: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Companion Power")),
            PowerText: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Power")),
            ExtraPowers: Array.Empty<CompanionExtraPower>(),
            IsMinion: false,
            IsSummon: false,
            AnchorPowerInternalId: powerCard?.InternalId,
            AttackAbility: NullIfBlank(baseCompanion.Fields.GetValueOrDefault("Ability Score")));
    }

    /// <summary>
    /// Build a <see cref="CompanionData"/> from an Animal Master's Companion
    /// minion power. These powers store all stats inline in fields like
    /// "Defenses", "Hit Points", "Skills" (which contains ability scores in
    /// multi-line text) and "Statistics".
    /// </summary>
    public static CompanionData FromAnimalCompanionPower(
        RulesElement powerCard,
        ModifyOverlay? overlay,
        int characterLevel,
        string? customName,
        string? customAppearance)
    {
        var defenses = ParseDefenses(GetOverlaidField(powerCard, overlay, "Defenses"));

        var rawHp = GetOverlaidField(powerCard, overlay, "Hit Points");
        int? hp = null;
        string? hpNote = null;
        if (!string.IsNullOrWhiteSpace(rawHp))
        {
            int sep = rawHp.IndexOf(';');
            string hpPart = sep > 0 ? rawHp[..sep].Trim() : rawHp.Trim();
            hp = ParseInt(hpPart);
            if (sep > 0) hpNote = rawHp[(sep + 1)..].Trim();
        }

        var skillsField = GetOverlaidField(powerCard, overlay, "Skills") ?? string.Empty;
        var (skillNames, abilities) = ParseAnimalCompanionSkillsField(skillsField);

        var statsField = GetOverlaidField(powerCard, overlay, "Statistics") ?? string.Empty;
        var (size, speed, vision) = ParseAnimalCompanionStatistics(statsField);

        string category = ExtractAnimalCompanionCategory(powerCard.Name);
        var extraPowers = ParseExtraPowers(powerCard);

        return new CompanionData(
            Category: category,
            Name: NullIfBlank(customName),
            Appearance: NullIfBlank(customAppearance),
            Strength: abilities.GetValueOrDefault("Strength", 10),
            Constitution: abilities.GetValueOrDefault("Constitution", 10),
            Dexterity: abilities.GetValueOrDefault("Dexterity", 10),
            Intelligence: abilities.GetValueOrDefault("Intelligence", 10),
            Wisdom: abilities.GetValueOrDefault("Wisdom", 10),
            Charisma: abilities.GetValueOrDefault("Charisma", 10),
            Ac: defenses.Ac,
            Fortitude: defenses.Fortitude,
            Reflex: defenses.Reflex,
            Will: defenses.Will,
            DefensesText: defenses.IsEmpty ? NullIfBlank(GetOverlaidField(powerCard, overlay, "Defenses")) : null,
            HitPoints: hp,
            HitPointsText: hp is null ? NullIfBlank(rawHp) : null,
            HitPointsNote: hpNote,
            HealingSurgeText: null,
            AttackBonus: null,
            AttackText: null,
            Damage: null,
            Size: size,
            Speed: speed,
            Vision: vision,
            TrainedSkills: skillNames,
            PowerName: null,
            PowerText: null,
            ExtraPowers: extraPowers,
            IsMinion: true,
            IsSummon: false,
            AnchorPowerInternalId: powerCard.InternalId);
    }

    /// <summary>
    /// Build a <see cref="CompanionData"/> from a summoning power that includes
    /// a summoned creature stat block (Hit Points + Defenses fields plus
    /// action-typed fields like "Standard Action", "Move Action", "Immediate
    /// Interrupt", etc.). Differs from animal companions in two ways:
    /// (1) HP/Defenses values are often descriptive ("(your healing surge value)",
    /// "Equal to yours") rather than numeric — surface them via *Text fields;
    /// (2) named action fields use 4e action-type names instead of bare
    /// parenthetical action tags.
    /// </summary>
    public static CompanionData FromSummonPower(
        RulesElement powerCard,
        ModifyOverlay? overlay,
        int characterLevel)
    {
        var defenses = ParseDefenses(GetOverlaidField(powerCard, overlay, "Defenses"));
        string? defensesText = defenses.IsEmpty
            ? NullIfBlank(GetOverlaidField(powerCard, overlay, "Defenses"))
            : null;

        var rawHp = GetOverlaidField(powerCard, overlay, "Hit Points");
        int? hp = null;
        string? hpNote = null;
        string? hpText = null;
        if (!string.IsNullOrWhiteSpace(rawHp))
        {
            int sep = rawHp.IndexOf(';');
            string hpPart = sep > 0 ? rawHp[..sep].Trim() : rawHp.Trim();
            hp = ParseInt(hpPart);
            if (sep > 0) hpNote = rawHp[(sep + 1)..].Trim();
            if (hp is null) hpText = NullIfBlank(rawHp);
        }

        var statsField = GetOverlaidField(powerCard, overlay, "Statistics") ?? string.Empty;
        var (size, speed, vision) = ParseAnimalCompanionStatistics(statsField);

        // Summons sometimes have a "Healing Surges" field with a textual value
        string? surgeText = NullIfBlank(GetOverlaidField(powerCard, overlay, "Healing Surges"))
            ?? NullIfBlank(GetOverlaidField(powerCard, overlay, "Healing Surge Value"));

        // Use the power's own name (without "Summoned" / "Summon" prefix) as category.
        string category = ExtractSummonCategory(powerCard.Name);
        var extraPowers = ParseExtraPowers(powerCard);

        return new CompanionData(
            Category: category,
            Name: null,
            Appearance: null,
            Strength: 10,
            Constitution: 10,
            Dexterity: 10,
            Intelligence: 10,
            Wisdom: 10,
            Charisma: 10,
            Ac: defenses.Ac,
            Fortitude: defenses.Fortitude,
            Reflex: defenses.Reflex,
            Will: defenses.Will,
            DefensesText: defensesText,
            HitPoints: hp,
            HitPointsText: hpText,
            HitPointsNote: hpNote,
            HealingSurgeText: surgeText,
            AttackBonus: null,
            AttackText: null,
            Damage: null,
            Size: size,
            Speed: speed,
            Vision: vision,
            TrainedSkills: Array.Empty<string>(),
            PowerName: null,
            PowerText: null,
            ExtraPowers: extraPowers,
            IsMinion: false,
            IsSummon: true,
            AnchorPowerInternalId: powerCard.InternalId);
    }

    private static string ExtractSummonCategory(string powerName)
    {
        // "Summon Angel of Fire" → "Angel of Fire"
        // "Summoned Sidhe Ally" → "Sidhe Ally"
        var prefixes = new[] { "Summoned ", "Summon " };
        foreach (var prefix in prefixes)
        {
            if (powerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return powerName[prefix.Length..].Trim();
        }
        return powerName;
    }

    /// <summary>
    /// True for the OCB-shipped familiar power cards (e.g. Disembodied Hand,
    /// Pseudodragon, Owl). Familiars are granted by the Arcane Familiar feat
    /// (or similar) and the familiar's <c>&lt;Familiar&gt;</c> RulesElement
    /// grants a <c>Power</c>-typed RulesElement that carries the familiar's
    /// stat block in its Fields.
    ///
    /// <para>Detection prefers the internal-id family (Tivaan's
    /// <c>ID_TIV_FAMILIAR-*</c> and the LFR <c>ID_LFR_FAMILIAR-*</c> set
    /// account for nearly every shipping familiar power), then falls back to
    /// the <c>"Familiar:"</c> name prefix and finally the
    /// (Constant Benefits + Secondary Speed) field signature unique to
    /// familiar power cards.</para>
    /// </summary>
    public static bool IsFamiliarPower(RulesElement element)
    {
        if (!string.Equals(element.Type, "Power", StringComparison.OrdinalIgnoreCase))
            return false;
        if (element.InternalId is { } iid
            && (iid.Contains("_FAMILIAR-", StringComparison.OrdinalIgnoreCase)
                || iid.Contains("_FAMILIAR_", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (element.Name.StartsWith("Familiar:", StringComparison.OrdinalIgnoreCase))
            return true;
        return element.Fields.ContainsKey("Constant Benefits")
               && element.Fields.ContainsKey("Secondary Speed");
    }

    /// <summary>
    /// Build a <see cref="CompanionData"/> for a familiar power card. Familiars
    /// don't carry ability scores or defenses (the wizard's own stats stand in
    /// during play), so the mini-sheet hides those rows. Speed comes from the
    /// power's <c>Speed</c> field (with optional <c>Secondary Speed</c>); the
    /// at-will familiar action is exposed as a single <see cref="ExtraPower"/>
    /// entry built from the named-action field on the power.
    /// </summary>
    public static CompanionData FromFamiliarPower(RulesElement powerCard)
    {
        string category = powerCard.Name.StartsWith("Familiar:", StringComparison.OrdinalIgnoreCase)
            ? powerCard.Name["Familiar:".Length..].Trim()
            : powerCard.Name;

        var speed = ComposeFamiliarSpeed(powerCard);
        var senses = NullIfBlank(powerCard.Fields.GetValueOrDefault("Senses"));
        var size = NullIfBlank(powerCard.Fields.GetValueOrDefault("Size"));
        var info = NullIfBlank(powerCard.Fields.GetValueOrDefault("Information"));
        var benefits = NullIfBlank(powerCard.Fields.GetValueOrDefault("Constant Benefits"));

        var extraPowers = new List<CompanionExtraPower>();

        // Constant Benefits is the always-on rider that the familiar grants
        // its master. Surface it as the leading "extra power" entry so the
        // mini-sheet shows it prominently.
        if (benefits is not null)
        {
            extraPowers.Add(new CompanionExtraPower(
                Name: "Constant Benefits",
                Action: null,
                Description: benefits));
        }

        // The familiar's at-will action lives in a non-standard field whose
        // name is the action's display title (e.g. "Agile Digits"). Skip the
        // standard descriptive fields and expose the rest as the action card.
        string? actionType = NullIfBlank(powerCard.Fields.GetValueOrDefault("Action Type"));
        foreach (var (key, value) in powerCard.Fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (IsFamiliarStockField(key)) continue;
            extraPowers.Add(new CompanionExtraPower(
                Name: key,
                Action: actionType,
                Description: value.Trim()));
        }

        if (info is not null)
        {
            extraPowers.Add(new CompanionExtraPower(
                Name: "Description",
                Action: null,
                Description: info));
        }

        return new CompanionData(
            Category: category,
            Name: null,
            Appearance: null,
            Strength: 10, Constitution: 10, Dexterity: 10,
            Intelligence: 10, Wisdom: 10, Charisma: 10,
            Ac: null, Fortitude: null, Reflex: null, Will: null,
            DefensesText: null,
            HitPoints: null,
            HitPointsText: null,
            HitPointsNote: null,
            HealingSurgeText: null,
            AttackBonus: null,
            AttackText: null,
            Damage: null,
            Size: size,
            Speed: speed,
            Vision: senses,
            TrainedSkills: Array.Empty<string>(),
            PowerName: null,
            PowerText: null,
            ExtraPowers: extraPowers,
            IsMinion: false,
            IsSummon: false,
            AnchorPowerInternalId: powerCard.InternalId,
            IsFamiliar: true);
    }

    /// <summary>
    /// Build a <see cref="CompanionData"/> for a <c>type="Associate"</c>
    /// inventory entry. Sentinel druid companions (Living Zephyr, Flame
    /// Zephyr, etc.) and similar "I bring this creature along" picks
    /// from Heroes of the Elemental Chaos / Feywild ship as
    /// <c>&lt;RulesElement type="Associate"&gt;</c> inside
    /// <c>&lt;LootTally&gt;</c>, NOT as <c>type="Companion"</c> elements
    /// like the Hunter / Druid Cat-Wolf-Bear family. They typically
    /// pair with an identically-named <c>Power</c> element that carries
    /// the structured stat block (Ability Scores, Defenses, Hit Points,
    /// Speed, named action fields). When that Power is active on the
    /// character we prefer it for the structured fields; otherwise we
    /// fall back to parsing the Associate's free-text Description blob.
    /// </summary>
    public static CompanionData FromAssociate(
        RulesElement associate,
        RulesElement? powerCard,
        ModifyOverlay? overlay,
        string? customName,
        string? customAppearance)
    {
        // Prefer the Power card's structured "Defenses" / "Hit Points"
        // fields. Both can carry descriptive (rather than numeric) text
        // (e.g. "Your bloodied value", "Add your level to each defense");
        // in that case ParseDefenses returns Empty and we surface the
        // raw text via DefensesText / HitPointsText.
        string? defensesField = GetOverlaidField(powerCard, overlay, "Defenses");
        var defenses = ParseDefenses(defensesField);

        string? rawHp = GetOverlaidField(powerCard, overlay, "Hit Points");
        int? hp = ParseInt(rawHp);
        string? hpText = hp is null ? NullIfBlank(rawHp) : null;

        var abilities = ParseAbilityScores(
            GetOverlaidField(powerCard, overlay, "Ability Scores")
            ?? associate.Fields.GetValueOrDefault("Description"));

        string? speed = NullIfBlank(GetOverlaidField(powerCard, overlay, "Speed"));
        string? size = ExtractCreatureSize(
            GetOverlaidField(powerCard, overlay, "Summoned Creature")
            ?? GetOverlaidField(powerCard, overlay, "Size"));
        string? vision = NullIfBlank(
            GetOverlaidField(powerCard, overlay, "Perception")
            ?? GetOverlaidField(powerCard, overlay, "Senses"));

        // Pick the first attack-style field as the headline attack line.
        // Associate stat blocks use names like "Animal Attack (At-Will)",
        // "Standard Action (At-Will * Fire)", etc.; ParseExtraPowers
        // picks them all up but we want the first one inline on the
        // mini-sheet too.
        string? attackText = null;
        var extraPowers = powerCard is not null
            ? ParseExtraPowers(powerCard)
            : Array.Empty<CompanionExtraPower>();
        if (extraPowers.Count > 0)
        {
            attackText = extraPowers[0].Name;
        }

        return new CompanionData(
            Category: associate.Name,
            Name: NullIfBlank(customName),
            Appearance: NullIfBlank(customAppearance),
            Strength: abilities.GetValueOrDefault("Strength", 10),
            Constitution: abilities.GetValueOrDefault("Constitution", 10),
            Dexterity: abilities.GetValueOrDefault("Dexterity", 10),
            Intelligence: abilities.GetValueOrDefault("Intelligence", 10),
            Wisdom: abilities.GetValueOrDefault("Wisdom", 10),
            Charisma: abilities.GetValueOrDefault("Charisma", 10),
            Ac: defenses.Ac,
            Fortitude: defenses.Fortitude,
            Reflex: defenses.Reflex,
            Will: defenses.Will,
            DefensesText: defenses.IsEmpty ? NullIfBlank(defensesField) : null,
            HitPoints: hp,
            HitPointsText: hpText,
            HitPointsNote: null,
            HealingSurgeText: null,
            AttackBonus: null,
            AttackText: attackText,
            Damage: null,
            Size: size,
            Speed: speed,
            Vision: vision,
            TrainedSkills: Array.Empty<string>(),
            PowerName: null,
            PowerText: powerCard is null
                ? NullIfBlank(associate.Fields.GetValueOrDefault("Description"))
                : null,
            ExtraPowers: extraPowers,
            IsMinion: false,
            IsSummon: false,
            AnchorPowerInternalId: powerCard?.InternalId);
    }

    private static Dictionary<string, int> ParseAbilityScores(string? source)
    {
        var abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(source))
            return abilities;

        foreach (Match m in AbilityPattern.Matches(source))
        {
            string abbr = m.Groups[1].Value;
            if (int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int score))
                abilities[ExpandAbilityName(abbr)] = score;
        }
        return abilities;
    }

    private static string? ExtractCreatureSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sizes = new[] { "Tiny", "Small", "Medium", "Large", "Huge", "Gargantuan" };
        foreach (var size in sizes)
        {
            if (text.Contains(size, StringComparison.OrdinalIgnoreCase))
                return text.Trim();
        }
        return null;
    }

    private static string? ComposeFamiliarSpeed(RulesElement power)
    {
        var primary = power.Fields.GetValueOrDefault("Speed");
        var secondary = power.Fields.GetValueOrDefault("Secondary Speed");
        if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(secondary)) return null;
        if (string.IsNullOrWhiteSpace(secondary)) return primary?.Trim();
        if (string.IsNullOrWhiteSpace(primary)) return secondary.Trim();
        return $"{primary.Trim()} ({secondary.Trim()})";
    }

    private static bool IsFamiliarStockField(string key)
        => key.Equals("Power Usage", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Action Type", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Constant Benefits", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Information", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Speed", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Secondary Speed", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Senses", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Size", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Tier", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Description", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Short Description", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Flavor", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Prerequisite", StringComparison.OrdinalIgnoreCase);

    private static string? GetOverlaidField(RulesElement? element, ModifyOverlay? overlay, string fieldName)
    {
        if (element is null) return null;
        if (overlay is not null)
            return overlay.GetField(element, fieldName);
        return element.Fields.GetValueOrDefault(fieldName);
    }

    private static int? ParseInt(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? NullIfBlank(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? StripPrefix(string? text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[prefix.Length..].Trim();
        return trimmed;
    }

    private static ParsedDefenses ParseDefenses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParsedDefenses.Empty;
        int? ac = null, fort = null, refl = null, will = null;
        foreach (var part in text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            int spaceIdx = trimmed.LastIndexOf(' ');
            if (spaceIdx <= 0) continue;
            var name = trimmed[..spaceIdx].Trim();
            var value = ParseInt(trimmed[(spaceIdx + 1)..]);
            if (value is null) continue;
            switch (name.ToLowerInvariant())
            {
                case "ac": ac = value; break;
                case "fortitude" or "fort": fort = value; break;
                case "reflex" or "ref": refl = value; break;
                case "will": will = value; break;
            }
        }
        return new ParsedDefenses(ac, fort, refl, will);
    }

    private static IReadOnlyList<string> ResolveTrainedSkillNames(string? raw, Func<string, RulesElement?>? findById)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            string resolved = id;
            if (findById is not null && id.StartsWith("ID_", StringComparison.OrdinalIgnoreCase))
            {
                var el = findById(id);
                if (el is not null) resolved = el.Name;
            }
            names.Add(resolved);
        }
        return names;
    }

    private static (IReadOnlyList<string> Skills, Dictionary<string, int> Abilities) ParseAnimalCompanionSkillsField(string text)
    {
        var skillLines = new List<string>();
        var abilities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
            return (skillLines, abilities);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var matches = AbilityPattern.Matches(line);
            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    string abbr = m.Groups[1].Value;
                    int score = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    abilities[ExpandAbilityName(abbr)] = score;
                }
                continue;
            }

            if (line.StartsWith("Alignment", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Languages", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var entry in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                skillLines.Add(entry);
        }

        return (skillLines, abilities);
    }

    private static (string? Size, string? Speed, string? Vision) ParseAnimalCompanionStatistics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null, null);

        string? size = null, speed = null, vision = null;
        var firstLine = text.Split('\n')[0];
        var parts = firstLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var speedParts = new List<string>();
        foreach (var part in parts)
        {
            int colon = part.IndexOf(':');
            if (colon > 0)
            {
                var key = part[..colon].Trim();
                var value = part[(colon + 1)..].Trim();
                if (key.Equals("Speed", StringComparison.OrdinalIgnoreCase))
                    speedParts.Insert(0, value);
                else if (key.Equals("Senses", StringComparison.OrdinalIgnoreCase))
                    vision = value;
                else
                    speedParts.Add($"{key} {value}");
            }
            else if (size is null && (part.Contains("Tiny", StringComparison.OrdinalIgnoreCase)
                || part.Contains("Small", StringComparison.OrdinalIgnoreCase)
                || part.Contains("Medium", StringComparison.OrdinalIgnoreCase)
                || part.Contains("Large", StringComparison.OrdinalIgnoreCase)))
            {
                size = part.Trim();
            }
            else if (part.Contains("vision", StringComparison.OrdinalIgnoreCase))
            {
                vision = part.Trim();
            }
            else
            {
                speedParts.Add(part);
            }
        }
        if (speedParts.Count > 0) speed = string.Join(", ", speedParts);

        return (size, speed, vision);
    }

    private static string ExtractAnimalCompanionCategory(string powerName)
    {
        int colonIdx = powerName.LastIndexOf(':');
        if (colonIdx >= 0 && colonIdx < powerName.Length - 1)
            return powerName[(colonIdx + 1)..].Trim();
        return powerName;
    }

    private static IReadOnlyList<CompanionExtraPower> ParseExtraPowers(RulesElement powerCard)
    {
        var skipFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Animal Companion", "Defenses", "Hit Points", "Hit Points at 1st Level",
            "Hit Points per Level Gained", "Skills", "Statistics",
            "Power Usage", "Action Type", "Attack Type", "Keywords", "Target",
            "Hit", "Effect", "Miss", "Special", "Sustain", "Trigger", "Requirement",
            "Prerequisite", "Short Description", "Description", "Flavor", "Display",
            "Range", "Healing", "Healing Surge Value", "Healing Surge Uses",
            "Healing Surges", "Attack", "Damage", "Damage Type", "Class", "Level",
            "Power Type", "Companion Power", "Power", "Channel Divinity",
            "Summoned Companion", "Senses",
        };

        // Action-typed field names commonly used by summoning powers and
        // companion stat blocks for named powers/abilities.
        var actionFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Standard Action", "Move Action", "Minor Action", "Free Action",
            "Immediate Interrupt", "Immediate Reaction", "Opportunity Attack",
            "No Action", "Standard action", "Move action", "Minor action",
        };

        var extras = new List<CompanionExtraPower>();
        foreach (var (name, value) in powerCard.Fields)
        {
            if (skipFields.Contains(name)) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;
            // Normalize: skip leading-space sub-attack fields (handled by power
            // card sub-attack rendering, not by the mini-sheet).
            if (name.StartsWith(" ", StringComparison.Ordinal)) continue;

            string trimmedName = name.Trim();
            string firstLine = value.Split('\n')[0].TrimStart();
            bool isActionField = actionFieldNames.Contains(trimmedName);
            bool isParenAction = firstLine.StartsWith("(", StringComparison.Ordinal);
            if (!isActionField && !isParenAction) continue;

            string? action;
            string body;
            if (isParenAction)
            {
                int closeIdx = firstLine.IndexOf(')');
                action = closeIdx > 0 ? firstLine[1..closeIdx].Trim() : null;
                int newlineIdx = value.IndexOf('\n');
                body = newlineIdx >= 0
                    ? value[(newlineIdx + 1)..].Trim()
                    : (closeIdx > 0 && closeIdx < firstLine.Length - 1 ? firstLine[(closeIdx + 1)..].Trim() : "");
            }
            else
            {
                // For action-type fields, the field name IS the action and the
                // value's first line may contain a usage tag or extra qualifier.
                action = trimmedName;
                body = value.Trim();
            }
            extras.Add(new CompanionExtraPower(isParenAction ? trimmedName : trimmedName, action, body));
        }

        return extras;
    }

    private static string ExpandAbilityName(string abbr) => abbr.ToUpperInvariant() switch
    {
        "STR" => "Strength",
        "CON" => "Constitution",
        "DEX" => "Dexterity",
        "INT" => "Intelligence",
        "WIS" => "Wisdom",
        "CHA" => "Charisma",
        _ => abbr,
    };

    private static readonly Regex AbilityPattern = new(
        @"\b(Str|Con|Dex|Int|Wis|Cha):?\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Compute the ability modifier (4e: floor((score - 10) / 2)).</summary>
    public int Mod(int score) => (score - 10) / 2;

    private sealed record ParsedDefenses(int? Ac, int? Fortitude, int? Reflex, int? Will)
    {
        public static ParsedDefenses Empty { get; } = new(null, null, null, null);
        public bool IsEmpty => Ac is null && Fortitude is null && Reflex is null && Will is null;
    }
}

/// <summary>Named extra power on a companion stat block (e.g. "Snatch and Scoot").</summary>
public sealed record CompanionExtraPower(string Name, string? Action, string Description);
