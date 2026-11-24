namespace Staffora.Core.Entities.Identity;

public interface IUserPasswordIssuer
{
    void IssueNewPassword(ApplicationUser user);

    Task RemoveOtherSignInMeansAsync(Microsoft.EntityFrameworkCore.DbContext context, Guid userId, CancellationToken cancellationToken);
}
