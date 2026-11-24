using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Core.Entities.Organization;

public class OrganizationUnitUserRole : TenantEntityBase
{
    public Guid UserId { get; set; }
    public Guid TenantRoleId { get; set; }
    public Guid OrganizationUnitId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public TenantRole TenantRole { get; set; } = null!;
    public OrganizationUnit OrganizationUnit { get; set; } = null!;
}
