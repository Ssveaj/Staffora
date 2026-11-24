using Microsoft.EntityFrameworkCore;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class AuditStampingTests : IClassFixture<RealDatabase>
{
    private static readonly DateTimeOffset morning = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private readonly RealDatabase database;

    public AuditStampingTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task An_insert_is_stamped_with_the_fake_clock_and_the_signed_in_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);

        var id = await InsertAsync(scenario.A.Runtime(), clock, "Stamped", cancellationToken);

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(morning.UtcDateTime, stored.Created);
        Assert.Equal(scenario.A.OwnerId, stored.CreatedById);
        Assert.Equal(scenario.A.TenantId, stored.TenantId);
        Assert.Null(stored.Modified);
        Assert.Null(stored.ModifiedById);
        Assert.Null(stored.ImpersonatedById);
    }

    [Fact]
    public async Task An_update_stamps_Modified_from_the_fake_clock_and_ModifiedById_with_the_editing_user_and_keeps_the_creation_stamp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var editor = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Editor", cancellationToken);
        var clock = new FakeClock(morning);
        var id = await InsertAsync(scenario.A.Runtime(), clock, "Edited later", cancellationToken);

        clock.Advance(TimeSpan.FromHours(3));
        await using (var context = this.database.CreateContext(scenario.A.RuntimeOf(editor), clock))
        {
            var parent = await context.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = "Edited";

            parent.Created = morning.UtcDateTime.AddYears(-1);
            parent.CreatedById = editor;
            await context.SaveChangesAsync(cancellationToken);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(morning.AddHours(3).UtcDateTime, stored.Modified);
        Assert.Equal(editor, stored.ModifiedById);
        Assert.Equal(morning.UtcDateTime, stored.Created);
        Assert.Equal(scenario.A.OwnerId, stored.CreatedById);
    }

    [Fact]
    public async Task A_row_written_while_impersonating_records_the_impersonated_user_as_creator_and_the_platform_owner_as_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);

        var impersonating = new RuntimeExecutionContext(platform.TenantId, platform.OwnerId);
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);

        var id = await InsertAsync(impersonating, new FakeClock(morning), "Written as A's owner", cancellationToken);

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(scenario.A.TenantId, stored.TenantId);
        Assert.Equal(scenario.A.OwnerId, stored.CreatedById);
        Assert.Equal(platform.OwnerId, stored.ImpersonatedById);

        await using var reader = this.database.CreateContext(impersonating);
        Assert.Equal([id], await reader.TestParents.Select(row => row.Id).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task A_creator_and_creation_time_set_by_the_caller_are_kept_and_record_no_impersonator()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var created = morning.UtcDateTime.AddDays(-2);
        Guid id;

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = new TestParent { Name = "On B's behalf", TenantId = scenario.B.TenantId, CreatedById = scenario.B.OwnerId, Created = created };
            system.TestParents.Add(parent);
            await system.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(created, stored.Created);
        Assert.Equal(scenario.B.OwnerId, stored.CreatedById);
        Assert.Null(stored.ImpersonatedById);
    }

    [Fact]
    public async Task An_editor_set_by_the_caller_is_kept_and_stamped_at_the_clock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var clock = new FakeClock(morning);
        var id = await InsertAsync(scenario.B.Runtime(), clock, "Updated on B's behalf", cancellationToken);
        var onBehalfOf = await TwoTenantScenario.AddUserAsync(this.database, scenario.B, "Stand-in", cancellationToken);

        clock.Advance(TimeSpan.FromMinutes(5));
        await using (var system = this.database.CreateContext(scenario.A.Runtime(), clock))
        using (system.BeginSystemScope("test: update on B's behalf"))
        {
            var parent = await system.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = "Updated";
            parent.ModifiedById = onBehalfOf;
            await system.SaveChangesAsync(cancellationToken);
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Equal(onBehalfOf, stored.ModifiedById);
        Assert.Equal(morning.AddMinutes(5).UtcDateTime, stored.Modified);
    }

    [Fact]
    public async Task Outside_system_scope_a_caller_set_creator_editor_or_impersonator_is_overwritten_with_the_runtimes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var colleague = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Colleague", cancellationToken);
        Guid id;

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = new TestParent { Name = "Forged on insert", CreatedById = colleague, ImpersonatedById = scenario.B.OwnerId, ModifiedById = scenario.B.OwnerId };
            writerA.TestParents.Add(parent);
            await writerA.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        var inserted = await LoadAsync(id, cancellationToken);
        Assert.Equal(scenario.A.OwnerId, inserted.CreatedById);
        Assert.Null(inserted.ImpersonatedById);
        Assert.Null(inserted.ModifiedById);

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime()))
        {
            var parent = await writerA.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = "Forged on update";
            parent.ModifiedById = colleague;
            await writerA.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal(scenario.A.OwnerId, (await LoadAsync(id, cancellationToken)).ModifiedById);
    }

    [Fact]
    public async Task Outside_system_scope_a_caller_set_creation_time_is_overwritten_with_the_clock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var backdated = morning.UtcDateTime.AddYears(-10);
        Guid id;

        await using (var writerA = this.database.CreateContext(scenario.A.Runtime(), new FakeClock(morning)))
        {
            var parent = new TestParent { Name = "Backdated on insert", Created = backdated };
            writerA.TestParents.Add(parent);
            await writerA.SaveChangesAsync(cancellationToken);
            id = parent.Id;
        }

        Assert.Equal(morning.UtcDateTime, (await LoadAsync(id, cancellationToken)).Created);
    }

    [Fact]
    public async Task In_system_scope_a_creator_from_another_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            system.TestParents.Add(new TestParent { Name = "Created in B by A", TenantId = scenario.B.TenantId, CreatedById = scenario.A.OwnerId });

            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using (var context = this.database.CreateContext(scenario.A.Runtime()))
        using (context.BeginSystemScope("test: insert into B as A's user"))
        {
            context.TestParents.Add(new TestParent { Name = "Stamped in B with A's user", TenantId = scenario.B.TenantId });

            await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.TestParents.AnyAsync(row => row.TenantId == scenario.B.TenantId, cancellationToken));
    }

    [Fact]
    public async Task In_system_scope_an_editor_from_another_tenant_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var id = await InsertAsync(scenario.B.Runtime(), new FakeClock(morning), "Edited across the boundary", cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            var parent = await system.TestParents.SingleAsync(row => row.Id == id, cancellationToken);
            parent.Notes = "Edited by A's owner";
            parent.ModifiedById = scenario.A.OwnerId;

            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        var stored = await LoadAsync(id, cancellationToken);
        Assert.Null(stored.Notes);
        Assert.Null(stored.ModifiedById);
    }

    [Fact]
    public async Task An_insert_with_no_user_to_stamp_as_creator_is_refused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var system = this.database.CreateSystemContext())
        {
            system.TestParents.Add(new TestParent { Name = "Nobody wrote this", TenantId = scenario.A.TenantId });

            await Assert.ThrowsAsync<InvalidOperationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.TestParents.AnyAsync(row => row.TenantId == scenario.A.TenantId, cancellationToken));
    }

    private async Task<Guid> InsertAsync(IRuntimeExecutionContext runtime, TimeProvider clock, string name, CancellationToken cancellationToken)
    {
        await using var context = this.database.CreateContext(runtime, clock);
        var parent = new TestParent { Name = name };
        context.TestParents.Add(parent);
        await context.SaveChangesAsync(cancellationToken);
        return parent.Id;
    }

    private async Task<TestParent> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.TestParents.AsNoTracking().SingleAsync(row => row.Id == id, cancellationToken);
    }
}
