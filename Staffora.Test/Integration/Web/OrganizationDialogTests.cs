using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;
using static Staffora.Test.Integration.Infrastructure.RenderedClicks;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class OrganizationDialogTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public OrganizationDialogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_unit_is_created_below_its_parent_deactivated_reactivated_and_deleted_through_its_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);

        _ = rendered.InvokeAsync(() => OrganizationUnitDialog.OpenCreateAsync(circuit.Dialogs(page), circuit.Get<OrganizationService>(), rootId));
        var dialog = WaitForDialog(rendered);
        Assert.Equal("New unit", dialog.Title());
        Assert.Equal("true", rendered.Find("[name=unit-name]").GetAttribute("aria-required"));
        rendered.Find("[name=unit-name]").Change("Harbour office");
        rendered.WaitForAssertion(() => Assert.Equal([OrganizationUnitWorkflowAttribute.CreateStep], dialog.StepKeys()), Wait);
        dialog.Step(OrganizationUnitWorkflowAttribute.CreateStep).Click();

        dialog.WaitForLocked(OrganizationUnitState.Active, OrganizationUnitWorkflowAttribute.DeactivateStep, OrganizationUnitWorkflowAttribute.ArchiveStep, OrganizationUnitWorkflowAttribute.DeleteStep);
        Assert.Equal("Harbour office", dialog.Title());
        var id = await UnitIdAsync(scenario.A.TenantId, "Harbour office", cancellationToken);
        Assert.Equal((OrganizationUnitState.Active, (Guid?)rootId), await StoredAsync(id, cancellationToken));

        dialog.Step(OrganizationUnitWorkflowAttribute.DeactivateStep).Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Deactivate").Click();
        dialog.WaitForState(OrganizationUnitState.Inactive, OrganizationUnitWorkflowAttribute.ReactivateStep, OrganizationUnitWorkflowAttribute.ArchiveInactiveStep, OrganizationUnitWorkflowAttribute.DeleteInactiveStep);
        Assert.True(rendered.Find("[name=unit-name]").HasAttribute("readonly"));

        dialog.Step(OrganizationUnitWorkflowAttribute.ReactivateStep).Click();
        dialog.WaitForLocked(OrganizationUnitState.Active, OrganizationUnitWorkflowAttribute.DeactivateStep, OrganizationUnitWorkflowAttribute.ArchiveStep, OrganizationUnitWorkflowAttribute.DeleteStep);
        dialog.Step(OrganizationUnitWorkflowAttribute.DeleteStep).Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Delete").Click();
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.OrganizationUnits.AnyAsync(unit => unit.Id == id, cancellationToken));
        Assert.Equal(
            [OrganizationUnitWorkflowAttribute.CreateStep, OrganizationUnitWorkflowAttribute.DeactivateStep, OrganizationUnitWorkflowAttribute.ReactivateStep, OrganizationUnitWorkflowAttribute.DeleteStep],
            await check.StateHistoryEntries.Where(entry => entry.EntityId == id).OrderBy(entry => entry.Created).Select(entry => entry.TransitionKey).ToListAsync(cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_roots_and_a_parents_steps_out_of_use_are_greyed_with_their_reasons_and_the_units_below_show_in_their_view()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        Guid region;
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            region = await host.Get<OrganizationService>().CreateUnitIdAsync("North", OrganizationUnitType.Region, rootId, cancellationToken);
            await host.Get<OrganizationService>().CreateUnitIdAsync("Harbour", OrganizationUnitType.Branch, region, cancellationToken);
        }

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => OrganizationUnitDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<OrganizationService>(), rootId));
            var root = WaitForDialog(rendered);
            root.AssertGreyed(OrganizationUnitWorkflowAttribute.DeactivateStep, OrganizationStructurePolicy.RootStaysActive);
            root.AssertGreyed(OrganizationUnitWorkflowAttribute.ArchiveStep, OrganizationUnitWorkflowAttribute.RootIsNeverArchived);
            root.AssertGreyed(OrganizationUnitWorkflowAttribute.DeleteStep, OrganizationStructurePolicy.RootIsNeverDeleted);
        }

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => OrganizationUnitDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<OrganizationService>(), region));
            var parent = WaitForDialog(rendered);
            parent.AssertGreyed(OrganizationUnitWorkflowAttribute.DeleteStep, OrganizationUnitWorkflowAttribute.NoUnitsBelowReason);
            parent.AssertGreyed(OrganizationUnitWorkflowAttribute.ArchiveStep, OrganizationUnitWorkflowAttribute.EveryUnitBelowArchivedReason);

            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Units below", StringComparison.Ordinal)).Click();
            rendered.WaitForAssertion(() => Assert.Contains("Harbour", rendered.Find(".state-entity-dialog tr.list-row").TextContent), Wait);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_is_created_with_its_permissions_and_its_delete_is_greyed_with_the_reason_while_somebody_holds_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        Guid id;
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenCreateAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>()));
            var dialog = WaitForDialog(rendered);
            Assert.Equal("New role", dialog.Title());
            rendered.Find("[name=role-name]").Change("Coordinator");

            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Permissions", StringComparison.Ordinal)).Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<RadzenCheckBox<bool>>()), Wait);
            var boxes = rendered.FindComponents<RadzenCheckBox<bool>>();
            Assert.True(boxes.Single(box => box.Instance.Name == "role-permission-TenantView").Instance.Disabled);
            var usersView = boxes.Single(box => box.Instance.Name == "role-permission-UsersView");
            Assert.False(usersView.Instance.Disabled);
            await rendered.InvokeAsync(() => usersView.Instance.Change.InvokeAsync(true));

            rendered.WaitForAssertion(() => Assert.Equal([TenantRoleWorkflowAttribute.CreateStep], dialog.StepKeys()), Wait);
            dialog.Step(TenantRoleWorkflowAttribute.CreateStep).Click();

            dialog.WaitForLocked(TenantRoleState.Active, TenantRoleWorkflowAttribute.DeleteStep);
            id = await RoleIdAsync(scenario.A.TenantId, "Coordinator", cancellationToken);
            Assert.Equal([RolePermission.UsersView], await PermissionsOfAsync(id, cancellationToken));
        }

        Assert.True((await circuit.Get<RoleHoldingService>().AssignUserToRoleAsync(colleague, rootId, id, scenario.A.TenantId)).IsSuccess);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), id));
            var dialog = WaitForDialog(rendered);
            dialog.AssertGreyed(TenantRoleWorkflowAttribute.DeleteStep, RoleHoldersBlockRoleDelete.Reason(1));
            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Holders", StringComparison.Ordinal)).Click();
            rendered.WaitForAssertion(() => Assert.Contains("Colleague", rendered.Find(".state-entity-dialog tr.list-row").TextContent), Wait);
        }

        var holding = await HoldingIdAsync(id, cancellationToken);
        Assert.True((await circuit.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), id));
            var dialog = WaitForDialog(rendered);
            dialog.Step(TenantRoleWorkflowAttribute.DeleteStep).Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
            dialog.ConfirmButton("Delete").Click();
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.TenantRoles.AnyAsync(role => role.Id == id, cancellationToken));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_that_grants_more_than_the_reader_holds_shows_read_only_with_the_reason()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.RolesView, RolePermission.RolesEdit, RolePermission.RolesDelete, RolePermission.UsersView);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsEdit);
        Guid strong;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var created = await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Strong", RolePermission.UsersView, RolePermission.SettingsEdit), cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            strong = created.Value;
        }

        await using var circuit = await CircuitAsync(editor);
        using var page = circuit.Page();
        var rendered = HostDialogs(page);
        _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), strong));
        var dialog = WaitForDialog(rendered);

        Assert.Contains(TenantRoleService.RoleBeyondCeilingReadOnly, rendered.Find("[data-role-beyond-ceiling]").TextContent, StringComparison.Ordinal);
        Assert.True(rendered.Find("[name=role-name]").HasAttribute("readonly"));
        Assert.DoesNotContain(TenantRoleWorkflowAttribute.SaveStep, dialog.StepKeys());
        dialog.AssertGreyed(TenantRoleWorkflowAttribute.DeleteStep, TenantRoleWorkflowAttribute.StoredWithinCeilingReason);
        rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Permissions", StringComparison.Ordinal)).Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<RadzenCheckBox<bool>>()), Wait);
        Assert.All(rendered.FindComponents<RadzenCheckBox<bool>>(), box => Assert.True(box.Instance.Disabled));
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reader_who_may_view_and_delete_but_not_edit_deletes_a_leaf_unit_and_an_unheld_role_from_their_dialogs_and_saves_no_edit()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var deleter = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Deleter", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, deleter, cancellationToken, RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsDelete, RolePermission.RolesView, RolePermission.RolesDelete);
        Guid leaf, kept, role;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            leaf = await owner.Get<OrganizationService>().CreateUnitIdAsync("Leaf office", OrganizationUnitType.Branch, rootId, cancellationToken);
            kept = await owner.Get<OrganizationService>().CreateUnitIdAsync("Kept office", OrganizationUnitType.Branch, rootId, cancellationToken);
            var created = await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Unheld", RolePermission.RolesView), cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            role = created.Value;
        }

        await using var circuit = await CircuitAsync(deleter);

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => OrganizationUnitDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<OrganizationService>(), leaf));
            var dialog = WaitForDialog(rendered);
            Assert.True(rendered.Find("[name=unit-name]").HasAttribute("readonly"));
            dialog.Step(OrganizationUnitWorkflowAttribute.DeleteStep).Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
            dialog.ConfirmButton("Delete").Click();
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        }

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), role));
            var dialog = WaitForDialog(rendered);
            dialog.Step(TenantRoleWorkflowAttribute.DeleteStep).Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
            dialog.ConfirmButton("Delete").Click();
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        }

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.False(await check.OrganizationUnits.AnyAsync(unit => unit.Id == leaf, cancellationToken));
            Assert.False(await check.TenantRoles.AnyAsync(row => row.Id == role, cancellationToken));
        }

        var units = circuit.Get<OrganizationService>();
        var unitUpdater = (await units.LoadOrganizationUnitUpdaterAsync(kept, cancellationToken)).Value!;
        unitUpdater.Entity.Name = "Renamed by a deleter";
        Assert.Equal("You do not have permission to edit organization units.", (await units.UpdateOrganizationUnitAndTransitionAsync(unitUpdater, OrganizationUnitWorkflowAttribute.SaveStep, null, cancellationToken)).Error);
        Guid other;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            other = (await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Other", RolePermission.RolesView), cancellationToken)).Value;
        }

        var roles = circuit.Get<TenantRoleService>();
        var roleUpdater = (await roles.LoadTenantRoleUpdaterAsync(other, cancellationToken)).Value!;
        roleUpdater.Entity.Name = "Renamed by a deleter";
        Assert.Equal("You do not have permission to edit roles.", (await roles.UpdateTenantRoleAndTransitionAsync(roleUpdater, TenantRoleWorkflowAttribute.SaveStep, null, cancellationToken)).Error);
        await using (var check = this.database.CreateSystemContext())
        {
            Assert.Equal("Kept office", await check.OrganizationUnits.Where(unit => unit.Id == kept).Select(unit => unit.Name).SingleAsync(cancellationToken));
            Assert.Equal("Other", await check.TenantRoles.Where(row => row.Id == other).Select(row => row.Name).SingleAsync(cancellationToken));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_holding_added_from_a_units_or_a_roles_dialog_greys_its_Delete_with_the_reason_at_once_and_its_removal_lifts_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, rootId) = await ArrangeAsync(cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var second = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Second", cancellationToken);
        Guid office, role;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            office = await owner.Get<OrganizationService>().CreateUnitIdAsync("Staffed office", OrganizationUnitType.Branch, rootId, cancellationToken);
            role = (await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Planner", RolePermission.UsersView), cancellationToken)).Value;
        }

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => OrganizationUnitDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<OrganizationService>(), office));
            var dialog = WaitForDialog(rendered);
            Assert.Null(dialog.Step(OrganizationUnitWorkflowAttribute.DeleteStep).GetAttribute("aria-disabled"));
            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Role holdings", StringComparison.Ordinal)).Click();
            await AddHoldingAsync(rendered, "Add role holding", ("role-holding-person", colleague), ("user-role-role", role));
            rendered.WaitForAssertion(() => Assert.Contains("Colleague", rendered.Find(".state-entity-dialog tr.list-row").TextContent), Wait);
            rendered.WaitForAssertion(() => dialog.AssertGreyed(OrganizationUnitWorkflowAttribute.DeleteStep, RoleHoldingsBlockUnitDelete.Reason), Wait);
        }

        using (var page = circuit.Page())
        {
            var rendered = HostDialogs(page);
            _ = rendered.InvokeAsync(() => TenantRoleDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<TenantRoleService>(), role));
            var dialog = WaitForDialog(rendered);
            dialog.AssertGreyed(TenantRoleWorkflowAttribute.DeleteStep, RoleHoldersBlockRoleDelete.Reason(1));
            rendered.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("Holders", StringComparison.Ordinal)).Click();
            await AddHoldingAsync(rendered, "Add holder", ("role-holding-person", second));
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
            rendered.WaitForAssertion(() => dialog.AssertGreyed(TenantRoleWorkflowAttribute.DeleteStep, RoleHoldersBlockRoleDelete.Reason(2)), Wait);

            foreach (var name in new[] { "Colleague", "Second" })
            {
                await rendered.InvokeAsync(() => rendered.FindAll(".state-entity-dialog tr.list-row").First(row => row.TextContent.Contains(name, StringComparison.Ordinal)).QuerySelector("button.list-row__open")!.Click());
                rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
                rendered.WaitForAssertion(() => Assert.Contains(name, rendered.FindAll(".state-entity-dialog-header__title").Last().TextContent, StringComparison.Ordinal), Wait);
                await rendered.InvokeAsync(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("[role=tab]").Single(tab => tab.TextContent.Contains("Roles", StringComparison.Ordinal)).Click());
                rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("tr.list-row")), Wait);
                await rendered.InvokeAsync(() => rendered.FindAll(".state-entity-dialog").Last().QuerySelectorAll("tr.list-row").First().QuerySelector("button.list-row__open")!.Click());
                rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog-frame__footer")), Wait);
                await rendered.InvokeAsync(() => rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Remove role").Click());
                rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".form-dialog-frame__footer")), Wait);
                await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
                rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog")), Wait);
                rendered.WaitForAssertion(() => Assert.DoesNotContain(rendered.FindAll(".state-entity-dialog tr.list-row"), row => row.TextContent.Contains(name, StringComparison.Ordinal)), Wait);
            }

            rendered.WaitForAssertion(() => Assert.Null(dialog.Step(TenantRoleWorkflowAttribute.DeleteStep).GetAttribute("aria-disabled")), Wait);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_card_has_no_delete_of_its_own_and_opens_the_dialog_whose_footer_deletes()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, _) = await ArrangeAsync(cancellationToken);
        Guid role;
        await using (var owner = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            role = (await owner.Get<TenantRoleService>().CreateAsync(RoleSteps.Draft("Carded", RolePermission.UsersView), cancellationToken)).Value;
        }

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        var rendered = page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<Staffora.Blazor.Pages.TenantAdmin.Roles>(1);
            builder.CloseComponent();
        });
        rendered.WaitForAssertion(() => Assert.Contains(rendered.FindAll("tr.list-row .list-row__open"), open => open.TextContent.Contains("Carded", StringComparison.Ordinal)), Wait);

        Assert.Empty(rendered.FindAll("tr.list-row button[aria-label^='Delete'], tr.list-row .delete-btn"));
        ClickOnce(rendered, () => rendered.FindAll("tr.list-row .list-row__open").Single(open => open.TextContent.Contains("Carded", StringComparison.Ordinal)));
        var dialog = WaitForDialog(rendered);
        Assert.Contains(TenantRoleWorkflowAttribute.DeleteStep, dialog.StepKeys());
        Assert.Equal(role, rendered.FindComponent<TenantRoleDialog>().Instance.Context.EntityId);
    }

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static async Task AddHoldingAsync(IRenderedComponent<IComponent> rendered, string label, params (string Name, Guid Value)[] choices)
    {
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-child-list__actions button")), Wait);
        rendered.FindAll(".state-entity-child-list__actions button").Single(button => TextOf(button) == label).Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<AddRoleHoldingDialog>()), Wait);
        foreach (var (name, value) in choices)
        {
            var dropDown = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(box => box.Instance.Name == name);
            await rendered.InvokeAsync(() => dropDown.Instance.ValueChanged.InvokeAsync(value));
        }

        rendered.WaitForAssertion(() => Assert.False(rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Add role").HasAttribute("disabled")), Wait);
        rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Add role").Click();
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindComponents<AddRoleHoldingDialog>()), Wait);
    }

    private async Task<(TwoTenantScenario Scenario, Guid RootId)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken,
            RolePermission.OrganizationUnitsView, RolePermission.OrganizationUnitsCreate, RolePermission.OrganizationUnitsEdit, RolePermission.OrganizationUnitsDelete,
            RolePermission.RolesView, RolePermission.RolesCreate, RolePermission.RolesEdit, RolePermission.RolesDelete, RolePermission.UsersView);
        return (scenario, await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken));
    }

    private static IRenderedComponent<IComponent> HostDialogs(PageContext page) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
    });

    private static OpenDialog WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        return new OpenDialog(rendered);
    }

    private async Task<Guid> UnitIdAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnits.Where(unit => unit.TenantId == tenantId && unit.Name == name).Select(unit => unit.Id).SingleAsync(cancellationToken);
    }

    private async Task<(OrganizationUnitState, Guid?)> StoredAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        var unit = await context.OrganizationUnits.AsNoTracking().SingleAsync(row => row.Id == unitId, cancellationToken);
        return (unit.State, unit.ParentId);
    }

    private async Task<Guid> RoleIdAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.TenantRoles.Where(role => role.TenantId == tenantId && role.Name == name).Select(role => role.Id).SingleAsync(cancellationToken);
    }

    private async Task<List<RolePermission>> PermissionsOfAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.TenantRolePermissions.Where(permission => permission.TenantRoleId == roleId).Select(permission => permission.Permission).ToListAsync(cancellationToken);
    }

    private async Task<Guid> HoldingIdAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnitUserRoles.Where(holding => holding.TenantRoleId == roleId).Select(holding => holding.Id).SingleAsync(cancellationToken);
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
            page.Services.AddSingleton(this.host.Get<Staffora.Core.Entities.Identity.IUserAccountGuard>());
            page.Services.AddSingleton(this.host.Get<IOrganizationUnitGuard>());
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

    private sealed class OpenDialog
    {
        private readonly IRenderedComponent<IComponent> host;

        public OpenDialog(IRenderedComponent<IComponent> host)
        {
            this.host = host;
        }

        public string Title() => this.host.Find(".state-entity-dialog-header__title").TextContent;

        public List<string> StepKeys() => [.. this.host.FindAll(".state-entity-dialog button[data-transition]").Select(button => button.GetAttribute("data-transition")!)];

        public IElement Step(string key) => this.host.Find($".state-entity-dialog button[data-transition='{key}']");

        public IElement ConfirmButton(string text) => this.host.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

        public void AssertGreyed(string key, string reason)
        {
            var step = Step(key);
            Assert.Equal("true", step.GetAttribute("aria-disabled"));
            Assert.Contains(reason, this.host.Find($"#{step.GetAttribute("aria-describedby")}").TextContent, StringComparison.Ordinal);
        }

        public void WaitForState(Enum state, params string[] steps) => this.host.WaitForAssertion(() =>
        {
            Assert.Equal(state.ToString(), this.host.Find(".state-pill__button").GetAttribute("data-state"));
            Assert.Equal("false", this.host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy"));
            Assert.Equal(steps.Order(StringComparer.Ordinal), StepKeys().Order(StringComparer.Ordinal));
        }, Wait);

        public void WaitForLocked(Enum state, params string[] steps)
        {
            WaitForState(state, steps);
            this.host.WaitForAssertion(() => Assert.NotNull(this.host.EditButton()), Wait);
            Assert.Empty(this.host.FindAll(".state-entity-dialog-footer button[data-dialog-action='cancel']"));
        }
    }

    private sealed class PageContext : WebTestContext;
}
