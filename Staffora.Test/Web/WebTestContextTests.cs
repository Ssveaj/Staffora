using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;

namespace Staffora.Test.Web;

public class WebTestContextTests : WebTestContext
{
    [Fact]
    public void Renders_a_radzen_dialog_opened_through_the_registered_dialog_service()
    {
        var host = Render<RadzenDialog>();
        var dialogs = Services.GetRequiredService<DialogService>();

        host.InvokeAsync(() => dialogs.Open("Confirm", _ => builder => builder.AddContent(0, "Dialog body")));

        host.WaitForAssertion(() => Assert.Contains("Dialog body", host.Markup));
    }
}
