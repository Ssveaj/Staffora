using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Blazor.Layout;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.Catalogue;
using Staffora.Services.Jobs;
using Staffora.Services.Search;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class GlobalSearchDialogTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public GlobalSearchDialogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_keyboard_opens_the_search_with_slash_moves_through_the_hits_opens_the_chosen_job_with_Enter_and_closes_with_Escape()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var token = $"K{Guid.NewGuid():N}"[..9];
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Search Keys", cancellationToken);
        var staffUser = await scenario.AddStaffAsync(this.database, $"Marlowe{token}", cancellationToken);
        var (harbour, _) = await scenario.StartedJobAsync(this.database, $"Harbour {token}", new DateOnly(2026, 11, 9), new DateOnly(2026, 12, 18), cancellationToken);
        var (depot, _) = await scenario.StartedJobAsync(this.database, $"{token} Depot", new DateOnly(2026, 11, 9), new DateOnly(2026, 12, 18), cancellationToken);

        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(scenario.Coordinator));
        await using var circuit = host;
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        using var page = Page(host, runtime);
        var rendered = page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<GlobalSearchButton>(1);
            builder.CloseComponent();
        });
        rendered.WaitForAssertion(() => Assert.Equal("Search", rendered.Find("button[data-global-search]").GetAttribute("aria-label")), Wait);
        var button = rendered.FindComponent<GlobalSearchButton>();

        _ = rendered.InvokeAsync(button.Instance.OpenFromShortcutAsync);
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("[data-form-dialog=GlobalSearchDialog]")), Wait);
        var input = rendered.Find("input[role=combobox]");
        Assert.Equal("false", input.GetAttribute("aria-expanded"));
        Assert.Equal("Search staff members, jobs and customers", input.GetAttribute("placeholder"));
        Assert.Contains("Type at least 2 letters", rendered.Find("[data-search-dialog]").TextContent, StringComparison.Ordinal);

        rendered.Find("input[role=combobox]").Input(token);
        rendered.WaitForAssertion(() => Assert.Equal(3, rendered.FindAll("[role=option]").Count), Wait);
        Assert.Equal(["StaffMembers", "Jobs"], rendered.FindAll("[role=group]").Select(group => group.GetAttribute("data-search-group")));
        Assert.Equal([staffUser, depot, harbour], rendered.FindAll("[role=option]").Select(option => Guid.Parse(option.GetAttribute("data-hit-id")!)));
        Assert.Equal("3 matches", rendered.Find("[data-search-status]").TextContent);
        Assert.Equal("true", rendered.Find("input[role=combobox]").GetAttribute("aria-expanded"));
        AssertActive(rendered, 0);

        Press(rendered, "ArrowDown");
        AssertActive(rendered, 1);
        Press(rendered, "ArrowDown");
        AssertActive(rendered, 2);
        Press(rendered, "ArrowDown");
        AssertActive(rendered, 0);
        Press(rendered, "ArrowUp");
        AssertActive(rendered, 2);

        Press(rendered, "Enter");
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-form-dialog=GlobalSearchDialog]")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
        Assert.Equal($"Harbour {token}", rendered.Find(".state-entity-dialog-header__title").TextContent.Trim());
        Assert.Equal([new DialogStackSegment(DialogTokens.Job, harbour)], page.Services.GetRequiredService<UrlStateService>().DialogStack);

        await rendered.InvokeAsync(() => page.Services.GetRequiredService<DialogService>().Close());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);

        rendered.Find("button[data-global-search]").Click();
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("[data-form-dialog=GlobalSearchDialog]")), Wait);
        rendered.Find("input[role=combobox]").Input("Brightpath");
        rendered.WaitForAssertion(() => Assert.Equal([scenario.Customer], rendered.FindAll("[role=option]").Select(option => Guid.Parse(option.GetAttribute("data-hit-id")!))), Wait);
        Press(rendered, "Escape");
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll("[data-form-dialog=GlobalSearchDialog]")), Wait);
        Assert.Empty(rendered.FindAll(".state-entity-dialog"));
        Assert.Empty(page.Services.GetRequiredService<UrlStateService>().DialogStack);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reader_who_may_search_nothing_gets_no_search_button_and_the_shortcut_opens_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Search None", cancellationToken);
        var nobody = await scenario.StaffMembers.AddPersonAsync(this.database, "Noor", cancellationToken);

        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(nobody));
        await using var circuit = host;
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        using var page = Page(host, runtime);
        var rendered = page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<GlobalSearchButton>(1);
            builder.CloseComponent();
        });
        var button = rendered.FindComponent<GlobalSearchButton>();
        await rendered.InvokeAsync(button.Instance.OpenFromShortcutAsync);

        Assert.Empty(rendered.FindAll("button[data-global-search]"));
        Assert.Empty(rendered.FindAll("[data-form-dialog]"));
    }

    private static void Press(IRenderedComponent<IComponent> rendered, string key) =>
        rendered.Find("input[role=combobox]").KeyDown(new KeyboardEventArgs { Key = key });

    private static void AssertActive(IRenderedComponent<IComponent> rendered, int index)
    {
        var options = rendered.FindAll("[role=option]");
        Assert.Equal(options[index].Id, rendered.Find("input[role=combobox]").GetAttribute("aria-activedescendant"));
        Assert.Equal(index, options.ToList().FindIndex(option => option.GetAttribute("aria-selected") == "true"));
        Assert.Single(options, option => option.GetAttribute("aria-selected") == "true");
    }

    private static PageContext Page(ServiceHost host, RuntimeExecutionContext runtime)
    {
        var page = new PageContext();
        page.Services.AddSingleton<IRuntimeExecutionContext>(runtime);
        page.Services.AddSingleton(TimeProvider.System);
        page.Services.AddSingleton(host.Get<WorkflowService>());
        page.Services.AddSingleton(host.Get<UserSessionService>());
        page.Services.AddSingleton(host.Get<PermissionService>());
        page.Services.AddSingleton(host.Get<GlobalSearchService>());
        page.Services.AddSingleton(host.Get<JobService>());
        page.Services.AddSingleton(host.Get<JobSlotService>());
        page.Services.AddSingleton(host.Get<SlotAssignmentService>());
        page.Services.AddSingleton(host.Get<RequestRoundService>());
        page.Services.AddSingleton(host.Get<ServiceCatalogueService>());
        page.Services.AddSingleton(host.Get<IJobGuard>());
        page.Services.AddSingleton(host.Get<IJobSlotGuard>());
        page.Services.AddSingleton(host.Get<IServiceGuard>());
        page.Services.AddSingleton(host.Get<ISlotAssignmentGuard>());
        return page;
    }

    private sealed class PageContext : WebTestContext;
}
