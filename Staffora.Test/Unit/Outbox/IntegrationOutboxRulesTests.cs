using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Integration;
using Staffora.Data;
using Staffora.Services.Integration;
using Staffora.Test.Unit.Data;

namespace Staffora.Test.Unit.Outbox;

public class IntegrationOutboxRulesTests
{
    [Fact]
    public void A_failed_message_waits_thirty_seconds_then_twice_as_long_each_time_at_most_an_hour()
    {
        Assert.Equal(
            [30, 60, 120, 240, 480, 960, 1920, 3600, 3600],
            Enumerable.Range(1, 9).Select(attempts => (int)IntegrationRetryRules.RetryAfter(attempts).TotalSeconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegrationRetryRules.RetryAfter(0));
        Assert.Equal(TimeSpan.FromHours(1), IntegrationRetryRules.RetryAfter(int.MaxValue));
    }

    [Fact]
    public void A_message_gets_eight_attempts()
    {
        Assert.False(IntegrationRetryRules.IsSpent(IntegrationRetryRules.MaxAttempts - 1));
        Assert.True(IntegrationRetryRules.IsSpent(IntegrationRetryRules.MaxAttempts));
        Assert.Equal(8, IntegrationRetryRules.MaxAttempts);
    }

    [Theory]
    [InlineData(TenantState.Active, true)]
    [InlineData(TenantState.Setup, false)]
    [InlineData(TenantState.Suspended, false)]
    [InlineData(TenantState.PendingDeletion, false)]
    [InlineData(TenantState.Archived, false)]
    [InlineData(null, false)]
    public void Only_an_Active_tenant_exchanges_messages(TenantState? state, bool exchanges) =>
        Assert.Equal(exchanges, TenantStates.ExchangesMessages(state));

    [Fact]
    public void Only_a_staffing_company_and_a_tenant_it_manages_are_linked()
    {
        var (company, managed, stranger, otherCompany) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.True(TenantStates.AreLinked(company, null, managed, company));
        Assert.True(TenantStates.AreLinked(managed, company, company, null));

        Assert.False(TenantStates.AreLinked(company, null, otherCompany, null));
        Assert.False(TenantStates.AreLinked(managed, company, stranger, company));
        Assert.False(TenantStates.AreLinked(otherCompany, null, managed, company));
        Assert.False(TenantStates.AreLinked(company, company, company, company));
    }

    [Fact]
    public void The_registry_takes_one_handler_per_kind_and_refuses_a_kind_twice_an_empty_or_overlong_one_and_a_type_that_handles_nothing()
    {
        var registry = new IntegrationHandlerRegistry([new("test.one", typeof(NoHandler)), new("test.two", typeof(NoHandler))]);
        Assert.True(registry.Handles("test.one"));
        Assert.False(registry.Handles("test.three"));
        Assert.Equal(["test.one", "test.two"], registry.Kinds.Order(StringComparer.Ordinal));

        Assert.Contains("two handlers", Assert.Throws<InvalidOperationException>(() => new IntegrationHandlerRegistry([new("test.one", typeof(NoHandler)), new("test.one", typeof(NoHandler))])).Message);
        Assert.Throws<InvalidOperationException>(() => new IntegrationHandlerRegistry([new(" ", typeof(NoHandler))]));
        Assert.Throws<InvalidOperationException>(() => new IntegrationHandlerRegistry([new(new string('k', IntegrationMessage.MaxKindLength + 1), typeof(NoHandler))]));
        Assert.Contains("handles no integration message", Assert.Throws<InvalidOperationException>(() => new IntegrationHandlerRegistry([new("test.one", typeof(string))])).Message);
    }

    [Fact]
    public void A_queued_message_is_a_pending_row_of_the_runtimes_tenant_due_at_once()
    {
        var (sender, target, correlation) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = new DateTimeOffset(2027, 1, 4, 9, 0, 0, TimeSpan.Zero);
        using var context = CreateContext(new RuntimeExecutionContext(sender, Guid.NewGuid()));
        context.Clock = new FakeClock(now);

        var message = IntegrationOutbox.Enqueue(context, "test.kind", target, correlation, "{}");

        Assert.Equal((sender, target, correlation, IntegrationMessageState.Pending, 0, now.UtcDateTime), (message.TenantId, message.TargetTenantId, message.CorrelationId, message.State, message.Attempts, message.NextAttempt));
        Assert.Equal(EntityState.Added, context.Entry(message).State);
    }

    [Fact]
    public void Queuing_refuses_a_tenant_sending_to_itself_a_context_without_a_tenant_and_what_does_not_fit_its_columns()
    {
        var (sender, target) = (Guid.NewGuid(), Guid.NewGuid());
        using var context = CreateContext(new RuntimeExecutionContext(sender, Guid.NewGuid()));
        using var system = CreateContext(null);

        Assert.Throws<ArgumentException>(() => IntegrationOutbox.Enqueue(context, "test.kind", sender, Guid.NewGuid(), "{}"));
        Assert.Throws<InvalidOperationException>(() => IntegrationOutbox.Enqueue(system, "test.kind", target, Guid.NewGuid(), "{}"));
        Assert.Throws<ArgumentException>(() => IntegrationOutbox.Enqueue(context, "test.kind", Guid.Empty, Guid.NewGuid(), "{}"));
        Assert.Throws<ArgumentException>(() => IntegrationOutbox.Enqueue(context, "test.kind", target, Guid.Empty, "{}"));
        Assert.Throws<ArgumentException>(() => IntegrationOutbox.Enqueue(context, " ", target, Guid.NewGuid(), "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegrationOutbox.Enqueue(context, new string('k', IntegrationMessage.MaxKindLength + 1), target, Guid.NewGuid(), "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegrationOutbox.Enqueue(context, "test.kind", target, Guid.NewGuid(), new string('p', IntegrationMessage.MaxPayloadLength + 1)));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Queuing_under_a_lease_is_refused_since_the_runtimes_tenant_need_not_be_the_sender_there()
    {
        using var context = CreateContext(new RuntimeExecutionContext(Guid.NewGuid(), Guid.NewGuid()));
        using (context.BeginSystemScope("test: a step under a lease"))
        {
            Assert.Contains("lease", Assert.Throws<InvalidOperationException>(() => IntegrationOutbox.Enqueue(context, "test.kind", Guid.NewGuid(), Guid.NewGuid(), "{}")).Message);
        }

        Assert.Empty(context.ChangeTracker.Entries());
    }

    private static ApplicationDbContext CreateContext(IRuntimeExecutionContext? runtime)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(ApplicationDbContextOptions.Create(ModelUnderTest.UnopenedConnection));
        return new ApplicationDbContext(options.Options) { RuntimeContext = runtime };
    }

    private sealed class NoHandler : IIntegrationHandler
    {
        public Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("Never run.");
    }
}
