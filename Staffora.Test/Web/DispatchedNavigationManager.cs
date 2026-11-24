using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Staffora.Test.Web;

public sealed class DispatchedNavigationManager : NavigationManager
{
    private readonly BunitContext context;

    public DispatchedNavigationManager(BunitContext context)
    {
        this.context = context;
        Initialize("http://localhost/", "http://localhost/");
    }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        var absolute = ToAbsoluteUri(uri).ToString();
        _ = this.context.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (!await NotifyLocationChangingAsync(absolute, options.HistoryEntryState, isNavigationIntercepted: false))
            {
                return;
            }

            Uri = absolute;
            NotifyLocationChanged(isInterceptedLink: false);
        });
    }

    protected override void HandleLocationChangingHandlerException(Exception ex, LocationChangingContext context) => throw ex;

    protected override void SetNavigationLockState(bool value)
    {
    }
}
