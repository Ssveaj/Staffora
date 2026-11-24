using System.Text.RegularExpressions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Staffora.Services.Workflow;
using Staffora.Test.Docs;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web.StateEntity;

namespace Staffora.Test.Integration.Web;

public partial class StateEntityDialogTests
{
    private static readonly string fixtureFolder = Path.Combine(RepoFiles.Root, "tests", "e2e", "fixtures", "state-entity-dialog");

    [Fact]
    public async Task The_browser_journeys_markup_is_what_the_dialogs_components_render_now()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var id = await AddParentAsync(scenario.A, TestParentState.Canceled, "Spring period job", cancellationToken);

        var clock = new FakeClock(new DateTimeOffset(2026, 6, 29, 12, 5, 0, TimeSpan.Zero));
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        foreach (var (runtime, step, next) in new[] { (platform.PlatformOwnerRuntime(), "test-parent-reopen", new TimeSpan(1, 2, 25, 0)), (impersonating, "test-parent-save", new TimeSpan(16, 42, 0)), (scenario.A.Runtime(), "test-parent-save", new TimeSpan(10, 35, 0)), (scenario.A.Runtime(), "test-parent-save", TimeSpan.FromMinutes(13)) })
        {
            await using var mover = ServiceHost.Create(this.database, runtime, services => services.AddSingleton<TimeProvider>(clock));
            var moved = await mover.Get<WorkflowService>().TransitionEntityAsync<TestParentState, TestParent>(id, step, TestParentWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            Assert.True(moved.IsSuccess, moved.Error);
            clock.Advance(next);
        }

        await AddNoticeAsync(scenario.A, id, cancellationToken);
        await using var circuit = Circuit(scenario.A.Runtime(), clock: clock);
        var dialog = await circuit.OpenLoadedAsync(id);

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.Equal(3, Card(dialog).QuerySelectorAll("[data-history-step]").Length), Wait);
        var rendered = new Dictionary<string, string> { ["dialog.html"] = circuit.Dialog.Markup };

