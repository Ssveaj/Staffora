using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data;

public partial class ApplicationDbContext
{
    private static readonly MethodInfo storedTenantsQuery =
        typeof(ApplicationDbContext).GetMethod(nameof(StoredTenantsQuery), BindingFlags.NonPublic | BindingFlags.Static)!;

    private async Task CheckReferencesAsync(IReadOnlyList<EntityEntry> pending, Guid? runtimeTenant, bool systemScope, bool synchronously, CancellationToken cancellationToken)
    {
        var tenants = new StoredTenants(pending);
        var references = new List<(EntityEntry Row, Guid? RowTenant, string Column, IEntityType Target, Guid Key)>();
        var values = new List<(EntityEntry Value, IEntityType OwnerType, Guid Owner, IEntityType DefinitionType, Guid Definition)>();

        foreach (var entry in pending.Where(entry => entry.State is EntityState.Added or EntityState.Modified && entry.Entity is ITenantEntity))
        {
            var rowTenant = ((ITenantEntity)entry.Entity).TenantId;
            foreach (var foreignKey in entry.Metadata.GetForeignKeys().Where(key => HasTenant(key.PrincipalEntityType) && !IsAuditColumn(key)))
            {
                var column = entry.Property(SingleColumn(foreignKey).Name);
                if ((entry.State == EntityState.Added || column.IsModified) && column.CurrentValue is Guid key)
                {
                    references.Add((entry, rowTenant, column.Metadata.Name, foreignKey.PrincipalEntityType, key));
                    tenants.Want(foreignKey.PrincipalEntityType, key);
                }
            }
        }

        foreach (var entry in pending.Where(entry => IsAttributeValue(entry.Metadata.ClrType) && !(systemScope && entry.State == EntityState.Deleted)))
        {
            var owner = entry.Metadata.FindNavigation(AttributeOwner)!.ForeignKey;
            var definition = entry.Metadata.FindNavigation(nameof(EntityAttributeBase<,>.Definition))!.ForeignKey;
            var ownerKey = (Guid)entry.Property(SingleColumn(owner).Name).CurrentValue!;
            var definitionKey = (Guid)entry.Property(SingleColumn(definition).Name).CurrentValue!;
            values.Add((entry, owner.PrincipalEntityType, ownerKey, definition.PrincipalEntityType, definitionKey));
            tenants.Want(owner.PrincipalEntityType, ownerKey);
            tenants.Want(definition.PrincipalEntityType, definitionKey);
        }

        await tenants.LoadAsync(this, synchronously, cancellationToken);

        foreach (var (row, rowTenant, column, target, key) in references)
        {
            if (!tenants.Fits(target, key, rowTenant))
            {
                throw Refuse(row, $"Its {column} refers to a row the database does not store for the row's tenant.", rowTenant);
            }
        }

        foreach (var (value, ownerType, owner, definitionType, definition) in values)
        {
            if (!tenants.TryGet(ownerType, owner, out var ownerTenant) || (!systemScope && (ownerTenant is null || ownerTenant != runtimeTenant)))
            {
                throw Refuse(value, "The row it describes is not stored for the runtime tenant.", ownerTenant);
            }

            if (value.State != EntityState.Deleted && !tenants.Fits(definitionType, definition, ownerTenant))
            {
                throw Refuse(value, "It uses a definition the database does not store for its owner's tenant.", ownerTenant);
            }
        }
    }

    private static bool HasTenant(IReadOnlyEntityType type) =>
        type.ClrType != typeof(Tenant) && type.FindProperty(nameof(ITenantEntity.TenantId)) is not null;

    private static bool IsAuditColumn(IReadOnlyForeignKey foreignKey) =>
        foreignKey.Properties is [{ Name: nameof(ITenantEntity.CreatedById) or nameof(ITenantEntity.ModifiedById) or nameof(ITenantEntity.ImpersonatedById) }];

    private static IReadOnlyProperty SingleColumn(IReadOnlyForeignKey foreignKey) =>
        foreignKey.Properties is [{ } column] && (Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType) == typeof(Guid)
            && foreignKey.PrincipalKey.Properties is [{ } principalKey] && principalKey.ClrType == typeof(Guid)
            ? column
            : throw new NotSupportedException($"The tenant checks compare single Guid keys only; {foreignKey} is not one.");

    private static IQueryable<StoredTenant> StoredTenantsQuery<TRow>(ApplicationDbContext context, string keyColumn, bool tenantIsNullable, List<Guid> keys)
        where TRow : class
    {
        var rows = context.Set<TRow>().IgnoreQueryFilters().AsNoTracking().Where(row => keys.Contains(EF.Property<Guid>(row, keyColumn)));
        return tenantIsNullable
            ? rows.Select(row => new StoredTenant(EF.Property<Guid>(row, keyColumn), EF.Property<Guid?>(row, nameof(ITenantEntity.TenantId))))
            : rows.Select(row => new StoredTenant(EF.Property<Guid>(row, keyColumn), EF.Property<Guid>(row, nameof(ITenantEntity.TenantId))));
    }

    private sealed record StoredTenant(Guid Key, Guid? TenantId);

    private sealed class StoredTenants
    {
        private readonly Dictionary<(Type Table, Guid Key), Guid?> known = [];
        private readonly Dictionary<Type, (IEntityType Table, HashSet<Guid> Keys)> wanted = [];

        public StoredTenants(IEnumerable<EntityEntry> pending)
        {
            foreach (var entry in pending.Where(entry => entry.State == EntityState.Added && HasTenant(entry.Metadata)))
            {
                var table = entry.Metadata.GetRootType();
                if (table.FindPrimaryKey()?.Properties is [{ } key] && entry.Property(key.Name).CurrentValue is Guid id)
                {
                    this.known[(table.ClrType, id)] = (Guid?)entry.Property(nameof(ITenantEntity.TenantId)).CurrentValue;
                }
            }
        }

        public void Want(IEntityType type, Guid key)
        {
            var table = type.GetRootType();
            if (this.known.ContainsKey((table.ClrType, key)))
            {
                return;
            }

            if (!this.wanted.TryGetValue(table.ClrType, out var wanted))
            {
                this.wanted[table.ClrType] = wanted = (table, []);
            }

            wanted.Keys.Add(key);
        }

        public async Task LoadAsync(ApplicationDbContext context, bool synchronously, CancellationToken cancellationToken)
        {
            foreach (var (table, keys) in this.wanted.Values)
            {
                var keyColumn = table.FindPrimaryKey()!.Properties.Single().Name;
                var tenantIsNullable = table.FindProperty(nameof(ITenantEntity.TenantId))!.ClrType != typeof(Guid);
                var query = (IQueryable<StoredTenant>)storedTenantsQuery.MakeGenericMethod(table.ClrType).Invoke(null, [context, keyColumn, tenantIsNullable, keys.ToList()])!;
                var stored = synchronously ? query.ToList() : await query.ToListAsync(cancellationToken);
                foreach (var row in stored)
                {
                    this.known[(table.ClrType, row.Key)] = row.TenantId;
                }
            }

            this.wanted.Clear();
        }

        public bool TryGet(IEntityType type, Guid key, out Guid? tenant) => this.known.TryGetValue((type.GetRootType().ClrType, key), out tenant);

        public bool Fits(IEntityType type, Guid key, Guid? tenant) =>
            TryGet(type, key, out var stored)
            && (stored is null ? IsAttributeDefinition(type.ClrType) : stored == tenant);
    }
}
