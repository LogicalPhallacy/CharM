using Microsoft.AspNetCore.Components.WebView;

namespace CharM.Maui;

public partial class MainPage : ContentPage
{
#if MACCATALYST
    // Strong reference to our wrapping delegate. WKWebView.UIDelegate is a weak
    // property, so without this our delegate (and the MAUI delegate it wraps)
    // could be garbage collected and the file picker would silently stop.
    private BlazorFilePickerUIDelegate? _filePickerDelegate;
    private WebKit.WKWebView? _wkWebView;
#endif

    public MainPage()
    {
        InitializeComponent();
    }

    private void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if MACCATALYST
        // On Mac Catalyst <input type="file"> clicks are dropped because the
        // active WKUIDelegate doesn't implement runOpenPanel. We can't simply
        // assign our own delegate here: MAUI's IOSWebViewManager installs its
        // OWN WebViewUIDelegate a moment AFTER this event fires, clobbering
        // anything we set now (confirmed by runtime logging). So we reconcile —
        // wait for MAUI's delegate to appear, then WRAP it with one that adds
        // runOpenPanel and forwards everything else back to MAUI. See
        // BlazorFilePickerUIDelegate.
        if (e.WebView is WebKit.WKWebView wkWebView)
        {
            _wkWebView = wkWebView;
            _ = ReconcileFilePickerDelegateAsync();
        }
        else
        {
            FilePickerLog.Warn("init",
                $"e.WebView is not a WKWebView ({e.WebView?.GetType().FullName ?? "<null>"}); " +
                "file picker delegate NOT installed");
        }
#endif
    }

#if MACCATALYST
    private async Task ReconcileFilePickerDelegateAsync()
    {
        // MAUI sets its delegate within ~1s of init. Poll for ~10s so we both
        // install promptly once it appears and re-assert our wrapper if MAUI
        // (or a layout/handler event) replaces it again. Cheap and bounded.
        for (var i = 0; i < 40; i++)
        {
            await MainThread.InvokeOnMainThreadAsync(ReconcileFilePickerDelegate);
            await Task.Delay(250);
        }
        await MainThread.InvokeOnMainThreadAsync(ReconcileFilePickerDelegate);
    }

    private void ReconcileFilePickerDelegate()
    {
        var webView = _wkWebView;
        if (webView is null)
            return;

        try
        {
            var current = webView.UIDelegate;

            // Our wrapper is already the active delegate — nothing to do.
            if (_filePickerDelegate is not null && current is not null
                && current.Handle == _filePickerDelegate.Handle)
            {
                return;
            }

            // MAUI hasn't installed its delegate yet; wait for the next tick.
            if (current is null)
                return;

            // current is MAUI's (or some other) real delegate — wrap it so we
            // add runOpenPanel while forwarding the rest back to it.
            var wrapper = new BlazorFilePickerUIDelegate(current);
            webView.UIDelegate = wrapper;
            _filePickerDelegate = wrapper;
            FilePickerLog.Info("install",
                $"wrapped UIDelegate ({current.GetType().FullName}); runOpenPanel now handled");
        }
        catch (Exception ex)
        {
            FilePickerLog.Error("install", "reconcile failed", ex);
        }
    }
#endif
}
