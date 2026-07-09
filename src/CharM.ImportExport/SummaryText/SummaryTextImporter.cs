using CharM.Engine.Creation;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport.SummaryText;

/// <summary>
/// Parses an OCB SummaryText string back into a populated
/// <see cref="CharacterSession"/> by replaying each block's encoded choice
/// through <see cref="CharacterCreationWizard"/>. Mirrors OCB's reader
/// rotation loop (try every block, rotate first line on no-match).
/// </summary>
public static class SummaryTextImporter
{
    public sealed record ImportResult(
        CharacterSession Session,
        IReadOnlyList<string> UnconsumedLines,
        IReadOnlyList<string> UnresolvedNames);

    /// <summary>Import the SummaryText into a fresh session built off <paramref name="database"/>.</summary>
    public static ImportResult Import(
        string text,
        IRulesDatabase database,
        SummaryTextImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(database);
        options ??= SummaryTextImportOptions.Default;

        // Strip CharM extensions block (after OCB footer) before driving the
        // OCB blocks — the extensions block has its own parser.
        string ocbText = text;
        var ext = Extensions.CharMExtensionsBlock.SplitExtensions(ref ocbText);

        // Start at level 1 — NameLevel block will SetLevel(N) when it sees
        // "level N" on the second line.
        var session = new CharacterSession(
            database.FindByInternalId,
            database.FindByNameAndType,
            (type, includeRules) => database.FindByType(type, includeRules),
            (type, source, includeRules) => database.FindByTypeAndSource(type, source, includeRules),
            level: 1);

        var unconsumed = SummaryTextDriver.Replay(session, database, ocbText);

        // Restore recorded part provenance from the CharM extensions block so
        // the character can be audited against the current rules database.
        foreach (var part in Extensions.CharMExtensionsBlock.ParseProvenance(ext))
            session.BuildProvenance.Add(part);

        return new ImportResult(session, unconsumed, Array.Empty<string>());
    }
}

public sealed record SummaryTextImportOptions
{
    public static readonly SummaryTextImportOptions Default = new();
}
