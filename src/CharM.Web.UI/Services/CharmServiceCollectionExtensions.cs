using CharM.RulesDb.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CharM.Web.Services;

/// <summary>
/// Host-agnostic registration of the CharM services shared by the Blazor Server
/// host (<c>CharM.Web</c>) and the MAUI Blazor Hybrid host (<c>CharM.Maui</c>).
/// Both render the same Razor components from <c>CharM.Web.UI</c>, so the
/// service set must match exactly — a service missing from one host throws at
/// component render time. Centralizing the list here stops the two startups
/// from drifting (previously kept in sync by a comment).
/// </summary>
public static class CharmServiceCollectionExtensions
{
    public static IServiceCollection AddCharmCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<RulesDatabaseService>();
        services.AddSingleton<PartManagementService>();
        services.AddSingleton<PartPreferencesStore>();
        services.AddSingleton<IRulesDatabase>(sp => sp.GetRequiredService<RulesDatabaseService>());

        // Scoped per connection (one session per user tab on the server host).
        services.AddScoped<CharacterSessionService>();
        services.AddScoped<RetrainingService>();
        services.AddScoped<BrowserStorageService>();
        services.AddScoped<CharacterRestoreState>();
        services.AddScoped<PrintCardCollector>();
        services.AddScoped<DiceRoller>();
        services.AddScoped<DiceRollerUiService>();
        services.AddScoped<CharacterResourceTracker>();
        services.AddScoped<CalculationBreakdownService>();
        services.AddScoped<DisplaySettingsService>();

        return services;
    }
}
