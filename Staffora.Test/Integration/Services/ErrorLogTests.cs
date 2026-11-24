using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Data;
using Staffora.Services.Platform;
using Staffora.Services.Diagnostics;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public class ErrorLogTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public ErrorLogTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact]
    public async Task A_tenant_context_reads_no_error_row_even_one_labelled_with_its_own_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var stored = await StoreAsync(new ErrorLog { Category = "test", Message = "A's error", TenantId = scenario.A.TenantId, UserId = scenario.A.OwnerId }, cancellationToken);

        await using (var tenant = this.database.CreateContext(scenario.A.Runtime()))
        {
            Assert.False(await tenant.ErrorLogs.AnyAsync(log => log.Id == stored, cancellationToken));
            Assert.Empty(await tenant.ErrorLogs.ToListAsync(cancellationToken));
        }

        await using var system = this.database.CreateSystemContext();
        Assert.True(await system.ErrorLogs.AnyAsync(log => log.Id == stored, cancellationToken));
    }

    [Fact]
    public async Task A_row_is_written_only_in_system_scope_and_never_changed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);

        await using (var tenant = this.database.CreateContext(scenario.A.Runtime()))
        {
            tenant.ErrorLogs.Add(new ErrorLog { Category = "test", Message = "forged by a tenant", TenantId = scenario.A.TenantId });
            await Assert.ThrowsAsync<TenantIsolationException>(() => tenant.SaveChangesAsync(cancellationToken));
        }

        var stored = await StoreAsync(new ErrorLog { Category = "test", Message = "as logged" }, cancellationToken);
        await using (var system = this.database.CreateSystemContext())
        {
            var row = await system.ErrorLogs.SingleAsync(log => log.Id == stored, cancellationToken);
            row.Message = "rewritten";
            await Assert.ThrowsAsync<TenantIsolationException>(() => system.SaveChangesAsync(cancellationToken));
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal("as logged", (await check.ErrorLogs.SingleAsync(log => log.Id == stored, cancellationToken)).Message);
        Assert.False(await check.ErrorLogs.AnyAsync(log => log.Message == "forged by a tenant", cancellationToken));
    }

    [Fact]
    public async Task Only_a_platform_owner_who_is_not_impersonating_reads_the_error_log_through_the_service()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var message = $"B failed {Guid.NewGuid():N}";
        var stored = await StoreAsync(new ErrorLog { Category = "test", Message = message, TenantId = scenario.B.TenantId, UserId = scenario.B.OwnerId }, cancellationToken);
        var query = ErrorLogList.Definition.Default with { Search = message };
        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            var read = await console.Get<PlatformConsoleService>().QueryErrorLogAsync(query, cancellationToken);
            Assert.True(read.IsSuccess, read.Error);
            var row = Assert.Single(read.Value!.Rows, entry => entry.Id == stored);
            Assert.Equal((scenario.B.Name, message), (row.TenantName, row.Message));
            Assert.NotNull(row.UserName);
        }

        foreach (var refused in new IRuntimeExecutionContext[] { scenario.B.Runtime(), impersonating })
        {
            await using var host = ServiceHost.Create(this.database, refused);
            var read = await host.Get<PlatformConsoleService>().QueryErrorLogAsync(query, cancellationToken);
            Assert.False(read.IsSuccess);
            Assert.Equal(PlatformConsoleService.ErrorLogNotPlatformOwner, read.Error);
        }
    }

    [Fact]
    public async Task The_list_leaves_the_exception_text_out_and_one_rows_text_is_read_by_its_id_with_platform_reach_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        var detail = "System.InvalidOperationException: broken\n   at Sample.Run()" + new string('.', 20_000);
        var marker = $"with a long exception {Guid.NewGuid():N}";
        var stored = await StoreAsync(new ErrorLog { Category = "test", Message = marker, ExceptionType = "System.InvalidOperationException", ExceptionDetail = detail }, cancellationToken);

        await using (var console = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime()))
        {
            console.ClearCommandLog();
            var list = await console.Get<PlatformConsoleService>().QueryErrorLogAsync(ErrorLogList.Definition.Default with { Search = marker }, cancellationToken);
            Assert.True(list.IsSuccess, list.Error);
            Assert.Contains(list.Value!.Rows, entry => entry.Id == stored);
            Assert.All(console.CommandLog.Where(command => command.Contains("FROM [ErrorLogs]", StringComparison.Ordinal)), command => Assert.DoesNotContain("[ExceptionDetail]", command));

            var read = await console.Get<PlatformConsoleService>().GetExceptionDetailAsync(stored, cancellationToken);
            Assert.True(read.IsSuccess, read.Error);
            Assert.Equal(detail, read.Value);
            Assert.False((await console.Get<PlatformConsoleService>().GetExceptionDetailAsync(Guid.NewGuid(), cancellationToken)).IsSuccess);
        }

        var impersonating = platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.A.OwnerId, scenario.A.TenantId);
        foreach (var refused in new IRuntimeExecutionContext[] { scenario.A.Runtime(), impersonating })
        {
            await using var host = ServiceHost.Create(this.database, refused);
            var read = await host.Get<PlatformConsoleService>().GetExceptionDetailAsync(stored, cancellationToken);
            Assert.Equal((false, PlatformConsoleService.ErrorLogNotPlatformOwner), (read.IsSuccess, read.Error));
            Assert.False(host.Get<PlatformConsoleService>().DroppedSinceStart().IsSuccess);
        }
    }

    [Fact]
    public async Task Entries_the_full_queue_dropped_are_recorded_as_one_warning_row_with_the_next_batch_and_counted_for_the_page()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(this.database, cancellationToken);
        await using var host = ServiceHost.Create(this.database, platform.PlatformOwnerRuntime(), services => services.AddErrorLog());
        var queue = host.Get<ErrorLogQueue>();
        var writer = host.Get<ErrorLogWriter>();
        var marker = $"drop-{Guid.NewGuid():N}";
        var started = TimeProvider.System.GetUtcNow().UtcDateTime.AddSeconds(-1);

        for (var i = 0; i < ErrorLogQueue.Capacity + 7; i++)
        {
            queue.TryEnqueue(new ErrorLog { Category = "test", Message = $"{marker} {i}", CorrelationId = marker, Occurred = TimeProvider.System.GetUtcNow().UtcDateTime });
        }

        Assert.Equal(7, queue.Dropped);
        Assert.Equal((true, 7L), (host.Get<PlatformConsoleService>().DroppedSinceStart().IsSuccess, host.Get<PlatformConsoleService>().DroppedSinceStart().Value));
        await writer.StartAsync(cancellationToken);
        try
        {
            await queue.FlushAsync(cancellationToken);
        }
        finally
        {
            await writer.StopAsync(cancellationToken);
        }

        await using var check = this.database.CreateSystemContext();
        Assert.Equal(ErrorLogQueue.Capacity, await check.ErrorLogs.CountAsync(log => log.CorrelationId == marker, cancellationToken));
        var summary = Assert.Single(await check.ErrorLogs.AsNoTracking().Where(log => log.Message == ErrorLogWriter.DroppedMessage(7) && log.Occurred >= started).ToListAsync(cancellationToken));
        Assert.Equal((ErrorLogLevel.Warning, typeof(ErrorLogQueue).FullName), (summary.Level, summary.Category));
    }

    [Fact]
    public async Task The_writer_stores_queued_errors_and_a_failed_write_queues_nothing_of_its_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var commandLog = new ConcurrentQueue<string>();
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext(), services =>
        {
            services.AddLogging(logging => logging.AddProvider(new CategoryRecorder(DatabaseCommandLog.Category, commandLog)));
            services.AddErrorLog();
        });
        var queue = host.Get<ErrorLogQueue>();
        var writer = host.Get<ErrorLogWriter>();
        var logger = host.Get<ILoggerFactory>().CreateLogger("Staffora.Services.Sample");
        await writer.StartAsync(cancellationToken);
        try
        {
            var correlationId = $"test-{Guid.NewGuid():N}";
            using (logger.BeginScope(new LogScope(correlationId, "/sample")))
            {
                logger.LogError(new InvalidOperationException("sample"), "A sample failure.");
            }

            await queue.FlushAsync(cancellationToken);
            await using (var check = this.database.CreateSystemContext())
            {
                Assert.Equal("A sample failure.", (await check.ErrorLogs.SingleAsync(log => log.CorrelationId == correlationId, cancellationToken)).Message);
            }

            var taken = await StoreAsync(new ErrorLog { Category = "test", Message = "stored first" }, cancellationToken);
            Assert.True(queue.TryEnqueue(new ErrorLog { Id = taken, Category = "test", Message = "same key" }));
            var queuedBefore = queue.Enqueued;
            await queue.FlushAsync(cancellationToken);

            Assert.Contains(commandLog, message => message.StartsWith("A database command failed", StringComparison.Ordinal));
            Assert.Equal(queuedBefore, queue.Enqueued);
            await using var after = this.database.CreateSystemContext();
            Assert.Equal("stored first", (await after.ErrorLogs.SingleAsync(log => log.Id == taken, cancellationToken)).Message);
        }
        finally
        {
            await writer.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task The_command_log_names_a_failed_commands_text_and_parameters_but_never_their_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var log = new ConcurrentQueue<string>();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(new CategoryRecorder(DatabaseCommandLog.Category, log)));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(this.database.ConnectionString)).UseLoggerFactory(loggerFactory).Options;
        var id = Guid.NewGuid();
        const string secretLike = "value-that-must-not-be-logged";

        foreach (var attempt in new[] { "first", "second" })
        {
            await using var context = new TestDataContext(options);
            using var lease = context.BeginSystemScope("test: write two error rows with one key");
            context.ErrorLogs.Add(new ErrorLog { Id = id, Category = "test", Message = $"{secretLike} {attempt}" });
            if (attempt == "first")
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
            }
        }

        var failure = Assert.Single(log, message => message.StartsWith("A database command failed", StringComparison.Ordinal));
        Assert.Contains("INSERT INTO [ErrorLogs]", failure);
        Assert.Contains("@p", failure);
        Assert.DoesNotContain(secretLike, failure);
    }

    [Fact]
    public async Task A_command_slower_than_the_threshold_is_logged_as_a_warning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var log = new ConcurrentQueue<string>();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(new CategoryRecorder(DatabaseCommandLog.Category, log)));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(this.database.ConnectionString))
            .UseLoggerFactory(loggerFactory)
            .AddInterceptors(new DatabaseCommandLog(TimeSpan.Zero))
            .Options;

        await using var context = new TestDataContext(options);
        using var lease = context.BeginSystemScope("test: read the error log");
        await context.ErrorLogs.AnyAsync(cancellationToken);

        Assert.Contains(log, message => message.StartsWith("A database command took", StringComparison.Ordinal) && message.Contains("more than 0 ms", StringComparison.Ordinal));
    }

    private async Task<Guid> StoreAsync(ErrorLog row, CancellationToken cancellationToken)
    {
        if (row.Occurred == default)
        {
            row.Occurred = TimeProvider.System.GetUtcNow().UtcDateTime;
        }

        await using var system = this.database.CreateSystemContext();
        system.ErrorLogs.Add(row);
        await system.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    private sealed class CategoryRecorder : ILoggerProvider, ILogger
    {
        private readonly string category;
        private readonly ConcurrentQueue<string> messages;

        public CategoryRecorder(string category, ConcurrentQueue<string> messages)
        {
            this.category = category;
            this.messages = messages;
        }

        public ILogger CreateLogger(string categoryName) => categoryName == this.category ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.messages.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
