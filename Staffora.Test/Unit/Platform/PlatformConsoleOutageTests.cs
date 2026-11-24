using Microsoft.Extensions.Logging;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Services.Platform;
using Staffora.Test.Web;

namespace Staffora.Test.Unit.Platform;

public class PlatformConsoleOutageTests
{
    [Fact]
    public async Task The_users_tab_answers_a_failed_result_and_logs_it_when_the_tenant_check_fails()
    {
        var runtime = new RuntimeExecutionContext();
        runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        runtime.MarkInitialized();
        var logger = new ErrorRecorder();

        var console = new PlatformConsoleService(new UnreachableDbContextFactory("The tenant check"), runtime, users: null!, TimeProvider.System, queue: null!, retention: null!, Microsoft.Extensions.Options.Options.Create(new Staffora.Services.Settings.PlatformOptions()), logger);

        var result = await console.QueryTenantUsersAsync(Guid.NewGuid(), new ListQuery());

        Assert.False(result.IsSuccess);
        Assert.Equal(PlatformConsoleService.TenantUnreadable, result.Error);
        Assert.Single(logger.Errors);
    }

    private sealed class ErrorRecorder : ILogger<PlatformConsoleService>
    {
        public List<Exception?> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(exception);
            }
        }
    }
}
