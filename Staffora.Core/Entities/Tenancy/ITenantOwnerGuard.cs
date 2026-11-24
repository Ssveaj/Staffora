namespace Staffora.Core.Entities.Tenancy;

public interface ITenantOwnerGuard
{
    bool HasActiveSystemOwner(Tenant tenant);
}
