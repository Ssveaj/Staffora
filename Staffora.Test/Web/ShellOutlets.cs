using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Staffora.Blazor.Layout;

namespace Staffora.Test.Web;

public sealed class ShellOutlets : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-shell-crumb", true);
        builder.OpenComponent<SectionOutlet>(2);
        builder.AddComponentParameter(3, nameof(SectionOutlet.SectionName), AppBreadcrumb.PageSection);
        builder.CloseComponent();
        builder.CloseElement();
    }
}
