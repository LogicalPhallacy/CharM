using System.Security.Cryptography;
using System.Xml.Linq;

namespace CharM.RulesDb.Import;

/// <summary>
/// Metadata describing a single CBLoader <c>.part</c> file, extracted from its
/// <c>&lt;UpdateInfo&gt;</c> / <c>&lt;Description&gt;</c> elements plus a content
/// hash of the raw bytes. Mirrors the fields the cbparts <c>build.py</c> and
/// CBLoader use for version tracking.
/// </summary>
public sealed record PartFileInfo
{
    /// <summary>Stable identifier, e.g. <c>sorted/06-races.part</c>. Defaults to the filename.</summary>
    public required string PartId { get; init; }

    /// <summary>The <c>&lt;Filename&gt;</c> from UpdateInfo, or the on-disk name.</summary>
    public required string Filename { get; init; }

    /// <summary>Folder-derived category: sorted | UnearthedArcana | Homebrew | 3rdParty, or null.</summary>
    public string? Category { get; init; }

    /// <summary><c>&lt;UpdateInfo&gt;&lt;Version&gt;</c> (e.g. "1.42"), or null.</summary>
    public string? Version { get; init; }

    /// <summary>Human description from <c>&lt;Description&gt;</c>, or null.</summary>
    public string? Description { get; init; }

    /// <summary><c>&lt;PartAddress&gt;</c> download URL, or null.</summary>
    public string? PartAddress { get; init; }

    /// <summary>SHA-256 (hex) of the raw part bytes.</summary>
    public required string ContentHash { get; init; }

    /// <summary>True when the part is an <c>&lt;Obsolete/&gt;</c> stub (should be skipped on merge).</summary>
    public bool IsObsolete { get; init; }
}

/// <summary>
/// Parses CBLoader <c>.part</c> file metadata. Tolerant of missing UpdateInfo
/// (older or hand-authored parts) — falls back to the filename and a content
/// hash so every part is still trackable in <c>part_registry</c>.
/// </summary>
public static class PartMetadataReader
{
    /// <summary>Read metadata from a part file on disk.</summary>
    public static PartFileInfo Read(string partPath, string? partId = null, string? category = null)
    {
        byte[] bytes = File.ReadAllBytes(partPath);
        string filename = Path.GetFileName(partPath);
        return Read(bytes, filename, partId ?? filename, category);
    }

    /// <summary>Read metadata from in-memory part bytes (e.g. uploaded or downloaded).</summary>
    public static PartFileInfo Read(byte[] bytes, string filename, string? partId = null, string? category = null)
    {
        string contentHash = Convert.ToHexString(SHA256.HashData(bytes));

        string? version = null, description = null, partAddress = null, infoFilename = null;
        bool obsolete = false;

        try
        {
            using var stream = new MemoryStream(bytes);
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root is not null)
            {
                obsolete = root.Elements().Any(e => e.Name.LocalName == "Obsolete");

                var updateInfo = root.Elements().FirstOrDefault(e => e.Name.LocalName == "UpdateInfo");
                if (updateInfo is not null)
                {
                    version = LocalValue(updateInfo, "Version");
                    infoFilename = LocalValue(updateInfo, "Filename");
                    partAddress = LocalValue(updateInfo, "PartAddress");
                }

                var desc = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Description");
                description = desc?.Value?.Trim();
            }
        }
        catch
        {
            // Malformed XML: keep the content hash + filename so the part is
            // still registrable; merge will surface the parse failure later.
        }

        return new PartFileInfo
        {
            PartId = partId ?? filename,
            Filename = infoFilename?.Trim() is { Length: > 0 } f ? f : filename,
            Category = category,
            Version = NullIfBlank(version),
            Description = NullIfBlank(description),
            PartAddress = NullIfBlank(partAddress),
            ContentHash = contentHash,
            IsObsolete = obsolete,
        };
    }

    private static string? LocalValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
