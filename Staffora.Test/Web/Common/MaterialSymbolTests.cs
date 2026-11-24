using Bunit;
using Staffora.Blazor.Components.Common;

namespace Staffora.Test.Web.Common;

public class MaterialSymbolTests : WebTestContext
{
    [Fact]
    public void An_icon_without_a_label_is_decorative_and_hidden_from_assistive_technology()
    {
        var icon = Render<MaterialSymbol>(parameters => parameters.Add(symbol => symbol.Name, "logout"));

        var span = icon.Find("span");
        Assert.Equal("logout", span.TextContent);
        Assert.Equal("material-symbol", span.ClassName);
        Assert.Equal("true", span.GetAttribute("aria-hidden"));
        Assert.False(span.HasAttribute("role"));
        Assert.False(span.HasAttribute("aria-label"));
    }

    [Fact]
    public void An_icon_with_a_label_is_an_image_with_that_name()
    {
        var icon = Render<MaterialSymbol>(parameters => parameters
            .Add(symbol => symbol.Name, "warning")
            .Add(symbol => symbol.Label, "Needs attention"));

        var span = icon.Find("span");
        Assert.Equal("img", span.GetAttribute("role"));
        Assert.Equal("Needs attention", span.GetAttribute("aria-label"));
        Assert.False(span.HasAttribute("aria-hidden"));
    }

    [Fact]
    public void A_blank_label_counts_as_none()
    {
        var icon = Render<MaterialSymbol>(parameters => parameters
            .Add(symbol => symbol.Name, "info")
            .Add(symbol => symbol.Label, "  "));

        var span = icon.Find("span");
        Assert.Equal("true", span.GetAttribute("aria-hidden"));
        Assert.False(span.HasAttribute("aria-label"));
    }

    [Fact]
    public void Classes_and_other_attributes_are_added_to_the_icon()
    {
        var icon = Render<MaterialSymbol>(parameters => parameters
            .Add(symbol => symbol.Name, "domain")
            .Add(symbol => symbol.Class, "material-symbol--lg")
            .AddUnmatched("title", "Tenant"));

        var span = icon.Find("span");
        Assert.Equal("material-symbol material-symbol--lg", span.ClassName);
        Assert.Equal("Tenant", span.GetAttribute("title"));
    }
}
