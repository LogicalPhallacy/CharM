using CharM.Engine.CharacterModel;
using CharM.Engine.Creation;
using CharM.Engine.Rules;
using CharM.Serialization;
using CharM.Web.Components.Shared;

using CharacterSnapshot = CharM.Engine.Creation.CharacterSnapshot;

namespace CharM.Web.Services;

/// <summary>
/// Single source of truth for power-card DATA assembly and classification shared
/// by the interactive <c>Pages/Powers.razor</c> and the print
/// <see cref="PrintCardCollector"/>. Both pages must classify powers, detect
/// cantrips/companions, and group companion mini-sheets identically; keeping one
/// implementation here prevents the two from drifting (which had already
/// happened: companion classifier, cantrip detection, and the companion
/// paired-card skip).
///
/// View-specific concerns deliberately stay in each page: sort order
/// (<c>SortPowers</c> depends on interactive view state) and rendering/markup
/// (print cards are laid out differently by design).
/// </summary>
public static class PowerCardAssembly
{
    // ---- power card building ------------------------------------------------

    public static IReadOnlyList<PowerDisplayCard> BuildPowerCards(
        CharacterSessionService sessionService,
        CharacterSession session,
        CharacterSnapshot? snapshot,
        IReadOnlyList<PowerStatEntry> powerStats)
    {
        var stats = snapshot?.Builder.Stats;
        var cantripIds = CollectCantripPowerIds(snapshot);

        var powers = GetDisplayPowers(sessionService, session, snapshot)
            .Where(power => !PowerCardFactory.IsFamiliarCardPower(power))
            .Where(power => !IsAugmentVariant(power));

        return PowerCardFactory.BuildSessionCards(
            powers,
            stats,
            powerStats,
            session.IsHouseruledElement,
            sectionOverride: power =>
            {
                if (!string.IsNullOrEmpty(power.InternalId)
                    && cantripIds.Contains(power.InternalId))
                {
                    return PowerSectionKeys.Cantrip;
                }

                return null;
            },
            augmentVersions: power => GetAugmentVersions(sessionService, power));
    }

