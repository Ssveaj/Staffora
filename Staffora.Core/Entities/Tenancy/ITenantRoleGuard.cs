namespace Staffora.Core.Entities.Tenancy;

public interface ITenantRoleGuard
{
    bool StoredBeyondTheSession(TenantRole role);

    bool ChangesBeyondTheSession(TenantRole role);

    bool ResourceFlagChangeBeyondTheSession(TenantRole role);

    bool NewRoleFlagBeyondTheSession(TenantRole role);
}
