using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Resources;

public sealed class UserStepsKeepStaffMembers : IStepFollowUp
{
    private readonly ResourceLinker linker;

    public UserStepsKeepStaffMembers(ResourceLinker linker)
    {
        this.linker = linker;
    }

    public Type EntityType => typeof(ApplicationUser);

    public Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        var user = (ApplicationUser)entity;
        return taken.Outcome.Steps.Any(step => ReferenceEquals(step.Entity, user) && !Equals(step.From, step.To))
            ? this.linker.SyncAsync(context, user.TenantId, [user.Id], cancellationToken)
            : Task.CompletedTask;
    }
}

public sealed class RoleStepsKeepStaffMembers : IStepFollowUp
{
    private readonly ResourceLinker linker;

    public RoleStepsKeepStaffMembers(ResourceLinker linker)
    {
        this.linker = linker;
    }

    public Type EntityType => typeof(TenantRole);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        var role = (TenantRole)entity;
        var holders = await context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => holding.TenantRoleId == role.Id)
            .Select(holding => holding.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (holders.Count > 0)
        {
            await this.linker.SyncAsync(context, role.TenantId, holders, cancellationToken);
        }
    }
}

public sealed class UnitStepsKeepStaffMembers : IStepFollowUp
{
    private readonly ResourceLinker linker;

    public UnitStepsKeepStaffMembers(ResourceLinker linker)
    {
        this.linker = linker;
    }

    public Type EntityType => typeof(OrganizationUnit);

    public async Task RunAsync(ApplicationDbContext context, IStateEntity entity, StepFollowUpContext taken, CancellationToken cancellationToken)
    {
        var unit = (OrganizationUnit)entity;
        var moved = taken.Outcome.Steps.Where(step => step.Entity is OrganizationUnit && !Equals(step.From, step.To)).Select(step => step.Entity.Id).Distinct().ToList();
        if (moved.Count == 0)
        {
            return;
        }

        var holders = await context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => moved.Contains(holding.OrganizationUnitId) && holding.TenantRole.ApplyResourceMembershipToUser)
            .Select(holding => holding.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (holders.Count > 0)
        {
            await this.linker.SyncAsync(context, unit.TenantId, holders, cancellationToken);
        }
    }
}
