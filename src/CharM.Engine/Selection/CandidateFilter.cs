using CharM.Engine.Rules;

namespace CharM.Engine.Selection;

/// <summary>
/// Full filtering pipeline for finding valid candidates for a SelectDirective.
/// Combines element queries, category expression matching, prerequisite evaluation,
/// and requires checking into a single pipeline.
///
/// Uses functional callbacks to query the rules database, keeping the Engine
/// project free of storage-layer dependencies.
/// </summary>
public sealed class CandidateFilter
{
    private readonly Func<string, IEnumerable<RulesElement>>? _findByType;
    private readonly Func<string, string, IEnumerable<RulesElement>>? _findByTypeAndSource;
    private readonly Func<string, bool, IEnumerable<RulesElement>>? _findByTypeWithRules;
    private readonly Func<string, string, bool, IEnumerable<RulesElement>>? _findByTypeAndSourceWithRules;

    /// <summary>
    /// Create a filter with database query callbacks.
    /// </summary>
    /// <param name="findByType">Returns all elements of a given type.</param>
    /// <param name="findByTypeAndSource">Returns elements of a given type from a given source.</param>
    public CandidateFilter(
        Func<string, IEnumerable<RulesElement>> findByType,
        Func<string, string, IEnumerable<RulesElement>>? findByTypeAndSource = null)
    {
        _findByType = findByType;
        _findByTypeAndSource = findByTypeAndSource;
    }

    /// <summary>
    /// Create a filter with database query callbacks that can choose whether
    /// to load full rules for each element.
    /// When includeRules is false, implementations may skip expensive
    /// rules_json deserialization and only load lightweight headers.
    /// </summary>
    /// <param name="findByType">
    /// Returns all elements of a given type. The boolean parameter indicates
    /// whether full rules are needed (true) or a lightweight view is
    /// sufficient (false).
    /// </param>
    /// <param name="findByTypeAndSource">
    /// Returns elements of a given type from a given source. The boolean
    /// parameter indicates whether full rules are needed (true) or a
    /// lightweight view is sufficient (false).
    /// </param>
    public CandidateFilter(
        Func<string, bool, IEnumerable<RulesElement>> findByType,
        Func<string, string, bool, IEnumerable<RulesElement>>? findByTypeAndSource = null)
    {
        _findByTypeWithRules = findByType;
        _findByTypeAndSourceWithRules = findByTypeAndSource;
    }

    /// <summary>
    /// Find all valid candidates for a given select directive.
    /// </summary>
    /// <param name="select">The select directive defining what to choose from.</param>
    /// <param name="variables">Variable substitutions (e.g., $$CLASS → class ID).</param>
    /// <param name="sourceFilter">Optional source book filter (e.g., "Player's Handbook").</param>
    /// <param name="prereqCheck">Optional callback to evaluate element prerequisites against character state.</param>
    /// <param name="hasElement">Optional callback to check if character has a named element (for Existing filter).</param>
    /// <param name="characterLevel">Current character level for requires evaluation.</param>
    /// <param name="sourceAllowed">
    /// Optional predicate over an element's <c>Source</c> deciding whether it is
    /// allowed by the currently enabled sourcebook set. When null, all sources
    /// are allowed. This is the multi-sourcebook enable/disable filter (distinct
    /// from <paramref name="sourceFilter"/>, which narrows the DB query to a
    /// single source).
    /// </param>
    public IReadOnlyList<RulesElement> FindCandidates(
        SelectDirective select,
        Dictionary<string, string>? variables = null,
        string? sourceFilter = null,
        Func<RulesElement, bool>? prereqCheck = null,
        Func<string, bool>? hasElement = null,
        int characterLevel = 1,
        Func<string, string?>? resolveNameToId = null,
        Func<string?, bool>? sourceAllowed = null)
    {
        // 1. Query by ElementType (with optional source filter)
        bool needsRules = prereqCheck is not null;
        IEnumerable<RulesElement> candidates;

        if (_findByTypeWithRules is not null)
        {
            candidates =
                sourceFilter is not null && _findByTypeAndSourceWithRules is not null
                    ? _findByTypeAndSourceWithRules(select.ElementType, sourceFilter, needsRules)
                    : _findByTypeWithRules(select.ElementType, needsRules);
        }
        else if (_findByType is not null)
        {
            candidates =
                sourceFilter is not null && _findByTypeAndSource is not null
                    ? _findByTypeAndSource(select.ElementType, sourceFilter)
                    : _findByType(select.ElementType);
        }
        else
        {
            throw new System.InvalidOperationException("CandidateFilter is not configured with query callbacks.");
        }

        // 2-5. Apply filters
        var filtered = new List<RulesElement>();

        foreach (var candidate in candidates)
        {
            int? elementLevel = GetElementLevel(candidate);

            // 2. Category expression filter
            if (!CategoryMatcher.Matches(select.Category, candidate, variables, elementLevel, resolveNameToId))
                continue;

            // 2b. Enabled-sourcebook filter (multi-source enable/disable).
            if (sourceAllowed is not null && !sourceAllowed(candidate.Source))
                continue;

            // 3. Existing filter: only elements already on the character
            if (select.Existing)
            {
                if (hasElement is null || !hasElement(candidate.Name))
                    continue;
            }

            // 4. Prereq check (element's own prerequisites against character state)
            if (prereqCheck is not null && !prereqCheck(candidate))
                continue;

            filtered.Add(candidate);
        }

        // 6. Sort by Name
        filtered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return filtered;
    }

    /// <summary>
    /// Extract the element's level from its Fields dictionary.
    /// Checks "Level" then "_Level" fields.
    /// </summary>
    public static int? GetElementLevel(RulesElement element)
    {
        if (element.Fields.TryGetValue("Level", out var levelStr) && int.TryParse(levelStr, out int level))
            return level;

        if (element.Fields.TryGetValue("_Level", out levelStr) && int.TryParse(levelStr, out level))
            return level;

        return null;
    }
}
