using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;

namespace Staffora.Data;

public partial class ApplicationDbContext
{
    private const string AttributeOwner = nameof(EntityAttributeBase<,>.Entity);

    private SystemScopeLeases? systemScope;
    private ILogger? isolationLog;

    private HashSet<object>? namedEditors;


    public bool IsInSystemScope => this.systemScope?.AnyOpen == true;

    public Guid? CurrentTenantId => RuntimeContext?.TenantId;

    public IDisposable BeginSystemScope(string reason) => (this.systemScope ??= new SystemScopeLeases(IsolationLog())).Open(reason);

    public void NameEditor(ITenantEntity row, Guid editorId)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.ModifiedById = editorId;
        (this.namedEditors ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(row);
    }

    public Guid ActorIn(Guid tenantId)
    {
        var session = RuntimeContext?.Current;
        return session?.TenantId == tenantId && session.EffectiveUserId is { } user ? user : SystemAccount.IdFor(tenantId);
    }

    public void StampOnOtherTenantsBehalf()
    {
        if (!IsInSystemScope)
        {
            return;
        }

        var runtimeTenant = RuntimeContext?.Current.TenantId;
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>().Where(entry => entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != runtimeTenant).ToList())
        {
            var row = entry.Entity;
            if (entry.State == EntityState.Added && row.CreatedById == Guid.Empty)
            {
                row.CreatedById = ActorIn(row.TenantId);
            }
            else if (entry.State == EntityState.Modified)
            {
                NameEditor(row, ActorIn(row.TenantId));
            }
        }

