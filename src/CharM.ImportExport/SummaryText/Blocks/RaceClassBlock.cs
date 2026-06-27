using CharM.Engine.Creation;
using CharM.Engine.Rules;
using CharM.Engine.Selection;
using CharM.RulesDb.Storage;

namespace CharM.ImportExport.SummaryText.Blocks;

/// <summary>
/// "&lt;Race&gt;, &lt;Class&gt;[, &lt;ParagonPath&gt;][, &lt;EpicDestiny&gt;]\r\n".
/// For hybrid characters, Class is rendered as "&lt;A&gt;|&lt;B&gt;".
/// See decompiled <c>RaceClass.cs</c>.
/// </summary>
internal sealed class RaceClassBlock : SummaryBlock
{
    public override string Write(CharacterSession session, IRulesDatabase database)
    {
        string race = session.GetSelectedElement("Race")?.Name ?? string.Empty;
        string cls = GetClassName(session);
        string paragon = session.GetSelectedElement("Paragon Path")?.Name ?? string.Empty;
        string ed = session.GetSelectedElement("Epic Destiny")?.Name ?? string.Empty;

        string line = string.Empty;
        CommaAppend(ref line, race);
        CommaAppend(ref line, cls);
        CommaAppend(ref line, paragon);
        CommaAppend(ref line, ed);
        if (line.Length == 0) return string.Empty;
        return line + Newline;
    }

    private static string GetClassName(CharacterSession session)
    {
        // Hybrid: prefer the two Hybrid Class picks joined with "|".
        var hybrids = session.GetSelectedElements("Hybrid Class");
        if (hybrids.Count >= 1)
        {
            // Trim the "Hybrid " prefix WotC ships on most of these so the
            // output is "Psion|Vampire" not "Hybrid Psion|Hybrid Vampire".
            static string Trim(string name) => name.StartsWith("Hybrid ", StringComparison.OrdinalIgnoreCase)
                ? name["Hybrid ".Length..] : name;
            return string.Join("|", hybrids.Select(h => Trim(h.Name)));
        }

        return session.GetSelectedElement("Class")?.Name ?? string.Empty;
    }

    public override bool TryRead(CharacterSession session, IRulesDatabase database, ref string input)
    {
        int nl = input.IndexOf(Newline, StringComparison.Ordinal);
        if (nl == -1) return false;
        string line = input[..nl];

        // The line is N comma-separated tokens; each must resolve as a Race,
        // Class, Paragon Path, Epic Destiny, or Hybrid pair ("A|B"). If
        // anything fails to resolve, we don't claim the line.
        var tokens = SplitCommaTokens(line);
        var resolved = new List<(RulesElement Element, string? HybridA, string? HybridB)>();

        foreach (var token in tokens)
        {
            int bar = token.IndexOf('|');
            if (bar > 0)
            {
                string a = token[..bar].Trim();
                string b = token[(bar + 1)..].Trim();
                var hybA = database.FindByNameAndType("Hybrid " + a, "Hybrid Class")
                           ?? database.FindByNameAndType(a, "Hybrid Class");
                var hybB = database.FindByNameAndType("Hybrid " + b, "Hybrid Class")
                           ?? database.FindByNameAndType(b, "Hybrid Class");
                if (hybA is null || hybB is null) return false;
                var hybridParent = database.FindByNameAndType("Hybrid", "Class");
                if (hybridParent is null) return false;
                resolved.Add((hybridParent, hybA.InternalId, hybB.InternalId));
                continue;
            }

            RulesElement? element = null;
            foreach (var type in new[] { "Race", "Class", "Paragon Path", "Epic Destiny" })
            {
                element = database.FindByNameAndType(token, type);
                if (element is not null) break;
            }
            if (element is null) return false;
            resolved.Add((element, null, null));
        }

        // All tokens resolve — apply.
        foreach (var (element, hybA, hybB) in resolved)
        {
            // TODO(importer-driver wave): wire up actual choice replay via wizard slots.
            // For Phase-1 we just record the intent on the session via choice metadata.
            SessionReplayHelpers.PlacePositionally(session, element);
            if (hybA is not null && hybB is not null)
            {
                var ha = database.FindByInternalId(hybA);
                var hb = database.FindByInternalId(hybB);
                if (ha is not null) SessionReplayHelpers.PlacePositionally(session, ha);
                if (hb is not null) SessionReplayHelpers.PlacePositionally(session, hb);
            }
        }

        input = input[(nl + Newline.Length)..];
        return true;
    }
}
