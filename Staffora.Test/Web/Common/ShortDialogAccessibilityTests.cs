using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Core.Models;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Web.Common;

public class ShortDialogAccessibilityTests : WebTestContext
{
    public static readonly IReadOnlyList<string> Covered = [nameof(MessageDialog), nameof(PromptDialog), nameof(ErrorDialog), nameof(ConfirmDialog), nameof(StateHistoryDialog), nameof(DeadLetterDialog)];

    private readonly IRenderedComponent<RadzenDialog> host;
    private readonly DialogService dialogs;

    public ShortDialogAccessibilityTests()
    {
        this.host = Render<RadzenDialog>();
        this.dialogs = Services.GetRequiredService<DialogService>();
    }

    public static TheoryData<string> Dialogs() => [.. Covered];

    [Theory]
    [MemberData(nameof(Dialogs))]
    public async Task The_dialog_is_named_by_its_title_and_every_control_has_a_name(string dialog)
    {
        const string title = "Dialog title";
        await this.host.InvokeAsync(() =>
        {
            _ = dialog switch
            {
                nameof(MessageDialog) => this.dialogs.ShowMessageAsync("Four staff members were reminded.", title),
                nameof(PromptDialog) => this.dialogs.PromptAsync(title, "Why do you decline?", placeholder: "Your reason"),
                nameof(ErrorDialog) => this.dialogs.ShowErrorAsync("The role is held by users.", title),
                nameof(ConfirmDialog) => this.dialogs.ConfirmAsync("Delete the role?", title, destructive: true),

                nameof(StateHistoryDialog) => this.dialogs.OpenFormDialogAsync<StateHistoryDialog>(title, parameters => parameters
                    .Set(dialog => dialog.LoadPage, query => Task.FromResult(Result<StateHistoryPage>.Success(SampleHistory(query.Take))))),
                nameof(DeadLetterDialog) => this.dialogs.OpenFormDialogAsync<DeadLetterDialog>(title, parameters => parameters
                    .Set(dialog => dialog.Letter, new PlatformDeadLetterRow(Guid.NewGuid(), "portal.order-sent", true, Guid.NewGuid(), "Company B", Guid.NewGuid(), 3, 5, new DateTime(2027, 2, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2027, 2, 1, 9, 0, 0, DateTimeKind.Utc), "InvalidOperationException"))),
                _ => throw new ArgumentOutOfRangeException(nameof(dialog), dialog, "Not a short dialog."),
            };
        });
        this.host.WaitForElement(".form-dialog");

        Assert.Equal(dialog, this.host.Find(".form-dialog").GetAttribute("data-form-dialog"));

        var element = this.host.Find("[role=dialog]");
        var labelledBy = element.GetAttribute("aria-labelledby");
        Assert.Equal(title, labelledBy is null ? element.GetAttribute("aria-label") : this.host.Find($"#{labelledBy}").TextContent.Trim());

        var buttons = this.host.FindAll("[role=dialog] button");
        Assert.NotEmpty(buttons);
        Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(NameOf(button)), $"A button without a name: {button.OuterHtml}"));

        Assert.All(this.host.FindAll("[role=dialog] input:not([type=hidden]), [role=dialog] textarea, [role=dialog] select"), field =>
            Assert.True(
                !string.IsNullOrWhiteSpace(field.GetAttribute("aria-label"))
                || !string.IsNullOrWhiteSpace(field.GetAttribute("aria-labelledby"))
                || (field.Id is { Length: > 0 } id && this.host.FindAll($"label[for='{id}']").Count > 0),
                $"A field without a name: {field.OuterHtml}"));
    }

    private static StateHistoryPage SampleHistory(int take) => new(
        [.. Enumerable.Range(0, Math.Min(take, 25)).Select(index => new StateHistoryStep(
            Guid.NewGuid(), "test-parent-save", "Save", TestParentState.Draft, TestParentState.Draft,
            new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(2)).AddMinutes(-index), "Owner Company A", index == 0 ? [StateHistoryService.ImpersonatedNote] : [],
            new StateHistoryCursor(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(-index), Guid.NewGuid())))],
        25, 0, "Europe/Stockholm");

    private static string? NameOf(IElement button) =>
        button.GetAttribute("aria-label") is { Length: > 0 } label ? label : button.TextContent.Trim();
}
