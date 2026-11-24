using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Platform;
using Staffora.Data.Configurations.Platform;
using Staffora.Services.Diagnostics;

namespace Staffora.Test.Unit.Diagnostics;

public class ErrorLogPipelineTests
{
    private static readonly DateTimeOffset now = new(2026, 11, 3, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void An_error_is_queued_with_the_scopes_correlation_id_path_and_identity()
    {
        var (factory, queue) = Pipeline();
        var identity = new LogIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var logger = factory.CreateLogger("Staffora.Services.Sample");

        using (logger.BeginScope(new LogScope("corr-1", "/tenant/management", identity)))
        {
            logger.LogError(new EventId(7, "SampleFailed"), new InvalidOperationException("broken"), "Saving {Thing} failed.", "the unit");
        }

        var row = Assert.Single(Drain(queue));
        Assert.Equal(("corr-1", "/tenant/management"), (row.CorrelationId, row.RequestPath));
        Assert.Equal((identity.TenantId, identity.UserId, identity.ImpersonatorId), (row.TenantId, row.UserId, row.ImpersonatorId));
        Assert.Equal((ErrorLogLevel.Error, "Staffora.Services.Sample", 7, "SampleFailed"), (row.Level, row.Category, row.EventId, row.EventName));
        Assert.Equal("Saving the unit failed.", row.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, row.ExceptionType);
        Assert.Contains("broken", row.ExceptionDetail);
        Assert.Equal(now.UtcDateTime, row.Occurred);
    }

    [Fact]
    public void Only_error_and_critical_entries_are_queued()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Sample");

        logger.LogWarning("a warning");
        logger.LogInformation("information");
        logger.LogCritical("critical");

        Assert.Equal([ErrorLogLevel.Critical], Drain(queue).Select(row => row.Level));
    }

    [Fact]
    public void The_error_logs_own_classes_and_EF_Cores_categories_are_not_queued()
    {
        var (factory, queue) = Pipeline();

        factory.CreateLogger<ErrorLogWriter>().LogError("the writer failed");
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError("Failed executing DbCommand");
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Update").LogError("An exception occurred while saving changes");

        Assert.Empty(Drain(queue));
    }

    [Fact]
    public void Nothing_the_writers_suppressed_flow_logs_is_queued_and_the_flow_after_it_is_queued_again()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Data.DatabaseCommands");

        using (ErrorLogQueue.Suppress())
        {
            logger.LogError("A database command failed while the error log was written.");
            Assert.True(ErrorLogQueue.IsSuppressed);
        }

        logger.LogError("A later failure of a request.");

