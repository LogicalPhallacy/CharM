namespace CharM.Compendium;

/// <summary>An id remap: iws currently files this element under <see cref="FromId"/>; align it to <see cref="ToId"/>.</summary>
public readonly record struct IwsIdRemap(string Category, string Name, string FromId, string ToId);

/// <summary>An entry the authoritative source has that iws lacks (to be authored into iws).</summary>
public sealed record IwsNewEntry(
    string Category, string Id, string Name,
    IReadOnlyDictionary<string, string> Fields,
    string? BodyHtml, string? IndexText);

/// <summary>A name that couldn't be aligned unambiguously (surfaced for review, never guessed).</summary>
public readonly record struct IwsAmbiguous(string Category, string Name, string JsonpId, IReadOnlyList<string> RebornIds, string Reason);

public sealed class IwsAlignmentSummary
{
    public int Categories { get; set; }
    public int IdRemaps { get; set; }
    public int NewEntries { get; set; }
    public int Ambiguous { get; set; }
    public int AlreadyAligned { get; set; }
}

public sealed class IwsAlignment
{
    public List<IwsIdRemap> IdRemaps { get; } = [];
    public List<IwsNewEntry> NewEntries { get; } = [];
    public List<IwsAmbiguous> Ambiguous { get; } = [];
    public IwsAlignmentSummary Summary { get; } = new();
}

