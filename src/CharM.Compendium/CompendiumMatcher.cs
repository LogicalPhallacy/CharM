namespace CharM.Compendium;

public enum CompendiumMatchKind
{
    /// <summary>Id maps to an existing entry whose name also agrees. Highest confidence.</summary>
    IdNameAgree,
    /// <summary>Id maps to an existing entry, but the name differs (common for FMP formatting).</summary>
    IdOnly,
    /// <summary>No id match, but the normalized name resolves to exactly one entry in-category.</summary>
    NameUnique,
    /// <summary>No id match, and the name resolves to more than one entry in-category.</summary>
    NameAmbiguous,
    /// <summary>No id or name match.</summary>
    None,
}

/// <summary>Resolution of a single rules-DB element to the compendium.</summary>
public sealed record CompendiumMatch(
    string InternalId,
    string Name,
    string Type,
    string Namespace,
    string? Category,
    string? CompendiumId,
    CompendiumMatchKind Kind,
    IReadOnlyList<string> CandidateIds,
    string? IdImpliedCompendiumId)
{
    public bool IsMatched => Kind is CompendiumMatchKind.IdNameAgree
        or CompendiumMatchKind.IdOnly or CompendiumMatchKind.NameUnique;

    /// <summary>
    /// True when the element resolved to a compendium entry whose numeric id is
    /// NOT carried anywhere in the internal-id — i.e. the internal-id doesn't
    /// encode the compendium number at all. Robust to id suffixes/variants
    /// (ID_FMP_POWER_14206.1, ..._RANGER_VERSION) and unmapped tokens
    /// (ID_FMP_SUBRACE_54 → race54): those still "carry" their number and are not
    /// flagged. Genuine cases are part-local ids (ID_TIV_..., ID_MMA_FEAT_000003
    /// → feat3595) that use a number unrelated to the compendium id.
    /// </summary>
    public bool HasIdNumberMismatch
    {
        get
        {
            if (!IsMatched || CompendiumId is null) return false;
            int num = CompendiumIdMapper.NumericSuffix(CompendiumId);
            return num >= 0 && !InternalIdCarriesNumber(InternalId, num);
        }
    }

    /// <summary>Whether <paramref name="num"/> appears as a delimited numeric segment in the id.</summary>
    private static bool InternalIdCarriesNumber(string internalId, int num)
    {
        var s = num.ToString();
        int idx = 0;
        while ((idx = internalId.IndexOf(s, idx, StringComparison.Ordinal)) >= 0)
        {
            int before = idx - 1;
            int after = idx + s.Length;
            bool digitBefore = before >= 0 && char.IsDigit(internalId[before]);
            bool digitAfter = after < internalId.Length && char.IsDigit(internalId[after]);
            bool delimBefore = before < 0 || internalId[before] == '_';
            if (!digitBefore && !digitAfter && delimBefore) return true;
            idx = after;
        }
        return false;
    }
}

/// <summary>
/// Resolves rules-DB elements to compendium entries, first by the OCB id
/// heuristic and then, when that fails, by category-scoped name matching. Name
/// matching is what bridges the ~23k part-file elements whose ids don't map.
/// </summary>
public sealed class CompendiumMatcher(ICompendiumSource reader)
{
    private readonly ICompendiumSource _reader = reader;

