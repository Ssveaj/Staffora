using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Helper;

namespace Staffora.Test.Web.Common;

public class PromptDialogTests : WebTestContext
{
    private readonly IRenderedComponent<RadzenDialog> host;
    private readonly DialogService dialogs;

    public PromptDialogTests()
    {
        this.host = Render<RadzenDialog>();
        this.dialogs = Services.GetRequiredService<DialogService>();
    }

    [Fact]
    public async Task A_limited_prompt_takes_at_most_its_limit_counts_as_the_user_types_and_refuses_a_longer_answer()
    {
        Task<string?>? answer = null;
        await this.host.InvokeAsync(() => { answer = this.dialogs.PromptAsync("Cancel the job", "Why?", initialText: "An answer far too long.", required: true, maxLength: 10); });
        var box = this.host.WaitForElement(".form-dialog textarea");

        Assert.Equal("10", box.GetAttribute("maxlength"));
        var count = this.host.Find(".prompt-dialog__count");
        Assert.Equal(count.Id, box.GetAttribute("aria-describedby"));
        Assert.Equal("23 of 10 characters", count.TextContent);

        this.host.Find(".dialog-confirm").Click();
        Assert.Equal("Write at most 10 characters.", this.host.Find(".form-dialog [role=alert]").TextContent);
        Assert.False(answer!.IsCompleted);

        this.host.Find(".form-dialog textarea").Input("Too late.");
        this.host.WaitForAssertion(() => Assert.Equal("9 of 10 characters", this.host.Find(".prompt-dialog__count").TextContent));
        this.host.Find(".dialog-confirm").Click();
        Assert.Equal("Too late.", await answer.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_prompt_without_a_limit_shows_no_count_and_sets_no_maxlength()
    {
        await this.host.InvokeAsync(() => { _ = this.dialogs.PromptAsync("Rename", "New name?"); });
        var box = this.host.WaitForElement(".form-dialog textarea");

        Assert.False(box.HasAttribute("maxlength"));
        Assert.False(box.HasAttribute("aria-describedby"));
        Assert.Empty(this.host.FindAll(".prompt-dialog__count"));
    }
}
