using CharM.Engine.Creation;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport.SummaryText.Extensions;

/// <summary>
/// Optional CharM-specific section appended AFTER the OCB end marker.
/// Legacy OCB stops reading at the end marker, so anything we write here is
/// forward-compatible — older tools see and ignore it.
///
/// Currently emits a simple "key: value" line block headed by the
/// versioned banner. Reserved for things OCB didn't carry (portrait,
/// money tracking, persisted current HP / surges / power-points, deity
/// when not surfaced by ClassChoices, etc.).
/// </summary>
internal static class CharMExtensionsBlock
{
    public const string Header = "====== CharM Extensions v1 ======\r\n";

    public static string Write(CharacterSession session, IRulesDatabase database)
    {
        var lines = new List<string>();

        foreach (var (k, v) in session.Details.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            lines.Add($"{k}: {v}");
        }

        var skip = new HashSet<string>(StringComparer.Ordinal) { "Name" };
        foreach (var (k, v) in session.TextStrings.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (skip.Contains(k)) continue;
            if (string.IsNullOrWhiteSpace(v)) continue;
            lines.Add($"TextString[{k}]: {v}");
        }

        // Build provenance: record the enabled part layers (id + version) so the
        // character can be audited against whatever rules DB it is opened under.
        // Only emitted when the session actually carries provenance (CharM-built
        // or round-tripped), so importing a plain OCB summary and re-exporting
        // adds nothing.
        foreach (var part in session.BuildProvenance)
            lines.Add($"{PartLinePrefix}{part.PartId}|{part.Version}|{part.Category}");

        if (lines.Count == 0) return string.Empty;
        return Header + string.Join(SummaryBlock.Newline, lines) + SummaryBlock.Newline;
    }

    private const string PartLinePrefix = "Part: ";

    /// <summary>
    /// Parse the recorded part-provenance lines out of an extensions body into
    /// <see cref="RecordedPart"/> entries. Other extension lines are ignored.
    /// </summary>
    public static IReadOnlyList<RecordedPart> ParseProvenance(string? extensionsBody)
    {
        if (string.IsNullOrEmpty(extensionsBody)) return [];

        var parts = new List<RecordedPart>();
        foreach (var raw in extensionsBody.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(PartLinePrefix, StringComparison.Ordinal)) continue;

            var payload = line[PartLinePrefix.Length..];
            var cols = payload.Split('|');
            if (cols.Length < 1 || string.IsNullOrWhiteSpace(cols[0])) continue;

            parts.Add(new RecordedPart(
                cols[0],
                cols.Length > 1 && cols[1].Length > 0 ? cols[1] : null,
                cols.Length > 2 && cols[2].Length > 0 ? cols[2] : null));
        }
        return parts;
    }

    /// <summary>
    /// Split the extensions section off the tail of <paramref name="text"/>.
    /// On return, <paramref name="text"/> is the OCB portion (up to and
    /// including the end marker); the captured extensions body is returned
    /// for parsing by the importer.
    /// </summary>
    public static string? SplitExtensions(ref string text)
    {
        int idx = text.IndexOf(Header, StringComparison.Ordinal);
        if (idx == -1) return null;

        string ext = text[(idx + Header.Length)..];
        text = text[..idx];
        return ext;
    }
}
