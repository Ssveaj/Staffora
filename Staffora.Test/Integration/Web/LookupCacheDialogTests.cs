using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes;
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
public class LookupCacheDialogTests : IClassFixture<RealDatabase>
{
    private const string DefinitionsTable = "[ApplicationUserAttributeDefinitions]";

    private readonly RealDatabase database;

    public LookupCacheDialogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Two_openings_of_the_user_dialog_issue_one_definitions_query_and_a_saved_definition_shows_on_the_next_opening()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.UsersEdit);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, colleague, cancellationToken, RolePermission.UsersView);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var own = await AddFieldAsync(scenario.A.TenantId, "Badge number", scenario.A.OwnerId, cancellationToken);
        var global = await AddFieldAsync(tenantId: null, $"Employee number {Guid.NewGuid():N}"[..24], platform.OwnerId, cancellationToken);
        var foreign = await AddFieldAsync(scenario.B.TenantId, "Secret of B", scenario.B.OwnerId, cancellationToken);

        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var administrator = await CircuitAsync(SessionCircuit.Cookie(scenario.A.OwnerId), app);
        await using var other = await CircuitAsync(SessionCircuit.Cookie(colleague), app);
        IReadOnlyList<ServiceHost> hosts = [app, administrator.Host, other.Host];

        var first = OpenUserDialog(administrator, colleague);
        var second = OpenUserDialog(other, scenario.A.OwnerId);

        Assert.Equal(new[] { own, global }.Order(), first.Order());
        Assert.Equal(first.Order(), second.Order());
        Assert.DoesNotContain(foreign, first);
        Assert.Equal(1, DefinitionQueries(hosts));

        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), ServiceHost.SharingLookupCacheOf(app));
        var created = await console.Get<CustomFieldService>().CreateFieldAsync(CustomFieldTarget.User, new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Shoe size", ValueType = AttributeValueType.Integer }, scenario.A.TenantId, cancellationToken: cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        ClearCommandLogs(hosts);

        var third = OpenUserDialog(other, scenario.A.OwnerId);
        Assert.Equal(new[] { own, global, created.Value }.Order(), third.Order());
        Assert.Equal(1, DefinitionQueries(hosts));

        var fourth = OpenUserDialog(administrator, colleague);
        Assert.Equal(third.Order(), fourth.Order());
        Assert.Equal(1, DefinitionQueries(hosts));
    }

    private static List<Guid> OpenUserDialog(Circuit circuit, Guid userId)
    {
        using var page = circuit.Page();
        var rendered = page.Render<RadzenDialog>();
        _ = rendered.InvokeAsync(() => UserDialog.OpenAsync(page.Services.GetRequiredService<DialogService>(), page.Services.GetRequiredService<UserService>(), userId));
        var dialog = rendered.WaitForComponent<UserDialog>();
        rendered.WaitForAssertion(() => Assert.NotNull(dialog.Find("[name='user-first-name']")), TimeSpan.FromSeconds(10));
        Assert.Empty(dialog.FindAll(".rz-alert"));
        return [.. dialog.FindComponents<AttributeValueField>().Select(field => field.Instance.Definition.Id)];
    }

    private static int DefinitionQueries(IEnumerable<ServiceHost> hosts) =>
        hosts.SelectMany(host => host.CommandLog).Count(command => command.Contains(DefinitionsTable, StringComparison.Ordinal));

    private static void ClearCommandLogs(IEnumerable<ServiceHost> hosts)
    {
        foreach (var host in hosts)
        {
            host.ClearCommandLog();
        }
    }

    private async Task<Guid> AddFieldAsync(Guid? tenantId, string name, Guid creatorId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var field = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = AttributeValueType.Text, TenantId = tenantId, CreatedById = creatorId };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task<Circuit> CircuitAsync(ClaimsPrincipal cookie, ServiceHost app)
    {
        var (host, runtime) = SessionCircuit.Open(this.database, cookie, ServiceHost.SharingLookupCacheOf(app));
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

        public ServiceHost Host => this.host;

        public PageContext Page()
        {
            var page = new PageContext();
            page.Services.AddSingleton<IRuntimeExecutionContext>(this.runtime);
            page.Services.AddSingleton(TimeProvider.System);
            page.Services.AddSingleton(this.host.Get<Staffora.Services.Workflow.WorkflowService>());
            page.Services.AddSingleton(this.host.Get<UserSessionService>());
            page.Services.AddSingleton(this.host.Get<PermissionService>());
            page.Services.AddSingleton(this.host.Get<UserService>());
            page.Services.AddSingleton(this.host.Get<TenantService>());
            page.Services.AddSingleton(this.host.Get<TenantRoleService>());
            page.Services.AddSingleton(this.host.Get<RoleHoldingService>());
            page.Services.AddSingleton(this.host.Get<CustomFieldService>());
            return page;
        }

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    private sealed class PageContext : WebTestContext;
}
