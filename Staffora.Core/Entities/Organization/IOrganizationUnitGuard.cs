namespace Staffora.Core.Entities.Organization;

public interface IOrganizationUnitGuard
{
    bool HoldingsBeyondTheSession(OrganizationUnit unit);
}
