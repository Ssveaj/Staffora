using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;

namespace Staffora.Data.Seed;

public sealed class PlatformBootstrap
{
    public const string Command = "bootstrap-platform-owner";

    private const int PasswordLength = 24;
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#$%&*+-=?@_";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly RoleManager<ApplicationRole> roleManager;
    private readonly ILogger<PlatformBootstrap> logger;

    public PlatformBootstrap(IApplicationDbContextFactory<ApplicationDbContext> contexts, UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager, ILogger<PlatformBootstrap> logger)
    {
        this.contexts = contexts;
        this.userManager = userManager;
        this.roleManager = roleManager;
        this.logger = logger;
    }

    public static string? ParseEmail(string? email)
    {
        var trimmed = email?.Trim();
        return MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed ? address.Address : null;
    }

    public async Task<Result<PlatformOwnerCredentials>> BootstrapAsync(string email, CancellationToken cancellationToken = default)
    {
        if (ParseEmail(email) is not { } address)
        {
            return Result<PlatformOwnerCredentials>.Failure("Give the first platform owner's email address, such as owner@example.com.");
        }

        await using var context = this.contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("bootstrap the platform tenant and its first owner");

        await context.Database.OpenConnectionAsync(cancellationToken);
        if (!await AppLock.PlatformBootstrap.TryTakeForSessionAsync(context, cancellationToken))
        {
            this.logger.LogWarning("Refused to bootstrap the platform: another bootstrap held its lock too long.");
            return Result<PlatformOwnerCredentials>.Failure("Another bootstrap is running on this database, so this one changed nothing. Run it again once that one has finished.");
        }

        try
        {
            return await BootstrapLockedAsync(context, address, cancellationToken);
        }
        finally
        {
            await AppLock.PlatformBootstrap.ReleaseForSessionAsync(context);
            await context.Database.CloseConnectionAsync();
        }
    }

    private async Task<Result<PlatformOwnerCredentials>> BootstrapLockedAsync(ApplicationDbContext context, string address, CancellationToken cancellationToken)
    {
        if (await DatabaseSeeder.FindPlatformOwnerIdAsync(context, cancellationToken) is not null)
        {
            this.logger.LogWarning("Refused to bootstrap the platform: a platform owner exists already.");
            return Result<PlatformOwnerCredentials>.Failure("A platform owner exists already, so the bootstrap changed nothing. It runs once, on a database without one.");
        }

        await DatabaseSeeder.EnsureAdminRoleAsync(this.roleManager);
        var platform = await context.Tenants.FirstOrDefaultAsync(tenant => tenant.IsRootTenant, cancellationToken);
        if (platform is null)
        {
            platform = new Tenant { Name = DatabaseSeeder.PlatformTenantName, State = TenantState.Active, IsRootTenant = true };
            context.Tenants.Add(platform);
            await context.SaveChangesAsync(cancellationToken);
        }

        await SystemAccountService.EnsureAsync(context, platform.Id, cancellationToken);

        await CompanyRoot.EnsureAsync(context, platform.Id, cancellationToken);

        var password = NewPassword();
        var owner = new ApplicationUser
        {
            UserName = address,
            Email = address,
            FirstName = "Platform",
            LastName = "Owner",
            EmailConfirmed = true,
            State = UserState.Active,
            TenantId = platform.Id,
            CreatedById = SystemAccount.IdFor(platform.Id),
        };

        var created = await this.userManager.CreateAsync(owner, password);
        if (!created.Succeeded)
        {
            return Result<PlatformOwnerCredentials>.Failure($"The first platform owner could not be created: {Describe(created)}");
        }

        var role = await this.userManager.AddToRoleAsync(owner, DatabaseSeeder.AdminRole);
        if (!role.Succeeded)
        {
            await this.userManager.DeleteAsync(owner);
            return Result<PlatformOwnerCredentials>.Failure($"The first platform owner could not be given the Admin role: {Describe(role)}");
        }

        if (platform.CreatedById == Guid.Empty)
        {
            platform.CreatedById = owner.Id;
            await context.SaveChangesAsync(cancellationToken);
        }

        this.logger.LogInformation("Bootstrapped the platform tenant {TenantId} and its first owner {UserId}.", platform.Id, owner.Id);
        return Result<PlatformOwnerCredentials>.Success(new PlatformOwnerCredentials(address, password));
    }

    private static string NewPassword()
    {
        var characters = new char[PasswordLength];
        characters[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        characters[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        characters[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        characters[3] = Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)];
        RandomNumberGenerator.GetItems((Upper + Lower + Digits + Symbols).AsSpan(), characters.AsSpan(4));
        RandomNumberGenerator.Shuffle(characters.AsSpan());
        return new string(characters);
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(error => error.Description));
}

public sealed record PlatformOwnerCredentials(string Email, string InitialPassword)
{
    public override string ToString() => $"Platform owner {Email}";
}
