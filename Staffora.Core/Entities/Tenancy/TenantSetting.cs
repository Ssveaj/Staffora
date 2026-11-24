namespace Staffora.Core.Entities.Tenancy;

public class TenantSetting : TenantEntityBase
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public byte[] RowVersion { get; set; } = [];
}
