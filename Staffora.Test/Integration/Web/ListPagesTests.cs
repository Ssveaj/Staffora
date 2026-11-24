using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Blazor.Pages.TenantAdmin;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class ListPagesTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ListPagesTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_click_on_a_user_row_opens_that_users_dialog_and_the_list_reads_its_page_again_when_it_closes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit, RolePermission.RolesView);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var other = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Stranger", cancellationToken);

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<Users>(builder));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll("tr.list-row").Count), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindAll($"tr.list-row[data-row='{other}']"));
        Assert.Contains("Colleague Company A", rendered.Find($"tr.list-row[data-row='{colleague}']").TextContent);

        Assert.Empty(rendered.FindAll("button.list-expand, .list-detail"));

        rendered.Find($"tr.list-row[data-row='{colleague}']").Click();

        var dialog = rendered.WaitForComponent<UserDialog>();
        Assert.Equal(colleague, dialog.Instance.Context.EntityId);
        rendered.WaitForAssertion(() => Assert.Equal("Colleague", rendered.Find("[name=user-first-name]").GetAttribute("value")), TimeSpan.FromSeconds(10));

        await RenameUserAsync(colleague, "Renamed", cancellationToken);
        await rendered.InvokeAsync(() => page.Services.GetRequiredService<DialogService>().Close());
        rendered.WaitForAssertion(() => Assert.Contains("Renamed Company A", rendered.Find($"tr.list-row[data-row='{colleague}']").TextContent), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindComponents<UserDialog>());

        rendered.Find($"tr.list-row[data-row='{scenario.A.OwnerId}'] button.list-row__open").Click();
        Assert.Equal(scenario.A.OwnerId, rendered.WaitForComponent<UserDialog>().Instance.Context.EntityId);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_click_on_a_unit_row_in_the_organization_list_opens_that_units_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken,
            RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.UsersView, RolePermission.RolesView);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        Guid north, sales;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var units = host.Get<OrganizationService>();
            north = await units.CreateUnitIdAsync("North", OrganizationUnitType.Region, rootId, cancellationToken);
            sales = await units.CreateUnitIdAsync("Sales", OrganizationUnitType.Branch, north, cancellationToken);
        }

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/tenant/management/organization-units?view=list");
        var rendered = page.Render(builder => Host<OrganizationUnits>(builder));
        rendered.WaitForAssertion(() => Assert.Equal(3, rendered.FindAll("tr.list-row").Count), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindAll("[role=tree]"));
        Assert.Equal($"Below {scenario.A.Name} › North", rendered.FindAll(".list-heading__label")[2].TextContent.Trim());

        await rendered.InvokeAsync(() => rendered.Find($"tr.list-row[data-row='{sales}']").Click());

        var dialog = rendered.WaitForComponent<OrganizationUnitDialog>();
        Assert.Equal(sales, dialog.Instance.Context.EntityId);
        rendered.WaitForAssertion(() => Assert.Equal("Sales", rendered.Find("[name=unit-name]").GetAttribute("value")), TimeSpan.FromSeconds(10));

        rendered.Edit();
        var parents = dialog.FindComponents<RadzenDropDown<Guid?>>().Single(component => component.Instance.Name == "unit-parent");
        Assert.Equal([scenario.A.Name, $"{scenario.A.Name} › North"], parents.Instance.Data!.Cast<OrganizationUnit>().Select(option => option.Name));

        await RenameUnitAsync(sales, "Sales and marketing", cancellationToken);
        await rendered.InvokeAsync(() => page.Services.GetRequiredService<DialogService>().Close(false));
        rendered.WaitForAssertion(() => Assert.Contains("Sales and marketing", rendered.Find($"tr.list-row[data-row='{sales}']").TextContent), TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_organization_page_opens_on_its_tree_and_switches_to_the_list_in_the_address()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.UsersView, RolePermission.RolesView);

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        var urlState = page.Services.GetRequiredService<UrlStateService>();
        var rendered = page.Render(builder => Host<OrganizationUnits>(builder));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("[role=tree]")), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindAll("table.list-table"));
        Assert.Equal("true", rendered.Find(".org-view-switch button:first-child").GetAttribute("aria-pressed"));

        rendered.FindAll(".org-view-switch button")[1].Click();

        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("tr.list-row")), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindAll("[role=tree]"));
        Assert.Equal("list", urlState.ReadRaw(UrlStateKeys.View));
        Assert.Equal("true", rendered.FindAll(".org-view-switch button")[1].GetAttribute("aria-pressed"));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_viewer_of_users_opens_a_row_read_only_and_an_editor_saves_through_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.UsersView);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using (var circuit = await CircuitAsync(viewer))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<Users>(builder));
            rendered.WaitForAssertion(() => Assert.Equal(3, rendered.FindAll("tr.list-row").Count), TimeSpan.FromSeconds(10));

            await rendered.InvokeAsync(() => rendered.Find($"tr.list-row[data-row='{colleague}'] button.list-row__open").Click());

            rendered.WaitForComponent<UserDialog>();
            rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.Find(".state-entity-dialog").GetAttribute("data-load-state")), TimeSpan.FromSeconds(10));
            rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), TimeSpan.FromSeconds(10));
            Assert.Equal("Colleague", rendered.Find("[name=user-first-name]").GetAttribute("value"));
            Assert.All(["user-first-name", "user-last-name", "user-email", "user-username", "user-phone"], name => Assert.True(rendered.Find($"[name={name}]").HasAttribute("readonly"), name));
            Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
            Assert.Null(rendered.EditButton());
        }

        await using (var circuit = await CircuitAsync(scenario.A.OwnerId))
        {
            using var page = circuit.Page();
            var rendered = page.Render(builder => Host<Users>(builder));
            rendered.WaitForAssertion(() => Assert.Equal(3, rendered.FindAll("tr.list-row").Count), TimeSpan.FromSeconds(10));

            await rendered.InvokeAsync(() => rendered.Find($"tr.list-row[data-row='{colleague}'] button.list-row__open").Click());

            rendered.WaitForComponent<UserDialog>();
            rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), TimeSpan.FromSeconds(10));
            Assert.Equal("Colleague", rendered.Find("[name=user-first-name]").GetAttribute("value"));
            Assert.True(rendered.Find("[name=user-first-name]").HasAttribute("readonly"));
            rendered.Edit();
            Assert.False(rendered.Find("[name=user-first-name]").HasAttribute("readonly"));
            Assert.Contains("user-save", rendered.FindAll(".state-entity-dialog button[data-transition]").Select(button => button.GetAttribute("data-transition")));

            rendered.Find("[name=user-first-name]").Change("Edited");
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog .state-entity-dialog-header__unsaved")), TimeSpan.FromSeconds(10));
            rendered.Find(".state-entity-dialog button[data-transition='user-save']").Click();
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog .state-entity-dialog-header__unsaved")), TimeSpan.FromSeconds(10));
            await rendered.InvokeAsync(() => page.Services.GetRequiredService<DialogService>().Close());

            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindComponents<UserDialog>()), TimeSpan.FromSeconds(10));
            rendered.WaitForAssertion(() => Assert.Contains("Edited Company A", rendered.Find($"tr.list-row[data-row='{colleague}']").TextContent), TimeSpan.FromSeconds(10));
        }

        await using var system = this.database.CreateSystemContext();
        Assert.Equal("Edited", await system.Users.Where(user => user.Id == colleague).Select(user => user.FirstName).SingleAsync(cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_viewer_of_units_opens_a_list_row_read_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        Guid north;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            north = await host.Get<OrganizationService>().CreateUnitIdAsync("North", OrganizationUnitType.Region, rootId, cancellationToken);
        }

        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.OrganizationUnitsView);

        await using var circuit = await CircuitAsync(viewer);
        using var page = circuit.Page();
        page.Services.GetRequiredService<NavigationManager>().NavigateTo("/tenant/management/organization-units?view=list");
        var rendered = page.Render(builder => Host<OrganizationUnits>(builder));
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll("tr.list-row").Count), TimeSpan.FromSeconds(10));
        await rendered.InvokeAsync(() => rendered.Find($"tr.list-row[data-row='{north}']").Click());

        var dialog = rendered.WaitForComponent<OrganizationUnitDialog>();
        rendered.WaitForAssertion(() => Assert.Equal("North", rendered.Find("[name=unit-name]").GetAttribute("value")), TimeSpan.FromSeconds(10));
        Assert.True(rendered.Find("[name=unit-name]").HasAttribute("readonly"));
        Assert.True(rendered.Find("[name=unit-description]").HasAttribute("readonly"));
        Assert.All(dialog.FindComponents<RadzenDropDown<Guid?>>(), dropDown => Assert.True(dropDown.Instance.ReadOnly));
        Assert.All(dialog.FindComponents<RadzenDropDown<OrganizationUnitType>>(), dropDown => Assert.True(dropDown.Instance.ReadOnly));
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
    }

    private async Task<Circuit> CircuitAsync(Guid userId)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, SessionCircuit.Cookie(userId));
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        return new Circuit(host, runtime);
    }

    private static void Host<TPage>(RenderTreeBuilder builder) where TPage : IComponent
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
        builder.CloseComponent();
        builder.OpenComponent<TPage>(1);
        builder.CloseComponent();
    }

    private async Task RenameUserAsync(Guid userId, string firstName, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var user = await system.Users.SingleAsync(row => row.Id == userId, cancellationToken);
        user.FirstName = firstName;
        system.NameEditor(user, user.CreatedById);
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task RenameUnitAsync(Guid unitId, string name, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var unit = await system.OrganizationUnits.SingleAsync(row => row.Id == unitId, cancellationToken);
        unit.Name = name;
        system.NameEditor(unit, unit.CreatedById);
        await system.SaveChangesAsync(cancellationToken);
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
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Workflow.WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<UserService>());
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Settings.TenantClock>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Identity.IUserAccountGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Organization.IOrganizationUnitGuard>());
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Tenancy.ITenantRoleGuard>());
            page.Services.AddSingleton(this.host.Get<TenantService>());
            page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            page.Services.AddSingleton(this.host.Get<OrganizationService>());
            page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            return page;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
