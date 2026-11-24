using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Workflow;

namespace Staffora.Services.Tenancy;

public class RoleHoldingService
{
    public const string InactiveUnitTakesNoRoles = "The unit is not in use, so it takes no new role holdings. Reactivate it first.";

    public const string InactiveUserTakesNoRoles = "The user is inactive, so they take no new roles. Activate them first.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly ResourceLinker resourceLinker;
    private readonly Staffora.Services.Coordination.TenantChangeBus changes;
    private readonly ILogger<RoleHoldingService> log;

    public RoleHoldingService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, ResourceLinker resourceLinker, Staffora.Services.Coordination.TenantChangeBus changes, ILogger<RoleHoldingService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.resourceLinker = resourceLinker;
        this.changes = changes;
        this.log = log;
    }

    private enum HoldingColumn
    {
        Person,
        Role,
        Unit,
    }

    private const string NoRolesView = "You do not have permission to view role assignments.";

    public async Task<Result<ListPage<UserRoleHoldingRow>>> QueryTenantRoleHoldersAsync(Guid roleId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView, cancellationToken))
            return Result<ListPage<UserRoleHoldingRow>>.Failure(NoRolesView);

        var list = RoleHolderList.Definition.Normalize(query);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "list the holders of a role of any tenant");
        var holdings = context.OrganizationUnitUserRoles.AsNoTracking().Where(holding => holding.TenantRoleId == roleId);
        var sortedBy = list.Sort == RoleHolderList.SortUnit ? HoldingColumn.Unit : HoldingColumn.Person;
        return Result<ListPage<UserRoleHoldingRow>>.Success(await PageOfHoldingsAsync(holdings, list, HoldingColumn.Role, sortedBy, cancellationToken));
    }

    public async Task<Result<ListPage<UserRoleHoldingRow>>> QueryOrganizationUnitRoleHoldingsAsync(Guid unitId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView, cancellationToken))
            return Result<ListPage<UserRoleHoldingRow>>.Failure(NoRolesView);

        var list = UnitRoleHoldingList.Definition.Normalize(query);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "list the role holdings on a unit of any tenant");
        var holdings = context.OrganizationUnitUserRoles.AsNoTracking().Where(holding => holding.OrganizationUnitId == unitId);
        var sortedBy = list.Sort == UnitRoleHoldingList.SortRole ? HoldingColumn.Role : HoldingColumn.Person;
        return Result<ListPage<UserRoleHoldingRow>>.Success(await PageOfHoldingsAsync(holdings, list, HoldingColumn.Unit, sortedBy, cancellationToken));
    }

    public async Task<Result<ListPage<UserRoleHoldingRow>>> QueryApplicationUserRoleHoldingsAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView, cancellationToken))
            return Result<ListPage<UserRoleHoldingRow>>.Failure(NoRolesView);

        var list = UserRoleHoldingList.Definition.Normalize(query);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "list the role holdings of a user of any tenant");
        var holdings = context.OrganizationUnitUserRoles.AsNoTracking().Where(holding => holding.UserId == userId && !holding.User.IsSystemAccount);
        var sortedBy = list.Sort == UserRoleHoldingList.SortUnit ? HoldingColumn.Unit : HoldingColumn.Role;
        return Result<ListPage<UserRoleHoldingRow>>.Success(await PageOfHoldingsAsync(holdings, list, HoldingColumn.Person, sortedBy, cancellationToken));
    }

    private static async Task<ListPage<UserRoleHoldingRow>> PageOfHoldingsAsync(IQueryable<OrganizationUnitUserRole> holdings, ListQuery list, HoldingColumn held, HoldingColumn sortedBy, CancellationToken cancellationToken)
    {
        if (list.Search is { Length: > 0 } search)
        {
            holdings = held switch
            {
                HoldingColumn.Role => holdings.Where(holding => holding.OrganizationUnit.Name.Contains(search) || (holding.User.FirstName + " " + holding.User.LastName).Contains(search) || (holding.User.Email != null && holding.User.Email.Contains(search))),
                HoldingColumn.Unit => holdings.Where(holding => holding.TenantRole.Name.Contains(search) || (holding.User.FirstName + " " + holding.User.LastName).Contains(search) || (holding.User.Email != null && holding.User.Email.Contains(search))),
                _ => holdings.Where(holding => holding.TenantRole.Name.Contains(search) || holding.OrganizationUnit.Name.Contains(search)),
            };
        }

        var total = await holdings.CountAsync(cancellationToken);
        var page = ListPage<UserRoleHoldingRow>.PageWithin(list, total);
        var thenBy = Enum.GetValues<HoldingColumn>().Single(column => column != held && column != sortedBy);
        var order = OrderBy(OrderBy(new ListOrder<OrganizationUnitUserRole>(holdings), sortedBy, list.Descending), thenBy, held == HoldingColumn.Person && list.Descending);
        var rows = await order.By(holding => holding.Id).Query
            .Skip((page - 1) * list.PageSize)
            .Take(list.PageSize)
            .Select(holding => new
            {
                holding.Id,
                Role = holding.TenantRole.Name,
                Unit = holding.OrganizationUnit.Name,
                holding.OrganizationUnit.State,
                holding.OrganizationUnit.UnitType,
                holding.TenantId,
                holding.TenantRoleId,
                Person = (holding.User.FirstName + " " + holding.User.LastName).Trim(),
                holding.User.Email,
                holding.UserId,
            })
            .ToListAsync(cancellationToken);
        List<UserRoleHoldingRow> items = [.. rows.Select(row => new UserRoleHoldingRow(row.Id, row.Role, row.Unit, row.State, row.UnitType == OrganizationUnitType.Company, row.TenantId, row.TenantRoleId)
        {
            Person = held == HoldingColumn.Person ? null : row.Person.Length > 0 ? row.Person : row.Email ?? "Unknown user",
            UserId = row.UserId,
        })];
        return ListPage<UserRoleHoldingRow>.Ungrouped(items, total, page, list.PageSize);
    }

    private static ListOrder<OrganizationUnitUserRole> OrderBy(ListOrder<OrganizationUnitUserRole> order, HoldingColumn column, bool descending) => column switch
    {
        HoldingColumn.Person => order.By(holding => holding.User.FirstName, descending).By(holding => holding.User.LastName, descending),
        HoldingColumn.Role => order.By(holding => holding.TenantRole.Name, descending),
        _ => order.By(holding => holding.OrganizationUnit.Name, descending),
    };

    public async Task<Result<List<OrganizationUnitUserRole>>> FetchOwnRoleHoldingsAsync()
    {
        try
        {
            var userId = this.runtime.EffectiveUserId;
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var holdings = await context.OrganizationUnitUserRoles.AsNoTracking()
                .Where(holding => holding.UserId == userId)
                .Include(holding => holding.OrganizationUnit)
                .Include(holding => holding.TenantRole)
                .ToListAsync();
            return Result<List<OrganizationUnitUserRole>>.Success(holdings);
        }
        catch (Exception)
        {
            return Result<List<OrganizationUnitUserRole>>.Failure("An error occurred while fetching your role holdings.");
        }
    }

    private void PublishChanges(Staffora.Services.Coordination.TenantChangeCollector touched, string what)
    {
        try
        {
            touched.PublishTo(this.changes);
        }
        catch (Exception exception)
        {
            this.log.LogError(exception, "Publishing the changes of {What} failed after its commit; the holding stands, and open pages read again when they next load.", what);
        }
    }

    public async Task<Result<OrganizationUnitUserRole>> AssignUserToRoleAsync(Guid userId, Guid organizationUnitId, Guid tenantRoleId, Guid tenantId)
    {
        try
        {
            if (await RoleHoldingRefusalAsync(tenantId) is { } refusal)
                return Result<OrganizationUnitUserRole>.Failure(refusal);

            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var touched = Staffora.Services.Coordination.TenantChangeCollector.Attach(context, tenantId);
            await using var transaction = await context.Database.BeginTransactionAsync(ApplicationDbContext.OrganizationStructureIsolationLevel);
            await context.LockOrganizationStructureAsync(tenantId);
            if (await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.IsRootTenant).FirstOrDefaultAsync())
                return Result<OrganizationUnitUserRole>.Failure(ApplicationDbContext.PlatformTreeIsRootOnlyReason);

            var holder = await context.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new { user.TenantId, user.IsSystemAccount, user.State })
                .FirstOrDefaultAsync();
            if (holder is null || holder.TenantId != tenantId)
                return Result<OrganizationUnitUserRole>.Failure("The user is not a user of this tenant.");

            if (holder.IsSystemAccount)
                return Result<OrganizationUnitUserRole>.Failure("A tenant's system account holds no role.");

            if (holder.State != UserState.Active)
                return Result<OrganizationUnitUserRole>.Failure(InactiveUserTakesNoRoles);

            var unit = await context.OrganizationUnits.AsNoTracking()
                .Where(row => row.Id == organizationUnitId)
                .Select(row => new { row.UnitType, row.State })
                .FirstOrDefaultAsync();
            if (unit is null)
                return Result<OrganizationUnitUserRole>.Failure("The organization unit was not found in this tenant.");

            if (OrganizationStructurePolicy.CheckRoleHolding(unit.UnitType) is { } reason)
                return Result<OrganizationUnitUserRole>.Failure(reason);

            if (unit.State != OrganizationUnitState.Active && unit.UnitType != OrganizationUnitType.Company)
                return Result<OrganizationUnitUserRole>.Failure(InactiveUnitTakesNoRoles);

            if (OrganizationStructurePolicy.CheckRoleHolding(await OrganizationService.LoadStructureAsync(context, tenantId), organizationUnitId) is { } placeReason)
                return Result<OrganizationUnitUserRole>.Failure(placeReason);

            if (!await context.TenantRoles.AnyAsync(role => role.Id == tenantRoleId && role.State == TenantRoleState.Active))
                return Result<OrganizationUnitUserRole>.Failure("The role was not found in this tenant.");

            if (await context.TenantRoles.AnyAsync(role => role.Id == tenantRoleId && role.IsSystemOwner))
            {
                if (!await SessionIsSystemOwnerAsync(context, tenantId))
                    return Result<OrganizationUnitUserRole>.Failure(OnlyOwnersAppointOwners);

                if (unit.UnitType != OrganizationUnitType.Company)
                    return Result<OrganizationUnitUserRole>.Failure(OrganizationStructurePolicy.SystemOwnerOnRootOnly);
            }

            if (await RolesBeyondCeilingAsync(context, [tenantRoleId]) is { Count: > 0 })
                return Result<OrganizationUnitUserRole>.Failure(RoleBeyondCeilingNotGiven);

            var exists = await context.OrganizationUnitUserRoles.AnyAsync(a =>
                a.UserId == userId
                && a.TenantRoleId == tenantRoleId
                && a.OrganizationUnitId == organizationUnitId);

            if (exists)
                return Result<OrganizationUnitUserRole>.Failure("The user already holds this role on this unit.");

            var assignment = new OrganizationUnitUserRole
            {
                UserId = userId,
                OrganizationUnitId = organizationUnitId,
                TenantRoleId = tenantRoleId,
                TenantId = tenantId
            };

            context.OrganizationUnitUserRoles.Add(assignment);
            await context.SaveChangesAsync();

            await this.resourceLinker.SyncAsync(context, tenantId, [userId]);
            await touched.NameAffectedAsync(context, this.log, CancellationToken.None);
            await transaction.CommitAsync();

            PublishChanges(touched, "giving a role");
            await InvalidateOwnPermissionsAsync(userId == this.runtime.EffectiveUserId);
            return Result<OrganizationUnitUserRole>.Success(assignment);
        }
        catch (OrganizationStructureException refused)
        {
            return Result<OrganizationUnitUserRole>.Failure(refused.Message);
        }
        catch (SystemStepRefusedException refused)
        {
            return Result<OrganizationUnitUserRole>.Failure(refused.Message);
        }
        catch (TenantHeldException held)
        {
            return Result<OrganizationUnitUserRole>.Failure(held.Message);
        }
        catch (Exception)
        {
            return Result<OrganizationUnitUserRole>.Failure("An error occurred while assigning the user to the role.");
        }
    }

    public async Task<Result> RemoveUserFromRoleAsync(Guid assignmentId)
    {
        if (await RoleHoldingRefusalAsync(this.runtime.TenantId) is { } refusal)
            return Result.Failure(refusal);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var touched = Staffora.Services.Coordination.TenantChangeCollector.Attach(context, this.runtime.TenantId);
            await using var transaction = await context.Database.BeginTransactionAsync(ApplicationDbContext.OrganizationStructureIsolationLevel);
            await context.LockOrganizationStructureAsync(this.runtime.TenantId!.Value);
            var assignment = await context.OrganizationUnitUserRoles.FirstOrDefaultAsync(a => a.Id == assignmentId);
            if (assignment is null)
                return Result.Failure("Assignment not found.");

            if (await context.TenantRoles.AnyAsync(role => role.Id == assignment.TenantRoleId && role.IsSystemOwner))
            {
                if (!await SessionIsSystemOwnerAsync(context, assignment.TenantId))
                    return Result.Failure(OnlyOwnersAppointOwners);

                if (!await SystemOwners.ActiveOwnerIds(context, assignment.TenantId).AnyAsync(owner => owner != assignment.UserId))
                    return Result.Failure(LastOwnerKeepsTheRole);
            }

            if (await RolesBeyondCeilingAsync(context, [assignment.TenantRoleId]) is { Count: > 0 })
                return Result.Failure(RoleBeyondCeilingNotTaken);

            context.OrganizationUnitUserRoles.Remove(assignment);
            await context.SaveChangesAsync();

            await this.resourceLinker.SyncAsync(context, assignment.TenantId, [assignment.UserId]);
            await touched.NameAffectedAsync(context, this.log, CancellationToken.None);
            await transaction.CommitAsync();

            PublishChanges(touched, "taking a role away");
            await InvalidateOwnPermissionsAsync(assignment.UserId == this.runtime.EffectiveUserId);
            return Result.Success();
        }
        catch (OrganizationStructureException refused)
        {
            return Result.Failure(refused.Message);
        }
        catch (SystemStepRefusedException refused)
        {
            return Result.Failure(refused.Message);
        }
        catch (TenantHeldException held)
        {
            return Result.Failure(held.Message);
        }
        catch (Exception)
        {
            return Result.Failure("An error occurred while removing the user from the role.");
        }
    }

    public async Task<Result<List<OrganizationUnitUserRole>>> GetAssignmentsByUnitAsync(Guid organizationUnitId)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView))
            return Result<List<OrganizationUnitUserRole>>.Failure("You do not have permission to view role assignments.");

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "read the role holdings of a unit of any tenant");

            var assignments = await context.OrganizationUnitUserRoles
                .AsNoTracking()
                .Where(a => a.OrganizationUnitId == organizationUnitId)
                .Include(a => a.User)
                .Include(a => a.TenantRole)
                .OrderBy(a => a.TenantRole.Name)
                .ThenBy(a => a.User.FirstName)
                .ThenBy(a => a.User.LastName)
                .ToListAsync();

            return Result<List<OrganizationUnitUserRole>>.Success(assignments);
        }
        catch (Exception)
        {
            return Result<List<OrganizationUnitUserRole>>.Failure("An error occurred while fetching role assignments.");
        }
    }

    public const string NoRoleHoldingPermission = "You do not have permission to manage role assignments.";

    public const string RoleHoldingsInOwnTenantOnly = "You are not authorized to assign roles in this tenant.";

    public async Task<bool> CanManageRoleHoldingsAsync(Guid tenantId) => await RoleHoldingRefusalAsync(tenantId) is null;

    public async Task<Result<RoleHoldingChoices>> GetRoleHoldingChoicesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (await RoleHoldingRefusalAsync(tenantId) is { } refusal)
            return Result<RoleHoldingChoices>.Failure(refusal);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var allRoles = await context.TenantRoles.AsNoTracking()
                .Where(role => role.TenantId == tenantId && role.State == TenantRoleState.Active)
                .OrderBy(role => role.Name).ThenBy(role => role.Id)
                .Select(role => new RoleHoldingOption(role.Id, role.Name))
                .ToListAsync(cancellationToken);
            var staffRoles = await context.TenantRoles.AsNoTracking()
                .Where(role => role.TenantId == tenantId && role.State == TenantRoleState.Active && role.ApplyResourceMembershipToUser)
                .Select(role => role.Id)
                .ToListAsync(cancellationToken);

            var beyond = await RolesBeyondCeilingAsync(context, [.. allRoles.Select(role => role.Id)], cancellationToken);
            var roles = allRoles.Where(role => !beyond.Contains(role.Id)).ToList();
            var tree = await context.OrganizationUnits.AsNoTracking()
                .Where(unit => unit.TenantId == tenantId)
                .Select(unit => new OrganizationUnitNode(unit.Id, unit.ParentId, unit.Name, null, unit.UnitType, unit.State))
                .ToListAsync(cancellationToken);
            var units = OrganizationUnitOutline.Of(tree)
                .Where(entry => entry.IsLinked && entry.Unit.InUse && !OrganizationStructurePolicy.IsGrouping(entry.Unit.UnitType))
                .Select(entry => new RoleHoldingOption(entry.Unit.Id, entry.Path))
                .ToList();
            var people = await context.Users.AsNoTracking().ExceptSystemAccounts()
                .Where(user => user.TenantId == tenantId && user.State == UserState.Active)
                .OrderBy(user => user.FirstName).ThenBy(user => user.LastName).ThenBy(user => user.Id)
                .Select(user => new RoleHoldingOption(user.Id, string.IsNullOrWhiteSpace((user.FirstName + " " + user.LastName).Trim())
                    ? (user.Email ?? user.UserName ?? "Unknown user") : (user.FirstName + " " + user.LastName).Trim()))
                .ToListAsync(cancellationToken);
            return Result<RoleHoldingChoices>.Success(new RoleHoldingChoices(roles, units, people) { RolesBeyondCeiling = beyond, StaffRoles = staffRoles.ToHashSet() });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<RoleHoldingChoices>.Failure("An error occurred while reading the roles, units and people to choose from.");
        }
    }

    private async Task<string?> RoleHoldingRefusalAsync(Guid? tenantId)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesEdit))
            return NoRoleHoldingPermission;

        if (tenantId is not { } tenant || this.runtime.TenantId != tenant)
            return RoleHoldingsInOwnTenantOnly;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Tenants.AsNoTracking().AnyAsync(row => row.Id == tenant && row.IsRootTenant)
            ? ApplicationDbContext.PlatformTreeIsRootOnlyReason
            : null;
    }

    public const string OnlyOwnersAppointOwners = "Only a system owner gives or takes away the System owner role.";

    public const string LastOwnerKeepsTheRole = "This is the organization's last active system owner, and it always keeps one: make someone else a system owner first.";

    public const string RoleBeyondCeilingNotGiven = "You cannot give a role that grants permissions you do not hold.";

    public const string RoleBeyondCeilingNotTaken = "You cannot remove a role that grants permissions you do not hold.";

    internal async Task<IReadOnlySet<Guid>> RolesBeyondCeilingAsync(ApplicationDbContext context, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        if (roleIds.Count == 0)
            return new HashSet<Guid>();

        var granted = await context.TenantRolePermissions.AsNoTracking()
            .Where(permission => roleIds.Contains(permission.TenantRoleId))
            .Select(permission => new { permission.TenantRoleId, permission.Permission })
            .ToListAsync(cancellationToken);
        var missing = await this.permissionService.MissingAsync(granted.Select(row => row.Permission).Distinct(), cancellationToken);
        var beyond = granted.Where(row => missing.Contains(row.Permission)).Select(row => row.TenantRoleId).ToHashSet();

        var ownerRoles = await context.TenantRoles.AsNoTracking()
            .Where(role => roleIds.Contains(role.Id) && role.IsSystemOwner)
            .Select(role => new { role.Id, role.TenantId })
            .ToListAsync(cancellationToken);
        foreach (var ownerRole in ownerRoles)
        {
            if (!await SessionIsSystemOwnerAsync(context, ownerRole.TenantId, cancellationToken))
            {
                beyond.Add(ownerRole.Id);
            }
        }

        return beyond;
    }

    private async Task<bool> SessionIsSystemOwnerAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return true;

        return session.TenantId == tenantId && session.EffectiveUserId is { } self && await SystemOwners.IsActiveOwnerAsync(context, tenantId, self, cancellationToken);
    }

    private async Task InvalidateOwnPermissionsAsync(bool ownRolesChanged)
    {
        if (ownRolesChanged)
        {
            await this.permissionService.InvalidateCacheAsync();
        }
    }
}
