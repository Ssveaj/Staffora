using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Models;
using Staffora.Data.Seed;

namespace Staffora.Services.Authorization;

public sealed class AccountUnlockCommand
{
    public const string Command = "unlock-user";

    private readonly UserManager<ApplicationUser> userManager;
    private readonly ILogger<AccountUnlockCommand> logger;

    public AccountUnlockCommand(UserManager<ApplicationUser> userManager, ILogger<AccountUnlockCommand> logger)
    {
        this.userManager = userManager;
        this.logger = logger;
    }

    public async Task<Result> UnlockAsync(string email)
    {
        if (PlatformBootstrap.ParseEmail(email) is not { } address)
        {
            return Result.Failure("Give the email address of the account to unlock, such as owner@example.com.");
        }

        var user = await this.userManager.FindByEmailAsync(address);
        if (user is null || user.IsSystemAccount)
        {
            return Result.Failure($"No user signs in with {address}.");
        }

        if (await this.userManager.GetLockoutEndDateAsync(user) is not null)
        {
            var ended = await this.userManager.SetLockoutEndDateAsync(user, null);
            if (!ended.Succeeded)
            {
                return Result.Failure(string.Join(" ", ended.Errors.Select(error => error.Description)));
            }
        }

        var reset = await this.userManager.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded)
        {
            return Result.Failure(string.Join(" ", reset.Errors.Select(error => error.Description)));
        }

        this.logger.LogWarning("Lifted the sign-in lockout of user {UserId} from the command line.", user.Id);
        return Result.Success();
    }
}
