using Microsoft.EntityFrameworkCore;
using Staffora.Core;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class CustomFieldServiceTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public CustomFieldServiceTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_tenant_administrator_reads_the_tenants_active_fields_read_only_and_writes_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, global) = await ArrangeAsync(cancellationToken);
        var own = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, $"Locker {Guid.NewGuid():N}"[..16], AttributeValueType.Text, cancellationToken);
        var draft = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, $"Draft {Guid.NewGuid():N}"[..16], AttributeValueType.Text, cancellationToken, CustomFieldState.Draft);
        var foreign = await AddFieldAsync<OrganizationUnitAttributeDefinition>(scenario.B, "Secret of B", AttributeValueType.Text, cancellationToken);
        var (ownBefore, globalBefore) = (await StoredAsync<ApplicationUserAttributeDefinition>(own, cancellationToken), await StoredAsync<ApplicationUserAttributeDefinition>(global, cancellationToken));
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var fields = host.Get<CustomFieldService>();

        var rows = (await fields.QueryDefinitionsAsync(CustomFieldList.Definition.Default with { PageSize = 100 })).Value!.Rows.ToList();
        Assert.Contains(rows, row => row.Id == own && !row.IsGlobal);
        Assert.Contains(rows, row => row.Id == global && row.IsGlobal);
        Assert.DoesNotContain(rows, row => row.Id == foreign || row.Id == draft);
        Assert.All(rows, row => Assert.False(row.CanEdit));

        var options = (await fields.GetApplicationUserAttributeDefinitionEditorOptionsAsync(own, cancellationToken)).Value!;
        Assert.True(options.FieldsLocked);
        Assert.Equal([scenario.A.TenantId], options.Tenants.Select(tenant => tenant.Id));
        Assert.Equal(CustomFieldService.NotFound, (await fields.LoadFieldAsync(CustomFieldTarget.User, draft, cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.GetApplicationUserAttributeDefinitionEditorOptionsAsync(null, cancellationToken)).Error);

        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.StepFieldAsync(CustomFieldTarget.User, own, CustomFieldSteps.SaveActive, field => field.Name = "Renamed by A", cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.StepFieldAsync(CustomFieldTarget.User, own, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.StepFieldAsync(CustomFieldTarget.User, global, CustomFieldSteps.SaveActive, field => field.Name = "Renamed by A", cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.CreateFieldAsync(CustomFieldTarget.User, CustomFieldSteps.New(CustomFieldTarget.User, "New in A"), scenario.A.TenantId, cancellationToken: cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.CreateFieldAsync(CustomFieldTarget.User, CustomFieldSteps.New(CustomFieldTarget.User, "New for B"), scenario.B.TenantId, cancellationToken: cancellationToken)).Error);
        Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.NewApplicationUserAttributeDefinitionAsync(cancellationToken)).Error);

        var workflows = host.Get<WorkflowService>();
        Assert.False((await workflows.TransitionEntityAsync<CustomFieldState, ApplicationUserAttributeDefinition>(own, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).IsSuccess);

        var writes = new Func<ApplicationDbContext, Task>[]
        {
            context =>
            {
                context.Add(new ApplicationUserAttributeDefinition { Name = "Added by A", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, State = CustomFieldState.Create });
                return Task.CompletedTask;
            },
            async context => (await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == own, cancellationToken)).Name = "Renamed by A",
            async context => context.Remove(await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == own, cancellationToken)),
            async context => (await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == global, cancellationToken)).Name = "Renamed by A",
            async context => context.Remove(await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == global, cancellationToken)),
            context =>
            {
                context.Update(new ApplicationUserAttributeDefinition { Id = global, Name = "Claimed by A", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.A.OwnerId, State = CustomFieldState.Active });
                return Task.CompletedTask;
            },
        };
        foreach (var write in writes)
        {
            await using var context = this.database.CreateContext(scenario.A.Runtime());
            await write(context);
            var refused = await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
            Assert.Contains("A definition is written only in system scope.", refused.Message, StringComparison.Ordinal);
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            await Assert.ThrowsAsync<TenantIsolationException>(() => context.ApplicationUserAttributeDefinitions.Where(row => row.Id == own).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "Bulk by A"), cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => context.ApplicationUserAttributeDefinitions.Where(row => row.Id == global).ExecuteDeleteAsync(cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => context.ApplicationUserAttributes.ExecuteUpdateAsync(set => set.SetProperty(row => row.StringValue, "Bulk by A"), cancellationToken));
        }

        var (ownAfter, globalAfter) = (await StoredAsync<ApplicationUserAttributeDefinition>(own, cancellationToken), await StoredAsync<ApplicationUserAttributeDefinition>(global, cancellationToken));
        Assert.Equal((ownBefore.Name, ownBefore.State, ownBefore.Modified), (ownAfter.Name, ownAfter.State, ownAfter.Modified));
        Assert.Equal((globalBefore.Name, globalBefore.TenantId, globalBefore.Modified), (globalAfter.Name, globalAfter.TenantId, globalAfter.Modified));
        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.ApplicationUserAttributeDefinitions.AnyAsync(row => row.TenantId == scenario.A.TenantId && (row.Name == "New in A" || row.Name == "Added by A"), cancellationToken));
        Assert.False(await check.ApplicationUserAttributeDefinitions.AnyAsync(row => row.TenantId == scenario.B.TenantId && row.Name == "New for B", cancellationToken));
    }

    [Fact]
    public async Task The_platform_creates_a_tenants_field_as_a_draft_activates_changes_and_deactivates_it_for_that_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var name = $"Shirt size {Guid.NewGuid():N}"[..20];
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();

        var offered = (await fields.GetApplicationUserAttributeDefinitionEditorOptionsAsync(null, cancellationToken)).Value!;
        Assert.False(offered.FieldsLocked);
        Assert.Contains(offered.Tenants, tenant => tenant.Id == scenario.A.TenantId && tenant.Name == scenario.A.Name);
        Assert.Contains(offered.Tenants, tenant => tenant.Id == scenario.B.TenantId);
        Assert.DoesNotContain(offered.Tenants, tenant => tenant.Id == platform.TenantId);
        var blank = (await fields.NewApplicationUserAttributeDefinitionAsync(cancellationToken)).Value!;
        Assert.Equal((CustomFieldState.Create, AttributeValueType.Text, (Guid?)null), (blank.State, blank.ValueType, blank.TenantId));

        var sent = new ApplicationUserAttributeDefinition
        {
            Name = $"  {name}  ",
            ValueType = AttributeValueType.Choice,
            CreatedById = scenario.B.OwnerId,
            State = CustomFieldState.Active,
            Options = [new(string.Empty, "Small"), new(string.Empty, "Medium")],
        };
        var created = await fields.CreateFieldAsync(CustomFieldTarget.User, sent, scenario.A.TenantId, activate: false, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        Assert.Contains(host.ContextLog, message => message.Contains("check a new custom field for a tenant", StringComparison.Ordinal));

        var stored = await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken);
        Assert.Equal((name, (Guid?)scenario.A.TenantId, CustomFieldState.Draft, scenario.A.SystemAccountId, (Guid?)null), (stored.Name, stored.TenantId, stored.State, stored.CreatedById, stored.ImpersonatedById));
        Assert.Equal([new("small", "Small"), new("medium", "Medium")], stored.Options);

        var row = Assert.Single((await fields.QueryDefinitionsAsync(CustomFieldList.PlatformDefinition.Default with { Search = name })).Value!.Rows);
        Assert.Equal((created.Value, false, true, (string?)scenario.A.Name, (Guid?)scenario.A.TenantId, CustomFieldState.Draft), (row.Id, row.IsGlobal, row.CanEdit, row.TenantName, row.TenantId, row.State));

        await AssertSeenByAsync(scenario.A, created.Value, name, seen: false, cancellationToken);

        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.Activate, cancellationToken: cancellationToken)).IsSuccess);
        await AssertSeenByAsync(scenario.A, created.Value, name, seen: true, cancellationToken);
        await using (var hostB = ServiceHost.Create(this.database, scenario.B.Runtime()))
        {
            var fieldsB = hostB.Get<CustomFieldService>();
            Assert.DoesNotContain((await fieldsB.GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Value!, field => field.Id == created.Value);
            Assert.Equal(CustomFieldService.NotFound, (await fieldsB.LoadFieldAsync(CustomFieldTarget.User, created.Value, cancellationToken)).Error);
        }

        var saved = await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.SaveActive, field =>
        {
            field.Name = $"{name} 2";
            field.Options = [new("small", "S"), new("medium", "Medium", IsRetired: true), new(string.Empty, "Small")];
        }, cancellationToken);
        Assert.True(saved.IsSuccess, saved.Error);
        stored = await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken);
        Assert.Equal(($"{name} 2", (Guid?)scenario.A.SystemAccountId), (stored.Name, stored.ModifiedById));
        Assert.Equal([new("small", "S"), new("medium", "Medium", IsRetired: true), new("small-2", "Small")], stored.Options);

        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.SaveActive, field => field.Name = $"{name} 3", cancellationToken)).IsSuccess);
        stored = await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken);
        Assert.Equal(($"{name} 3", (Guid?)scenario.A.SystemAccountId), (stored.Name, stored.ModifiedById));

        await AddValuesAsync(cancellationToken, (scenario.A.OwnerId, created.Value, "small"));
        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).IsSuccess);
        Assert.Equal(CustomFieldState.Inactive, (await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken)).State);
        await AssertSeenByAsync(scenario.A, created.Value, $"{name} 3", seen: false, cancellationToken);
        Assert.Equal(1, await ValuesOfAsync(created.Value, cancellationToken));

        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.SaveInactive, field => field.Name = $"{name} 4", cancellationToken)).IsSuccess);
        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.Reactivate, cancellationToken: cancellationToken)).IsSuccess);
        await AssertSeenByAsync(scenario.A, created.Value, $"{name} 4", seen: true, cancellationToken);
        Assert.Equal(1, await ValuesOfAsync(created.Value, cancellationToken));

        await using var check = this.database.CreateSystemContext();
        var history = await check.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == created.Value).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
        Assert.Equal(
            [CustomFieldSteps.Create, CustomFieldSteps.Activate, CustomFieldSteps.SaveActive, CustomFieldSteps.SaveActive, CustomFieldSteps.Deactivate, CustomFieldSteps.SaveInactive, CustomFieldSteps.Reactivate],
            history.Select(entry => entry.TransitionKey));
        Assert.All(history, entry => Assert.Equal((scenario.A.TenantId, scenario.A.SystemAccountId, platform.OwnerId), (entry.TenantId, entry.CreatedById, entry.ActorId)));
        Assert.Equal((int)CustomFieldState.Create, history[0].FromState);
        Assert.False((await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken)).IsInTransition);

        Assert.Equal(7, (await fields.GetApplicationUserAttributeDefinitionHistoryAsync(created.Value, StateHistoryQuery.Newest(10), cancellationToken)).Value!.TotalCount);
        await using (var reader = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            Assert.Equal(StateHistoryService.OwnersOnly, (await reader.Get<CustomFieldService>().GetApplicationUserAttributeDefinitionHistoryAsync(created.Value, StateHistoryQuery.Newest(10), cancellationToken)).Error);
        }

        await TwoTenantScenario.MakeSystemOwnerAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken);
        await using var hostA = ServiceHost.Create(this.database, scenario.A.Runtime());
        Assert.Equal(7, (await hostA.Get<CustomFieldService>().GetApplicationUserAttributeDefinitionHistoryAsync(created.Value, StateHistoryQuery.Newest(10), cancellationToken)).Value!.TotalCount);
    }

    [Fact]
    public async Task The_platform_makes_a_global_field_and_an_impersonating_owner_writes_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var name = $"Region code {Guid.NewGuid():N}"[..24];

        Guid global;
        await using (var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var fields = host.Get<CustomFieldService>();
            var created = await fields.CreateFieldAsync(CustomFieldTarget.OrganizationUnit, CustomFieldSteps.New(CustomFieldTarget.OrganizationUnit, name), cancellationToken: cancellationToken);
            Assert.True(created.IsSuccess, created.Error);
            global = created.Value;

            var stored = await StoredAsync<OrganizationUnitAttributeDefinition>(global, cancellationToken);
            Assert.Equal(((Guid?)null, platform.OwnerId, (Guid?)null, CustomFieldState.Active), (stored.TenantId, stored.CreatedById, stored.ImpersonatedById, stored.State));
            Assert.Contains(host.ContextLog, message => message.Contains("check a new global custom field", StringComparison.Ordinal));

            Assert.True((await fields.StepFieldAsync(CustomFieldTarget.OrganizationUnit, global, CustomFieldSteps.SaveActive, field => field.Name = $"Region {Guid.NewGuid():N}"[..20], cancellationToken)).IsSuccess);
            Assert.Equal(platform.OwnerId, (await StoredAsync<OrganizationUnitAttributeDefinition>(global, cancellationToken)).ModifiedById);
            Assert.Equal(3, (await fields.GetOrganizationUnitAttributeDefinitionHistoryAsync(global, StateHistoryQuery.Newest(10), cancellationToken)).Value!.TotalCount);
        }

        await using (var check = this.database.CreateSystemContext())
        {
            Assert.All(await check.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityId == global).ToListAsync(cancellationToken), entry => Assert.Equal((platform.TenantId, platform.OwnerId), (entry.TenantId, entry.CreatedById)));
        }

        await using (var tenant = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var fields = tenant.Get<CustomFieldService>();
            Assert.True((await fields.LoadFieldAsync(CustomFieldTarget.OrganizationUnit, global, cancellationToken)).IsSuccess);
            Assert.Equal(CustomFieldService.GlobalHistoryIsThePlatforms, (await fields.GetOrganizationUnitAttributeDefinitionHistoryAsync(global, StateHistoryQuery.Newest(3), cancellationToken)).Error);
        }

        var (impersonating, runtime) = await ImpersonatingAsync(platform, scenario.A.OwnerId, cancellationToken);
        await using (impersonating)
        {
            Assert.False(runtime.HasPlatformReach());
            var fields = impersonating.Get<CustomFieldService>();
            Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.CreateFieldAsync(CustomFieldTarget.User, CustomFieldSteps.New(CustomFieldTarget.User, $"Desk {Guid.NewGuid():N}"[..16]), cancellationToken: cancellationToken)).Error);
            Assert.Equal(CustomFieldService.FieldsAreThePlatforms, (await fields.StepFieldAsync(CustomFieldTarget.OrganizationUnit, global, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).Error);
            Assert.True((await fields.GetOrganizationUnitAttributeDefinitionEditorOptionsAsync(global, cancellationToken)).Value!.FieldsLocked);
        }

        Assert.Equal(CustomFieldState.Active, (await StoredAsync<OrganizationUnitAttributeDefinition>(global, cancellationToken)).State);
    }

    [Fact]
    public async Task A_saved_field_keeps_its_type_its_tenant_and_every_saved_option()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Team", AttributeValueType.Choice, cancellationToken, options: [new("teaching", "Working"), new("planning", "Planning")]);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();
        var before = await StoredAsync<ApplicationUserAttributeDefinition>(team, cancellationToken);

        Assert.Equal("A saved field keeps its type: Team holds choice values. Add a new field for another kind of value.", (await fields.StepFieldAsync(CustomFieldTarget.User, team, CustomFieldSteps.SaveActive, field =>
        {
            field.ValueType = AttributeValueType.Text;
            field.Options = [];
        }, cancellationToken)).Error);
        Assert.Equal("The option \"Planning\" cannot be removed, since values may hold it. Retire it instead.", (await fields.StepFieldAsync(CustomFieldTarget.User, team, CustomFieldSteps.SaveActive, field => field.Options = [new("teaching", "Working")], cancellationToken)).Error);

        Assert.Equal("The option \"Planning\" cannot be removed, since values may hold it. Retire it instead.", (await fields.StepFieldAsync(CustomFieldTarget.User, team, CustomFieldSteps.Deactivate, field => field.Options = [new("teaching", "Working"), new("plans", "Planning")], cancellationToken)).Error);

        var after = await StoredAsync<ApplicationUserAttributeDefinition>(team, cancellationToken);
        Assert.Equal((before.ValueType, before.State), (after.ValueType, after.State));
        Assert.Equal(before.Options, after.Options);
        Assert.Null(after.Modified);
    }

    [Fact]
    public async Task Another_tenants_field_and_a_field_not_in_use_are_never_read_by_a_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var foreign = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.B, "Badge of B", AttributeValueType.Text, cancellationToken);
        var own = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Badge", AttributeValueType.Text, cancellationToken);
        var inactive = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Badge out of use", AttributeValueType.Text, cancellationToken, CustomFieldState.Inactive);
        await using (var host = ServiceHost.Create(this.database, scenario.A.Runtime()))
        {
            var fields = host.Get<CustomFieldService>();
            Assert.Equal(CustomFieldService.NotFound, (await fields.LoadFieldAsync(CustomFieldTarget.User, foreign, cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NotFound, (await fields.LoadFieldAsync(CustomFieldTarget.User, inactive, cancellationToken)).Error);
            Assert.Equal(CustomFieldService.NotFound, (await fields.GetApplicationUserAttributeDefinitionHistoryAsync(foreign, StateHistoryQuery.Newest(3), cancellationToken)).Error);
            var dialogs = (await fields.GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Value!.Select(row => row.Id).ToList();
            Assert.Contains(own, dialogs);
            Assert.DoesNotContain(foreign, dialogs);
            Assert.DoesNotContain(inactive, dialogs);
            Assert.Equal(CustomFieldService.OtherTenantsFields, (await fields.GetDefinitionsAsync<ApplicationUserAttributeDefinition>(scenario.B.TenantId)).Error);
            Assert.DoesNotContain((await fields.QueryDefinitionsAsync(CustomFieldList.Definition.Default with { Search = "Badge" })).Value!.Rows, row => row.Id == foreign || row.Id == inactive);
        }

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            Assert.Equal(CustomFieldService.NotFound, (await console.Get<CustomFieldService>().LoadFieldAsync(CustomFieldTarget.OrganizationUnit, own, cancellationToken)).Error);
            Assert.True((await console.Get<CustomFieldService>().LoadFieldAsync(CustomFieldTarget.User, inactive, cancellationToken)).IsSuccess);
        }

        Assert.Equal("Badge of B", (await StoredAsync<ApplicationUserAttributeDefinition>(foreign, cancellationToken)).Name);
    }

    [Fact]
    public async Task A_name_is_unique_per_scope_and_never_shared_between_a_tenant_field_and_a_global_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var globalName = $"Employee {Guid.NewGuid():N}"[..20];

        var badge = $"Badge {Guid.NewGuid():N}"[..16];
        await AddGlobalFieldAsync(platform, globalName, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();
        var (a, b) = (scenario.A.TenantId, scenario.B.TenantId);
        IEntityAttributeDefinition Named(string name) => CustomFieldSteps.New(CustomFieldTarget.User, name);

        Assert.True((await fields.CreateFieldAsync(CustomFieldTarget.User, Named(badge), a, cancellationToken: cancellationToken)).IsSuccess);
        Assert.Equal($"A field named \"{badge.ToLowerInvariant()}\" exists already for users.", (await fields.CreateFieldAsync(CustomFieldTarget.User, Named(badge.ToLowerInvariant()), a, cancellationToken: cancellationToken)).Error);

        var draft = await fields.CreateFieldAsync(CustomFieldTarget.User, Named($"Draft {Guid.NewGuid():N}"[..16]), a, activate: false, cancellationToken);
        Assert.Equal($"A field named \"{badge}\" exists already for users.", (await fields.StepFieldAsync(CustomFieldTarget.User, draft.Value, CustomFieldSteps.SaveDraft, field => field.Name = badge, cancellationToken)).Error);

        Assert.True((await fields.CreateFieldAsync(CustomFieldTarget.User, Named(badge), b, cancellationToken: cancellationToken)).IsSuccess);
        Assert.True((await fields.CreateFieldAsync(CustomFieldTarget.OrganizationUnit, CustomFieldSteps.New(CustomFieldTarget.OrganizationUnit, badge), a, cancellationToken: cancellationToken)).IsSuccess);

        Assert.Equal($"\"{globalName}\" is the name of a global field for users. Choose another name.", (await fields.CreateFieldAsync(CustomFieldTarget.User, Named(globalName), a, cancellationToken: cancellationToken)).Error);
        var renamed = await fields.CreateFieldAsync(CustomFieldTarget.User, Named($"Locker {Guid.NewGuid():N}"[..16]), a, cancellationToken: cancellationToken);
        Assert.Equal($"\"{globalName}\" is the name of a global field for users. Choose another name.", (await fields.StepFieldAsync(CustomFieldTarget.User, renamed.Value, CustomFieldSteps.SaveActive, field => field.Name = globalName, cancellationToken)).Error);

        Assert.Equal($"2 tenants have a field named \"{badge}\" for users. Choose another name.", (await fields.CreateFieldAsync(CustomFieldTarget.User, Named(badge), cancellationToken: cancellationToken)).Error);

        await using (var system = this.database.CreateSystemContext())
        {
            system.Add(new ApplicationUserAttributeDefinition { Name = badge.ToUpperInvariant(), ValueType = AttributeValueType.Text, TenantId = a, CreatedById = scenario.A.SystemAccountId });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => system.SaveChangesAsync(cancellationToken));
            Assert.Contains("IX_ApplicationUserAttributeDefinitions_TenantId_Name", refused.InnerException!.Message, StringComparison.Ordinal);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.Add(new ApplicationUserAttributeDefinition { Name = globalName, ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId });
            await Assert.ThrowsAsync<DbUpdateException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(1, await check.ApplicationUserAttributeDefinitions.CountAsync(row => row.TenantId == a && row.Name == badge, cancellationToken));
        Assert.Equal(1, await check.ApplicationUserAttributeDefinitions.CountAsync(row => row.TenantId == null && row.Name == globalName, cancellationToken));
    }

    [Fact]
    public async Task The_tenants_list_filters_searches_groups_and_pages_in_one_query_over_both_entities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, global) = await ArrangeAsync(cancellationToken);
        await AddFieldAsync<OrganizationUnitAttributeDefinition>(scenario.A, "Rooms", AttributeValueType.Integer, cancellationToken);
        await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Room key", AttributeValueType.Boolean, cancellationToken);
        await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Room plan", AttributeValueType.Text, cancellationToken, CustomFieldState.Draft);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var fields = host.Get<CustomFieldService>();
        var query = CustomFieldList.Definition.Default;

        var page = (await fields.QueryDefinitionsAsync(query)).Value!;
        Assert.Equal([CustomFieldList.User, CustomFieldList.Unit], page.Sections.Select(section => section.Key));
        Assert.Equal(["Users", "Organization units"], page.Sections.Select(section => section.Label));
        Assert.All(page.Rows, row => Assert.Equal(CustomFieldState.Active, row.State));

        var units = (await fields.QueryDefinitionsAsync(query.WithFilter(CustomFieldList.Entity, CustomFieldList.Unit))).Value!;
        Assert.All(units.Rows, row => Assert.Equal(CustomFieldTarget.OrganizationUnit, row.Target));
        Assert.Contains(units.Rows, row => row.Name == "Rooms");

        var own = (await fields.QueryDefinitionsAsync(query.WithFilter(CustomFieldList.Scope, CustomFieldList.Own))).Value!;
        Assert.DoesNotContain(own.Rows, row => row.Id == global);
        var globals = (await fields.QueryDefinitionsAsync(query.WithFilter(CustomFieldList.Scope, CustomFieldList.Global))).Value!;
        Assert.All(globals.Rows, row => Assert.True(row.IsGlobal));

        var searched = (await fields.QueryDefinitionsAsync(query with { Search = "room", Group = null, Sort = CustomFieldList.SortType })).Value!;
        Assert.Equal(["Rooms", "Room key"], searched.Rows.Select(row => row.Name));
        Assert.Equal(2, searched.TotalCount);

        Assert.Contains(host.CommandLog, command => command.Contains("UNION ALL", StringComparison.Ordinal) && command.Contains("OFFSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_platform_settings_list_groups_every_field_by_tenant_and_filters_by_scope_and_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, global) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();
        var forA = (await fields.CreateFieldAsync(CustomFieldTarget.OrganizationUnit, CustomFieldSteps.New(CustomFieldTarget.OrganizationUnit, $"Site {Guid.NewGuid():N}"[..16]), scenario.A.TenantId, activate: false, cancellationToken)).Value;

        Assert.Equal(CustomFieldList.ByTenant, CustomFieldList.PlatformDefinition.Default.Group);
        var page = (await fields.QueryDefinitionsAsync(CustomFieldList.PlatformDefinition.Default with { Search = scenario.A.Name })).Value!;
        Assert.Contains(page.Rows, row => row.Id == forA && row.CanEdit && row.State == CustomFieldState.Draft);
        Assert.All(page.Rows, row => Assert.Equal(scenario.A.Name, row.TenantName));
        Assert.Contains(page.Sections, section => section.Key == scenario.A.TenantId.ToString() && section.Label == scenario.A.Name);

        var globalName = (await StoredAsync<ApplicationUserAttributeDefinition>(global, cancellationToken)).Name;
        var globals = (await fields.QueryDefinitionsAsync(CustomFieldList.PlatformDefinition.Default with { Search = globalName })).Value!;
        Assert.Contains(globals.Sections, section => section.Key == CustomFieldList.GlobalGroup && section.Label == "Global");
        Assert.All(globals.Rows, row => Assert.True(row.CanEdit));

        var tenants = (await fields.QueryDefinitionsAsync((CustomFieldList.PlatformDefinition.Default with { Search = scenario.A.Name }).WithFilter(CustomFieldList.Scope, CustomFieldList.Own))).Value!;
        Assert.Contains(tenants.Rows, row => row.Id == forA);
        var onlyGlobal = (await fields.QueryDefinitionsAsync((CustomFieldList.PlatformDefinition.Default with { Search = globalName }).WithFilter(CustomFieldList.Scope, CustomFieldList.Global))).Value!;
        Assert.Contains(onlyGlobal.Rows, row => row.Id == global);
        Assert.All(onlyGlobal.Rows, row => Assert.True(row.IsGlobal));

        var drafts = (await fields.QueryDefinitionsAsync((CustomFieldList.PlatformDefinition.Default with { Search = scenario.A.Name }).WithFilter(CustomFieldList.StateFilter, CustomFieldList.Draft))).Value!;
        Assert.Contains(drafts.Rows, row => row.Id == forA);
        var active = (await fields.QueryDefinitionsAsync((CustomFieldList.PlatformDefinition.Default with { Search = scenario.A.Name }).WithFilter(CustomFieldList.StateFilter, CustomFieldList.Active))).Value!;
        Assert.DoesNotContain(active.Rows, row => row.Id == forA);

        var tab = (await fields.QueryTenantDefinitionsAsync(scenario.A.TenantId, CustomFieldList.ConsoleDefinition.Default with { PageSize = 100 })).Value!;
        Assert.Contains(tab.Rows, row => row.Id == forA && row.State == CustomFieldState.Draft);
        Assert.Contains(tab.Rows, row => row.Id == global && row.State == CustomFieldState.Active);
    }

    [Fact]
    public async Task A_field_is_never_deleted_in_any_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, _, global) = await ArrangeAsync(cancellationToken);
        var own = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Locker", AttributeValueType.Text, cancellationToken, CustomFieldState.Draft);

        var workflow = WorkflowRegistry.Default.Get<CustomFieldState, ApplicationUserAttributeDefinition>();
        Assert.DoesNotContain(Enum.GetValues<CustomFieldState>(), state => ((Enum)state).IsDeleteState());
        Assert.DoesNotContain(workflow.Transitions, transition => ((Enum)transition.To).IsDeleteState());

        foreach (var id in new[] { own, global })
        {
            await using var system = this.database.CreateSystemContext();
            using var lease = system.BeginSystemScope("test: delete a custom field past its workflow");
            system.Remove(await system.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == id, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(2, await check.ApplicationUserAttributeDefinitions.CountAsync(row => row.Id == own || row.Id == global, cancellationToken));
    }

    [Fact]
    public async Task An_option_key_the_caller_made_up_is_replaced_on_create_and_a_new_option_gets_one_on_save()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();

        var sent = CustomFieldSteps.New(CustomFieldTarget.User, $"Size {Guid.NewGuid():N}"[..16], AttributeValueType.Choice);
        sent.Options = [new("client-picked", "Small"), new("medium", "Medium")];
        var created = await fields.CreateFieldAsync(CustomFieldTarget.User, sent, scenario.A.TenantId, cancellationToken: cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        Assert.Equal([new("small", "Small"), new("medium", "Medium")], (await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken)).Options);

        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.User, created.Value, CustomFieldSteps.SaveActive, field => field.Options = [new("small", "Small"), new("medium", "Medium"), new(string.Empty, "Large")], cancellationToken)).IsSuccess);
        Assert.Equal([new("small", "Small"), new("medium", "Medium"), new("large", "Large")], (await StoredAsync<ApplicationUserAttributeDefinition>(created.Value, cancellationToken)).Options);
    }

    [Fact]
    public async Task A_step_of_a_field_whose_id_or_tenant_was_changed_in_the_dialog_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var badge = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Badge", AttributeValueType.Text, cancellationToken);
        var desk = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Desk", AttributeValueType.Text, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();

        Assert.False((await fields.StepFieldAsync(CustomFieldTarget.User, badge, CustomFieldSteps.SaveActive, field =>
        {
            field.Id = desk;
            field.Name = "Swapped";
        }, cancellationToken)).IsSuccess);
        Assert.Equal(("Badge", "Desk"), ((await StoredAsync<ApplicationUserAttributeDefinition>(badge, cancellationToken)).Name, (await StoredAsync<ApplicationUserAttributeDefinition>(desk, cancellationToken)).Name));

        var moved = await fields.StepFieldAsync(CustomFieldTarget.User, badge, CustomFieldSteps.SaveActive, field =>
        {
            field.TenantId = scenario.B.TenantId;
            field.Name = "Moved";
        }, cancellationToken);
        Assert.False(moved.IsSuccess);
        Assert.DoesNotContain(scenario.B.Name, moved.Error, StringComparison.Ordinal);
        var stored = await StoredAsync<ApplicationUserAttributeDefinition>(badge, cancellationToken);
        Assert.Equal(("Badge", (Guid?)scenario.A.TenantId), (stored.Name, stored.TenantId));
    }

    [Fact]
    public async Task A_save_over_a_field_someone_else_saved_since_it_was_opened_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        var team = await AddFieldAsync<ApplicationUserAttributeDefinition>(scenario.A, "Team", AttributeValueType.Choice, cancellationToken, options: [new("teaching", "Working"), new("planning", "Planning")]);
        await using var first = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        await using var second = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());

        var mine = (await first.Get<CustomFieldService>().LoadFieldAsync(CustomFieldTarget.User, team, cancellationToken)).Value!;
        var theirs = (await second.Get<CustomFieldService>().LoadFieldAsync(CustomFieldTarget.User, team, cancellationToken)).Value!;

        mine.Definition.Name = "Teams";
        Assert.True((await first.Get<CustomFieldService>().StepAsync(mine, CustomFieldSteps.SaveActive, cancellationToken)).IsSuccess);

        theirs.Definition.Options = [new("teaching", "Times"), new("planning", "Planning")];
        Assert.Equal(CustomFieldService.ChangedMeanwhile, (await second.Get<CustomFieldService>().StepAsync(theirs, CustomFieldSteps.SaveActive, cancellationToken)).Error);
        var stored = await StoredAsync<ApplicationUserAttributeDefinition>(team, cancellationToken);
        Assert.Equal(("Teams", "Working", (Guid?)scenario.A.SystemAccountId), (stored.Name, stored.Options[0].Label, stored.ModifiedById));

        Assert.True((await second.Get<CustomFieldService>().StepFieldAsync(CustomFieldTarget.User, team, CustomFieldSteps.SaveActive, field => field.Options = [new("teaching", "Times"), new("planning", "Planning")], cancellationToken)).IsSuccess);
        stored = await StoredAsync<ApplicationUserAttributeDefinition>(team, cancellationToken);
        Assert.Equal(("Teams", "Times"), (stored.Name, stored.Options[0].Label));
    }

    [Fact]
    public async Task The_platform_writes_no_field_of_a_held_tenant_or_one_that_is_no_staffing_company()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, platform, _) = await ArrangeAsync(cancellationToken);
        var suspended = await TwoTenantScenario.AddTenantAsync(this.database, $"Suspended {Guid.NewGuid():N}"[..20], cancellationToken, TenantState.Suspended);
        var held = await AddFieldAsync<ApplicationUserAttributeDefinition>(suspended, "Held field", AttributeValueType.Text, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        var fields = host.Get<CustomFieldService>();

        Assert.DoesNotContain((await fields.GetApplicationUserAttributeDefinitionEditorOptionsAsync(null, cancellationToken)).Value!.Tenants, tenant => tenant.Id == suspended.TenantId);
        Assert.Contains((await fields.GetApplicationUserAttributeDefinitionEditorOptionsAsync(held, cancellationToken)).Value!.Tenants, tenant => tenant.Id == suspended.TenantId);
        Assert.Equal(TenantStates.HeldDataReason, (await fields.CreateFieldAsync(CustomFieldTarget.User, CustomFieldSteps.New(CustomFieldTarget.User, "For the suspended"), suspended.TenantId, cancellationToken: cancellationToken)).Error);
        Assert.Equal(CustomFieldService.UnknownTenant, (await fields.CreateFieldAsync(CustomFieldTarget.User, CustomFieldSteps.New(CustomFieldTarget.User, "For the platform"), platform.TenantId, cancellationToken: cancellationToken)).Error);
        Assert.Equal(CustomFieldService.UnknownTenant, (await fields.CreateFieldAsync(CustomFieldTarget.User, CustomFieldSteps.New(CustomFieldTarget.User, "For nobody"), Guid.NewGuid(), cancellationToken: cancellationToken)).Error);

        Assert.Equal(TenantStates.HeldDataReason, (await fields.StepFieldAsync(CustomFieldTarget.User, held, CustomFieldSteps.SaveActive, field => field.Name = "Renamed while held", cancellationToken)).Error);
        Assert.False((await fields.StepFieldAsync(CustomFieldTarget.User, held, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).IsSuccess);

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.ApplicationUserAttributeDefinitions.AnyAsync(row => row.Name == "For the suspended" || row.Name == "For the platform" || row.Name == "For nobody", cancellationToken));
        var stored = await check.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == held, cancellationToken);
        Assert.Equal(("Held field", CustomFieldState.Active), (stored.Name, stored.State));
    }

    [Fact]
    public async Task Whether_a_job_field_is_required_is_saved_on_create_and_with_a_step_and_read_through_the_lookup_cache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (scenario, platform, _) = await ArrangeAsync(cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.JobsView);
        await using var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime());
        await using var tenant = ServiceHost.Create(this.database, scenario.A.Runtime(), ServiceHost.SharingLookupCacheOf(console));
        var fields = console.Get<CustomFieldService>();

        var sent = CustomFieldSteps.New(CustomFieldTarget.Job, $"Grade {Guid.NewGuid():N}");
        sent.IsRequired = true;
        var created = await fields.CreateFieldAsync(CustomFieldTarget.Job, sent, scenario.A.TenantId, cancellationToken: cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await StoredAsync<JobAttributeDefinition>(created.Value, cancellationToken)).IsRequired);
        Assert.True((await tenant.Get<CustomFieldService>().GetDefinitionsAsync<JobAttributeDefinition>()).Value!.Single(field => field.Id == created.Value).IsRequired);

        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.Job, created.Value, CustomFieldSteps.SaveActive, field => field.IsRequired = false, cancellationToken)).IsSuccess);
        Assert.False((await StoredAsync<JobAttributeDefinition>(created.Value, cancellationToken)).IsRequired);
        Assert.False((await tenant.Get<CustomFieldService>().GetDefinitionsAsync<JobAttributeDefinition>()).Value!.Single(field => field.Id == created.Value).IsRequired);

        Assert.True((await fields.StepFieldAsync(CustomFieldTarget.Job, created.Value, CustomFieldSteps.Deactivate, cancellationToken: cancellationToken)).IsSuccess);
        Assert.DoesNotContain((await tenant.Get<CustomFieldService>().GetDefinitionsAsync<JobAttributeDefinition>()).Value!, field => field.Id == created.Value);

        var user = CustomFieldSteps.New(CustomFieldTarget.User, $"Grade {Guid.NewGuid():N}");
        user.IsRequired = true;
        var userField = await fields.CreateFieldAsync(CustomFieldTarget.User, user, scenario.A.TenantId, cancellationToken: cancellationToken);
        Assert.False((await StoredAsync<ApplicationUserAttributeDefinition>(userField.Value, cancellationToken)).IsRequired);
        Assert.False((await fields.StepFieldAsync(CustomFieldTarget.User, userField.Value, CustomFieldSteps.SaveActive, field => field.IsRequired = true, cancellationToken)).IsSuccess);

        var yesOrNo = CustomFieldSteps.New(CustomFieldTarget.Job, $"Flagged {Guid.NewGuid():N}", AttributeValueType.Boolean);
        yesOrNo.IsRequired = true;
        Assert.Equal(AttributeDefinitionRules.RequiredYesOrNo, (await fields.CreateFieldAsync(CustomFieldTarget.Job, yesOrNo, scenario.A.TenantId, cancellationToken: cancellationToken)).Error);
    }

    private async Task<(TwoTenantScenario Scenario, ScenarioTenant Platform, Guid Global)> ArrangeAsync(CancellationToken cancellationToken)
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, scenario.A, scenario.A.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        await TwoTenantScenario.GrantAsync(this.database, scenario.B, scenario.B.OwnerId, cancellationToken, RolePermission.SettingsView, RolePermission.SettingsEdit);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var global = await AddGlobalFieldAsync(platform, $"Global {Guid.NewGuid():N}", cancellationToken);
        return (scenario, platform, global);
    }

    private async Task AssertSeenByAsync(ScenarioTenant tenant, Guid field, string name, bool seen, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(this.database, tenant.Runtime());
        var fields = host.Get<CustomFieldService>();
        var rows = (await fields.QueryDefinitionsAsync(CustomFieldList.Definition.Default with { Search = name })).Value!.Rows;
        Assert.Equal(seen, rows.Any(row => row.Id == field && !row.CanEdit && row.TenantName is null));
        Assert.Equal(seen, (await fields.GetDefinitionsAsync<ApplicationUserAttributeDefinition>()).Value!.Any(definition => definition.Id == field));
        Assert.Equal(seen, (await fields.LoadFieldAsync(CustomFieldTarget.User, field, cancellationToken)).IsSuccess);
    }

    private async Task<Guid> AddFieldAsync<TDefinition>(ScenarioTenant tenant, string name, AttributeValueType type, CancellationToken cancellationToken, CustomFieldState state = CustomFieldState.Active, params AttributeChoiceOption[] options)
        where TDefinition : class, IEntityAttributeDefinition, new()
    {
        await using var system = this.database.CreateSystemContext();
        var field = new TDefinition { Name = name, ValueType = type, TenantId = tenant.TenantId, CreatedById = tenant.SystemAccountId, Options = [.. options], State = state };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task AddValuesAsync(CancellationToken cancellationToken, params (Guid User, Guid Field, string Text)[] values)
    {
        await using var system = this.database.CreateSystemContext();
        system.ApplicationUserAttributes.AddRange(values.Select(value => new ApplicationUserAttribute { EntityId = value.User, EntityAttributeDefinitionId = value.Field, StringValue = value.Text }));
        await system.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ValuesOfAsync(Guid field, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ApplicationUserAttributes.CountAsync(value => value.EntityAttributeDefinitionId == field, cancellationToken);
    }

    private async Task<Guid> AddGlobalFieldAsync(ScenarioTenant platform, string name, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var field = new ApplicationUserAttributeDefinition { Name = name, ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId, State = CustomFieldState.Active };
        system.Add(field);
        await system.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task<TDefinition> StoredAsync<TDefinition>(Guid id, CancellationToken cancellationToken) where TDefinition : class, IEntityAttributeDefinition
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Set<TDefinition>().AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }

    private async Task<(ServiceHost Host, IRuntimeExecutionContext Runtime)> ImpersonatingAsync(ScenarioTenant platform, Guid targetUserId, CancellationToken cancellationToken)
    {
        System.Security.Claims.ClaimsPrincipal cookie;
        await using (var endpoint = ServiceHost.Create(this.database, new RuntimeExecutionContext()))
        {
            var started = await endpoint.Get<ImpersonationService>().StartAsync(SessionCircuit.Cookie(platform.OwnerId, AppRoles.Admin), targetUserId, cancellationToken);
            Assert.True(started.IsSuccess, started.Error);
            cookie = started.Value!;
        }

        var (host, runtime) = SessionCircuit.Open(this.database, cookie);
        await host.Get<UserSessionService>().InitializeFromClaimsAsync();
        Assert.True(runtime.Current.IsImpersonating);
        return (host, runtime);
    }
}
