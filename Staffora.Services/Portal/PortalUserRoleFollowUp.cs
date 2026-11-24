using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.Portal;

public sealed class PortalUserRoleFollowUp : IStepFollowUp
{
    public const string RoleBeyondCeiling = "That role grants permissions you do not hold, so the user was not created.";

    public const string RoleMissing = "Your organization has no such role in use, so the user was not created.";

    private readonly PermissionService permissions;

    public PortalUserRoleFollowUp(PermissionService permissions)
    {
        this.permissions = permissions;
    }

    public Type EntityType => typeof(ApplicationUser);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(taken);
        if (entity is not ApplicationUser user
            || taken.Outcome.Steps.FirstOrDefault(step => ReferenceEquals(step.Entity, user) && ApplicationUserWorkflowAttribute.PortalRoleOf(step.TransitionKey) is not null) is not { } created
            || ApplicationUserWorkflowAttribute.PortalRoleOf(created.TransitionKey) is not { } preset)
        {
            return;
        }

        var tenantId = user.TenantId;
        var role = await context.TenantRoles.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.Name == preset.Name && !row.IsSystemOwner && row.State == TenantRoleState.Active)
            .Select(row => new { row.Id, Permissions = row.Permissions.Select(permission => permission.Permission).ToList() })
            .SingleOrDefaultAsync(cancellationToken);
        var rootId = await CompanyRoot.FindAsync(context, tenantId, cancellationToken);
        if (role is null || rootId is null)
        {
            throw new StepFollowUpRefusedException(RoleMissing);
        }

        foreach (var permission in role.Permissions.Where(permission => !RolePermissions.IsPersonal(permission)))
        {
            if (!await this.permissions.HasPermissionAsync(permission, cancellationToken))
            {
                throw new StepFollowUpRefusedException(RoleBeyondCeiling);
            }
        }

        context.OrganizationUnitUserRoles.Add(new OrganizationUnitUserRole
        {
            TenantId = tenantId,
            UserId = user.Id,
            OrganizationUnitId = rootId.Value,
            TenantRoleId = role.Id,
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}
