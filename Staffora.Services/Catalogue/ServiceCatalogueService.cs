using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Diagnostics;
using Staffora.Services.Jobs;
using Staffora.Services.Workflow;

namespace Staffora.Services.Catalogue;

public sealed class ServiceCatalogueService
{
    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.SettingsView, RolePermission.ServicesEdit];

    public const string NoView = "You do not have permission to view services.";

    public const string NoEdit = "You do not have permission to change services.";

    public const string NotFound = "Service not found.";

    public const string NameTaken = "Another service already has this name.";

    public static readonly string CountOutOfRange = $"A service takes {Service.MinSlotCount} to {Service.MaxSlotCount} people by default.";

    public const string QualificationNotActive = "Only an active qualification of the catalogue is named by a service.";

    public const string OwnTenantOnly = "Services are changed inside the organization, not from the platform console.";

    public const string ReadFailed = "The services could not be read. Try again.";

    public static readonly NameAndDescriptionRule TextRule = new("service", Service.NameMaxLength, Service.DescriptionMaxLength);

    internal static readonly LookupCatalogue<ServiceCatalogueRow> Catalogue = new("services", LoadCatalogueAsync);

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantLookupCache lookupCache;
    private readonly ILogger<ServiceCatalogueService> log;

    public ServiceCatalogueService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        TenantLookupCache lookupCache,
        ILogger<ServiceCatalogueService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.lookupCache = lookupCache;
        this.log = log;
    }

    private static DetachedEntityUpdaterOptions<Service> UpdaterOptions => new DetachedEntityUpdaterOptions<Service>()
        .WithCollection(service => service.Qualifications);

    public async Task<Result<ListPage<ServiceListItem>>> QueryServicesAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ListPage<ServiceListItem>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<ListPage<ServiceListItem>>.Failure(IRuntimeExecutionContext.NoTenant);

        var list = ServiceList.Definition.Normalize(query);
        return await ReadAsync("the service catalogue", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var entries = context.Services.AsNoTracking().Where(service => service.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                entries = entries.Where(service => service.Name.Contains(search) || (service.Description != null && service.Description.Contains(search)));
            }

            if (list.Filter(ServiceList.Status) is { } status && ListOption.TryParseEnum<ServiceState>(status, out var state))
            {
                entries = entries.Where(service => service.State == state);
            }

            if (list.Filter(ServiceList.Portal) is { } portal)
            {
                var offered = portal == ServiceList.Offered;
                entries = entries.Where(service => service.OfferedInPortal == offered);
            }

            var total = await entries.CountAsync(token);
            var page = ListPage<ServiceListItem>.PageWithin(list, total);
            var rows = await new ListOrder<Service>(entries)
                .By(service => service.Name, list.Descending)
                .By(service => service.Id)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(service => new ServiceListItem(service.Id, service.Name, service.Description, service.State, service.DefaultSlotCount, service.OfferedInPortal, service.Qualifications.Count))
                .ToListAsync(token);
            return ListPage<ServiceListItem>.Ungrouped(rows, total, page, list.PageSize);
        }, cancellationToken);
    }

    internal async Task<Result<IReadOnlyList<ServiceCatalogueRow>>> GetCatalogueForJobsAsync(CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.JobsEdit, cancellationToken) || this.runtime.Current.HasPlatformReach())
            return Result<IReadOnlyList<ServiceCatalogueRow>>.Failure(NoView);

        if (this.runtime.TenantId is not { } tenantId)
            return Result<IReadOnlyList<ServiceCatalogueRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        return await ReadAsync("the service catalogue", async token =>
        {
            var catalogue = await this.lookupCache.GetAsync(Catalogue, tenantId, token);
            IReadOnlyList<ServiceCatalogueRow> active = [.. catalogue.Where(service => service.State == ServiceState.Active)];
            return active;
        }, cancellationToken);
    }

    public async Task<Result<DetachedEntityUpdater<Service>>> LoadServiceUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<DetachedEntityUpdater<Service>>.Failure(NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var service = await context.Services
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return service is null
                ? Result<DetachedEntityUpdater<Service>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<Service>>.Success(new DetachedEntityUpdater<Service>(service, context, UpdaterOptions));
        }, this.log, $"the service {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<Service>> NewServiceAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ServicesEdit, cancellationToken))
            return Result<Service>.Failure(NoEdit);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Service>.Failure(OwnTenantOnly);

        return session.TenantId is { } tenantId
            ? Result<Service>.Success(new Service { TenantId = tenantId, State = ServiceState.Create, DefaultSlotCount = 1 })
            : Result<Service>.Failure(IRuntimeExecutionContext.NoTenant);
    }

    public async Task<Result<Guid>> CreateServiceAsync(Service service, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ServicesEdit, cancellationToken))
            return Result<Guid>.Failure(NoEdit);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Guid>.Failure(OwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        var name = service.Name?.Trim() ?? string.Empty;
        var description = string.IsNullOrWhiteSpace(service.Description) ? null : service.Description.Trim();
        if (TextRule.Problem(name, description) is { } textReason)
            return Result<Guid>.Failure(textReason);

        if (service.DefaultSlotCount is < Service.MinSlotCount or > Service.MaxSlotCount)
            return Result<Guid>.Failure(CountOutOfRange);

        var row = new Service
        {
            TenantId = tenantId,
            State = ServiceState.Create,
            Name = name,
            Description = description,
            DefaultSlotCount = service.DefaultSlotCount,
            OfferedInPortal = service.OfferedInPortal,
            CrossTenantId = Guid.NewGuid(),
        };
        foreach (var named in service.Qualifications.GroupBy(named => named.QualificationId))
        {
            row.Qualifications.Add(new ServiceQualification { TenantId = tenantId, QualificationId = named.Key, Level = named.Min(each => each.Level) });
        }

        try
        {
            if (await NameTakenAsync(name, null, cancellationToken))
                return Result<Guid>.Failure(NameTaken);

            if (!await AllActiveAsync(row.Qualifications.Select(named => named.QualificationId), cancellationToken))
                return Result<Guid>.Failure(QualificationNotActive);

            var created = await this.workflows.CreateAndTransitionAsync<ServiceState, Service>(row, transitionKey, ServiceWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

            return !created.IsSuccess && await NameTakenAsync(name, null, cancellationToken) ? Result<Guid>.Failure(NameTaken) : created;
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    public async Task<Result> UpdateServiceAndTransitionAsync(DetachedEntityUpdater<Service> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ServicesEdit, cancellationToken))
            return Result.Failure(NoEdit);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result.Failure(OwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result.Failure(IRuntimeExecutionContext.NoTenant);

        var id = (Guid)updater.OriginalValue(nameof(Service.Id))!;
        var service = updater.Entity;
        if (service.Name is { } typed && typed != typed.Trim())
        {
            service.Name = typed.Trim();
        }

        if (service.Description is { } text && !string.Equals(text, updater.OriginalValue(nameof(Service.Description)) as string, StringComparison.Ordinal))
        {
            service.Description = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        var renamed = !string.Equals(service.Name, updater.OriginalValue(nameof(Service.Name)) as string, StringComparison.Ordinal);
        try
        {
            if (updater.HasChanges())
            {
                if (TextRule.Problem(service.Name ?? string.Empty, service.Description) is { } textReason)
                    return Result.Failure(textReason);

                if (service.DefaultSlotCount is < Service.MinSlotCount or > Service.MaxSlotCount)
                    return Result.Failure(CountOutOfRange);

                if (renamed && await NameTakenAsync(service.Name!, id, cancellationToken))
                    return Result.Failure(NameTaken);

                if (!await AllActiveAsync(await AddedQualificationsAsync(id, service, cancellationToken), cancellationToken))
                    return Result.Failure(QualificationNotActive);
            }

            var saved = await this.workflows.SaveAndTransitionAsync<ServiceState, Service>(updater, transitionKey, ServiceWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            return !saved.IsSuccess && renamed && await NameTakenAsync(service.Name ?? string.Empty, id, cancellationToken) ? Result.Failure(NameTaken) : saved;
        }
        finally
        {
            this.lookupCache.Invalidate(Catalogue, tenantId);
        }
    }

    public async Task<Result<StateHistoryPage>> GetServiceHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<Service>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public async Task<Result<ServiceEditorOptions>> GetServiceEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var edits = await this.permissionService.HasPermissionAsync(RolePermission.ServicesEdit, cancellationToken) && !this.runtime.Current.HasPlatformReach();
        if (id is null ? !edits : !edits && !await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
            return Result<ServiceEditorOptions>.Failure(id is null ? NoEdit : NoView);

        return await ReadAsync("the service editor's options", async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            Guid? serviceId = id;
            var named = await context.ServiceQualifications.AsNoTracking()
                .Where(row => row.ServiceId == serviceId)
                .Select(row => row.QualificationId)
                .ToListAsync(token);
            var qualifications = await context.Qualifications.AsNoTracking()
                .Where(qualification => qualification.State == QualificationState.Active || named.Contains(qualification.Id))
                .OrderBy(qualification => qualification.Name)
                .ThenBy(qualification => qualification.Id)
                .Select(qualification => new SlotQualificationChoice(qualification.Id, qualification.Name, qualification.State == QualificationState.Archived))
                .ToListAsync(token);
            return new ServiceEditorOptions(FieldsLocked: !edits, qualifications);
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> AddedQualificationsAsync(Guid id, Service service, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var stored = await context.ServiceQualifications.AsNoTracking().Where(named => named.ServiceId == id).Select(named => named.QualificationId).ToListAsync(cancellationToken);
        return [.. service.Qualifications.Select(named => named.QualificationId).Except(stored)];
    }

    private async Task<bool> AllActiveAsync(IEnumerable<Guid> qualificationIds, CancellationToken cancellationToken)
    {
        var wanted = qualificationIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return true;
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var active = await context.Qualifications.AsNoTracking().CountAsync(qualification => wanted.Contains(qualification.Id) && qualification.State == QualificationState.Active, cancellationToken);
        return active == wanted.Count;
    }

    private async Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Services.AsNoTracking().AnyAsync(row => row.Name == name && row.Id != exceptId, cancellationToken);
    }

    private static async Task<List<ServiceCatalogueRow>> LoadCatalogueAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await context.Services.AsNoTracking()
            .Where(service => service.TenantId == tenantId)
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .Select(service => new { service.Id, service.TenantId, service.Name, service.Description, service.DefaultSlotCount, service.OfferedInPortal, service.State })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var named = await context.ServiceQualifications.AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .OrderBy(row => row.ServiceId)
            .ThenBy(row => row.QualificationId)
            .Select(row => new { row.ServiceId, row.QualificationId, row.Level })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byService = named.ToLookup(row => row.ServiceId, row => new SlotRequirement(row.QualificationId, row.Level));
        return [.. rows.Select(row => new ServiceCatalogueRow(row.Id, row.TenantId, row.Name, row.Description, row.DefaultSlotCount, row.OfferedInPortal, row.State, [.. byService[row.Id]]))];
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
