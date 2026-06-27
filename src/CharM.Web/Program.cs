using CharM.Web.Components;
using CharM.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Persisted-character restore reads the full base64 .dnd4e payload back
// over SignalR (JS -> server return value). A typical character is
// 50-500 KB base64; the default MaximumReceiveMessageSize of 32 KB
// silently kills the interop call AND drops the WebSocket with an
// "unknown error" close. Lift the cap so restore works and the circuit
// stays alive. 4 MB matches the default upload-file chunk size used by
// InputFile and is comfortably larger than any single character file.
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 4L * 1024L * 1024L;
});

builder.Services.AddCharmCoreServices();

var app = builder.Build();

var rulesDb = app.Services.GetRequiredService<RulesDatabaseService>();
rulesDb.TryOpenFirstAvailable(RulesDatabasePathResolver.GetStartupCandidates(
    builder.Configuration.GetValue<string>("RulesDbPath"),
    args,
    includeCurrentDirectory: true));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(CharM.Web.Components.Routes).Assembly)
    .AddInteractiveServerRenderMode();

app.Run();
