namespace CharM.Engine.Creation;

/// <summary>
/// Describes how CharM routes a build-choice <c>ElementType</c> through the
/// character creation wizard.
/// </summary>
/// <param name="ElementType">The recognized element type name.</param>
/// <param name="Step">The wizard step that handles the element type.</param>
/// <param name="SortOrder">The preferred order used when choosing the next pending slot.</param>
/// <param name="IsRecognized">
/// <see langword="true"/> when CharM recognizes the element type or a supported
/// element-type family; otherwise, <see langword="false"/>.
/// </param>
public sealed record ChoiceTypeMetadata(
    string ElementType,
    WizardStep Step,
    int SortOrder,
    bool IsRecognized);

/// <summary>
/// Describes CharM's recognized build-choice <c>ElementType</c> routing
/// vocabulary and preferred wizard ordering.
/// </summary>
/// <remarks>
/// Unknown element types remain supported and route to <see cref="WizardStep.Details"/>.
/// Names beginning with <c>Ability Score</c> form a recognized family that routes
/// to <see cref="WizardStep.AbilityScores"/> without enumerating fabricated literals
/// in <see cref="Known"/>.
/// </remarks>
public static class ChoiceTypeCatalog
{
    private const int FallbackSortOrder = 17;

    private static readonly IReadOnlyDictionary<string, ChoiceTypeMetadata> MetadataByElementType =
        CreateMetadataByElementType();

    /// <summary>
    /// Gets built-in literal routing metadata, ordered by the
    /// historical wizard priority and then alphabetically for unordered aliases.
    /// This is not the complete vocabulary of choice types in a loaded rules database.
    /// Corpus discovery belongs to the rules-database API; discovered types may be
    /// unrecognized here and still use the Details fallback.
    /// </summary>
    public static IReadOnlyList<ChoiceTypeMetadata> Known { get; } = Array.AsReadOnly(
        MetadataByElementType.Values
            .OrderBy(metadata => metadata.SortOrder)
            .ThenBy(metadata => metadata.ElementType, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    /// <summary>
    /// Describes how an arbitrary <c>ElementType</c> is routed and ordered.
    /// Unknown values route to <see cref="WizardStep.Details"/> with the fallback
    /// sort order and <see cref="ChoiceTypeMetadata.IsRecognized"/> set to
    /// <see langword="false"/>.
    /// </summary>
    public static ChoiceTypeMetadata Describe(string elementType)
    {
        ArgumentNullException.ThrowIfNull(elementType);

        if (MetadataByElementType.TryGetValue(elementType, out var metadata))
            return metadata;

        if (elementType.StartsWith("Ability Score", StringComparison.OrdinalIgnoreCase))
        {
            return new ChoiceTypeMetadata(
                elementType,
                WizardStep.AbilityScores,
                FallbackSortOrder,
                IsRecognized: true);
        }

        return new ChoiceTypeMetadata(
            elementType,
            WizardStep.Details,
            FallbackSortOrder,
            IsRecognized: false);
    }

    private static IReadOnlyDictionary<string, ChoiceTypeMetadata> CreateMetadataByElementType()
    {
        var metadata = new[]
        {
            KnownType("race", WizardStep.Race, 0),
            KnownType("hybrid class", WizardStep.Class, 1),
            KnownType("paragon path", WizardStep.ParagonPath, 2),
            KnownType("epic destiny", WizardStep.EpicDestiny, 3),
            KnownType("class feature", WizardStep.Class, 4),
            KnownType("class", WizardStep.Class, 5),
            KnownType("class build", WizardStep.Class, 6),
            KnownType("trait package", WizardStep.Class, 7),
            KnownType("proficiency", WizardStep.Class, 8),
            KnownType("ability scores", WizardStep.AbilityScores, 9),
            KnownType("skill training", WizardStep.Skills, 10),
            KnownType("racial trait", WizardStep.Race, 11),
            KnownType("race ability bonus", WizardStep.AbilityScores, 12),
            KnownType("background", WizardStep.Background, 13),
            KnownType("feat", WizardStep.Feats, 14),
            KnownType("power", WizardStep.Powers, 15),
            KnownType("language", WizardStep.Race, 16),
            KnownType("background choice", WizardStep.Background, FallbackSortOrder),
            KnownType("campaign setting", WizardStep.Background, FallbackSortOrder),
            KnownType("companion", WizardStep.Class, FallbackSortOrder),
            KnownType("countsasrace", WizardStep.Race, FallbackSortOrder),
            KnownType("god fragment", WizardStep.Class, FallbackSortOrder),
            KnownType("magic item", WizardStep.Class, FallbackSortOrder),
            KnownType("theme", WizardStep.Class, FallbackSortOrder),
        };

        return metadata.ToDictionary(
            item => item.ElementType,
            StringComparer.OrdinalIgnoreCase);
    }

    private static ChoiceTypeMetadata KnownType(string elementType, WizardStep step, int sortOrder) =>
        new(elementType, step, sortOrder, IsRecognized: true);
}
