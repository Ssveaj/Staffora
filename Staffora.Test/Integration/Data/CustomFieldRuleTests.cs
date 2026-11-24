using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class CustomFieldRuleTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public CustomFieldRuleTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_definition_written_past_the_service_is_held_to_its_rules()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var team = await AddAsync(scenario.A, "Team", AttributeValueType.Choice, cancellationToken, new("working", "Working"), new("planning", "Planning"));
        var badge = await AddAsync(scenario.A, "Badge", AttributeValueType.Text, cancellationToken);

        var refused = new (string Reason, Func<ApplicationDbContext, Task> Write)[]
        {
            ("A saved field keeps its type: Team holds choice values. Add a new field for another kind of value.", async context =>
            {
                var field = await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == team, cancellationToken);
                field.ValueType = AttributeValueType.Text;
                field.Options = [];
                field.ModifiedById = scenario.A.SystemAccountId;
            }),
            ("The option \"Planning\" cannot be removed, since values may hold it. Retire it instead.", async context =>
            {
                var field = await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == team, cancellationToken);
                field.Options = [new("working", "Working")];
                field.ModifiedById = scenario.A.SystemAccountId;
            }),

            ("The option \"Planning\" cannot be removed, since values may hold it. Retire it instead.", context =>
            {
                var stub = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Id = team, Name = "Team", ValueType = AttributeValueType.Choice, TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId, ModifiedById = scenario.A.SystemAccountId, Options = [new("working", "Working")] };
                context.Update(stub);

                context.Entry(stub).Property(row => row.ModifiedById).OriginalValue = null;
                return Task.CompletedTask;
            }),
            ("Only a choice field has options.", async context =>
            {
                var field = await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == badge, cancellationToken);
                field.Options = [new("a", "A")];
                field.ModifiedById = scenario.A.SystemAccountId;
            }),
            ("A custom field needs a name.", context =>
            {
                context.Add(new OrganizationUnitAttributeDefinition { State = CustomFieldState.Active, Name = " ", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId });
                return Task.CompletedTask;
            }),
            ("Choose the kind of value the field holds.", context =>
            {
                context.Add(new OrganizationUnitAttributeDefinition { State = CustomFieldState.Active, Name = "Settings", ValueType = (AttributeValueType)5, TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId });
                return Task.CompletedTask;
            }),
        };

        foreach (var (reason, write) in refused)
        {
            await using var context = this.database.CreateSystemContext();
            await write(context);
            Assert.Equal(reason, (await Assert.ThrowsAsync<CustomFieldException>(() => context.SaveChangesAsync(cancellationToken))).Message);
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.ApplicationUserAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Id == team, cancellationToken);
        Assert.Equal(AttributeValueType.Choice, stored.ValueType);
        Assert.Equal([new("working", "Working"), new("planning", "Planning")], stored.Options);
        Assert.Empty((await check.ApplicationUserAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Id == badge, cancellationToken)).Options);
    }

    [Fact]
    public async Task A_definition_is_written_only_in_system_scope_and_stamped_only_with_a_user_of_its_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

        await using (var context = this.database.CreateContext(scenario.A.Runtime(), clock))
        {
            context.Add(new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Desk by A", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId });
            Assert.Contains("A definition is written only in system scope.", (await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken))).Message, StringComparison.Ordinal);
        }

        var field = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = "Desk", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId };
        await using (var context = this.database.CreateContext(scenario.A.Runtime(), clock))
        {
            using var lease = context.BeginSystemScope("test: the platform's write");
            context.Add(field);
            await context.SaveChangesAsync(cancellationToken);
        }

        var stored = await StoredAsync(field.Id, cancellationToken);
        Assert.Equal((scenario.A.OwnerId, clock.GetUtcNow().UtcDateTime, (DateTime?)null, (Guid?)null), (stored.CreatedById, stored.Created, stored.Modified, stored.ModifiedById));

        await using (var context = this.database.CreateContext(scenario.A.Runtime(), clock))
        {
            var edited = await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == field.Id, cancellationToken);
            edited.Name = "Desk by a colleague";
            Assert.Contains("A definition is written only in system scope.", (await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken))).Message, StringComparison.Ordinal);
        }

        clock.Advance(TimeSpan.FromHours(1));
        await using (var context = this.database.CreateContext(scenario.A.Runtime(), clock))
        {
            using var lease = context.BeginSystemScope("test: the platform's write");
            var edited = await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == field.Id, cancellationToken);
            edited.Name = "Desk number";
            edited.CreatedById = colleague;
            edited.ModifiedById = colleague;
            await context.SaveChangesAsync(cancellationToken);
        }

        stored = await StoredAsync(field.Id, cancellationToken);
        Assert.Equal(("Desk number", scenario.A.OwnerId, (DateTime?)clock.GetUtcNow().UtcDateTime, (Guid?)colleague), (stored.Name, stored.CreatedById, stored.Modified, stored.ModifiedById));

        var foreignWriters = new (ApplicationUserAttributeDefinition Field, string Reason)[]
        {
            (new() { Name = "Of A by B", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.B.OwnerId }, "is not a user of the row's tenant"),
            (new() { Name = "Of A by the platform", ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = platform.OwnerId }, "is not a user of the row's tenant"),
            (new() { Name = $"Global by A {Guid.NewGuid():N}", ValueType = AttributeValueType.Text, CreatedById = scenario.A.OwnerId }, "is not a user of the platform tenant"),
        };
        foreach (var (foreign, reason) in foreignWriters)
        {
            await using var system = this.database.CreateSystemContext();
            system.Add(foreign);
            Assert.Contains(reason, (await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken))).Message, StringComparison.Ordinal);
        }

        var global = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = $"Global by the platform {Guid.NewGuid():N}", ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId };
        var ofA = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = $"Of A by its account {Guid.NewGuid():N}"[..30], ValueType = AttributeValueType.Text, TenantId = scenario.A.TenantId, CreatedById = scenario.A.SystemAccountId };
        await using (var system = this.database.CreateSystemContext())
        {
            system.AddRange(global, ofA);
            await system.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal((platform.OwnerId, scenario.A.SystemAccountId), ((await StoredAsync(global.Id, cancellationToken)).CreatedById, (await StoredAsync(ofA.Id, cancellationToken)).CreatedById));
        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.ApplicationUserAttributeDefinitions.AnyAsync(row => row.Name == "Desk by A" || row.Name == "Desk by a colleague" || row.Name.StartsWith("Of A by B") || row.Name == "Of A by the platform" || row.Name.StartsWith("Global by A"), cancellationToken));
    }

    [Fact]
    public async Task A_retired_option_is_judged_against_the_stored_value_past_the_services()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        var team = await AddAsync(scenario.A, "Team", AttributeValueType.Choice, cancellationToken, new("working", "Working"), new("support", "Support"));
        const string retired = "Team's option \"Support\" is retired. Choose another.";

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Add(new ApplicationUserAttribute { EntityId = colleague, EntityAttributeDefinitionId = team, StringValue = "support" });
            context.Add(new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = team, StringValue = "working" });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = this.database.CreateSystemContext())
        {
            var field = await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == team, cancellationToken);
            field.Options = [new("working", "Working"), new("support", "Support", IsRetired: true)];
            field.ModifiedById = scenario.A.SystemAccountId;
            await context.SaveChangesAsync(cancellationToken);
        }

        var refused = new Func<ApplicationDbContext, Task>[]
        {
            async context =>
            {
                var other = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Newcomer", cancellationToken);
                context.Add(new ApplicationUserAttribute { EntityId = other, EntityAttributeDefinitionId = team, StringValue = "support" });
            },

            async context => (await context.ApplicationUserAttributes.SingleAsync(row => row.EntityId == scenario.A.OwnerId && row.EntityAttributeDefinitionId == team, cancellationToken)).StringValue = "support",

            context =>
            {
                var stub = new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = team, StringValue = "support" };
                context.Attach(stub);
                context.Entry(stub).Property(row => row.StringValue).IsModified = true;
                return Task.CompletedTask;
            },
        };
        foreach (var write in refused)
        {
            await using var context = this.database.CreateContext(scenario.A.Runtime());
            await write(context);
            Assert.Equal(retired, (await Assert.ThrowsAsync<CustomFieldException>(() => context.SaveChangesAsync(cancellationToken))).Message);
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Update(new ApplicationUserAttribute { EntityId = colleague, EntityAttributeDefinitionId = team, StringValue = "support" });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var system = this.database.CreateSystemContext())
        {
            var held = await system.ApplicationUserAttributes.SingleAsync(row => row.EntityId == colleague && row.EntityAttributeDefinitionId == team, cancellationToken);
            system.Entry(held).Property(row => row.StringValue).IsModified = true;
            await system.SaveChangesAsync(cancellationToken);
        }

        await using var check = this.database.CreateSystemContext();
        var stored = await check.ApplicationUserAttributes.AsNoTracking().Where(row => row.EntityAttributeDefinitionId == team).ToDictionaryAsync(row => row.EntityId, row => row.StringValue, cancellationToken);
        Assert.Equal(new Dictionary<Guid, string?> { [colleague] = "support", [scenario.A.OwnerId] = "working" }, stored);
    }

    [Fact]
    public async Task A_value_row_that_holds_nothing_is_refused_past_the_services()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var badge = await AddAsync(scenario.A, "Badge", AttributeValueType.Text, cancellationToken);
        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Add(new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = badge, StringValue = "B-1" });
            await context.SaveChangesAsync(cancellationToken);
        }

        var empty = new Func<ApplicationDbContext, Task>[]
        {
            async context => context.Add(new ApplicationUserAttribute { EntityId = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Blank", cancellationToken), EntityAttributeDefinitionId = badge }),
            async context => context.Add(new ApplicationUserAttribute { EntityId = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Spaces", cancellationToken), EntityAttributeDefinitionId = badge, StringValue = "   " }),
            async context => (await context.ApplicationUserAttributes.SingleAsync(row => row.EntityAttributeDefinitionId == badge, cancellationToken)).StringValue = null,
            async context => (await context.ApplicationUserAttributes.SingleAsync(row => row.EntityAttributeDefinitionId == badge, cancellationToken)).StringValue = " ",
        };
        foreach (var write in empty)
        {
            await using var context = this.database.CreateContext(scenario.A.Runtime());
            await write(context);
            Assert.Equal(AttributeValueRules.EmptyRow, (await Assert.ThrowsAsync<CustomFieldException>(() => context.SaveChangesAsync(cancellationToken))).Message);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(["B-1"], await check.ApplicationUserAttributes.Where(row => row.EntityAttributeDefinitionId == badge).Select(row => row.StringValue).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_field_that_holds_values_is_never_deleted_by_a_database_cascade()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var badge = await AddAsync(scenario.A, "Badge", AttributeValueType.Text, cancellationToken);
        var global = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = $"Global badge {Guid.NewGuid():N}", ValueType = AttributeValueType.Text, CreatedById = platform.OwnerId };
        await using (var system = this.database.CreateSystemContext())
        {
            system.Add(global);
            await system.SaveChangesAsync(cancellationToken);
            system.AddRange(
                new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = badge, StringValue = "B-1" },
                new ApplicationUserAttribute { EntityId = scenario.A.OwnerId, EntityAttributeDefinitionId = global.Id, StringValue = "G-A" },
                new ApplicationUserAttribute { EntityId = scenario.B.OwnerId, EntityAttributeDefinitionId = global.Id, StringValue = "G-B" });
            await system.SaveChangesAsync(cancellationToken);
        }

        Assert.Contains(ModelUnderTestForeignKeys(), foreignKey => foreignKey == "ApplicationUserAttribute->ApplicationUserAttributeDefinition: NoAction");

        foreach (var field in new[] { global.Id, badge })
        {
            await using var context = this.database.CreateSystemContext();
            context.Remove(await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == field, cancellationToken));
            await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        {
            context.Remove(await context.ApplicationUserAttributeDefinitions.SingleAsync(row => row.Id == badge, cancellationToken));
            await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(2, await check.ApplicationUserAttributeDefinitions.CountAsync(row => row.Id == badge || row.Id == global.Id, cancellationToken));
        Assert.Equal(3, await check.ApplicationUserAttributes.CountAsync(row => row.EntityAttributeDefinitionId == badge || row.EntityAttributeDefinitionId == global.Id, cancellationToken));
    }

    private IEnumerable<string> ModelUnderTestForeignKeys()
    {
        using var context = this.database.CreateSystemContext();
        return context.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetForeignKeys())
            .Where(foreignKey => typeof(IEntityAttributeDefinition).IsAssignableFrom(foreignKey.PrincipalEntityType.ClrType) && typeof(IEntityAttributeValue).IsAssignableFrom(foreignKey.DeclaringEntityType.ClrType))
            .Select(foreignKey => $"{foreignKey.DeclaringEntityType.ClrType.Name}->{foreignKey.PrincipalEntityType.ClrType.Name}: {foreignKey.DeleteBehavior}")
            .ToList();
    }

    private async Task<Guid> AddAsync(ScenarioTenant tenant, string name, AttributeValueType type, CancellationToken cancellationToken, params AttributeChoiceOption[] options)
    {
        await using var context = this.database.CreateSystemContext();
        var field = new ApplicationUserAttributeDefinition { State = CustomFieldState.Active, Name = name, ValueType = type, TenantId = tenant.TenantId, CreatedById = tenant.SystemAccountId, Options = [.. options] };
        context.Add(field);
        await context.SaveChangesAsync(cancellationToken);
        return field.Id;
    }

    private async Task<ApplicationUserAttributeDefinition> StoredAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.ApplicationUserAttributeDefinitions.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }
}
