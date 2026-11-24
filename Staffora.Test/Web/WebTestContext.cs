using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Test.Web;

public abstract class WebTestContext : BunitContext
{
    protected WebTestContext()
    {
        Services.AddRadzenComponents();
        Services.AddUrlState();
        Services.AddScoped<OpenStateEntityDialogs>();
        Services.AddDialogRoutes(typeof(StateEntityDialogContext).Assembly);

        Services.AddSingleton<TimeProvider>(new AnchoredClock(new DateTimeOffset(2027, 3, 10, 9, 0, 0, TimeSpan.Zero)));

        Services.AddSingleton<NavigationManager>(new DispatchedNavigationManager(this));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