/// <summary>
/// Computes how to bring the iws.mx JSONP dump into id/completeness alignment
/// with the reborn-authoritative superset, per element type (category). Only
/// numeric-id alignment for the SAME category matters; category-name divergence
/// between the dumps is deliberately ignored.
///
/// Produces:
/// <list type="bullet">
///   <item><b>id remaps</b>: an iws entry whose name uniquely matches one
///     reborn entry under a different id → align iws's id to reborn's.</item>
///   <item><b>new entries</b>: a reborn entry (in a category iws already has)
///     that iws lacks entirely → author it into iws with the authoritative
///     field payload.</item>
///   <item><b>ambiguous</b>: names that match multiple candidates, or whose
///     target id already exists in iws — left for manual review, not guessed.</item>
/// </list>
/// </summary>
public static class IwsAlignmentBuilder
{
    public static IwsAlignment Build(IReadOnlyList<SupersetEntry> superset, ICompendiumSource jsonp)
    {
        var result = new IwsAlignment();

        // Reborn-authoritative entries grouped by category (these carry the ids
        // we want iws to converge on).
        var rebornAuthoritative = superset
            .Where(e => e.Sources.HasFlag(CompendiumSourceFlags.Reborn))
            .ToList();
        var rebornByCategory = rebornAuthoritative
            .GroupBy(e => e.Category, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Global (compendium-number, normalized-name) set across ALL Reborn
        // categories. An iws entry whose own number + name already matches a
        // Reborn entry is already aligned even when the two dumps file it under
        // different categories (e.g. the Heroes of Shadow assassin poisons iws
        // lists under "poison" but Reborn lists under "item", with the same
        // number). Category divergence is acceptable — only the number matters.
        var rebornNumberName = new HashSet<(int, string)>();
        foreach (var e in rebornAuthoritative)
        {
            var num = CompendiumIdMapper.NumericSuffix(e.Id);
            var en = CompendiumName.Normalize(e.Name);
            if (num >= 0 && en.Length > 0) rebornNumberName.Add((num, en));
        }

        // Global (number, name) set for iws across ALL categories, so a Reborn
        // entry iws already carries under a different category isn't re-emitted
        // as a spurious new entry.
        var jsonpNumberName = new HashSet<(int, string)>();
        foreach (var cat in jsonp.Categories)
            foreach (var e in jsonp.GetListing(cat).Entries)
            {
                var num = CompendiumIdMapper.NumericSuffix(e.Id);
                var en = CompendiumName.Normalize(e.Name);
                if (num >= 0 && en.Length > 0) jsonpNumberName.Add((num, en));
            }

        foreach (var category in jsonp.Categories)
        {
            result.Summary.Categories++;

            var jsonpEntries = jsonp.GetListing(category).Entries;
            var jsonpIdSet = new HashSet<string>(jsonpEntries.Select(e => e.Id), StringComparer.Ordinal);
            var jsonpNameToIds = GroupByName(jsonpEntries.Select(e => (e.Id, e.Name)));

            if (!rebornByCategory.TryGetValue(category, out var rebornEntries))
                rebornEntries = [];
            var rebornNameToIds = GroupByName(rebornEntries.Select(e => (e.Id, e.Name)));

            // Track reborn ids already claimed by a remap so two iws entries
            // can't both retarget the same reborn id.
            var claimedTargets = new HashSet<string>(StringComparer.Ordinal);

            // 1) id remaps — pass 1: collect candidate (from → to) remaps.
            var candidates = new List<(string From, string To, string Name)>();
            foreach (var j in jsonpEntries)
            {
                var norm = CompendiumName.Normalize(j.Name);
                if (norm.Length == 0) continue;

                // Already aligned across categories: iws's own number + name
                // already identifies a Reborn entry (any category).
                if (rebornNumberName.Contains((CompendiumIdMapper.NumericSuffix(j.Id), norm)))
                {
                    result.Summary.AlreadyAligned++;
                    continue;
                }

                if (!rebornNameToIds.TryGetValue(norm, out var rebornIds)) continue;

                if (rebornIds.Count > 1 || jsonpNameToIds[norm].Count > 1)
                {
                    // When the name is ambiguous but this iws entry's own id is
                    // already one of the Reborn candidates, it is already aligned
                    // to a valid Reborn id — not a real ambiguity (these are
                    // parenthetical name-variants that normalize to the same base,
                    // e.g. "Aundair (City)" vs "Aundair (Small village)").
                    if (rebornIds.Contains(j.Id)) { result.Summary.AlreadyAligned++; continue; }

                    result.Ambiguous.Add(new IwsAmbiguous(category, j.Name, j.Id, rebornIds, "ambiguous name match"));
                    continue;
                }

                var target = rebornIds[0];
                if (target == j.Id) { result.Summary.AlreadyAligned++; continue; }

                candidates.Add((j.Id, target, j.Name));
            }

            // Pass 2: resolve target occupancy transitively. A target already
            // used by another iws entry is only a real problem when that
            // occupant (or, recursively, whatever it is waiting on) STAYS PUT.
            // The dependency graph is functional (each moving id points at the
            // occupant of its target), so it is chains feeding into cycles:
            //   - target free                    → applicable
            //   - occupant is moving             → depends on that occupant
            //   - occupant stays put (non-moving) → blocked (never frees)
            //   - a pure cycle of movers         → applicable (apply vacates all
            //                                       ids to temps first, then places)
            // Blocking propagates: if any node in the chain is blocked, every
            // remap waiting on it to vacate is blocked too.
            var movingIds = new HashSet<string>(candidates.Select(c => c.From), StringComparer.Ordinal);
            var candByFrom = candidates.ToDictionary(c => c.From, StringComparer.Ordinal);
            var blockedMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            bool IsBlocked(string from)
            {
                if (blockedMemo.TryGetValue(from, out var known)) return known;
                if (!visiting.Add(from)) return false; // cycle: temp-id apply handles it

                var to = candByFrom[from].To;
                bool b = jsonpIdSet.Contains(to)
                    ? (movingIds.Contains(to) ? IsBlocked(to) : true) // occupant moving → recurse; else stays put → blocked
                    : false;                                          // target free

                visiting.Remove(from);
                blockedMemo[from] = b;
                return b;
            }

            foreach (var (from, to, name) in candidates)
            {
                if (IsBlocked(from))
                {
                    result.Ambiguous.Add(new IwsAmbiguous(category, name, from, [to], "target id occupied by non-moving element"));
                    continue;
                }
                if (!claimedTargets.Add(to))
                {
                    result.Ambiguous.Add(new IwsAmbiguous(category, name, from, [to], "two iws entries target the same id"));
                    continue;
                }
                result.IdRemaps.Add(new IwsIdRemap(category, name, from, to));
            }

            // 2) new entries (reborn has it, iws doesn't — same category only)
            foreach (var r in rebornEntries)
            {
                var norm = CompendiumName.Normalize(r.Name);
                if (norm.Length == 0) continue;
                if (jsonpNameToIds.ContainsKey(norm) || jsonpIdSet.Contains(r.Id)) continue;
                // Already present in iws under a different category (same number
                // + name) → not a new entry.
                if (jsonpNumberName.Contains((CompendiumIdMapper.NumericSuffix(r.Id), norm))) continue;

                // Derive the compendium body + index text the same way the
                // readers do, so iws's dataN.js/_index.js get canonical content.
                string? body = null, indexText = null;
                if (r.Fields.TryGetValue("Txt", out var txt) && txt.Length > 0)
                {
                    body = CompendiumHtml.ExtractDetail(txt);
                    indexText = CompendiumHtml.HtmlToText(body);
                }
                result.NewEntries.Add(new IwsNewEntry(category, r.Id, r.Name, r.Fields, body, indexText));
            }
        }

        result.Summary.IdRemaps = result.IdRemaps.Count;
        result.Summary.NewEntries = result.NewEntries.Count;
        result.Summary.Ambiguous = result.Ambiguous.Count;
        return result;
    }

    private static Dictionary<string, List<string>> GroupByName(IEnumerable<(string Id, string Name)> entries)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, name) in entries)
        {
            var norm = CompendiumName.Normalize(name);
            if (norm.Length == 0) continue;
            if (!map.TryGetValue(norm, out var list)) map[norm] = list = [];
            if (!list.Contains(id)) list.Add(id);
        }
        return map;
    }
}
