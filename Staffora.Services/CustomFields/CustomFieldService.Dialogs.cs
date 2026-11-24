using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;

namespace Staffora.Services.CustomFields;

public partial class CustomFieldService
{
    public async Task<Result<DetachedEntityUpdater<ApplicationUserAttributeDefinition>>> LoadApplicationUserAttributeDefinitionUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await ReadRefusalAsync(cancellationToken) is { } refusal)
            return Result<DetachedEntityUpdater<ApplicationUserAttributeDefinition>>.Failure(refusal);

        var platform = this.runtime.HasPlatformReach();
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = platform ? context.BeginPlatformScope(this.runtime, "open a custom field of users on the platform console") : null;
            var field = await context.ApplicationUserAttributeDefinitions
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id && (platform || row.State == CustomFieldState.Active), cancellationToken);
            return field is null
                ? Result<DetachedEntityUpdater<ApplicationUserAttributeDefinition>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<ApplicationUserAttributeDefinition>>.Success(new DetachedEntityUpdater<ApplicationUserAttributeDefinition>(field, context));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<DetachedEntityUpdater<ApplicationUserAttributeDefinition>>.Failure(OpenFailed);
        }
    }

    public Task<Result> UpdateApplicationUserAttributeDefinitionAndTransitionAsync(DetachedEntityUpdater<ApplicationUserAttributeDefinition> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        UpdateAndTransitionAsync(updater, transitionKey, comment, CustomFieldTarget.User, ApplicationUserAttributeDefinitionWorkflowQueries.WithWorkflowGraph, cancellationToken);

    public Task<Result<ApplicationUserAttributeDefinition>> NewApplicationUserAttributeDefinitionAsync(CancellationToken cancellationToken) =>
        NewFieldAsync(new ApplicationUserAttributeDefinition(), cancellationToken);

    public Task<Result<Guid>> CreateApplicationUserAttributeDefinitionAsync(ApplicationUserAttributeDefinition field, string transitionKey, CancellationToken cancellationToken) =>
        CreateAsync(field, new ApplicationUserAttributeDefinition(), CustomFieldTarget.User, transitionKey, ApplicationUserAttributeDefinitionWorkflowQueries.WithWorkflowGraph, cancellationToken);

    public async Task<Result<StateHistoryPage>> GetApplicationUserAttributeDefinitionHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await HistoryRefusalAsync<ApplicationUserAttributeDefinition>(id, cancellationToken) is { } refusal
            ? Result<StateHistoryPage>.Failure(refusal)
            : await this.history.GetHistoryAsync<ApplicationUserAttributeDefinition>(id, query, cancellationToken);

    public Task<Result<CustomFieldEditorOptions>> GetApplicationUserAttributeDefinitionEditorOptionsAsync(Guid? id, CancellationToken cancellationToken) =>
        EditorOptionsAsync<ApplicationUserAttributeDefinition>(id, cancellationToken);

    public async Task<Result<DetachedEntityUpdater<OrganizationUnitAttributeDefinition>>> LoadOrganizationUnitAttributeDefinitionUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await ReadRefusalAsync(cancellationToken) is { } refusal)
            return Result<DetachedEntityUpdater<OrganizationUnitAttributeDefinition>>.Failure(refusal);

        var platform = this.runtime.HasPlatformReach();
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = platform ? context.BeginPlatformScope(this.runtime, "open a custom field of organization units on the platform console") : null;
            var field = await context.OrganizationUnitAttributeDefinitions
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id && (platform || row.State == CustomFieldState.Active), cancellationToken);
            return field is null
                ? Result<DetachedEntityUpdater<OrganizationUnitAttributeDefinition>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<OrganizationUnitAttributeDefinition>>.Success(new DetachedEntityUpdater<OrganizationUnitAttributeDefinition>(field, context));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<DetachedEntityUpdater<OrganizationUnitAttributeDefinition>>.Failure(OpenFailed);
        }
    }

    public Task<Result> UpdateOrganizationUnitAttributeDefinitionAndTransitionAsync(DetachedEntityUpdater<OrganizationUnitAttributeDefinition> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        UpdateAndTransitionAsync(updater, transitionKey, comment, CustomFieldTarget.OrganizationUnit, OrganizationUnitAttributeDefinitionWorkflowQueries.WithWorkflowGraph, cancellationToken);

    public Task<Result<OrganizationUnitAttributeDefinition>> NewOrganizationUnitAttributeDefinitionAsync(CancellationToken cancellationToken) =>
        NewFieldAsync(new OrganizationUnitAttributeDefinition(), cancellationToken);

    public Task<Result<Guid>> CreateOrganizationUnitAttributeDefinitionAsync(OrganizationUnitAttributeDefinition field, string transitionKey, CancellationToken cancellationToken) =>
        CreateAsync(field, new OrganizationUnitAttributeDefinition(), CustomFieldTarget.OrganizationUnit, transitionKey, OrganizationUnitAttributeDefinitionWorkflowQueries.WithWorkflowGraph, cancellationToken);

    public async Task<Result<StateHistoryPage>> GetOrganizationUnitAttributeDefinitionHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await HistoryRefusalAsync<OrganizationUnitAttributeDefinition>(id, cancellationToken) is { } refusal
            ? Result<StateHistoryPage>.Failure(refusal)
            : await this.history.GetHistoryAsync<OrganizationUnitAttributeDefinition>(id, query, cancellationToken);

    public Task<Result<CustomFieldEditorOptions>> GetOrganizationUnitAttributeDefinitionEditorOptionsAsync(Guid? id, CancellationToken cancellationToken) =>
        EditorOptionsAsync<OrganizationUnitAttributeDefinition>(id, cancellationToken);

    public async Task<Result<DetachedEntityUpdater<JobAttributeDefinition>>> LoadJobAttributeDefinitionUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await ReadRefusalAsync(cancellationToken) is { } refusal)
            return Result<DetachedEntityUpdater<JobAttributeDefinition>>.Failure(refusal);

        var platform = this.runtime.HasPlatformReach();
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = platform ? context.BeginPlatformScope(this.runtime, "open a custom field of jobs on the platform console") : null;
            var field = await context.JobAttributeDefinitions
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id && (platform || row.State == CustomFieldState.Active), cancellationToken);
            return field is null
                ? Result<DetachedEntityUpdater<JobAttributeDefinition>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<JobAttributeDefinition>>.Success(new DetachedEntityUpdater<JobAttributeDefinition>(field, context));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<DetachedEntityUpdater<JobAttributeDefinition>>.Failure(OpenFailed);
        }
    }

    public Task<Result> UpdateJobAttributeDefinitionAndTransitionAsync(DetachedEntityUpdater<JobAttributeDefinition> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        UpdateAndTransitionAsync(updater, transitionKey, comment, CustomFieldTarget.Job, JobAttributeDefinitionWorkflowQueries.WithWorkflowGraph, cancellationToken);

    public Task<Result<JobAttributeDefinition>> NewJobAttributeDefinitionAsync(CancellationToken cancellationToken) =>
        NewFieldAsync(new JobAttributeDefinition { Audience = JobFieldAudience.Coordinators }, cancellationToken);

    public Task<Result<Guid>> CreateJobAttributeDefinitionAsync(JobAttributeDefinition field, string transitionKey, CancellationToken cancellationToken) =>
        CreateAsync(field, new JobAttributeDefinition { Audience = field?.Audience ?? JobFieldAudience.Coordinators }, CustomFieldTarget.Job, transitionKey, JobAttributeDefinitionWorkflowQueries.WithWorkflowGraph, cancellationToken);

    public async Task<Result<StateHistoryPage>> GetJobAttributeDefinitionHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await HistoryRefusalAsync<JobAttributeDefinition>(id, cancellationToken) is { } refusal
            ? Result<StateHistoryPage>.Failure(refusal)
            : await this.history.GetHistoryAsync<JobAttributeDefinition>(id, query, cancellationToken);

    public Task<Result<CustomFieldEditorOptions>> GetJobAttributeDefinitionEditorOptionsAsync(Guid? id, CancellationToken cancellationToken) =>
        EditorOptionsAsync<JobAttributeDefinition>(id, cancellationToken);

    public const string OpenFailed = "An error occurred while opening the custom field.";

    private async Task<string?> ReadRefusalAsync(CancellationToken cancellationToken) =>
        await this.permissionService.HasPermissionAsync(RolePermission.SettingsView, cancellationToken) ? null : NoViewPermission;

    private async Task<string?> WriteRefusalAsync(CancellationToken cancellationToken) =>
        !await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken) ? NoEditPermission
        : !this.runtime.HasPlatformReach() ? FieldsAreThePlatforms
        : null;

    private async Task<string?> OpenRefusalAsync<TDefinition>(Guid id, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        if (await ReadRefusalAsync(cancellationToken) is { } refusal)
            return refusal;

        if (this.runtime.HasPlatformReach())
            return null;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Set<TDefinition>().AsNoTracking().AnyAsync(row => row.Id == id && row.State == CustomFieldState.Active, cancellationToken) ? null : NotFound;
    }

    private async Task<string?> HistoryRefusalAsync<TDefinition>(Guid id, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        if (await OpenRefusalAsync<TDefinition>(id, cancellationToken) is { } refusal)
            return refusal;

        if (this.runtime.HasPlatformReach())
            return null;

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Set<TDefinition>().AsNoTracking().AnyAsync(row => row.Id == id && row.TenantId == null, cancellationToken) ? GlobalHistoryIsThePlatforms : null;
    }

    private async Task<Result<TDefinition>> NewFieldAsync<TDefinition>(TDefinition field, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        if (await WriteRefusalAsync(cancellationToken) is { } refusal)
            return Result<TDefinition>.Failure(refusal);

        field.ValueType = AttributeValueType.Text;
        field.State = CustomFieldState.Create;
        return Result<TDefinition>.Success(field);
    }

    private async Task<Result<CustomFieldEditorOptions>> EditorOptionsAsync<TDefinition>(Guid? id, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        if (await ReadRefusalAsync(cancellationToken) is { } refusal)
            return Result<CustomFieldEditorOptions>.Failure(refusal);

        var session = this.runtime.Current;
        var platform = session.HasPlatformReach();
        var mayChange = platform && await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit, cancellationToken);
        if (id is null && !mayChange)
            return Result<CustomFieldEditorOptions>.Failure(platform ? NoEditPermission : FieldsAreThePlatforms);

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            if (!platform)
            {
                var own = await context.Tenants.AsNoTracking()
                    .Where(tenant => tenant.Id == session.TenantId)
                    .Select(tenant => new CustomFieldTenant(tenant.Id, tenant.Name))
                    .ToListAsync(cancellationToken);
                return Result<CustomFieldEditorOptions>.Success(new CustomFieldEditorOptions(own, FieldsLocked: true));
            }

            using var lease = context.BeginPlatformScope(this.runtime, "list the tenants a custom field can be made for");
            if (lease is null)
                return Result<CustomFieldEditorOptions>.Failure(ConsoleOnly);

            var fieldTenant = id is { } fieldId
                ? await context.Set<TDefinition>().AsNoTracking().Where(row => row.Id == fieldId).Select(row => row.TenantId).FirstOrDefaultAsync(cancellationToken)
                : null;
            var tenants = await context.Tenants.AsNoTracking()
                .Where(tenant => tenant.Id == fieldTenant || (!tenant.IsRootTenant && tenant.ParentTenantId == null && (tenant.State == TenantState.Setup || tenant.State == TenantState.Active)))
                .OrderBy(tenant => tenant.Name)
                .ThenBy(tenant => tenant.Id)
                .Select(tenant => new CustomFieldTenant(tenant.Id, tenant.Name))
                .ToListAsync(cancellationToken);
            return Result<CustomFieldEditorOptions>.Success(new CustomFieldEditorOptions(tenants, FieldsLocked: !mayChange));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<CustomFieldEditorOptions>.Failure("An error occurred while reading the tenants.");
        }
    }

    private async Task<Result<Guid>> CreateAsync<TDefinition>(TDefinition sent, TDefinition row, CustomFieldTarget target, string transitionKey, Func<IQueryable<TDefinition>, IQueryable<TDefinition>> graph, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (await WriteRefusalAsync(cancellationToken) is { } refusal)
            return Result<Guid>.Failure(refusal);

        row.Name = sent.Name?.Trim() ?? string.Empty;
        row.ValueType = sent.ValueType;
        row.TenantId = sent.TenantId;
        row.Options = sent.ValueType == AttributeValueType.Choice ? AttributeChoiceOption.WithKeys(sent.Options ?? [], []) : [];
        row.IsRequired = sent.IsRequired && CustomFieldTargets.CanBeRequired(target);
        row.State = CustomFieldState.Create;
        if (AttributeDefinitionRules.DescribeInvalid(row) is { } invalid)
            return Result<Guid>.Failure(invalid);

        try
        {
            if (await CreateCheckAsync<TDefinition>(target, row.Name, row.TenantId) is { } check)
                return Result<Guid>.Failure(check);

            var created = await this.workflows.CreateAndTransitionAsync<CustomFieldState, TDefinition>(row, transitionKey, graph, cancellationToken: cancellationToken);
            return !created.IsSuccess && await CreateCheckAsync<TDefinition>(target, row.Name, row.TenantId) is { } clash
                ? Result<Guid>.Failure(clash)
                : created;
        }
        catch (Exception exception) when (Refusal(exception) is { } reason)
        {
            return Result<Guid>.Failure(reason);
        }
        finally
        {
            Invalidate<TDefinition>(row.TenantId);
        }
    }

    private async Task<string?> CreateCheckAsync<TDefinition>(CustomFieldTarget target, string name, Guid? tenantId)
        where TDefinition : class, IEntityAttributeDefinition
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, tenantId is null ? "check a new global custom field" : "check a new custom field for a tenant");
        if (lease is null)
            return FieldsAreThePlatforms;

        if (tenantId is { } chosen && await FieldTenantRefusalAsync(context, chosen) is { } held)
            return held;

        return await NameClashAsync<TDefinition>(context, target, name, tenantId, exceptId: null);
    }

    private async Task<Result> UpdateAndTransitionAsync<TDefinition>(DetachedEntityUpdater<TDefinition> updater, string transitionKey, string? comment, CustomFieldTarget target, Func<IQueryable<TDefinition>, IQueryable<TDefinition>> graph, CancellationToken cancellationToken)
        where TDefinition : class, IEntityAttributeDefinition
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (await WriteRefusalAsync(cancellationToken) is { } refusal)
            return Result.Failure(refusal);

        var id = (Guid)updater.OriginalValue(nameof(IEntityAttributeDefinition.Id))!;
        var edited = updater.Entity;
        if (edited.Id != id)
            return Result.Failure(NotFound);

        Guid? tenant = null;
        var renamed = false;
        try
        {
            await using (var context = this.dbFactory.CreateDbContext(this.runtime))
            {
                using var lease = context.BeginPlatformScope(this.runtime, "check a custom field's edits");
                if (lease is null)
                    return Result.Failure(FieldsAreThePlatforms);

                var stored = await context.Set<TDefinition>().AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (stored is null)
                    return Result.Failure(NotFound);

                tenant = stored.TenantId;
                if (tenant is { } owner && await FieldTenantRefusalAsync(context, owner) is { } held)
                    return Result.Failure(held);

                if (edited.Name is { } name && name != name.Trim())
                {
                    edited.Name = name.Trim();
                }

                if (edited.ValueType == AttributeValueType.Choice && (edited.Options ?? []).Any(option => string.IsNullOrEmpty(option.Key)))
                {
                    edited.Options = AttributeChoiceOption.WithKeys(edited.Options ?? [], stored.Options.Select(option => option.Key));
                }

                if (updater.HasChanges() && AttributeDefinitionRules.DescribeInvalid(edited, stored) is { } invalid)
                    return Result.Failure(invalid);

                renamed = !string.Equals(edited.Name, stored.Name, StringComparison.Ordinal);
                if (renamed && await NameClashAsync<TDefinition>(context, target, edited.Name ?? string.Empty, stored.TenantId, exceptId: stored.Id) is { } clash)
                    return Result.Failure(clash);
            }

            var saved = await this.workflows.SaveAndTransitionAsync<CustomFieldState, TDefinition>(updater, transitionKey, graph, comment, cancellationToken);
            if (saved.IsSuccess)
                return saved;

            if (renamed && await RenameClashAsync<TDefinition>(target, edited.Name ?? string.Empty, tenant, id) is { } lost)
                return Result.Failure(lost);

            return await SavedMeanwhileAsync<TDefinition>(id, updater.OriginalValue(nameof(EntityAttributeDefinitionBase<object>.RowVersion)) as byte[]) ? Result.Failure(ChangedMeanwhile) : saved;
        }
        catch (Exception exception) when (Refusal(exception) is { } reason)
        {
            return Result.Failure(reason);
        }
        finally
        {
            Invalidate<TDefinition>(tenant);
        }
    }

    private async Task<string?> RenameClashAsync<TDefinition>(CustomFieldTarget target, string name, Guid? tenantId, Guid id)
        where TDefinition : class, IEntityAttributeDefinition
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "check a custom field's name after a refused step");
        return lease is null ? null : await NameClashAsync<TDefinition>(context, target, name, tenantId, exceptId: id);
    }

    private async Task<bool> SavedMeanwhileAsync<TDefinition>(Guid id, byte[]? opened)
        where TDefinition : class, IEntityAttributeDefinition
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "check whether a custom field was saved meanwhile");
        var stored = lease is null ? null : await context.Set<TDefinition>().AsNoTracking().Where(row => row.Id == id).Select(row => EF.Property<byte[]>(row, nameof(EntityAttributeDefinitionBase<object>.RowVersion))).FirstOrDefaultAsync();
        return opened is not null && stored is not null && !stored.AsSpan().SequenceEqual(opened);
    }

    private void Invalidate<TDefinition>(Guid? tenantId)
        where TDefinition : class, IEntityAttributeDefinition
    {
        if (tenantId is { } tenant)
        {
            this.lookupCache.Invalidate(DefinitionCatalogue<TDefinition>.Instance, tenant);
        }
        else
        {
            this.lookupCache.InvalidateEveryTenant(DefinitionCatalogue<TDefinition>.Instance);
        }
    }
}

public sealed record CustomFieldTenant(Guid Id, string Name);

public sealed record CustomFieldEditorOptions(IReadOnlyList<CustomFieldTenant> Tenants, bool FieldsLocked);
