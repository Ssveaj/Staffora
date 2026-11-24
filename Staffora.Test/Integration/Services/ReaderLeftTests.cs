using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Data;
using Staffora.Services.Coordination;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class ReaderLeftTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);
    private static readonly DateOnly Monday = new(2027, 3, 8);

    private readonly RealDatabase database;

    public ReaderLeftTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_workspace_or_board_read_ended_after_its_reader_left_is_a_cancellation_logged_as_information_alone()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Left Board", Ct);
        await scenario.AddStaffAsync(this.database, "Ada", Ct);
        await scenario.StartedJobAsync(this.database, "Left algebra", PeriodStart, PeriodEnd, Ct);
        var leaving = new LeavingMidRead();
        var log = new LogSink();
        await using var host = ServiceHost.Create(
            this.database,
            scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator),
            services =>
            {
                AssignmentScenario.UseClock(services);
                services.AddLogging(logging => logging.AddProvider(log));
            },
            [leaving]);
        var workspace = host.Get<CoordinationService>();
        var board = host.Get<CoordinationService>();

        var snapshot = await workspace.ReadAsync(Ct);
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        var staffMembers = await board.ReadStaffMembersAsync(Monday, new BoardFilter(), Ct);
        Assert.True(staffMembers.IsSuccess, staffMembers.Error);
        Assert.True((await board.ReadBookingsAsync(staffMembers.Value!, snapshot.Value!, Ct)).IsSuccess);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ReadAsync(leaving.LeaveDuringNextRead()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => board.ReadStaffMembersAsync(Monday, new BoardFilter(), leaving.LeaveDuringNextRead()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => board.ReadBookingsAsync(staffMembers.Value!, snapshot.Value!, leaving.LeaveDuringNextRead()));
        Assert.Equal(3, leaving.Struck);
        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Equal(3, log.Entries.Count(entry => entry.Level == LogLevel.Information && entry.Message.Contains("taken as a cancellation", StringComparison.Ordinal)));

        Assert.Equal(CoordinationService.BoardReadFailed, (await board.ReadStaffMembersAsync(Monday, new BoardFilter(), leaving.FailNextRead())).Error);
        Assert.Equal(4, leaving.Struck);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error && entry.Category == typeof(CoordinationService).FullName);
        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Error && entry.Category == DatabaseCommandLog.Category);
    }

    private sealed class LeavingMidRead : DbCommandInterceptor
    {
        private CancellationTokenSource? armed;
        private bool leaves;
        private int struck;

        public int Struck => Volatile.Read(ref this.struck);

        public CancellationToken LeaveDuringNextRead() => this.Arm(leaves: true);

        public CancellationToken FailNextRead() => this.Arm(leaves: false);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref this.armed, null) is { } reader)
            {
                Interlocked.Increment(ref this.struck);
                if (this.leaves)
                {
                    reader.Cancel();
                }

                throw SqlFailures.Create(0, SqlFailures.SevereError);
            }

            return ValueTask.FromResult(result);
        }

        private CancellationToken Arm(bool leaves)
        {
            var reader = new CancellationTokenSource();
            this.leaves = leaves;
            this.armed = reader;
            return reader.Token;
        }
    }

    private sealed class LogSink : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

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
                this.sink.Entries.Enqueue((this.name, logLevel, formatter(state, exception)));
        }
    }
}
