using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Coordination;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Resources;

public sealed class QualificationService
{
    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.ResourcesView, RolePermission.QualificationsManage];

    public const string NoView = "You do not have permission to view qualifications.";

    public const string NoManage = "You do not have permission to manage qualifications.";

    public const string NotFound = "Qualification not found.";

    public const string StaffNotFound = "This person is not a staff member.";

    public const string NameTaken = "Another qualification already has this name.";

    public const string NotActive = "Only an active qualification can be given to a staff member.";

    public const string StaffInactive = "The staff member is inactive: their qualifications are kept as they are.";

    public const string AlreadyHeld = "The staff member holds this qualification already.";

    public const string NotHeld = "The staff member does not hold this qualification.";

    public const string ReadFailed = "The qualifications could not be read. Try again.";

    public static readonly NameAndDescriptionRule TextRule = new("qualification", Qualification.NameMaxLength, Qualification.DescriptionMaxLength);

    internal static readonly LookupCatalogue<QualificationCatalogueRow> Catalogue = new("qualifications", LoadCatalogueAsync);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantLookupCache lookupCache;
    private readonly TenantChangeBus changes;
    private readonly ILogger<QualificationService> log;

    public QualificationService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        TenantLookupCache lookupCache,
        TenantChangeBus changes,
        ILogger<QualificationService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.lookupCache = lookupCache;
        this.changes = changes;
        this.log = log;
    }

    public async Task<Result<ListPage<QualificationListItem>>> QueryQualificationsAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<QualificationListItem>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<QualificationListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = QualificationList.Definition.Normalize(query);
        return await ReadAsync("the qualification catalogue", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var entries = context.Qualifications.AsNoTracking().Where(qualification => qualification.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                entries = entries.Where(qualification => qualification.Name.Contains(search) || (qualification.Description != null && qualification.Description.Contains(search)));
            }

            if (list.Filter(QualificationList.Status) is { } status && ListOption.TryParseEnum<QualificationState>(status, out var state))
            {
                entries = entries.Where(qualification => qualification.State == state);
            }

            var total = await entries.CountAsync(token);
            var page = ListPage<QualificationListItem>.PageWithin(list, total);
            var rows = await new ListOrder<Qualification>(entries)
                .By(qualification => qualification.Name, list.Descending)
                .By(qualification => qualification.Id)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(qualification => new { qualification.Id, qualification.Name, qualification.Description, qualification.State })
                .ToListAsync(token);
            var ids = rows.Select(row => row.Id).ToList();
            var holders = await context.ResourceQualifications.AsNoTracking()
                .Where(held => held.TenantId == tenantId && ids.Contains(held.QualificationId))
                .GroupBy(held => held.QualificationId)
                .Select(group => new { QualificationId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.QualificationId, group => group.Count, token);
            List<QualificationListItem> items = [.. rows.Select(row => new QualificationListItem(row.Id, row.Name, row.Description, row.State, holders.GetValueOrDefault(row.Id)))];
            return ListPage<QualificationListItem>.Ungrouped(items, total, page, list.PageSize);
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<QualificationCatalogueRow>>> GetCatalogueAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<IReadOnlyList<QualificationCatalogueRow>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<IReadOnlyList<QualificationCatalogueRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        return await ReadAsync("the qualification catalogue", token => this.lookupCache.GetAsync(Catalogue, tenantId, token), cancellationToken);
    }

    public async Task<Result<DetachedEntityUpdater<Qualification>>> LoadQualificationUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<DetachedEntityUpdater<Qualification>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var qualification = await context.Qualifications
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return qualification is null
                ? Result<DetachedEntityUpdater<Qualification>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<Qualification>>.Success(new DetachedEntityUpdater<Qualification>(qualification, context));
        }, this.log, $"the qualification {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<Qualification>> NewQualificationAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return Result<Qualification>.Failure(NoManage);

        return this.runtime.TenantId is { } tenantId
            ? Result<Qualification>.Success(new Qualification { TenantId = tenantId, State = QualificationState.Create })
            : Result<Qualification>.Failure(IRuntimeExecutionContext.NoTenant);
    }

    public async Task<Result<Guid>> CreateQualificationAsync(Qualification qualification, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return Result<Guid>.Failure(NoManage);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var name = qualification.Name?.Trim() ?? string.Empty;
        var description = string.IsNullOrWhiteSpace(qualification.Description) ? null : qualification.Description.Trim();
        if (TextRule.Problem(name, description) is { } textReason)
            return Result<Guid>.Failure(textReason);

        try
        {
            if (await NameTakenAsync(name, null, cancellationToken))
                return Result<Guid>.Failure(NameTaken);

            var row = new Qualification { TenantId = tenantId, Name = name, Description = description, State = QualificationState.Create };
            var created = await this.workflows.CreateAndTransitionAsync<QualificationState, Qualification>(row, transitionKey, QualificationWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

            return !created.IsSuccess && await NameTakenAsync(name, null, cancellationToken) ? Result<Guid>.Failure(NameTaken) : created;
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    public async Task<Result> UpdateQualificationAndTransitionAsync(DetachedEntityUpdater<Qualification> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return Result.Failure(NoManage);

        if (this.runtime.TenantId is not { } tenantId)
            return Result.Failure(IRuntimeExecutionContext.NoTenant);

        var id = (Guid)updater.OriginalValue(nameof(Qualification.Id))!;
        var qualification = updater.Entity;
        if (qualification.Name is { } typed && typed != typed.Trim())
        {
            qualification.Name = typed.Trim();
        }

        var renamed = !string.Equals(qualification.Name, updater.OriginalValue(nameof(Qualification.Name)) as string, StringComparison.Ordinal);
        try
        {
            if (updater.HasChanges())
            {
                if (TextRule.Problem(qualification.Name ?? string.Empty, qualification.Description) is { } textReason)
                    return Result.Failure(textReason);

                if (renamed && await NameTakenAsync(qualification.Name!, id, cancellationToken))
                    return Result.Failure(NameTaken);
            }

            var saved = await this.workflows.SaveAndTransitionAsync<QualificationState, Qualification>(updater, transitionKey, QualificationWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            return !saved.IsSuccess && renamed && await NameTakenAsync(qualification.Name ?? string.Empty, id, cancellationToken) ? Result.Failure(NameTaken) : saved;
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    public async Task<Result<StateHistoryPage>> GetQualificationHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<Qualification>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public async Task<Result<QualificationEditorOptions>> GetQualificationEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var manages = await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken);
        if (id is null)
            return manages ? Result<QualificationEditorOptions>.Success(new QualificationEditorOptions(FieldsLocked: false)) : Result<QualificationEditorOptions>.Failure(NoManage);

        return manages || await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? Result<QualificationEditorOptions>.Success(new QualificationEditorOptions(FieldsLocked: !manages))
            : Result<QualificationEditorOptions>.Failure(NoView);
    }

    public async Task<bool> MayManageQualificationsOfAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return false;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Resources.AsNoTracking().Where(resource => resource.UserId == userId).AnyAsync(StaffServiceRules.SetUpOpen, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<QualificationChoice>>> GetChoicesForAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return Result<IReadOnlyList<QualificationChoice>>.Failure(NoManage);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<IReadOnlyList<QualificationChoice>>.Failure(IRuntimeExecutionContext.NoTenant);

        return await ReadAsync("the qualifications a staff member may be given", async token =>
        {
            var catalogue = await this.lookupCache.GetAsync(Catalogue, tenantId, token);
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var held = await context.ResourceQualifications.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.Resource.UserId == userId)
                .Select(row => row.QualificationId)
                .ToListAsync(token);
            IReadOnlyList<QualificationChoice> choices = [.. catalogue
                .Where(entry => entry.State == QualificationState.Active && !held.Contains(entry.Id))
                .Select(entry => new QualificationChoice(entry.Id, entry.Name, entry.Description))];
            return choices;
        }, cancellationToken);
    }

    public async Task<Result> GiveQualificationAsync(Guid userId, Guid qualificationId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return Result.Failure(NoManage);

        if (this.runtime.TenantId is not { } tenantId)
            return Result.Failure(IRuntimeExecutionContext.NoTenant);

        return await WriteAsync("give a qualification", AlreadyHeld, async (context, token) =>
        {
            var staff = await context.Resources.Where(resource => resource.UserId == userId).Select(resource => new { resource.Id, resource.State }).FirstOrDefaultAsync(token);
            if (staff is null)
                return Result.Failure(StaffNotFound);

            if (!StaffServiceRules.IsSetUpOpen(staff.State))
                return Result.Failure(StaffInactive);

            var qualification = await context.Qualifications.Where(row => row.Id == qualificationId).Select(row => new { row.State, row.IsInTransition }).FirstOrDefaultAsync(token);
            if (qualification is null)
                return Result.Failure(NotFound);

            if (qualification.State != QualificationState.Active || qualification.IsInTransition)
                return Result.Failure(NotActive);

            if (await context.ResourceQualifications.AnyAsync(row => row.ResourceId == staff.Id && row.QualificationId == qualificationId, token))
                return Result.Failure(AlreadyHeld);

            context.ResourceQualifications.Add(new ResourceQualification { TenantId = tenantId, ResourceId = staff.Id, QualificationId = qualificationId });
            await context.SaveChangesAsync(token);
            return Result.Success();
        }, cancellationToken);
    }

    public async Task<Result> TakeQualificationAsync(Guid userId, Guid qualificationId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.QualificationsManage, cancellationToken))
            return Result.Failure(NoManage);

        return await WriteAsync("take a qualification away", NotHeld, async (context, token) =>
        {
            var staff = await context.Resources.Where(resource => resource.UserId == userId).Select(resource => new { resource.Id, resource.State }).FirstOrDefaultAsync(token);
            if (staff is null)
                return Result.Failure(StaffNotFound);

            if (!StaffServiceRules.IsSetUpOpen(staff.State))
                return Result.Failure(StaffInactive);

            var held = await context.ResourceQualifications.FirstOrDefaultAsync(row => row.ResourceId == staff.Id && row.QualificationId == qualificationId, token);
            if (held is null)
                return Result.Failure(NotHeld);

            context.ResourceQualifications.Remove(held);
            await context.SaveChangesAsync(token);
            return Result.Success();
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>> ReadQualificationsOfStaffMembersAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (!await this.permissionService.HasAnyPermissionAsync([RolePermission.ResourcesView, RolePermission.ViewCoordination], cancellationToken))
            return Result<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>.Failure(IRuntimeExecutionContext.NoTenant);

        var ids = resourceIds.Distinct().ToList();
        return await ReadAsync("the qualifications of staff members", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var rows = await context.ResourceQualifications.AsNoTracking()
                .Where(row => row.TenantId == tenantId && ids.Contains(row.ResourceId))
                .Select(row => new { row.ResourceId, row.QualificationId })
                .ToListAsync(token);
            var byStaff = rows.ToLookup(row => row.ResourceId, row => row.QualificationId);
            IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> answer = ids.ToDictionary(id => id, id => (IReadOnlySet<Guid>)byStaff[id].ToHashSet());
            return answer;
        }, cancellationToken);
    }

    internal Task<Result<ListPage<StaffQualificationRow>>> QualificationsOfAsync(Guid userId, ListQuery query, CancellationToken cancellationToken)
    {
        var list = StaffQualificationList.Definition.Normalize(query);
        return ReadAsync("a staff member's qualifications", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var held = context.ResourceQualifications.AsNoTracking().Where(row => row.Resource.UserId == userId);
            if (list.Search is { Length: > 0 } search)
            {
                held = held.Where(row => row.Qualification.Name.Contains(search));
            }

            var total = await held.CountAsync(token);
            var page = ListPage<StaffQualificationRow>.PageWithin(list, total);
            var rows = await new ListOrder<ResourceQualification>(held)
                .By(row => row.Qualification.Name, list.Descending)
                .By(row => row.QualificationId)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(row => new StaffQualificationRow(row.Resource.UserId, row.QualificationId, row.Qualification.Name, row.Qualification.Description, row.Qualification.State))
                .ToListAsync(token);
            return ListPage<StaffQualificationRow>.Ungrouped(rows, total, page, list.PageSize);
        }, cancellationToken);
    }

    public async Task<Result<ListPage<QualificationHolderRow>>> QueryQualificationHoldersAsync(Guid qualificationId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken))
            return Result<ListPage<QualificationHolderRow>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<QualificationHolderRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = QualificationHolderList.Definition.Normalize(query);
        return await ReadAsync("the holders of a qualification", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var held = context.ResourceQualifications.AsNoTracking().Where(row => row.TenantId == tenantId && row.QualificationId == qualificationId);
            if (list.Search is { Length: > 0 } search)
            {
                held = held.Where(row => (row.Resource.User.FirstName + " " + row.Resource.User.LastName).Contains(search) || (row.Resource.User.Email != null && row.Resource.User.Email.Contains(search)));
            }

            if (list.Filter(QualificationHolderList.Status) is { } status && ListOption.TryParseEnum<ResourceState>(status, out var state))
            {
                held = held.Where(row => row.Resource.State == state);
            }

            var total = await held.CountAsync(token);
            var page = ListPage<QualificationHolderRow>.PageWithin(list, total);
            var rows = await new ListOrder<ResourceQualification>(held)
                .By(row => row.Resource.User.FirstName, list.Descending)
                .By(row => row.Resource.User.LastName, list.Descending)
                .By(row => row.Resource.User.Email, list.Descending)
                .By(row => row.ResourceId)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(row => new
                {
                    row.ResourceId,
                    row.Resource.UserId,
                    Name = (row.Resource.User.FirstName + " " + row.Resource.User.LastName).Trim(),
                    row.Resource.User.Email,
                    row.Resource.State,
                })
                .ToListAsync(token);
            List<QualificationHolderRow> items = [.. rows.Select(row => new QualificationHolderRow(row.ResourceId, row.UserId, row.Name.Length > 0 ? row.Name : row.Email ?? "Unknown staff member", row.Email ?? string.Empty, row.State))];
            return ListPage<QualificationHolderRow>.Ungrouped(items, total, page, list.PageSize);
        }, cancellationToken);
    }

    private async Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Qualifications.AsNoTracking().AnyAsync(row => row.Name == name && row.Id != exceptId, cancellationToken);
    }

    private static async Task<List<QualificationCatalogueRow>> LoadCatalogueAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await context.Qualifications.AsNoTracking()
            .Where(qualification => qualification.TenantId == tenantId)
            .OrderBy(qualification => qualification.Name)
            .ThenBy(qualification => qualification.Id)
            .Select(qualification => new { qualification.Id, qualification.TenantId, qualification.Name, qualification.Description, qualification.State })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(row => new QualificationCatalogueRow(row.Id, row.TenantId, row.Name, row.Description, row.State))];
    }

    private async Task<Result> WriteAsync(string what, string conflict, Func<ApplicationDbContext, CancellationToken, Task<Result>> write, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var touched = TenantChangeCollector.Attach(context, this.runtime.TenantId);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var result = await write(context, cancellationToken);
            if (result.IsSuccess)
            {
                await transaction.CommitAsync(CancellationToken.None);
                Publish(touched, what);
            }

            return result;
        }
        catch (TenantHeldException held)
        {
            return Result.Failure(held.Message);
        }
        catch (DbUpdateException exception) when (!cancellationToken.IsCancellationRequested)
        {
            this.log.LogWarning(exception, "Writing to {What} in tenant {TenantId} was refused by the database.", what, this.runtime.TenantId);
            return Result.Failure(conflict);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Trying to {What} in tenant {TenantId} failed.", what, this.runtime.TenantId);
            return Result.Failure("The qualifications could not be changed. Try again.");
        }
    }

    private void Publish(TenantChangeCollector touched, string what)
    {
        try
        {
            touched.PublishTo(this.changes);
        }
        catch (Exception exception)
        {
            this.log.LogError(exception, "Publishing what {What} changed in tenant {TenantId} failed after its commit; open pages read again when they next load.", what, this.runtime.TenantId);
        }
    }

    private async Task<Result<T>> ReadAsync<T>(string what, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return Result<T>.Success(await read(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading {What} of tenant {TenantId} failed.", what, this.runtime.TenantId);
            return Result<T>.Failure(ReadFailed);
        }
    }
}
