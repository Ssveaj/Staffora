using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Diagnostics;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.Services.Services;

public class OrganizationService
{
    private const string NoViewPermission = "You do not have permission to view organization units.";
    private const string NoCreatePermission = "You do not have permission to create organization units.";
    private const string NoEditPermission = "You do not have permission to edit organization units.";
    private const string NoDeletePermission = "You do not have permission to delete organization units.";

    public const string UnitNotFound = "Organization unit not found.";

    public const string UnitReadFailed = "The organization unit could not be read. Try again.";

    public const string CreateInOwnTenantOnly = "Only a user of this organization adds units to it.";

    public const string ChangeInOwnTenantOnly = "The platform console reads a tenant's organization and changes none of it: impersonate one of the tenant's users to change it.";

    public static readonly NameAndDescriptionRule TextRule = new("unit", OrganizationUnit.NameMaxLength, OrganizationUnit.DescriptionMaxLength);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly CustomFieldService attributes;
    private readonly RoleHoldingService holdings;
    private readonly ILogger<OrganizationService> log;

    public OrganizationService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, CustomFieldService attributes, RoleHoldingService holdings, ILogger<OrganizationService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.attributes = attributes;
        this.holdings = holdings;
        this.log = log;
    }

    public RoleHoldingService Holdings => this.holdings;

    public async Task<Result<List<OrganizationUnitNode>>> FetchUnitTreeAsync(Guid tenantId)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsView))
            return Result<List<OrganizationUnitNode>>.Failure(NoViewPermission);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScopeFor(this.runtime, tenantId, "read a tenant's organization tree");
            var units = await context.OrganizationUnits.AsNoTracking()
                .Where(unit => unit.TenantId == tenantId)
                .OrderBy(unit => unit.Name)
                .Select(unit => new OrganizationUnitNode(unit.Id, unit.ParentId, unit.Name, unit.Description, unit.UnitType, unit.State))
                .ToListAsync();

            return Result<List<OrganizationUnitNode>>.Success(units);
        }
        catch (Exception)
        {
            return Result<List<OrganizationUnitNode>>.Failure("An error occurred while fetching the organization tree.");
        }
    }

    public async Task<Result<ListPage<OrganizationUnitListItem>>> QueryUnitsAsync(ListQuery query)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsView))
            return Result<ListPage<OrganizationUnitListItem>>.Failure(NoViewPermission);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<OrganizationUnitListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = OrganizationUnitList.Definition.Normalize(query);

        var readRoles = await this.permissionService.HasPermissionAsync(RolePermission.RolesView);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            var all = context.OrganizationUnits.AsNoTracking().Where(unit => unit.TenantId == tenantId);
            var matching = all;
            if (list.Search is { Length: > 0 } search)
            {
                matching = matching.Where(unit => unit.Name.Contains(search) || (unit.Description != null && unit.Description.Contains(search)));
            }

            if (list.FilterAs<OrganizationUnitType>(OrganizationUnitList.Type) is { } type)
            {
                matching = matching.Where(unit => unit.UnitType == type);
            }

            var units = matching;
            var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!list.IsOn(OrganizationUnitList.ShowInactive))
            {
                units = matching.Where(unit => unit.State == OrganizationUnitState.Active || unit.UnitType == OrganizationUnitType.Company);
                hidden[OrganizationUnitList.ShowInactive] = await matching.CountAsync(unit => unit.State != OrganizationUnitState.Active && unit.UnitType != OrganizationUnitType.Company);
            }

            var total = await units.CountAsync();
            var page = ListPage<OrganizationUnitListItem>.PageWithin(list, total);

            var tree = await all
                .Select(unit => new OrganizationUnitNode(unit.Id, unit.ParentId, unit.Name, null, unit.UnitType, unit.State))
                .ToListAsync();
            var outline = OrganizationUnitOutline.Of(tree);
            var paths = outline.ToDictionary(entry => entry.Unit.Id, entry => entry.Path);
            string PathOf(Guid? id) => id is { } known && paths.TryGetValue(known, out var path) ? path : string.Empty;

            var rowsQuery = units;
            var skip = (page - 1) * list.PageSize;
            var order = new ListOrder<OrganizationUnit>(rowsQuery);
            Dictionary<string, (string Label, int Count)>? groups = null;
            switch (list.Group)
            {
                case OrganizationUnitList.ByParent:
                    {
                        var counts = await units.GroupBy(unit => unit.ParentId).Select(group => new { group.Key, Count = group.Count() }).ToListAsync();
                        var rank = outline.Select((entry, index) => (entry.Unit.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
                        var ordered = counts
                            .OrderBy(count => count.Key is { } parent ? rank.GetValueOrDefault(parent, int.MaxValue) : -1)
                            .ThenBy(count => count.Key)
                            .ToList();
                        groups = ordered.ToDictionary(count => ParentGroupKey(count.Key), count => (count.Key is null ? TopOfTreeLabel : $"Below {PathOf(count.Key)}", count.Count));

                        var pageGroups = new List<Guid?>();
                        var offset = skip;
                        var before = 0;
                        foreach (var group in ordered)
                        {
                            if (before + group.Count > offset && before < offset + list.PageSize)
                            {
                                if (pageGroups.Count == 0)
                                {
                                    skip = offset - before;
                                }

                                pageGroups.Add(group.Key);
                            }

                            before += group.Count;
                        }

                        rowsQuery = units.Where(unit => pageGroups.Contains(unit.ParentId));
                        order = new ListOrder<OrganizationUnit>(rowsQuery).By(GroupRank(pageGroups));
                        break;
                    }
                case OrganizationUnitList.ByType:
                    {
                        var counts = await units.GroupBy(unit => unit.UnitType).Select(group => new { group.Key, Count = group.Count() }).ToListAsync();
                        var labels = ListOption.ForEnum<OrganizationUnitType>().ToDictionary(option => option.Value, option => option.Label);
                        groups = counts.ToDictionary(count => TypeGroupKey(count.Key), count => (labels[TypeGroupKey(count.Key)], count.Count));
                        order.By(unit => unit.UnitType);
                        break;
                    }
            }

            _ = list.Sort switch
            {
                OrganizationUnitList.SortType => order.By(unit => unit.UnitType, list.Descending).By(unit => unit.Name),
                _ => order.By(unit => unit.Name, list.Descending),
            };

            var rows = await order.By(unit => unit.Id).Query
                .Skip(skip)
                .Take(list.PageSize)
                .Select(unit => new
                {
                    unit.Id,
                    unit.ParentId,
                    unit.Name,
                    unit.Description,
                    unit.UnitType,
                    unit.State,
                    UnitsBelow = unit.Children.Count(),
                    RoleHoldings = readRoles ? context.OrganizationUnitUserRoles.Count(holding => holding.OrganizationUnitId == unit.Id) : (int?)null,
                })
                .ToListAsync();

            var items = rows
                .Select(row => new OrganizationUnitListItem(row.Id, row.ParentId, row.Name, row.Description, row.UnitType, row.State, PathOf(row.ParentId), row.UnitsBelow, row.RoleHoldings))
                .ToList();

            var result = list.Group switch
            {
                OrganizationUnitList.ByParent => ListPage<OrganizationUnitListItem>.Grouped(items, total, page, list.PageSize, item => ParentGroupKey(item.ParentId), groups!),
                OrganizationUnitList.ByType => ListPage<OrganizationUnitListItem>.Grouped(items, total, page, list.PageSize, item => TypeGroupKey(item.UnitType), groups!),
                _ => ListPage<OrganizationUnitListItem>.Ungrouped(items, total, page, list.PageSize),
            };

            return Result<ListPage<OrganizationUnitListItem>>.Success(result with { HiddenCounts = hidden });
        }
        catch (Exception)
        {
            return Result<ListPage<OrganizationUnitListItem>>.Failure("An error occurred while fetching the organization units.");
        }
    }

    private static Expression<Func<OrganizationUnit, int>> GroupRank(IReadOnlyList<Guid?> parents)
    {
        var unit = Expression.Parameter(typeof(OrganizationUnit), "unit");
        var parentId = Expression.Property(unit, nameof(OrganizationUnit.ParentId));
        var ids = parents.ToArray();
        Expression rank = Expression.Constant(ids.Length);
        for (var index = ids.Length - 1; index >= 0; index--)
        {
            Expression test = ids[index] is null
                ? Expression.Equal(parentId, Expression.Constant(null, typeof(Guid?)))
                : Expression.Equal(parentId, Expression.ArrayIndex(Expression.Constant(ids), Expression.Constant(index)));
            rank = Expression.Condition(test, Expression.Constant(index), rank);
        }

        return Expression.Lambda<Func<OrganizationUnit, int>>(rank, unit);
    }

    public const string TopOfTreeLabel = "Top of the tree";

    private static string ParentGroupKey(Guid? parentId) => parentId?.ToString("N") ?? "top";

    private static string TypeGroupKey(OrganizationUnitType type) => type.ToString().ToLowerInvariant();

    private static DetachedEntityUpdaterOptions<OrganizationUnit> UpdaterOptions => new DetachedEntityUpdaterOptions<OrganizationUnit>()
        .WithCollection(unit => unit.Attributes);

    public async Task<Result<DetachedEntityUpdater<OrganizationUnit>>> LoadOrganizationUnitUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsView, cancellationToken))
            return Result<DetachedEntityUpdater<OrganizationUnit>>.Failure(NoViewPermission);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "open a unit's dialog");
            var unit = await context.OrganizationUnits
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return unit is null
                ? Result<DetachedEntityUpdater<OrganizationUnit>>.Failure(UnitNotFound)
                : Result<DetachedEntityUpdater<OrganizationUnit>>.Success(new DetachedEntityUpdater<OrganizationUnit>(unit, context, UpdaterOptions));
        }, this.log, $"the unit {id}", UnitReadFailed, cancellationToken);
    }

    public async Task<Result<OrganizationUnit>> NewOrganizationUnitAsync(Guid? parentId, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsCreate, cancellationToken))
            return Result<OrganizationUnit>.Failure(NoCreatePermission);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenantId || session.HasPlatformReach())
            return Result<OrganizationUnit>.Failure(CreateInOwnTenantOnly);

        return Result<OrganizationUnit>.Success(new OrganizationUnit { TenantId = tenantId, ParentId = parentId, UnitType = OrganizationUnitType.Branch, State = OrganizationUnitState.Create });
    }

    public async Task<Result<Guid>> CreateOrganizationUnitAsync(OrganizationUnit unit, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsCreate, cancellationToken))
            return Result<Guid>.Failure(NoCreatePermission);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var name = unit.Name?.Trim() ?? string.Empty;
        if (TextRule.Problem(name, unit.Description) is { } textReason)
            return Result<Guid>.Failure(textReason);

        if (unit.ParentId is null)
            return Result<Guid>.Failure($"{name} needs a parent: every unit except the Company root sits below another unit.");

        if (unit.UnitType == OrganizationUnitType.Company)
            return Result<Guid>.Failure(OrganizationUnitWorkflowAttribute.NotACompanyReason + ".");

        try
        {
            var row = new OrganizationUnit
            {
                TenantId = tenantId,
                ParentId = unit.ParentId,
                Name = name,
                Description = string.IsNullOrWhiteSpace(unit.Description) ? null : unit.Description.Trim(),
                UnitType = unit.UnitType,
                State = OrganizationUnitState.Create,
            };

            await using (var check = this.dbFactory.CreateDbContext(this.runtime))
            {
                if (await IsPlatformTenantAsync(check, tenantId))
                    return Result<Guid>.Failure(ApplicationDbContext.PlatformTreeIsRootOnlyReason);

                if (row.ParentId is { } parentId)
                {
                    var structure = await LoadStructureAsync(check, tenantId);
                    if (OrganizationStructurePolicy.FirstNewViolation(structure, structure.WithUnit(Guid.NewGuid(), tenantId, name, row.UnitType, parentId)) is { } reason)
                        return Result<Guid>.Failure(reason);
                }

                var values = CustomFieldValues.Normalized(unit.Attributes).Where(value => !AttributeValues.IsEmpty(value)).ToList();
                if (await CustomFieldValues.DescribeInvalidAsync<OrganizationUnitAttributeDefinition, OrganizationUnitAttribute>(check, tenantId, values, cancellationToken: cancellationToken) is { } invalid)
                    return Result<Guid>.Failure(invalid);

                foreach (var value in values)
                {
                    row.Attributes.Add(CopyOf(value));
                }
            }

            var created = await this.workflows.CreateAndTransitionAsync<OrganizationUnitState, OrganizationUnit>(row, transitionKey, OrganizationUnitWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
            if (created.IsSuccess || row.ParentId is not { } parent)
                return created;

            return await SiblingTakenAsync(parent, name, exceptId: null, cancellationToken) is { } clash ? Result<Guid>.Failure(clash) : created;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<Guid>.Failure("An error occurred while creating the organization unit.");
        }
    }

    public async Task<Result> UpdateOrganizationUnitAndTransitionAsync(DetachedEntityUpdater<OrganizationUnit> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);

        var id = (Guid)updater.OriginalValue(nameof(OrganizationUnit.Id))!;

        var session = this.runtime.Current;
        if (session.HasPlatformReach() && (Guid)updater.OriginalValue(nameof(OrganizationUnit.TenantId))! != session.TenantId)
            return Result.Failure(ChangeInOwnTenantOnly);

        if (WorkflowRegistry.Default.Get<OrganizationUnitState, OrganizationUnit>().IsDeleteStep(transitionKey))
        {
            if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsDelete, cancellationToken))
                return Result.Failure(NoDeletePermission);

            return await this.workflows.TransitionEntityAsync<OrganizationUnitState, OrganizationUnit>(id, transitionKey, OrganizationUnitWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }

        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsEdit, cancellationToken))
            return Result.Failure(NoEditPermission);

        var tenantId = (Guid)updater.OriginalValue(nameof(OrganizationUnit.TenantId))!;
        var unit = updater.Entity;
        try
        {
            if (unit.Name is { } typed && typed != typed.Trim())
            {
                unit.Name = typed.Trim();
            }

            if (updater.HasChanges())
            {
                if (TextRule.Problem(unit.Name ?? string.Empty, unit.Description) is { } textReason)
                    return Result.Failure(textReason);

                if (await EditRefusalAsync(updater, id, tenantId, cancellationToken) is { } refusal)
                    return Result.Failure(refusal);
            }

            var saved = await this.workflows.SaveAndTransitionAsync<OrganizationUnitState, OrganizationUnit>(updater, transitionKey, OrganizationUnitWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            if (saved.IsSuccess)
            {
                if (transitionKey != OrganizationUnitWorkflowAttribute.SaveStep)
                {
                    await this.permissionService.InvalidateCacheAsync(cancellationToken);
                }

                return saved;
            }

            return unit.ParentId is { } parent && await SiblingTakenAsync(parent, unit.Name ?? string.Empty, id, cancellationToken) is { } clash
                ? Result.Failure(clash)
                : saved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure("An error occurred while saving the organization unit.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetOrganizationUnitHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsView, cancellationToken)
            ? await this.history.GetHistoryAsync<OrganizationUnit>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoViewPermission);

    public async Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> GetOrganizationUnitCustomFieldsAsync(Guid? unitId, CancellationToken cancellationToken)
    {
        if (!await MayOpenAsync(unitId, cancellationToken))
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(unitId is null ? NoCreatePermission : NoViewPermission);

        if (await TenantOfAsync(unitId, cancellationToken) is not { } tenantId)
            return Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(UnitNotFound);

        var fields = await this.attributes.GetDefinitionsAsync<OrganizationUnitAttributeDefinition>(tenantId);
        return fields.IsSuccess && fields.Value is { } read
            ? Result<IReadOnlyList<IEntityAttributeDefinition>>.Success([.. read])
            : Result<IReadOnlyList<IEntityAttributeDefinition>>.Failure(fields.Error);
    }

    public async Task<Result<OrganizationUnitEditorOptions>> GetOrganizationUnitEditorOptionsAsync(Guid? unitId, CancellationToken cancellationToken)
    {
        if (!await MayOpenAsync(unitId, cancellationToken))
            return Result<OrganizationUnitEditorOptions>.Failure(unitId is null ? NoCreatePermission : NoViewPermission);

        if (await TenantOfAsync(unitId, cancellationToken) is not { } tenantId)
            return Result<OrganizationUnitEditorOptions>.Failure(UnitNotFound);

        var session = this.runtime.Current;
        var mayEdit = unitId is null || (tenantId == session.TenantId && !session.HasPlatformReach() && await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsEdit, cancellationToken));
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScopeFor(this.runtime, tenantId, "read the choices of a unit's dialog");
        var tree = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenantId)
            .Select(unit => new OrganizationUnitNode(unit.Id, unit.ParentId, unit.Name, null, unit.UnitType, unit.State))
            .ToListAsync(cancellationToken);
        var structure = OrganizationStructure.Of(tree.Select(unit => (unit.Id, tenantId, unit.Name, unit.UnitType, unit.ParentId)));
        var excluded = unitId is { } id ? structure.SelfAndDescendants(id) : [];
        IReadOnlyList<OrganizationUnit> parents = [.. OrganizationUnitOutline.Of(tree)
            .Where(entry => entry.IsLinked && entry.Unit.InUse && !excluded.Contains(entry.Unit.Id))
            .Select(entry => new OrganizationUnit { Id = entry.Unit.Id, TenantId = tenantId, ParentId = entry.Unit.ParentId, Name = entry.Path, UnitType = entry.Unit.UnitType, State = entry.Unit.State })];
        var isRoot = unitId is { } own && tree.FirstOrDefault(unit => unit.Id == own) is { IsRoot: true };
        IReadOnlyList<OrganizationUnitTypeOption> types = [.. (isRoot ? [OrganizationUnitType.Company] : OrganizationStructurePolicy.TypesBelowRoot)
            .Select(type => new OrganizationUnitTypeOption(type, TypeName(type)))];

        var choices = await this.holdings.GetRoleHoldingChoicesAsync(tenantId, cancellationToken);
        return Result<OrganizationUnitEditorOptions>.Success(choices.IsSuccess && choices.Value is { } offered
            ? new OrganizationUnitEditorOptions(!mayEdit, parents, types, true, offered)
            : new OrganizationUnitEditorOptions(!mayEdit, parents, types, false, RoleHoldingChoices.None));
    }

    public async Task<Result<ListPage<OrganizationUnitListItem>>> QueryOrganizationUnitChildrenAsync(Guid unitId, ListQuery query, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.OrganizationUnitsView, cancellationToken))
            return Result<ListPage<OrganizationUnitListItem>>.Failure(NoViewPermission);

        var list = OrganizationUnitChildList.Definition.Normalize(query);
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "list the units below a unit of any tenant");
        var units = context.OrganizationUnits.AsNoTracking().Where(unit => unit.ParentId == unitId);
        if (list.Search is { Length: > 0 } search)
        {
            units = units.Where(unit => unit.Name.Contains(search) || (unit.Description != null && unit.Description.Contains(search)));
        }

        var total = await units.CountAsync(cancellationToken);
        var page = ListPage<OrganizationUnitListItem>.PageWithin(list, total);
        var order = new ListOrder<OrganizationUnit>(units);
        _ = list.Sort == OrganizationUnitChildList.SortType
            ? order.By(unit => unit.UnitType, list.Descending).By(unit => unit.Name)
            : order.By(unit => unit.Name, list.Descending);
        var rows = await order.By(unit => unit.Id).Query
            .Skip((page - 1) * list.PageSize)
            .Take(list.PageSize)
            .Select(unit => new OrganizationUnitListItem(unit.Id, unit.ParentId, unit.Name, unit.Description, unit.UnitType, unit.State, string.Empty, unit.Children.Count(), null))
            .ToListAsync(cancellationToken);
        List<OrganizationUnitListItem> items = [.. rows.Select(row => row with { TypeName = TypeName(row.UnitType) })];
        return Result<ListPage<OrganizationUnitListItem>>.Success(ListPage<OrganizationUnitListItem>.Ungrouped(items, total, page, list.PageSize));
    }

    private async Task<string?> EditRefusalAsync(DetachedEntityUpdater<OrganizationUnit> updater, Guid id, Guid tenantId, CancellationToken cancellationToken)
    {
        var unit = updater.Entity;
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        if (await IsPlatformTenantAsync(context, tenantId))
            return ApplicationDbContext.PlatformTreeIsRootOnlyReason;

        var storedName = (string?)updater.OriginalValue(nameof(OrganizationUnit.Name));
        var storedType = (OrganizationUnitType)updater.OriginalValue(nameof(OrganizationUnit.UnitType))!;
        var storedParent = (Guid?)updater.OriginalValue(nameof(OrganizationUnit.ParentId));
        if (unit.Name != storedName || unit.UnitType != storedType || unit.ParentId != storedParent)
        {
            var holdings = await context.OrganizationUnitUserRoles.CountAsync(holding => holding.OrganizationUnitId == id, cancellationToken);
            if (OrganizationStructurePolicy.CheckTypeChange(unit.Name ?? string.Empty, storedType, unit.UnitType, holdings) is { } typeReason)
                return typeReason;

            var structure = await LoadStructureAsync(context, tenantId);
            if (structure.Find(id) is null)
                return UnitNotFound;

            var proposed = structure.Edited(id, unit.Name ?? string.Empty, unit.UnitType);
            if (unit.ParentId != storedParent)
            {
                proposed = proposed.Moved(id, unit.ParentId);
            }

            if (OrganizationStructurePolicy.FirstNewViolation(structure, proposed) is { } reason)
                return reason;
        }

        foreach (var emptied in CustomFieldValues.Normalized(unit.Attributes).Where(value => AttributeValues.IsEmpty(value)).ToList())
        {
            unit.Attributes.Remove(emptied);
        }

        var edited = CustomFieldValues.Normalized(unit.Attributes);
        if (edited.Count == 0)
            return null;

        var stored = await context.OrganizationUnitAttributes.AsNoTracking().Where(value => value.EntityId == id).ToListAsync(cancellationToken);
        return await CustomFieldValues.DescribeInvalidAsync<OrganizationUnitAttributeDefinition, OrganizationUnitAttribute>(context, tenantId, edited, stored, cancellationToken);
    }

    private async Task<string?> SiblingTakenAsync(Guid parentId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var found = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.ParentId == parentId && unit.Name == name && unit.Id != exceptId)
            .Select(unit => new { unit.Name, Parent = unit.Parent!.Name })
            .FirstOrDefaultAsync(cancellationToken);
        return found is null ? null : OrganizationStructurePolicy.SiblingNameReason(found.Parent, found.Name);
    }

    private async Task<bool> MayOpenAsync(Guid? unitId, CancellationToken cancellationToken) =>
        await this.permissionService.HasPermissionAsync(unitId is null ? RolePermission.OrganizationUnitsCreate : RolePermission.OrganizationUnitsView, cancellationToken);

    private async Task<Guid?> TenantOfAsync(Guid? unitId, CancellationToken cancellationToken)
    {
        if (unitId is not { } id)
            return this.runtime.TenantId;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "read the tenant of a unit of any tenant");
        return await context.OrganizationUnits.AsNoTracking().Where(unit => unit.Id == id).Select(unit => (Guid?)unit.TenantId).FirstOrDefaultAsync(cancellationToken);
    }

    private static string TypeName(OrganizationUnitType type) =>
        typeof(OrganizationUnitType).GetField(type.ToString())?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? type.ToString();

    private static Task<bool> IsPlatformTenantAsync(ApplicationDbContext context, Guid tenantId) =>
        context.Tenants.AsNoTracking().Where(tenant => tenant.Id == tenantId).Select(tenant => tenant.IsRootTenant).FirstOrDefaultAsync();

    internal static async Task<OrganizationStructure> LoadStructureAsync(ApplicationDbContext context, Guid tenantId)
    {
        var units = await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.TenantId == tenantId)
            .Select(unit => new { unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId })
            .ToListAsync();
        return OrganizationStructure.Of(units.Select(unit => (unit.Id, unit.TenantId, unit.Name, unit.UnitType, unit.ParentId)));
    }

    private static OrganizationUnitAttribute CopyOf(OrganizationUnitAttribute value)
    {
        var row = new OrganizationUnitAttribute { EntityAttributeDefinitionId = value.EntityAttributeDefinitionId };
        AttributeValues.CopyValues(value, row);
        return row;
    }
}
