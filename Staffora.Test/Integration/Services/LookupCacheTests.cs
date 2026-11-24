using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Caching;
using Staffora.Services.CustomFields;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class LookupCacheTests : IClassFixture<RealDatabase>
{
    private const string UserDefinitions = "[ApplicationUserAttributeDefinitions]";

    private readonly RealDatabase database;

    public LookupCacheTests(RealDatabase database)
    {
        this.database = database;
    }

    private readonly HashSet<Guid> globals = [];

    [Fact]
    public async Task An_entry_holds_its_own_tenants_fields_whichever_circuit_filled_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, fields) = await ArrangeAsync(cancellationToken);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), ServiceHost.SharingLookupCacheOf(app)))
        {
            Assert.Equal(fields.OfB, await IdsAsync(console, scenario.B.TenantId));
        }

        app.ClearCommandLog();
        await using (var ofB = ServiceHost.Create(this.database, scenario.B.Runtime(), ServiceHost.SharingLookupCacheOf(app)))
        {
            Assert.Equal(fields.OfB, await IdsAsync(ofB));
            Assert.Equal(0, Queries(app, ofB));
        }

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        await using (var asA = ServiceHost.Create(this.database, impersonating, ServiceHost.SharingLookupCacheOf(app)))
        {
            Assert.Equal(fields.OfA, await IdsAsync(asA));
            Assert.Equal(CustomFieldService.OtherTenantsFields, (await asA.Get<CustomFieldService>().GetDefinitionsAsync<ApplicationUserAttributeDefinition>(scenario.B.TenantId)).Error);
        }

        app.ClearCommandLog();
        await using var ofA = ServiceHost.Create(this.database, scenario.A.Runtime(), ServiceHost.SharingLookupCacheOf(app));
        Assert.Equal(fields.OfA, await IdsAsync(ofA));
        Assert.Equal(0, Queries(app, ofA));
    }

    [Fact]
    public async Task A_tenant_user_naming_another_tenant_is_refused_before_the_cache_and_fills_no_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, fields) = await ArrangeAsync(cancellationToken);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());

        await using (var ofA = ServiceHost.Create(this.database, scenario.A.Runtime(), ServiceHost.SharingLookupCacheOf(app)))
        {
            var refused = await ofA.Get<CustomFieldService>().GetDefinitionsAsync<ApplicationUserAttributeDefinition>(scenario.B.TenantId);
            Assert.Equal(CustomFieldService.OtherTenantsFields, refused.Error);
            Assert.Equal(0, Queries(app, ofA));
        }

        await using var ofB = ServiceHost.Create(this.database, scenario.B.Runtime(), ServiceHost.SharingLookupCacheOf(app));
        Assert.Equal(fields.OfB, await IdsAsync(ofB));
        Assert.Equal(1, Queries(app, ofB));
    }

    [Fact]
    public async Task The_platform_console_naming_a_tenant_that_does_not_exist_is_refused_and_fills_no_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, fields) = await ArrangeAsync(cancellationToken);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var cache = console.Get<TenantLookupCache>();
        var service = console.Get<CustomFieldService>();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.Equal(CustomFieldService.UnknownTenant, (await service.GetDefinitionsAsync<ApplicationUserAttributeDefinition>(Guid.NewGuid())).Error);
            Assert.Equal(CustomFieldService.UnknownTenant, (await service.GetDefinitionsAsync<OrganizationUnitAttributeDefinition>(Guid.NewGuid())).Error);
        }

        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(0, Queries(console));

        Assert.Equal(fields.OfB, await IdsAsync(console, scenario.B.TenantId));
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public async Task A_warm_entry_never_answers_a_caller_the_service_refuses()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, fields) = await ArrangeAsync(cancellationToken);
        var stranger = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Stranger", cancellationToken);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var ofA = ServiceHost.Create(this.database, scenario.A.Runtime(), ServiceHost.SharingLookupCacheOf(app));
        Assert.Equal(fields.OfA, await IdsAsync(ofA));

        await using var withoutPermission = ServiceHost.Create(this.database, scenario.A.RuntimeOf(stranger), ServiceHost.SharingLookupCacheOf(app));
        Assert.Equal(CustomFieldService.NoViewPermission, (await withoutPermission.Get<CustomFieldService>().GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Error);
    }

    [Fact]
    public async Task Every_save_of_a_field_shows_on_the_next_read_of_each_entry_it_is_in_and_of_no_other()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, fields) = await ArrangeAsync(cancellationToken);
        await using var app = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        await using var ofA = ServiceHost.Create(this.database, scenario.A.Runtime(), ServiceHost.SharingLookupCacheOf(app));
        await using var ofB = ServiceHost.Create(this.database, scenario.B.Runtime(), ServiceHost.SharingLookupCacheOf(app));
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), ServiceHost.SharingLookupCacheOf(app));
        var service = ofA.Get<CustomFieldService>();
        var platformFields = console.Get<CustomFieldService>();
        Assert.Equal(fields.OfA, await IdsAsync(ofA));
        Assert.Equal(fields.OfB, await IdsAsync(ofB));

        var created = await platformFields.CreateFieldAsync(CustomFieldTarget.User, new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Shoe size", ValueType = AttributeValueType.Integer }, scenario.A.TenantId, cancellationToken: cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        ClearLogs(app, ofA, ofB, console);
        Assert.Contains(created.Value, await IdsAsync(ofA));
        Assert.Equal(fields.OfB, await IdsAsync(ofB));
        Assert.Equal(1, Queries(app, ofA, ofB));

        Assert.True((await platformFields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.SaveActive, field => field.Name = "Shoe size (EU)", cancellationToken)).IsSuccess);
        ClearLogs(app, ofA, ofB, console);
        Assert.Equal("Shoe size (EU)", (await service.GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Value!.Single(field => field.Id == created.Value).Name);
        Assert.Equal(1, Queries(app, ofA, ofB));

        Assert.True((await platformFields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).IsSuccess);
        ClearLogs(app, ofA, ofB, console);
        Assert.Equal(fields.OfA, await IdsAsync(ofA));
        Assert.Equal(1, Queries(app, ofA, ofB));

        var global = await platformFields.CreateFieldAsync(CustomFieldTarget.User, new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = $"Locker {Guid.NewGuid():N}"[..16], ValueType = AttributeValueType.Text }, cancellationToken: cancellationToken);
        Assert.True(global.IsSuccess, global.Error);
        this.globals.Add(global.Value);
        ClearLogs(app, ofA, ofB, console);
        Assert.Contains(global.Value, await IdsAsync(ofA));
        Assert.Contains(global.Value, await IdsAsync(ofB));
        Assert.Equal(2, Queries(app, ofA, ofB));

        Assert.True((await platformFields.CreateFieldAsync(CustomFieldTarget.OrganizationUnit, new OrganizationUnitAttributeDefinition { State = CustomFieldState.Active, Name = "Rooms", ValueType = AttributeValueType.Integer }, scenario.A.TenantId, cancellationToken: cancellationToken)).IsSuccess);
        ClearLogs(app, ofA, ofB, console);
        Assert.Contains(global.Value, await IdsAsync(ofA));
        Assert.Equal(0, Queries(app, ofA, ofB));
    }

    [Fact]
    public async Task Each_call_gets_definitions_of_its_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, fields) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<CustomFieldService>();

        var first = (await service.GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Value!;
        var choice = first.Single(field => field.Id == fields.Choice);
        choice.Name = "Changed by a dialog";
        choice.Options.Clear();
        first.Clear();

        var second = (await service.GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Value!;
        Assert.Equal(fields.OfA, Ours(second));
        var again = second.Single(field => field.Id == fields.Choice);
        Assert.Equal(("Team", 2), (again.Name, again.Options.Count));
        Assert.Equal(1, Queries(host));
    }

    private async Task<(TwoTenantScenario Scenario, ScenarioTenant Platform, Fields Fields)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.UsersView, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.UsersView);

        await using var system = this.database.CreateSystemContext();
        var choice = new ApplicationUserAttributeDefinition { Name = "Team", ValueType = AttributeValueType.Choice, TenantId = scenario.A.TenantId, CreatedById = scenario.A.OwnerId, Options = [new("teaching", "Working"), new("planning", "Planning")], State = CustomFieldState.Active };
        var ofB = new ApplicationUserAttributeDefinition { Name = "Secret of B", ValueType = AttributeValueType.Text, TenantId = scenario.B.TenantId, CreatedById = scenario.B.OwnerId, State = CustomFieldState.Active };
        var global = new ApplicationUserAttributeDefinition { Name = $"Employee number {Guid.NewGuid():N}"[..24], ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId, State = CustomFieldState.Active };
        system.AddRange(choice, ofB, global);
        await system.SaveChangesAsync(cancellationToken);
        this.globals.Add(global.Id);
        return (scenario, platform, new Fields(choice.Id, [.. new[] { choice.Id, global.Id }.Order()], [.. new[] { ofB.Id, global.Id }.Order()]));
    }

    private async Task<List<Guid>> IdsAsync(ServiceHost host, Guid? tenantId = null)
    {
        var read = await host.Get<CustomFieldService>().GetDefinitionsAsync<ApplicationUserAttributeDefinition>(tenantId);
        Assert.True(read.IsSuccess, read.Error);
        return Ours(read.Value!);
    }

    private List<Guid> Ours(IEnumerable<ApplicationUserAttributeDefinition> definitions) =>
        [.. definitions.Where(field => field.TenantId is not null || this.globals.Contains(field.Id)).Select(field => field.Id).Order()];

    private static int Queries(params ServiceHost[] hosts) =>
        hosts.SelectMany(host => host.CommandLog).Count(command => command.Contains(UserDefinitions, StringComparison.Ordinal));

    private static void ClearLogs(params ServiceHost[] hosts)
    {
        foreach (var host in hosts)
        {
            host.ClearCommandLog();
        }
    }

    private sealed record Fields(Guid Choice, List<Guid> OfA, List<Guid> OfB);
}
