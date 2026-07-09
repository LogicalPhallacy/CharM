using System.Xml;

namespace CharM.Compendium;

/// <summary>A rules-DB element the generator considers, minus engine coupling.</summary>
public readonly record struct RulesElementRef(string InternalId, string Name, string Type, string? Source);

/// <summary>A name-ambiguous element the generator skipped, surfaced for manual review.</summary>
public readonly record struct AmbiguousElement(string InternalId, string Name, string Type, IReadOnlyList<string> CandidateIds);

/// <summary>Outcome of a compendiumid part-file generation run.</summary>
public sealed class CompendiumIdPartResult
{
    /// <summary>Elements that got a <c>compendiumid</c> specific written.</summary>
    public int Emitted { get; set; }
    /// <summary>Elements skipped because the FMP url convention already links them.</summary>
    public int AlreadyAutoLinks { get; set; }
    /// <summary>Elements that resolved to no compendium entry (nothing to link).</summary>
    public int Unresolved { get; set; }
    /// <summary>Elements not relevant to the compendium at all (excluded).</summary>
    public int Irrelevant { get; set; }
    /// <summary>Name-ambiguous elements skipped pending manual review.</summary>
    public List<AmbiguousElement> Ambiguous { get; } = [];
}

/// <summary>
/// Generates a CharM-local CBLoader part file that attaches a
/// <c>&lt;specific name="compendiumid"&gt;</c> to every rules-DB element that
/// resolves to a compendium entry but whose internal-id does NOT already
/// auto-link under the OCB FMP url convention. Link generation then prefers the
/// specific and only falls back to the internal-id heuristic — so part-file
/// elements (ID_TIV/ID_HF/ID_MMA/… and the FMP catalog families) get correct
/// compendium links.
///
/// Only high-confidence resolutions (id match, or a unique in-category name
/// match) are emitted; name-ambiguous elements are collected for review rather
/// than guessed. The output is large and compendium-derived — keep it
/// gitignored, never committed.
/// </summary>
public static class CompendiumIdPartGenerator
{
    public static CompendiumIdPartResult Generate(
        IEnumerable<RulesElementRef> elements,
        CompendiumMatcher matcher,
        TextWriter output,
        string filename = "99-compendium-ids.part")
    {
        var result = new CompendiumIdPartResult();

        using var xml = XmlWriter.Create(output, new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            CloseOutput = false,
        });

        xml.WriteStartDocument();
        xml.WriteStartElement("D20Rules");
        xml.WriteAttributeString("game-system", "D&D4E");

        xml.WriteStartElement("UpdateInfo");
        xml.WriteElementString("Version", "1.0");
        xml.WriteElementString("Filename", filename);
        xml.WriteEndElement();
        xml.WriteElementString("Description",
            "CharM-generated compendium-id overlay: adds <specific name=\"compendiumid\"> " +
            "to elements whose internal-id doesn't carry the correct compendium id. " +
            "Compendium-derived data — do not commit.");

        foreach (var el in elements)
        {
            if (!CompendiumMatcher.IsCompendiumRelevant(el.InternalId, el.Type))
            {
                result.Irrelevant++;
                continue;
            }

            var match = matcher.Match(el.InternalId, el.Name, el.Type);

            if (match.Kind == CompendiumMatchKind.NameAmbiguous)
            {
                result.Ambiguous.Add(new AmbiguousElement(el.InternalId, el.Name, el.Type, match.CandidateIds));
                continue;
            }

            if (!match.IsMatched || match.CompendiumId is null)
            {
                result.Unresolved++;
                continue;
            }

            // Already auto-links via the FMP url convention → no specific needed.
            if (CompendiumIdMapper.FmpConventionCompendiumId(el.InternalId) == match.CompendiumId)
            {
                result.AlreadyAutoLinks++;
                continue;
            }

            xml.WriteStartElement("AppendNodes");
            xml.WriteAttributeString("name", el.Name);
            xml.WriteAttributeString("type", el.Type);
            xml.WriteAttributeString("internal-id", el.InternalId);
            xml.WriteStartElement("specific");
            xml.WriteAttributeString("name", "compendiumid");
            xml.WriteString(match.CompendiumId);
            xml.WriteEndElement(); // specific
            xml.WriteEndElement(); // AppendNodes
            result.Emitted++;
        }

        xml.WriteEndElement(); // D20Rules
        xml.WriteEndDocument();
        xml.Flush();

        return result;
    }
}