        dialog.Step("test-parent-delete").Click();
        dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll(".form-dialog")), Wait);
        rendered["confirm.html"] = TopWrapper(dialog);
        dialog.ConfirmButton("Cancel").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);

        dialog.Host.Find(".state-pill__button").Click();
        dialog.Host.WaitForAssertion(() => Assert.False(Card(dialog).HasAttribute("hidden")), Wait);
        Card(dialog).QuerySelector("[data-open-history]")!.Click();
        dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll("[data-state-history] li")), Wait);
        rendered["history.html"] = TopWrapper(dialog);
        await dialog.Host.InvokeAsync(() => circuit.Dialogs.Close());
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);

        await AssertFixturesAsync(rendered, markup => Normalize(markup, scenario, platform), cancellationToken);
    }

    [Fact]
    public async Task The_browser_journeys_field_markup_is_what_the_fields_render_now()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken);
        var grade = await AddFieldAsync(scenario.A, "Grade", Staffora.Core.Entities.EntityAttributes.AttributeValueType.Text, required: true, cancellationToken);
        var room = await AddFieldAsync(scenario.A, "Room", Staffora.Core.Entities.EntityAttributes.AttributeValueType.Choice, required: false, cancellationToken, "North", "South");
        var seats = await AddFieldAsync(scenario.A, "Seats", Staffora.Core.Entities.EntityAttributes.AttributeValueType.Integer, required: false, cancellationToken);
        var budget = await AddFieldAsync(scenario.A, "Budget", Staffora.Core.Entities.EntityAttributes.AttributeValueType.Number, required: false, cancellationToken);
        var opens = await AddFieldAsync(scenario.A, "Opens on", Staffora.Core.Entities.EntityAttributes.AttributeValueType.Date, required: false, cancellationToken);
        var projector = await AddFieldAsync(scenario.A, "Projector", Staffora.Core.Entities.EntityAttributes.AttributeValueType.Boolean, required: false, cancellationToken);
        var draft = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        var confirmed = await AddParentWithValuesAsync(scenario.A, TestParentState.Confirmed, "Winter period job", cancellationToken, (grade, "9"));
        var canceled = await AddParentWithValuesAsync(scenario.A, TestParentState.Canceled, "Summer job", cancellationToken, (grade, "8"));

        Guid active;
        await using (var writer = this.database.CreateContext(scenario.A.Runtime()))
        using (writer.BeginSystemScope("test: insert the active parent with its values of every type"))
        {
            var parent = new TestParent
            {
                Name = "Autumn period job",
                State = TestParentState.Active,
                Attributes =
                [
                    new TestParentAttribute { EntityAttributeDefinitionId = grade, StringValue = "7" },
                    new TestParentAttribute { EntityAttributeDefinitionId = room, StringValue = "north" },
                    new TestParentAttribute { EntityAttributeDefinitionId = seats, Int32Value = 24 },
                    new TestParentAttribute { EntityAttributeDefinitionId = budget, DoubleValue = 1250.5 },
                    new TestParentAttribute { EntityAttributeDefinitionId = opens, DateValue = new DateOnly(2026, 9, 1) },
                    new TestParentAttribute { EntityAttributeDefinitionId = projector, BooleanValue = true },
                ],
            };
            writer.TestParents.Add(parent);
            await writer.SaveChangesAsync(cancellationToken);
            active = parent.Id;
        }

        var draftChild = await AddChildAsync(scenario.A, draft, "Afternoon group", cancellationToken);
        var child = await AddChildAsync(scenario.A, draft, "Morning group", cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestChildren.Where(row => row.Id == child).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, TestChildState.Active).SetProperty(row => row.IsPinned, true), cancellationToken);
        }

        await using var circuit = Circuit(scenario.A.Runtime());
        var rendered = new Dictionary<string, string>();
        foreach (var (name, id) in new[] { ("fields-draft.html", draft), ("fields-active.html", active), ("fields-confirmed.html", confirmed), ("fields-canceled.html", canceled) })
        {
            var dialog = await circuit.OpenLoadedAsync(id);
            rendered[name] = TopWrapper(dialog);
            await dialog.CloseByEscAsync();
        }

        foreach (var (name, id) in new[] { ("fields-child-draft.html", draftChild), ("fields-child.html", child) })
        {
            var childDialog = await circuit.OpenChildLoadedAsync(id);
            rendered[name] = TopWrapper(childDialog);
            await childDialog.CloseByEscAsync();
        }

        await AssertFixturesAsync(rendered, markup => Normalize(markup, scenario, platform: null), cancellationToken);
    }

    [Fact]
    public async Task The_browser_journeys_views_markup_is_what_the_views_render_now()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await ScenarioAsync(cancellationToken, TestParentDialogService.ChildrenPermission);
        var id = await AddParentAsync(scenario.A, TestParentState.Draft, "Spring period job", cancellationToken);
        await AddChildAsync(scenario.A, id, "Afternoon group", cancellationToken);
        var morning = await AddChildAsync(scenario.A, id, "Morning group", cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            await system.TestChildren.Where(row => row.Id == morning).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, TestChildState.Active).SetProperty(row => row.IsPinned, true), cancellationToken);
        }

        await using var circuit = Circuit(scenario.A.Runtime());
        var dialog = await circuit.OpenLoadedAsync(id);
        var rendered = new Dictionary<string, string> { ["views-info.html"] = TopWrapper(dialog) };

        SelectView(dialog, TestParentDialogContext.ChildrenView);
        WaitForRows(dialog, 2);
        rendered["views-children.html"] = TopWrapper(dialog);

        await OpenRowAsync(circuit, dialog, morning);
        rendered["views-stacked.html"] = circuit.Dialog.Markup;
        await CloseTopByEscAsync(dialog, layouts: 1);

        SelectView(dialog, TestParentDialogContext.InfoView);
        dialog.Host.WaitForAssertion(() => Assert.Equal(TestParentDialogContext.InfoView, SelectedView(dialog)), Wait);
        dialog.Host.Find("[name=test-parent-name]").Change(string.Empty);
        dialog.WaitForEdit();
        SelectView(dialog, TestParentDialogContext.ChildrenView);
        dialog.Host.WaitForAssertion(() => Assert.Equal("Discard changes?", dialog.Host.Find(".form-dialog-header__title").TextContent), Wait);
        rendered["views-leave-ask.html"] = circuit.Dialog.Markup;
        dialog.ConfirmButton("Keep editing").Click();
        dialog.Host.WaitForAssertion(() => Assert.Empty(dialog.Host.FindAll(".form-dialog")), Wait);
        _ = dialog.Host.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        dialog.Host.WaitForAssertion(() => Assert.Single(dialog.Host.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        await dialog.Result.WaitAsync(Wait, cancellationToken);

        var autumn = await AddParentAsync(scenario.A, TestParentState.Draft, "Autumn period job", cancellationToken);
        var (session, _) = await OpenSessionOfAsync(circuit, [id, autumn]);
        rendered["views-session.html"] = TopWrapper(session);

        await AssertFixturesAsync(rendered, markup => Normalize(markup, scenario, platform: null), cancellationToken);
    }

    [Fact]
    public void The_normalized_markup_keeps_distinct_ids_apart_and_the_same_id_the_same()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var markup = $"<label for=\"a-{first}\">A</label><input id=\"a-{first}\" /><label for=\"a-{second}\">B</label><input id=\"a-{second}\" />"
            + "<div id=\"Ab3_x-9Qz0\"></div><ul id=\"popupAb3_x-9Qz0\"></ul><div id=\"Zq8rT2mLp1\" aria-controls=\"Ab3_x-9Qz0\"></div><button id=\"K7vB2nQx9Lpm\"></button>";

        var once = Normalize(markup, scenario: null, platform: null);
        var again = Normalize(markup.Replace(first.ToString(), Guid.NewGuid().ToString()).Replace(second.ToString(), Guid.NewGuid().ToString()), scenario: null, platform: null);

        Assert.Equal(once, again);
        Assert.Contains("for=\"a-00000000-0000-0000-0000-000000000001\">A", once, StringComparison.Ordinal);
        Assert.Contains("id=\"a-00000000-0000-0000-0000-000000000001\" ", once, StringComparison.Ordinal);
        Assert.Contains("for=\"a-00000000-0000-0000-0000-000000000002\">B", once, StringComparison.Ordinal);
        Assert.Contains("<div id=\"rz-generated-1\"></div><ul id=\"popuprz-generated-1\"></ul><div id=\"rz-generated-2\" aria-controls=\"rz-generated-1\"></div><button id=\"rz-generated-3pm\">", once, StringComparison.Ordinal);
    }

    private async Task AddNoticeAsync(ScenarioTenant tenant, Guid id, CancellationToken cancellationToken)
    {
        var newest = (await HistoryOfAsync(id, cancellationToken))[^1];
        await using var writer = this.database.CreateContext(tenant.Runtime());
        using var lease = writer.BeginSystemScope("test: a delivered notice of the newest step");
        writer.OutgoingMessages.Add(new Staffora.Core.Entities.Messaging.OutgoingMessage
        {
            MessageType = Staffora.Core.Messaging.MessageTypes.AbsenceQuestionToStaff.Key,
            Category = Staffora.Core.Messaging.MessageTypes.AbsenceQuestionToStaff.Category,
            EntityType = nameof(TestParent),
            EntityId = id,
            HistoryEntryId = newest.Id,
            Subject = "Question about your absence",
            Text = "A question",
            RecipientUserIds = [tenant.OwnerId],
            Status = Staffora.Core.Entities.Messaging.OutgoingMessageStatus.Delivered,
            RecipientCount = 1,
        });
        await writer.SaveChangesAsync(cancellationToken);
    }

    private static async Task AssertFixturesAsync(Dictionary<string, string> rendered, Func<string, string> normalize, CancellationToken cancellationToken)
    {
        var update = Environment.GetEnvironmentVariable("STAFFORA_UPDATE_E2E_FIXTURES") == "1";
        foreach (var (name, markup) in rendered)
        {
            Assert.False(markup.Contains("rz-calendar", StringComparison.Ordinal), $"{name} holds a date picker's calendar, which is drawn for the day it was rendered; a date picker draws its calendar only when it opens (PopupRenderMode.OnDemand).");
            var normalized = normalize(markup);
            var path = Path.Combine(fixtureFolder, name);
            if (update)
            {
                Directory.CreateDirectory(fixtureFolder);
                await File.WriteAllTextAsync(path, normalized, cancellationToken);
                continue;
            }

            Assert.True(File.Exists(path), $"{path} is missing; run this test with STAFFORA_UPDATE_E2E_FIXTURES=1 to write it.");
            var committed = (await File.ReadAllTextAsync(path, cancellationToken)).ReplaceLineEndings("\n");
            Assert.True(committed == normalized, $"{name} is not what the components render now; run this test with STAFFORA_UPDATE_E2E_FIXTURES=1, review the change and run npm run test:e2e.");
        }
    }

    private static string TopWrapper(OpenDialog dialog) => dialog.Host.FindAll(".rz-dialog-wrapper").Last().OuterHtml;

    private static string Normalize(string markup, TwoTenantScenario? scenario, ScenarioTenant? platform)
    {
        var text = Regex.Replace(markup, @"\s+blazor:[\w:]+(=""[^""]*"")?", string.Empty);

        var guids = Regex.Matches(text, GuidPattern).Select(match => match.Value.ToLowerInvariant()).Distinct().ToList();
        text = Regex.Replace(text, GuidPattern, match => $"00000000-0000-0000-0000-{guids.IndexOf(match.Value.ToLowerInvariant()) + 1:x12}");

        var generatedIds = Regex.Matches(text, @"\bid=""(?:popup)?([A-Za-z0-9_-]{10})(?:pm|nm)?""").Select(match => match.Groups[1].Value).Distinct().ToList();
        for (var index = 0; index < generatedIds.Count; index++)
        {
            text = Regex.Replace(text, $@"(?:(?<![A-Za-z0-9_])|(?<=popup)){Regex.Escape(generatedIds[index])}(?=(?:pm|nm)?(?![A-Za-z0-9_]))", $"rz-generated-{index + 1}");
        }
        foreach (var prefix in new[] { "state-pill-card", "state-entity-dialog-title", "transition-card", "state-entity-dialog-views", "list", "form-dialog" })
        {
            var numbers = Regex.Matches(text, $@"\b{prefix}-(\d+)\b").Select(match => match.Groups[1].Value).Distinct().ToList();
            text = Regex.Replace(text, $@"\b{prefix}-(\d+)\b", match => $"{prefix}-{numbers.IndexOf(match.Groups[1].Value) + 1}");
        }

        text = platform is null ? text : text.Replace(platform.Name, "Platform", StringComparison.Ordinal);
        text = scenario is null ? text : text.Replace(scenario.A.Name, "Company A", StringComparison.Ordinal);
        return text.ReplaceLineEndings("\n").TrimEnd() + "\n";
    }

    private const string GuidPattern = @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
}
