using CharM.Engine.CharacterModel;
using CharM.Engine.Creation;
using CharM.Engine.Powers;
using CharM.Engine.Rules;
using CharM.Serialization;
using CharM.Web.Components.Shared;

// Disambiguate: there's also a CharM.Serialization.CharacterSnapshot from the
// using above. The session API uses the Creation one.
using CharacterSnapshot = CharM.Engine.Creation.CharacterSnapshot;

namespace CharM.Web.Services;

/// <summary>
/// Card collection for the print packet (page 1 summary + power card grid).
/// Mirrors the data assembly in <c>Pages/Powers.razor</c> but lives in a
/// service so the print page can reuse it without copying a thousand-line
/// .razor file. If Powers.razor's logic changes, update this in lockstep.
/// </summary>
public sealed class PrintCardCollector
{
    private readonly CharacterSessionService _sessionService;

    public PrintCardCollector(CharacterSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    public PrintCardCollection Collect()
    {
        var session = _sessionService.Session
            ?? throw new InvalidOperationException("No active character session.");
        var snapshot = session.GetPartialSnapshot();
        var powerStats = _sessionService.GetRebuiltPowerStatsForDisplay();

        var standardCards = PowerCardAssembly.BuildPowerCards(_sessionService, session, snapshot, powerStats);
        var basicCards = standardCards.Where(PowerCardAssembly.IsBasicPowerCard).ToList();
        var nonBasicCards = standardCards.Where(c => !PowerCardAssembly.IsBasicPowerCard(c)).ToList();
        var magicItemCards = BuildMagicItemPowerCards(session).ToList();

        var companionData = PowerCardAssembly.DedupeCompanionDataForDisplay(session.GetCompanionData());
        var miniSheetAnchors = new HashSet<string>(
            companionData.Where(c => c.IsMinion || c.IsSummon)
                         .Select(c => c.AnchorPowerInternalId)
                         .Where(id => id is not null)!,
            StringComparer.OrdinalIgnoreCase);

        bool IsStandardSectionEligible(PowerDisplayCard card)
            => !PowerCardAssembly.IsCompanionPowerCard(card)
               && !miniSheetAnchors.Contains(card.InternalId)
               && !IsChannelDivinityCard(card);

        var atWill = nonBasicCards.Where(c => c.Section == PowerSectionKeys.AtWill && IsStandardSectionEligible(c)).ToList();
        var cantrip = nonBasicCards.Where(c => c.Section == PowerSectionKeys.Cantrip && IsStandardSectionEligible(c)).ToList();
        var encounter = nonBasicCards.Where(c => c.Section == PowerSectionKeys.Encounter && IsStandardSectionEligible(c)).ToList();
        var daily = nonBasicCards.Where(c => c.Section == PowerSectionKeys.Daily && IsStandardSectionEligible(c)).ToList();
        var utility = nonBasicCards.Where(c => c.Section == PowerSectionKeys.Utility && IsStandardSectionEligible(c)).ToList();
        var channelDivinity = nonBasicCards.Where(IsChannelDivinityCard).ToList();

        var allCompanionCards = nonBasicCards.Where(PowerCardAssembly.IsCompanionPowerCard).ToList();
        bool hasSpecificCompanion = allCompanionCards.Any(c =>
            c.Name.StartsWith("Companion:", StringComparison.OrdinalIgnoreCase));
        var companionCards = allCompanionCards
            .Where(c => !hasSpecificCompanion || !PowerCardAssembly.IsGenericBeastAttack(c))
            .ToList();

        // Companion mini-sheet/attack-card pairing — shared with the live page.
        var layout = PowerCardAssembly.BuildCompanionGroups(companionData, companionCards, miniSheetAnchors);
        var companionGroups = layout.Groups;
        var unpairedCompanionCards = layout.Unpaired
            .Select(c => (Card: c, NameOverride: (string?)null))
            .ToList();

        // Print packet ordering: standard powers then channel divinity then magic
        // items then companion attack cards then basic powers.
        var allCards = atWill
            .Concat(cantrip)
            .Concat(encounter)
            .Concat(daily)
            .Concat(utility)
            .Concat(channelDivinity)
            .Concat(magicItemCards)
            .Concat(companionCards)
            .Concat(basicCards)
            .ToList();

        // Non-companion subset for the regular card grid (companion rendered separately).
        var allNonCompanion = atWill
            .Concat(cantrip)
            .Concat(encounter)
            .Concat(daily)
            .Concat(utility)
            .Concat(channelDivinity)
            .Concat(magicItemCards)
            .Concat(basicCards)
            .ToList();

        var pendingChoices = GetPendingPowerChoices(session);

        return new PrintCardCollection
        {
            AtWill = atWill,
            Cantrip = cantrip,
            Encounter = encounter,
            Daily = daily,
            Utility = utility,
            ChannelDivinity = channelDivinity,
            MagicItem = magicItemCards,
            Companion = companionCards,
            Basic = basicCards,
            All = allCards,
            AllNonCompanion = allNonCompanion,
            CompanionGroups = companionGroups,
            UnpairedCompanionCards = unpairedCompanionCards,
            CompanionData = companionData,
            MiniSheetAnchors = miniSheetAnchors,
            PendingPowerChoices = pendingChoices,
            Snapshot = snapshot,
        };
    }

    // Magic-item cards: shared discovery, then this view's own Level/Name order.
    private IReadOnlyList<PowerDisplayCard> BuildMagicItemPowerCards(CharacterSession session)
        => PowerCardAssembly.BuildMagicItemPowerCards(session)
            .OrderBy(c => c.Level)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool IsChannelDivinityCard(PowerDisplayCard card)
        => PowerCardAssembly.IsChannelDivinityCard(_sessionService, card);

    private static IReadOnlyList<PendingChoice> GetPendingPowerChoices(CharacterSession session)
        => session.GetAllPendingChoices()
            .Where(choice => choice.Slot.ElementType.Equals("Power", StringComparison.OrdinalIgnoreCase))
            .ToList();
}

public sealed class PrintCardCollection
{
    public required IReadOnlyList<PowerDisplayCard> AtWill { get; init; }
    public required IReadOnlyList<PowerDisplayCard> Cantrip { get; init; }
    public required IReadOnlyList<PowerDisplayCard> Encounter { get; init; }
    public required IReadOnlyList<PowerDisplayCard> Daily { get; init; }
    public required IReadOnlyList<PowerDisplayCard> Utility { get; init; }
    public required IReadOnlyList<PowerDisplayCard> ChannelDivinity { get; init; }
    public required IReadOnlyList<PowerDisplayCard> MagicItem { get; init; }
    public required IReadOnlyList<PowerDisplayCard> Companion { get; init; }
    public required IReadOnlyList<PowerDisplayCard> Basic { get; init; }
    /// <summary>All cards including companion attack cards.</summary>
    public required IReadOnlyList<PowerDisplayCard> All { get; init; }
    /// <summary>Non-companion cards for the regular card grid; companion section rendered separately.</summary>
    public required IReadOnlyList<PowerDisplayCard> AllNonCompanion { get; init; }
    /// <summary>Ordered pairing of companion mini-sheets and their attack cards, mirroring Powers.razor grouping.</summary>
    public required IReadOnlyList<(CompanionData? Mini, PowerDisplayCard? Card, string? NameOverride)> CompanionGroups { get; init; }
    /// <summary>Companion attack cards not paired to a mini-sheet.</summary>
    public required IReadOnlyList<(PowerDisplayCard Card, string? NameOverride)> UnpairedCompanionCards { get; init; }
    public required IReadOnlyList<CompanionData> CompanionData { get; init; }
    public required HashSet<string> MiniSheetAnchors { get; init; }
    public required IReadOnlyList<PendingChoice> PendingPowerChoices { get; init; }
    public required CharacterSnapshot? Snapshot { get; init; }

    public int TotalAtWill => AtWill.Count;
    public int TotalEncounter => Encounter.Count;
    public int TotalDaily => Daily.Count;
    public int TotalUtility => Utility.Count;
    public int TotalCantrip => Cantrip.Count;
    public int TotalChannelDivinity => ChannelDivinity.Count;
    public int TotalMagicItem => MagicItem.Count;
}
