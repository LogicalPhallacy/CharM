using CharM.Engine.Creation;

namespace CharM.RulesDb.Storage;

/// <summary>Kind of audit finding when comparing a character's recorded parts to the current DB.</summary>
public enum PartAuditKind
{
    /// <summary>A part the character was built with is not enabled (or absent) in the current DB.</summary>
    MissingPart,
    /// <summary>The character was built with a newer version of a part than the DB currently has.</summary>
    OutdatedDatabase,
}

/// <summary>A single audit finding.</summary>
public sealed record PartAuditAlert(
    PartAuditKind Kind,
    string PartId,
    string? RecordedVersion,
    string? CurrentVersion,
    string Message);

/// <summary>
/// Audits a character's recorded build provenance against the rules database it
/// is being opened under. Default policy (per project requirement): raise an
/// alert only when an enabled/built part is <b>missing</b> from the current DB,
/// or the character was built with a <b>newer</b> version than the DB has. Parts
/// the DB has but the character didn't use are NOT flagged (additive content is
/// harmless).
/// </summary>
public static class PartAuditService
{
    public static IReadOnlyList<PartAuditAlert> Audit(
        IEnumerable<RecordedPart> recordedParts,
        IReadOnlyList<PartLayer> currentLayers)
    {
        var current = currentLayers
            .Where(l => !l.IsBase)
            .ToDictionary(l => l.PartId, StringComparer.OrdinalIgnoreCase);

        var alerts = new List<PartAuditAlert>();

        foreach (var recorded in recordedParts)
        {
            if (!current.TryGetValue(recorded.PartId, out var layer) || !layer.Enabled)
            {
                alerts.Add(new PartAuditAlert(
                    PartAuditKind.MissingPart,
                    recorded.PartId,
                    recorded.Version,
                    layer?.Version,
                    $"Part '{recorded.PartId}' was used to build this character but is " +
                    (layer is null ? "not present" : "disabled") + " in the current rules database."));
                continue;
            }

            if (CompareVersions(recorded.Version, layer.Version) > 0)
            {
                alerts.Add(new PartAuditAlert(
                    PartAuditKind.OutdatedDatabase,
                    recorded.PartId,
                    recorded.Version,
                    layer.Version,
                    $"This character was built with '{recorded.PartId}' v{recorded.Version}, " +
                    $"but the current database has the older v{layer.Version}."));
            }
        }

        return alerts;
    }

    /// <summary>
    /// Compare two dotted/numeric version strings (e.g. "1.42" vs "1.128").
    /// Returns &gt;0 when <paramref name="a"/> is newer, &lt;0 when older, 0 when
    /// equal or not comparable. Numeric components compare numerically so 1.128
    /// is correctly newer than 1.42.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;

        var pa = a.Split('.');
        var pb = b.Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
