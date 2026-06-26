using System.Text.Json;
using CharM.RulesDb.Import;

namespace CharM.Web.Services;

/// <summary>
/// Persists part/source management preferences as JSON in the rules working
/// directory: the remote <see cref="PartSourceConfig"/> and the set of enabled
/// sourcebooks. Server-side persistence keeps it host-agnostic (web + MAUI)
/// without JS interop. All operations are best-effort and never throw.
/// </summary>
public sealed class PartPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _sourceConfigPath;
    private readonly string _enabledSourcesPath;

    public PartPreferencesStore()
        : this(RulesDatabasePathResolver.GetDefaultWorkingDirectory())
    {
    }

    public PartPreferencesStore(string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        _sourceConfigPath = Path.Combine(workingDirectory, "part-source-config.json");
        _enabledSourcesPath = Path.Combine(workingDirectory, "enabled-sources.json");
    }

    public PartSourceConfig? LoadSourceConfig()
    {
        try
        {
            return File.Exists(_sourceConfigPath)
                ? JsonSerializer.Deserialize<PartSourceConfig>(File.ReadAllText(_sourceConfigPath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    public void SaveSourceConfig(PartSourceConfig config)
    {
        try { File.WriteAllText(_sourceConfigPath, JsonSerializer.Serialize(config, JsonOpts)); }
        catch { /* best-effort */ }
    }

    public IReadOnlySet<string>? LoadEnabledSources()
    {
        try
        {
            if (!File.Exists(_enabledSourcesPath)) return null;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_enabledSourcesPath));
            return list is { Count: > 0 }
                ? new HashSet<string>(list, StringComparer.OrdinalIgnoreCase)
                : null;
        }
        catch { return null; }
    }

    public void SaveEnabledSources(IReadOnlySet<string>? sources)
    {
        try
        {
            if (sources is null or { Count: 0 })
            {
                if (File.Exists(_enabledSourcesPath)) File.Delete(_enabledSourcesPath);
                return;
            }
            File.WriteAllText(_enabledSourcesPath, JsonSerializer.Serialize(sources.ToList(), JsonOpts));
        }
        catch { /* best-effort */ }
    }
}
