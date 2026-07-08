using System.Globalization;
using System.Text.RegularExpressions;
using CharM.Engine.Evaluation;
using CharM.Engine.Powers;
using CharM.Engine.Rules;
using CharM.Serialization;

namespace CharM.Web.Components.Shared;

/// <summary>Section keys for grouping / styling powers.</summary>
public static class PowerSectionKeys
{
    public const string AtWill = "At-Will";
    public const string Encounter = "Encounter";
    public const string Daily = "Daily";
    public const string Utility = "Utility";
    public const string MagicItem = "Magic Item";
    public const string Cantrip = "Cantrip";

    public static int GetOrder(string section) => section switch
    {
        AtWill => 0,
        Cantrip => 1,
        Encounter => 2,
        Daily => 3,
        Utility => 4,
        MagicItem => 5,
        _ => 5,
    };
}

public sealed record PowerStatLine(string Label, string Value, string? Components = null);

public sealed record PowerWeaponStatLine(
    string Name,
    string? Attack,
    string? Damage,
    string? Healing,
    string? AttackRollExpression = null,
    string? DamageRollExpression = null,
    string? HealingRollExpression = null,
    string? AttackComponents = null,
    string? DamageComponents = null,
    string? HealingComponents = null);

public sealed record PowerBodyEntry(string Label, string Text, string LabelClass);

/// <summary>Kind of a parsed body-entry segment.</summary>
public enum PowerEntrySegmentKind { Text, Table }

/// <summary>
/// One segment of a power body entry: either a run of prose or an embedded
/// table (rows of cells). Produced by
/// <see cref="PowerCardFactory.ParseEntrySegments"/>.
/// </summary>
public sealed record PowerEntrySegment(
    PowerEntrySegmentKind Kind,
    string Text,
    IReadOnlyList<IReadOnlyList<string>>? Rows);

/// <summary>
/// Render-model for a single power card, shared between the Powers page
/// and the choice-modal preview. Section-aware styling classes drive the
/// colour scheme; the shape of the card itself is identical across uses.
/// </summary>
public sealed partial class PowerDisplayCard
{
    public required string InternalId { get; init; }
    public required string Name { get; init; }
    public required string Usage { get; init; }
    public required string Section { get; init; }
    public required int Level { get; init; }
    public string? Action { get; init; }
    public string? Flavor { get; init; }
    public string? KeywordsText { get; init; }

    /// <summary>Source book this power comes from (e.g. "Player's Handbook").</summary>
    public string? Source { get; init; }

    public bool IsHouseruled { get; init; }

    /// <summary>
    /// Rulebook-style citation for display in the small bottom-right corner of a
    /// card. The rules database carries no page numbers, so this is the source
    /// book name only — we never fabricate a page reference.
    /// </summary>
    public string? SourceCite => string.IsNullOrWhiteSpace(Source) ? null : Source;
    public required List<PowerStatLine> ListStats { get; init; }
    public required List<PowerStatLine> PrintStats { get; init; }
    public required List<PowerWeaponStatLine> WeaponStats { get; init; }
    public required List<PowerBodyEntry> Entries { get; init; }

    public bool ShowUsageCheckbox => !Usage.Equals("At-Will", StringComparison.OrdinalIgnoreCase);
    public bool ShowHeaderUsage => Section == PowerSectionKeys.MagicItem;
    public bool ShowWeaponStats => WeaponStats.Count > 1;

    public string HeaderClass => Section switch
    {
        PowerSectionKeys.AtWill => "bg-success-container/40 px-4 py-2 flex justify-between items-center border-b border-outline-variant/10",
        PowerSectionKeys.Encounter => "bg-error-container/10 px-4 py-2 flex justify-between items-center border-b border-outline-variant/10",
        PowerSectionKeys.Daily => "bg-primary px-4 py-2 flex justify-between items-center",
        PowerSectionKeys.MagicItem => "bg-amber-100 px-4 py-2 flex justify-between items-center border-b border-amber-300",
        PowerSectionKeys.Cantrip => "bg-sky-100 px-4 py-2 flex justify-between items-center border-b border-outline-variant/10",
        _ => "bg-surface-container-high px-4 py-2 flex justify-between items-center border-b border-outline-variant/10",
    };

