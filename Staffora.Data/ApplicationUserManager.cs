using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Identity;

namespace Staffora.Data;

public sealed class ApplicationUserManager : UserManager<ApplicationUser>
{
    private readonly TimeProvider clock;

    public ApplicationUserManager(IUserStore<ApplicationUser> store, IOptions<IdentityOptions> optionsAccessor, IPasswordHasher<ApplicationUser> passwordHasher, IEnumerable<IUserValidator<ApplicationUser>> userValidators, IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators, ILookupNormalizer keyNormalizer, IdentityErrorDescriber errors, IServiceProvider services, ILogger<UserManager<ApplicationUser>> logger, TimeProvider clock) : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        this.clock = clock;
    }

    public override async Task<IdentityResult> AccessFailedAsync(ApplicationUser user)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(user);
        if (Store is not ApplicationUserStore store || !SupportsUserLockout)
        {
            return await base.AccessFailedAsync(user);
        }

        var lockout = Options.Lockout;
        if (!await store.CountFailedAccessAsync(user, lockout.MaxFailedAccessAttempts, this.clock.GetUtcNow(), lockout.DefaultLockoutTimeSpan, CancellationToken))
        {
            return IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure());
        }

        if (user.LockoutEnd is { } end && end > this.clock.GetUtcNow())
        {
            Logger.LogWarning("User {UserId} is locked out after failed sign-in attempts.", user.Id);
        }

        return IdentityResult.Success;
    }
}
