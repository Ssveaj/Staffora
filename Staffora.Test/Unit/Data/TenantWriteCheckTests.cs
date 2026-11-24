using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Test.Unit.Data;

public class TenantWriteCheckTests
{
    private static readonly RecordingLoggerProvider log = new();
    private static readonly ILoggerFactory loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(log).SetMinimumLevel(LogLevel.Information));

    [Fact]
    public void A_system_scope_lease_nests_ends_on_dispose_and_logs_its_reason_when_opened_and_closed()
    {
        using var context = CreateContext(new RuntimeExecutionContext(Guid.NewGuid(), Guid.NewGuid()));
        var reason = $"unit test {Guid.NewGuid():N}";

        Assert.False(context.IsInSystemScope);
        var outer = context.BeginSystemScope(reason);
        var inner = context.BeginSystemScope(reason);
        inner.Dispose();
        Assert.True(context.IsInSystemScope);
        inner.Dispose();
        Assert.True(context.IsInSystemScope);
        outer.Dispose();

        Assert.False(context.IsInSystemScope);
        Assert.Equal(
            [$"System scope opened for {reason} (depth 1).", $"System scope opened for {reason} (depth 2).", $"System scope closed for {reason} (depth 1).", $"System scope closed for {reason} (depth 0)."],
            log.MessagesContaining(reason));
    }

    [Fact]
    public void A_lease_ends_when_the_block_it_guards_throws()
    {
        using var context = CreateContext(new RuntimeExecutionContext(Guid.NewGuid(), Guid.NewGuid()));

        Action failingBlock = () =>
        {
            using (context.BeginSystemScope("unit test: failing block"))
            {
                throw new InvalidOperationException("The block failed.");
            }
        };

        Assert.Throws<InvalidOperationException>(failingBlock);

        Assert.False(context.IsInSystemScope);
    }

    [Fact]
    public void A_lease_needs_a_reason()
    {
        using var context = CreateContext(runtime: null);

        Assert.Throws<ArgumentException>(() => context.BeginSystemScope(" "));
    }

    [Fact]
    public void A_write_without_a_runtime_tenant_is_refused_and_logged()
    {
        var signedInWithoutTenant = new RuntimeExecutionContext();
        signedInWithoutTenant.SetUser(Guid.NewGuid());
        using var context = CreateContext(signedInWithoutTenant);
        var role = new TenantRole { Name = "Orphan", TenantId = Guid.NewGuid() };
        context.TenantRoles.Add(role);

        var refused = Assert.Throws<TenantIsolationException>(() => context.SaveChanges());

        Assert.Contains("no runtime tenant", refused.Message);
        Assert.Contains(log.Warnings, message => message.Contains($"Refused tenant insert on TenantRole {role.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_insert_for_another_tenant_is_refused_and_logged_without_naming_that_tenant_in_the_error()
    {
        var otherTenant = Guid.NewGuid();
        using var context = CreateContext(new RuntimeExecutionContext(Guid.NewGuid(), Guid.NewGuid()));
        var role = new TenantRole { Name = "Planted", TenantId = otherTenant };
        context.TenantRoles.Add(role);

        var refused = await Assert.ThrowsAsync<TenantIsolationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(otherTenant.ToString(), refused.Message);
        Assert.Contains(log.Warnings, message => message.Contains($"Refused tenant insert on TenantRole {role.Id}", StringComparison.Ordinal) && message.Contains(otherTenant.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("Created")]
    [InlineData("CreatedById")]
    [InlineData("ImpersonatedById")]
    public async Task An_ExecuteUpdate_setting_TenantId_or_an_insert_only_column_is_refused_before_the_database(string column)
    {
        using var context = CreateContext(new RuntimeExecutionContext(Guid.NewGuid(), Guid.NewGuid()));
        var roles = context.TenantRoles.Where(role => role.Name == "Viewer");

        Func<Task<int>> update = column switch
        {
            "Created" => () => roles.ExecuteUpdateAsync(set => set.SetProperty(role => role.Created, DateTime.UnixEpoch), TestContext.Current.CancellationToken),
            "ImpersonatedById" => () => roles.ExecuteUpdateAsync(set => set.SetProperty(role => role.ImpersonatedById, Guid.NewGuid()), TestContext.Current.CancellationToken),
            _ => () => roles.ExecuteUpdateAsync(set => set.SetProperty(role => EF.Property<Guid>(role, column), Guid.NewGuid()), TestContext.Current.CancellationToken),
        };

        var refused = await Assert.ThrowsAsync<TenantIsolationException>(update);

        Assert.Contains(column, refused.Message, StringComparison.Ordinal);
        Assert.Contains(log.Warnings, message => message.StartsWith("Refused a bulk update", StringComparison.Ordinal) && message.Contains(column, StringComparison.Ordinal));
    }

    private static ApplicationDbContext CreateContext(IRuntimeExecutionContext? runtime)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection))
            .UseLoggerFactory(loggerFactory);
        return new ApplicationDbContext(options.Options) { RuntimeContext = runtime };
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public IEnumerable<string> Warnings => this.entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message);

        public List<string> MessagesContaining(string text) => this.entries.Select(entry => entry.Message).Where(message => message.Contains(text, StringComparison.Ordinal)).ToList();

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(ApplicationDbContext).FullName ? new Recorder(this.entries) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class Recorder : ILogger
        {
            private readonly ConcurrentQueue<(LogLevel, string)> entries;

            public Recorder(ConcurrentQueue<(LogLevel, string)> entries)
            {
                this.entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                this.entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
