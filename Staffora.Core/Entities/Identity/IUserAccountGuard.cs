namespace Staffora.Core.Entities.Identity;

public interface IUserAccountGuard
{
    bool IsPlatformOwner(ApplicationUser user);

    bool HoldsMoreThanTheSession(ApplicationUser user);

    bool SignInNameChanged(ApplicationUser user);

    bool IsOwnerBeyondTheSession(ApplicationUser user);

    bool SessionIsOwnerOf(ApplicationUser user);

    bool IsLastActiveOwner(ApplicationUser user);

    bool IsLastActiveCustomerAdministrator(ApplicationUser user);
}
