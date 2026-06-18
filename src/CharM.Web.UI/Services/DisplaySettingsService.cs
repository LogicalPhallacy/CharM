using Microsoft.JSInterop;
using System.Text.Json;

namespace CharM.Web.Services;

/// <summary>
/// Holds user display preferences (dice buttons, skills layout, custom CSS)
/// and persists them to <c>localStorage</c> under key <c>charm:display-settings</c>.
/// Independent of the active character — settings apply globally.
///
/// <para>Like <see cref="BrowserStorageService"/>, all JS calls are safe only
/// AFTER the first interactive render. Callers must guard with
/// <c>OnAfterRenderAsync</c>.</para>
/// </summary>
public sealed class DisplaySettingsService
{
    public event Action? Changed;

    public bool HideDiceButtons { get; private set; }
    public bool SeparateSkillsBlock { get; private set; }
    public string? CustomCss { get; private set; }

    public bool IsLoaded { get; private set; }

    private IJSObjectReference? _module;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Update(bool hideDiceButtons, bool separateSkillsBlock, string? customCss)
    {
        HideDiceButtons = hideDiceButtons;
        SeparateSkillsBlock = separateSkillsBlock;
        CustomCss = string.IsNullOrWhiteSpace(customCss) ? null : customCss;
        Changed?.Invoke();
    }

    public async Task LoadAsync(IJSRuntime js)
    {
        if (IsLoaded) return;

        string? json = null;

        // Try global bridge first (web host injects this).
        try
        {
            json = await js.InvokeAsync<string?>(
                "charm.browserStorage.getItem", "charm:display-settings");
        }
        catch
        {
            // Fall through to RCL module (MAUI path).
        }

        if (json is null)
        {
            try
            {
                var module = await GetModuleAsync(js);
                if (module is not null)
                    json = await module.InvokeAsync<string?>("getItem", "charm:display-settings");
            }
            catch { /* ignore */ }
        }

        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<SettingsDto>(json, _jsonOptions);
                if (dto is not null)
                {
                    HideDiceButtons = dto.HideDiceButtons;
                    SeparateSkillsBlock = dto.SeparateSkillsBlock;
                    CustomCss = string.IsNullOrWhiteSpace(dto.CustomCss) ? null : dto.CustomCss;
                }
            }
            catch { /* corrupt data — keep defaults */ }
        }

        IsLoaded = true;
        Changed?.Invoke();
    }

    public async Task SaveAsync(IJSRuntime js)
    {
        var dto = new SettingsDto
        {
            HideDiceButtons = HideDiceButtons,
            SeparateSkillsBlock = SeparateSkillsBlock,
            CustomCss = CustomCss,
        };
        var json = JsonSerializer.Serialize(dto, _jsonOptions);

        try
        {
            await js.InvokeAsync<bool>(
                "charm.browserStorage.setItem", "charm:display-settings", json);
            return;
        }
        catch { /* fall through to RCL module */ }

        try
        {
            var module = await GetModuleAsync(js);
            if (module is not null)
                await module.InvokeAsync<bool>("setItem", "charm:display-settings", json);
        }
        catch { /* ignore */ }
    }

    private async Task<IJSObjectReference?> GetModuleAsync(IJSRuntime js)
    {
        if (_module is not null) return _module;
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/CharM.Web.UI/js/browser-storage.js");
            return _module;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SettingsDto
    {
        public bool HideDiceButtons { get; set; }
        public bool SeparateSkillsBlock { get; set; }
        public string? CustomCss { get; set; }
    }
}
