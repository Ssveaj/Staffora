using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Data;

public sealed record TenantPurgeStep(IEntityType EntityType, IProperty? TenantProperty, IForeignKey? Owner, IReadOnlyList<IForeignKey> SelfReferences, IReadOnlyList<IForeignKey> OutsideReferences)
{
    public Type ClrType => EntityType.ClrType;
}

internal sealed record TenantPurgeLease(DbTransaction Transaction, Guid TenantId);

public static class TenantPurge
{
    public const string Tag = "Staffora retention: tenant purge";

    public const string NotPendingDeletionReason = "only a tenant scheduled for deletion is purged, never the platform tenant, and only inside the transaction that holds its row.";

    public static IReadOnlyDictionary<Type, string> OutsideTheTenant { get; } = new Dictionary<Type, string>
    {
        [typeof(Tenant)] = "the tenant's own row, kept Archived as the record that the tenant existed",
        [typeof(ErrorLog)] = "the platform's error log: its TenantId only labels a row, which leaves by its age",
        [typeof(ImpersonationAudit)] = "the platform's impersonation audit is append-only and never deleted, so a user it names stays",
        [typeof(ApplicationRole)] = "Identity's application roles (the platform owner's Admin role) belong to no tenant",
        [typeof(IdentityRoleClaim<Guid>)] = "the claims of Identity's application roles, which belong to no tenant",
    };

    private static readonly ConditionalWeakTable<IModel, IReadOnlyList<TenantPurgeStep>> plans = new();

    private static readonly MethodInfo efProperty = typeof(EF).GetMethod(nameof(EF.Property))!;

    private static readonly MethodInfo setOf = typeof(DbContext).GetMethods().Single(method => method.Name == nameof(DbContext.Set) && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);

    private static readonly MethodInfo anyOf = typeof(Queryable).GetMethods().Single(method => method.Name == nameof(Queryable.Any) && method.GetParameters().Length == 2);

    private static readonly MethodInfo orderBy = typeof(Queryable).GetMethods().Single(method => method.Name == nameof(Queryable.OrderBy) && method.GetParameters().Length == 2);

    private static readonly MethodInfo deleteBatchOf = typeof(TenantPurge).GetMethod(nameof(DeleteBatchOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo countOf = typeof(TenantPurge).GetMethod(nameof(CountOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static IReadOnlyList<TenantPurgeStep> PlanFor(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return plans.GetValue(model, BuildPlan);
    }

    public static async Task<IDisposable> BeginAsync(ApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsInSystemScope || context.Database.CurrentTransaction?.GetDbTransaction() is not { } transaction)
        {
            throw context.RefuseTenantPurge(tenantId, "a tenant is purged in system scope, inside a transaction begun on the context.");
        }

        if (context.PurgeLease is not null)
        {
            throw context.RefuseTenantPurge(tenantId, "a context purges one tenant at a time.");
        }

        var state = await context.HoldTenantAsync(tenantId, cancellationToken);
        var isPlatformTenant = await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId && tenant.IsRootTenant, cancellationToken);
        if (state != TenantState.PendingDeletion || isPlatformTenant)
        {
            throw context.RefuseTenantPurge(tenantId, NotPendingDeletionReason);
        }

        context.PurgeLease = new TenantPurgeLease(transaction, tenantId);
        context.RecordTenantPurge("opened", tenantId);
        return new Release(context, context.PurgeLease);
    }

    public static async Task<int> DeleteBatchAsync(ApplicationDbContext context, TenantPurgeStep step, Guid tenantId, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        RequireLease(context, tenantId);
        return await (Task<int>)deleteBatchOf.MakeGenericMethod(step.ClrType).Invoke(null, [context, step, tenantId, batchSize, cancellationToken])!;
    }

    public static async Task<int> CountAsync(ApplicationDbContext context, TenantPurgeStep step, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(step);
        return await (Task<int>)countOf.MakeGenericMethod(step.ClrType).Invoke(null, [context, step, tenantId, cancellationToken])!;
    }

    public static Task<int> DeleteMessagesToBatchAsync(ApplicationDbContext context, Guid tenantId, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        RequireLease(context, tenantId);
        var key = new TenantKey(tenantId);
        return context.IntegrationMessages.TagWith(Tag).Where(message => message.TargetTenantId == key.TenantId).OrderBy(message => message.Id).Take(batchSize).ExecuteDeleteAsync(cancellationToken);
    }

    internal static bool IsTagged(Expression statement)
    {
        var finder = new TagFinder();
        finder.Visit(statement);
        return finder.Found;
    }

    internal static void JudgeStatement(ApplicationDbContext context, DbCommand command)
    {
        if (!command.CommandText.Contains(Tag, StringComparison.Ordinal))
        {
            return;
        }

        if (context.PurgeLease is not { } lease)
        {
            throw context.RefuseTenantPurge(null, "a statement tagged as a tenant purge runs only while the purge of its tenant is open.");
        }

        if (!ReferenceEquals(command.Transaction, lease.Transaction))
        {
            throw context.RefuseTenantPurge(lease.TenantId, "a tenant purge's statement runs in the transaction that holds the tenant's row.");
        }

        if (!command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid named && named == lease.TenantId))
        {
            throw context.RefuseTenantPurge(lease.TenantId, "a tenant purge's statement names the tenant being purged.");
        }
    }

    private static void RequireLease(ApplicationDbContext context, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PurgeLease is not { } lease || lease.TenantId != tenantId || !ReferenceEquals(context.Database.CurrentTransaction?.GetDbTransaction(), lease.Transaction))
        {
            throw context.RefuseTenantPurge(tenantId, NotPendingDeletionReason);
        }
    }