    public string TitleClass => Section switch
    {
        PowerSectionKeys.AtWill => "font-label font-extrabold text-sm uppercase tracking-tight text-on-success-container",
        PowerSectionKeys.Encounter => "font-label font-extrabold text-sm uppercase tracking-tight text-error",
        PowerSectionKeys.Daily => "font-label font-extrabold text-sm uppercase tracking-tight text-on-primary",
        PowerSectionKeys.MagicItem => "font-label font-extrabold text-sm uppercase tracking-tight text-amber-950",
        PowerSectionKeys.Cantrip => "font-label font-extrabold text-sm uppercase tracking-tight text-sky-900",
        _ => "font-label font-extrabold text-sm uppercase tracking-tight text-on-surface",
    };

    public string ActionBadgeClass => Section switch
    {
        PowerSectionKeys.AtWill => "bg-on-success-container/10 text-on-success-container text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
        PowerSectionKeys.Encounter => "bg-error/10 text-error text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
        PowerSectionKeys.Daily => "bg-white/20 text-on-primary text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
        PowerSectionKeys.MagicItem => "bg-amber-200 text-amber-950 text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
        PowerSectionKeys.Cantrip => "bg-sky-200 text-sky-900 text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
        _ => "bg-outline/10 text-outline text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
    };

    public string UsageBadgeClass => Section switch
    {
        PowerSectionKeys.MagicItem => "bg-amber-300 text-amber-950 text-[9px] px-2 py-0.5 font-bold uppercase tracking-wider rounded-sm",
        _ => ActionBadgeClass,
    };

    public string HeaderIcon => Section switch
    {
        PowerSectionKeys.Utility => "settings_accessibility",
        PowerSectionKeys.MagicItem => "auto_fix_high",
        _ => "expand_more",
    };

    public string HeaderIconClass => Section switch
    {
        PowerSectionKeys.AtWill => "text-on-success-container text-lg",
        PowerSectionKeys.Encounter => "text-on-tertiary-container text-lg",
        PowerSectionKeys.Daily => "text-on-primary text-lg",
        PowerSectionKeys.MagicItem => "text-amber-900 text-lg",
        _ => "text-outline text-lg",
    };

    public string PrintHeaderClass => Section switch
    {
        PowerSectionKeys.AtWill => "bg-success-container/40 px-3 py-2 border-b border-outline-variant/20",
        PowerSectionKeys.Encounter => "bg-error-container/10 px-3 py-2 border-b border-outline-variant/20",
        PowerSectionKeys.Daily => "bg-primary px-3 py-2 border-b border-outline-variant/20",
        PowerSectionKeys.MagicItem => "bg-amber-100 px-3 py-2 border-b border-amber-300",
        _ => "bg-surface-container-high px-3 py-2 border-b border-outline-variant/20",
    };

    public string PrintTitleClass => Section switch
    {
        PowerSectionKeys.AtWill => "font-label font-extrabold text-xs uppercase tracking-tight text-on-success-container leading-none",
        PowerSectionKeys.Encounter => "font-label font-extrabold text-xs uppercase tracking-tight text-error leading-none",
        PowerSectionKeys.Daily => "font-label font-extrabold text-xs uppercase tracking-tight text-on-primary leading-none",
        PowerSectionKeys.MagicItem => "font-label font-extrabold text-xs uppercase tracking-tight text-amber-950 leading-none",
        _ => "font-label font-extrabold text-xs uppercase tracking-tight text-on-surface leading-none",
    };

    public string PrintUsageClass => Section switch
    {
        PowerSectionKeys.AtWill => "text-[8px] font-bold text-on-success-container/70 tracking-widest uppercase",
        PowerSectionKeys.Encounter => "text-[8px] font-bold text-error/70 tracking-widest uppercase",
        PowerSectionKeys.Daily => "text-[8px] font-bold text-white/70 tracking-widest uppercase",
        PowerSectionKeys.MagicItem => "text-[8px] font-bold text-amber-900 tracking-widest uppercase",
        _ => "text-[8px] font-bold text-outline/70 tracking-widest uppercase",
    };

    public string PrintActionBadgeClass => Section switch
    {
        PowerSectionKeys.AtWill => "bg-on-success-container/10 text-on-success-container text-[8px] px-1.5 py-0.5 font-bold uppercase rounded-sm",
        PowerSectionKeys.Encounter => "bg-error/10 text-error text-[8px] px-1.5 py-0.5 font-bold uppercase rounded-sm",
        PowerSectionKeys.Daily => "bg-white/20 text-on-primary text-[8px] px-1.5 py-0.5 font-bold uppercase rounded-sm",
        PowerSectionKeys.MagicItem => "bg-amber-200 text-amber-950 text-[8px] px-1.5 py-0.5 font-bold uppercase rounded-sm",
        _ => "bg-outline/10 text-outline text-[8px] px-1.5 py-0.5 font-bold uppercase rounded-sm",
    };

