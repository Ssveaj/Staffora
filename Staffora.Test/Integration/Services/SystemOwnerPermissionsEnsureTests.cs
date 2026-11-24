using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Staffora.Core.Entities.Tenancy;
using Staffora.Data.Configurations.Tenancy;
using Staffora.Services.Services;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class SystemOwnerPermissionsEnsureTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public SystemOwnerPermissionsEnsureTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_startup_every_tenants_System_owner_role_holds_every_permission_outside_own_work_and_the_other_presets_are_left_alone()
    {
        var first = await OnboardedScenario.CreateAsync(this.database, Ct, "Older Staffing", activate: true);
        var second = await OnboardedScenario.CreateAsync(this.database, Ct, "Setup Staffing");

        await TakeAwayAsync(first.Roles.SystemOwner, RolePermission.PerformCoordination, RolePermission.JobsView, RolePermission.ResourcesView);
        await TakeAwayAsync(second.Roles.SystemOwner, RolePermission.ViewCoordination);
        await TakeAwayAsync(first.Roles.Coordinator, RolePermission.JobsDelete);

        await using (AppHost.Start(this.database))
        {
        }

        foreach (var onboarded in new[] { first, second })
        {
            var rows = await RowsAsync(onboarded.Roles.SystemOwner);
            Assert.Equal(RolePresets.SystemOwner.Permissions.Order(), rows.Select(row => row.Permission).Order());
            Assert.All(rows, row => Assert.Equal(onboarded.Tenant.SystemAccountId, row.CreatedById));
        }

        Assert.DoesNotContain(RolePermission.JobsDelete, (await RowsAsync(first.Roles.Coordinator)).Select(row => row.Permission));
    }

    [Fact]
    public async Task A_second_run_adds_nothing_and_no_run_takes_a_permission_away()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, "Kept Staffing", activate: true);
        await TakeAwayAsync(onboarded.Roles.SystemOwner, RolePermission.CustomersManage);

        await using (var context = this.database.CreateSystemContext())
        {
            context.TenantRolePermissions.Add(new TenantRolePermission { TenantId = onboarded.TenantId, TenantRoleId = onboarded.Roles.SystemOwner, Permission = RolePermission.ViewOwnSchedule, CreatedById = onboarded.Tenant.SystemAccountId });
            await context.SaveChangesAsync(Ct);
        }

        await using var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime());
        var ensure = host.Get<SystemOwnerPermissionsEnsure>();
        var once = await ensure.EnsureForEveryTenantAsync(Ct);
        Assert.True(once.IsSuccess, once.Error);
        Assert.DoesNotContain(onboarded.TenantId, once.Value!.FailedTenantIds);
        var afterOnce = await RowsAsync(onboarded.Roles.SystemOwner);
        Assert.Equal(RolePresets.SystemOwner.Permissions.Append(RolePermission.ViewOwnSchedule).Order(), afterOnce.Select(row => row.Permission).Order());

        var twice = await ensure.EnsureForEveryTenantAsync(Ct);
        Assert.True(twice.IsSuccess, twice.Error);
        Assert.Equal(afterOnce.Select(row => row.Id).Order(), (await RowsAsync(onboarded.Roles.SystemOwner)).Select(row => row.Id).Order());
    }

    [Fact]
    public async Task Four_ensures_at_once_add_each_permission_once_round_after_round_and_never_touch_the_other_presets()
    {
        var active = await OnboardedScenario.CreateAsync(this.database, Ct, "Racing Staffing", activate: true);
        var suspended = await OnboardedScenario.CreateAsync(this.database, Ct, "Racing Held Staffing", activate: true);
        await MoveTenantAsync(active, suspended.TenantId, "tenant-suspend");

        for (var round = 0; round < 5; round++)
        {
            await TakeAwayAsync(active.Roles.SystemOwner, RolePermission.ViewCoordination, RolePermission.JobsView, RolePermission.ResourcesManage);
            await TakeAwayAsync(suspended.Roles.SystemOwner, RolePermission.ViewCoordination, RolePermission.JobsView, RolePermission.ResourcesManage);
            var coordinatorBefore = (await RowsAsync(active.Roles.Coordinator)).Select(row => row.Id).Order().ToList();
            var administratorBefore = (await RowsAsync(active.Roles.Administrator)).Select(row => row.Id).Order().ToList();

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                await using var host = ServiceHost.Create(this.database, active.OwnerRuntime());
                return await host.Get<SystemOwnerPermissionsEnsure>().EnsureForEveryTenantAsync(Ct);
            })).ToList();
            gate.SetResult();
            var results = await Task.WhenAll(runs);

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error));
            Assert.All(results, result => Assert.DoesNotContain(active.TenantId, result.Value!.FailedTenantIds));
            Assert.All(results, result => Assert.DoesNotContain(suspended.TenantId, result.Value!.FailedTenantIds));
            foreach (var onboarded in new[] { active, suspended })
            {
                var rows = await RowsAsync(onboarded.Roles.SystemOwner);
                Assert.Equal(RolePresets.SystemOwner.Permissions.Order(), rows.Select(row => row.Permission).Order());
                Assert.All(rows, row => Assert.Equal(onboarded.Tenant.SystemAccountId, row.CreatedById));
            }

            Assert.Equal(coordinatorBefore, (await RowsAsync(active.Roles.Coordinator)).Select(row => row.Id).Order());
            Assert.Equal(administratorBefore, (await RowsAsync(active.Roles.Administrator)).Select(row => row.Id).Order());
        }
    }

    [Fact]
    public async Task Setup_Active_and_Suspended_tenants_are_kept_complete_while_a_tenant_scheduled_for_deletion_or_archived_is_left_as_it_is()
    {
        var setup = await OnboardedScenario.CreateAsync(this.database, Ct, "Kept Setup Staffing");
        var suspended = await OnboardedScenario.CreateAsync(this.database, Ct, "Kept Suspended Staffing", activate: true);
        var pending = await OnboardedScenario.CreateAsync(this.database, Ct, "Pending Staffing", activate: true);
        var archived = await OnboardedScenario.CreateAsync(this.database, Ct, "Archived Staffing", activate: true);
        foreach (var onboarded in new[] { setup, suspended, pending, archived })
        {
            await TakeAwayAsync(onboarded.Roles.SystemOwner, RolePermission.PerformCoordination);
        }

        await MoveTenantAsync(setup, suspended.TenantId, "tenant-suspend");
        await MoveTenantAsync(setup, pending.TenantId, "tenant-schedule-deletion");
        await MoveTenantAsync(setup, archived.TenantId, "tenant-schedule-deletion");
        await MoveTenantAsync(setup, archived.TenantId, "tenant-archive");

        await using (var host = ServiceHost.Create(this.database, setup.OwnerRuntime()))
        {
            var ensured = await host.Get<SystemOwnerPermissionsEnsure>().EnsureForEveryTenantAsync(Ct);
            Assert.True(ensured.IsSuccess, ensured.Error);
            Assert.Empty(ensured.Value!.FailedTenantIds);
        }

        Assert.Contains(RolePermission.PerformCoordination, (await RowsAsync(setup.Roles.SystemOwner)).Select(row => row.Permission));
        Assert.Contains(RolePermission.PerformCoordination, (await RowsAsync(suspended.Roles.SystemOwner)).Select(row => row.Permission));
        Assert.DoesNotContain(RolePermission.PerformCoordination, (await RowsAsync(pending.Roles.SystemOwner)).Select(row => row.Permission));
        Assert.DoesNotContain(RolePermission.PerformCoordination, (await RowsAsync(archived.Roles.SystemOwner)).Select(row => row.Permission));
    }

    [Fact]
    public async Task One_query_finds_the_tenants_that_miss_a_permission_and_only_those_take_the_lock()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, "Lean Staffing", activate: true);
        await using var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime());
        var ensure = host.Get<SystemOwnerPermissionsEnsure>();
        Assert.True((await ensure.EnsureForEveryTenantAsync(Ct)).IsSuccess);

        host.ClearCommandLog();
        var idle = await ensure.EnsureForEveryTenantAsync(Ct);
        Assert.Equal((0, 0), (idle.Value!.Added, idle.Value.FailedTenantIds.Count));
        Assert.Single(host.CommandLog);
        Assert.DoesNotContain(host.CommandLog, command => command.Contains("sp_getapplock", StringComparison.Ordinal));

        await TakeAwayAsync(onboarded.Roles.SystemOwner, RolePermission.CustomersView);
        host.ClearCommandLog();
        var one = await ensure.EnsureForEveryTenantAsync(Ct);
        Assert.Equal(1, one.Value!.Added);
        Assert.Single(host.CommandLog, command => command.Contains("sp_getapplock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_permission_another_writer_added_first_rolls_back_that_tenants_attempt_alone_and_the_tenant_is_ensured_again()
    {
        var onboarded = await OnboardedScenario.CreateAsync(this.database, Ct, "Duplicate Staffing", activate: true);
        await TakeAwayAsync(onboarded.Roles.SystemOwner, RolePermission.JobsCancel, RolePermission.CustomersManage);

        await using (var context = this.database.CreateSystemContext())
        {
            context.TenantRolePermissions.Add(new TenantRolePermission { TenantId = onboarded.TenantId, TenantRoleId = onboarded.Roles.SystemOwner, Permission = RolePermission.TenantView, CreatedById = onboarded.Tenant.SystemAccountId });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains(TenantRolePermissionConfiguration.OnePerPermissionIndex, refused.InnerException!.Message, StringComparison.Ordinal);
        }

        var racer = new RacingWriter(onboarded.TenantId, onboarded.Roles.SystemOwner, RolePermission.JobsCancel, onboarded.Tenant.SystemAccountId);
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime(), interceptors: [racer]))
        {
            var ensured = await host.Get<SystemOwnerPermissionsEnsure>().EnsureForEveryTenantAsync(Ct);
            Assert.True(ensured.IsSuccess, ensured.Error);
            Assert.DoesNotContain(onboarded.TenantId, ensured.Value!.FailedTenantIds);
        }

        Assert.Equal(1, racer.Raced);
        var rows = await RowsAsync(onboarded.Roles.SystemOwner);
        Assert.Equal(RolePresets.SystemOwner.Permissions.Order(), rows.Select(row => row.Permission).Order());
    }

    private async Task MoveTenantAsync(OnboardedScenario any, Guid tenantId, string step)
    {
        await using var host = ServiceHost.Create(this.database, any.Platform.PlatformOwnerRuntime());
        var moved = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenantId, step, cancellationToken: Ct);
        Assert.True(moved.IsSuccess, moved.Error);
    }

    private sealed class RacingWriter : SaveChangesInterceptor
    {
        private int raced;
        private readonly Guid tenantId;
        private readonly Guid roleId;
        private readonly RolePermission permission;
        private readonly Guid stamp;

        public RacingWriter(Guid tenantId, Guid roleId, RolePermission permission, Guid stamp)
        {
            this.tenantId = tenantId;
            this.roleId = roleId;
            this.permission = permission;
            this.stamp = stamp;
        }

        public int Raced => Volatile.Read(ref this.raced);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            var adds = context.ChangeTracker.Entries<TenantRolePermission>().Any(entry => entry.State == EntityState.Added && entry.Entity.TenantRoleId == this.roleId && entry.Entity.Permission == this.permission);
            if (adds && Interlocked.CompareExchange(ref this.raced, 1, 0) == 0)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO TenantRolePermissions (Id, TenantRoleId, Permission, TenantId, Created, CreatedById) VALUES ({Guid.NewGuid()}, {this.roleId}, {(int)this.permission}, {this.tenantId}, {DateTime.UtcNow}, {this.stamp})",
                    cancellationToken);
            }

            return result;
        }
    }

    private async Task TakeAwayAsync(Guid roleId, params RolePermission[] permissions)
    {
        await using var context = this.database.CreateSystemContext();
        var rows = await context.TenantRolePermissions.Where(row => row.TenantRoleId == roleId && permissions.Contains(row.Permission)).ToListAsync(Ct);
        Assert.Equal(permissions.Length, rows.Count);
        context.TenantRolePermissions.RemoveRange(rows);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<TenantRolePermission>> RowsAsync(Guid roleId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.TenantRolePermissions.AsNoTracking().Where(row => row.TenantRoleId == roleId).ToListAsync(Ct);
    }
}
