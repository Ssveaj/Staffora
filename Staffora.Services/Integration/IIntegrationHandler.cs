using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Integration;

public interface IIntegrationHandler
{
    Task HandleAsync(IntegrationDelivery delivery, ApplicationDbContext context, CancellationToken cancellationToken);
}

public sealed record IntegrationDelivery(Guid MessageId, string Kind, Guid SourceTenantId, Guid TargetTenantId, Guid CorrelationId, string Payload, IServiceProvider Services)
{
    public AfterCommitWork AfterCommit { get; } = new();
}