    public static IEnumerable<RulesElement> GetDisplayPowers(
        CharacterSessionService sessionService,
        CharacterSession session,
        CharacterSnapshot? snapshot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (snapshot is not null)
        {
            foreach (var node in snapshot.Builder.ElementTree.Root.GetAllDescendants())
            {
                if (node.RulesElement is not { } power) continue;
                if (!power.Type.Equals("Power", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsAugmentVariant(power)) continue;
                if (!string.IsNullOrEmpty(power.InternalId)
                    && (snapshot.PowerStatsExcludedIds.Contains(power.InternalId)
                        || snapshot.LevelNestedOnlyIds.Contains(power.InternalId)))
                {
                    continue;
                }

                var key = !string.IsNullOrEmpty(power.InternalId)
                    ? power.InternalId
                    : "name:" + power.Name;
                if (!seen.Add(key)) continue;

                yield return !string.IsNullOrEmpty(power.InternalId)
                    ? sessionService.GetElementDetails(power.InternalId) ?? power
                    : power;
            }

            yield break;
        }

        foreach (var power in session.GetAllElementsOfType("Power"))
        {
            if (IsAugmentVariant(power)) continue;
            var key = !string.IsNullOrEmpty(power.InternalId)
                ? power.InternalId
                : "name:" + power.Name;
            if (seen.Add(key))
                yield return !string.IsNullOrEmpty(power.InternalId)
                    ? sessionService.GetElementDetails(power.InternalId) ?? power
                    : power;
        }
    }

    /// <summary>
    /// Magic-item power cards in discovery order (equipped then inventory).
    /// Intentionally UNSORTED — each view applies its own ordering afterward.
    /// </summary>
    public static IReadOnlyList<PowerDisplayCard> BuildMagicItemPowerCards(CharacterSession session)
    {
        var cards = new List<PowerDisplayCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLoot(LootItem loot)
        {
            foreach (var component in loot.Components())
            {
                if (!component.Type.Equals("Magic Item", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var card in PowerCardFactory.BuildItemPowerCards(component, PowerSectionKeys.MagicItem))
                {
                    if (seen.Add(card.InternalId))
                        cards.Add(card);
                }
            }
        }

        foreach (var loot in session.GetEquippedLoot().Values)
            AddLoot(loot);

        foreach (var inventoryItem in session.GetInventory().Where(item => item.Quantity >= 1))
            AddLoot(inventoryItem.Item);

        return cards;
    }

    public static IReadOnlyList<RulesElement> GetAugmentVersions(
        CharacterSessionService sessionService, RulesElement power)
    {
        if (!power.Fields.TryGetValue("_AugmentVersions", out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];

        var variants = new List<RulesElement>();
        foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var variant = sessionService.GetElementDetails(id);
            if (variant is not null)
                variants.Add(variant);
        }

        return variants;
    }

    // ---- cantrip detection (slot-owner Class Feature name contains "Cantrip") ----

    public static HashSet<string> CollectCantripPowerIds(CharacterSnapshot? snapshot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot is null) return ids;

        var featureNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Builder.ElementTree.Root.GetAllDescendants())
        {
            if (node.RulesElement is not { } el) continue;
            if (!el.Type.Equals("Class Feature", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(el.InternalId)) continue;
            featureNamesById[el.InternalId] = el.Name;
        }

        foreach (var node in snapshot.Builder.ElementTree.Root.GetAllDescendants())
        {
            if (node.RulesElement is not { } power) continue;
            if (!power.Type.Equals("Power", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(power.InternalId)) continue;
            if (string.IsNullOrEmpty(node.SlotOwnerInternalId)) continue;
            if (!featureNamesById.TryGetValue(node.SlotOwnerInternalId, out var ownerName)) continue;
            if (ownerName.IndexOf("Cantrip", StringComparison.OrdinalIgnoreCase) < 0) continue;
            ids.Add(power.InternalId);
        }
        return ids;
    }

    // ---- classification predicates -----------------------------------------

    public static bool IsAugmentVariant(RulesElement power)
        => power.Fields.ContainsKey("_AugmentParent");

    public static bool IsChannelDivinityCard(CharacterSessionService sessionService, PowerDisplayCard card)
    {
        var detail = sessionService.GetElementDetails(card.InternalId);
        return detail is not null && detail.Fields.ContainsKey("Channel Divinity");
    }

    public static bool IsCompanionPowerCard(PowerDisplayCard card)
        => card.InternalId.StartsWith("ID_TIV_COMPANION-", StringComparison.OrdinalIgnoreCase)
        || card.InternalId.StartsWith("ID_TIV_COMPANION_", StringComparison.OrdinalIgnoreCase)
        || card.InternalId.StartsWith("ID_TIV_ANIMAL_COMPANION-", StringComparison.OrdinalIgnoreCase)
        || card.InternalId.StartsWith("ID_INTERNAL_POWER_BEAST_", StringComparison.OrdinalIgnoreCase)
        || card.Name.StartsWith("Companion:", StringComparison.OrdinalIgnoreCase)
        || card.Name.StartsWith("Animal Master's Companion:", StringComparison.OrdinalIgnoreCase)
        || card.Name.StartsWith("Animal Companion:", StringComparison.OrdinalIgnoreCase)
        || card.Name.StartsWith("Beast ", StringComparison.OrdinalIgnoreCase);

    public static bool IsGenericBeastAttack(PowerDisplayCard card)
        => card.InternalId.StartsWith("ID_INTERNAL_POWER_BEAST_", StringComparison.OrdinalIgnoreCase)
        || card.Name.Equals("Beast Melee Basic Attack", StringComparison.OrdinalIgnoreCase)
        || card.Name.Equals("Beast Ranged Basic Attack", StringComparison.OrdinalIgnoreCase);

    public static bool IsBasicPowerCard(PowerDisplayCard card)
        => IsBasicPower(card.InternalId, card.Name);

    public static bool IsBasicPower(string internalId, string name)
    {
        if (internalId.Equals("ID_INTERNAL_POWER_MELEE_BASIC_ATTACK", StringComparison.OrdinalIgnoreCase)
            || internalId.Equals("ID_INTERNAL_POWER_RANGED_BASIC_ATTACK", StringComparison.OrdinalIgnoreCase)
            || internalId.Equals("ID_INTERNAL_POWER_BULL_RUSH_ATTACK", StringComparison.OrdinalIgnoreCase)
            || internalId.Equals("ID_INTERNAL_POWER_GRAB_ATTACK", StringComparison.OrdinalIgnoreCase)
            || internalId.Equals("ID_INTERNAL_POWER_OPPORTUNITY_ATTACK", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Equals("Melee Basic Attack", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Ranged Basic Attack", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Bull Rush Attack", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Grab Attack", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Opportunity Attack", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<CompanionData> DedupeCompanionDataForDisplay(IReadOnlyList<CompanionData> source)
    {
        if (source.Count == 0) return source;
        var seenAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CompanionData>(source.Count);
        foreach (var beast in source)
        {
            if (beast.IsPlaceholderForActiveBeast) continue;
            if (!string.IsNullOrEmpty(beast.AnchorPowerInternalId)
                && !seenAnchors.Add(beast.AnchorPowerInternalId))
            {
                continue;
            }
            result.Add(beast);
        }
        return result;
    }

    // ---- companion mini-sheet / attack-card grouping -----------------------

    /// <summary>
    /// Pair companion mini-sheets with their attack cards, mirroring the live
    /// page. Skips the paired attack card when the mini-sheet already carries
    /// the content via ExtraPowers (associate-backed companions render an empty
    /// stat-block card otherwise). Returns the ordered groups plus the
    /// companion cards that weren't paired to any mini-sheet.
    /// </summary>
    public static CompanionGroupLayout BuildCompanionGroups(
        IReadOnlyList<CompanionData> companionData,
        IReadOnlyList<PowerDisplayCard> companionCards,
        HashSet<string> miniSheetAnchors)
    {
        var renderedAttackCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<(CompanionData? Mini, PowerDisplayCard? Card, string? NameOverride)>();

        foreach (var beast in companionData)
        {
            groups.Add((beast, null, null));
            if (beast.IsMinion || beast.IsSummon) continue;

            // Associate-backed companions surface their actions on the mini-sheet
            // via ExtraPowers; the paired Power element is an empty stat-block
            // card. Skip it but still mark the anchor as rendered.
            if (beast.ExtraPowers.Count > 0)
            {
                if (beast.AnchorPowerInternalId is not null)
                    renderedAttackCards.Add(beast.AnchorPowerInternalId);
                continue;
            }

            var attackCard = companionCards.FirstOrDefault(c =>
                beast.AnchorPowerInternalId is not null
                && string.Equals(c.InternalId, beast.AnchorPowerInternalId, StringComparison.OrdinalIgnoreCase));
            if (attackCard is not null)
            {
                string? nameOverride = attackCard.Name.Equals("DISPLAYNAME", StringComparison.OrdinalIgnoreCase)
                    ? $"Companion: {beast.Category} Basic Attack"
                    : attackCard.Name.StartsWith("Companion:", StringComparison.OrdinalIgnoreCase)
                        ? $"{attackCard.Name} Basic Attack"
                        : null;
                groups.Add((null, attackCard, nameOverride));
                renderedAttackCards.Add(attackCard.InternalId);
            }
        }

        var unpaired = companionCards
            .Where(c => !renderedAttackCards.Contains(c.InternalId)
                     && !miniSheetAnchors.Contains(c.InternalId))
            .ToList();

        return new CompanionGroupLayout(groups, unpaired);
    }
}

/// <summary>Result of <see cref="PowerCardAssembly.BuildCompanionGroups"/>.</summary>
public sealed record CompanionGroupLayout(
    IReadOnlyList<(CompanionData? Mini, PowerDisplayCard? Card, string? NameOverride)> Groups,
    IReadOnlyList<PowerDisplayCard> Unpaired);
