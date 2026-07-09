namespace CharM.RulesDb.Import;

/// <summary>
/// Maps a part file name to the category + official flag of the CBLoader
/// <c>.index</c> that references it. Built once from a set of parsed index
/// documents and shared by every part source (GitHub, CBLoader host, web page)
/// and by the local-folder build path, so category derivation can't drift
/// between them.
///
/// <para>When several indexes reference the same part filename, the first index
/// added wins (indexes are added in a stable, sorted order by their callers).</para>
/// </summary>
public sealed class PartIndexCatalog
{
    private readonly Dictionary<string, Entry> _byFilename = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _obsolete = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Category + official flag contributed by an index for a part.</summary>
    public readonly record struct Entry(string Category, bool IsOfficial, string IndexName);

    /// <summary>The union of all <c>&lt;Obsolete&gt;</c> filenames across the indexes.</summary>
    public IReadOnlyCollection<string> ObsoleteFilenames => _obsolete;

    /// <summary>True when no index contributed any part mapping.</summary>
    public bool IsEmpty => _byFilename.Count == 0;

    /// <summary>Add one parsed index document's part mappings (first index wins per filename).</summary>
    public void Add(PartIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var f in document.ObsoleteFilenames)
            _obsolete.Add(f);

        foreach (var (filename, _) in document.Parts)
        {
            if (string.IsNullOrWhiteSpace(filename)) continue;
            var key = filename.Trim();
            _byFilename.TryAdd(key, new Entry(document.Category, document.IsOfficial, document.IndexName));
        }
    }

    /// <summary>Add several index documents in order.</summary>
    public void AddRange(IEnumerable<PartIndexDocument> documents)
    {
        foreach (var d in documents) Add(d);
    }

    /// <summary>Look up the index-derived category/official flag for a part file name.</summary>
    public bool TryGet(string partFilename, out Entry entry)
    {
        if (!string.IsNullOrWhiteSpace(partFilename))
            return _byFilename.TryGetValue(Path.GetFileName(partFilename.Trim()), out entry);
        entry = default;
        return false;
    }

    /// <summary>
    /// Resolve the category for a part: the index-derived category when the part
    /// is referenced by an index, otherwise <paramref name="fallbackCategory"/>
    /// (typically the physical folder name).
    /// </summary>
    public string? CategoryFor(string partFilename, string? fallbackCategory) =>
        TryGet(partFilename, out var e) ? e.Category : fallbackCategory;

    /// <summary>True when the part is referenced by an official index.</summary>
    public bool IsOfficial(string partFilename) => TryGet(partFilename, out var e) && e.IsOfficial;
}
