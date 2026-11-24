using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Helper;

namespace Staffora.Test.Web.Common;

public class DialogFamilyTests : WebTestContext
{
    private readonly IRenderedComponent<RadzenDialog> host;
    private readonly DialogService dialogs;

    public DialogFamilyTests()
    {
        this.host = Render<RadzenDialog>();
        this.dialogs = Services.GetRequiredService<DialogService>();
    }

    [Fact]
    public async Task A_confirmation_answers_true_on_its_confirming_button()
    {
        var answer = await OpenAsync(() => this.dialogs.ConfirmAsync("Delete the role \"Staff\"?", "Delete role", okText: "Yes, delete", destructive: true));

        Assert.Equal("Delete role", HeaderTitle());
        Assert.Contains("Delete the role \"Staff\"?", this.host.Find(".dialog-notice__message").TextContent);
        Assert.Contains("rz-danger", this.host.Find(".dialog-confirm").ClassName);
        this.host.Find(".dialog-confirm").Click();

        Assert.True(await answer);
    }

    [Fact]
    public async Task A_confirmation_answers_false_on_cancel_and_on_the_headers_close_button()
    {
        var cancelled = await OpenAsync(() => this.dialogs.ConfirmAsync("Leave?"));
        ButtonNamed("Cancel").Click();
        Assert.False(await cancelled);

        var closed = await OpenAsync(() => this.dialogs.ConfirmAsync("Leave?"));
        this.host.Find(".form-dialog-header__close").Click();
        Assert.False(await closed);
    }

    [Fact]
    public async Task An_error_shows_its_message_under_its_title_and_closes_on_OK()
    {
        var shown = await OpenAsync(async () =>
        {
            await this.dialogs.ShowErrorAsync("A role that users hold cannot be deleted.", "The role was not deleted");
            return true;
        });

        Assert.Equal("The role was not deleted", HeaderTitle());
        Assert.Contains("dialog-notice--danger", this.host.Find(".dialog-notice").ClassName);
        Assert.Equal("A role that users hold cannot be deleted.", this.host.Find(".dialog-notice__message").TextContent);
        ButtonNamed("OK").Click();

        Assert.True(await shown);
        Assert.Empty(this.host.FindAll(".form-dialog"));
    }

    [Fact]
    public async Task An_error_without_a_message_says_the_step_could_not_be_completed()
    {
        var shown = await OpenAsync(async () =>
        {
            await this.dialogs.ShowErrorAsync(null);
            return true;
        });

        Assert.Equal("Something went wrong", HeaderTitle());
        Assert.Equal("The step could not be completed.", this.host.Find(".dialog-notice__message").TextContent);
        ButtonNamed("OK").Click();
        Assert.True(await shown);
    }

    [Fact]
    public async Task A_message_shows_its_outcome_and_closes_on_its_button()
    {
        var shown = await OpenAsync(async () =>
        {
            await this.dialogs.ShowMessageAsync("Four staff members were reminded.", "Reminders sent", okText: "Close");
            return true;
        });

        Assert.Equal("Reminders sent", HeaderTitle());
        Assert.Contains("dialog-notice--success", this.host.Find(".dialog-notice").ClassName);
        ButtonNamed("Close").Click();

        Assert.True(await shown);
    }

    [Fact]
    public async Task A_prompt_answers_the_text_written_and_null_when_cancelled()
    {
        var answered = await OpenAsync(() => this.dialogs.PromptAsync("Decline the request", "Why do you decline?", okText: "Decline"));
        Assert.Equal("Decline the request", HeaderTitle());
        Assert.Contains("Why do you decline?", this.host.Find(".prompt-dialog__message").TextContent);
        this.host.Find("textarea").Change("I am away that week.");
        ButtonNamed("Decline").Click();
        Assert.Equal("I am away that week.", await answered);

        var cancelled = await OpenAsync(() => this.dialogs.PromptAsync("Decline the request"));
        ButtonNamed("Cancel").Click();
        Assert.Null(await cancelled);
    }

    [Fact]
    public async Task A_required_prompt_refuses_an_empty_answer_and_says_why()
    {
        var answered = await OpenAsync(() => this.dialogs.PromptAsync("Reason", required: true, requiredMessage: "Give a reason first."));

        ButtonNamed("OK").Click();
        Assert.Equal("Give a reason first.", this.host.Find(".prompt-dialog__required").TextContent);
        Assert.False(answered.IsCompleted);

        this.host.Find("textarea").Change("Sick");
        ButtonNamed("OK").Click();
        Assert.Equal("Sick", await answered);
    }

    [Fact]
    public async Task A_form_dialog_opens_its_component_with_the_openers_parameters_under_the_header()
    {
        var answered = await OpenAsync(() => this.dialogs.OpenFormDialogAsync<ProbeForm, string>("Send a note", parameters => parameters.Set(form => form.Value, "the value")));

        Assert.Equal("Send a note", HeaderTitle());
        Assert.Equal("the value", this.host.Find(".probe").TextContent);
        Assert.Equal(nameof(ProbeForm), this.host.Find(".form-dialog").GetAttribute("data-form-dialog"));
        ButtonNamed("Send").Click();

        Assert.Equal("the value", await answered);
    }

    [Fact]
    public async Task A_form_dialog_answers_false_on_its_frames_cancel()
    {
        var answered = await OpenAsync(() => this.dialogs.OpenFormDialogAsync<ProbeForm>("Send a note"));

        ButtonNamed("Cancel").Click();

        Assert.False(await answered);
    }

    [Fact]
    public async Task A_dialog_is_named_by_its_title_for_assistive_technology()
    {
        var answer = await OpenAsync(() => this.dialogs.ConfirmAsync("Leave?", "Leave the page"));

        var dialog = this.host.Find("[role=dialog]");
        var labelledBy = dialog.GetAttribute("aria-labelledby");
        var name = labelledBy is null ? dialog.GetAttribute("aria-label") : this.host.Find($"#{labelledBy}").TextContent.Trim();
        Assert.Equal("Leave the page", name);

        this.host.Find(".form-dialog-header__close").Click();
        Assert.False(await answer);
    }

    private async Task<Task<T>> OpenAsync<T>(Func<Task<T>> open)
    {
        Task<T>? answer = null;
        await this.host.InvokeAsync(() => { answer = open(); });
        this.host.WaitForElement(".form-dialog");
        return answer!;
    }

    private string HeaderTitle() => this.host.Find(".form-dialog-header__title").TextContent;

    private AngleSharp.Dom.IElement ButtonNamed(string text) =>
        this.host.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

    public sealed class ProbeForm : ComponentBase
    {
        [Inject] public DialogService Dialogs { get; set; } = null!;

        [Parameter] public string? Value { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormDialogFrame>(0);
            builder.AddAttribute(1, nameof(FormDialogFrame.SaveText), "Send");
            builder.AddAttribute(2, nameof(FormDialogFrame.OnSave), EventCallback.Factory.Create(this, () => Dialogs.Close(Value)));
            builder.AddAttribute(3, nameof(FormDialogFrame.ChildContent), (RenderFragment)(content =>
            {
                content.OpenElement(0, "span");
                content.AddAttribute(1, "class", "probe");
                content.AddContent(2, Value);
                content.CloseElement();
            }));
            builder.CloseComponent();
        }
    }
}
