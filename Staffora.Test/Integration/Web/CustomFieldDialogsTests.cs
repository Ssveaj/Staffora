using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
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
public class CustomFieldDialogsTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public CustomFieldDialogsTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_user_dialog_renders_every_field_through_the_value_field_and_saves_what_it_sets()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var fields = await AddEveryTypeAsync<ApplicationUserAttributeDefinition>(scenario, cancellationToken);
        var foreign = await AddForeignAsync<ApplicationUserAttributeDefinition>(scenario, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);

        await using var circuit = await CircuitAsync(scenario.A.OwnerId);
        using var page = circuit.Page();
        var rendered = page.Render<RadzenDialog>();
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(page.Services.GetRequiredService<DialogService>(), circuit.Get<UserService>(), colleague));
        var dialog = rendered.WaitForComponent<UserDialog>();
        rendered.WaitForAssertion(() => Assert.Equal(fields.Count, dialog.FindComponents<AttributeValueField>().Count), TimeSpan.FromSeconds(10));

        AssertRenderedOnlyThroughTheValueField(dialog, fields.Values, foreign, "user-attribute");

        rendered.Edit();
        var choice = dialog.FindComponents<AttributeValueField>().Single(field => field.Instance.Definition.Id == fields[AttributeValueType.Choice]).FindComponent<RadzenDropDown<string>>();
        await rendered.InvokeAsync(() => choice.Instance.ValueChanged.InvokeAsync("teaching"));
        var date = dialog.FindComponents<AttributeValueField>().Single(field => field.Instance.Definition.Id == fields[AttributeValueType.Date]).FindComponent<RadzenDatePicker<DateOnly?>>();
        await rendered.InvokeAsync(() => date.Instance.ValueChanged.InvokeAsync(new DateOnly(2026, 11, 3)));
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog .state-entity-dialog-header__unsaved")), TimeSpan.FromSeconds(10));
        rendered.WaitForAssertion(() => Assert.NotEmpty(rendered.FindAll(".state-entity-dialog button[data-transition='user-save']")), TimeSpan.FromSeconds(10));
        await rendered.InvokeAsync(() => rendered.Find(".state-entity-dialog button[data-transition='user-save']").Click());
        rendered.WaitForAssertion(() => Assert.Empty(rendered.FindAll(".state-entity-dialog .state-entity-dialog-header__unsaved")), TimeSpan.FromSeconds(10));

        await using var check = this.database.CreateSystemContext();
        var stored = await check.ApplicationUserAttributes.AsNoTracking().Where(row => row.EntityId == colleague).ToDictionaryAsync(row => row.EntityAttributeDefinitionId, cancellationToken);
        Assert.Equal("teaching", stored[fields[AttributeValueType.Choice]].StringValue);
        Assert.Equal(new DateOnly(2026, 11, 3), stored[fields[AttributeValueType.Date]].DateValue);
        Assert.Equal(2, stored.Count);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_unit_dialog_renders_every_field_through_the_value_field_and_a_viewer_sees_them_disabled()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var viewer = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Viewer", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, viewer, cancellationToken, RolePermission.OrganizationUnitsView);
        var fields = await AddEveryTypeAsync<OrganizationUnitAttributeDefinition>(scenario, cancellationToken);
        var foreign = await AddForeignAsync<OrganizationUnitAttributeDefinition>(scenario, cancellationToken);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(this.database, scenario.A, cancellationToken);

        await using var circuit = await CircuitAsync(viewer);
        using var page = circuit.Page();
        var rendered = page.Render<RadzenDialog>();
        _ = rendered.InvokeAsync(() => OrganizationUnitDialog.OpenAsync(page.Services.GetRequiredService<DialogService>(), circuit.Get<OrganizationService>(), rootId));
        var dialog = rendered.WaitForComponent<OrganizationUnitDialog>();
        rendered.WaitForAssertion(() => Assert.Equal(fields.Count, dialog.FindComponents<AttributeValueField>().Count), TimeSpan.FromSeconds(10));

        AssertRenderedOnlyThroughTheValueField(dialog, fields.Values, foreign, "unit-attribute");
        Assert.Equal(Staffora.Core.Workflow.FieldAccess.FieldAccessFlags.Visible, dialog.Instance.Context.AreaAccessOf(Staffora.Core.Workflow.FieldAccess.WorkflowAreas.Attributes));
        Assert.All(dialog.FindAll("input.rz-textbox[name^=unit-attribute]"), input => Assert.True(input.HasAttribute("readonly")));
        rendered.WaitForAssertion(() => Assert.Equal("false", rendered.Find(".state-entity-dialog-footer__bar").GetAttribute("aria-busy")), TimeSpan.FromSeconds(10));
        Assert.Empty(rendered.FindAll(".state-entity-dialog button[data-transition]"));
        await Task.CompletedTask;
    }

    private static void AssertRenderedOnlyThroughTheValueField<TDialog>(IRenderedComponent<TDialog> dialog, IEnumerable<Guid> fields, Guid foreign, string prefix) where TDialog : class, IComponent
    {
        var rendered = dialog.FindComponents<AttributeValueField>();
        Assert.Equal(fields.Order(), rendered.Select(field => field.Instance.Definition.Id).Order());
        Assert.All(rendered, field => Assert.Equal($"{prefix}-{field.Instance.Definition.Id}", field.Instance.InputName));

        var named = dialog.FindAll($"[name^='{prefix}-']");
        Assert.NotEmpty(named);
        Assert.All(named, input => Assert.NotNull(input.Closest($".attribute-value-field[data-attribute-field='{input.GetAttribute("name")![(prefix.Length + 1)..]}']")));
        Assert.Empty(dialog.FindAll($"[name='{prefix}-{foreign}'], [data-attribute-field='{foreign}']"));
        AssertGroupedInTheCustomFieldsFieldset(dialog, fields, prefix);
    }

    private static void AssertGroupedInTheCustomFieldsFieldset<TDialog>(IRenderedComponent<TDialog> dialog, IEnumerable<Guid> fields, string prefix) where TDialog : class, IComponent
    {
        var grid = Assert.Single(dialog.FindAll(".custom-fields-fieldset"));
        Assert.Contains("field-row", grid.ClassList);
        Assert.Empty(dialog.FindAll("legend, .custom-fields-fieldset__heading"));
        Assert.DoesNotContain("Global fields", dialog.Markup, StringComparison.Ordinal);
        Assert.Null(dialog.Find(".custom-fields-fieldset").QuerySelector($"#{prefix}-global-fields"));
        var inside = grid.QuerySelectorAll(".attribute-value-field").Select(field => Guid.Parse(field.GetAttribute("data-attribute-field")!)).ToList();
        Assert.Equal(fields.Order(), inside.Order());
        Assert.Equal(inside.Count, dialog.FindAll(".attribute-value-field").Count);

        var global = Assert.Single(dialog.FindComponents<AttributeValueField>(), field => field.Instance.Definition.TenantId is null).Instance.Definition.Id;
        Assert.Equal(global, inside[^1]);
    }

    private async Task<Dictionary<AttributeValueType, Guid>> AddEveryTypeAsync<TDefinition>(TwoTenantScenario scenario, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition, new()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var fields = new Dictionary<AttributeValueType, Guid>();
        await using var system = this.database.CreateSystemContext();
        foreach (var type in AttributeDefinitionRules.Types)
        {
            var field = new TDefinition
            {
            State = CustomFieldState.Active,
                Name = $"{type} field",
                ValueType = type,
                TenantId = scenario.A.TenantId,
                CreatedById = scenario.A.OwnerId,
                Options = type == AttributeValueType.Choice ? [new("teaching", "Working"), new("planning", "Planning")] : [],
            };
            system.Add(field);
            await system.SaveChangesAsync(cancellationToken);
            fields[type] = field.Id;
        }

        var global = new TDefinition { State = CustomFieldState.Active, Name = $"Global {Guid.NewGuid():N}", ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId };
        system.Add(global);
        await system.SaveChangesAsync(cancellationToken);
        fields[(AttributeValueType)(-1)] = global.Id;
        return fields;
    }

    private async Task<Guid> AddForeignAsync<TDefinition>(TwoTenantScenario scenario, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition, new()
    {
        await using var system = this.database.CreateSystemContext();
        var field = new TDefinition { State = CustomFieldState.Active, Name = "Secret of B", ValueType = AttributeValueType.Text, TenantId = scenario.B.TenantId, CreatedById = scenario.B.OwnerId };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
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
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Workflow.WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<UserService>());
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

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
