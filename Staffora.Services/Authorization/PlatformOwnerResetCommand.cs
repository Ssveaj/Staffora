using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Services.Services;

namespace Staffora.Services.Authorization;

public sealed class PlatformOwnerResetCommand
{
    public const string Command = "reset-platform-owner";

    public const string NoAddress = "Give the email address of the platform owner whose password to reset, such as owner@example.com.";

    private readonly UserManager<ApplicationUser> userManager;
    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly UserPasswordIssuer issuer;
    private readonly ILogger<PlatformOwnerResetCommand> logger;

    public PlatformOwnerResetCommand(UserManager<ApplicationUser> userManager, IApplicationDbContextFactory<ApplicationDbContext> contexts, UserPasswordIssuer issuer, ILogger<PlatformOwnerResetCommand> logger)
    {
        this.userManager = userManager;
        this.contexts = contexts;
        this.issuer = issuer;
        this.logger = logger;
    }

    public static string NoSuchUser(string address) => $"No user signs in with {address}.";

    public static string NotAPlatformOwner(string address) =>
        $"{address} is not a platform owner, so nothing was changed. A tenant's users get a new password from their own organization's administrators.";

    public async Task<Result<PlatformOwnerCredentials>> ResetAsync(string email, CancellationToken cancellationToken = default)
    {
        if (PlatformBootstrap.ParseEmail(email) is not { } address)
        {
            return Result<PlatformOwnerCredentials>.Failure(NoAddress);
        }

        var found = await this.userManager.FindByEmailAsync(address);
        if (found is null || found.IsSystemAccount)
        {
            return Result<PlatformOwnerCredentials>.Failure(NoSuchUser(address));
        }

        if (!await this.userManager.IsInRoleAsync(found, AppRoles.Admin))
        {
            return Refused(found, address);
        }

        var runtime = new RuntimeExecutionContext(found.TenantId, SystemAccount.IdFor(found.TenantId));
        await using var context = this.contexts.CreateDbContext(runtime);
        if (!await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == found.TenantId && tenant.IsRootTenant, cancellationToken))
        {
            return Refused(found, address);
        }

        var owner = await context.Users.SingleAsync(user => user.Id == found.Id && user.TenantId == found.TenantId, cancellationToken);
        this.issuer.IssueNewPassword(owner);
        await this.issuer.RemoveOtherSignInMeansAsync(context, owner.Id, cancellationToken);
        StateHistoryWriter.Add(context, [new TransitionStep(owner, owner.State, owner.State, ApplicationUserWorkflowAttribute.NewPasswordStep)]);
        await context.SaveChangesAsync(cancellationToken);

        var password = this.issuer.TakeIssued(owner.Id) ?? throw new InvalidOperationException("The issued password was not kept for the command.");
        this.logger.LogWarning("Reset the password of platform owner {UserId} from the command line; every earlier sign-in of theirs has ended.", owner.Id);
        return Result<PlatformOwnerCredentials>.Success(new PlatformOwnerCredentials(address, password));
    }

    private Result<PlatformOwnerCredentials> Refused(ApplicationUser user, string address)
    {
        this.logger.LogWarning("Refused to reset the password of user {UserId} from the command line: not a platform owner.", user.Id);
        return Result<PlatformOwnerCredentials>.Failure(NotAPlatformOwner(address));
    }
}
