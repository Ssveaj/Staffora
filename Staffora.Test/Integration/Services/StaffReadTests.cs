using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class StaffReadTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public StaffReadTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reader_of_staff_lists_the_tenants_staff_members_alone_with_their_units_and_others_are_refused()
    {
        var ours = await StaffScenario.CreateAsync(this.database, Ct, "Ours Staff");
        var theirs = await StaffScenario.CreateAsync(this.database, Ct, "Theirs Staff");
        var ida = await ours.AddPersonAsync(this.database, "Ida", Ct);
        var jon = await theirs.AddPersonAsync(this.database, "Jon", Ct);
        var viewer = await ours.AddPersonAsync(this.database, "Viewer", Ct);
        await using (var owner = ServiceHost.Create(this.database, ours.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(ida, ours.TeamId, ours.Onboarded.Roles.Staff, ours.TenantId)).IsSuccess);
        }

        await using (var owner = ServiceHost.Create(this.database, theirs.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(jon, theirs.TeamId, theirs.Onboarded.Roles.Staff, theirs.TenantId)).IsSuccess);
        }

        await TwoTenantScenario.GrantAsync(this.database, ours.Onboarded.Tenant, viewer, Ct, RolePermission.ResourcesView);
        await using (var host = ServiceHost.Create(this.database, ours.Onboarded.RuntimeOf(viewer)))
        {
            var list = await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery(), Ct);
            Assert.True(list.IsSuccess, list.Error);
            Assert.Equal([ida], list.Value!.Rows.Select(staff => staff.UserId));
            Assert.Equal(["Team North"], list.Value.Rows.Single().Units);
        }

        await using (var host = ServiceHost.Create(this.database, ours.Onboarded.RuntimeOf(ida)))
        {
            Assert.Equal(ResourceService.NoResourcesView, (await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery(), Ct)).Error);
        }
    }

    [Fact]
    public async Task A_staff_read_that_fails_in_the_database_answers_a_logged_failure_and_a_deadlock_victim_is_read_once_more()
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, "Failing Staff");
        var onboarded = scenario.Onboarded;
        var cy = await scenario.AddPersonAsync(this.database, "Cy", Ct);
        await using (var owner = ServiceHost.Create(this.database, onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(cy, scenario.TeamId, onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        var failing = new FailingStaffReads(failures: int.MaxValue, deadlock: false);
        var log = new LogSink();
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime(), services => services.AddLogging(logging => logging.AddProvider(log)), [failing]))
        {
            var users = host.Get<UserService>();
            Assert.Equal((false, ResourceService.ReadFailed), Answer(await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery(), Ct)));
            Assert.Equal((false, ResourceService.ReadFailed), Answer(await users.QueryApplicationUserStaffUnitsAsync(cy, new ListQuery(), Ct)));
            Assert.Equal((false, ResourceService.ReadFailed), Answer(await users.GetApplicationUserEditorOptionsAsync(cy, Ct)));
        }

        Assert.Equal(3, log.Entries.Count(entry => entry.Level == LogLevel.Error && entry.Category == typeof(ResourceService).FullName && entry.Exception is InvalidOperationException));

        var deadlocked = new FailingStaffReads(failures: 1, deadlock: true);
        var retries = new LogSink();
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime(), services => services.AddLogging(logging => logging.AddProvider(retries)), [deadlocked]))
        {
            var found = await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery(), Ct);
            Assert.True(found.IsSuccess, found.Error);
            Assert.Equal([cy], found.Value!.Rows.Select(staff => staff.UserId));
        }

        Assert.Equal(1, deadlocked.Thrown);
        Assert.Single(retries.Entries, entry => entry.Level == LogLevel.Warning && entry.Category == typeof(ResourceService).FullName && IsDeadlock(entry.Exception));
        Assert.DoesNotContain(retries.Entries, entry => entry.Level == LogLevel.Error && entry.Category == typeof(ResourceService).FullName);

        var twice = new FailingStaffReads(failures: 2, deadlock: true);
        await using (var host = ServiceHost.Create(this.database, onboarded.OwnerRuntime(), interceptors: [twice]))
        {
            Assert.Equal((false, ResourceService.ReadFailed), Answer(await host.Get<ResourceService>().QueryStaffMembersAsync(new ListQuery(), Ct)));
        }

        Assert.Equal(2, twice.Thrown);
    }

    private static (bool IsSuccess, string? Error) Answer(Staffora.Core.Models.Result result) => (result.IsSuccess, result.Error);

    private static bool IsDeadlock(Exception? exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 1205 })
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FailingStaffReads : DbCommandInterceptor
    {
        private readonly int failures;
        private readonly bool deadlock;
        private int seen;
        private int thrown;

        public FailingStaffReads(int failures, bool deadlock)
        {
            this.failures = failures;
            this.deadlock = deadlock;
        }

        public int Thrown => Volatile.Read(ref this.thrown);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if ((command.CommandText.Contains("[Resources]", StringComparison.Ordinal) || command.CommandText.Contains("[ResourceUnitMemberships]", StringComparison.Ordinal))
                && Interlocked.Increment(ref this.seen) <= this.failures)
            {
                Interlocked.Increment(ref this.thrown);
                throw this.deadlock ? Deadlock() : new InvalidOperationException("The staff member read failed in a way nobody planned for.");
            }

            return ValueTask.FromResult(result);
        }

        private static SqlException Deadlock() => SqlFailures.Create(1205, SqlFailures.DeadlockVictim);
    }

    private sealed class LogSink : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Category(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Category : ILogger
        {
            private readonly LogSink sink;
            private readonly string name;

            public Category(LogSink sink, string name)
            {
                this.sink = sink;
                this.name = name;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                this.sink.Entries.Enqueue((this.name, logLevel, formatter(state, exception), exception));
        }
    }
}
