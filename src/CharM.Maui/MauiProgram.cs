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

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

        // Host-agnostic CharM services, shared with CharM.Web/Program.cs via
        // AddCharmCoreServices so the two host registrations can't drift.
        builder.Services.AddCharmCoreServices();

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