    public CompendiumMatch Match(string internalId, string name, string type)
    {
        var ns = CompendiumIdMapper.Namespace(internalId);

        // The compendium id the internal-id number implies (may point at a
        // wrong/absent entry for part namespaces); recorded on every result so
        // callers can flag ids that don't carry the correct compendium number.
        var idImplied = CompendiumIdMapper.TryMapToCompendiumId(internalId);

        // 1) Id heuristic. The OCB compendium-id convention only holds for the
        //    official ID_FMP namespace; CBLoader part namespaces (ID_TIV,
        //    ID_DRAG427, ID_MMA, …) mint their own part-local numbers, so a
        //    part element's number collides with an unrelated compendium entry
        //    (e.g. ID_DRAG427_FEAT_1 "Reaper's Blade" maps to feat1 "Back to the
        //    Wall"). The compendium-id convention only holds for ID_FMP *rules*
        //    categories (power, feat, deity, …). The FMP *catalog* families
        //    (gear/magic-item/weapon/armor/implement) keep an independent DB
        //    id-space, so their number also collides (e.g. ID_FMP_GEAR_103
        //    "Hunter's Kit" maps to item103 "Jacinth of Inestimable Beauty").
        //    So trust an id-mapped candidate outright only when it is an ID_FMP
        //    id-convention category; otherwise require the name to agree, else
        //    fall through to name matching below.
        if (CompendiumIdMapper.TryMap(internalId, out var idCat, out var num)
            && _reader.HasCategory(idCat))
        {
            var candidate = idCat + num;
            var entry = _reader.GetListing(idCat).FindById(candidate);
            if (entry is not null)
            {
                bool nameAgrees = CompendiumName.Matches(name, entry.Name);
                bool idTrustworthy = ns == "ID_FMP" && CompendiumIdMapper.IsIdConventionCategory(idCat);
                if (nameAgrees || idTrustworthy)
                {
                    var kind = nameAgrees ? CompendiumMatchKind.IdNameAgree : CompendiumMatchKind.IdOnly;
                    return new CompendiumMatch(internalId, name, type, ns, idCat, candidate, kind, [candidate], idImplied);
                }
            }
        }

        // 2) Name matching, scoped to the element's category. Prefer the
        //    id-token category if the id decomposed; else fall back to type.
        //    Try the full name first, then prefix-stripped variants.
        var category = ResolveCategory(internalId, type);
        if (category is not null && _reader.HasCategory(category))
        {
            var listing = _reader.GetListing(category);
            foreach (var norm in CompendiumName.NormalizedVariants(name))
            {
                var hits = listing.FindByNormalizedName(norm);
                if (hits.Count == 1)
                    return new CompendiumMatch(internalId, name, type, ns, category, hits[0].Id, CompendiumMatchKind.NameUnique, [hits[0].Id], idImplied);
                if (hits.Count > 1)
                    return new CompendiumMatch(internalId, name, type, ns, category, null, CompendiumMatchKind.NameAmbiguous, [.. hits.Select(h => h.Id)], idImplied);

                // Fall back to the global name index, filtered to this category.
                if (_reader.GlobalNameIndex.TryGetValue(norm, out var globalIds))
                {
                    var inCat = globalIds.Where(id => id.StartsWith(category, StringComparison.Ordinal)).Distinct().ToArray();
                    if (inCat.Length == 1)
                        return new CompendiumMatch(internalId, name, type, ns, category, inCat[0], CompendiumMatchKind.NameUnique, inCat, idImplied);
                    if (inCat.Length > 1)
                        return new CompendiumMatch(internalId, name, type, ns, category, null, CompendiumMatchKind.NameAmbiguous, inCat, idImplied);
                }
            }
        }

        return new CompendiumMatch(internalId, name, type, ns, category, null, CompendiumMatchKind.None, [], idImplied);
    }

    /// <summary>
    /// Whether this element's type maps to any compendium category at all.
    /// Elements with no analogue (Internal, Category, Build, source, …) are
    /// excluded from coverage denominators.
    /// </summary>
    public static bool IsCompendiumRelevant(string internalId, string type) =>
        ResolveCategory(internalId, type) is not null;

    private static string? ResolveCategory(string internalId, string type)
    {
        if (CompendiumIdMapper.TryMap(internalId, out var idCat, out _))
            return idCat;

        // Sub-races: the compendium lists Forgotten Realms / Dragon-magazine
        // sub-races as standalone "race" entries, but the DB models them as
        // "Racial Trait" elements with a "_SUBRACE_" internal-id (e.g.
        // ID_FMP_SUBRACE_56 "Moon Elf (Eladrin)", ID_TIV_SUBRACE_DRAGON_405_1).
        // Scope those to the race category so they match instead of reading as
        // orphans.
        if (internalId.Contains("_SUBRACE_", StringComparison.Ordinal))
            return "race";

        return CompendiumIdMapper.DbTypeToCategory.TryGetValue(type, out var cat) ? cat : null;
    }
}
