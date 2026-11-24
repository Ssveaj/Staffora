using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class QualificationDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public QualificationDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_maintains_the_catalogue_and_gives_a_staff_member_two_qualifications()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator, kim) = await ArrangeAsync("Dialog Keeping Staffing", cancellationToken);
        await using var circuit = await CircuitAsync(coordinator);

        using (var page = circuit.Page())
        {
            var rendered = page.Render(builder => Host<Qualifications>(builder));
            rendered.WaitForAssertion(() => Assert.Contains("The catalogue is empty", rendered.Markup, StringComparison.Ordinal), Wait);
            foreach (var name in new[] { "Mathematics", "Physics", "Latin" })
            {
                ClickOnce(rendered, () => rendered.FindAll("button").Single(button => TextOf(button) == "New qualification"));
                WaitForDialog(rendered);
                rendered.Find("[name=qualification-name]").Change(name);
                rendered.WaitForAssertion(() => rendered.Find(".state-entity-dialog button[data-transition='qualification-create']").Click(), Wait);
                rendered.WaitForAssertion(() => Assert.Equal("Active", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
                await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
                rendered.WaitForAssertion(() => Assert.Contains(rendered.FindAll("tr.list-row"), row => row.TextContent.Contains(name, StringComparison.Ordinal)), Wait);
            }

            OpenRow(rendered, "Mathematics");
            rendered.Edit();
            rendered.Find("[name=qualification-name]").Change("Mathematics, upper secondary");
            rendered.Find("[name=qualification-description]").Change("Grades 10 to 12");
            rendered.WaitForAssertion(() => rendered.Find(".state-entity-dialog button[data-transition='qualification-save']").Click(), Wait);
            rendered.WaitForAssertion(() => Assert.Equal("Mathematics, upper secondary", rendered.Find(".state-entity-dialog-header__title").TextContent.Trim()), Wait);
            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());

            OpenRow(rendered, "Latin");
            ClickOnce(rendered, () => rendered.Find(".state-entity-dialog button[data-transition='qualification-archive']"));
            rendered.WaitForAssertion(() => ConfirmButton(rendered, "Archive").Click(), Wait);
            rendered.WaitForAssertion(() => Assert.Equal("Archived", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        }

        var stored = await StoredAsync(scenario.TenantId, cancellationToken);
        Assert.Equal(["Latin", "Mathematics, upper secondary", "Physics"], stored.Select(row => row.Name));
        Assert.Equal([QualificationState.Archived, QualificationState.Active, QualificationState.Active], stored.Select(row => row.State));

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=qualifications]"));
            rendered.WaitForAssertion(() => Assert.Contains("No qualifications yet", rendered.Markup, StringComparison.Ordinal), Wait);
            foreach (var (name, count) in new[] { ("Mathematics, upper secondary", 1), ("Physics", 2) })
            {
                ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog button").Single(button => TextOf(button) == "Add qualification"));
                rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<RadzenDropDown<Guid?>>(), box => box.Instance.Name == "staff-qualification"), Wait);
                var box = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(box => box.Instance.Name == "staff-qualification");
                Assert.DoesNotContain("Latin", ChoiceNames(box), StringComparer.Ordinal);
                await rendered.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(stored.Single(row => row.Name == name).Id));
                rendered.WaitForAssertion(() => Assert.False(FormButton(rendered, "Add qualification").HasAttribute("disabled")), Wait);
                ClickOnce(rendered, () => FormButton(rendered, "Add qualification"));
                rendered.WaitForAssertion(() => Assert.Equal(count, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
            }

            var rows = rendered.FindAll(".state-entity-dialog tr.list-row");
            Assert.Equal(2, rows.Count);
            Assert.Contains("Mathematics, upper secondary", rows[0].TextContent, StringComparison.Ordinal);
            Assert.Contains("Physics", rows[1].TextContent, StringComparison.Ordinal);
        }

        Assert.Equal(2, await HeldCountAsync(kim, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_row_of_the_Qualifications_view_takes_its_qualification_away_and_a_reader_of_staff_changes_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator, kim) = await ArrangeAsync("Dialog Reading Staffing", cancellationToken);
        Guid music, art;
        await using (var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator)))
        {
            var qualifications = keeper.Get<QualificationService>();
            music = (await qualifications.CreateQualificationAsync(new Qualification { Name = "Music" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            art = (await qualifications.CreateQualificationAsync(new Qualification { Name = "Art" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            Assert.True((await qualifications.GiveQualificationAsync(kim, music, cancellationToken)).IsSuccess);
            Assert.True((await qualifications.GiveQualificationAsync(kim, art, cancellationToken)).IsSuccess);
        }

        var reader = await scenario.AddPersonAsync(this.database, "Reader", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, cancellationToken, RolePermission.ResourcesView, RolePermission.UsersView);
        await using (var circuit = await CircuitAsync(reader))
        {
            using (var page = circuit.Page())
            {
                var rendered = HostDialogs(page);
                _ = rendered.InvokeAsync(() => QualificationDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<QualificationService>(), music));
                WaitForDialog(rendered);
                Assert.True(rendered.Find("[name=qualification-name]").HasAttribute("readonly"));
                Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
            }

            using (var page = circuit.Page())
            {
                var rendered = HostDialogs(page);
                _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
                WaitForDialog(rendered);
                ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=qualifications]"));
                rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
                Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Add qualification");
                ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog tr.list-row").First().QuerySelector("button.list-row__open")!);
                rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<StaffQualificationDialog>()), Wait);
                Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Remove qualification");
            }
        }

        await using (var circuit = await CircuitAsync(coordinator))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
            WaitForDialog(rendered);
            ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=qualifications]"));
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
            ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains("Art", StringComparison.Ordinal)).QuerySelector("button.list-row__open")!);
            rendered.WaitForAssertion(() => FormButton(rendered, "Remove qualification").Click(), Wait);
            rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
            Assert.Contains("Music", rendered.Find(".state-entity-dialog tr.list-row").TextContent, StringComparison.Ordinal);
        }

        Assert.Equal(1, await HeldCountAsync(kim, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_Staff_view_lists_the_holders_and_a_row_opens_their_user_dialog_on_top()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, coordinator, kim) = await ArrangeAsync("Dialog Holder Staffing", cancellationToken);
        var lee = await scenario.AddPersonAsync(this.database, "Lee", cancellationToken);
        Guid music, art;
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(lee, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
            var qualifications = owner.Get<QualificationService>();
            music = (await qualifications.CreateQualificationAsync(new Qualification { Name = "Music" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            art = (await qualifications.CreateQualificationAsync(new Qualification { Name = "Art" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            Assert.True((await qualifications.GiveQualificationAsync(kim, music, cancellationToken)).IsSuccess);
            Assert.True((await qualifications.GiveQualificationAsync(lee, music, cancellationToken)).IsSuccess);
            Assert.True((await qualifications.GiveQualificationAsync(lee, art, cancellationToken)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(coordinator);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => QualificationDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<QualificationService>(), music));
        WaitForDialog(rendered);
        Assert.Equal(["Info", "Staff"], rendered.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
        Assert.Empty(rendered.FindAll(".state-entity-dialog tr.list-row"));
        ClickOnce(rendered, () => rendered.Find("[role=tab][data-view=staff]"));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
        var rows = rendered.FindAll(".state-entity-dialog tr.list-row");
        Assert.Contains("Kim", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Lee", rows[1].TextContent, StringComparison.Ordinal);
        Assert.All(rows, row => Assert.Contains("Active", row.TextContent, StringComparison.Ordinal));

        ClickOnce(rendered, () => rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains("Kim", StringComparison.Ordinal)).QuerySelector("button.list-row__open")!);
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindComponents<UserDialog>()), Wait);
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        WaitForDialog(rendered);
        Assert.Contains("Kim", rendered.FindAll(".state-entity-dialog-header__title").Last().TextContent, StringComparison.Ordinal);

        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindComponents<UserDialog>()), Wait);
        WaitForDialog(rendered);
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reader_without_the_staff_permission_sees_no_Staff_tab()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, _, kim) = await ArrangeAsync("Dialog Keeper Staffing", cancellationToken);
        var keeper = await scenario.AddPersonAsync(this.database, "Keeper", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, keeper, cancellationToken, RolePermission.QualificationsManage);
        Guid music;
        await using (var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(keeper)))
        {
            var qualifications = host.Get<QualificationService>();
            music = (await qualifications.CreateQualificationAsync(new Qualification { Name = "Music" }, QualificationWorkflowAttribute.CreateStep, cancellationToken)).Value;
            Assert.True((await qualifications.GiveQualificationAsync(kim, music, cancellationToken)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(keeper);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => QualificationDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<QualificationService>(), music));
        WaitForDialog(rendered);
        Assert.Single(rendered.FindAll("[name=qualification-name]"));
        Assert.Empty(rendered.FindAll("[role=tab]"));
        Assert.Empty(rendered.FindAll(".state-entity-dialog tr.list-row"));
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator, Guid Kim)> ArrangeAsync(string name, CancellationToken cancellationToken)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, name);
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", cancellationToken);
        var kim = await scenario.AddPersonAsync(this.database, "Kim", cancellationToken);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var tenants = owner.Get<RoleHoldingService>();
        Assert.True((await tenants.AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        Assert.True((await tenants.AssignUserToRoleAsync(kim, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator, kim);
    }

    private async Task<List<Qualification>> StoredAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Qualifications.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Name).ToListAsync(cancellationToken);
    }

    private async Task<int> HeldCountAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ResourceQualifications.CountAsync(row => row.Resource.UserId == userId, cancellationToken);
    }

    private static void OpenRow(IRenderedComponent<IComponent> rendered, string name)
    {
        ClickOnce(rendered, () => rendered.FindAll("tr.list-row").Single(row => row.TextContent.Contains(name, StringComparison.Ordinal)).QuerySelector("button.list-row__open")!);
        WaitForDialog(rendered);
    }

    private static List<string> ChoiceNames(IRenderedComponent<RadzenDropDown<Guid?>> box) =>
        [.. ((IEnumerable<Staffora.Core.Models.QualificationChoice>?)box.Instance.Data ?? []).Select(choice => choice.Name)];

    private static IElement FormButton(IRenderedComponent<IComponent> rendered, string text) =>
        rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == text);

    private static IElement ConfirmButton(IRenderedComponent<IComponent> rendered, string text) =>
        rendered.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(Guid userId)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime);
    }

    private sealed class Circuit : IAsyncDisposable
    {
        private readonly ServiceHost host;
        private readonly RuntimeExecutionContext runtime;

        public Circuit(ServiceHost host, RuntimeExecutionContext runtime)
        {
            this.host = host;
            this.runtime = runtime;
        }

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(TimeProvider.System);
            page.Services.AddSingleton(this.host.Get<WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<UserService>());
            page.Services.AddSingleton(this.host.Get<ResourceService>());
            page.Services.AddSingleton(this.host.Get<QualificationService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Identity.IUserAccountGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Organization.IOrganizationUnitGuard>());
            page.Services.AddSingleton(this.host.Get<ITenantRoleGuard>());
            page.Services.AddSingleton(this.host.Get<TenantService>());
            page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            page.Services.AddSingleton(this.host.Get<OrganizationService>());
            page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            return page;
        }

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
