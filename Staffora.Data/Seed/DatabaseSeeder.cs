using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;

namespace Staffora.Data.Seed;

public sealed class DatabaseSeeder
{
    public const string PlatformTenantName = "Platform";

    public const string AdminRole = "Admin";

    private static readonly SeedUser platformOwner = new("admin@staffora.system", "Admin123!", "Platform", "Owner");

    private readonly IApplicationDbContextFactory<ApplicationDbContext> contexts;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly RoleManager<ApplicationRole> roleManager;
    private readonly ILogger<DatabaseSeeder> logger;

    public DatabaseSeeder(IApplicationDbContextFactory<ApplicationDbContext> contexts, UserManager<ApplicationUser> userManager, RoleManager<ApplicationRole> roleManager, ILogger<DatabaseSeeder> logger)
    {
        this.contexts = contexts;
        this.userManager = userManager;
        this.roleManager = roleManager;
        this.logger = logger;
    }

    public async Task<Guid?> SeedAsync(bool includeDevelopmentData, CancellationToken cancellationToken = default)
    {
        await using var context = this.contexts.CreateSystemDbContext();
        using var lease = context.BeginSystemScope("seed");

        await EnsureAdminRoleAsync(this.roleManager);
        if (!includeDevelopmentData)
        {
            return null;
        }

        return await SeedPlatformAsync(context, cancellationToken);
    }

    internal static async Task<Guid?> FindPlatformOwnerIdAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var adminRoleIds = context.Roles.Where(role => role.Name == AdminRole).Select(role => role.Id);
        return await context.Users
            .Where(user => context.Tenants.Any(tenant => tenant.Id == user.TenantId && tenant.IsRootTenant)
                && context.UserRoles.Any(holding => holding.UserId == user.Id && adminRoleIds.Contains(holding.RoleId)))
            .OrderBy(user => user.Created)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal static async Task EnsureAdminRoleAsync(RoleManager<ApplicationRole> roleManager)
    {
        if (!await roleManager.RoleExistsAsync(AdminRole))
        {
            Require(await roleManager.CreateAsync(new ApplicationRole { Name = AdminRole }), "the Admin role");
        }
    }

    private async Task<Guid> SeedPlatformAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        var platform = await context.Tenants.FirstOrDefaultAsync(tenant => tenant.IsRootTenant, cancellationToken);
        if (platform is null)
        {
            platform = new Tenant { Name = PlatformTenantName, State = TenantState.Active, IsRootTenant = true };
            context.Tenants.Add(platform);
            await context.SaveChangesAsync(cancellationToken);
            this.logger.LogInformation("Seeded the platform tenant.");
        }

        await EnsureSystemAccountAsync(context, platform, cancellationToken);
        await EnsureCompanyRootAsync(context, platform, cancellationToken);
        if (await FindPlatformOwnerIdAsync(context, cancellationToken) is { } existingOwner)
        {
            return existingOwner;
        }

        var owner = await EnsureUserAsync(platformOwner, platform.Id);
        if (!await this.userManager.IsInRoleAsync(owner, AdminRole))
        {
            Require(await this.userManager.AddToRoleAsync(owner, AdminRole), "the platform owner's Admin role");
        }

        if (platform.CreatedById == Guid.Empty)
        {
            platform.CreatedById = owner.Id;
            await context.SaveChangesAsync(cancellationToken);
        }

        return owner.Id;
    }

    private async Task EnsureSystemAccountAsync(ApplicationDbContext context, Tenant tenant, CancellationToken cancellationToken)
    {
        if (await SystemAccountService.EnsureAsync(context, tenant.Id, cancellationToken))
        {
            this.logger.LogInformation("Seeded the system account of {Tenant}.", tenant.Name);
        }
    }

    private async Task<Guid> EnsureCompanyRootAsync(ApplicationDbContext context, Tenant tenant, CancellationToken cancellationToken)
    {
        var (rootId, created) = await CompanyRoot.EnsureAsync(context, tenant.Id, cancellationToken);
        if (created)
        {
            this.logger.LogInformation("Seeded the Company root of {Tenant}.", tenant.Name);
        }

        return rootId;
    }

    private async Task<ApplicationUser> EnsureUserAsync(SeedUser seed, Guid tenantId)
    {
        if (await this.userManager.FindByEmailAsync(seed.Email) is { } existing)
        {
            return existing;
        }

        var user = new ApplicationUser
        {
            UserName = seed.Email,
            Email = seed.Email,
            FirstName = seed.FirstName,
            LastName = seed.LastName,
            EmailConfirmed = true,
            State = UserState.Active,
            TenantId = tenantId,
            CreatedById = SystemAccount.IdFor(tenantId),
        };

        Require(await this.userManager.CreateAsync(user, seed.Password), $"the user {seed.Email}");
        return user;
    }

    private static void Require(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Seeding {what} failed: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        }
    }

    private sealed record SeedUser(string Email, string Password, string FirstName, string LastName);
}
