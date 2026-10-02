using CharM.Engine.Creation;
using CharM.RulesDb.Import;
using CharM.RulesDb.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CharM.RulesDb.Tests;

public sealed class ChoiceTypeDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "charm-choice-types-" + Guid.NewGuid());
    private string DatabasePath => Path.Combine(_directory, "rules.db");

    public ChoiceTypeDiscoveryTests()
    {
        Directory.CreateDirectory(_directory);
        using var connection = OpenWritable();
        RulesDbSchema.Create(connection);
    }

    [Fact]
    public void Discovers_actual_select_types_with_counts_independent_of_element_types_and_routing()
    {
        Insert("""
            [{"$type":"select","elementType":"gender"},
             {"$type":"select","elementType":"Alignment","number":3},
             {"$type":"select","elementType":"Build"},
             {"$type":"select","elementType":"Deity"},
             {"$type":"grant","elementType":"Not a choice"}]
            """);
        Insert("""
            [{"$type":"select","elementType":"Gender"},
             {"$type":"select","elementType":"build"},
             {"$type":"select","elementType":"Build"}]
            """);

        using IRulesDatabase database = new RulesDatabase(DatabasePath);
        Assert.Equal(new[]
        {
            new ChoiceTypeOccurrence("Alignment", 1),
            new ChoiceTypeOccurrence("Build", 3),
            new ChoiceTypeOccurrence("Deity", 1),
            new ChoiceTypeOccurrence("Gender", 2),
        }, database.GetChoiceTypes());
        Assert.All(database.GetChoiceTypes(), type =>
        {
            Assert.DoesNotContain(ChoiceTypeCatalog.Known,
                known => string.Equals(known.ElementType, type.ElementType, StringComparison.OrdinalIgnoreCase));
            Assert.False(ChoiceTypeCatalog.Describe(type.ElementType).IsRecognized);
            Assert.Equal(WizardStep.Details, ChoiceTypeCatalog.Describe(type.ElementType).Step);
        });
    }

    [Fact]
    public void Deterministic_spelling_does_not_depend_on_record_order()
    {
        Insert("""[{"$type":"select","elementType":"gender"}]""");
        Insert("""[{"$type":"select","elementType":"Gender"}]""");
        using (var database = new RulesDatabase(DatabasePath))
            Assert.Equal(new ChoiceTypeOccurrence("Gender", 2), Assert.Single(database.GetChoiceTypes()));
        using (var connection = OpenWritable())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM rules_elements";
            command.ExecuteNonQuery();
        }
        Insert("""[{"$type":"select","elementType":"Gender"}]""");
        Insert("""[{"$type":"select","elementType":"gender"}]""");
        using var reopened = new RulesDatabase(DatabasePath);
        Assert.Equal(new ChoiceTypeOccurrence("Gender", 2), Assert.Single(reopened.GetChoiceTypes()));
    }

    [Fact]
    public void Skips_corrupt_records_and_invalid_type_fields_but_keeps_valid_siblings()
    {
        string?[] invalid = [null, "", " ", "null", "{}", "42", "[]", "[invalid",
            "[{\"$type\":\"select\",\"elementType\":\"Incomplete\"},"];
        foreach (var json in invalid)
            Insert(json);
        Insert("""
            [null, 42, {}, {"$type":42}, {"$type":"select"},
             {"$type":"select","elementType":null},
             {"$type":"select","elementType":42},
             {"$type":"select","elementType":" "},
             {"$type":"select","elementType":""},
             {"$type":"future-directive","elementType":"Not a select"},
             {"$type":"select","elementType":"Domain"}]
            """);
        using var database = new RulesDatabase(DatabasePath);
        Assert.Equal(new ChoiceTypeOccurrence("Domain", 1), Assert.Single(database.GetChoiceTypes()));
        Assert.Equal(invalid.Length + 1, database.Count);
    }

    [Fact]
    public void Cache_is_read_only_and_scoped_to_opened_database()
    {
        using (var database = new RulesDatabase(DatabasePath))
        {
            var first = database.GetChoiceTypes();
            Assert.Empty(first);
            Insert("""[{"$type":"select","elementType":"New Homebrew Type"}]""");
            Assert.Same(first, database.GetChoiceTypes());
            Assert.Throws<NotSupportedException>(() =>
                ((IList<ChoiceTypeOccurrence>)first).Add(new("Injected", 1)));
        }
        using var reopened = new RulesDatabase(DatabasePath);
        Assert.Equal(new ChoiceTypeOccurrence("New Homebrew Type", 1), Assert.Single(reopened.GetChoiceTypes()));
    }

    [Fact]
    public void Discovers_selects_serialized_by_import_and_part_overlay()
    {
        var xmlPath = Path.Combine(_directory, "base.xml");
        File.WriteAllText(xmlPath, """
            <D20Rules><RulesElement name="Base" type="Class" internal-id="base">
            <rules><select type="Alignment" /></rules></RulesElement></D20Rules>
            """);
        RulesDbBuilder.Import(xmlPath, DatabasePath);
        var partsPath = Path.Combine(_directory, "Homebrew");
        Directory.CreateDirectory(partsPath);
        File.WriteAllText(Path.Combine(partsPath, "custom.part"), """
            <D20Rules><RulesElement name="Homebrew" type="Class" internal-id="overlay">
            <rules><select type="Custom Homebrew Choice" /></rules></RulesElement></D20Rules>
            """);
        var merged = PartMerger.Merge(DatabasePath, partsPath);
        Assert.Equal(1, merged.ElementsAdded);
        using var database = new RulesDatabase(DatabasePath);
        Assert.Equal(new[]
        {
            new ChoiceTypeOccurrence("Alignment", 1),
            new ChoiceTypeOccurrence("Custom Homebrew Choice", 1),
        }, database.GetChoiceTypes());
    }

    private SqliteConnection OpenWritable()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private void Insert(string? rulesJson)
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rules_elements (internal_id, name, type, rules_json)
            VALUES ($id, 'Synthetic', 'Not a choice-slot type', $rules)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$rules", (object?)rulesJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Import/merge use pooled connections; release their handles before cleanup.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
