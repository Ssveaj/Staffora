using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common;

namespace Staffora.Test.Web.Common;

public class IconTextButtonTests : WebTestContext
{
    [Fact]
    public void The_icon_is_hidden_from_assistive_technology_and_the_text_names_the_button()
    {
        var clicks = 0;
        var button = Render<IconTextButton>(parameters => parameters
            .Add(component => component.Text, "Save")
            .Add(component => component.Icon, "save")
            .Add(component => component.Click, EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, () => clicks++))
            .AddUnmatched("data-transition", "tenant-save")
            .AddUnmatched("class", "transition-button__button"));

        var element = button.Find("button");
        var icon = element.QuerySelector("i.rzi")!;
        Assert.Equal("save", icon.TextContent);
        Assert.Equal("true", icon.GetAttribute("aria-hidden"));
        Assert.Equal("Save", element.QuerySelector(".rz-button-text")!.TextContent);

        Assert.Equal("Save", string.Concat(element.QuerySelectorAll(".rz-button-box > :not([aria-hidden=true])").Select(part => part.TextContent)).Trim());
        Assert.Equal("tenant-save", element.GetAttribute("data-transition"));
        Assert.Contains("transition-button__button", element.ClassName, StringComparison.Ordinal);

        element.Click();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void It_draws_the_markup_of_Radzens_own_button_but_for_the_hidden_icon()
    {
        var ours = Render<IconTextButton>(parameters => parameters
            .Add(component => component.Text, "New Tenant")
            .Add(component => component.Icon, "add_business")
            .Add(component => component.ButtonStyle, ButtonStyle.Light)
            .Add(component => component.Size, ButtonSize.Small));
        var radzens = Render<RadzenButton>(parameters => parameters
            .Add(component => component.Text, "New Tenant")
            .Add(component => component.Icon, "add_business")
            .Add(component => component.ButtonStyle, ButtonStyle.Light)
            .Add(component => component.Size, ButtonSize.Small));

        var expected = radzens.Find("button");
        expected.QuerySelector("i.rzi")!.SetAttribute("aria-hidden", "true");
        var drawn = ours.Find("button");

        Assert.Equal(expected.ClassName, drawn.ClassName);
        Assert.Equal(expected.QuerySelector(".rz-button-box")!.InnerHtml.Replace(" ", string.Empty, StringComparison.Ordinal), drawn.QuerySelector(".rz-button-box")!.InnerHtml.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void A_busy_button_is_disabled_and_keeps_its_text()
    {
        var button = Render<IconTextButton>(parameters => parameters
            .Add(component => component.Text, "Save")
            .Add(component => component.Icon, "save")
            .Add(component => component.IsBusy, true));

        var element = button.Find("button");
        Assert.True(element.HasAttribute("disabled"));
        Assert.Null(element.QuerySelector("i.rzi"));
        Assert.Equal("Save", element.QuerySelector(".rz-button-text")!.TextContent);
        Assert.Equal("true", element.QuerySelector(".rz-progressbar-circular")!.GetAttribute("aria-hidden"));
    }
}
