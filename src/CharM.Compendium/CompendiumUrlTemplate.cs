namespace CharM.Compendium;

/// <summary>
/// Builds compendium reference URLs from what we know about an element
/// (compendium category/page, numeric id, and name), using a caller-supplied
/// template. The official Wizards DDI compendium is dead, so the target host is
/// configurable — point it at a local mirror, Compendium Reborn, iws.mx, or any
/// viewer that accepts the same coordinates.
///
/// Supported placeholders (case-insensitive):
/// <list type="bullet">
///   <item><c>{category}</c> — compendium page/category name (e.g. "power").</item>
///   <item><c>{id}</c> / <c>{num}</c> — numeric compendium id (e.g. "6872").</item>
///   <item><c>{compendiumid}</c> — full id (<c>category+num</c>, e.g. "power6872").</item>
///   <item><c>{name}</c> — element name, raw.</item>
///   <item><c>{name_encoded}</c> — element name, URL-encoded.</item>
/// </list>
/// </summary>
public sealed class CompendiumUrlTemplate
{
    /// <summary>The historical Wizards DDI format (now dead, kept as the default for back-compat).</summary>
    public const string WizardsDefault =
        "http://www.wizards.com/dndinsider/compendium/{category}.aspx?id={id}";

    private readonly string _template;

    public CompendiumUrlTemplate(string? template) =>
        _template = string.IsNullOrWhiteSpace(template) ? WizardsDefault : template.Trim();

    /// <summary>A template using the historical Wizards format.</summary>
    public static CompendiumUrlTemplate Default { get; } = new(WizardsDefault);

    /// <summary>
    /// Resolve a template from an explicit value, then the
    /// <c>CHARM_COMPENDIUM_URL_TEMPLATE</c> environment variable, then the
    /// Wizards default.
    /// </summary>
    public const string EnvVar = "CHARM_COMPENDIUM_URL_TEMPLATE";

    public static CompendiumUrlTemplate Resolve(string? explicitTemplate) =>
        new(!string.IsNullOrWhiteSpace(explicitTemplate)
            ? explicitTemplate
            : Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>Fill the template from a compendium category, numeric id, and optional name.</summary>
    public string Build(string category, string id, string? name = null)
    {
        var name0 = name ?? string.Empty;
        return _template
            .Replace("{category}", category, StringComparison.OrdinalIgnoreCase)
            .Replace("{compendiumid}", category + id, StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", id, StringComparison.OrdinalIgnoreCase)
            .Replace("{num}", id, StringComparison.OrdinalIgnoreCase)
            .Replace("{name_encoded}", Uri.EscapeDataString(name0), StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", name0, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fill the template from a full compendium id (<c>&lt;category&gt;&lt;num&gt;</c>,
    /// e.g. "power6872"), or null when the id can't be split.
    /// </summary>
    public string? BuildFromCompendiumId(string? compendiumId, string? name = null) =>
        CompendiumIdMapper.SplitCompendiumId(compendiumId, out var category, out var num)
            ? Build(category, num, name)
            : null;
}
