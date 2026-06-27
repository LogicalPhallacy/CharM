using Microsoft.AspNetCore.Components;

namespace CharM.Web.Components;

/// <summary>
/// Base for components that re-render in response to long-lived service events
/// (e.g. the singleton <c>RulesDatabaseService.Changed</c>, which can fire from
/// a background thread after a circuit has been torn down).
///
/// <para>
/// It guards the render against the circuit-teardown race: once the component is
/// disposed, <see cref="NotifyStateChanged"/> becomes a no-op, so a late event
/// can't queue a render that would instantiate child components and resolve
/// their injected services from an already-disposed scope — the
/// <c>ObjectDisposedException('IServiceProvider')</c> seen on exit.
/// </para>
///
/// <para>
/// Subclasses override <see cref="OnDispose"/> to unsubscribe and release
/// resources; the base sets the disposed flag first so any in-flight handler
/// short-circuits.
/// </para>
/// </summary>
public abstract class CharmComponentBase : ComponentBase, IDisposable
{
    private volatile bool _disposed;

    /// <summary>True once the component has begun disposing.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// Queue a re-render on the renderer's dispatcher, skipping it entirely if
    /// the component is (being) disposed. Safe to call from background-thread
    /// event handlers.
    /// </summary>
    protected void NotifyStateChanged()
    {
        if (_disposed)
            return;

        _ = InvokeAsync(() =>
        {
            if (_disposed)
                return;
            StateHasChanged();
        });
    }

    /// <summary>Override to unsubscribe from events and dispose owned resources. Called once.</summary>
    protected virtual void OnDispose()
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        OnDispose();
        GC.SuppressFinalize(this);
    }
}
