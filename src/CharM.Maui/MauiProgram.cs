using CharM.RulesDb.Storage;
using CharM.Web.Rendering;
using CharM.Web.Services;
using Microsoft.Extensions.Logging;

namespace CharM.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        InteractiveRenderSettings.ConfigureBlazorHybrid();
#if WINDOWS
        ConfigureWebView2UserDataFolder();
#endif
#if MACCATALYST
        FilePickerLog.Info("startup", $"MauiProgram.CreateMauiApp entered (pid={Environment.ProcessId})");
#endif

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

        // Service registration must mirror CharM.Web/Program.cs since both
        // hosts render the same Razor components from CharM.Web.UI. Missing
        // a service here means a component injection throws at render time
        // and the WebView is stuck on the static "Loading..." HTML.
        builder.Services.AddSingleton<RulesDatabaseService>();
        builder.Services.AddSingleton<IRulesDatabase>(sp =>
            sp.GetRequiredService<RulesDatabaseService>());
        builder.Services.AddScoped<CharacterSessionService>();
        builder.Services.AddScoped<RetrainingService>();
        builder.Services.AddScoped<BrowserStorageService>();
        builder.Services.AddScoped<CharacterRestoreState>();
        builder.Services.AddScoped<PrintCardCollector>();
        builder.Services.AddScoped<DiceRoller>();
        builder.Services.AddScoped<DiceRollerUiService>();
        builder.Services.AddScoped<CharacterResourceTracker>();
        builder.Services.AddScoped<CalculationBreakdownService>();
        builder.Services.AddScoped<DisplaySettingsService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        app.Services.GetRequiredService<RulesDatabaseService>().TryOpenFirstAvailable(
            RulesDatabasePathResolver.GetStartupCandidates(
                configuredPath: null,
                Environment.GetCommandLineArgs().Skip(1),
                includeCurrentDirectory: false));

        return app;
    }

    private static void ConfigureWebView2UserDataFolder()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            return;

        var path = Path.Combine(localData, "CharM", "WebView2");
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", path);
    }
}