    public string PrintCheckboxClass => Section switch
    {
        PowerSectionKeys.Encounter => "w-3 h-3 border-error rounded-sm",
        PowerSectionKeys.Daily => "w-3 h-3 border-white/40 bg-transparent rounded-sm",
        _ => "w-3 h-3 border-outline/40 rounded-sm",
    };
}

/// <summary>
/// Factory that builds a <see cref="PowerDisplayCard"/> view-model from a
/// <see cref="RulesElement"/> of type Power. Pass <paramref name="stats"/> and
/// <paramref name="weapon"/> when available to get computed attack/damage;
/// callers (like a choice preview) can omit them for a static preview.
/// </summary>
public static partial class PowerCardFactory
{
    public static IReadOnlyList<PowerDisplayCard> BuildSessionCards(
        IEnumerable<RulesElement> powers,
        StatBlock? stats,
        IReadOnlyList<PowerStatEntry> powerStats,
        Func<string?, bool> isHouseruled,
        Func<RulesElement, string?>? sectionOverride = null,
        Func<RulesElement, IEnumerable<RulesElement>>? augmentVersions = null)
    {
        var powerStatQueues = BuildPowerStatQueues(powerStats);

        return powers
            .Select(power =>
            {
                var entry = DequeuePowerStat(powerStatQueues, power.Name);
                return BuildSessionCard(
                    power,
                    stats,
                    isHouseruled,
                    precomputedWeapons: entry?.Weapons,
                    sectionOverride: sectionOverride?.Invoke(power),
                    augmentVersions: augmentVersions?.Invoke(power));
            })
            .OrderBy(card => PowerSectionKeys.GetOrder(card.Section))
            .ThenBy(card => card.Level)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static PowerDisplayCard BuildSessionCard(
        RulesElement power,
        StatBlock? stats,
        Func<string?, bool> isHouseruled,
        IEnumerable<PowerStatWeapon>? precomputedWeapons = null,
        string? sectionOverride = null,
        IEnumerable<RulesElement>? augmentVersions = null)
        => Build(
            power,
            stats,
            isHouseruled: isHouseruled(power.InternalId),
            precomputedWeapons: precomputedWeapons,
            sectionOverride: sectionOverride,
            augmentVersions: augmentVersions);

    private static Dictionary<string, Queue<PowerStatEntry>> BuildPowerStatQueues(
        IReadOnlyList<PowerStatEntry> powerStats)
        => powerStats
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<PowerStatEntry>(group),
                StringComparer.OrdinalIgnoreCase);

    private static PowerStatEntry? DequeuePowerStat(
        Dictionary<string, Queue<PowerStatEntry>> powerStatQueues,
        string powerName)
        => powerStatQueues.TryGetValue(powerName, out var queue) && queue.Count > 0
            ? queue.Dequeue()
            : null;

