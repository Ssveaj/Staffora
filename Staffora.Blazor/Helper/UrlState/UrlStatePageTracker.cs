using Microsoft.AspNetCore.Components;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace Staffora.Blazor.Helper.UrlState;

public sealed class UrlStatePageTracker : ComponentBase
{
    [Inject] private UrlStateService UrlState { get; set; } = null!;

    [Parameter, EditorRequired] public RouteData RouteData { get; set; } = null!;

    protected override void OnParametersSet() => UrlState.EnterPage(RouteData.PageType);
}
