using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Data.Seed;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record ScenarioTenant(Guid TenantId, string Name, Guid OwnerId)
{
    public IRuntimeExecutionContext Runtime() => new RuntimeExecutionContext(TenantId, OwnerId);

    public IRuntimeExecutionContext RuntimeOf(Guid userId) => new RuntimeExecutionContext(TenantId, userId);

    public RuntimeExecutionContext PlatformOwnerRuntime()
    {
        var runtime = new RuntimeExecutionContext();
        runtime.SetUser(OwnerId, isSuperAdmin: true);
        runtime.SetBaseTenant(TenantId, isPlatformTenant: true);
        runtime.MarkInitialized();
        return runtime;
    }

    public Guid SystemAccountId => SystemAccount.IdFor(TenantId);
}

public sealed record TwoTenantScenario(ScenarioTenant A, ScenarioTenant B)
{
    public static async Task<TwoTenantScenario> CreateAsync(RealDatabase database, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateSystemContext();
        var a = await AddAsync(context, "Company A", isPlatformTenant: false, cancellationToken);
        var b = await AddAsync(context, "Company B", isPlatformTenant: false, cancellationToken);
        return new TwoTenantScenario(a, b);
    }

    public static async Task<ScenarioTenant> AddTenantAsync(RealDatabase database, string name, CancellationToken cancellationToken = default, TenantState state = TenantState.Active)
    {
        await using var context = database.CreateSystemContext();
        return await AddAsync(context, name, isPlatformTenant: false, cancellationToken, state);
    }

    public static async Task<ScenarioTenant> AddPlatformTenantAsync(RealDatabase database, CancellationToken cancellationToken = default)
    {
        ScenarioTenant platform;
        await using (var context = database.CreateSystemContext())
        {
            var existing = await context.Tenants.FirstOrDefaultAsync(tenant => tenant.IsRootTenant, cancellationToken);
            if (existing is null)
            {
                platform = await AddAsync(context, DatabaseSeeder.PlatformTenantName, isPlatformTenant: true, cancellationToken);
            }
            else
            {
                var owner = NewUser("Owner", existing.Name, existing.Id);
                context.Users.Add(owner);
                await context.SaveChangesAsync(cancellationToken);
                platform = new ScenarioTenant(existing.Id, existing.Name, owner.Id);
            }
        }

        await GiveAdminRoleAsync(database, platform.OwnerId, cancellationToken);
        return platform;
    }

    public static Task GiveAdminRoleAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken = default) =>
        GiveIdentityRoleAsync(database, userId, DatabaseSeeder.AdminRole, cancellationToken);

    public static async Task GiveIdentityRoleAsync(RealDatabase database, Guid userId, string roleName, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateSystemContext();
        var role = await context.Roles.FirstOrDefaultAsync(row => row.Name == roleName, cancellationToken);
        if (role is null)
        {
            role = new ApplicationRole { Id = Guid.NewGuid(), Name = roleName, NormalizedName = roleName.ToUpperInvariant() };
            context.Roles.Add(role);
        }

        context.UserRoles.Add(new() { UserId = userId, RoleId = role.Id });
        await context.SaveChangesAsync(cancellationToken);
    }

    public static async Task<Guid> AddUserAsync(RealDatabase database, ScenarioTenant tenant, string firstName, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateSystemContext();
        var user = NewUser(firstName, tenant.Name, tenant.TenantId);
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    public static async Task GrantAsync(RealDatabase database, ScenarioTenant tenant, Guid userId, CancellationToken cancellationToken, params RolePermission[] permissions)
    {
        var rootId = await RootUnitIdAsync(database, tenant, cancellationToken);
        await using var context = database.CreateSystemContext();
        var role = new TenantRole
        {
            TenantId = tenant.TenantId,
            Name = $"Granted {Guid.NewGuid():N}",

            State = TenantRoleState.Active,
            CreatedById = tenant.OwnerId,
            Permissions = [.. permissions.Select(permission => new TenantRolePermission { TenantId = tenant.TenantId, Permission = permission, CreatedById = tenant.OwnerId })],
        };
        context.TenantRoles.Add(role);
        await context.SaveChangesAsync(cancellationToken);

        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole
        {
            TenantId = tenant.TenantId,
            UserId = userId,
            OrganizationUnitId = rootId,
            TenantRoleId = role.Id,
            CreatedById = tenant.OwnerId,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public static async Task MakeSystemOwnerAsync(RealDatabase database, ScenarioTenant tenant, Guid userId, CancellationToken cancellationToken)
    {
        var rootId = await RootUnitIdAsync(database, tenant, cancellationToken);
        await using var context = database.CreateSystemContext();
        var roleId = await SystemOwners.RoleIdAsync(context, tenant.TenantId, cancellationToken);
        if (roleId is null)
        {
            var role = new TenantRole { TenantId = tenant.TenantId, Name = "System owner", State = TenantRoleState.Active, IsSystemOwner = true, CreatedById = tenant.OwnerId };
            context.TenantRoles.Add(role);
            await context.SaveChangesAsync(cancellationToken);
            roleId = role.Id;
        }

        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole
        {
            TenantId = tenant.TenantId,
            UserId = userId,
            OrganizationUnitId = rootId,
            TenantRoleId = roleId.Value,
            CreatedById = tenant.OwnerId,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public static async Task<Guid> RootUnitIdAsync(RealDatabase database, ScenarioTenant tenant, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateSystemContext();
        if (await CompanyRoot.FindAsync(context, tenant.TenantId, cancellationToken) is { } rootId)
        {
            return rootId;
        }

        var root = new OrganizationUnit { TenantId = tenant.TenantId, Name = tenant.Name, UnitType = OrganizationUnitType.Company, State = OrganizationUnitState.Active, CreatedById = tenant.OwnerId };
        context.OrganizationUnits.Add(root);
        await context.SaveChangesAsync(cancellationToken);
        return root.Id;
    }

    private static async Task<ScenarioTenant> AddAsync(TestDataContext context, string name, bool isPlatformTenant, CancellationToken cancellationToken, TenantState state = TenantState.Active)
    {
        name = isPlatformTenant ? name : UniqueName(name);
        var tenant = new Tenant { Name = name, State = state, IsRootTenant = isPlatformTenant };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync(cancellationToken);
        await SystemAccountService.EnsureAsync(context, tenant.Id, cancellationToken);
        await CompanyRoot.EnsureAsync(context, tenant.Id, cancellationToken);

        var owner = NewUser("Owner", name, tenant.Id);
        context.Users.Add(owner);
        await context.SaveChangesAsync(cancellationToken);
        return new ScenarioTenant(tenant.Id, tenant.Name, owner.Id);
    }

    public static string UniqueName(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static ApplicationUser NewUser(string firstName, string tenantName, Guid tenantId)
    {
        var email = $"{firstName.ToLowerInvariant()}.{Guid.NewGuid():N}@{tenantName.Replace(' ', '-').ToLowerInvariant()}.example";
        return new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),

            State = UserState.Active,
            FirstName = firstName,
            LastName = tenantName,
            TenantId = tenantId,
            CreatedById = SystemAccount.IdFor(tenantId),
        };
    }
}
