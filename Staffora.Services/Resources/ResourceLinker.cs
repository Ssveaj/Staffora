using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Services.Resources;

public sealed class ResourceLinker
{
    private readonly WorkflowService workflows;

    public ResourceLinker(WorkflowService workflows)
    {
        this.workflows = workflows;
    }

    public static ResourceState TargetState(ResourceState current, bool holdsStaffRole, bool userIsActive) =>
        (holdsStaffRole, userIsActive, current) switch
        {
            (true, true, _) => ResourceState.Active,
            (true, false, ResourceState.Active) => ResourceState.Inactive,
            (true, false, _) => current,
            (false, _, _) => ResourceState.Inactive,
        };

    public static string? StepBetween(ResourceState from, ResourceState to) => (from, to) switch
    {
        (ResourceState.Registration, ResourceState.Active) => ResourceWorkflowAttribute.ActivateStep,
        (ResourceState.Active, ResourceState.Inactive) => ResourceWorkflowAttribute.DeactivateStep,
        (ResourceState.Inactive, ResourceState.Active) => ResourceWorkflowAttribute.ReactivateStep,
        (ResourceState.Registration, ResourceState.Inactive) => ResourceWorkflowAttribute.WithdrawStep,
        _ when from == to => null,
        _ => throw new InvalidOperationException($"A staff record is never moved from {from} to {to}."),
    };

    public async Task SyncAsync(ApplicationDbContext context, Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(userIds);
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The resource linker writes inside the transaction of the write that calls for it: begin one first.");
        }

        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var users = await context.Users.AsNoTracking()
            .Where(user => ids.Contains(user.Id) && user.TenantId == tenantId && !user.IsSystemAccount)
            .Select(user => new { user.Id, user.State })
            .ToListAsync(cancellationToken);
        if (users.Count == 0)
        {
            return;
        }

        var found = users.Select(user => user.Id).ToList();
        var units = (await context.OrganizationUnitUserRoles.AsNoTracking()
                .Where(holding => found.Contains(holding.UserId)
                    && holding.TenantId == tenantId
                    && holding.TenantRole.ApplyResourceMembershipToUser
                    && holding.TenantRole.State == TenantRoleState.Active
                    && (holding.OrganizationUnit.State == OrganizationUnitState.Active || holding.OrganizationUnit.UnitType == OrganizationUnitType.Company))
                .Select(holding => new { holding.UserId, holding.OrganizationUnitId })
                .ToListAsync(cancellationToken))
            .GroupBy(holding => holding.UserId)
            .ToDictionary(group => group.Key, group => group.Select(holding => holding.OrganizationUnitId).ToHashSet());

        var records = await context.Resources
            .Include(resource => resource.Memberships)
            .Where(resource => found.Contains(resource.UserId) && resource.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var plan = new List<(Resource Record, ResourceState Target)>();
        foreach (var user in users)
        {
            var held = units.GetValueOrDefault(user.Id) ?? [];
            var record = records.FirstOrDefault(resource => resource.UserId == user.Id);
            if (record is null)
            {
                if (held.Count == 0)
                {
                    continue;
                }

                record = new Resource { TenantId = tenantId, UserId = user.Id, State = ResourceState.Registration };
                context.Resources.Add(record);
            }

            foreach (var membership in record.Memberships.Where(membership => !held.Contains(membership.OrganizationUnitId)).ToList())
            {
                record.Memberships.Remove(membership);
                context.ResourceUnitMemberships.Remove(membership);
            }

            foreach (var unitId in held.Where(unitId => !record.Memberships.Any(membership => membership.OrganizationUnitId == unitId)))
            {
                record.Memberships.Add(new ResourceUnitMembership { TenantId = tenantId, OrganizationUnitId = unitId });
            }

            plan.Add((record, TargetState(record.State, held.Count > 0, user.State == UserState.Active)));
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        foreach (var (record, target) in plan)
        {
            if (StepBetween(record.State, target) is { } step)
            {
                await this.workflows.RunSystemStepInTransactionAsync<ResourceState, Resource>(context, record, step, cancellationToken);
            }
        }
    }
}