    public static PowerDisplayCard Build(
        RulesElement power,
        StatBlock? stats = null,
        RulesElement? weapon = null,
        bool isHouseruled = false,
        IEnumerable<PowerStatWeapon>? precomputedWeapons = null,
        string? sectionOverride = null,
        IEnumerable<RulesElement>? augmentVersions = null)
    {
        var precomputedWeaponList = precomputedWeapons?.ToList();
        var weaponStats = BuildWeaponStats(precomputedWeaponList);
        var firstWeaponStat = weaponStats.FirstOrDefault();
        var firstPrecomputedWeapon = precomputedWeaponList?.FirstOrDefault();
        var augments = augmentVersions?.ToList() ?? [];
        var powerStat = stats is not null
            ? PowerStatCalculator.Calculate(power, stats, weapon)
            : null;

        string usage = NormalizeUsage(PowerFieldParser.GetPowerUsage(power));
        string section = sectionOverride ?? (IsUtilityPower(power) ? PowerSectionKeys.Utility : usage switch
        {
            "Encounter" => PowerSectionKeys.Encounter,
            "Daily" => PowerSectionKeys.Daily,
            _ => PowerSectionKeys.AtWill,
        });

        string? attack = firstWeaponStat?.Attack ?? FormatAttackText(power, powerStat);
        string? damage = firstWeaponStat?.Damage ?? FormatDamageText(power, powerStat);
        string? range = FirstNonBlank(
            power.Fields.GetValueOrDefault("Attack Type"),
            power.Fields.GetValueOrDefault("Range"));
        range = CleanFieldText(range);

        string? keywordsText = CleanFieldText(string.Join(", ",
            (powerStat?.Keywords.Count > 0 ? powerStat.Keywords : PowerFieldParser.GetKeywords(power))
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))));

        string? fourthLabel = null;
        string? fourthValue = null;
        // Always prefer Keywords for the 4th stat slot — Target / Hit are
        // already rendered as body entries below the stat grid, so showing
        // them here was redundant. Keywords have nowhere else to go.
        if (!string.IsNullOrWhiteSpace(keywordsText))
        {
            fourthLabel = "Keywords";
            fourthValue = keywordsText;
        }
        else if (!string.IsNullOrWhiteSpace(power.Fields.GetValueOrDefault("Target")))
        {
            fourthLabel = "Target";
            fourthValue = SummarizeText(power.Fields.GetValueOrDefault("Target"), 48);
        }
        else if (!string.IsNullOrWhiteSpace(power.Fields.GetValueOrDefault("Hit")))
        {
            fourthLabel = "Hit";
            fourthValue = SummarizeText(power.Fields.GetValueOrDefault("Hit"), 48);
        }

        var listStats = new List<PowerStatLine>();
        AddStatLine(listStats, "Attack", attack, firstPrecomputedWeapon?.HitComponents ?? powerStat?.AttackComponents);
        AddStatLine(listStats, "Damage", damage, firstPrecomputedWeapon?.DamageComponents ?? powerStat?.DamageComponents);
        AddStatLine(listStats, "Range", range);
        AddStatLine(listStats, fourthLabel, fourthValue);

        var printStats = new List<PowerStatLine>();
        AddStatLine(printStats, "Attack", attack, firstPrecomputedWeapon?.HitComponents ?? powerStat?.AttackComponents);
        AddStatLine(printStats, "Damage", damage, firstPrecomputedWeapon?.DamageComponents ?? powerStat?.DamageComponents);

        var entries = new List<PowerBodyEntry>();
        AddEntry(entries, "Requirement", power.Fields.GetValueOrDefault("Requirement")
            ?? power.Fields.GetValueOrDefault("Prerequisite"));
        AddEntry(entries, "Trigger", power.Fields.GetValueOrDefault("Trigger"));
        AddEntry(entries, "Target", power.Fields.GetValueOrDefault("Target")
            ?? power.Fields.GetValueOrDefault("Targets"));
        if (augments.Count > 0)
        {
            AddAugmentEntries(entries, augments, stats, weapon);
            AddSustainEntries(entries, power);
            AddEntry(entries, "Special", power.Fields.GetValueOrDefault("Special"));
        }
        else
        {
            AddEntry(entries, "Hit", power.Fields.GetValueOrDefault("Hit"));
            AddEntry(entries, "Effect", power.Fields.GetValueOrDefault("Effect"));
            // Sub-attack continuation lines: many encounter/daily powers store a
            // follow-up attack inside the Effect block as fields with leading-space
            // keys (" Target", " Attack", " Hit", " Miss", " Effect"). Render them
            // in source order under the Effect.
            AddSubAttackEntries(entries, power);
            AddEntry(entries, "Miss", power.Fields.GetValueOrDefault("Miss"), "text-primary");
            AddEntry(entries, "Aftereffect", power.Fields.GetValueOrDefault("Aftereffect"));
            AddSustainEntries(entries, power);
            AddEntry(entries, "Special", power.Fields.GetValueOrDefault("Special"));
        }
        if (IsFamiliarCardPower(power))
        {
            AddEntry(entries, "Constant Benefits", power.Fields.GetValueOrDefault("Constant Benefits"));
            AddEntry(entries, "Power", GetFamiliarPowerText(power));
            AddEntry(entries, "Information", power.Fields.GetValueOrDefault("Information"));
            AddEntry(entries, "Speed", FormatFamiliarSpeed(power));
            AddEntry(entries, "Senses", power.Fields.GetValueOrDefault("Senses"));
        }
        AddEntry(entries, "Conditions", powerStat?.Conditions);

        if (entries.Count == 0 && !string.IsNullOrWhiteSpace(power.Fields.GetValueOrDefault("Short Description")))
        {
            AddEntry(entries, "Effect", power.Fields.GetValueOrDefault("Short Description"));
        }

        return new PowerDisplayCard
        {
            InternalId = power.InternalId,
            Name = power.Name,
            Usage = usage,
            Section = section,
            Action = FormatAction(power.Fields.GetValueOrDefault("Action Type")),
            IsHouseruled = isHouseruled,
            Flavor = FirstNonBlank(
                CleanFieldText(power.Fields.GetValueOrDefault("Flavor")),
                CleanFieldText(power.Fields.GetValueOrDefault("Short Description"))),
            KeywordsText = keywordsText,
            Source = power.Source,
            ListStats = listStats,
            PrintStats = printStats,
            WeaponStats = weaponStats,
            Entries = entries,
            Level = ExtractLevel(power),
        };
    }

    public static IReadOnlyList<PowerDisplayCard> BuildItemPowerCards(
        RulesElement item,
        string? sectionOverride = null)
    {
        if (!item.Fields.TryGetValue("Power", out var raw) || string.IsNullOrWhiteSpace(raw))
            return Array.Empty<PowerDisplayCard>();

        var cards = new List<PowerDisplayCard>();
        int index = 0;
        foreach (var block in SplitItemPowerBlocks(raw))
        {
            var synth = ParseItemPowerBlock(item, block, index++);
            if (synth is not null)
                cards.Add(Build(synth, sectionOverride: sectionOverride));
        }
        return cards;
    }

    private static void AddEntry(List<PowerBodyEntry> entries, string label, string? text, string labelClass = "")
    {
        string? cleaned = CleanEntryText(text);
        if (!string.IsNullOrWhiteSpace(cleaned))
            entries.Add(new PowerBodyEntry(label, cleaned, labelClass));
    }

    /// <summary>
    /// Render leading-space continuation fields (" Target", " Attack", " Hit",
    /// " Effect", " Miss", " Special", " Sustain") as indented entries under
    /// the parent Effect block. These represent sub-attack details that the
    /// OCB renders on a separate line beneath the Effect prose.
    /// </summary>
    private static void AddSubAttackEntries(List<PowerBodyEntry> entries, RulesElement power)
    {
        // Preserve OCB ordering: Target, Attack, Hit, Miss, Effect, Special, Sustain.
        var subFields = new[] { " Target", " Attack", " Hit", " Miss", " Effect", " Special", " Sustain" };
        foreach (var key in subFields)
        {
            if (!power.Fields.TryGetValue(key, out var value)) continue;
            string? cleaned = CleanFieldText(value);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            // Indent label slightly to mirror the OCB sub-attack hierarchy.
            entries.Add(new PowerBodyEntry($"\u00A0\u00A0{key.TrimStart()}", cleaned, "text-on-surface-variant"));
        }
    }

    /// <summary>
    /// Render Sustain and its action-specific variants (Sustain Minor,
    /// Sustain Move, Sustain Standard, Sustain No Action) as separate entries.
    /// 4e powers commonly use the action-specific form; we previously only
    /// rendered the bare "Sustain" field so 460+ powers showed nothing.
    /// </summary>
    private static void AddSustainEntries(List<PowerBodyEntry> entries, RulesElement power)
    {
        // Plain Sustain first
        AddEntry(entries, "Sustain", power.Fields.GetValueOrDefault("Sustain"));
        // Then any "Sustain X" variant
        foreach (var (key, value) in power.Fields)
        {
            if (!key.StartsWith("Sustain ", StringComparison.OrdinalIgnoreCase)) continue;
            string? cleaned = CleanFieldText(value);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;
            entries.Add(new PowerBodyEntry(key, cleaned, ""));
        }
    }

    private static void AddAugmentEntries(
        List<PowerBodyEntry> entries,
        IReadOnlyList<RulesElement> augments,
        StatBlock? stats,
        RulesElement? weapon)
    {
        foreach (var augment in augments.OrderBy(ExtractAugmentSortKey))
        {
            var parts = new List<string>();
            AddAugmentPart(parts, "Target", augment.Fields.GetValueOrDefault("Target"));
            AddAugmentPart(parts, "Hit", augment.Fields.GetValueOrDefault("Hit"));
            AddAugmentPart(parts, "Effect", augment.Fields.GetValueOrDefault("Effect"));
            AddAugmentPart(parts, "Miss", augment.Fields.GetValueOrDefault("Miss"));
            AddAugmentPart(parts, "Special", augment.Fields.GetValueOrDefault("Special"));

            if (parts.Count == 0)
                continue;

            string label = GetAugmentLabel(augment);
            string? resolvedDamage = ResolveAugmentDamage(augment, stats, weapon);
            if (!string.IsNullOrWhiteSpace(resolvedDamage))
                label = $"{label} ({resolvedDamage})";

            entries.Add(new PowerBodyEntry(label, string.Join(" ", parts), "text-primary"));
        }
    }

    /// <summary>
    /// Compute the resolved damage for an augment by running it through the
    /// PowerStatCalculator with the same stat block and weapon as the parent
    /// power. Returns the resolved damage expression (e.g. "1d8+4") or null
    /// if the augment has no calculable damage.
    /// </summary>
    private static string? ResolveAugmentDamage(RulesElement augment, StatBlock? stats, RulesElement? weapon)
    {
        if (stats is null) return null;
        try
        {
            var calc = PowerStatCalculator.Calculate(augment, stats, weapon);
            return string.IsNullOrWhiteSpace(calc.DamageExpression)
                ? null
                : FormatExpression(calc.DamageExpression);
        }
        catch
        {
            return null;
        }
    }

    private static void AddAugmentPart(List<string> parts, string label, string? value)
    {
        string? cleaned = CleanFieldText(value);
        if (!string.IsNullOrWhiteSpace(cleaned))
            parts.Add($"{label}: {cleaned}");
    }

    private static string GetAugmentLabel(RulesElement augment)
    {
        var match = AugmentNameRegex().Match(augment.Name);
        if (match.Success)
            return $"Augment {match.Groups[1].Value.Trim()}";
        if (augment.Fields.TryGetValue("Augment", out var raw) && !string.IsNullOrWhiteSpace(raw))
            return $"Augment {raw.Trim()}";
        return "Augment";
    }

    private static int ExtractAugmentSortKey(RulesElement augment)
    {
        var label = GetAugmentLabel(augment);
        var match = DigitsRegex().Match(label);
        return match.Success && int.TryParse(match.Value, out var value) ? value : int.MaxValue;
    }

    private static void AddStatLine(List<PowerStatLine> stats, string? label, string? value, string? components = null)
    {
        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value))
            stats.Add(new PowerStatLine(label, value, components));
    }

    private static List<PowerWeaponStatLine> BuildWeaponStats(IEnumerable<PowerStatWeapon>? weapons)
    {
        if (weapons is null)
            return [];

        var result = new List<PowerWeaponStatLine>();
        foreach (var weapon in weapons)
        {
            string? attack = !string.IsNullOrWhiteSpace(weapon.Defense)
                ? $"{FormatSigned(weapon.AttackBonus)} vs {weapon.Defense}"
                : null;
            string? damage = CleanFieldText(weapon.Damage);
            if (!string.IsNullOrWhiteSpace(damage))
            {
                damage = FormatExpression(damage);
                if (!string.IsNullOrWhiteSpace(weapon.DamageType))
                    damage = $"{damage} {CleanFieldText(weapon.DamageType)}";
            }

            string? healing = CleanFieldText(weapon.Healing);
            if (!string.IsNullOrWhiteSpace(attack)
                || !string.IsNullOrWhiteSpace(damage)
                || !string.IsNullOrWhiteSpace(healing))
            {
                result.Add(new PowerWeaponStatLine(
                    string.IsNullOrWhiteSpace(weapon.Name) ? "Unarmed" : weapon.Name,
                    attack,
                    damage,
                    healing,
                    attack is not null ? $"1d20{FormatSigned(weapon.AttackBonus)}" : null,
                    TryBuildRollExpression(damage),
                    TryBuildRollExpression(healing),
                    weapon.HitComponents,
                    weapon.DamageComponents,
                    weapon.HealingComponents));
            }
        }

        return result;
    }

    private static string? TryBuildRollExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = DicePrefixRegex().Match(value.Trim());
        return match.Success ? match.Value.Replace(" ", "") : null;
    }

    private static bool IsUtilityPower(RulesElement power)
    {
        string combined = string.Join(" ",
            power.Fields.GetValueOrDefault("Display"),
            power.Fields.GetValueOrDefault("Power Type"),
            power.Fields.GetValueOrDefault("Attack Type"),
            power.Fields.GetValueOrDefault("Keywords"));

        return combined.Contains("utility", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFamiliarCardPower(RulesElement power)
        => CharM.Engine.CharacterModel.CompanionData.IsFamiliarPower(power);

    private static string? GetFamiliarPowerText(RulesElement power)
    {
        foreach (var (key, value) in power.Fields)
        {
            if (IsStandardPowerField(key))
                continue;
            if (key.Equals("Information", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Constant Benefits", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Secondary Speed", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Senses", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Size", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Speed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
                return $"{key}: {value}";
        }

        return null;
    }

    private static bool IsStandardPowerField(string key)
        => key.Equals("Power Usage", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Action Type", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Attack Type", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Keywords", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Target", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Hit", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Effect", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Miss", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Sustain", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Special", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Requirement", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Trigger", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Short Description", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Flavor", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Display", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Power Type", StringComparison.OrdinalIgnoreCase)
           || key.Equals("Level", StringComparison.OrdinalIgnoreCase);

    private static string? FormatFamiliarSpeed(RulesElement power)
    {
        var parts = new List<string>();
        if (power.Fields.TryGetValue("Speed", out var speed) && !string.IsNullOrWhiteSpace(speed))
            parts.Add(speed);
        if (power.Fields.TryGetValue("Secondary Speed", out var secondary) && !string.IsNullOrWhiteSpace(secondary))
            parts.Add(secondary);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string NormalizeUsage(string? usage)
    {
        string normalized = CleanFieldText(usage) ?? "At-Will";
        if (normalized.Contains("Daily", StringComparison.OrdinalIgnoreCase))
            return "Daily";
        if (normalized.Contains("Encounter", StringComparison.OrdinalIgnoreCase))
            return "Encounter";
        if (normalized.Contains("At-Will", StringComparison.OrdinalIgnoreCase))
            return "At-Will";
        return normalized;
    }

    private static string? FormatAction(string? actionType)
    {
        string? cleaned = CleanFieldText(actionType);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        return cleaned
            .Replace(" action", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string? FormatAttackText(RulesElement power, PowerStatBlock? powerStat)
    {
        string? rawAttack = CleanFieldText(power.Fields.GetValueOrDefault("Attack"));
        if (powerStat is not null && !string.IsNullOrWhiteSpace(powerStat.Defense))
        {
            string sign = powerStat.AttackBonus >= 0 ? "+" : string.Empty;
            return $"{sign}{powerStat.AttackBonus} vs {powerStat.Defense}";
        }

        return rawAttack;
    }

    private static string? FormatDamageText(RulesElement power, PowerStatBlock? powerStat)
    {
        if (powerStat is not null && !string.IsNullOrWhiteSpace(powerStat.DamageExpression))
            return FormatExpression(powerStat.DamageExpression);

        // Without computed stats, the Damage column should be a brief summary —
        // the full Hit text is rendered in the body of the card. Take the first
        // sentence/clause so prose powers (e.g. "Ongoing 10 poison damage (save ends)...")
        // don't crowd out the grid.
        string? rawDamage = PowerFieldParser.GetDamageText(power);
        string? cleaned = CleanFieldText(rawDamage);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        int sentenceBreak = cleaned.IndexOf('.');
        if (sentenceBreak > 0)
            cleaned = cleaned[..sentenceBreak];

        return cleaned.Trim();
    }

    private static string FormatExpression(string expression)
        => expression
            .Replace("+", " + ", StringComparison.Ordinal)
            .Replace("-", " - ", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();

    private static string FormatSigned(int value)
        => value >= 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<string> SplitItemPowerBlocks(string raw)
    {
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Power", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('('))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }
            }

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(trimmed);
        }

        if (sb.Length > 0)
            yield return sb.ToString().Trim();
    }

    private static RulesElement? ParseItemPowerBlock(RulesElement item, string block, int index)
    {
        var match = ItemPowerBlockRegex().Match(block);
        if (!match.Success) return null;

        var inside = match.Groups[1].Value.Trim();
        var action = match.Groups[2].Value.Trim();
        var body = match.Groups[3].Value.Trim();

        var parts = inside.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var usage = parts.Length > 0 ? parts[0] : "";
        var keywords = parts.Length > 1 ? string.Join(", ", parts.Skip(1)) : "";

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Power Usage"] = usage,
            ["Action Type"] = action,
            ["Effect"] = body,
        };
        if (!string.IsNullOrEmpty(keywords))
            fields["Keywords"] = keywords;

        var itemId = string.IsNullOrWhiteSpace(item.InternalId)
            ? item.Name
            : item.InternalId;
        return new RulesElement
        {
            InternalId = $"_synth_item_power_{itemId}_{index}".Replace(' ', '_'),
            Name = item.Name,
            Type = "Power",
            Source = item.Source,
            Fields = fields,
        };
    }

    private static string? CleanFieldText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string cleaned = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);

        return WhitespaceRegex().Replace(cleaned, " ").Trim();
    }

    /// <summary>
    /// Clean a body-entry field the same way as <see cref="CleanFieldText"/> but
    /// preserve any <c>&lt;table&gt;...&lt;/table&gt;</c> blocks verbatim (their
    /// internal newlines / tabs are the row / column delimiters that
    /// <see cref="ParseEntrySegments"/> relies on to render an actual table).
    /// Prose outside the table blocks is still collapsed to single spaces.
    /// </summary>
    private static string? CleanEntryText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!text.Contains("<table>", StringComparison.OrdinalIgnoreCase))
            return CleanFieldText(text);

        var result = new System.Text.StringBuilder();
        int pos = 0;
        foreach (Match m in TableBlockRegex().Matches(text))
        {
            string before = CleanFieldText(text[pos..m.Index]) ?? string.Empty;
            if (before.Length > 0)
            {
                if (result.Length > 0) result.Append(' ');
                result.Append(before);
            }
            if (result.Length > 0) result.Append(' ');
            result.Append(m.Value.Trim());
            pos = m.Index + m.Length;
        }
        string tail = CleanFieldText(text[pos..]) ?? string.Empty;
        if (tail.Length > 0)
        {
            if (result.Length > 0) result.Append(' ');
            result.Append(tail);
        }

        string final = result.ToString().Trim();
        return final.Length > 0 ? final : null;
    }

    /// <summary>
    /// Split a cleaned body-entry text into prose and table segments so a
    /// renderer can emit real <c>&lt;table&gt;</c> markup for embedded tables
    /// (e.g. Hunter's Quarry's damage-by-level table). Table rows are
    /// newline-separated and cells tab-separated; leading empty cells mark
    /// continuation rows in the source data.
    /// </summary>
    public static IReadOnlyList<PowerEntrySegment> ParseEntrySegments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<PowerEntrySegment>();

        if (!text.Contains("<table>", StringComparison.OrdinalIgnoreCase))
            return [new PowerEntrySegment(PowerEntrySegmentKind.Text, text.Trim(), null)];

        var segments = new List<PowerEntrySegment>();
        int pos = 0;
        foreach (Match m in TableBlockRegex().Matches(text))
        {
            string before = text[pos..m.Index].Trim();
            if (before.Length > 0)
                segments.Add(new PowerEntrySegment(PowerEntrySegmentKind.Text, before, null));

            string body = m.Groups[1].Value;
            var rows = body
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n')
                .Where(line => line.Trim().Length > 0)
                .Select(line => (IReadOnlyList<string>)line.Split('\t').Select(cell => cell.Trim()).ToList())
                .ToList();
            if (rows.Count > 0)
                segments.Add(new PowerEntrySegment(PowerEntrySegmentKind.Table, string.Empty, rows));

            pos = m.Index + m.Length;
        }
        string tail = text[pos..].Trim();
        if (tail.Length > 0)
            segments.Add(new PowerEntrySegment(PowerEntrySegmentKind.Text, tail, null));

        return segments;
    }

    private static string? SummarizeText(string? text, int maxLength)
    {
        string? cleaned = CleanFieldText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        int sentenceBreak = cleaned.IndexOfAny(['.', ';']);
        if (sentenceBreak > 0)
            cleaned = cleaned[..sentenceBreak];

        if (cleaned.Length <= maxLength)
            return cleaned;

        return $"{cleaned[..maxLength].TrimEnd()}...";
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.Select(CleanFieldText).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static int ExtractLevel(RulesElement power)
    {
        string? levelText = FirstNonBlank(
            power.Fields.GetValueOrDefault("Level"),
            power.Fields.GetValueOrDefault("Display"));

        if (levelText is null)
            return 0;

        var match = LevelNumberRegex().Match(levelText);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
            ? level
            : 0;
    }

    [GeneratedRegex(@"\bAugment\s+([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex AugmentNameRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^Power\s*\(([^)]*)\)\s*:\s*([^.]+?)\.\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex ItemPowerBlockRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b(\d+)\b")]
    private static partial Regex LevelNumberRegex();

    [GeneratedRegex(@"^\d*d\d+(?:\s*[+-]\s*\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex DicePrefixRegex();

    [GeneratedRegex(@"<table>(.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableBlockRegex();
}
