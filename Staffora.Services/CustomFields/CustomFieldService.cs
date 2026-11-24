using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Data.Configurations.EntityAttributes;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Workflow;

namespace Staffora.Services.CustomFields;

public partial class CustomFieldService
{
    public const string NoViewPermission = "You do not have permission to view custom fields.";
    public const string NoEditPermission = "You do not have permission to change custom fields.";
    public const string NotFound = "Custom field not found.";

    public const string FieldsAreThePlatforms = "Custom fields are set up by the platform, so they cannot be added or changed here.";

    public const string ChangedMeanwhile = "Someone else changed this field after you opened it. Close it and open it again to see their change, then make yours.";

    public const string GlobalHistoryIsThePlatforms = "A global field's history is kept by the platform.";

    public const string CannotChangeHere = "This field cannot be changed here.";

    public const string OtherTenantsFields = "Custom fields are read only in your own tenant.";

    public const string UnknownTenant = "There is no such tenant.";

    public const string ConsoleOnly = "Only a platform owner on the platform console, not impersonating, reads another tenant's custom fields.";

    private static readonly string[] nameIndexes =
    [
        EntityAttributeDefinitionBaseConfiguration.NameIndexOf(typeof(ApplicationUserAttributeDefinition)),
        EntityAttributeDefinitionBaseConfiguration.NameIndexOf(typeof(OrganizationUnitAttributeDefinition)),
        EntityAttributeDefinitionBaseConfiguration.NameIndexOf(typeof(JobAttributeDefinition)),
    ];

    public const string NoOwnAssignments = "You do not have permission to view your assignments.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly TenantLookupCache lookupCache;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;

