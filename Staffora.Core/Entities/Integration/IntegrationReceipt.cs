namespace Staffora.Core.Entities.Integration;

public class IntegrationReceipt : TenantEntityBase
{
    public Guid MessageId { get; set; }

    public string Kind { get; set; } = string.Empty;
}
