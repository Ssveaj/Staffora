namespace Staffora.Core.Entities.Workflow;

public class StateHistoryEntry : TenantEntityBase
{
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public string TransitionKey { get; set; } = string.Empty;

    public int FromState { get; set; }

    public int ToState { get; set; }

    public Guid CorrelationId { get; set; }

    public Guid ActorId { get; set; }
}
