using CharM.Engine.CharacterModel;
using CharM.Engine.Prerequisites;
using CharM.Engine.Rules;

namespace CharM.Engine.Selection;

/// <summary>
/// Result of a legality check on a single element.
/// </summary>
public sealed record LegalityResult(
    bool IsLegal,
    string? Reason = null,
    string? PrintPrereqs = null,
    LegalitySource Source = LegalitySource.RulesLegal);

public enum LegalitySource
{
    RulesLegal,
    PartFile,
    HouseRule
}

/// <summary>
/// Evaluates whether game elements meet their prerequisites against character state.
/// Wraps the existing PrereqParser/PrereqEvaluator system into a usable API.
/// </summary>
public sealed class LegalityChecker
{
    /// <summary>
    /// Optional provenance-based source classifier keyed by element internal-id.
    /// When set (by a layer with rules-DB access), it takes precedence over the
    /// source-string heuristic so PartFile / HouseRule classification can be
    /// derived from <c>part_provenance</c> / <c>part_registry</c> rather than a
    /// fragile substring match on the sourcebook name. Returning null falls back
    /// to the heuristic.
    /// </summary>
    public Func<RulesElement, LegalitySource?>? ProvenanceClassifier { get; set; }

    /// <summary>
    /// Check if a single element is legal for the current character state.
    /// </summary>
    public LegalityResult CheckElement(
        RulesElement element,
        Func<string, bool> hasElement,
        Func<string, int> getStatValue,
        int characterLevel = 1)
    {
        var source = DetermineSource(element);
        var printPrereqs = ExtractPrintPrereqs(element);

        if (string.IsNullOrWhiteSpace(element.Prereqs))
            return new LegalityResult(true, PrintPrereqs: printPrereqs, Source: source);

        var tree = PrereqParser.Parse(element.Prereqs);
        if (tree is null)
            return new LegalityResult(true, PrintPrereqs: printPrereqs, Source: source);

        bool met = PrereqEvaluator.Evaluate(tree, hasElement, characterLevel, getAbilityScore: getStatValue);

        if (met)
            return new LegalityResult(true, PrintPrereqs: printPrereqs, Source: source);

        string reason = DescribeFailure(tree, hasElement, getStatValue);
        return new LegalityResult(false, reason, printPrereqs, source);
    }

    /// <summary>
    /// Check all elements on a character for legality.
    /// Returns only those that are illegal.
    /// </summary>
    public IReadOnlyList<(RulesElement Element, LegalityResult Result)> CheckAll(
        IEnumerable<RulesElement> elements,
        Func<string, bool> hasElement,
        Func<string, int> getStatValue,
        int characterLevel = 1)
    {
        var illegal = new List<(RulesElement, LegalityResult)>();

        foreach (var element in elements)
        {
            var result = CheckElement(element, hasElement, getStatValue, characterLevel);
            if (!result.IsLegal)
                illegal.Add((element, result));
        }

        return illegal;
    }

    /// <summary>
    /// Create a prereq check callback suitable for CandidateFilter.
    /// </summary>
    public Func<RulesElement, bool> CreatePrereqFilter(
        Func<string, bool> hasElement,
        Func<string, int> getStatValue,
        int characterLevel = 1)
    {
        return element => CheckElement(element, hasElement, getStatValue, characterLevel).IsLegal;
    }

    /// <summary>
    /// Create a prereq check callback from a character state object.
    /// This is the preferred overload — passes all character context at once
    /// and supports all prereq types including "any X class".
    /// </summary>
    public Func<RulesElement, bool> CreatePrereqFilter(ICharacterState state)
    {
        return element =>
        {
            if (string.IsNullOrWhiteSpace(element.Prereqs))
                return true;

            var tree = PrereqParser.Parse(element.Prereqs);
            if (tree is null)
                return true;

            return PrereqEvaluator.Evaluate(tree, state);
        };
    }

    private LegalitySource DetermineSource(RulesElement element)
    {
        // Prefer provenance-based classification when a classifier is wired in.
        if (ProvenanceClassifier?.Invoke(element) is { } classified)
            return classified;

        if (element.Source is null)
            return LegalitySource.HouseRule;

        // Legacy/houserule heuristic: some hand-authored parts store a ".part"
        // filename in the source field. Provenance classification (above) is the
        // authoritative path; this remains as a fallback.
        if (element.Source.Contains(".part", StringComparison.OrdinalIgnoreCase))
            return LegalitySource.PartFile;

        return LegalitySource.RulesLegal;
    }

    private static string? ExtractPrintPrereqs(RulesElement element)
    {
        if (element.Fields.TryGetValue("print-prereqs", out var value))
            return value;
        if (element.Fields.TryGetValue("_print-prereqs", out value))
            return value;
        return null;
    }

    private static string DescribeFailure(
        PrereqNode node,
        Func<string, bool> hasElement,
        Func<string, int> getStatValue)
    {
        var failures = new List<string>();
        CollectFailures(node, hasElement, getStatValue, failures);
        return failures.Count > 0
            ? string.Join("; ", failures)
            : "Prerequisites not met";
    }

    private static void CollectFailures(
        PrereqNode node,
        Func<string, bool> hasElement,
        Func<string, int> getStatValue,
        List<string> failures)
    {
        switch (node)
        {
            case PrereqNode.HasElement has:
                if (has.Negate)
                {
                    if (hasElement(has.Name))
                        failures.Add($"Must not have {has.Name}");
                }
                else
                {
                    if (!hasElement(has.Name))
                        failures.Add($"Requires {has.Name}");
                }
                break;

            case PrereqNode.AbilityCheck ability:
                int score = getStatValue(ability.AbilityName);
                if (score < ability.MinScore)
                    failures.Add($"Requires {ability.AbilityName} {ability.MinScore} (have {score})");
                break;

            case PrereqNode.LevelCheck level:
                failures.Add($"Requires level {level.MinLevel}");
                break;

            case PrereqNode.Compound compound when compound.IsAnd:
                CollectFailures(compound.Left, hasElement, getStatValue, failures);
                CollectFailures(compound.Right, hasElement, getStatValue, failures);
                break;

            case PrereqNode.Compound compound: // OR
                bool leftMet = PrereqEvaluator.Evaluate(compound.Left, hasElement, getAbilityScore: getStatValue);
                bool rightMet = PrereqEvaluator.Evaluate(compound.Right, hasElement, getAbilityScore: getStatValue);
                if (!leftMet && !rightMet)
                {
                    var leftFailures = new List<string>();
                    var rightFailures = new List<string>();
                    CollectFailures(compound.Left, hasElement, getStatValue, leftFailures);
                    CollectFailures(compound.Right, hasElement, getStatValue, rightFailures);
                    failures.Add($"({string.Join(", ", leftFailures)}) or ({string.Join(", ", rightFailures)})");
                }
                break;
        }
    }
}
