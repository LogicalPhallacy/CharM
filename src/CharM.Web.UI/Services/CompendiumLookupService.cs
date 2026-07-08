using CharM.Compendium;
using CharM.Engine.Rules;

namespace CharM.Web.Services;

/// <summary>
/// Prototype on-demand compendium lookup for the Web UI. Resolves a rules
/// element to a compendium entry (id heuristic + name fallback, via the shared
/// <see cref="CharM.Compendium"/> reader) and returns its human-friendly HTML
/// body for display.
///
/// This is a feasibility prototype (see docs/compendium-integration.md):
/// - The compendium data is copyrighted and external, so it is opt-in via the
///   <c>CHARM_COMPENDIUM_DIR</c> environment variable (pointing at the
///   <c>4e_database_files</c> directory). When unset/missing the service is
///   simply disabled and the UI shows nothing extra.
/// - Body HTML is rendered as-is; a production integration must sanitize it
///   (see the doc's "HTML trust" section) before wider rollout.
/// </summary>
public sealed class CompendiumLookupService
{
    public const string DirectoryEnvVar = "CHARM_COMPENDIUM_DIR";

    private readonly Lazy<(CompendiumReader Reader, CompendiumMatcher Matcher)?> _engine;

    public CompendiumLookupService()
    {
        _engine = new(Initialize);
    }

    /// <summary>True when a valid compendium directory is configured on disk.</summary>
    public bool IsEnabled => _engine.Value is not null;

    /// <summary>
    /// Resolve an element and return its compendium id and HTML body, or null
    /// when the service is disabled or nothing matches with a body.
    /// </summary>
    public CompendiumBody? Lookup(RulesElement element)
    {
        if (element is null || _engine.Value is not { } e) return null;

        // Strategy: an explicit "compendiumid" specific (added by the
        // compendiumid part file for elements whose internal-id doesn't carry
        // the correct compendium number) wins over heuristic matching; fall
        // back to the id/name matcher only when it's absent or resolves nothing.
        if (element.Fields.TryGetValue("compendiumid", out var cid) &&
            CompendiumIdMapper.SplitCompendiumId(cid?.Trim(), out var cidCategory, out _))
        {
            var cidBody = e.Reader.GetBody(cidCategory, cid!.Trim());
            if (!string.IsNullOrEmpty(cidBody))
                return new CompendiumBody(cid.Trim(), cidCategory, CompendiumMatchKind.IdOnly, cidBody);
        }

        var match = e.Matcher.Match(element.InternalId, element.Name, element.Type);
        if (match.CompendiumId is null || match.Category is null) return null;

        var html = e.Reader.GetBody(match.Category, match.CompendiumId);
        if (string.IsNullOrEmpty(html)) return null;

        return new CompendiumBody(match.CompendiumId, match.Category, match.Kind, html);
    }

    private static (CompendiumReader, CompendiumMatcher)? Initialize()
    {
        var dir = Environment.GetEnvironmentVariable(DirectoryEnvVar);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        try
        {
            var reader = new CompendiumReader(dir);
            return (reader, new CompendiumMatcher(reader));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>A resolved compendium entry with its rendered body.</summary>
public sealed record CompendiumBody(string CompendiumId, string Category, CompendiumMatchKind MatchKind, string Html);