        Assert.Equal(["A later failure of a request."], Drain(queue).Select(row => row.Message));
    }

    [Fact]
    public void An_inner_known_identity_replaces_the_outer_ones_and_an_unknown_one_keeps_them()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Blazor.Sample");
        var cookie = new LogIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var circuit = new LogIdentity(Guid.NewGuid(), Guid.NewGuid(), null);

        using (logger.BeginScope(new LogScope("connection", "/_blazor", cookie)))
        {
            using (logger.BeginScope(new LogScope("before-session", "/me/overview")))
            {
                logger.LogError("before the session resolved");
            }

            using (logger.BeginScope(new LogScope("after-session", "/me/overview", circuit)))
            {
                logger.LogError("after the session resolved");
            }
        }

        var rows = Drain(queue);
        Assert.Equal(("before-session", cookie.TenantId, cookie.ImpersonatorId), (rows[0].CorrelationId, rows[0].TenantId, rows[0].ImpersonatorId));
        Assert.Equal(("after-session", circuit.TenantId, (Guid?)null), (rows[1].CorrelationId, rows[1].TenantId, rows[1].ImpersonatorId));
    }

    [Fact]
    public void Secrets_in_the_message_and_the_exception_never_reach_the_row()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Sample");
        var exception = new InvalidOperationException("Login failed: Server=db;Database=Staffora;User ID=app;Password=Hunter2!;");

        logger.LogError(exception, "Bootstrap for {Email} got {InitialPassword}; the call sent Authorization: Bearer abc.DEF-123", "owner@example.com", "Xy7-unguessable");

        var row = Assert.Single(Drain(queue));
        Assert.DoesNotContain("Xy7-unguessable", row.Message);
        Assert.DoesNotContain("abc.DEF-123", row.Message);
        Assert.Contains("owner@example.com", row.Message);
        Assert.DoesNotContain("Hunter2!", row.ExceptionDetail);
        Assert.Contains("Password=[redacted]", row.ExceptionDetail);
    }

    [Theory]
    [InlineData("Server=db;Password=s3cret;Database=x", "Server=db;Password=[redacted];Database=x")]
    [InlineData("{\"userName\":\"a\",\"password\":\"s3cret\"}", "{\"userName\":\"a\",\"password\":[redacted]}")]
    [InlineData("GET /callback?code=1&access_token=abc123&state=2", "GET /callback?code=[redacted]&access_token=[redacted]&state=2")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: Bearer [redacted]")]
    [InlineData("newPassword=Hunter2", "newPassword=[redacted]")]
    [InlineData("user_password=Hunter2", "user_password=[redacted]")]
    [InlineData("db_pwd=Hunter2", "db_pwd=[redacted]")]
    [InlineData("resetToken=Hunter2", "resetToken=[redacted]")]
    [InlineData("PasswordHash: Hunter2", "PasswordHash: [redacted]")]
    [InlineData("SecurityStamp=Hunter2", "SecurityStamp=[redacted]")]
    [InlineData("Password=Hunter2", "Password=[redacted]")]
    [InlineData("Password = \"Hunter2\"", "Password = [redacted]")]
    [InlineData("client_secret=Hunter2", "client_secret=[redacted]")]
    [InlineData("AccountKey=Hunter2", "AccountKey=[redacted]")]
    [InlineData("x-api-key: Hunter2", "x-api-key: [redacted]")]
    [InlineData("sig=Hunter2", "sig=[redacted]")]
    [InlineData("code=Hunter2", "code=[redacted]")]
    [InlineData("note='token=Hunter2'", "note='token=[redacted]'")]
    [InlineData("Authorization: Basic Hunter2", "Authorization: Basic [redacted]")]
    [InlineData("Authorization: Hunter2", "Authorization: [redacted]")]
    [InlineData("Cookie: .AspNetCore.Identity.Application=Hunter2; theme=dark", "Cookie: [redacted]")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.Hunter2", "[redacted]")]
    [InlineData("the token eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0. was sent", "the token [redacted] was sent")]
    [InlineData("nothing secret here", "nothing secret here")]
    [InlineData("the keyboard layout is Swedish", "the keyboard layout is Swedish")]
    [InlineData("StatusCode=500; Timestamp=2026-11-03T09:30:00Z", "StatusCode=500; Timestamp=2026-11-03T09:30:00Z")]
    [InlineData("AssignmentId=42, CacheKey=units:a, design=compact, monkey=1", "AssignmentId=42, CacheKey=units:a, design=compact, monkey=1")]
    [InlineData("   at Staffora.Services.PasswordPolicy.Check() in C:\\src\\PasswordPolicy.cs:line 12", "   at Staffora.Services.PasswordPolicy.Check() in C:\\src\\PasswordPolicy.cs:line 12")]
    [InlineData("Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException: IDX10223: Lifetime validation failed.", "Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException: IDX10223: Lifetime validation failed.")]
    [InlineData("password is Hunter2", "password is Hunter2")]
    public void The_scrubber_replaces_secret_values_and_keeps_the_rest(string text, string expected) =>
        Assert.Equal(expected, SecretScrubber.Scrub(text));

    [Theory]
    [InlineData("NewPassword", true)]
    [InlineData("ResetToken", true)]
    [InlineData("AccountKey", true)]
    [InlineData("Code", true)]
    [InlineData("StatusCode", false)]
    [InlineData("CacheKey", false)]
    [InlineData("Email", false)]
    public void A_template_placeholder_is_judged_by_the_same_names(string name, bool secret) =>
        Assert.Equal(secret, SecretScrubber.IsSecretName(name));

    [Fact]
    public void A_template_value_cut_from_the_message_is_cut_from_the_exception_too()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Sample");

        logger.LogError(new InvalidOperationException("Could not set Hunter2Secret as the new password"), "Setting {NewPassword} failed", "Hunter2Secret");

        var row = Assert.Single(Drain(queue));
        Assert.Equal("Setting [redacted] failed", row.Message);
        Assert.DoesNotContain("Hunter2Secret", row.ExceptionDetail);
        Assert.Contains("Could not set [redacted] as the new password", row.ExceptionDetail);
    }

    [Fact]
    public void A_huge_message_and_exception_are_capped_and_scrubbed()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Sample");
        var huge = "Password=Hunter2 " + new string('x', 2_000_000);

        logger.LogError(new InvalidOperationException(huge), "{Text}", huge);

        var row = Assert.Single(Drain(queue));
        Assert.Equal(ErrorLogConfiguration.MessageMaxLength, row.Message.Length);
        Assert.Equal(ErrorLogConfiguration.ExceptionDetailMaxLength, row.ExceptionDetail!.Length);
        Assert.StartsWith("Password=[redacted] ", row.Message);
        Assert.DoesNotContain("Hunter2", row.ExceptionDetail);
    }

    [Fact]
    public void A_secret_cut_by_the_cap_is_still_replaced_as_a_whole()
    {
        var text = new string('x', 40) + " Password=" + new string('S', 30);

        Assert.Equal(new string('x', 40) + " Password=[r", SecretScrubber.Scrub(text, 52));
    }

    [Fact]
    public void Logging_never_waits_on_a_full_queue_it_drops_and_counts()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Sample");

        for (var i = 0; i < ErrorLogQueue.Capacity + 5; i++)
        {
            logger.LogError("error {Number}", i);
        }

        Assert.Equal(5, queue.Dropped);
        Assert.Equal(ErrorLogQueue.Capacity, Drain(queue).Count);
    }

    [Fact]
    public async Task A_flush_completes_once_the_writer_has_handled_every_entry_queued_before_it()
    {
        var queue = new ErrorLogQueue();
        queue.TryEnqueue(new ErrorLog { Message = "first" });
        queue.TryEnqueue(new ErrorLog { Message = "second" });

        var flush = queue.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(queue.Reader.TryRead(out var first));
        queue.MarkProcessed(first!.Sequence);
        Assert.False(flush.IsCompleted);

        Assert.True(queue.Reader.TryRead(out var second));
        queue.MarkProcessed(second!.Sequence);
        await flush;
        Assert.True(queue.FlushAsync(TestContext.Current.CancellationToken).IsCompleted);
    }

    [Fact]
    public void A_circuit_scope_follows_the_trace_so_an_error_inside_a_step_carries_that_steps_trace_id()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Sample");
        var circuit = new LogIdentity(Guid.NewGuid(), Guid.NewGuid(), null);
        Activity.Current = null;

        using (logger.BeginScope(new LogScope("circuit-event", "/tenant/jobs", circuit, followsTrace: true)))
        {
            logger.LogError("outside any step");
            using var step = new Activity("staffora.workflow.step").SetIdFormat(ActivityIdFormat.W3C).Start();
            logger.LogError("inside a step");
            using (logger.BeginScope(new LogScope("caller-sent", "/api", circuit)))
            {
                logger.LogError("a caller's own id");
            }

            var rows = Drain(queue);
            Assert.Equal(["circuit-event", step.TraceId.ToHexString(), "caller-sent"], rows.Select(row => row.CorrelationId));
            Assert.All(rows, row => Assert.Equal(circuit.TenantId, row.TenantId));
        }
    }

    [Fact]
    public void An_error_with_no_scope_inside_a_hosted_pass_carries_the_pass_trace_id()
    {
        var (factory, queue) = Pipeline();
        var logger = factory.CreateLogger("Staffora.Services.Jobs.JobClock");

        using (var pass = new Activity("staffora.job_clock.pass").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            logger.LogError("a job could not move");
            Assert.Equal(pass.TraceId.ToHexString(), Assert.Single(Drain(queue)).CorrelationId);
        }
    }

    [Theory]
    [InlineData("abc-123_DEF.4:5", true)]
    [InlineData("two words", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("", false)]
    public void A_callers_correlation_id_is_taken_only_in_a_safe_shape(string sent, bool taken) =>
        Assert.Equal(taken, LogScope.CorrelationIdFrom(sent) == sent);

    [Fact]
    public void A_correlation_id_longer_than_the_limit_is_replaced() =>
        Assert.NotEqual(new string('a', LogScope.MaxCorrelationIdLength + 1), LogScope.CorrelationIdFrom(new string('a', LogScope.MaxCorrelationIdLength + 1)));

    private static (ILoggerFactory Factory, ErrorLogQueue Queue) Pipeline()
    {
        var queue = new ErrorLogQueue();
        var factory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new ErrorLogLoggerProvider(queue, new FakeClock(now))));
        return (factory, queue);
    }

    private static List<ErrorLog> Drain(ErrorLogQueue queue)
    {
        var rows = new List<ErrorLog>();
        while (queue.Reader.TryRead(out var queued))
        {
            rows.Add(queued.Error);
        }

        return rows;
    }
}
