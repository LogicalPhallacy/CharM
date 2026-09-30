using CharM.Engine.Creation;
using Xunit;

namespace CharM.Engine.Tests;

public sealed class ChoiceTypeCatalogTests
{
    private static readonly (string ElementType, WizardStep Step)[] ExplicitMappings =
    [
        ("race", WizardStep.Race),
        ("racial trait", WizardStep.Race),
        ("countsasrace", WizardStep.Race),
        ("class", WizardStep.Class),
        ("class feature", WizardStep.Class),
        ("hybrid class", WizardStep.Class),
        ("proficiency", WizardStep.Class),
        ("class build", WizardStep.Class),
        ("trait package", WizardStep.Class),
        ("god fragment", WizardStep.Class),
        ("magic item", WizardStep.Class),
        ("companion", WizardStep.Class),
        ("theme", WizardStep.Class),
        ("paragon path", WizardStep.ParagonPath),
        ("epic destiny", WizardStep.EpicDestiny),
        ("background", WizardStep.Background),
        ("background choice", WizardStep.Background),
        ("campaign setting", WizardStep.Background),
        ("skill training", WizardStep.Skills),
        ("feat", WizardStep.Feats),
        ("power", WizardStep.Powers),
        ("race ability bonus", WizardStep.AbilityScores),
        ("ability scores", WizardStep.AbilityScores),
        ("language", WizardStep.Race),
    ];

    private static readonly string[] HistoricalTypeOrder =
    [
        "race",
        "hybrid class",
        "paragon path",
        "epic destiny",
        "class feature",
        "class",
        "class build",
        "trait package",
        "proficiency",
        "ability scores",
        "skill training",
        "racial trait",
        "race ability bonus",
        "background",
        "feat",
        "power",
        "language",
    ];

    [Fact]
    public void Known_contains_every_explicit_mapping_with_its_existing_step()
    {
        Assert.Equal(ExplicitMappings.Length, ChoiceTypeCatalog.Known.Count);

        foreach (var (elementType, expectedStep) in ExplicitMappings)
        {
            var metadata = Assert.Single(
                ChoiceTypeCatalog.Known,
                item => string.Equals(item.ElementType, elementType, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(expectedStep, metadata.Step);
            Assert.True(metadata.IsRecognized);
            Assert.Equal(expectedStep, CharacterCreationWizard.MapTypeToStep(elementType));
        }
    }

    [Fact]
    public void Historical_types_keep_their_exact_priority_order()
    {
        var ordered = HistoricalTypeOrder
            .Select(ChoiceTypeCatalog.Describe)
            .OrderBy(metadata => metadata.SortOrder)
            .Select(metadata => metadata.ElementType);

        Assert.Equal(HistoricalTypeOrder, ordered);
        Assert.Equal(
            Enumerable.Range(0, HistoricalTypeOrder.Length),
            HistoricalTypeOrder.Select(type => ChoiceTypeCatalog.Describe(type).SortOrder));
    }

    [Fact]
    public void Built_in_unordered_types_follow_historical_types()
    {
        string[] expected =
        [
            "background choice",
            "campaign setting",
            "companion",
            "countsasrace",
            "god fragment",
            "magic item",
            "theme",
        ];

        var unordered = ChoiceTypeCatalog.Known
            .Where(metadata => metadata.SortOrder >= HistoricalTypeOrder.Length)
            .ToArray();

        Assert.Equal(expected, unordered.Select(metadata => metadata.ElementType));
        Assert.All(unordered, metadata => Assert.Equal(HistoricalTypeOrder.Length, metadata.SortOrder));
    }

    [Theory]
    [InlineData("future choice type")]
    [InlineData("Alignment")]
    [InlineData("Gender")]
    [InlineData("Build")]
    [InlineData("Deity")]
    [InlineData("Domain")]
    public void Types_outside_built_in_metadata_use_details_fallback(string elementType)
    {
        var metadata = ChoiceTypeCatalog.Describe(elementType);

        Assert.DoesNotContain(ChoiceTypeCatalog.Known,
            item => string.Equals(item.ElementType, elementType, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(elementType, metadata.ElementType);
        Assert.Equal(WizardStep.Details, metadata.Step);
        Assert.Equal(HistoricalTypeOrder.Length, metadata.SortOrder);
        Assert.False(metadata.IsRecognized);
        Assert.Equal(WizardStep.Details, CharacterCreationWizard.MapTypeToStep(elementType));
    }

    [Theory]
    [InlineData("RACE", WizardStep.Race)]
    [InlineData("Class Feature", WizardStep.Class)]
    [InlineData("BACKGROUND CHOICE", WizardStep.Background)]
    public void Lookup_is_case_insensitive(string elementType, WizardStep expectedStep)
    {
        var metadata = ChoiceTypeCatalog.Describe(elementType);

        Assert.Equal(expectedStep, metadata.Step);
        Assert.True(metadata.IsRecognized);
        Assert.Equal(expectedStep, CharacterCreationWizard.MapTypeToStep(elementType));
    }

    [Theory]
    [InlineData("Ability Score")]
    [InlineData("Ability Score Strength")]
    [InlineData("ability score adjustment")]
    public void Ability_score_prefix_is_a_recognized_family(string elementType)
    {
        var metadata = ChoiceTypeCatalog.Describe(elementType);

        Assert.Equal(elementType, metadata.ElementType);
        Assert.Equal(WizardStep.AbilityScores, metadata.Step);
        Assert.Equal(HistoricalTypeOrder.Length, metadata.SortOrder);
        Assert.True(metadata.IsRecognized);
        Assert.Equal(WizardStep.AbilityScores, CharacterCreationWizard.MapTypeToStep(elementType));
    }

    [Fact]
    public void Known_has_no_case_insensitive_duplicates()
    {
        var duplicate = ChoiceTypeCatalog.Known
            .GroupBy(metadata => metadata.ElementType, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        Assert.Null(duplicate);
    }
}