        foreach (var entry in ChangeTracker.Entries().Where(entry => IsAttributeDefinition(entry.Metadata.ClrType)).ToList())
        {
            var row = (IEntityAttributeDefinition)entry.Entity;
            if (row.TenantId is not { } tenant || tenant == runtimeTenant)
            {
                continue;
            }

            if (entry.State == EntityState.Added && row.CreatedById == Guid.Empty)
            {
                row.CreatedById = ActorIn(tenant);
            }
            else if (entry.State == EntityState.Modified)
            {
                row.ModifiedById = ActorIn(tenant);
                (this.namedEditors ??= new HashSet<object>(ReferenceEqualityComparer.Instance)).Add(row);
            }
        }
    }

    private ILogger IsolationLog() =>
        this.isolationLog ??= (this.GetInfrastructure().GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<ApplicationDbContext>();

    private void AddTenantFilters(ModelBuilder builder)
    {
        var context = Expression.Constant(this, typeof(ApplicationDbContext));
        var inSystemScope = Expression.Property(context, nameof(IsInSystemScope));
        var runtimeTenant = Expression.Property(context, nameof(CurrentTenantId));
        var hasTenant = Expression.NotEqual(runtimeTenant, Expression.Constant(null, typeof(Guid?)));

        Expression IsRuntimeTenants(Expression tenant) =>
            Expression.AndAlso(hasTenant, Expression.Equal(Expression.Convert(tenant, typeof(Guid?)), runtimeTenant));

        var roots = builder.Model.GetEntityTypes().Where(type => type.BaseType is null).ToList();
        foreach (var root in roots)
        {
            var type = root.ClrType;
            var row = Expression.Parameter(type, "row");
            var tenantColumn = nameof(ITenantEntity.TenantId);
            Expression? isRuntimeTenants = type switch
            {
                _ when typeof(ITenantEntity).IsAssignableFrom(type) => IsRuntimeTenants(Expression.Property(row, tenantColumn)),
                _ when type == typeof(Tenant) => IsRuntimeTenants(Expression.Property(row, nameof(Tenant.Id))),
                _ when IsAttributeDefinition(type) => Expression.OrElse(
                    Expression.Equal(Expression.Property(row, tenantColumn), Expression.Constant(null, typeof(Guid?))),
                    IsRuntimeTenants(Expression.Property(row, tenantColumn))),
                _ when IsAttributeValue(type) => IsRuntimeTenants(Expression.Property(Expression.Property(row, AttributeOwner), tenantColumn)),
                _ when type == typeof(ImpersonationAudit) || type == typeof(ErrorLog) => Expression.Constant(false),
                _ => null,
            };

            if (isRuntimeTenants is not null)
            {
                builder.Entity(type).HasQueryFilter(Expression.Lambda(Expression.OrElse(inSystemScope, isRuntimeTenants), row));
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        return SaveGuardedAsync(acceptAllChangesOnSuccess, synchronously: true, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveGuardedAsync(acceptAllChangesOnSuccess, synchronously: false, cancellationToken);

    private async Task<int> SaveGuardedAsync(bool acceptAllChangesOnSuccess, bool synchronously, CancellationToken cancellationToken)
    {
        try
        {
            await GuardWritesAsync(synchronously, cancellationToken);
            if (this.heldSaveTenant is null)
            {
                return await SaveCheckingStructureAsync(acceptAllChangesOnSuccess, synchronously, cancellationToken);
            }

            var behavior = Database.AutoTransactionBehavior;
            Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
            try
            {
                return await SaveCheckingStructureAsync(acceptAllChangesOnSuccess, synchronously, cancellationToken);
            }
            finally
            {
                Database.AutoTransactionBehavior = behavior;
            }
        }
        finally
        {
            this.heldSaveTenant = null;
        }
    }

    private async Task GuardWritesAsync(bool synchronously, CancellationToken cancellationToken)
    {
        try
        {
            RenewUserConcurrencyStamps();
            RefuseReplacedRows();
            var storedRows = new Dictionary<object, PropertyValues?>(ReferenceEqualityComparer.Instance);
            await GuardTenantWritesAsync(storedRows, synchronously, cancellationToken);
            await CheckCustomFieldsAsync(storedRows, synchronously, cancellationToken);
            await EnforceWorkflowFieldsAsync(storedRows, synchronously, cancellationToken);
        }
        finally
        {
            this.namedEditors?.Clear();
        }
    }

    private void RefuseReplacedRows()
    {
        var deleted = new HashSet<(Type, string)>();
        var added = new List<(EntityEntry Entry, (Type, string) Key)>();
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Deleted && !entry.Metadata.IsOwned()))
        {
            if (entry.Metadata.FindPrimaryKey() is not { } primaryKey)
            {
                continue;
            }

            var key = (entry.Metadata.GetRootType().ClrType, string.Join("\u001f", primaryKey.Properties.Select(property => entry.Property(property.Name).CurrentValue)));
            if (entry.State == EntityState.Deleted)
            {
                deleted.Add(key);
            }
            else
            {
                added.Add((entry, key));
            }
        }

        if (added.FirstOrDefault(insert => deleted.Contains(insert.Key)) is { Entry: not null } replaced)
        {
            throw Refuse(replaced.Entry, "A row is not deleted and inserted again with the same key in one save, which would rewrite its creation stamp and pass an update unchecked; update the tracked row instead.");
        }
    }

    private async Task GuardTenantWritesAsync(Dictionary<object, PropertyValues?> storedRows, bool synchronously, CancellationToken cancellationToken)
    {
        var systemScope = IsInSystemScope;

        var session = RuntimeContext?.Current;
        var runtimeTenant = session?.TenantId;
        var now = Clock.GetUtcNow().UtcDateTime;
        var pending = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        var tenantRows = pending.Where(entry => entry.Entity is ITenantEntity).Select(entry => entry.Context.Entry((ITenantEntity)entry.Entity)).ToList();
        var guarded = pending.Where(entry => entry.Entity is ITenantEntity or Tenant || IsAttributeDefinition(entry.Metadata.ClrType) || IsAttributeValue(entry.Metadata.ClrType)).ToList();

        if (guarded.Count > 0 && !systemScope && runtimeTenant is null)
        {
            throw Refuse(guarded[0], "There is no runtime tenant, so no tenant row may be written.");
        }

        if (!systemScope && pending.FirstOrDefault(entry => entry.Entity is Tenant) is { } tenantWrite)
        {
            throw Refuse(tenantWrite, "Tenants are written only in system scope.");
        }

        await CheckStaffingCompaniesAsync(pending, synchronously, cancellationToken);

        foreach (var audit in pending.Where(entry => entry.Entity is ImpersonationAudit))
        {
            if (!systemScope || audit.State == EntityState.Deleted)
            {
                throw Refuse(audit, audit.State == EntityState.Deleted
                    ? "The impersonation audit is never deleted."
                    : "The impersonation audit is written only in system scope.");
            }

            if (audit.State == EntityState.Modified)
            {
                var stored = synchronously ? audit.GetDatabaseValues() : await audit.GetDatabaseValuesAsync(cancellationToken);
                if (AuditUpdateRefusal(audit, stored) is { } reason)
                {
                    throw Refuse(audit, reason);
                }
            }
        }

        foreach (var error in pending.Where(entry => entry.Entity is ErrorLog))
        {
            if (!systemScope || error.State == EntityState.Modified)
            {
                throw Refuse(error, error.State == EntityState.Modified
                    ? "An error-log row is never changed."
                    : "The error log is written only in system scope.");
            }
        }

        var stamped = new List<(EntityEntry Entry, Guid? Tenant, Guid Actor)>();
        foreach (var entry in tenantRows)
        {
            var stored = ReadsStoredRow(entry.State, systemScope)
                ? synchronously ? entry.GetDatabaseValues() : await entry.GetDatabaseValuesAsync(cancellationToken)
                : null;
            if (ReadsStoredRow(entry.State, systemScope))
            {
                storedRows[entry.Entity] = stored;
            }

            CheckTenant(entry, runtimeTenant, systemScope, stored);
            if (Stamp(entry, session, systemScope, now) is { } actor)
            {
                stamped.Add((entry, entry.Entity.TenantId, actor));
            }
        }

        foreach (var entry in pending.Where(entry => IsAttributeDefinition(entry.Metadata.ClrType)))
        {
            var stored = ReadsStoredRow(entry.State, systemScope)
                ? synchronously ? entry.GetDatabaseValues() : await entry.GetDatabaseValuesAsync(cancellationToken)
                : null;
            storedRows[entry.Entity] = stored;

            CheckAttributeDefinition(entry, systemScope, stored);
            if (StampAttributeDefinition(entry, session, systemScope, now) is { } actor)
            {
                stamped.Add((entry, ((IEntityAttributeDefinition)entry.Entity).TenantId, actor));
            }
        }

        await CheckReferencesAsync(pending, runtimeTenant, systemScope, synchronously, cancellationToken);

        await RefuseWritesOfHeldTenantAsync(runtimeTenant, systemScope, synchronously, cancellationToken);

        StampNewTenants(session, now);

        if (systemScope && stamped.Count > 0)
        {
            var actorById = ChangeTracker.Entries<ApplicationUser>()
                .Where(entry => entry.State == EntityState.Added)
                .Select(entry => new StampedActor(entry.Entity.Id, entry.Entity.TenantId, entry.Entity.IsSystemAccount))
                .ToDictionary(user => user.Id);
            var storedActorIds = stamped.Select(write => write.Actor).Distinct().Where(id => !actorById.ContainsKey(id)).ToList();
            var actors = Users.Where(user => storedActorIds.Contains(user.Id)).Select(user => new StampedActor(user.Id, user.TenantId, user.IsSystemAccount));
            foreach (var actor in synchronously ? actors.ToList() : await actors.ToListAsync(cancellationToken))
            {
                actorById[actor.Id] = actor;
            }

            Guid? platformTenant = null;
            if (stamped.Any(write => write.Tenant is null))
            {
                var root = Tenants.Where(tenant => tenant.IsRootTenant).Select(tenant => (Guid?)tenant.Id);
                platformTenant = synchronously ? root.FirstOrDefault() : await root.FirstOrDefaultAsync(cancellationToken);
            }

            var foreign = stamped.FirstOrDefault(write => (write.Tenant ?? platformTenant) is not { } owner || !IsUserOfTenant(actorById.GetValueOrDefault(write.Actor), owner));
            if (foreign.Entry is not null)
            {
                throw Refuse(foreign.Entry, foreign.Tenant is null
                    ? "The user stamped as its creator or editor is not a user of the platform tenant."
                    : "The user stamped as its creator or editor is not a user of the row's tenant.", foreign.Tenant);
            }
        }
    }

    private async Task CheckStaffingCompaniesAsync(List<EntityEntry> pending, bool synchronously, CancellationToken cancellationToken)
    {
        const string link = nameof(Tenant.ParentTenantId);
        foreach (var entry in pending.Where(entry => entry.Entity is Tenant && entry.State is EntityState.Added or EntityState.Modified))
        {
            var tenant = (Tenant)entry.Entity;
            var property = entry.Property(link);
            if (entry.State == EntityState.Modified)
            {
                var changed = !Equals(property.OriginalValue, property.CurrentValue);
                if (!changed && property.IsModified)
                {
                    var stored = synchronously ? entry.GetDatabaseValues() : await entry.GetDatabaseValuesAsync(cancellationToken);
                    changed = stored is not null && !Equals(stored[link], property.CurrentValue);
                }

                if (changed)
                {
                    throw Refuse(entry, "A managed tenant names its staffing company as it is created and never another, nor none, afterwards.", tenant.Id);
                }

                continue;
            }

            if (tenant.ParentTenantId is not { } parentId)
            {
                continue;
            }

            if (tenant.IsRootTenant)
            {
                throw Refuse(entry, "The platform tenant has no staffing company.", tenant.Id);
            }

            var parent = ChangeTracker.Entries<Tenant>().FirstOrDefault(candidate => candidate.State == EntityState.Added && candidate.Entity.Id == parentId)?.Entity is { } added
                ? (Found: true, added.IsRootTenant, added.ParentTenantId)
                : await ReadStaffingCompanyAsync(parentId, synchronously, cancellationToken);
            var reason = parent switch
            {
                _ when parentId == tenant.Id => "A tenant is not its own staffing company.",
                { Found: false } => "The staffing company it names is no tenant.",
                { IsRootTenant: true } => "The platform tenant is no tenant's staffing company.",
                { ParentTenantId: not null } => "A managed tenant is no tenant's staffing company: the link is one level only.",
                _ => null,
            };
            if (reason is not null)
            {
                throw Refuse(entry, reason, tenant.Id);
            }
        }
    }

    private async Task<(bool Found, bool IsRootTenant, Guid? ParentTenantId)> ReadStaffingCompanyAsync(Guid parentId, bool synchronously, CancellationToken cancellationToken)
    {
        var query = Tenants.AsNoTracking().Where(tenant => tenant.Id == parentId).Select(tenant => new { tenant.IsRootTenant, tenant.ParentTenantId });
        var row = synchronously ? query.FirstOrDefault() : await query.FirstOrDefaultAsync(cancellationToken);
        return row is null ? (false, false, null) : (true, row.IsRootTenant, row.ParentTenantId);
    }

    private static string? AuditUpdateRefusal(EntityEntry entry, PropertyValues? stored)
    {
        var ended = nameof(ImpersonationAudit.Ended);
        if (stored is null)
        {
            return "The impersonation audit row does not exist.";
        }

        if (stored[ended] is not null)
        {
            return "A closed impersonation session is never changed.";
        }

        if (entry.Property(ended).CurrentValue is null)
        {
            return "An impersonation audit row is updated only to record its end.";
        }

        var rewritten = entry.Properties.FirstOrDefault(property => property.Metadata.Name != ended && !Equals(property.CurrentValue, stored[property.Metadata.Name]));
        return rewritten is null ? null : $"Only the end of an impersonation session is recorded after its start; {rewritten.Metadata.Name} never changes.";
    }

    private sealed record StampedActor(Guid Id, Guid TenantId, bool IsSystemAccount);

    private static bool IsUserOfTenant(StampedActor? actor, Guid tenantId) =>
        actor is not null
        && actor.TenantId == tenantId
        && (!actor.IsSystemAccount || actor.Id == SystemAccount.IdFor(tenantId));

    private static bool ReadsStoredRow(EntityState state, bool systemScope) =>
        state == EntityState.Modified || (state == EntityState.Deleted && !systemScope);

    private void CheckTenant(EntityEntry<ITenantEntity> entry, Guid? runtimeTenant, bool systemScope, PropertyValues? stored)
    {
        var row = entry.Entity;
        var storedTenant = stored?.GetValue<Guid>(nameof(ITenantEntity.TenantId));
        switch (entry.State)
        {
            case EntityState.Added:
                if (row.TenantId == Guid.Empty)
                {
                    row.TenantId = runtimeTenant ?? throw Refuse(entry, "The row names no tenant and the runtime has none.");
                }

                if (!systemScope && row.TenantId != runtimeTenant)
                {
                    throw Refuse(entry, "The row is for another tenant than the runtime tenant.");
                }

                break;

            case EntityState.Modified:
                if (!systemScope && storedTenant != runtimeTenant)
                {
                    throw Refuse(entry, "The database does not store the row for the runtime tenant.", storedTenant);
                }

                var tracked = entry.Property(nameof(ITenantEntity.TenantId));
                if (!Equals(tracked.OriginalValue, tracked.CurrentValue) || (storedTenant is { } before && before != row.TenantId))
                {
                    throw Refuse(entry, "TenantId cannot change.", storedTenant);
                }

                break;

            case EntityState.Deleted when !systemScope && storedTenant != runtimeTenant:
                throw Refuse(entry, "The database does not store the row for the runtime tenant.", storedTenant);
        }
    }

    private void CheckAttributeDefinition(EntityEntry entry, bool systemScope, PropertyValues? stored)
    {
        var tenant = entry.Property(nameof(IEntityAttributeDefinition.TenantId));
        var current = (Guid?)tenant.CurrentValue;
        var storedTenant = stored?.GetValue<Guid?>(nameof(IEntityAttributeDefinition.TenantId));

        if (!systemScope)
        {
            throw Refuse(entry, "A definition is written only in system scope.", current, storedTenant);
        }

        if (entry.State == EntityState.Modified && (!Equals(tenant.OriginalValue, current) || (stored is not null && storedTenant != current)))
        {
            throw Refuse(entry, "TenantId cannot change.", current, storedTenant);
        }
    }

    private Guid? Stamp(EntityEntry<ITenantEntity> entry, RuntimeSnapshot? session, bool systemScope, DateTime now)
    {
        var row = entry.Entity;
        var actor = session?.EffectiveUserId;
        switch (entry.State)
        {
            case EntityState.Added:
                if (!systemScope || row.Created == default)
                {
                    row.Created = now;
                }

                row.Modified = null;
                row.ModifiedById = null;

                if (systemScope && row.CreatedById != Guid.Empty)
                {
                    row.ImpersonatedById = null;
                }
                else
                {
                    row.CreatedById = actor ?? throw RefuseStamp(entry, "creator");
                    row.ImpersonatedById = session?.ImpersonatedById;
                }

                return row.CreatedById;

            case EntityState.Modified:
                RestoreInsertOnlyColumns(entry);
                row.Modified = now;

                var editor = entry.Property(nameof(ITenantEntity.ModifiedById));
                var named = this.namedEditors?.Contains(row) == true || !Equals(editor.OriginalValue, editor.CurrentValue);
                if (!systemScope || row.ModifiedById is null || !named)
                {
                    row.ModifiedById = actor ?? throw RefuseStamp(entry, "editor");
                }

                return row.ModifiedById;

            default:
                return null;
        }
    }

    private static void RestoreInsertOnlyColumns(EntityEntry entry)
    {
        string[] insertOnly = [nameof(ITenantEntity.Created), nameof(ITenantEntity.CreatedById), nameof(ITenantEntity.ImpersonatedById)];
        foreach (var column in insertOnly.Select(name => entry.Property(name)).Where(column => column.IsModified))
        {
            column.CurrentValue = column.OriginalValue;
            column.IsModified = false;
        }
    }

    private void StampNewTenants(RuntimeSnapshot? session, DateTime now)
    {
        foreach (var tenant in ChangeTracker.Entries<Tenant>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity))
        {
            if (tenant.Created == default)
            {
                tenant.Created = now;
            }

            if (tenant.CreatedById == Guid.Empty && session?.EffectiveUserId is { } creator)
            {
                tenant.CreatedById = creator;
            }
        }
    }

    internal TenantIsolationException RefuseBulkUpdate(string reason)
    {
        IsolationLog().LogWarning("Refused a bulk update: {Reason} Runtime tenant {RuntimeTenantId}.", reason, CurrentTenantId);
        return new TenantIsolationException($"Refused a bulk update: {reason}");
    }

    internal TenantIsolationException RefuseBulkAuditWrite(string statement, string reason)
    {
        IsolationLog().LogWarning("Refused a bulk {Statement} of the impersonation audit: {Reason} Runtime tenant {RuntimeTenantId}.", statement, reason, CurrentTenantId);
        return new TenantIsolationException($"Refused a bulk {statement} of {nameof(ImpersonationAudit)}: {reason}");
    }

    private TenantIsolationException Refuse(EntityEntry<ITenantEntity> entry, string reason, Guid? storedTenant = null) =>
        Refuse(entry, reason, entry.Entity.TenantId, storedTenant);

    private TenantIsolationException Refuse(EntityEntry entry, string reason, Guid? rowTenant = null, Guid? storedTenant = null)
    {
        var (operation, entity, key) = Describe(entry);
        IsolationLog().LogWarning(
            "Refused tenant {Operation} on {EntityType} {Key}: {Reason} Runtime tenant {RuntimeTenantId}, row tenant {RowTenantId}, stored tenant {StoredTenantId}.",
            operation, entity, key, reason, CurrentTenantId, rowTenant, storedTenant);

        return new TenantIsolationException($"Refused to {operation} {entity} {key}: {reason}");
    }

    private InvalidOperationException RefuseStamp(EntityEntry entry, string role)
    {
        var (operation, entity, key) = Describe(entry);
        IsolationLog().LogWarning("Refused {Operation} on {EntityType} {Key}: no user to stamp as its {Role}.", operation, entity, key, role);
        return new InvalidOperationException(
            $"Refused to {operation} {entity} {key}: the runtime has no user to stamp as its {role}. Outside a user's session, set it to a user of the row's tenant.");
    }

    private static (string Operation, string Entity, string Key) Describe(EntityEntry entry)
    {
        var operation = entry.State switch
        {
            EntityState.Added => "insert",
            EntityState.Deleted => "delete",
            _ => "update",
        };
        var key = string.Join(",", entry.Metadata.FindPrimaryKey()?.Properties.Select(property => entry.Property(property.Name).CurrentValue) ?? []);
        return (operation, entry.Metadata.ClrType.Name, key);
    }

    private static bool IsAttributeDefinition(Type type) => typeof(IEntityAttributeDefinition).IsAssignableFrom(type);

    private static bool IsAttributeValue(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(EntityAttributeBase<,>))
            {
                return true;
            }
        }

        return false;
    }
}
