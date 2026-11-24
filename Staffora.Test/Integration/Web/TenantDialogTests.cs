using Staffora.Core.Models;
using Staffora.Services.Platform;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Pages.SuperAdmin;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow;
using Staffora.Services.Authorization;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class TenantDialogTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public TenantDialogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_tenant_is_onboarded_then_saved_and_moved_through_every_state_through_its_dialog()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var console = circuit.RenderConsole();
        console.WaitForAssertion(() => Assert.True(console.FindAll("tr.list-row").Count > 0 || console.Markup.Contains("No tenants have been onboarded yet.", StringComparison.Ordinal)), Wait);

        var name = $"Brightpath Staffing {Guid.NewGuid():N}"[..32];
        var ownerEmail = $"owner.{Guid.NewGuid():N}@brightpath-staffing.example";
        console.FindAll("button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "New Tenant").Click();
        Onboard(console, name, "Robin", "Hale", ownerEmail);

        console.WaitForAssertion(() => Assert.Matches("^\\S{16}$", console.Find(".tenant-onboarding__password code").TextContent), Wait);
        Assert.Contains(ownerEmail, console.Find(".tenant-onboarding__done").TextContent, StringComparison.Ordinal);

        TickCopied(console);
        DoneButton(console).Click();
        var dialog = circuit.WaitForDialog(console);
        Assert.Empty(console.FindAll(".form-dialog"));
        dialog.WaitForState(TenantState.Setup, "tenant-activate");
        Assert.Equal(name, dialog.Title());
        Assert.Empty(console.FindAll(".tenant-onboarding__password"));

        var id = await TenantIdAsync(name, cancellationToken);
        Assert.Equal(TenantState.Setup, (await TenantAsync(id, cancellationToken)).State);
        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: read the new tenant's own rows");
            Assert.True(await system.Users.AnyAsync(user => user.Id == SystemAccount.IdFor(id) && user.IsSystemAccount && user.TenantId == id, cancellationToken));
            var root = await system.OrganizationUnits.SingleAsync(unit => unit.TenantId == id, cancellationToken);
            Assert.Equal((Staffora.Core.Entities.Organization.OrganizationUnitType.Company, (Guid?)null, name, SystemAccount.IdFor(id)), (root.UnitType, root.ParentId, root.Name, root.CreatedById));
        }

        console.Edit();
        console.Find("[name=tenant-description]").Change("Places staff members in the north");
        dialog.WaitForEdit();
        dialog.Step("tenant-save-setup").Click();
        await WaitForStoredAsync(id, tenant => tenant.Description == "Places staff members in the north", cancellationToken);
        dialog.WaitForState(TenantState.Setup, "tenant-activate");

        await MoveAsync(dialog, id, "tenant-activate", confirm: null, TenantState.Active, cancellationToken, "tenant-suspend", "tenant-schedule-deletion");

        console.Edit();
        console.Find("[name=tenant-name]").Change(name + " AB");
        dialog.WaitForEdit();
        dialog.Step("tenant-save").Click();
        await WaitForStoredAsync(id, tenant => tenant.Name == name + " AB", cancellationToken);
        dialog.WaitForState(TenantState.Active, "tenant-suspend", "tenant-schedule-deletion");
        Assert.Equal(name + " AB", dialog.Title());

        await MoveAsync(dialog, id, "tenant-suspend", confirm: "Suspend", TenantState.Suspended, cancellationToken, "tenant-schedule-deletion-suspended", "tenant-reactivate");

        Assert.True(console.Find("[name=tenant-name]").HasAttribute("readonly"));
        Assert.True(console.Find("[name=tenant-description]").HasAttribute("readonly"));

        await MoveAsync(dialog, id, "tenant-reactivate", confirm: null, TenantState.Active, cancellationToken, "tenant-suspend", "tenant-schedule-deletion");
        await MoveAsync(dialog, id, "tenant-schedule-deletion", confirm: "Schedule deletion", TenantState.PendingDeletion, cancellationToken, "tenant-cancel-deletion", "tenant-archive");
        await MoveAsync(dialog, id, "tenant-cancel-deletion", confirm: null, TenantState.Suspended, cancellationToken, "tenant-schedule-deletion-suspended", "tenant-reactivate");
        await MoveAsync(dialog, id, "tenant-schedule-deletion-suspended", confirm: "Schedule deletion", TenantState.PendingDeletion, cancellationToken, "tenant-cancel-deletion", "tenant-archive");
        await MoveAsync(dialog, id, "tenant-archive", confirm: "Archive", TenantState.Archived, cancellationToken);
        Assert.True(console.Find("[name=tenant-name]").HasAttribute("readonly"));

        var history = await HistoryOfAsync(id, cancellationToken);
        Assert.Equal(
            [
                ("tenant-save-setup", TenantState.Setup, TenantState.Setup),
                ("tenant-save-setup", TenantState.Setup, TenantState.Setup),
                ("tenant-activate", TenantState.Setup, TenantState.Active),
                ("tenant-save", TenantState.Active, TenantState.Active),
                ("tenant-suspend", TenantState.Active, TenantState.Suspended),
                ("tenant-reactivate", TenantState.Suspended, TenantState.Active),
                ("tenant-schedule-deletion", TenantState.Active, TenantState.PendingDeletion),
                ("tenant-cancel-deletion", TenantState.PendingDeletion, TenantState.Suspended),
                ("tenant-schedule-deletion-suspended", TenantState.Suspended, TenantState.PendingDeletion),
                ("tenant-archive", TenantState.PendingDeletion, TenantState.Archived),
            ],
            history.Select(entry => (entry.TransitionKey, (TenantState)entry.FromState, (TenantState)entry.ToState)));
        Assert.All(history, entry => Assert.Equal((platform.OwnerId, SystemAccount.IdFor(id), id), (entry.ActorId, entry.CreatedById, entry.TenantId)));

        Assert.Equal(TenantUserStepKeys().Order(StringComparer.Ordinal), history.Select(entry => entry.TransitionKey).Distinct().Order(StringComparer.Ordinal));

        console.Find(".state-pill__button").Click();
        console.WaitForAssertion(() => Assert.Equal(3, console.FindAll(".state-pill__card [data-history-step]").Count), Wait);
        Assert.Contains("Archive", console.Find(".state-pill__card [data-history-step]").TextContent, StringComparison.Ordinal);

        await dialog.CloseByEscAsync();
        Assert.True(dialog.Result!.Changed);
        console.Find("input[name='tenants-search']").Change(name);
        console.WaitForAssertion(() => Assert.Equal("Archived", StateCell(console, id)), Wait);
        var link = console.Find($"tr.list-row[data-row='{id}'] a.list-row__open");
        Assert.Equal((name + " AB", $"/superadmin/tenants/{id}"), (link.TextContent.Trim(), link.GetAttribute("href")));
    }

    [Fact]
    public async Task A_refused_first_save_creates_nothing_and_the_dialog_keeps_its_edits()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var tenants = circuit.Get<TenantService>();

        var before = await TenantCountAsync(cancellationToken);
        var refused = await tenants.CreateTenantAsync(new Tenant { Name = "   ", Description = "No name" }, "tenant-save-setup", cancellationToken);
        Assert.False(refused.IsSuccess);
        Assert.Contains("Name", refused.Error, StringComparison.Ordinal);
        Assert.Equal(before, await TenantCountAsync(cancellationToken));

        var activated = await tenants.CreateTenantAsync(new Tenant { Name = "Straight to active" }, "tenant-suspend", cancellationToken);
        Assert.False(activated.IsSuccess);
        Assert.Equal(before, await TenantCountAsync(cancellationToken));
    }

    [Fact]
    public async Task The_tenant_workflow_designates_no_save_step_so_closing_with_edits_asks_and_never_saves()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var workflow = WorkflowRegistry.Default.Get<TenantState, Tenant>();
        Assert.All(Enum.GetValues<TenantState>(), state => Assert.Null(((IWorkflow)workflow).GetSaveStep(state)));

        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var console = circuit.RenderTenantConsole(scenario.A.TenantId);
        console.WaitForAssertion(() => Assert.Equal(scenario.A.Name, console.Find("h1").TextContent.Trim()), Wait);
        TenantDetailsButton(console).Click();
        var dialog = circuit.WaitForDialog(console);
        Assert.Equal(scenario.A.Name, dialog.Title());

        console.Edit();
        console.Find("[name=tenant-description]").Change("Not saved by leaving");
        dialog.WaitForEdit();
        var leaving = console.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        console.WaitForAssertion(() => Assert.Equal("You have changes that are not saved. Close the dialog and lose them?", console.Find(".form-dialog .dialog-notice__message").TextContent.Trim()), Wait);
        Assert.Equal("Discard changes?", console.Find(".form-dialog .form-dialog-header__title").TextContent);

        dialog.ConfirmButton("Keep editing").Click();
        Assert.False(await leaving.WaitAsync(Wait, cancellationToken));
        console.WaitForAssertion(() => Assert.Empty(console.FindAll(".form-dialog")), Wait);
        Assert.Equal(scenario.A.Name, dialog.Title());
        Assert.Equal("Not saved by leaving", console.Find("[name=tenant-description]").GetAttribute("value"));
        Assert.Null((await TenantAsync(scenario.A.TenantId, cancellationToken)).Description);

        _ = console.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        console.WaitForAssertion(() => Assert.Single(console.FindAll(".form-dialog")), Wait);
        dialog.ConfirmButton("Discard changes").Click();
        console.WaitForAssertion(() => Assert.Empty(console.FindAll(".state-entity-dialog")), Wait);
        Assert.Null((await TenantAsync(scenario.A.TenantId, cancellationToken)).Description);
        Assert.Empty(await HistoryOfAsync(scenario.A.TenantId, cancellationToken));
    }

    [Fact]
    public async Task Only_the_platform_console_opens_a_tenants_dialog_and_every_tenant_method_refuses_anyone_else()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, [.. RolePermissions.Grantable]);
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);

        foreach (var runtime in new IRuntimeExecutionContext[] { scenario.A.Runtime(), impersonating })
        {
            await using var circuit = new ConsoleCircuit(this.database, runtime);
            var tenants = circuit.Get<TenantService>();

            var page = circuit.RenderHost();
            Task<StateEntityDialogResult>? opened = null;
            await page.InvokeAsync(() => { opened = TenantDialog.OpenAsync(circuit.Dialogs, tenants, scenario.A.TenantId); });
            page.WaitForAssertion(() => Assert.Contains(TenantService.ConsoleOnly, page.Find(".state-entity-dialog__could-not-open [role=alert]").TextContent, StringComparison.Ordinal), Wait);
            Assert.Empty(page.FindAll("[name=tenant-name]"));
            await page.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
            await opened!.WaitAsync(Wait, cancellationToken);

            var history = await tenants.GetTenantHistoryAsync(scenario.A.TenantId, StateHistoryQuery.Newest(3), cancellationToken);
            var created = await tenants.CreateTenantAsync(new Tenant { Name = "Refused" }, "tenant-save-setup", cancellationToken);
            var onboarded = await circuit.Get<TenantOnboardingService>().OnboardTenantAsync(new TenantOnboarding("Refused", null, "Robin", "Hale", "robin.hale@refused.example"), cancellationToken);
            Assert.Equal(TenantService.ConsoleOnly, history.Error);
            Assert.Equal(TenantService.ConsoleOnlyCreates, created.Error);
            Assert.Equal(TenantOnboardingService.ConsoleOnly, onboarded.Error);
        }

        await using var owner = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var updater = (await owner.Get<TenantService>().LoadTenantUpdaterAsync(scenario.A.TenantId, cancellationToken)).Value!;
        updater.Entity.Description = "Written from inside";
        await using (var inside = new ConsoleCircuit(this.database, scenario.A.Runtime()))
        {
            Assert.Equal(TenantService.ConsoleOnly, (await inside.Get<TenantService>().UpdateTenantAndTransitionAsync(updater, "tenant-save", null, cancellationToken)).Error);
        }

        Assert.Null((await TenantAsync(scenario.A.TenantId, cancellationToken)).Description);
        Assert.Equal(TenantService.TenantNotFound, (await owner.Get<TenantService>().LoadTenantUpdaterAsync(Guid.NewGuid(), cancellationToken)).Error);
    }

    [Fact]
    public async Task A_refused_onboarding_stays_in_its_form_with_the_reason_and_creates_nothing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var taken = await TwoTenantScenario.AddTenantAsync(this.database, "Taken", cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var console = circuit.RenderConsole();
        console.WaitForAssertion(() => Assert.NotEmpty(console.FindAll("tr.list-row")), Wait);
        var before = await TenantCountAsync(cancellationToken);

        console.FindAll("button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "New Tenant").Click();
        console.WaitForAssertion(() => Assert.NotEmpty(console.FindAll("[name=tenant-onboarding-name]")), Wait);
        Assert.True(CreateButton(console).HasAttribute("disabled"));

        Onboard(console, taken.Name, "Robin", "Hale", $"owner.{Guid.NewGuid():N}@example.com");
        console.WaitForAssertion(() => Assert.Contains(TenantService.NameTaken, console.Find(".form-dialog .rz-alert").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(console.FindAll(".tenant-onboarding__password"));
        Assert.Equal(taken.Name, console.Find("[name=tenant-onboarding-name]").GetAttribute("value"));
        Assert.Equal(before, await TenantCountAsync(cancellationToken));

        console.FindAll(".form-dialog button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        console.WaitForAssertion(() => Assert.Empty(console.FindAll(".form-dialog")), Wait);
        Assert.Empty(console.FindAll(".state-entity-dialog"));
        Assert.Equal(before, await TenantCountAsync(cancellationToken));
    }

    [Fact]
    public async Task Closing_onboarding_before_the_password_is_marked_copied_asks_first_by_Esc_the_close_button_and_Done()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var console = circuit.RenderConsole();
        console.WaitForAssertion(() => Assert.True(console.FindAll("tr.list-row").Count > 0 || console.Markup.Contains("No tenants have been onboarded yet.", StringComparison.Ordinal)), Wait);
        console.FindAll("button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "New Tenant").Click();

        console.WaitForAssertion(() => Assert.NotEmpty(console.FindAll("[name=tenant-onboarding-name]")), Wait);
        var list = console.FindComponent<Staffora.Blazor.Components.Common.List.ListView<PlatformTenantRow>>();
        var renders = list.RenderCount;
        Assert.True(await console.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null)));
        console.WaitForAssertion(() => Assert.Empty(console.FindAll(".form-dialog")), Wait);

        list.WaitForAssertion(() => Assert.True(list.RenderCount > renders), Wait);

        var name = $"Copied Staffing {Guid.NewGuid():N}"[..30];
        console.FindAll("button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "New Tenant").Click();
        Onboard(console, name, "Robin", "Hale", $"owner.{Guid.NewGuid():N}@copied-staffing.example");
        console.WaitForAssertion(() => Assert.Matches("^\\S{16}$", console.Find(".tenant-onboarding__password code").TextContent), Wait);
        var password = console.Find(".tenant-onboarding__password code").TextContent;

        var byEsc = console.InvokeAsync(() => circuit.Dialogs.TryCloseFromJs(null));
        KeepItOpen(console, password);
        Assert.False(await byEsc.WaitAsync(Wait, cancellationToken));
        console.Find(".form-dialog .form-dialog-header__close").Click();
        KeepItOpen(console, password);
        DoneButton(console).Click();
        KeepItOpen(console, password);

        console.Find(".form-dialog .form-dialog-header__close").Click();
        console.WaitForAssertion(() => Assert.Equal(2, console.FindAll(".form-dialog").Count), Wait);
        ConfirmChoice(console, "Close anyway").Click();
        console.WaitForAssertion(() => Assert.Empty(console.FindAll(".form-dialog")), Wait);
        Assert.Empty(console.FindAll(".state-entity-dialog"));
        Assert.Equal(TenantState.Setup, (await TenantAsync(await TenantIdAsync(name, cancellationToken), cancellationToken)).State);
    }

    [Fact]
    public async Task A_tenant_moved_from_its_console_shows_its_new_state_there_once_closed()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Aardvark Staffing", cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var console = circuit.RenderTenantConsole(tenant.TenantId);
        console.WaitForAssertion(() => Assert.Equal("Active", console.Find("[data-console-state]").TextContent.Trim()), Wait);

        TenantDetailsButton(console).Click();
        var dialog = circuit.WaitForDialog(console);
        await MoveAsync(dialog, tenant.TenantId, "tenant-suspend", confirm: "Suspend", TenantState.Suspended, cancellationToken, "tenant-schedule-deletion-suspended", "tenant-reactivate");
        await dialog.CloseByEscAsync();

        Assert.True(dialog.Result!.Changed);
        console.WaitForAssertion(() => Assert.Equal("Suspended", console.Find("[data-console-state]").TextContent.Trim()), Wait);
    }

    [Fact]
    public async Task The_list_names_every_state_of_a_tenant()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var byState = new Dictionary<TenantState, Guid>();
        foreach (var state in Enum.GetValues<TenantState>())
        {
            byState[state] = (await TwoTenantScenario.AddTenantAsync(this.database, $"Aardvark {state}", cancellationToken, state)).TenantId;
        }

        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var console = circuit.RenderConsole();

        console.Find("input[name='tenants-search']").Change("Aardvark");
        console.WaitForAssertion(() => Assert.All(byState.Values, id => Assert.NotEmpty(console.FindAll($"tr.list-row[data-row='{id}']"))), Wait);
        Assert.Equal(
            [("Setup", TenantState.Setup), ("Active", TenantState.Active), ("Suspended", TenantState.Suspended), ("Pending deletion", TenantState.PendingDeletion), ("Archived", TenantState.Archived)],
            byState.Select(entry => (StateCell(console, entry.Value), entry.Key)));
    }

    [Fact]
    public async Task The_platform_tenants_dialog_shows_its_fields_read_only_and_offers_no_step()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = new ConsoleCircuit(this.database, platform.PlatformOwnerRuntime());
        var page = circuit.RenderConsole();
        page.WaitForAssertion(() => Assert.True(page.FindAll("tr.list-row").Count > 0 || page.Markup.Contains("No tenants have been onboarded yet.", StringComparison.Ordinal)), Wait);
        await page.InvokeAsync(() => { _ = TenantDialog.OpenAsync(circuit.Dialogs, circuit.Get<TenantService>(), platform.TenantId); });
        var dialog = circuit.WaitForDialog(page);

        Assert.Equal(TenantState.Active.ToString(), page.Find(".state-pill__button").GetAttribute("data-state"));
        Assert.True(page.Find("[name=tenant-name]").HasAttribute("readonly"));
        Assert.True(page.Find("[name=tenant-description]").HasAttribute("readonly"));
        Assert.Null(page.Find("[name=tenant-name]").GetAttribute("aria-required"));
        Assert.Empty(dialog.StepKeys());
        await dialog.CloseByEscAsync();
    }

    private static string StateCell(IRenderedComponent<IComponent> console, Guid tenantId) =>
        console.Find($"tr.list-row[data-row='{tenantId}'] td.list-table__state .list-state").TextContent.Trim();

    private static IElement TenantDetailsButton(IRenderedComponent<IComponent> console) =>
        console.FindAll("button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "Tenant details");

    private static void Onboard(IRenderedComponent<IComponent> console, string name, string firstName, string lastName, string email)
    {
        console.WaitForAssertion(() => Assert.NotEmpty(console.FindAll("[name=tenant-onboarding-name]")), Wait);
        console.Find("[name=tenant-onboarding-name]").Change(name);
        console.Find("[name=tenant-onboarding-owner-first-name]").Change(firstName);
        console.Find("[name=tenant-onboarding-owner-last-name]").Change(lastName);
        console.Find("[name=tenant-onboarding-owner-email]").Change(email);
        console.WaitForAssertion(() => Assert.False(CreateButton(console).HasAttribute("disabled")), Wait);
        CreateButton(console).Click();
    }

    private static IElement DoneButton(IRenderedComponent<IComponent> console) =>
        console.FindAll(".form-dialog button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "Done");

    private static void TickCopied(IRenderedComponent<IComponent> console)
    {
        console.Find(".tenant-onboarding__copied .rz-chkbox-box").Click();
        console.WaitForAssertion(() => Assert.Equal("true", console.Find(".tenant-onboarding__copied [role=checkbox]").GetAttribute("aria-checked")), Wait);
    }

    private static IElement ConfirmChoice(IRenderedComponent<IComponent> console, string text) =>
        console.FindAll(".form-dialog button").Single(button => button.TextContent.Trim() == text);

    private static void KeepItOpen(IRenderedComponent<IComponent> console, string password)
    {
        console.WaitForAssertion(() => Assert.Contains("Close without the password?", console.Markup, StringComparison.Ordinal), Wait);
        ConfirmChoice(console, "Keep it open").Click();
        console.WaitForAssertion(() =>
        {
            Assert.Single(console.FindAll(".form-dialog"));
            Assert.Equal(password, console.Find(".tenant-onboarding__password code").TextContent);
        }, Wait);
    }

    private static IElement CreateButton(IRenderedComponent<IComponent> console) =>
        console.FindAll(".form-dialog button").Single(button => button.QuerySelector(".rz-button-text")?.TextContent.Trim() == "Create tenant");

    private static IEnumerable<string> TenantUserStepKeys() =>
        ((IWorkflow)WorkflowRegistry.Default.Get<TenantState, Tenant>()).Transitions.Where(transition => !transition.IsSystemDriven).Select(transition => transition.Key);

    private async Task MoveAsync(OpenDialog dialog, Guid id, string key, string? confirm, TenantState state, CancellationToken cancellationToken, params string[] stepsAfter)
    {
        dialog.Step(key).Click();
        if (confirm is not null)
        {
            dialog.Host.WaitForAssertion(() => Assert.NotEmpty(dialog.Host.FindAll(".form-dialog")), Wait);
            dialog.ConfirmButton(confirm).Click();
        }

        dialog.WaitForState(state, stepsAfter);
        await WaitForStoredAsync(id, tenant => tenant.State == state, cancellationToken);
    }

    private async Task<Guid> TenantIdAsync(string name, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: find the created tenant");
        return await system.Tenants.Where(tenant => tenant.Name == name).Select(tenant => tenant.Id).SingleAsync(cancellationToken);
    }

    private async Task<int> TenantCountAsync(CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: count the tenants");
        return await system.Tenants.CountAsync(cancellationToken);
    }

    private async Task<Tenant> TenantAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a tenant");
        return await system.Tenants.AsNoTracking().SingleAsync(tenant => tenant.Id == id, cancellationToken);
    }

    private async Task WaitForStoredAsync(Guid id, Func<Tenant, bool> stored, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + Wait;
        while (!stored(await TenantAsync(id, cancellationToken)))
        {
            Assert.True(DateTime.UtcNow < until, "The tenant never reached what the dialog showed.");
            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task<List<Staffora.Core.Entities.Workflow.StateHistoryEntry>> HistoryOfAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: read a tenant's history");
        return await system.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == id).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
    }

    private sealed class ConsoleCircuit : IAsyncDisposable
    {
        private readonly ServiceHost host;

        public ConsoleCircuit(RealDatabase database, IRuntimeExecutionContext runtime)
        {
            this.host = ServiceHost.Create(database, runtime);
            Page = new PageContext();
            Page.Services.AddLogging();
            Page.Services.AddSingleton(runtime);
            Page.Services.AddSingleton(TimeProvider.System);
            Page.Services.AddSingleton(this.host.Get<PermissionService>());
            Page.Services.AddSingleton(this.host.Get<WorkflowService>());
            Page.Services.AddSingleton(this.host.Get<StateHistoryService>());
            Page.Services.AddSingleton(this.host.Get<TenantService>());
            Page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            Page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            Page.Services.AddSingleton(this.host.Get<TenantOnboardingService>());
            Page.Services.AddSingleton(this.host.Get<OrganizationService>());
            Page.Services.AddSingleton(this.host.Get<UserService>());
            Page.Services.AddSingleton(this.host.Get<PlatformConsoleService>());
        }

        public PageContext Page { get; }

        public DialogService Dialogs => Page.Services.GetRequiredService<DialogService>();

        public T Get<T>() where T : notnull => this.host.Get<T>();

        public IRenderedComponent<IComponent> RenderConsole() => Page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(2);
            builder.CloseComponent();
            builder.OpenComponent<Tenants>(1);
            builder.CloseComponent();
        });

        public IRenderedComponent<IComponent> RenderTenantConsole(Guid tenantId) => Page.Render(builder =>
        {
            builder.OpenComponent<RadzenDialog>(0);
            builder.CloseComponent();
            builder.OpenComponent<Staffora.Test.Web.ShellOutlets>(3);
            builder.CloseComponent();
            builder.OpenComponent<TenantConsole>(1);
            builder.AddComponentParameter(2, nameof(TenantConsole.TenantId), tenantId);
            builder.CloseComponent();
        });

        public IRenderedComponent<RadzenDialog> RenderHost() => Page.Render<RadzenDialog>();

        public OpenDialog WaitForDialog(IRenderedComponent<IComponent> console)
        {
            console.WaitForAssertion(() => Assert.Equal("loaded", console.Find(".state-entity-dialog").GetAttribute("data-load-state")), Wait);
            console.WaitForAssertion(() => Assert.Equal("false", console.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), Wait);
            return new OpenDialog(console, Dialogs);
        }

        public async ValueTask DisposeAsync()
        {
            await Page.DisposeAsync();
            await this.host.DisposeAsync();
        }
    }

    private sealed class OpenDialog
    {
        private readonly IRenderedComponent<IComponent> host;
        private readonly DialogService dialogs;

        public OpenDialog(IRenderedComponent<IComponent> host, DialogService dialogs)
        {
            this.host = host;
            this.dialogs = dialogs;
        }

        public IRenderedComponent<IComponent> Host => this.host;

        public StateEntityDialogResult? Result { get; private set; }

        public string Title() => Host.Find(".state-entity-dialog-header__title").TextContent;

        public StateEntityDialogContext Context() => Host.FindComponent<StateEntityDialogLayout>().Instance.Context;

        public List<string> StepKeys() => [.. Host.FindAll(".state-entity-dialog button[data-transition]").Select(button => button.GetAttribute("data-transition")!)];

        public IElement Step(string key) => Host.Find($".state-entity-dialog button[data-transition='{key}']");

        public IElement ConfirmButton(string text) => Host.FindAll(".form-dialog button").Single(button => button.TextContent.Trim().Equals(text, StringComparison.Ordinal));

        public void WaitForEdit() => Host.WaitForAssertion(() => Assert.NotNull(Host.Find(".state-entity-dialog").QuerySelector(".state-entity-dialog-header__unsaved")), Wait);

        public void WaitForState(TenantState state, params string[] steps) => Host.WaitForAssertion(() =>
        {
            Assert.Equal(state.ToString(), Host.Find(".state-pill__button").GetAttribute("data-state"));
            Assert.Equal("false", Host.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy"));
            Assert.Equal(steps.Order(StringComparer.Ordinal), StepKeys().Order(StringComparer.Ordinal));
            Assert.Empty(Host.FindAll(".state-entity-dialog .state-entity-dialog-header__unsaved"));
        }, Wait);

        public async Task CloseByEscAsync()
        {
            var shown = Context();
            await Host.InvokeAsync(() => this.dialogs.TryCloseFromJs(null));
            Host.WaitForAssertion(() => Assert.Empty(Host.FindAll(".state-entity-dialog")), Wait);
            Result = shown.DialogResult;
        }
    }

    private sealed class PageContext : WebTestContext;
}
