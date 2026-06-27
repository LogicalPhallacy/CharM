using CharM.Engine.Creation;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport.SummaryText.Blocks;

/// <summary>
/// "Background: &lt;Bg&gt;[, &lt;Bg2&gt;][ (&lt;BgChoice&gt;)]\r\n".
/// See decompiled <c>Background.cs</c>.
///
/// Backgrounds can carry parens inside their NAME (e.g. "Mournland (Cyre)").
/// The optional in-paren choice is always the LAST "(...)" group.
/// </summary>
internal sealed class BackgroundBlock : SummaryBlock
{
    private const string Prefix = "Background: ";

    public override string Write(CharacterSession session, IRulesDatabase database)
    {
        var backgrounds = session.GetSelectedElements("Background")
            .Select(b => b.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        if (backgrounds.Count == 0) return string.Empty;

        string body = string.Join(", ", backgrounds);
        string? choice = GetBackgroundChoice(session);
        if (!string.IsNullOrEmpty(choice))
            body += $" ({choice})";

        return $"{Prefix}{body}{Newline}";
    }

    /// <summary>
    /// Find the in-paren BackgroundChoice. OCB uses a dedicated stock type
    /// ("Background Specific Element"); our DB uses "Background Choice".
    /// We accept both names.
    /// </summary>
    private static string? GetBackgroundChoice(CharacterSession session)
    {
        foreach (var record in session.ChoiceHistory)
        {
            if (string.Equals(record.Element.Type, "Background Choice", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Element.Type, "Background Specific Element", StringComparison.OrdinalIgnoreCase))
                return record.Element.Name;
        }
        return null;
    }

    public override bool TryRead(CharacterSession session, IRulesDatabase database, ref string input)
    {
        if (!input.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        int nl = input.IndexOf(Newline, StringComparison.Ordinal);
        if (nl == -1) return false;

        string body = input[Prefix.Length..nl];

        // Detect trailing "(...)" and treat it as the BackgroundChoice ONLY
        // when the head before it resolves as Background(s). Otherwise treat
        // it as part of the name (Mournland (Cyre) is a real Background).
        string? bgChoice = null;
        if (body.EndsWith(")", StringComparison.Ordinal))
        {
            int lastOpen = body.LastIndexOf('(');
            if (lastOpen > 0)
            {
                string headCandidate = body[..lastOpen].TrimEnd();
                string parenCandidate = body[(lastOpen + 1)..^1];
                // Heuristic: split commas in head, try every token as a Background.
                var headTokens = SplitCommaTokens(headCandidate);
                if (headTokens.All(t => database.FindByNameAndType(t, "Background") is not null))
                {
                    body = headCandidate;
                    bgChoice = parenCandidate;
                }
            }
        }

        // Resolve background tokens.
        var tokens = SplitCommaTokens(body);
        var resolved = new List<CharM.Engine.Rules.RulesElement>();
        foreach (var t in tokens)
        {
            var el = database.FindByNameAndType(t, "Background");
            if (el is null) return false;
            resolved.Add(el);
        }

        foreach (var el in resolved)
            SessionReplayHelpers.PlacePositionally(session, el);

        if (!string.IsNullOrEmpty(bgChoice))
        {
            var choice = database.FindByNameAndType(bgChoice, "Background Specific Element");
            if (choice is not null)
                SessionReplayHelpers.PlacePositionally(session, choice);
        }

        input = input[(nl + Newline.Length)..];
        return true;
    }
}
