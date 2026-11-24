using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class TenantLockOrderTests : IClassFixture<RealDatabase>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly RealDatabase database;

    public TenantLockOrderTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_console_unlock_holding_its_locks_a_tenants_holding_removal_and_a_suspension_all_complete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var onboarded = await OnboardedScenario.CreateAsync(this.database, cancellationToken, activate: true);
        var root = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, cancellationToken);
        var locked = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Locked", cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Holder", cancellationToken);
        Guid holdingId;
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            var given = await host.Get<RoleHoldingService>().AssignUserToRoleAsync(holder, root, onboarded.Roles.Coordinator, onboarded.TenantId);
            Assert.True(given.IsSuccess, given.Error);
            holdingId = given.Value!.Id;
        }

        await SetLockoutAsync(locked, DateTimeOffset.UtcNow.AddHours(1));

        var pause = new PauseOnce();
        await using var console = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime(), services => services.AddScoped<IStepLock>(_ => pause));
        await using var session = ServiceHost.Create(this.database, onboarded.OwnerRuntime());
        await using var mover = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime());
        var unlock = Task.Run(() => console.Get<UserService>().StepUserAsync(locked, ApplicationUserWorkflowAttribute.UnlockStep), cancellationToken);
        await pause.Reached.Task.WaitAsync(Wait, cancellationToken);

        var removal = Task.Run(() => session.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holdingId), cancellationToken);
        await WaitForLockWaitAsync("APPLICATION", "WAIT");

        var suspension = Task.Run(() => mover.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(onboarded.TenantId, "tenant-suspend", cancellationToken: cancellationToken), cancellationToken);
        await WaitForLockWaitAsync("KEY", "CONVERT");

        pause.Release.TrySetResult();
        var outcomes = new[] { await Outcome(unlock), await Outcome(removal), await Outcome(suspension) };
        Assert.Equal(["ok", "ok", "ok"], outcomes);

        await using var check = this.database.CreateSystemContext();
        using var lease = check.BeginSystemScope("test: read the outcome of the three");
        Assert.Equal(TenantState.Suspended, await check.Tenants.Where(tenant => tenant.Id == onboarded.TenantId).Select(tenant => tenant.State).SingleAsync(cancellationToken));
        Assert.Null(await check.Users.Where(user => user.Id == locked).Select(user => user.LockoutEnd).SingleAsync(cancellationToken));
        Assert.False(await check.OrganizationUnitUserRoles.AnyAsync(holding => holding.Id == holdingId, cancellationToken));
    }

    [Theory(Timeout = WalkTimeout.Database)]
    [InlineData("structure")]
    [InlineData("comment thread")]
    public async Task An_application_lock_taken_in_system_scope_holds_the_tenants_row_first(string kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var onboarded = await OnboardedScenario.CreateAsync(this.database, cancellationToken, activate: true);
        var tenantId = onboarded.TenantId;
        var resource = kind == "structure" ? AppLock.Structure.Resource(tenantId) : AppLock.CommentThread.Resource(tenantId, CommentThreadWriter.ThreadOf(nameof(ApplicationUser), onboarded.OwnerId));

        await using var mover = new SqlConnection(this.database.ConnectionString);
        await mover.OpenAsync(cancellationToken);
        await using var moving = (SqlTransaction)await mover.BeginTransactionAsync(cancellationToken);
        await using (var update = new SqlCommand("UPDATE [Tenants] SET [IsInTransition] = [IsInTransition] WHERE [Id] = @tenant", mover, moving))
        {
            update.Parameters.AddWithValue("@tenant", tenantId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
        }

        var taking = Task.Run(async () =>
        {
            await using var context = this.database.CreateSystemContext();
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            if (kind == "structure")
            {
                await context.LockOrganizationStructureAsync(tenantId, cancellationToken);
            }
            else
            {
                await CommentThreadWriter.LockAsync(context, tenantId, nameof(ApplicationUser), onboarded.OwnerId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

        await WaitForLockWaitAsync("KEY", "WAIT");
        Assert.False(taking.IsCompleted, "the lock was taken while a step moving the tenant held its row");
        await using (var test = new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND resource_type = 'APPLICATION' AND resource_description LIKE '%' + @resource + '%'", mover, moving))
        {
            test.Parameters.AddWithValue("@resource", resource[..Math.Min(resource.Length, 32)]);
            Assert.Equal(0, (int)(await test.ExecuteScalarAsync(cancellationToken))!);
        }

        await moving.CommitAsync(cancellationToken);
        await taking.WaitAsync(Wait, cancellationToken);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Suspensions_owner_steps_holdings_renames_and_console_unlocks_at_once_never_deadlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var onboarded = await OnboardedScenario.CreateAsync(this.database, cancellationToken, activate: true);
        var root = await TwoTenantScenario.RootUnitIdAsync(this.database, onboarded.Tenant, cancellationToken);
        var second = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Second", cancellationToken);
        var worker = await TwoTenantScenario.AddUserAsync(this.database, onboarded.Tenant, "Worker", cancellationToken);
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            Assert.True((await host.Get<RoleHoldingService>().AssignUserToRoleAsync(second, root, onboarded.Roles.SystemOwner, onboarded.TenantId)).IsSuccess);
        }

        var errors = new ErrorSink();
        var problems = new ConcurrentBag<string>();
        var answers = new ConcurrentDictionary<string, int>();
        ServiceHost Host(Core.Execution.IRuntimeExecutionContext runtime) => ServiceHost.Create(this.database, runtime, services => services.AddLogging(logging => logging.AddProvider(errors)));
        async Task Run(string who, Func<Task<Result>> step)
        {
            try
            {
                var result = await step();
                answers.AddOrUpdate(result.IsSuccess ? $"{who}: ok" : $"{who}: {result.Error}", 1, (_, count) => count + 1);
                if (!result.IsSuccess && IsFailure(result.Error))
                {
                    problems.Add($"{who}: {result.Error}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                problems.Add($"{who} threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        var end = DateTime.UtcNow.AddSeconds(12);
        await Task.WhenAll(
            Task.Run(async () =>
            {
                while (DateTime.UtcNow < end)
                {
                    await using var host = Host(onboarded.Platform.PlatformOwnerRuntime());
                    await Run("suspend", () => host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(onboarded.TenantId, "tenant-suspend", cancellationToken: cancellationToken));
                    await Run("reactivate", () => host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(onboarded.TenantId, "tenant-reactivate", cancellationToken: cancellationToken));
                }
            }, cancellationToken),
            Task.Run(async () =>
            {
                while (DateTime.UtcNow < end)
                {
                    await using var host = Host(onboarded.OwnerRuntime());
                    await Run("new password", () => host.Get<UserService>().StepUserAsync(second, ApplicationUserWorkflowAttribute.NewPasswordStep));
                }
            }, cancellationToken),
            Task.Run(async () =>
            {
                Guid? holding = null;
                while (DateTime.UtcNow < end)
                {
                    await using var host = Host(onboarded.RuntimeOf(second));
                    if (holding is { } given)
                    {
                        await Run("take", async () =>
                        {
                            var taken = await host.Get<RoleHoldingService>().RemoveUserFromRoleAsync(given);
                            holding = taken.IsSuccess ? null : holding;
                            return taken;
                        });
                    }
                    else
                    {
                        await Run("give", async () =>
                        {
                            var added = await host.Get<RoleHoldingService>().AssignUserToRoleAsync(worker, root, onboarded.Roles.Staff, onboarded.TenantId);
                            holding = added.IsSuccess ? added.Value!.Id : null;
                            return added;
                        });
                    }
                }
            }, cancellationToken),
            Task.Run(async () =>
            {
                while (DateTime.UtcNow < end)
                {
                    await using var host = Host(onboarded.OwnerRuntime());
                    await Run("rename", () => host.Get<UserService>().SaveUserAsync(worker, user => user.FirstName = "Worker " + Random.Shared.Next(1000)));
                }
            }, cancellationToken),
            Task.Run(async () =>
            {
                while (DateTime.UtcNow < end)
                {
                    try
                    {
                        await SetLockoutAsync(worker, DateTimeOffset.UtcNow.AddHours(1));
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        continue;
                    }

                    await using var host = Host(onboarded.Platform.PlatformOwnerRuntime());
                    await Run("console unlock", () => host.Get<UserService>().StepUserAsync(worker, ApplicationUserWorkflowAttribute.UnlockStep));
                }
            }, cancellationToken));

        foreach (var answer in answers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{answer.Key} x{answer.Value}");
        }

        Assert.Contains("suspend: ok", answers.Keys);
        Assert.Contains("console unlock: ok", answers.Keys);
        Assert.Contains("new password: ok", answers.Keys);
        Assert.Contains("take: ok", answers.Keys);
        Assert.Empty(problems);
        Assert.Empty(errors.Deadlocks);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Two_unrelated_tenants_creating_units_and_sending_orders_at_once_never_deadlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await PortalScenario.CreateAsync(this.database, "Parallel North", cancellationToken);
        var second = await PortalScenario.CreateAsync(this.database, "Parallel South", cancellationToken);
        var firstOrderer = await first.AddOrdererAsync(this.database, "Olive", cancellationToken);
        var secondOrderer = await second.AddOrdererAsync(this.database, "Owen", cancellationToken);

        var errors = new ErrorSink();
        var problems = new ConcurrentBag<string>();
        var answers = new ConcurrentDictionary<string, int>();
        ServiceHost Host(Core.Execution.IRuntimeExecutionContext runtime) => ServiceHost.Create(this.database, runtime, services => services.AddLogging(logging => logging.AddProvider(errors)));
        async Task Run(string who, Func<Task<Result>> step)
        {
            try
            {
                var result = await step();
                answers.AddOrUpdate(result.IsSuccess ? $"{who}: ok" : $"{who}: {result.Error}", 1, (_, count) => count + 1);
                if (!result.IsSuccess)
                {
                    problems.Add($"{who}: {result.Error}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                problems.Add($"{who} threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        Task Units(PortalScenario scenario, string who, DateTime end) => Task.Run(async () =>
        {
            for (var index = 0; DateTime.UtcNow < end; index++)
            {
                await using var host = Host(scenario.Supplier.OwnerRuntime());
                await Run(who, async () => await host.Get<OrganizationService>().CreateAsync(UnitSteps.Draft($"Branch {index}", Core.Entities.Organization.OrganizationUnitType.Branch, scenario.RootId), cancellationToken));
            }
        }, cancellationToken);

        Task Orders(PortalScenario scenario, Guid orderer, string who, DateTime end) => Task.Run(async () =>
        {
            for (var index = 0; DateTime.UtcNow < end; index++)
            {
                await using var host = Host(scenario.RuntimeOf(orderer));
                var orders = host.Get<Staffora.Services.Portal.OrderService>();
                await Run(who, async () =>
                {
                    var fresh = await orders.NewOrderAsync(cancellationToken);
                    if (!fresh.IsSuccess)
                    {
                        return fresh;
                    }

                    var order = fresh.Value!;
                    (order.Name, order.PeriodStart, order.PeriodEnd, order.Schedule) = ($"Order {index}", new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 1), Core.Entities.Jobs.JobSchedule.Recurring);
                    var saved = await orders.CreateOrderAsync(order, OrderWorkflowAttribute.SaveDraftStep, cancellationToken);
                    if (!saved.IsSuccess)
                    {
                        return saved;
                    }

                    var updater = await orders.LoadOrderUpdaterAsync(saved.Value, cancellationToken);
                    return updater.IsSuccess
                        ? await orders.UpdateOrderAndTransitionAsync(updater.Value!, OrderWorkflowAttribute.SendStep, null, cancellationToken)
                        : updater;
                });
            }
        }, cancellationToken);

        var end = DateTime.UtcNow.AddSeconds(8);
        await Task.WhenAll(
            Units(first, "north unit", end),
            Orders(second, secondOrderer, "south order", end),
            Units(second, "south unit", end),
            Orders(first, firstOrderer, "north order", end));

        foreach (var answer in answers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{answer.Key} x{answer.Value}");
        }

        Assert.Equal(["north order: ok", "north unit: ok", "south order: ok", "south unit: ok"], answers.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(errors.Deadlocks);
        Assert.Empty(problems);
    }

    private static bool IsFailure(string? error) => error is null
        || error.Contains("could not be completed", StringComparison.Ordinal)
        || error.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
        || error.Contains("error occurred", StringComparison.OrdinalIgnoreCase)
        || (error.Contains("could not be saved", StringComparison.Ordinal) && !error.Contains("changed", StringComparison.Ordinal));

    private static async Task<string> Outcome(Task<Result> task)
    {
        try
        {
            var result = await task;
            return result.IsSuccess ? "ok" : "refused: " + result.Error;
        }
        catch (Exception exception)
        {
            return $"threw {exception.GetType().Name}: {exception.Message}";
        }
    }

    private async Task WaitForLockWaitAsync(string resourceType, string status)
    {
        await using var connection = new SqlConnection(this.database.ConnectionString);
        await connection.OpenAsync(Ct);
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id = DB_ID() AND resource_type = @type AND request_status = @status", connection);
            command.Parameters.AddWithValue("@type", resourceType);
            command.Parameters.AddWithValue("@status", status);
            if ((int)(await command.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"No session waited for a {resourceType} lock ({status}) within {Wait}.");
    }

    private async Task SetLockoutAsync(Guid userId, DateTimeOffset until)
    {
        await using var context = this.database.CreateSystemContext();
        var user = await context.Users.SingleAsync(row => row.Id == userId, Ct);
        user.LockoutEnabled = true;
        user.LockoutEnd = until;
        context.NameEditor(user, SystemAccount.IdFor(user.TenantId));
        await context.SaveChangesAsync(Ct);
    }

    private sealed class PauseOnce : IStepLock
    {
        private int reached;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Type EntityType => typeof(ApplicationUser);

        public async Task TakeAsync(ApplicationDbContext context, Guid? entityId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.reached, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(Wait, cancellationToken);
            }
        }
    }

    private sealed class ErrorSink : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Deadlocks { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            for (var inner = exception; inner is not null; inner = inner.InnerException)
            {
                if (inner is SqlException { Number: 1205 } || inner.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
                {
                    Deadlocks.Enqueue($"{formatter(state, exception)}: {inner.Message}");
                    return;
                }
            }
        }
    }
}
