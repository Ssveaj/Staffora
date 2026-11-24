using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Data;

internal static class TenantOnboardingWriter
{
    public const string PlatformTenantTakesNoRoles = "The platform tenant takes no roles.";

    public static async Task<RolePresetIds> WriteAsync(ApplicationDbContext context, Guid tenantId, ApplicationUser owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(owner);
        using var lease = context.BeginSystemScope("onboard a tenant: its role presets and its first system owner, on its behalf");
        if (await SystemOwners.RoleIdAsync(context, tenantId, cancellationToken) is not null)
        {
            throw new InvalidOperationException("The tenant is onboarded already: it has its System owner role.");
        }

        var roles = await AddRolePresetsAsync(context, tenantId, cancellationToken);
        var account = SystemAccount.IdFor(tenantId);
        owner.TenantId = tenantId;
        owner.State = UserState.Active;
        owner.IsSystemAccount = false;
        owner.CreatedById = account;
        context.Users.Add(owner);
        await context.SaveChangesAsync(cancellationToken);
        await AddHoldingAsync(context, tenantId, owner.Id, roles.SystemOwner.Id, cancellationToken);

        List<TransitionStep> steps =
        [
            new(owner, UserState.Create, UserState.Active, ApplicationUserWorkflowAttribute.CreateStep),
            .. roles.All.Select(role => new TransitionStep(role, TenantRoleState.Create, TenantRoleState.Active, TenantRoleWorkflowAttribute.CreateStep)),
        ];
        StateHistoryWriter.Add(context, steps);
        await context.SaveChangesAsync(cancellationToken);
        return roles.Ids;
    }

    public static async Task<PortalRoleIds> WriteManagedAsync(ApplicationDbContext context, Guid tenantId, ApplicationUser administrator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(administrator);
        using var lease = context.BeginSystemScope("enable a customer's portal: its roles and its first customer administrator, on its behalf");
        var tenant = await context.Tenants.AsNoTracking().Where(row => row.Id == tenantId).Select(row => new { row.ParentTenantId }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("A portal's roles are written only for an existing tenant.");
        if (tenant.ParentTenantId is null)
        {
            throw new InvalidOperationException("Only a managed tenant is onboarded as a customer portal.");
        }

        if (await context.TenantRoles.AnyAsync(role => role.TenantId == tenantId, cancellationToken))
        {
            throw new InvalidOperationException("The portal is onboarded already: it has its roles.");
        }

        var account = SystemAccount.IdFor(tenantId);
        var roles = RolePresets.Portal.Select(preset => NewRole(context, tenantId, preset, systemOwner: false)).ToList();
        await context.SaveChangesAsync(cancellationToken);

        administrator.TenantId = tenantId;
        administrator.State = UserState.Active;
        administrator.IsSystemAccount = false;
        administrator.CreatedById = account;
        context.Users.Add(administrator);
        await context.SaveChangesAsync(cancellationToken);
        var customerAdmin = roles[RolePresets.Portal.ToList().IndexOf(RolePresets.CustomerAdmin)];
        await AddHoldingAsync(context, tenantId, administrator.Id, customerAdmin.Id, cancellationToken);

        List<TransitionStep> steps =
        [
            new(administrator, UserState.Create, UserState.Active, ApplicationUserWorkflowAttribute.CreateStep),
            .. roles.Select(role => new TransitionStep(role, TenantRoleState.Create, TenantRoleState.Active, TenantRoleWorkflowAttribute.CreateStep)),
        ];
        StateHistoryWriter.AddOnBehalfOf(context, tenantId, steps);
        await context.SaveChangesAsync(cancellationToken);
        return new PortalRoleIds(customerAdmin.Id, roles[RolePresets.Portal.ToList().IndexOf(RolePresets.Orderer)].Id);
    }

    private static async Task<WrittenPresets> AddRolePresetsAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants.AsNoTracking().Where(row => row.Id == tenantId).Select(row => new { row.IsRootTenant }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Role presets are written only for an existing tenant.");
        if (tenant.IsRootTenant)
        {
            throw new InvalidOperationException(PlatformTenantTakesNoRoles);
        }

        var written = new WrittenPresets(
            NewRole(context, tenantId, RolePresets.SystemOwner, systemOwner: true),
            NewRole(context, tenantId, RolePresets.Administrator, systemOwner: false),
            NewRole(context, tenantId, RolePresets.Coordinator, systemOwner: false),
            NewRole(context, tenantId, RolePresets.Staff, systemOwner: false));
        await context.SaveChangesAsync(cancellationToken);
        return written;
    }

    private static TenantRole NewRole(ApplicationDbContext context, Guid tenantId, RolePreset preset, bool systemOwner)
    {
        var account = SystemAccount.IdFor(tenantId);
        var role = new TenantRole
        {
            TenantId = tenantId,
            Name = preset.Name,
            Description = preset.Description,
            IsSystemOwner = systemOwner,

            ApplyResourceMembershipToUser = preset.ApplyResourceMembershipToUser,

            State = TenantRoleState.Active,
            CreatedById = account,
        };
        foreach (var permission in preset.Permissions)
        {
            role.Permissions.Add(new TenantRolePermission { TenantId = tenantId, Permission = permission, CreatedById = account });
        }

        context.TenantRoles.Add(role);
        return role;
    }

    private static async Task AddHoldingAsync(ApplicationDbContext context, Guid tenantId, Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var rootId = await CompanyRoot.FindAsync(context, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("The tenant has no Company root to hold its System owner role on.");
        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole
        {
            TenantId = tenantId,
            UserId = userId,
            OrganizationUnitId = rootId,
            TenantRoleId = roleId,
            CreatedById = SystemAccount.IdFor(tenantId),
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed record WrittenPresets(TenantRole SystemOwner, TenantRole Administrator, TenantRole Coordinator, TenantRole Staff)
    {
        public IReadOnlyList<TenantRole> All => [SystemOwner, Administrator, Coordinator, Staff];

        public RolePresetIds Ids => new(SystemOwner.Id, Administrator.Id, Coordinator.Id, Staff.Id);
    }
}

public sealed record RolePresetIds(Guid SystemOwner, Guid Administrator, Guid Coordinator, Guid Staff);

public sealed record PortalRoleIds(Guid CustomerAdmin, Guid Orderer);
