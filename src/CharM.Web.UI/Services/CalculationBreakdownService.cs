using CharM.Engine.Evaluation;
using CharM.Engine.Rules;

namespace CharM.Web.Services;

public sealed class CalculationBreakdownService
{
    private readonly CharacterSessionService? _sessionService;
    private readonly RulesDatabaseService? _rulesDb;

    public CalculationBreakdownService()
    {
    }

    public CalculationBreakdownService(CharacterSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    public CalculationBreakdownService(CharacterSessionService sessionService, RulesDatabaseService rulesDb)
    {
        _sessionService = sessionService;
        _rulesDb = rulesDb;
    }

    public CalculationBreakdown Build(StatBlock? stats, string statName)
    {
        if (stats is null)
            return CalculationBreakdown.Empty(statName);

        var stat = stats.TryGetStat(statName);
        if (stat is null)
            return CalculationBreakdown.Empty(statName);

        int finalValue = stat.ComputeValue(stats);
        var rows = stat.Contributions
            .Select(c => ToRow(c, stats, stat.Name))
            .ToList();

        ApplySkillLabels(stat.Name, rows);

        return new CalculationBreakdown(stat.Name, finalValue, rows);
    }

    private CalculationContributionRow ToRow(StatContribution contribution, StatBlock stats, string statName)
    {
        bool displayOnly = contribution.StringPayload is not null
            || !string.IsNullOrEmpty(contribution.Condition);
        int value = displayOnly ? 0 : contribution.GetEffectiveValue(stats);
        string? sourceName = !string.IsNullOrWhiteSpace(contribution.SourceElementId)
            ? _sessionService?.GetElementDetails(contribution.SourceElementId)?.Name
            : null;

        return new CalculationContributionRow(
            Value: value,
            BonusType: contribution.BonusType,
            SourceElementId: contribution.SourceElementId,
            SourceName: sourceName,
            Label: contribution.BonusType ?? InferDefaultLabel(contribution, statName, value),
            Active: contribution.Active && !displayOnly,
            DisplayOnly: displayOnly,
            Condition: contribution.Condition,
            Wearing: contribution.Wearing,
            NotWearing: contribution.NotWearing,
            Requires: contribution.RequiresText,
            StringPayload: contribution.StringPayload,
            Expression: contribution.Expression?.ToString());
    }

    private string InferDefaultLabel(StatContribution contribution, string statName, int value)
    {
        if (contribution.Expression is ValueExpression.AbilityModifier
            or ValueExpression.AbilityModFunction)
            return "Ability";

        if (IsSkill(statName) && value == 0)
            return "Misc";

        return string.IsNullOrWhiteSpace(contribution.BonusType)
            ? "Untyped"
            : contribution.BonusType!;
    }

    private void ApplySkillLabels(string statName, List<CalculationContributionRow> rows)
    {
        if (!IsSkill(statName))
            return;

        var untypedPositive = rows
            .Select((row, index) => (row, index))
            .Where(item => item.row.Label == "Untyped" && item.row.Value > 0)
            .ToList();

        if (untypedPositive.Count > 0)
            rows[untypedPositive[0].index] = untypedPositive[0].row with { Label = "Half level" };

        if (untypedPositive.Count > 1)
            rows[untypedPositive[1].index] = untypedPositive[1].row with { Label = "Trained" };
    }

    // Skill set comes from the loaded rules database (cached vocabulary), so it
    // tracks the actual ruleset instead of a hardcoded list. Falls back to "not
    // a skill" (labels are cosmetic) when no database is available.
    private bool IsSkill(string statName)
        => _rulesDb is { IsLoaded: true } db && db.Vocabulary.SkillNameSet.Contains(statName);
}

public sealed record CalculationBreakdown(
    string StatName,
    int FinalValue,
    IReadOnlyList<CalculationContributionRow> Contributions)
{
    public static CalculationBreakdown Empty(string statName) => new(statName, 0, []);
}

public sealed record CalculationContributionRow(
    int Value,
    string? BonusType,
    string? SourceElementId,
    string? SourceName,
    string Label,
    bool Active,
    bool DisplayOnly,
    string? Condition,
    string? Wearing,
    string? NotWearing,
    string? Requires,
    string? StringPayload,
    string? Expression);
