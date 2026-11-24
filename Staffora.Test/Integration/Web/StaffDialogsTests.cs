using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.Staffing;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class StaffDialogsTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public StaffDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_role_editor_shows_the_permissions_grouped_by_area_and_the_staff_flag_on_the_Staff_role_alone()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var onboarded = await OnboardedScenario.CreateAsync(this.database, cancellationToken, "Grouped Staffing", activate: true);
        await using var circuit = await CircuitAsync(onboarded.OwnerId);

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), onboarded.Roles.Staff));
            WaitForDialog(rendered);
            Assert.True(FlagOf(rendered));
            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Permissions", StringComparison.Ordinal)).Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-permission-matrix fieldset")), Wait);

            var groups = rendered.FindAll(".state-permission-matrix fieldset").ToList();
            var expected = TenantEditions.Ceiling(TenantEdition.Staffing).GroupBy(AreaOf).ToList();
            Assert.DoesNotContain(rendered.FindAll("[data-permission]"), row => RolePermissions.IsPortal(Enum.Parse<RolePermission>(row.GetAttribute("data-permission")!)));
            Assert.Equal(expected.Count, groups.Count);
            foreach (var (group, area) in groups.Zip(expected))
            {
                Assert.Equal(area.Select(permission => permission.ToString()), group.QuerySelectorAll("[data-permission]").Select(row => row.GetAttribute("data-permission")));
                Assert.Contains(Staffora.Blazor.Helper.EnumDisplayHelper.GetDisplayName(area.Key), group.QuerySelector("legend")!.TextContent, StringComparison.Ordinal);
            }

            Assert.Equal(
                [PermissionArea.Users, PermissionArea.Tenant, PermissionArea.Roles, PermissionArea.OrganizationUnits, PermissionArea.Settings, PermissionArea.Personal, PermissionArea.Resources, PermissionArea.Scheduling, PermissionArea.Customers, PermissionArea.Jobs, PermissionArea.Coordination],
                expected.Select(area => area.Key));
            Assert.Contains("Staff", groups[6].QuerySelector("legend")!.TextContent, StringComparison.Ordinal);
            Assert.Contains("Schedules and absence", groups[7].QuerySelector("legend")!.TextContent, StringComparison.Ordinal);
            Assert.Equal(RolePresets.Staff.Permissions.Select(permission => permission.ToString()), Ticked(rendered));
        }

        foreach (var (roleId, preset) in new[] { (onboarded.Roles.Coordinator, RolePresets.Coordinator), (onboarded.Roles.Administrator, RolePresets.Administrator), (onboarded.Roles.SystemOwner, RolePresets.SystemOwner) })
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), roleId));
            WaitForDialog(rendered);
            Assert.False(FlagOf(rendered));
            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Permissions", StringComparison.Ordinal)).Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-permission-matrix fieldset")), Wait);
            Assert.Equal(preset.Permissions.Select(permission => permission.ToString()).Order(), Ticked(rendered).Order());
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_dialog_offers_its_Staff_view_for_a_staff_member_and_a_reader_of_staff_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, "Viewing Staffing");
        var kim = await scenario.AddPersonAsync(this.database, "Kim", cancellationToken);
        var plain = await scenario.AddPersonAsync(this.database, "Plain", cancellationToken);
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(kim, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        await using (var circuit = await CircuitAsync(scenario.Onboarded.OwnerId))
        {
            using (var page = circuit.Page())
            {
                var rendered = HostDialogs(page);
                _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
                WaitForDialog(rendered);
                rendered.Find("[role=tab][data-view=staff]").Click();
                rendered.WaitForAssertion(() => Assert.Contains("Team North", rendered.Find(".state-entity-dialog tr.list-row").TextContent, StringComparison.Ordinal), Wait);
                Assert.Contains("Active", rendered.Find("[data-staff-status]").TextContent, StringComparison.Ordinal);
            }

            using (var page = circuit.Page())
            {
                var rendered = HostDialogs(page);
                _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), plain));
                WaitForDialog(rendered);
                Assert.Empty(rendered.FindAll("[role=tab][data-view=staff]"));
                Assert.NotEmpty(rendered.FindAll("[role=tab][data-view=roles]"));
            }
        }

        var userAdmin = await scenario.AddPersonAsync(this.database, "UserAdmin", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, userAdmin, cancellationToken, RolePermission.UsersView, RolePermission.RolesView);
        await using (var circuit = await CircuitAsync(userAdmin))
        {
            using var page = circuit.Page();
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), kim));
            WaitForDialog(rendered);
            Assert.Empty(rendered.FindAll("[role=tab][data-view=staff]"));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_Staff_page_lists_the_staff_members_opens_a_staff_members_user_dialog_and_makes_a_new_staff_member()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, "Listing Staffing");
        var kim = await scenario.AddPersonAsync(this.database, "Kim", cancellationToken);
        var newcomer = await scenario.AddPersonAsync(this.database, "Newcomer", cancellationToken);
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(kim, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        await using var circuit = await CircuitAsync(scenario.Onboarded.OwnerId);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<StaffMembers>(builder));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("tr.list-row")), Wait);
        Assert.Contains("Team North", rendered.Find("tr.list-row").TextContent, StringComparison.Ordinal);

        rendered.FindAll("button").Single(button => TextOf(button) == "New staff member").Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<AddRoleHoldingDialog>()), Wait);
        var dialog = rendered.FindComponent<AddRoleHoldingDialog>();
        Assert.Equal([scenario.Onboarded.Roles.Staff], dialog.Instance.Choices.Roles.Select(role => role.Id));
        Assert.Equal(scenario.Onboarded.Roles.Staff, dialog.Instance.RoleId);
        Assert.DoesNotContain(rendered.FindComponents<RadzenDropDown<Guid?>>(), box => box.Instance.Name == "user-role-role");
        foreach (var (name, value) in new[] { ("role-holding-person", newcomer), ("user-role-unit", scenario.TeamId) })
        {
            var dropDown = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(box => box.Instance.Name == name);
            await rendered.InvokeAsync(() => dropDown.Instance.ValueChanged.InvokeAsync(value));
        }

        rendered.WaitForAssertion(() => Assert.False(rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Make staff member").HasAttribute("disabled")), Wait);
        rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Make staff member").Click();
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll("tr.list-row").Count), Wait);
        Assert.Equal(ResourceState.Active, (await StaffScenario.RecordOfAsync(this.database, newcomer, cancellationToken)).State);

        rendered.Find($"tr.list-row[data-row='{(await StaffScenario.RecordOfAsync(this.database, kim, cancellationToken)).Id}'] button.list-row__open").Click();
        WaitForDialog(rendered);
        Assert.Equal(kim, rendered.FindComponent<UserDialog>().Instance.Context.EntityId);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_sees_the_staff_but_is_not_offered_New_staff_member_since_they_give_no_roles()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await StaffScenario.CreateAsync(this.database, cancellationToken, "Coordinated Staffing");
        var kim = await scenario.AddPersonAsync(this.database, "Kim", cancellationToken);
        var casey = await scenario.AddPersonAsync(this.database, "Casey", cancellationToken);
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(kim, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(casey, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        }

        Assert.Contains(RolePermission.ResourcesView, RolePresets.Coordinator.Permissions);
        Assert.DoesNotContain(RolePermission.RolesEdit, RolePresets.Coordinator.Permissions);
        await using var circuit = await CircuitAsync(casey);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<StaffMembers>(builder));
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll("tr.list-row")), Wait);
        Assert.Contains("Team North", rendered.Find("tr.list-row").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "New staff member");
    }

    private static PermissionArea AreaOf(RolePermission permission) => Staffora.Blazor.Helper.EnumDisplayHelper.GetPermissionArea(permission)!.Value;

    private static bool FlagOf(IRenderedComponent<IComponent> rendered) =>
        rendered.FindComponents<RadzenSwitch>().SingleOrDefault(field => field.Instance.Name == "role-staff") is { } editable
            ? editable.Instance.Value
            : rendered.Find("[name=role-staff]").GetAttribute("value") == "Yes";

    private static List<string> Ticked(IRenderedComponent<IComponent> rendered) =>
        [.. rendered.FindComponents<RadzenCheckBox<bool>>().Where(box => box.Instance.Value && box.Instance.Name?.StartsWith("role-permission-", StringComparison.Ordinal) == true).Select(box => box.Instance.Name!["role-permission-".Length..])];

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
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
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
