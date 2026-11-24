using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.OrganizationComponents;
using Staffora.Blazor.Pages.TenantAdmin;
using Staffora.Core.Entities.Identity;
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

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class UserDialogTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public UserDialogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_user_is_created_deactivated_and_reactivated_through_its_dialog_and_nobody_deactivates_themselves()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit);
        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<Users>(builder));
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll("tr.list-row")), Wait);

        rendered.FindAll("button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "New User").Click();
        var dialog = WaitForDialog(rendered);
        Assert.Equal("New user", dialog.Title());
        Assert.Equal("true", rendered.Find("[name=user-first-name]").GetAttribute("aria-required"));
        Assert.Empty(dialog.StepKeys());

        var email = $"made.{Guid.NewGuid():N}@company-a.example";
        rendered.Find("[name=user-first-name]").Change("Made");
        rendered.Find("[name=user-last-name]").Change("Here");
        rendered.Find("[name=user-email]").Change(email);
        rendered.Find("[name=user-username]").Change(email);
        rendered.WaitForAssertion(() => Assert.Equal([ApplicationUserWorkflowAttribute.CreateStep], dialog.StepKeys()), Wait);
        dialog.Step(ApplicationUserWorkflowAttribute.CreateStep).Click();

        dialog.WaitForLocked(UserState.Active, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep);
        Assert.Equal("Made Here", dialog.Title());
        Assert.Contains("Their initial password is ", rendered.Find(".state-entity-dialog-footer__notice").TextContent, StringComparison.Ordinal);
        var id = await UserIdAsync(email, cancellationToken);
        Assert.Equal(UserState.Active, (await StoredAsync(id, cancellationToken)).State);

        dialog.Step(ApplicationUserWorkflowAttribute.DeactivateStep).Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Deactivate").Click();
        dialog.WaitForState(UserState.Inactive, ApplicationUserWorkflowAttribute.ReactivateStep);
        Assert.Equal(UserState.Inactive, (await StoredAsync(id, cancellationToken)).State);
        Assert.All(["user-first-name", "user-last-name", "user-email", "user-username", "user-phone"], name => Assert.True(rendered.Find($"[name={name}]").HasAttribute("readonly"), name));

        dialog.Step(ApplicationUserWorkflowAttribute.ReactivateStep).Click();
        dialog.WaitForLocked(UserState.Active, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep);
        Assert.Equal(UserState.Active, (await StoredAsync(id, cancellationToken)).State);

        var hashBefore = (await StoredAsync(id, cancellationToken)).PasswordHash;
        dialog.Step(ApplicationUserWorkflowAttribute.NewPasswordStep).Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".form-dialog")), Wait);
        Assert.Contains("signed out everywhere", rendered.Find(".form-dialog").TextContent, StringComparison.Ordinal);
        dialog.ConfirmButton("New password").Click();
        rendered.WaitForAssertion(() => Assert.Contains("The new password is ", rendered.Find(".state-entity-dialog-footer__notice").TextContent, StringComparison.Ordinal), Wait);
        var notice = rendered.Find(".state-entity-dialog-footer__notice").TextContent;
        var start = notice.IndexOf("password is ", StringComparison.Ordinal) + "password is ".Length;
        var shown = notice[start..notice.IndexOf(' ', start)];
        var reset = await StoredAsync(id, cancellationToken);
        Assert.NotEqual(hashBefore, reset.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<ApplicationUser>().VerifyHashedPassword(reset, reset.PasswordHash!, shown));
        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);

        rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find($"tr.list-row[data-row='{scenario.A.OwnerId}'] button.list-row__open")), Wait);
        rendered.Find($"tr.list-row[data-row='{scenario.A.OwnerId}'] button.list-row__open").Click();
        dialog = WaitForDialog(rendered);
        dialog.AssertGreyed(ApplicationUserWorkflowAttribute.DeactivateStep, ApplicationUserWorkflowAttribute.NotSelf);
        dialog.AssertGreyed(ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.NotOwnPassword);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_saved_user_opens_locked_until_Edit_and_Cancel_drops_the_edits_while_Save_stores_them_each_locking_again()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Editor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, editor, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.UsersView);
        var staff = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Tess", cancellationToken);
        var stored = (await StoredAsync(staff, cancellationToken)).FirstName;

        await using (var circuit = await CircuitAsync(editor))
        {
            using var page = circuit.Page();
            var rendered = OpenUser(circuit, page, staff);
            var dialog = WaitForDialog(rendered);
            IElement FirstName() => rendered.Find("[name=user-first-name]");

            dialog.WaitForLocked(UserState.Active, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.True(FirstName().HasAttribute("readonly"));
            Assert.Equal(stored, FirstName().GetAttribute("value"));

            rendered.Edit();
            rendered.WaitForAssertion(() => Assert.False(FirstName().HasAttribute("readonly")), Wait);
            Assert.False(FirstName().HasAttribute("disabled"));
            Assert.Null(rendered.EditButton());
            Assert.Equal([ApplicationUserWorkflowAttribute.SaveStep], dialog.StepKeys());

            FirstName().Change("Changed");
            rendered.FindAll(".state-entity-dialog-footer button[data-dialog-action='cancel']").Single().Click();
            dialog.WaitForLocked(UserState.Active, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep);
            rendered.WaitForAssertion(() => Assert.Equal(stored, FirstName().GetAttribute("value")), Wait);
            Assert.True(FirstName().HasAttribute("readonly"));
            Assert.Equal(stored, (await StoredAsync(staff, cancellationToken)).FirstName);

            rendered.Edit();
            FirstName().Change("Renamed");
            rendered.WaitForAssertion(() => Assert.NotEqual("true", dialog.Step(ApplicationUserWorkflowAttribute.SaveStep).GetAttribute("aria-disabled")), Wait);
            dialog.Step(ApplicationUserWorkflowAttribute.SaveStep).Click();
            rendered.WaitForAssertion(() => Assert.Equal("Renamed", FirstName().GetAttribute("value")), Wait);
            dialog.WaitForLocked(UserState.Active, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep);
            Assert.True(FirstName().HasAttribute("readonly"));
            Assert.Equal("Renamed", (await StoredAsync(staff, cancellationToken)).FirstName);
        }

        await using (var circuit = await CircuitAsync(viewer))
        {
            using var page = circuit.Page();
            var rendered = OpenUser(circuit, page, staff);
            var dialog = WaitForDialog(rendered);
            rendered.WaitForAssertion(() => Assert.Equal(UserState.Active.ToString(), rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
            Assert.Null(rendered.EditButton());
            Assert.DoesNotContain(ApplicationUserWorkflowAttribute.SaveStep, dialog.StepKeys());
            Assert.All(["user-first-name", "user-last-name", "user-email", "user-username", "user-phone"], name => Assert.True(rendered.Find($"[name={name}]").HasAttribute("readonly"), name));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_reactivated_user_finds_a_notice_linking_to_their_overview_and_the_dialogs_history_shows_it_delivered_in_app()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);

        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
        var staff = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Tess", cancellationToken);
        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        Assert.True((await circuit.Get<UserService>().StepUserAsync(staff, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);

        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<Users>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(rendered.Find($"tr.list-row[data-row='{staff}'] button.list-row__open")), Wait);
        await rendered.InvokeAsync(() => rendered.Find($"tr.list-row[data-row='{staff}'] button.list-row__open").Click());
        var dialog = WaitForDialog(rendered);
        dialog.WaitForState(UserState.Inactive, ApplicationUserWorkflowAttribute.ReactivateStep);
        dialog.Step(ApplicationUserWorkflowAttribute.ReactivateStep).Click();
        dialog.WaitForLocked(UserState.Active, ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.DeactivateStep);

        await rendered.Find(".state-pill").TriggerEventAsync("onpointermove", new PointerEventArgs { PointerType = "mouse" });
        rendered.WaitForAssertion(() => Assert.Contains(
            rendered.FindAll("[data-history-notice]"),
            notice => notice.GetAttribute("data-history-notice") == nameof(Staffora.Core.Entities.Messaging.OutgoingMessageStatus.Delivered)
                && notice.TextContent.Contains("Notice: Account reactivated · delivered to 1 person", StringComparison.Ordinal)), Wait);

        await using var user = ServiceHost.Create(this.database, scenario.A.RuntimeOf(staff));
        var notices = await user.Get<Staffora.Services.Messaging.NoticeService>().GetNoticesAsync(Staffora.Services.Messaging.NoticeFilter.Unread, cancellationToken);
        Assert.True(notices.IsSuccess, notices.Error);
        var found = Assert.Single(notices.Value!.Notices);
        Assert.Equal(("Your account is active again", "/me/overview", false), (found.Subject, found.Link, found.IsRead));
        Assert.StartsWith("Hello Tess,", found.Text, StringComparison.Ordinal);

        using var own = new PageContext();
        own.Services.AddSingleton(user.Get<Staffora.Services.Messaging.NoticeService>());
        var bell = own.Render<Staffora.Blazor.Components.Account.NotificationBell>();
        bell.WaitForAssertion(() => Assert.Equal("1", bell.Find("[data-notice-bell]").GetAttribute("data-unread-count")), Wait);
        bell.Find(".notice-bell__trigger").Click();
        bell.WaitForAssertion(() => Assert.Single(bell.FindAll(".notice-row--unread")), Wait);
        bell.Find(".notice-row").Click();
        bell.WaitForAssertion(() => Assert.EndsWith("/me/overview", own.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal), Wait);
        Assert.Equal(0, (await user.Get<Staffora.Services.Messaging.NoticeService>().GetUnreadCountAsync(cancellationToken)).Value);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_stronger_accounts_credentials_and_lifecycle_steps_are_greyed_with_the_ceilings_reason_and_a_weaker_ones_are_offered()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit, RolePermission.SettingsEdit);
        var administrator = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Administrator", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, administrator, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        await using var circuit = await CircuitAsync(administrator);
        using var page = circuit.Page();

        var rendered = OpenUser(circuit, page, scenario.A.OwnerId);
        var dialog = WaitForDialog(rendered);
        dialog.AssertGreyed(ApplicationUserWorkflowAttribute.NewPasswordStep, ApplicationUserWorkflowAttribute.WithinCeilingReason);
        dialog.AssertGreyed(ApplicationUserWorkflowAttribute.DeactivateStep, ApplicationUserWorkflowAttribute.WithinCeilingReason);

        rendered.Edit();
        Assert.Contains(ApplicationUserWorkflowAttribute.SaveStep, dialog.StepKeys());
        rendered.Find("[name=user-email]").Change($"taken.{Guid.NewGuid():N}@company-a.example");
        rendered.WaitForAssertion(() => Assert.DoesNotContain(ApplicationUserWorkflowAttribute.SaveStep, dialog.StepKeys()), Wait);
        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());

        rendered = OpenUser(circuit, page, colleague);
        dialog = WaitForDialog(rendered);
        Assert.NotEqual("true", dialog.Step(ApplicationUserWorkflowAttribute.NewPasswordStep).GetAttribute("aria-disabled"));
        Assert.NotEqual("true", dialog.Step(ApplicationUserWorkflowAttribute.DeactivateStep).GetAttribute("aria-disabled"));
    }

    private static IRenderedComponent<IComponent> OpenUser(Circuit circuit, PageContext page, Guid userId)
    {
        var rendered = page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
        });
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), userId));
        return rendered;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_roles_view_hides_its_assignment_actions_from_a_reader_the_role_holding_service_would_refuse()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken);
        await HoldAsync(scenario.A, target, role, rootId, cancellationToken);

        var userAdmin = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "UserAdmin", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, userAdmin, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.RolesView);
        await using (var circuit = await CircuitAsync(userAdmin))
        {
            using var page = circuit.Page();
            var rendered = OpenRoles(circuit, page, target);
            rendered.WaitForAssertion(() => Assert.Contains("Coordinator", rendered.Find(".state-entity-dialog tr.list-row").TextContent), Wait);
            Assert.Empty(AddRoleButtons(rendered));

            rendered.Find(".state-entity-dialog tr.list-row button.list-row__open").Click();
            rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
            Assert.Empty(rendered.FindAll(".form-dialog-frame__footer"));

            Assert.Equal(RoleHoldingService.NoRoleHoldingPermission, (await circuit.Get<RoleHoldingService>().AssignUserToRoleAsync(target, rootId, role, scenario.A.TenantId)).Error);
        }

        var roleEditor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, roleEditor, cancellationToken, RolePermission.UsersView, RolePermission.RolesView, RolePermission.RolesEdit);
        var staff = await AddRoleAsync(scenario.A, "Staff", cancellationToken);
        await using (var circuit = await CircuitAsync(roleEditor))
        {
            using var page = circuit.Page();
            var rendered = OpenRoles(circuit, page, target);
            rendered.WaitForAssertion(() => Assert.Single(AddRoleButtons(rendered)), Wait);

            AddRoleButtons(rendered).Single().Click();
            rendered.WaitForAssertion(() => Assert.Equal(["Cancel", "Add role"], FormButtons(rendered)), Wait);
            var roles = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(dropDown => dropDown.Instance.Name == "user-role-role");
            await rendered.InvokeAsync(() => roles.Instance.ValueChanged.InvokeAsync(staff));
            rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Add role").Click();
            rendered.WaitForAssertion(() => Assert.Contains(rendered.FindAll(".state-entity-dialog tr.list-row"), row => row.TextContent.Contains("Staff", StringComparison.Ordinal)), Wait);
            Assert.Equal(2, await HoldingsOfAsync(target, cancellationToken));

            rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains("Staff", StringComparison.Ordinal)).QuerySelector("button.list-row__open")!.Click();
            rendered.WaitForAssertion(() => Assert.Equal(["Cancel", "Remove role"], FormButtons(rendered)), Wait);
            rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Remove role").Click();
            rendered.WaitForAssertion(() => Assert.DoesNotContain(rendered.FindAll(".state-entity-dialog tr.list-row"), row => row.TextContent.Contains("Staff", StringComparison.Ordinal)), Wait);
            Assert.Equal(1, await HoldingsOfAsync(target, cancellationToken));
        }

        await UserEdits.SetStateInTableAsync(this.database, target, UserState.Inactive, cancellationToken);
        await using (var circuit = await CircuitAsync(roleEditor))
        {
            using var page = circuit.Page();
            var rendered = OpenRoles(circuit, page, target);
            rendered.WaitForAssertion(() => Assert.Contains("Coordinator", rendered.Find(".state-entity-dialog tr.list-row").TextContent), Wait);
            Assert.Empty(AddRoleButtons(rendered));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_role_editor_is_offered_neither_the_giving_nor_the_removal_of_a_role_beyond_their_own_permissions()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken);
        var strong = await AddRoleAsync(scenario.A, "Strong", cancellationToken, RolePermission.UsersEdit);
        var weak = await AddRoleAsync(scenario.A, "Weak", cancellationToken, RolePermission.RolesView);
        await HoldAsync(scenario.A, target, strong, rootId, cancellationToken);
        await HoldAsync(scenario.A, target, weak, rootId, cancellationToken);
        var roleEditor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, roleEditor, cancellationToken, RolePermission.UsersView, RolePermission.RolesView, RolePermission.RolesEdit);
        await using var circuit = await CircuitAsync(roleEditor);
        using var page = circuit.Page();
        var rendered = OpenRoles(circuit, page, target);
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);

        Row(rendered, "Strong").QuerySelector("button.list-row__open")!.Click();
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog").Count), Wait);
        Assert.Empty(rendered.FindAll(".form-dialog-frame__footer"));
        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Single(rendered.FindAll(".state-entity-dialog")), Wait);

        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        rendered = OpenRoles(circuit, page, target);
        rendered.WaitForAssertion(() => Assert.Equal(2, rendered.FindAll(".state-entity-dialog tr.list-row").Count), Wait);
        Row(rendered, "Weak").QuerySelector("button.list-row__open")!.Click();
        rendered.WaitForAssertion(() => Assert.Equal(["Cancel", "Remove role"], FormButtons(rendered)), Wait);
        rendered.FindAll(".form-dialog-frame__footer button").Single(button => TextOf(button) == "Cancel").Click();
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".form-dialog-frame__footer")), Wait);

        AddRoleButtons(rendered).Single().Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<AddRoleHoldingDialog>()), Wait);
        var offered = rendered.FindComponent<AddRoleHoldingDialog>().Instance.Choices.Roles.Select(role => role.Id).ToList();
        Assert.Contains(weak, offered);
        Assert.DoesNotContain(strong, offered);
    }

    private static IElement Row(IRenderedComponent<IComponent> rendered, string role) =>
        rendered.FindAll(".state-entity-dialog tr.list-row").Single(row => row.TextContent.Contains(role, StringComparison.Ordinal));

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_units_role_holdings_offer_add_and_remove_by_the_services_check_and_its_people_need_no_UsersView()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);
        var target = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Target", cancellationToken);
        var role = await AddRoleAsync(scenario.A, "Coordinator", cancellationToken);
        await HoldAsync(scenario.A, target, role, rootId, cancellationToken);

        var userAdmin = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "UserAdmin", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, userAdmin, cancellationToken, RolePermission.UsersView, RolePermission.UsersCreate, RolePermission.UsersEdit, RolePermission.RolesView);
        await using (var circuit = await CircuitAsync(userAdmin))
        {
            using var page = circuit.Page();
            var rendered = RenderRoleAssignments(page, scenario.A.TenantId, rootId);
            rendered.WaitForAssertion(() => Assert.Contains("Coordinator", rendered.Markup, StringComparison.Ordinal), Wait);
            Assert.DoesNotContain(rendered.FindAll("button"), button => TextOf(button) == "Add person");
            Assert.Empty(rendered.FindAll("button.org-role-chip__remove"));
        }

        var roleEditor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "RoleEditor", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, roleEditor, cancellationToken, RolePermission.RolesView, RolePermission.RolesEdit);
        await using (var circuit = await CircuitAsync(roleEditor))
        {
            using var page = circuit.Page();
            var rendered = RenderRoleAssignments(page, scenario.A.TenantId, rootId);
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll("button[aria-label^='Remove the Coordinator '][aria-label*=' role from Target ']")), Wait);
            rendered.FindAll("button").Single(button => TextOf(button) == "Add person").Click();
            rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<AddRoleHoldingDialog>()), Wait);
            var adding = rendered.FindComponent<AddRoleHoldingDialog>().Instance;
            Assert.Equal(rootId, adding.UnitId);
            Assert.Contains(adding.Choices.People, person => person.Id == userAdmin);
            Assert.Contains(adding.Choices.Roles, offered => offered.Id == role);
        }
    }

    private static IRenderedComponent<IComponent> OpenRoles(Circuit circuit, PageContext page, Guid userId)
    {
        var rendered = page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
        });
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(circuit.Dialogs(page), circuit.Get<UserService>(), userId));
        WaitForDialog(rendered);
        rendered.Find("[role=tab][data-view=roles]").Click();
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog tr.list-row")), Wait);
        return rendered;
    }

    private static IRenderedComponent<IComponent> RenderRoleAssignments(PageContext page, Guid tenantId, Guid unitId) => page.Render(builder =>
    {
        builder.OpenComponent<RadzenDialog>(0);
        builder.CloseComponent();
        builder.OpenComponent<RoleAssignmentView>(1);
        builder.AddComponentParameter(2, nameof(RoleAssignmentView.TenantId), tenantId);
        builder.AddComponentParameter(3, nameof(RoleAssignmentView.OrganizationUnitId), unitId);
        builder.AddComponentParameter(4, nameof(RoleAssignmentView.UnitType), OrganizationUnitType.Company);
        builder.CloseComponent();
    });

    private static List<IElement> AddRoleButtons(IRenderedComponent<IComponent> rendered) =>
        [.. rendered.FindAll(".state-entity-dialog button").Where(button => TextOf(button) == "Add role")];

    private static string TextOf(IElement button) => (button.QuerySelector(".rz-button-text") ?? button).TextContent.Trim();

    private static List<string> FormButtons(IRenderedComponent<IComponent> rendered) =>
        [.. rendered.FindAll(".form-dialog-frame__footer button").Select(TextOf)];

    private static OpenDialog WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
        return new OpenDialog(rendered);
    }

    private async Task<Guid> AddRoleAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken, params RolePermission[] permissions)
    {
        await using var context = this.database.CreateSystemContext();
        var role = new TenantRole
        {
            TenantId = tenant.TenantId,
            Name = TwoTenantScenario.UniqueName(name),
            State = TenantRoleState.Active,
            CreatedById = tenant.OwnerId,
            Permissions = [.. permissions.Select(permission => new TenantRolePermission { TenantId = tenant.TenantId, Permission = permission, CreatedById = tenant.OwnerId })],
        };
        context.TenantRoles.Add(role);
        await context.SaveChangesAsync(cancellationToken);
        return role.Id;
    }

    private async Task HoldAsync(ScenarioTenant tenant, Guid userId, Guid roleId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole { TenantId = tenant.TenantId, UserId = userId, TenantRoleId = roleId, OrganizationUnitId = unitId, CreatedById = tenant.OwnerId });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> HoldingsOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.OrganizationUnitUserRoles.CountAsync(holding => holding.UserId == userId, cancellationToken);
    }

    private async Task<Guid> UserIdAsync(string email, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(cancellationToken);
    }

    private async Task<ApplicationUser> StoredAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Users.AsNoTracking().SingleAsync(user => user.Id == userId, cancellationToken);
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
            page.Services.AddSingleton(this.host.Get<StateHistoryService>());
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

        public void WaitForState(UserState state, params string[] steps) => this.host.WaitForAssertion(() =>
        {
            Assert.Equal(state.ToString(), this.host.Find(".state-pill__button").GetAttribute("data-state"));
            Assert.Equal("false", this.host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy"));
            Assert.Equal(steps.Order(StringComparer.Ordinal), StepKeys().Order(StringComparer.Ordinal));
        }, Wait);

        public void WaitForLocked(UserState state, params string[] steps)
        {
            WaitForState(state, steps);
            this.host.WaitForAssertion(() => Assert.NotNull(this.host.EditButton()), Wait);
            Assert.Empty(this.host.FindAll(".state-entity-dialog-footer button[data-dialog-action='cancel']"));
        }
    }

    private sealed class PageContext : WebTestContext;
}