    public CustomFieldService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, TenantLookupCache lookupCache, WorkflowService workflows, StateHistoryService history)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.lookupCache = lookupCache;
        this.workflows = workflows;
        this.history = history;
    }

    public async Task<Result<ListPage<CustomFieldRow>>> QueryDefinitionsAsync(ListQuery query)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.SettingsView))
            return Result<ListPage<CustomFieldRow>>.Failure(NoViewPermission);

        var session = this.runtime.Current;
        var platform = session.HasPlatformReach();
        if (!platform && session.TenantId is null)
            return Result<ListPage<CustomFieldRow>>.Failure(IRuntimeExecutionContext.NoTenant);

        var canEdit = await this.permissionService.HasPermissionAsync(RolePermission.SettingsEdit);

        return platform
            ? await QueryDefinitionsOfAsync(CustomFieldList.PlatformDefinition, query, tenantId: null, canEdit, activeOnly: false, platformReason: "list every custom field on the platform settings", everyTenant: true)
            : await QueryDefinitionsOfAsync(CustomFieldList.Definition, query, session.TenantId, canEdit: false, activeOnly: true, platformReason: null);
    }

    public async Task<Result<ListPage<CustomFieldRow>>> QueryTenantDefinitionsAsync(Guid tenantId, ListQuery query)
    {
        if (!this.runtime.HasPlatformReach())
            return Result<ListPage<CustomFieldRow>>.Failure(ConsoleOnly);

        await using (var context = this.dbFactory.CreateDbContext(this.runtime))
        {
            using var lease = context.BeginPlatformScope(this.runtime, "check that a tenant exists before listing its custom fields");
            if (lease is null)
                return Result<ListPage<CustomFieldRow>>.Failure(ConsoleOnly);

            if (!await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId))
                return Result<ListPage<CustomFieldRow>>.Failure(UnknownTenant);
        }

        return await QueryDefinitionsOfAsync(CustomFieldList.ConsoleDefinition, query, tenantId, canEdit: true, activeOnly: false, platformReason: "list a tenant's custom fields on its console");
    }

    private async Task<Result<ListPage<CustomFieldRow>>> QueryDefinitionsOfAsync(ListDefinition listDefinition, ListQuery query, Guid? tenantId, bool canEdit, bool activeOnly, string? platformReason, bool everyTenant = false)
    {
        var list = listDefinition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = platformReason is null ? null : context.BeginPlatformScope(this.runtime, platformReason);
            if (platformReason is not null && lease is null)
                return Result<ListPage<CustomFieldRow>>.Failure(ConsoleOnly);

            var users = context.ApplicationUserAttributeDefinitions.AsNoTracking()
                .Where(definition => (everyTenant || definition.TenantId == null || (tenantId != null && definition.TenantId == tenantId)) && (!activeOnly || definition.State == CustomFieldState.Active))
                .Select(definition => new FieldEntry { Id = definition.Id, Target = CustomFieldTarget.User, Name = definition.Name, ValueType = definition.ValueType, State = definition.State, TenantId = definition.TenantId, TenantName = everyTenant ? context.Tenants.Where(tenant => tenant.Id == definition.TenantId).Select(tenant => tenant.Name).FirstOrDefault() : null, Options = definition.Options, Audience = null });
            var units = context.OrganizationUnitAttributeDefinitions.AsNoTracking()
                .Where(definition => (everyTenant || definition.TenantId == null || (tenantId != null && definition.TenantId == tenantId)) && (!activeOnly || definition.State == CustomFieldState.Active))
                .Select(definition => new FieldEntry { Id = definition.Id, Target = CustomFieldTarget.OrganizationUnit, Name = definition.Name, ValueType = definition.ValueType, State = definition.State, TenantId = definition.TenantId, TenantName = everyTenant ? context.Tenants.Where(tenant => tenant.Id == definition.TenantId).Select(tenant => tenant.Name).FirstOrDefault() : null, Options = definition.Options, Audience = null });
            var jobs = context.JobAttributeDefinitions.AsNoTracking()
                .Where(definition => (everyTenant || definition.TenantId == null || (tenantId != null && definition.TenantId == tenantId)) && (!activeOnly || definition.State == CustomFieldState.Active))
                .Select(definition => new FieldEntry { Id = definition.Id, Target = CustomFieldTarget.Job, Name = definition.Name, ValueType = definition.ValueType, State = definition.State, TenantId = definition.TenantId, TenantName = everyTenant ? context.Tenants.Where(tenant => tenant.Id == definition.TenantId).Select(tenant => tenant.Name).FirstOrDefault() : null, Options = definition.Options, Audience = definition.Audience });

            var fields = list.Filter(CustomFieldList.Entity) switch
            {
                CustomFieldList.User => users,
                CustomFieldList.Unit => units,
                CustomFieldList.Job => jobs,
                _ => users.Concat(units).Concat(jobs),
            };

            if (list.Search is { Length: > 0 } search)
            {
                fields = fields.Where(field => field.Name.Contains(search) || (field.TenantName != null && field.TenantName.Contains(search)));
            }

            fields = list.Filter(CustomFieldList.Scope) switch
            {
                CustomFieldList.Own => fields.Where(field => field.TenantId != null),
                CustomFieldList.Global => fields.Where(field => field.TenantId == null),
                _ => fields,
            };

            fields = list.Filter(CustomFieldList.StateFilter) switch
            {
                CustomFieldList.Draft => fields.Where(field => field.State == CustomFieldState.Draft),
                CustomFieldList.Active => fields.Where(field => field.State == CustomFieldState.Active),
                CustomFieldList.Inactive => fields.Where(field => field.State == CustomFieldState.Inactive),
                _ => fields,
            };

            var total = await fields.CountAsync();
            var page = ListPage<CustomFieldRow>.PageWithin(list, total);
            var order = new ListOrder<FieldEntry>(fields);
            var byEntity = list.Group == CustomFieldList.ByEntity;
            var byTenant = list.Group == CustomFieldList.ByTenant;
            if (byEntity)
            {
                order.By(field => field.Target);
            }
            else if (byTenant)
            {
                order.By(field => field.TenantName).By(field => field.TenantId);
            }

            _ = list.Sort switch
            {
                CustomFieldList.SortType => order.By(field => field.ValueType, list.Descending).By(field => field.Name),
                _ => order.By(field => field.Name, list.Descending),
            };

            var entries = await order.By(field => field.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .ToListAsync();
            var rows = entries.Select(field => new CustomFieldRow(field.Id, field.Target, field.Name, field.ValueType, field.TenantId is null, canEdit)
            {
                State = field.State,
                TenantId = everyTenant ? field.TenantId : null,
                TenantName = field.TenantName,
                Options = [.. field.Options.Where(option => !option.IsRetired).Select(option => option.Label)],
                RetiredOptions = field.Options.Count(option => option.IsRetired),
                Audience = field.Audience is { } audience ? JobFieldAudiences.Label(audience) : null,
            }).ToList();

            if (byTenant)
            {
                var tenants = await fields.GroupBy(field => new { field.TenantId, field.TenantName }).Select(group => new { group.Key.TenantId, group.Key.TenantName, Count = group.Count() }).ToListAsync();
                var tenantGroups = tenants.ToDictionary(group => TenantGroupOf(group.TenantId), group => (group.TenantName ?? "Global", group.Count));
                return Result<ListPage<CustomFieldRow>>.Success(ListPage<CustomFieldRow>.Grouped(rows, total, page, list.PageSize, row => TenantGroupOf(row.TenantId), tenantGroups));
            }

            if (!byEntity)
            {
                return Result<ListPage<CustomFieldRow>>.Success(ListPage<CustomFieldRow>.Ungrouped(rows, total, page, list.PageSize));
            }

            var counts = await fields.GroupBy(field => field.Target).Select(group => new { group.Key, Count = group.Count() }).ToListAsync();
            var groups = counts.ToDictionary(count => CustomFieldList.ValueOf(count.Key), count => (CustomFieldTargets.Label(count.Key), count.Count));
            return Result<ListPage<CustomFieldRow>>.Success(ListPage<CustomFieldRow>.Grouped(rows, total, page, list.PageSize, row => CustomFieldList.ValueOf(row.Target), groups));
        }
        catch (Exception)
        {
            return Result<ListPage<CustomFieldRow>>.Failure("An error occurred while reading the custom fields.");
        }
    }

    private static string TenantGroupOf(Guid? tenantId) => tenantId?.ToString() ?? CustomFieldList.GlobalGroup;

    private static async Task<string?> FieldTenantRefusalAsync(ApplicationDbContext context, Guid tenantId)
    {
        var tenant = await context.Tenants.AsNoTracking()
            .Where(candidate => candidate.Id == tenantId && !candidate.IsRootTenant && candidate.ParentTenantId == null)
            .Select(candidate => new { candidate.State })
            .FirstOrDefaultAsync();
        return tenant is null ? UnknownTenant
            : TenantStates.HoldsData(tenant.State) ? TenantStates.HeldDataReason
            : null;
    }

    public async Task<Result<List<TDefinition>>> GetDefinitionsAsync<TDefinition>(Guid? tenantId = null) where TDefinition : class, IEntityAttributeDefinition, new()
    {
        if (!await this.permissionService.HasAnyPermissionAsync(DefinitionReaders<TDefinition>()))
            return Result<List<TDefinition>>.Failure(NoViewPermission);

        var session = this.runtime.Current;
        if ((tenantId ?? session.TenantId) is not { } tenant)
            return Result<List<TDefinition>>.Failure(IRuntimeExecutionContext.NoTenant);

        if (tenant != session.TenantId && !session.HasPlatformReach())
            return Result<List<TDefinition>>.Failure(OtherTenantsFields);

        try
        {
            if (tenant != session.TenantId && !await TenantExistsAsync(tenant))
                return Result<List<TDefinition>>.Failure(UnknownTenant);

            var rows = await this.lookupCache.GetAsync(DefinitionCatalogue<TDefinition>.Instance, tenant);
            return Result<List<TDefinition>>.Success([.. rows.Select(DefinitionOf<TDefinition>)]);
        }
        catch (Exception)
        {
            return Result<List<TDefinition>>.Failure("An error occurred while reading the custom fields.");
        }
    }

    internal async Task<Result<IReadOnlyList<AssignedStaffJobField>>> ReadAssignedStaffJobFieldsAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken))
            return Result<IReadOnlyList<AssignedStaffJobField>>.Failure(NoOwnAssignments);

        var session = this.runtime.Current;
        if (session.TenantId is not { } tenant)
            return Result<IReadOnlyList<AssignedStaffJobField>>.Failure(IRuntimeExecutionContext.NoTenant);

        var self = session.EffectiveUserId;
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);

            var jobId = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.Id == assignmentId && assignment.TenantId == tenant && assignment.State == SlotAssignmentState.Assigned && assignment.Resource.UserId == self)
                .Select(assignment => (Guid?)assignment.Slot.JobId)
                .FirstOrDefaultAsync(cancellationToken);
            if (jobId is not { } job)
                return Result<IReadOnlyList<AssignedStaffJobField>>.Success([]);

            var rows = await context.Set<JobAttributeDefinition>().AsNoTracking()
                .Where(definition => definition.Audience == JobFieldAudience.AssignedStaff && definition.State == CustomFieldState.Active && (definition.TenantId == null || definition.TenantId == tenant))
                .OrderBy(definition => definition.Name)
                .ThenBy(definition => definition.Id)
                .Select(definition => new
                {
                    Definition = definition,
                    Value = context.JobAttributes.Where(value => value.EntityId == job && value.EntityAttributeDefinitionId == definition.Id).FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);
            return Result<IReadOnlyList<AssignedStaffJobField>>.Success([.. rows.Select(row => new AssignedStaffJobField(row.Definition, row.Value))]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<AssignedStaffJobField>>.Failure("An error occurred while reading the custom fields.");
        }
    }

    private static TDefinition DefinitionOf<TDefinition>(CustomFieldDefinitionRow row) where TDefinition : class, IEntityAttributeDefinition, new()
    {
        var definition = new TDefinition
        {
            Id = row.Id,
            TenantId = row.TenantId,
            Name = row.Name,
            ValueType = row.ValueType,
            Options = [.. row.Options],
            IsRequired = row.IsRequired,
            State = CustomFieldState.Active,
        };
        if (definition is JobAttributeDefinition job)
        {
            job.Audience = row.Audience ?? JobFieldAudience.Coordinators;
        }

        return definition;
    }

    private async Task<bool> TenantExistsAsync(Guid tenantId)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        using var lease = context.BeginPlatformScope(this.runtime, "check the tenant whose custom fields are read");
        return await context.Tenants.AnyAsync(candidate => candidate.Id == tenantId);
    }

    internal static class DefinitionCatalogue<TDefinition> where TDefinition : class, IEntityAttributeDefinition
    {
        public static readonly LookupCatalogue<CustomFieldDefinitionRow> Instance = new($"custom fields ({typeof(TDefinition).Name})", LoadAsync);

        private static async Task<List<CustomFieldDefinitionRow>> LoadAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
        {
            var definitions = await context.Set<TDefinition>().AsNoTracking()
                .Where(definition => (definition.TenantId == null || definition.TenantId == tenantId) && definition.State == CustomFieldState.Active)
                .OrderBy(definition => definition.Name)
                .ThenBy(definition => definition.Id)
                .Select(definition => new { definition.Id, definition.TenantId, definition.Name, definition.ValueType, definition.Options, definition.IsRequired })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var audiences = typeof(TDefinition) == typeof(JobAttributeDefinition)
                ? await context.JobAttributeDefinitions.AsNoTracking()
                    .Where(definition => (definition.TenantId == null || definition.TenantId == tenantId) && definition.State == CustomFieldState.Active)
                    .ToDictionaryAsync(definition => definition.Id, definition => (JobFieldAudience?)definition.Audience, cancellationToken)
                    .ConfigureAwait(false)
                : new Dictionary<Guid, JobFieldAudience?>();
            return [.. definitions.Select(definition => new CustomFieldDefinitionRow(definition.Id, definition.TenantId, definition.Name, definition.ValueType, Array.AsReadOnly([.. definition.Options]), definition.IsRequired, audiences.GetValueOrDefault(definition.Id)))];
        }
    }

    private static async Task<string?> NameClashAsync<TDefinition>(ApplicationDbContext context, CustomFieldTarget target, string name, Guid? tenantId, Guid? exceptId)
        where TDefinition : class, IEntityAttributeDefinition
    {
        var named = context.Set<TDefinition>().AsNoTracking().Where(definition => definition.Name == name && definition.Id != exceptId);
        if (await named.AnyAsync(definition => definition.TenantId == tenantId))
            return NameTaken(target, name);

        if (tenantId is not null)
        {
            return await named.AnyAsync(definition => definition.TenantId == null)
                ? $"\"{name}\" is the name of a global field for {CustomFieldTargets.Label(target).ToLowerInvariant()}. Choose another name."
                : null;
        }

        var tenants = await named.Where(definition => definition.TenantId != null).Select(definition => definition.TenantId).Distinct().CountAsync();
        return tenants == 0 ? null : $"{tenants} {(tenants == 1 ? "tenant has" : "tenants have")} a field named \"{name}\" for {CustomFieldTargets.Label(target).ToLowerInvariant()}. Choose another name.";
    }

    private static string NameTaken(CustomFieldTarget target, string name) =>
        $"A field named \"{name}\" exists already for {CustomFieldTargets.Label(target).ToLowerInvariant()}.";

    private static string? Refusal(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } sql } when nameIndexes.Any(index => sql.Message.Contains(index, StringComparison.Ordinal))
            => "A field with that name exists already.",
        CustomFieldException refused => refused.Message,
        TenantHeldException held => held.Message,
        TenantIsolationException => CannotChangeHere,
        _ => null,
    };

    private static RolePermission[] DefinitionReaders<TDefinition>() =>
        [RolePermission.SettingsView, .. AreasOf(typeof(TDefinition)).SelectMany(PermissionService.GetPermissionsByArea)];

    private static PermissionArea[] AreasOf(Type definitionType) =>
        typeof(EntityAttributeDefinitionBase<ApplicationUser>).IsAssignableFrom(definitionType) ? [PermissionArea.Users]
        : typeof(EntityAttributeDefinitionBase<OrganizationUnit>).IsAssignableFrom(definitionType) ? [PermissionArea.OrganizationUnits]
        : typeof(EntityAttributeDefinitionBase<Job>).IsAssignableFrom(definitionType) ? [PermissionArea.Jobs, PermissionArea.Coordination]
        : [];

    private sealed class FieldEntry
    {
        public Guid Id { get; init; }
        public CustomFieldTarget Target { get; init; }
        public string Name { get; init; } = string.Empty;
        public AttributeValueType ValueType { get; init; }
        public CustomFieldState State { get; init; }
        public Guid? TenantId { get; init; }
        public string? TenantName { get; init; }
        public List<AttributeChoiceOption> Options { get; init; } = [];
        public JobFieldAudience? Audience { get; init; }
    }
}

internal sealed record AssignedStaffJobField(JobAttributeDefinition Field, JobAttribute? Value);