    private static Task<int> DeleteBatchOfAsync<T>(ApplicationDbContext context, TenantPurgeStep step, Guid tenantId, int batchSize, CancellationToken cancellationToken)
        where T : class =>
        ByKey(RowsOf<T>(context, step, tenantId, removableOnly: true), step.EntityType).Take(batchSize).ExecuteDeleteAsync(cancellationToken);

    private static Task<int> CountOfAsync<T>(ApplicationDbContext context, TenantPurgeStep step, Guid tenantId, CancellationToken cancellationToken)
        where T : class =>
        RowsOf<T>(context, step, tenantId, removableOnly: false).CountAsync(cancellationToken);

    private static IQueryable<T> RowsOf<T>(ApplicationDbContext context, TenantPurgeStep step, Guid tenantId, bool removableOnly)
        where T : class
    {
        var key = Expression.Property(Expression.Constant(new TenantKey(tenantId)), nameof(TenantKey.TenantId));
        var row = Expression.Parameter(typeof(T), "row");
        var condition = step.TenantProperty is { } tenant ? EqualIds(Read(row, tenant), key) : OwnedBy(context, row, step.Owner!, key);
        if (removableOnly)
        {
            foreach (var reference in step.SelfReferences)
            {
                condition = Expression.AndAlso(condition, Expression.Not(ReferencedBy(context, row, reference, bySibling: true)));
            }

            foreach (var reference in step.OutsideReferences)
            {
                condition = Expression.AndAlso(condition, Expression.Not(ReferencedBy(context, row, reference, bySibling: false)));
            }
        }

        return context.Set<T>().TagWith(Tag).Where(Expression.Lambda<Func<T, bool>>(condition, row));
    }

    private static IQueryable<T> ByKey<T>(IQueryable<T> rows, IEntityType entityType)
    {
        var row = Expression.Parameter(typeof(T), "row");
        var key = Read(row, entityType.FindPrimaryKey()!.Properties[0]);
        return (IQueryable<T>)orderBy.MakeGenericMethod(typeof(T), key.Type).Invoke(null, [rows, Expression.Lambda(key, row)])!;
    }

    private static Expression OwnedBy(ApplicationDbContext context, ParameterExpression row, IForeignKey owner, Expression key)
    {
        var principal = owner.PrincipalEntityType.GetRootType();
        var candidate = Expression.Parameter(principal.ClrType, "owner");
        var match = Expression.AndAlso(
            EqualIds(Read(candidate, owner.PrincipalKey.Properties[0]), Read(row, owner.Properties[0])),
            EqualIds(Read(candidate, TenantPropertyOf(principal)!), key));
        return Any(context, principal.ClrType, Expression.Lambda(match, candidate));
    }

    private static Expression ReferencedBy(ApplicationDbContext context, ParameterExpression row, IForeignKey reference, bool bySibling)
    {
        var other = Expression.Parameter(reference.DeclaringEntityType.ClrType, "other");
        var principalKey = reference.PrincipalKey.Properties[0];
        Expression match = EqualIds(Read(other, reference.Properties[0]), Read(row, principalKey));
        if (bySibling)
        {
            match = Expression.AndAlso(match, Expression.Not(EqualIds(Read(other, principalKey), Read(row, principalKey))));
        }

        return Any(context, other.Type, Expression.Lambda(match, other));
    }

    private static Expression Any(ApplicationDbContext context, Type entity, LambdaExpression predicate)
    {
        var source = ((IQueryable)setOf.MakeGenericMethod(entity).Invoke(context, null)!).Expression;
        return Expression.Call(anyOf.MakeGenericMethod(entity), source, Expression.Quote(predicate));
    }

