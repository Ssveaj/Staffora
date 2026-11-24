namespace Staffora.Core.Entities.Tenancy;
public class TenantRolePermission : TenantEntityBase
{
    public Guid TenantRoleId { get; set; }
    public RolePermission Permission { get; set; }
    public TenantRole TenantRole { get; set; } = null!;
}
