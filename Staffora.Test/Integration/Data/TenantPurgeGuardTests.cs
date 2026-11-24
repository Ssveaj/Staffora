using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Data;

[Trait("Category", "Integration")]
public class TenantPurgeGuardTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public TenantPurgeGuardTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(TenantState.Setup)]
    [InlineData(TenantState.Active)]
    [InlineData(TenantState.Suspended)]
    [InlineData(TenantState.Archived)]
    public async Task A_tenant_that_is_not_scheduled_for_deletion_is_never_purged_and_keeps_its_rows(TenantState state)
    {
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, $"Purge {state}", Ct, state);
        var before = await UsersOfAsync(tenant.TenantId);

        await using var system = this.database.CreateSystemContext();
        await using var transaction = await system.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
        var refused = await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.BeginAsync(system, tenant.TenantId, Ct));
        Assert.Contains(TenantPurge.NotPendingDeletionReason, refused.Message, StringComparison.Ordinal);
        Assert.Null(system.PurgeLease);
        await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.DeleteBatchAsync(system, UsersStep(system), tenant.TenantId, 10, Ct));
        await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.DeleteMessagesToBatchAsync(system, tenant.TenantId, 10, Ct));
        await transaction.RollbackAsync(Ct);

        Assert.Equal(before, await UsersOfAsync(tenant.TenantId));
    }

    [Fact]
    public async Task The_platform_tenant_is_never_purged_even_when_stored_as_scheduled_for_deletion()
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, Ct);
        await SetStateAsync(platform.TenantId, TenantState.PendingDeletion);
        try
        {
            await using var system = this.database.CreateSystemContext();
            await using var transaction = await system.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
            await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.BeginAsync(system, platform.TenantId, Ct));
        }
        finally
        {
            await SetStateAsync(platform.TenantId, TenantState.Active);
        }
    }

    [Fact]
    public async Task The_purge_opens_only_in_system_scope_inside_a_transaction_and_one_tenant_at_a_time()
    {
        var first = await TwoTenantScenario.AddTenantAsync(this.database, "Purge First", Ct, TenantState.PendingDeletion);
        var second = await TwoTenantScenario.AddTenantAsync(this.database, "Purge Second", Ct, TenantState.PendingDeletion);

        await using (var tenantContext = this.database.CreateContext(first.Runtime()))
        {
            await using var transaction = await tenantContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
            await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.BeginAsync(tenantContext, first.TenantId, Ct));
        }

        await using (var outsideTransaction = this.database.CreateSystemContext())
        {
            await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.BeginAsync(outsideTransaction, first.TenantId, Ct));
        }

        await using var system = this.database.CreateSystemContext();
        await using (var transaction = await system.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct))
        {
            using (await TenantPurge.BeginAsync(system, first.TenantId, Ct))
            {
                await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.BeginAsync(system, second.TenantId, Ct));
                await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.DeleteBatchAsync(system, UsersStep(system), second.TenantId, 10, Ct));
            }

            Assert.Null(system.PurgeLease);
            await Assert.ThrowsAsync<TenantIsolationException>(() => TenantPurge.DeleteBatchAsync(system, UsersStep(system), first.TenantId, 10, Ct));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(2, (await UsersOfAsync(first.TenantId)).Count);
        Assert.Equal(2, (await UsersOfAsync(second.TenantId)).Count);
    }

    [Fact]
    public async Task A_bulk_delete_tagged_as_a_purge_is_refused_outside_the_purge()
    {
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Purge Tagged", Ct, TenantState.PendingDeletion);
        var parent = new TestParent { Name = "Kept" };
        await using (var writer = this.database.CreateSystemContext())
        {
            parent.TenantId = tenant.TenantId;
            parent.CreatedById = tenant.OwnerId;
            writer.TestParents.Add(parent);
            await writer.SaveChangesAsync(Ct);
        }

        await using var system = this.database.CreateSystemContext();
        await Assert.ThrowsAsync<Staffora.Data.WorkflowFieldEditException>(() => system.TestParents.TagWith(TenantPurge.Tag).Where(row => row.TenantId == tenant.TenantId).ExecuteDeleteAsync(Ct));

        Assert.True(await system.TestParents.AnyAsync(row => row.Id == parent.Id, Ct));
    }

    [Fact]
    public async Task A_statement_tagged_as_a_purge_runs_only_in_the_purges_transaction_and_naming_its_tenant()
    {
        var tenant = await TwoTenantScenario.AddTenantAsync(this.database, "Purge Statement", Ct, TenantState.PendingDeletion);
        var other = await TwoTenantScenario.AddTenantAsync(this.database, "Purge Statement Other", Ct);

        await using var system = this.database.CreateSystemContext();
        using var stray = new SqlCommand($"-- {TenantPurge.Tag}\nDELETE FROM [Nothing]");
        Assert.Throws<TenantIsolationException>(() => TenantPurge.JudgeStatement(system, stray));

        await using var transaction = await system.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
        using (await TenantPurge.BeginAsync(system, tenant.TenantId, Ct))
        {
            using var otherTransaction = new SqlCommand($"-- {TenantPurge.Tag}\nDELETE FROM [Nothing] WHERE [TenantId] = @tenant");
            otherTransaction.Parameters.AddWithValue("@tenant", tenant.TenantId);
            Assert.Throws<TenantIsolationException>(() => TenantPurge.JudgeStatement(system, otherTransaction));

            using var otherTenant = new SqlCommand($"-- {TenantPurge.Tag}\nDELETE FROM [Nothing] WHERE [TenantId] = @tenant") { Transaction = (SqlTransaction)transaction.GetDbTransaction() };
            otherTenant.Parameters.AddWithValue("@tenant", other.TenantId);
            Assert.Throws<TenantIsolationException>(() => TenantPurge.JudgeStatement(system, otherTenant));

            using var named = new SqlCommand($"-- {TenantPurge.Tag}\nDELETE FROM [Nothing] WHERE [TenantId] = @tenant") { Transaction = (SqlTransaction)transaction.GetDbTransaction() };
            named.Parameters.AddWithValue("@tenant", tenant.TenantId);
            TenantPurge.JudgeStatement(system, named);

            using var untagged = new SqlCommand("DELETE FROM [Nothing]");
            TenantPurge.JudgeStatement(system, untagged);
        }

        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task An_open_purge_removes_its_tenants_rows_and_messages_sent_to_it_and_nothing_of_another_tenant()
    {
        var scenario = await TwoTenantScenario.CreateAsync(this.database, Ct);
        await SetStateAsync(scenario.A.TenantId, TenantState.PendingDeletion);
        var otherUsers = await UsersOfAsync(scenario.B.TenantId);

        await using var system = this.database.CreateSystemContext();
        await using var transaction = await system.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
        var removed = 0;
        using (await TenantPurge.BeginAsync(system, scenario.A.TenantId, Ct))
        {
            foreach (var step in TenantPurge.PlanFor(system.Model))
            {
                int batch;
                do
                {
                    batch = await TenantPurge.DeleteBatchAsync(system, step, scenario.A.TenantId, 1, Ct);
                    removed += batch;
                }
                while (batch > 0);
            }
        }

        await transaction.CommitAsync(Ct);

        Assert.True(removed >= 3, $"{removed} rows removed");
        Assert.Empty(await UsersOfAsync(scenario.A.TenantId));
        Assert.Equal(otherUsers, await UsersOfAsync(scenario.B.TenantId));
        await using var check = this.database.CreateSystemContext();
        Assert.False(await check.OrganizationUnits.AnyAsync(unit => unit.TenantId == scenario.A.TenantId, Ct));
        Assert.True(await check.OrganizationUnits.AnyAsync(unit => unit.TenantId == scenario.B.TenantId, Ct));
        Assert.True(await check.Tenants.AnyAsync(row => row.Id == scenario.A.TenantId, Ct));
    }

    private static TenantPurgeStep UsersStep(ApplicationDbContext context) =>
        TenantPurge.PlanFor(context.Model).Single(step => step.ClrType == typeof(Staffora.Core.Entities.Identity.ApplicationUser));

    private async Task SetStateAsync(Guid tenantId, TenantState state)
    {
        await using var system = this.database.CreateSystemContext();
        await system.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tenants SET State = {(int)state} WHERE Id = {tenantId}", Ct);
    }

    private async Task<List<Guid>> UsersOfAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.Where(user => user.TenantId == tenantId).OrderBy(user => user.Id).Select(user => user.Id).ToListAsync(Ct);
    }
}
