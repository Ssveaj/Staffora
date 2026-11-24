namespace Staffora.Core.Entities.Integration;

public class IntegrationMessage : TenantEntityBase
{
    public const int MaxKindLength = 100;

    public const int MaxPayloadLength = 4000;

    public const int MaxFailureLength = 200;

    public long Sequence { get; set; }

    public string Kind { get; set; } = string.Empty;

    public Guid TargetTenantId { get; set; }

    public Guid CorrelationId { get; set; }

    public string Payload { get; set; } = string.Empty;

    public IntegrationMessageState State { get; set; }

    public int Attempts { get; set; }

    public DateTime NextAttempt { get; set; }

    public Guid? ClaimToken { get; set; }

    public DateTime? ClaimedUntil { get; set; }

    public DateTime? Finished { get; set; }

    public string? LastFailure { get; set; }
}
