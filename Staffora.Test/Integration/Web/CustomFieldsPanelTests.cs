using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Pages.SuperAdmin;
using Staffora.Blazor.Pages.TenantAdmin;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;
using Staffora.Test.Web;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public class CustomFieldsPanelTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly RealDatabase database;

    public CustomFieldsPanelTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_owner_creates_a_tenants_choice_field_as_a_draft_and_activates_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, global, _) = await ArrangeAsync(cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<PlatformSettings>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, global)), Wait);

        await NewFieldAsync(rendered, CustomFieldTarget.User);
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<UserFieldDialog>()), Wait);
        Assert.Empty(FooterSteps(rendered));
        var tenantPicker = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(dropDown => dropDown.Instance.Name == "custom-field-tenant");
        Assert.Contains((IEnumerable<CustomFieldTenant>)tenantPicker.Instance.Data!, tenant => tenant.Id == scenario.A.TenantId);
        await rendered.InvokeAsync(() => tenantPicker.Instance.ValueChanged.InvokeAsync(scenario.A.TenantId));

        Assert.Empty(rendered.FindAll("[name=custom-field-required]"));
        var name = $"Shirt size {Guid.NewGuid():N}"[..20];
        rendered.Find("[name=custom-field-name]").Change(name);
        rendered.WaitForAssertion(() => Assert.Equal([CustomFieldSteps.Create], FooterSteps(rendered).Select(step => step.Key)), Wait);
        var type = rendered.FindComponents<RadzenDropDown<AttributeValueType>>().Single(dropDown => dropDown.Instance.Name == "custom-field-type");
        await rendered.InvokeAsync(() => type.Instance.ValueChanged.InvokeAsync(AttributeValueType.Choice));

        rendered.WaitForAssertion(() => Assert.Contains("Add the options to choose from.", rendered.Markup, StringComparison.Ordinal), Wait);
        Assert.Empty(rendered.FindAll(".state-entity-dialog textarea"));
        ClickOnce(rendered, () => Button(rendered, "Add option"));
        rendered.WaitForAssertion(() => Assert.Empty(FooterSteps(rendered)), Wait);
        rendered.Find("[name=custom-field-option-1]").Change("Small");
        rendered.WaitForAssertion(() => Assert.Equal([CustomFieldSteps.Create], FooterSteps(rendered).Select(step => step.Key)), Wait);
        ClickOnce(rendered, () => Button(rendered, "Add option"));
        rendered.Find("[name=custom-field-option-2]").Change("Medium");
        ClickOnce(rendered, () => Step(rendered, CustomFieldSteps.Create));
        rendered.WaitForAssertion(() => Assert.Equal("Draft", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);

        var stored = await StoredByNameAsync(name, cancellationToken);
        Assert.Equal((AttributeValueType.Choice, (Guid?)scenario.A.TenantId, scenario.A.SystemAccountId, CustomFieldState.Draft), (stored.ValueType, stored.TenantId, stored.CreatedById, stored.State));
        Assert.Equal([new("small", "Small"), new("medium", "Medium")], stored.Options);

        rendered.WaitForAssertion(() => Assert.True(rendered.Find("[name=custom-field-tenant]").HasAttribute("readonly")), Wait);
        Assert.Equal(scenario.A.Name, rendered.Find("[name=custom-field-tenant]").GetAttribute("value"));
        Assert.True(rendered.Find("[name=custom-field-type]").HasAttribute("readonly"));
        ClickOnce(rendered, () => Step(rendered, CustomFieldSteps.Activate));
        rendered.WaitForAssertion(() => Assert.Equal("Active", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
        Assert.Equal(CustomFieldState.Active, (await StoredByNameAsync(name, cancellationToken)).State);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_tenant_administrator_gets_no_new_field_and_opens_every_active_field_read_only()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, global, foreign) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync(scenario.A, "Team", cancellationToken);
        var draft = await AddFieldAsync(scenario.A, "Team draft", cancellationToken, CustomFieldState.Draft);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<Settings>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, team)), Wait);
        Assert.NotNull(Row(rendered, global));
        Assert.Null(Row(rendered, foreign));
        Assert.Null(Row(rendered, draft));
        Assert.Contains("Own", Row(rendered, team)!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(rendered.FindAll("button"), button => ButtonText(button) is "New field" or "New Field");

        foreach (var field in new[] { team, global })
        {
            ClickOnce(rendered, () => Row(rendered, field)!);
            WaitForDialog(rendered);
            rendered.WaitForAssertion(() => Assert.True(rendered.Find("[name=custom-field-name]").HasAttribute("readonly")), Wait);
            Assert.Contains("you can view this field", rendered.Find(".custom-field-dialog__note").TextContent, StringComparison.Ordinal);
            Assert.Empty(FooterSteps(rendered));
            Assert.Null(rendered.EditButton());
            Assert.DoesNotContain(rendered.FindAll(".state-entity-dialog button"), button => ButtonText(button) is "Add option");
            await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());
            rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_owner_changes_a_tenants_field_and_deactivates_it_keeping_the_tenants_values()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, _, _) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync(scenario.A, "Team", cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            system.Add(new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = team, StringValue = "planning" });
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<PlatformSettings>(builder));
        rendered.WaitForAssertion(() => Button(rendered, "New Field"), Wait);
        await rendered.InvokeAsync(() => rendered.Find("input[name='custom-fields-search']").Change(scenario.A.Name));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, team)), Wait);
        Assert.Contains("Active", Row(rendered, team)!.TextContent, StringComparison.Ordinal);

        ClickOnce(rendered, () => Row(rendered, team)!);
        WaitForDialog(rendered);
        rendered.WaitForAssertion(() => Assert.Equal("Team", rendered.Find("[name=custom-field-name]").GetAttribute("value")), Wait);
        Assert.Equal(scenario.A.Name, rendered.Find("[name=custom-field-tenant]").GetAttribute("value"));
        if (rendered.EditButton() is not null)
        {
            rendered.Edit();
        }

        rendered.Find("[name=custom-field-name]").Change("Teams");
        rendered.Find("[name=custom-field-option-2]").Change("Plans");
        ClickOnce(rendered, () => rendered.Find("button[aria-label='Retire option 1']"));
        Assert.Empty(rendered.FindAll("button[aria-label='Remove option 1']"));
        ClickOnce(rendered, () => Step(rendered, CustomFieldSteps.SaveActive));
        rendered.WaitForAssertion(() => Assert.Equal("Teams", rendered.Find(".state-entity-dialog-header__title").TextContent.Trim()), Wait);
        var stored = await StoredAsync(team, cancellationToken);
        Assert.Equal(("Teams", (Guid?)scenario.A.SystemAccountId), (stored.Name, stored.ModifiedById));
        Assert.Equal([new("teaching", "Working", IsRetired: true), new("planning", "Plans")], stored.Options);

        ClickOnce(rendered, () => Step(rendered, CustomFieldSteps.Deactivate));
        rendered.WaitForAssertion(() => Assert.Contains("The values records hold are kept", rendered.Find("[data-form-dialog=ConfirmDialog]").TextContent, StringComparison.Ordinal), Wait);
        ClickOnce(rendered, () => rendered.FindAll("[data-form-dialog=ConfirmDialog] button").Last(button => ButtonText(button) == "Deactivate"));
        rendered.WaitForAssertion(() => Assert.Equal("Inactive", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
        Assert.Equal(CustomFieldState.Inactive, (await StoredAsync(team, cancellationToken)).State);
        await using var check = this.database.CreateSystemContext();
        Assert.True(await check.ApplicationUserAttributes.AnyAsync(row => row.EntityAttributeDefinitionId == team, cancellationToken));
        Assert.Contains(FooterSteps(rendered), step => step.Key == CustomFieldSteps.Reactivate);
    }

    [Fact]
    public async Task The_applies_to_column_shows_only_while_the_list_is_not_grouped_by_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, global, _) = await ArrangeAsync(cancellationToken);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<Settings>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, global)), Wait);

        Assert.Equal(["Name", "Type", "Scope"], Headings(rendered));
        Assert.Empty(rendered.FindAll("td[data-label='Applies to']"));

        await rendered.InvokeAsync(() => rendered.Find("button#fields-list-group").Click());
        await rendered.InvokeAsync(() => rendered.Find("#fields-list-group ~ [role=listbox] [role=option][data-value='']").Click());
        rendered.WaitForAssertion(() => Assert.Equal(["Name", "Type", "Applies to", "Scope"], Headings(rendered)), Wait);
        rendered.WaitForAssertion(() => Assert.Contains("Users", Row(rendered, global)!.QuerySelector("td[data-label='Applies to']")!.TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_viewer_of_settings_gets_no_new_field_and_only_read_only_dialogs()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, _, _) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync(scenario.A, "Team", cancellationToken);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.SettingsView);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(viewer));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<Settings>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, team)), Wait);

        Assert.DoesNotContain(rendered.FindAll("button"), button => ButtonText(button) == "New field");
        ClickOnce(rendered, () => Row(rendered, team)!);
        WaitForDialog(rendered);
        rendered.WaitForAssertion(() => Assert.True(rendered.Find("[name=custom-field-name]").HasAttribute("readonly")), Wait);
        Assert.Empty(FooterSteps(rendered));
        Assert.DoesNotContain(rendered.FindAll(".state-entity-dialog button"), button => ButtonText(button) is "Add option");
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_settings_page_lists_every_field_and_creates_a_global_one_or_one_for_a_tenant()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var (scenario, global, _) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync(scenario.A, "Team", cancellationToken);
        var draft = await AddFieldAsync(scenario.A, "Team draft", cancellationToken, CustomFieldState.Draft);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var circuit = await CircuitAsync(SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin));
        using var page = circuit.Page();
        var rendered = page.Render(builder => Host<PlatformSettings>(builder));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, global)), Wait);
        rendered.WaitForAssertion(() => Button(rendered, "New Field"), Wait);

        await rendered.InvokeAsync(() => rendered.Find("input[name='custom-fields-search']").Change(scenario.A.Name));
        rendered.WaitForAssertion(() => Assert.NotNull(Row(rendered, team)), Wait);
        Assert.Contains(scenario.A.Name, rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("Draft", Row(rendered, draft)!.TextContent, StringComparison.Ordinal);

        var name = $"Cost centre {Guid.NewGuid():N}"[..20];
        await NewFieldAsync(rendered, CustomFieldTarget.OrganizationUnit);
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<UnitFieldDialog>()), Wait);
        rendered.Find("[name=custom-field-name]").Change(name);
        ClickOnce(rendered, () => Step(rendered, CustomFieldSteps.Create));
        rendered.WaitForAssertion(() => Assert.Equal("Draft", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
        await rendered.InvokeAsync(() => circuit.Dialogs(page).Close());

        await using (var check = this.database.CreateSystemContext())
        {
            var stored = await check.OrganizationUnitAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Name == name, cancellationToken);
            Assert.Equal(((Guid?)null, platform.OwnerId, CustomFieldState.Draft), (stored.TenantId, stored.CreatedById, stored.State));
        }

        using var jobPage = circuit.Page();
        rendered = jobPage.Render(builder => Host<PlatformSettings>(builder));
        rendered.WaitForAssertion(() => Button(rendered, "New Field"), Wait);
        var forA = $"Room {Guid.NewGuid():N}"[..20];
        await NewFieldAsync(rendered, CustomFieldTarget.Job);
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<JobFieldDialog>()), Wait);
        var tenantPicker = rendered.FindComponents<RadzenDropDown<Guid?>>().Single(dropDown => dropDown.Instance.Name == "custom-field-tenant");
        await rendered.InvokeAsync(() => tenantPicker.Instance.ValueChanged.InvokeAsync(scenario.A.TenantId));
        Assert.NotEmpty(rendered.FindAll("[name=custom-field-audience]"));
        Assert.NotEmpty(rendered.FindAll("[name=custom-field-required]"));
        rendered.Find("[name=custom-field-name]").Change(forA);
        ClickOnce(rendered, () => Step(rendered, CustomFieldSteps.Create));
        rendered.WaitForAssertion(() => Assert.Equal("Draft", rendered.Find(".state-pill__button").GetAttribute("data-state")), Wait);
        await rendered.InvokeAsync(() => circuit.Dialogs(jobPage).Close());

        await using (var check = this.database.CreateSystemContext())
        {
            var ofA = await check.JobAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Name == forA, cancellationToken);
            Assert.Equal(((Guid?)scenario.A.TenantId, scenario.A.SystemAccountId), (ofA.TenantId, ofA.CreatedById));
        }

        Assert.Equal("Platform settings", rendered.Find("h1.page-header__title").TextContent.Trim());
        Assert.NotNull(rendered.Find("button#custom-fields-filter-entity[aria-haspopup=listbox]"));
        Assert.NotEmpty(rendered.FindAll("#custom-fields-filter-scope"));
        Assert.NotEmpty(rendered.FindAll("#custom-fields-filter-state"));
    }

    private async Task<(TwoTenantScenario Scenario, Guid Global, Guid Foreign)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        Guid global;
        await using (var system = this.database.CreateSystemContext())
        {
            var field = new ApplicationUserAttributeDefinition { Name = $"Employee number {Guid.NewGuid():N}"[..24], ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId, State = CustomFieldState.Active };
            system.Add(field);
            await system.SaveChangesAsync(cancellationToken);
            global = field.Id;
        }

        return (scenario, global, await AddFieldAsync(scenario.B, "Secret of B", cancellationToken));
    }

    private async Task<Guid> AddFieldAsync(ScenarioTenant tenant, string name, CancellationToken cancellationToken, CustomFieldState state = CustomFieldState.Active)
    {
        await using var system = this.database.CreateSystemContext();
        var field = new ApplicationUserAttributeDefinition
        {
            Name = name,
            ValueType = AttributeValueType.Choice,
            TenantId = tenant.TenantId,
            CreatedById = tenant.SystemAccountId,
            State = state,
            Options = [new("teaching", "Working"), new("planning", "Planning")],
        };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task<ApplicationUserAttributeDefinition> StoredAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ApplicationUserAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<ApplicationUserAttributeDefinition> StoredByNameAsync(string name, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ApplicationUserAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Name == name, cancellationToken);
    }

    private static async Task NewFieldAsync(IRenderedComponent<IComponent> rendered, CustomFieldTarget target)
    {
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog")), Wait);
        ClickOnce(rendered, () => Button(rendered, "New Field"));
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindComponents<ChooseCustomFieldTargetDialog>()), Wait);
        var box = rendered.FindComponents<ChooseCustomFieldTargetDialog>().Single().FindComponents<RadzenDropDown<CustomFieldTarget>>().Single();
        await rendered.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(target));
        ClickOnce(rendered, () => rendered.FindAll(".form-dialog-frame__footer button").Single(button => ButtonText(button) == "Continue"));
        WaitForDialog(rendered);
    }

    private static List<(string Key, string Text)> FooterSteps(IRenderedComponent<IComponent> rendered) =>
        [.. rendered.FindAll(".state-entity-dialog-footer button[data-transition]").Select(button => (button.GetAttribute("data-transition")!, ButtonText(button)))];

    private static IElement Step(IRenderedComponent<IComponent> rendered, string key) =>
        rendered.FindAll($".state-entity-dialog button[data-transition='{key}']").Last();

    private static IElement? Row(IRenderedComponent<IComponent> rendered, Guid id) =>
        rendered.FindAll($"tr.list-row[data-row='{id}']").SingleOrDefault();

    private static IElement Button(IRenderedComponent<IComponent> rendered, string text) =>
        rendered.FindAll("button").Last(button => ButtonText(button) == text);

    private static List<string> Headings(IRenderedComponent<IComponent> rendered) =>
        [.. rendered.FindAll("table.list-table thead th").Where(heading => !heading.ClassList.Contains("list-table__expand")).Select(heading => (heading.QuerySelector(".list-sort__label")?.TextContent ?? heading.TextContent).Trim())];

    private static string ButtonText(IElement button) =>
        (button.QuerySelector(".rz-button-text")?.TextContent ?? button.TextContent).Trim();

    private static void ClickOnce(IRenderedComponent<IComponent> rendered, Func<IElement> find) =>
        rendered.WaitForAssertion(() =>
        {
            try
            {
                rendered.InvokeAsync(() => find().Click()).GetAwaiter().GetResult();
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException exception)
            {
                Assert.Fail(exception.Message);
            }
        }, Wait);

    private static void WaitForDialog(IRenderedComponent<IComponent> rendered)
    {
        rendered.WaitForAssertion(() => Assert.Equal("loaded", rendered.FindAll(".state-entity-dialog").Last().GetAttribute("data-load-state")), Wait);
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.FindAll(".state-entity-dialog-footer__bar").Last().GetAttribute("aria-busy")), Wait);
    }

    private async Task<Circuit> CircuitAsync(System.Security.Claims.ClaimsPrincipal cookie)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
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
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            page.Services.GetRequiredService<NavigationManager>().NavigateTo("/tenant/management/settings/custom-fields");
            return page;
        }

        public DialogService Dialogs(PageContext page) => page.Services.GetRequiredService<DialogService>();

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
