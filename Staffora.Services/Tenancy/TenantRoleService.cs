using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Services;
using Staffora.Services.Workflow;

namespace Staffora.Services.Tenancy;

public class TenantRoleService
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly RoleHoldingService holdings;
    private readonly ILogger<TenantRoleService> log;

    public TenantRoleService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, RoleHoldingService holdings, ILogger<TenantRoleService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.holdings = holdings;
        this.log = log;
    }

    public RoleHoldingService Holdings => this.holdings;

    public async Task<Result<List<TenantRole>>> GetTenantRolesAsync(Guid tenantId)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView))
            return Result<List<TenantRole>>.Failure("You do not have permission to view roles.");

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScopeFor(this.runtime, tenantId, "read a tenant's roles");
            var roles = await context.TenantRoles.Where(r => r.TenantId == tenantId).Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync();
            return Result<List<TenantRole>>.Success(roles);
        }
        catch (Exception)
        {
            return Result<List<TenantRole>>.Failure("An error occurred while fetching tenant roles.");
        }
    }

    public async Task<Result<ListPage<TenantRoleRow>>> QueryRolesAsync(ListQuery query)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView))
            return Result<ListPage<TenantRoleRow>>.Failure("You do not have permission to view roles.");

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<TenantRoleRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = TenantRoleList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var roles = context.TenantRoles.AsNoTracking().Where(role => role.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                roles = roles.Where(role => role.Name.Contains(search) || (role.Description != null && role.Description.Contains(search)));
            }

            var total = await roles.CountAsync();
            var page = ListPage<TenantRoleRow>.PageWithin(list, total);
            var order = new ListOrder<TenantRole>(roles);
            _ = list.Sort switch
            {
                TenantRoleList.SortPermissions => order.By(role => role.Permissions.Count, list.Descending).By(role => role.Name),
                _ => order.By(role => role.Name, list.Descending),
            };

            var read = await order.By(role => role.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(role => new
                {
                    role.Id,
                    role.Name,
                    role.Description,
                    role.State,
                    role.IsSystemOwner,
                    PermissionCount = role.Permissions.Count,
                    First = role.Permissions.OrderBy(permission => permission.Permission).Select(permission => permission.Permission).Take(3).ToList(),
                    role.ApplyResourceMembershipToUser,
                    Holders = context.OrganizationUnitUserRoles.Where(holding => holding.TenantId == tenantId && holding.TenantRoleId == role.Id).Select(holding => holding.UserId).Distinct().Count(),
                })
                .ToListAsync();
            var rows = read.Select(role => new TenantRoleRow(role.Id, role.Name, role.Description, role.State, role.IsSystemOwner, role.PermissionCount, role.First, role.ApplyResourceMembershipToUser, role.Holders)).ToList();
            return Result<ListPage<TenantRoleRow>>.Success(ListPage<TenantRoleRow>.Ungrouped(rows, total, page, list.PageSize));
        }
        catch (Exception)
        {
            return Result<ListPage<TenantRoleRow>>.Failure("An error occurred while reading the roles.");
        }
    }

    public const string RoleCreateInOwnTenantOnly = "Only a user of this organization creates its roles.";

    public const string RoleChangeInOwnTenantOnly = "The platform console reads a tenant's roles and changes none: impersonate one of the tenant's users to change them.";

    public const string RoleNotFound = "Role not found.";

    public const string RoleReadFailed = "The role could not be read. Try again.";

    public static readonly NameAndDescriptionRule TextRule = new("role", TenantRole.NameMaxLength, TenantRole.DescriptionMaxLength);

    private const string NoRolesView = "You do not have permission to view roles.";
    private const string NoRolesCreate = "You do not have permission to create roles.";
    private const string NoRolesEdit = "You do not have permission to edit roles.";
    private const string NoRolesDelete = "You do not have permission to delete roles.";

    public const string ResourceFlagRequiresResourcesManage = "You can only create a role whose holders are staff members if you manage staff members.";

    public const string SystemOwnerRoleReadOnly = "The System owner role is locked: it cannot be edited or deleted. Its holders are the organization's system owners.";

    public const string RoleBeyondCeilingReadOnly = "This role grants permissions you do not hold, so you cannot change it.";

    public const string RolePermissionsBeyondCeiling = "You cannot give or take away permissions you do not hold yourself.";

    private static DetachedEntityUpdaterOptions<TenantRole> RoleUpdaterOptions => new DetachedEntityUpdaterOptions<TenantRole>()
        .WithCollection(role => role.Permissions);

    public async Task<Result<DetachedEntityUpdater<TenantRole>>> LoadTenantRoleUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesView, cancellationToken))
            return Result<DetachedEntityUpdater<TenantRole>>.Failure(NoRolesView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "open a role's dialog");
            var role = await context.TenantRoles
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return role is null
                ? Result<DetachedEntityUpdater<TenantRole>>.Failure(RoleNotFound)
                : Result<DetachedEntityUpdater<TenantRole>>.Success(new DetachedEntityUpdater<TenantRole>(role, context, RoleUpdaterOptions));
        }, this.log, $"the role {id}", RoleReadFailed, cancellationToken);
    }

    public async Task<Result<TenantRole>> NewTenantRoleAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesCreate, cancellationToken))
            return Result<TenantRole>.Failure(NoRolesCreate);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.HasPlatformReach())
            return Result<TenantRole>.Failure(RoleCreateInOwnTenantOnly);

        return Result<TenantRole>.Success(new TenantRole { TenantId = tenantId, State = TenantRoleState.Create });
    }

    public async Task<Result<Guid>> CreateTenantRoleAsync(TenantRole role, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesCreate, cancellationToken))
            return Result<Guid>.Failure(NoRolesCreate);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var name = role.Name?.Trim() ?? string.Empty;
        if (TextRule.Problem(name, role.Description) is { } textReason)
            return Result<Guid>.Failure(textReason);

        var permissions = role.Permissions.Select(permission => permission.Permission).Where(permission => permission != RolePermission.None).Distinct().ToList();

        if ((await this.permissionService.MissingAsync(permissions, cancellationToken)).Count > 0)
            return Result<Guid>.Failure(RolePermissionsBeyondCeiling);

        if (role.ApplyResourceMembershipToUser && !await this.permissionService.HasPermissionAsync(RolePermission.ResourcesManage, cancellationToken))
            return Result<Guid>.Failure(ResourceFlagRequiresResourcesManage);

        var row = new TenantRole
        {
            TenantId = tenantId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(role.Description) ? null : role.Description.Trim(),
            ApplyResourceMembershipToUser = role.ApplyResourceMembershipToUser,
            State = TenantRoleState.Create,
        };
        foreach (var permission in permissions)
        {
            row.Permissions.Add(new TenantRolePermission { TenantId = tenantId, Permission = permission });
        }

        try
        {
            return await this.workflows.CreateAndTransitionAsync<TenantRoleState, TenantRole>(row, transitionKey, TenantRoleWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<Guid>.Failure("An error occurred while creating the role.");
        }
    }

    public async Task<Result> UpdateTenantRoleAndTransitionAsync(DetachedEntityUpdater<TenantRole> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        var id = (Guid)updater.OriginalValue(nameof(TenantRole.Id))!;

        var session = this.runtime.Current;
        if (session.HasPlatformReach() && (Guid)updater.OriginalValue(nameof(TenantRole.TenantId))! != session.TenantId)
            return Result.Failure(RoleChangeInOwnTenantOnly);

        if (WorkflowRegistry.Default.Get<TenantRoleState, TenantRole>().IsDeleteStep(transitionKey))
        {
            if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesDelete, cancellationToken))
                return Result.Failure(NoRolesDelete);

            return await this.workflows.TransitionEntityAsync<TenantRoleState, TenantRole>(id, transitionKey, TenantRoleWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }

        if (!await this.permissionService.HasPermissionAsync(RolePermission.RolesEdit, cancellationToken))
            return Result.Failure(NoRolesEdit);

        var role = updater.Entity;
        if (role.Name is { } typed && typed != typed.Trim())
        {
            role.Name = typed.Trim();
        }

        if (updater.HasChanges() && TextRule.Problem(role.Name ?? string.Empty, role.Description) is { } textReason)
            return Result.Failure(textReason);

        try
        {
            var saved = await this.workflows.SaveAndTransitionAsync<TenantRoleState, TenantRole>(updater, transitionKey, TenantRoleWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            if (saved.IsSuccess && await HoldsRoleAsync(id, cancellationToken))
            {
                await this.permissionService.InvalidateCacheAsync();
            }

            return saved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure("An error occurred while saving the role.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetTenantRoleHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasPermissionAsync(RolePermission.RolesView, cancellationToken)
            ? await this.history.GetHistoryAsync<TenantRole>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoRolesView);

    public async Task<Result<TenantRoleEditorOptions>> GetTenantRoleEditorOptionsAsync(Guid? roleId, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(roleId is null ? RolePermission.RolesCreate : RolePermission.RolesView, cancellationToken))
            return Result<TenantRoleEditorOptions>.Failure(roleId is null ? NoRolesCreate : NoRolesView);

        var grantable = await GetGrantablePermissionsAsync(cancellationToken);
        if (roleId is not { } id)
        {
            var offered = this.runtime.TenantId is { } own ? await this.holdings.GetRoleHoldingChoicesAsync(own, cancellationToken) : Result<RoleHoldingChoices>.Failure(string.Empty);
            var mayGive = offered.IsSuccess && offered.Value is not null;
            return Result<TenantRoleEditorOptions>.Success(new TenantRoleEditorOptions(false, null, grantable, mayGive, mayGive ? offered.Value! : RoleHoldingChoices.None)
            {
                Offered = TenantEditions.Ceiling(await this.permissionService.GetEditionAsync(cancellationToken)).ToHashSet(),
            });
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "read the choices of a role's dialog");
        var tenantId = await context.TenantRoles.AsNoTracking().Where(role => role.Id == id).Select(role => (Guid?)role.TenantId).FirstOrDefaultAsync(cancellationToken);
        if (tenantId is not { } tenant)
            return Result<TenantRoleEditorOptions>.Failure(RoleNotFound);

        var beyond = (await this.holdings.RolesBeyondCeilingAsync(context, [id], cancellationToken)).Contains(id);

        var locked = await context.TenantRoles.AsNoTracking().AnyAsync(role => role.Id == id && role.IsSystemOwner, cancellationToken);

        var session = this.runtime.Current;
        var mayEdit = tenant == session.TenantId && !session.HasPlatformReach() && await this.permissionService.HasPermissionAsync(RolePermission.RolesEdit, cancellationToken) && !beyond && !locked;
        var choices = await this.holdings.GetRoleHoldingChoicesAsync(tenant, cancellationToken);
        var canManage = !beyond && choices.IsSuccess && choices.Value is not null;
        var reason = locked ? SystemOwnerRoleReadOnly : beyond ? RoleBeyondCeilingReadOnly : null;

        var parent = await context.Tenants.AsNoTracking().Where(row => row.Id == tenant).Select(row => row.ParentTenantId).FirstOrDefaultAsync(cancellationToken);
        return Result<TenantRoleEditorOptions>.Success(new TenantRoleEditorOptions(!mayEdit, reason, grantable, canManage, canManage ? choices.Value! : RoleHoldingChoices.None)
        {
            Offered = TenantEditions.Ceiling(TenantEditions.Of(parent)).ToHashSet(),
        });
    }

    private async Task<bool> HoldsRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.OrganizationUnitUserRoles.AnyAsync(holding => holding.TenantRoleId == roleId && holding.UserId == this.runtime.EffectiveUserId, cancellationToken);
    }

    public async Task<IReadOnlySet<RolePermission>> GetGrantablePermissionsAsync(CancellationToken cancellationToken = default)
    {
        var all = Enum.GetValues<RolePermission>().Where(permission => permission != RolePermission.None).ToList();
        var missing = await this.permissionService.MissingAsync(all, cancellationToken);
        return all.Where(permission => !missing.Contains(permission)).ToHashSet();
    }
}
