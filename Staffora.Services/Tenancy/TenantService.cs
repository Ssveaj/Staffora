using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Workflow;

namespace Staffora.Services.Tenancy;

public class TenantService
{
    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly TenantSettingsEnsureService settingsEnsure;
    private readonly ILogger<TenantService> log;

    public TenantService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, TenantSettingsEnsureService settingsEnsure, ILogger<TenantService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.settingsEnsure = settingsEnsure;
        this.log = log;
    }

    public async Task<Result<Tenant>> FetchTenantContentAsync()
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.TenantView))
        {
            return Result<Tenant>.Failure("You do not have permission to view the tenant.");
        }

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var tenant = await context.Tenants.Include(x => x.OrganizationUnits).FirstOrDefaultAsync(x => x.Id == this.runtime.TenantId);
        return tenant is null ? Result<Tenant>.Failure("Tenant not found.") : Result<Tenant>.Success(tenant);
    }

    public const string ConsoleOnly = "Only the platform console opens tenants.";

    public const string ConsoleOnlyCreates = "Only the platform console creates tenants.";

    public const string TenantNotFound = "The tenant was not found.";

    public const string TenantReadFailed = "The tenant could not be read. Try again.";

    public const string NameTaken = "A tenant with that name already exists.";

    public const string PlatformTenantFixed = "The platform tenant's name and description cannot be changed.";

    public async Task<Result<DetachedEntityUpdater<Tenant>>> LoadTenantUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<DetachedEntityUpdater<Tenant>>.Failure(ConsoleOnly);
        }

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "open a tenant's dialog");
            var tenant = await context.Tenants
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return tenant is null
                ? Result<DetachedEntityUpdater<Tenant>>.Failure(TenantNotFound)
                : Result<DetachedEntityUpdater<Tenant>>.Success(new DetachedEntityUpdater<Tenant>(tenant, context));
        }, this.log, $"the tenant {id}", TenantReadFailed, cancellationToken);
    }

    internal async Task<Result<Guid>> CreateTenantAsync(Tenant tenant, string transitionKey, CancellationToken cancellationToken, Func<ApplicationDbContext, Guid, CancellationToken, Task>? onboard = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (!this.runtime.HasPlatformReach())
        {
            return Result<Guid>.Failure(ConsoleOnlyCreates);
        }

        var row = new Tenant
        {
            Name = (tenant.Name ?? string.Empty).Trim(),
            Description = string.IsNullOrWhiteSpace(tenant.Description) ? null : tenant.Description.Trim(),
            State = tenant.State,
        };

        if (row.Name.Length > 0 && await NameIsTakenAsync(row.Name, exceptId: null, cancellationToken))
        {
            return Result<Guid>.Failure(NameTaken);
        }

        var created = await this.workflows.CreateAndTransitionAsync<TenantState, Tenant>(row, transitionKey, afterInsert: async (context, inserted, ct) =>
        {
            await GiveNewTenantItsOwnRowsAsync(context, inserted, ct);
            if (onboard is not null)
            {
                await onboard(context, inserted.Id, ct);
            }
        }, queryConfigurator: TenantWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        if (created.IsSuccess)
        {
            this.settingsEnsure.ForgetCachedSettings(created.Value);
            return created;
        }

        return row.Name.Length > 0 && await NameIsTakenAsync(row.Name, exceptId: null, cancellationToken) ? Result<Guid>.Failure(NameTaken) : created;
    }

    internal async Task GiveNewTenantItsOwnRowsAsync(ApplicationDbContext context, Tenant tenant, CancellationToken cancellationToken, string? rootName = null)
    {
        await SystemAccountService.EnsureAsync(context, tenant.Id, cancellationToken);
        await CompanyRoot.EnsureAsync(context, tenant.Id, cancellationToken, rootName);
        await this.settingsEnsure.EnsureTimeZoneAsync(context, tenant.Id, cancellationToken);
    }

    public async Task<Result> UpdateTenantAndTransitionAsync(DetachedEntityUpdater<Tenant> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!this.runtime.HasPlatformReach())
        {
            return Result.Failure(ConsoleOnly);
        }

        var id = (Guid)updater.OriginalValue(nameof(Tenant.Id))!;
        var name = updater.Entity.Name;
        if (name is not null && name != name.Trim())
        {
            updater.Entity.Name = name = name.Trim();
        }

        var renamed = name is { Length: > 0 } && !string.Equals(name, (string?)updater.OriginalValue(nameof(Tenant.Name)), StringComparison.Ordinal);
        if (updater.HasChanges() && await IsPlatformTenantAsync(id, cancellationToken))
        {
            return Result.Failure(PlatformTenantFixed);
        }

        if (renamed && await NameIsTakenAsync(name!, id, cancellationToken))
        {
            return Result.Failure(NameTaken);
        }

        var saved = await this.workflows.SaveAndTransitionAsync<TenantState, Tenant>(updater, transitionKey, TenantWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        return !saved.IsSuccess && renamed && await NameIsTakenAsync(name!, id, cancellationToken) ? Result.Failure(NameTaken) : saved;
    }

    private async Task<bool> NameIsTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "check that a tenant's name is free");
        return await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Name == name && (exceptId == null || tenant.Id != exceptId), cancellationToken);
    }

    private async Task<bool> IsPlatformTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "check whether a tenant is the platform tenant");
        return await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == id && tenant.IsRootTenant, cancellationToken);
    }

    public async Task<Result<StateHistoryPage>> GetTenantHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.runtime.HasPlatformReach()
            ? await this.history.GetHistoryAsync<Tenant>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(ConsoleOnly);
}
