using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class JobSlotGroupViewTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public JobSlotGroupViewTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_group_of_three_slots_with_two_holders_shows_as_one_row_reading_two_of_three()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Group View Staffing", cancellationToken);
        var (ada, bo) = (await scenario.AddStaffAsync(this.database, "Ada", cancellationToken), await scenario.AddStaffAsync(this.database, "Bo", cancellationToken));
        var (job, _) = await scenario.StartedJobAsync(this.database, "Office cleaning", new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 11), cancellationToken, slots: ["Supervisor"]);
        Guid first;
        await using (var host = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var added = await host.Get<JobSlotService>().CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner", Count = 3 }, JobSlotWorkflowAttribute.AddStep, cancellationToken);
            Assert.True(added.IsSuccess, added.Error);
            first = added.Value;
        }

        Assert.True((await scenario.AssignAsync(this.database, scenario.Coordinator, first, ada, cancellationToken)).IsSuccess);
        Assert.True((await scenario.AssignAsync(this.database, scenario.Coordinator, first, bo, cancellationToken)).IsSuccess);

        var (circuitHost, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(scenario.Coordinator), AssignmentScenario.UseClock);
        await using var circuitLifetime = circuitHost;
        await circuitHost.Get<UserSessionService>().InitializeFromClaimsAsync();
        using var page = new PageContext();
        page.Services.AddSingleton<IRuntimeExecutionContext>(runtime);
        page.Services.AddSingleton(AssignmentScenario.Clock);
        foreach (var shared in new object[] { circuitHost.Get<WorkflowService>(), circuitHost.Get<UserSessionService>(), circuitHost.Get<PermissionService>(), circuitHost.Get<JobService>(), circuitHost.Get<JobSlotService>(), circuitHost.Get<SlotAssignmentService>(), circuitHost.Get<RequestRoundService>() })
        {
            page.Services.AddSingleton(shared.GetType(), shared);
        }

        page.Services.AddSingleton(circuitHost.Get<IJobGuard>());
        page.Services.AddSingleton(circuitHost.Get<IJobSlotGuard>());
        page.Services.AddSingleton(circuitHost.Get<ISlotAssignmentGuard>());
        var rendered = page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
        });

        _ = rendered.InvokeAsync(() => JobDialog.OpenAsync(page.Services.GetRequiredService<DialogService>(), circuitHost.Get<JobService>(), job));
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => rendered.Find("[role=tab][data-view=slots]").Click(), Wait);

        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
        var group = rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains("Cleaner", StringComparison.Ordinal));
        Assert.Contains("2 of 3", group.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ada Example", group.TextContent, StringComparison.Ordinal);
        Assert.Contains("Bo Example", group.TextContent, StringComparison.Ordinal);
        var single = rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains("Supervisor", StringComparison.Ordinal));
        Assert.DoesNotContain(" of ", single.TextContent, StringComparison.Ordinal);
        Assert.Contains("Uncovered", single.TextContent, StringComparison.Ordinal);

        rendered.WaitForAssertion(() => rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains("Cleaner", StringComparison.Ordinal)).QuerySelector("button.list-row__open")!.Click(), Wait);
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<JobSlotDialog>()), Wait);
        Assert.Equal(first, rendered.FindComponents<JobSlotDialog>().Last().Instance.Context.EntityId);
        rendered.WaitForAssertion(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelector("[role=tab][data-view=staffing]")!.Click(), Wait);
        rendered.WaitForAssertion(() => Assert.Contains("2 of 3 held", rendered.FindAll("[data-open-slots]").Last().TextContent, StringComparison.Ordinal), Wait);
    }

    private sealed class PageContext : WebTestContext;
}