    private static Expression Read(Expression row, IProperty property) =>
        Expression.Call(efProperty.MakeGenericMethod(property.ClrType), row, Expression.Constant(property.Name));

    private static Expression EqualIds(Expression left, Expression right) => Expression.Equal(AsNullable(left), AsNullable(right));

    private static Expression AsNullable(Expression value) => value.Type == typeof(Guid) ? Expression.Convert(value, typeof(Guid?)) : value;

    private static IProperty? TenantPropertyOf(IEntityType type) => type.FindProperty(nameof(Staffora.Core.Entities.ITenantEntity.TenantId));

    private static IEnumerable<IForeignKey> ForeignKeysOf(IEntityType type) => type.GetDerivedTypesInclusive().SelectMany(derived => derived.GetDeclaredForeignKeys());

    private static IReadOnlyList<TenantPurgeStep> BuildPlan(IModel model)
    {
        var roots = model.GetEntityTypes().Where(type => type.BaseType is null && !type.IsOwned()).ToList();
        var outside = roots.Where(type => OutsideTheTenant.ContainsKey(type.ClrType)).ToHashSet();
        var tenantRows = roots.Where(type => !outside.Contains(type) && TenantPropertyOf(type) is not null).ToDictionary(type => type, type => TenantPropertyOf(type)!);
        var owners = new Dictionary<IEntityType, IForeignKey>();
        foreach (var type in roots.Where(type => !outside.Contains(type) && !tenantRows.ContainsKey(type)))
        {
            owners[type] = ForeignKeysOf(type)
                .Where(key => key.IsRequired && key.Properties.Count == 1 && tenantRows.TryGetValue(key.PrincipalEntityType.GetRootType(), out var tenant) && !tenant.IsNullable)
                .OrderBy(key => key.Properties[0].Name, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"{type.DisplayName()} is neither a tenant row, nor a row a tenant row owns, nor listed outside the tenant, so a tenant purge cannot tell whose it is.");
        }

        var steps = tenantRows.Keys.Concat(owners.Keys).ToHashSet();
        var dependents = steps.ToDictionary(type => type, _ => new HashSet<IEntityType>());
        var selfReferences = steps.ToDictionary(type => type, _ => new List<IForeignKey>());
        foreach (var type in steps)
        {
            foreach (var key in ForeignKeysOf(type))
            {
                var principal = key.PrincipalEntityType.GetRootType();
                if (principal == type)
                {
                    selfReferences[type].Add(SingleColumn(key));
                }
                else if (steps.Contains(principal))
                {
                    dependents[principal].Add(type);
                }
            }
        }

        var outsideReferences = steps.ToDictionary(
            type => type,
            type => model.GetEntityTypes().SelectMany(entity => entity.GetDeclaredForeignKeys())
                .Where(key => key.PrincipalEntityType.GetRootType() == type && !steps.Contains(key.DeclaringEntityType.GetRootType()))
                .Select(SingleColumn)
                .ToList());

        var ordered = new List<TenantPurgeStep>();
        var remaining = new HashSet<IEntityType>(steps);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(type => !dependents[type].Any(remaining.Contains)).OrderBy(type => type.Name, StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
            {
                throw new InvalidOperationException($"The foreign keys between {string.Join(", ", remaining.Select(type => type.DisplayName()).Order(StringComparer.Ordinal))} form a cycle, so no order removes them one table at a time.");
            }

            foreach (var type in ready)
            {
                remaining.Remove(type);
                ordered.Add(new TenantPurgeStep(type, tenantRows.GetValueOrDefault(type), owners.GetValueOrDefault(type), selfReferences[type], outsideReferences[type]));
            }
        }

        return ordered;
    }

    private static IForeignKey SingleColumn(IForeignKey key) =>
        key.Properties.Count == 1
            ? key
            : throw new InvalidOperationException($"{key.DeclaringEntityType.DisplayName()} refers to {key.PrincipalEntityType.DisplayName()} through several columns, which a tenant purge does not follow.");

    private sealed record TenantKey(Guid TenantId);

    private sealed class TagFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == nameof(EntityFrameworkQueryableExtensions.TagWith) && node.Arguments is [_, ConstantExpression { Value: Tag }])
            {
                Found = true;
            }

            return base.VisitMethodCall(node);
        }
    }

    private sealed class Release : IDisposable
    {
        private readonly ApplicationDbContext context;
        private readonly TenantPurgeLease lease;

        public Release(ApplicationDbContext context, TenantPurgeLease lease)
        {
            this.context = context;
            this.lease = lease;
        }

        public void Dispose()
        {
            if (ReferenceEquals(this.context.PurgeLease, this.lease))
            {
                this.context.PurgeLease = null;
                this.context.RecordTenantPurge("closed", this.lease.TenantId);
            }
        }
    }
}
