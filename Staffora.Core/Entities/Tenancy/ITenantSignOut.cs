namespace Staffora.Core.Entities.Tenancy;

public interface ITenantSignOut
{
    Task SignOutEveryoneAsync(Microsoft.EntityFrameworkCore.DbContext context, Guid tenantId, CancellationToken cancellationToken);
}
