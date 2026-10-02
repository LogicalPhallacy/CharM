using CharM.RulesDb.Import;
using Xunit;

namespace CharM.RulesDb.Tests;

public sealed class RulesXmlReaderTests
{
    [Theory]
    [InlineData("Prereqs", false)]
    [InlineData("Prereqs", true)]
    [InlineData("Category", false)]
    [InlineData("Category", true)]
    public void ReadAll_yields_next_element_after_text_child(
        string finalChildName,
        bool prettyPrinted)
    {
        string separator = prettyPrinted ? "\r\n  " : string.Empty;
        string indentation = prettyPrinted ? "    " : string.Empty;
        string xml = $"""
            <D20Rules>{separator}<RulesElement name="First" type="Power" internal-id="first">{separator}{indentation}<{finalChildName}>foo</{finalChildName}>{separator}</RulesElement>{separator}<RulesElement name="Second" type="Power" internal-id="second" />{separator}</D20Rules>
            """;
        string path = Path.Combine(Path.GetTempPath(), $"charm-rules-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(path, xml);

            var parsed = RulesXmlReader.ReadAll(path).ToArray();

            Assert.Equal(["first", "second"], parsed.Select(item => item.Element.InternalId));
            Assert.Equal("Second", parsed[1].Element.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
