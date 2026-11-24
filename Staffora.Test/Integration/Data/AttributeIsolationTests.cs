using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class AttributeIsolationTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public AttributeIsolationTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_tenant_reads_the_values_of_its_own_rows_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());

        Assert.Equal([$"Secret of {scenario.A.Name}"], await readerA.OrganizationUnitAttributes.Select(value => value.StringValue).ToListAsync(cancellationToken));
        Assert.Empty(await readerA.OrganizationUnitAttributes.Where(value => value.EntityId == b.Unit).Select(value => new { value.StringValue, value.Entity.Name }).ToListAsync(cancellationToken));
        Assert.Empty(await readerA.ApplicationUserAttributes.Where(value => value.EntityId == scenario.B.OwnerId).ToListAsync(cancellationToken));
        Assert.Single(await readerA.ApplicationUserAttributes.Where(value => value.EntityId == scenario.A.OwnerId).ToListAsync(cancellationToken));
        Assert.Single((await readerA.OrganizationUnits.Include(unit => unit.Attributes).SingleAsync(unit => unit.Id == a.Unit, cancellationToken)).Attributes);
    }

    [Fact]
    public async Task A_tenant_reads_the_global_definitions_and_its_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);
        var global = await AddGlobalDefinitionAsync(cancellationToken);

        await using var readerA = this.database.CreateContext(scenario.A.Runtime());
        var seen = await readerA.OrganizationUnitAttributeDefinitions.Select(definition => definition.Id).ToListAsync(cancellationToken);

        Assert.Contains(global, seen);
        Assert.Contains(a.UnitDefinition, seen);
        Assert.DoesNotContain(b.UnitDefinition, seen);
        Assert.DoesNotContain(b.UserDefinition, await readerA.ApplicationUserAttributeDefinitions.Select(definition => definition.Id).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Writing_a_value_of_a_row_of_B_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);
        var global = await AddGlobalDefinitionAsync(cancellationToken);
        var before = await ValuesAsync(cancellationToken);

        var writes = new Func<TestDataContext, object>[]
        {
            context => context.Add(new OrganizationUnitAttribute { EntityId = b.Unit, EntityAttributeDefinitionId = global, StringValue = "Planted" }),
            context => context.Add(new OrganizationUnitAttribute { EntityId = b.Unit, EntityAttributeDefinitionId = a.UnitDefinition, StringValue = "Planted" }),

            context => context.Add(new ApplicationUserAttribute { EntityId = scenario.B.OwnerId, EntityAttributeDefinitionId = a.UserDefinition, StringValue = "Planted" }),

            context => context.Update(new OrganizationUnitAttribute { EntityId = b.Unit, EntityAttributeDefinitionId = b.UnitDefinition, StringValue = "Written from A" }),
            context => context.Remove(new OrganizationUnitAttribute { EntityId = b.Unit, EntityAttributeDefinitionId = b.UnitDefinition }),
            context => context.Remove(new ApplicationUserAttribute { EntityId = scenario.B.OwnerId, EntityAttributeDefinitionId = b.UserDefinition }),
        };

        foreach (var write in writes)
        {
            await using var writerA = this.database.CreateContext(scenario.A.Runtime());
            write(writerA);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(before, await ValuesAsync(cancellationToken));
    }

    [Fact]
    public async Task A_value_may_not_use_a_definition_of_another_tenant_also_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);
        var before = await ValuesAsync(cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Add(new OrganizationUnitAttribute { EntityId = a.Unit, EntityAttributeDefinitionId = b.UnitDefinition, StringValue = "Uses B's field" });

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.Add(new ApplicationUserAttribute { EntityId = scenario.B.OwnerId, EntityAttributeDefinitionId = a.UserDefinition, StringValue = "Uses A's field" });

            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(before, await ValuesAsync(cancellationToken));
    }

    [Fact]
    public async Task A_tenant_writes_no_definition_and_every_one_is_written_in_system_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);
        var global = await AddGlobalDefinitionAsync(cancellationToken);
        var before = await DefinitionsAsync(cancellationToken);

        var refused = new Func<TestDataContext, Task>[]
        {
            context => Task.FromResult(context.Add(Definition("Global from A", tenant: null))),
            context => Task.FromResult(context.Add(Definition("For B from A", scenario.B.TenantId))),
            context => Task.FromResult(context.Add(Definition("Added by its tenant", scenario.A.TenantId))),

            async context => (await context.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == global, cancellationToken)).Name = "Renamed by A",
            async context => context.Remove(await context.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == global, cancellationToken)),

            context => Task.FromResult(context.Update(Definition("Renamed by A", scenario.A.TenantId, b.UnitDefinition))),

            async context => (await context.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == a.UnitDefinition, cancellationToken)).Name = "Renamed by its tenant",
            async context => context.Remove(await context.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == a.UnitDefinition, cancellationToken)),
            async context => (await context.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == a.UnitDefinition, cancellationToken)).TenantId = null,
        };

        foreach (var write in refused)
        {
            await using var writerA = this.database.CreateContext(scenario.A.Runtime());
            await write(writerA);

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        Assert.Equal(before, await DefinitionsAsync(cancellationToken));

        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var system = this.database.CreateSystemContext();
        var renamed = await system.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == a.UnitDefinition, cancellationToken);
        renamed.Name = "Renamed by the platform";
        renamed.ModifiedById = scenario.A.SystemAccountId;
        var added = Definition("Added by the platform for A", scenario.A.TenantId);
        added.CreatedById = scenario.A.SystemAccountId;
        var platformField = Definition("Added by the platform", tenant: null);
        platformField.CreatedById = platform.OwnerId;
        system.AddRange(added, platformField);
        await system.SaveChangesAsync(cancellationToken);
        Assert.Equal(3, await system.OrganizationUnitAttributeDefinitions.CountAsync(row => row.Id == a.UnitDefinition || row.Id == added.Id || row.Id == platformField.Id, cancellationToken));
        Assert.Equal("Renamed by the platform", (await system.OrganizationUnitAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Id == a.UnitDefinition, cancellationToken)).Name);
    }

    [Fact]
    public async Task A_refused_update_of_another_tenants_definition_reads_exactly_like_one_of_a_missing_definition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);
        var before = await DefinitionsAsync(cancellationToken);

        async Task<string> RefusalAsync(Guid id)
        {
            await using var writerA = this.database.CreateContext(scenario.A.Runtime());
            writerA.Update(Definition("Renamed by A", scenario.A.TenantId, id));
            var refusal = await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
            return refusal.Message.Replace(id.ToString(), "{id}", StringComparison.Ordinal);
        }

        Assert.Equal(await RefusalAsync(Guid.NewGuid()), await RefusalAsync(b.UnitDefinition));
        Assert.Equal(before, await DefinitionsAsync(cancellationToken));
    }

    [Fact]
    public async Task Removing_Bs_values_never_removes_a_value_of_A_and_Bs_field_is_never_deleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var a = await SeedAsync(scenario.A, cancellationToken);
        var b = await SeedAsync(scenario.B, cancellationToken);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            writerA.Add(new OrganizationUnitAttribute { EntityId = a.Unit, EntityAttributeDefinitionId = b.UnitDefinition, StringValue = "On B's field" });

            await Assert.ThrowsAsync<TenantIsolationException>(() => writerA.SaveChangesAsync(cancellationToken));
        }

        var before = await ValuesAsync(cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            system.RemoveRange(await system.OrganizationUnitAttributes.Where(row => row.EntityAttributeDefinitionId == b.UnitDefinition).ToListAsync(cancellationToken));
            await system.SaveChangesAsync(cancellationToken);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            system.Remove(await system.OrganizationUnitAttributeDefinitions.SingleAsync(row => row.Id == b.UnitDefinition, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => system.SaveChangesAsync(cancellationToken));
        }

        var after = await ValuesAsync(cancellationToken);
        Assert.Equal(before.Where(value => value.StartsWith($"{a.Unit} ", StringComparison.Ordinal)), after.Where(value => value.StartsWith($"{a.Unit} ", StringComparison.Ordinal)));
        Assert.DoesNotContain(after, value => value.StartsWith($"{b.Unit} ", StringComparison.Ordinal));
    }

    private sealed record Seeded(Guid Unit, Guid UnitDefinition, Guid UserDefinition);

    private async Task<Seeded> SeedAsync(ScenarioTenant tenant, CancellationToken cancellationToken)
    {
        var unitDefinition = Definition("Room", tenant.TenantId);
        unitDefinition.CreatedById = tenant.SystemAccountId;
        var userDefinition = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Badge", ValueType = AttributeValueType.Text, TenantId = tenant.TenantId, CreatedById = tenant.SystemAccountId };
        await using (var system = this.database.CreateSystemContext())
        {
            system.AddRange(unitDefinition, userDefinition);
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var context = this.database.CreateContext(tenant.Runtime());
        var unit = new OrganizationUnit { ParentId = await TwoTenantScenario.RootUnitIdAsync(this.database, tenant, cancellationToken), Name = "Unit", UnitType = OrganizationUnitType.Branch };
        context.Add(unit);
        context.Add(new OrganizationUnitAttribute { EntityId = unit.Id, EntityAttributeDefinitionId = unitDefinition.Id, StringValue = $"Secret of {tenant.Name}" });
        context.Add(new ApplicationUserAttribute { EntityId = tenant.OwnerId, EntityAttributeDefinitionId = userDefinition.Id, StringValue = $"Badge of {tenant.Name}" });
        await context.SaveChangesAsync(cancellationToken);
        return new Seeded(unit.Id, unitDefinition.Id, userDefinition.Id);
    }

    private async Task<Guid> AddGlobalDefinitionAsync(CancellationToken cancellationToken)
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var system = this.database.CreateSystemContext();
        var definition = Definition($"Global field {Guid.NewGuid():N}", tenant: null);
        definition.CreatedById = platform.OwnerId;
        system.Add(definition);
        await system.SaveChangesAsync(cancellationToken);
        return definition.Id;
    }

    private static OrganizationUnitAttributeDefinition Definition(string name, Guid? tenant, Guid id = default) =>
        new() { Id = id, Name = name, ValueType = AttributeValueType.Text, TenantId = tenant, State = CustomFieldState.Active };

    private async Task<List<string>> ValuesAsync(CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var units = await system.OrganizationUnitAttributes.Select(value => $"{value.EntityId} {value.EntityAttributeDefinitionId} {value.StringValue}").ToListAsync(cancellationToken);
        var users = await system.ApplicationUserAttributes.Select(value => $"{value.EntityId} {value.EntityAttributeDefinitionId} {value.StringValue}").ToListAsync(cancellationToken);
        return [.. units.Concat(users).Order(StringComparer.Ordinal)];
    }

    private async Task<List<string>> DefinitionsAsync(CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        var definitions = await system.OrganizationUnitAttributeDefinitions.Select(row => $"{row.Id} {row.Name} {row.TenantId}").ToListAsync(cancellationToken);
        return [.. definitions.Order(StringComparer.Ordinal)];
    }
}
