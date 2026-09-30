using CharM.RulesDb.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CharM.RulesDb.Tests;

public sealed class RulesDbBuilderCorpusTests
{
    [Fact]
    [Trait("Category", "ManualCorpusRebuild")]
    public void Rebuild_canonical_rules_database_for_manual_use()
    {
        const string xmlPath = @"\\demdnas\backup\quick\4e\combined.dnd40";
        const string dbPath = @"C:\Projects\rules-fixed.db";

        if (File.Exists(dbPath))
            File.Delete(dbPath);

        RulesDbBuilder.Import(xmlPath, dbPath);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM rules_elements";
        Assert.Equal(38131L, (long)countCommand.ExecuteScalar()!);

        using var elementCommand = connection.CreateCommand();
        elementCommand.CommandText = """
            SELECT name
            FROM rules_elements
            WHERE internal_id = 'ID_FMP_POWER_704'
            """;
        Assert.Equal("Piercing Strike", (string)elementCommand.ExecuteScalar()!);
    }
}
