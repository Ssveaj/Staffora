using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Staffora.Core;
using Staffora.Core.Entities;
using Staffora.Core.Workflow;
using Staffora.Data;

namespace Staffora.Services.Workflow;

public sealed partial class WorkflowService
{
    private async Task<(Transition<TState, TEntity>? Transition, string? Refusal)> RefuseCreateAsync<TState, TEntity>(ApplicationDbContext context, TEntity entity, Func<TEntity, Workflow<TState, TEntity>, (Transition<TState, TEntity>? Transition, string? Error)> pick, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        var workflow = WorkflowRegistry.Default.Get<TState, TEntity>();
        var (transition, error) = pick(entity, workflow);
        if (transition is null)
        {
            return (null, error);
        }

        if (!((Enum)entity.State).IsEntryState() || !EqualityComparer<TState>.Default.Equals(entity.State, transition.From))
        {
            return (transition, $"A new {typeof(TEntity).Name} starts in its entry state, and '{NameOf(transition)}' does not leave it.");
        }

        if (CreateOutsideATenantRefusal<TEntity>() is { } outsideATenant)
        {
            this.log.LogInformation("Refused creating a {EntityType} with {TransitionKey} for user {UserId}: {Reason}", typeof(TEntity).Name, transition.Key, this.runtime.EffectiveUserId, outsideATenant);
            return (transition, outsideATenant);
        }

        if (await AccessRefusalAsync(transition, cancellationToken) is { } refusal)
        {
            this.log.LogInformation("Refused creating a {EntityType} with {TransitionKey} for user {UserId}: {Reason}", typeof(TEntity).Name, transition.Key, this.runtime.EffectiveUserId, refusal);
            return (transition, refusal);
        }

        if (((IWorkflow)workflow).GetCascades(transition.Key).Any(cascade => !cascade.Navigation.Contains('.', StringComparison.Ordinal)))
        {
            return (transition, $"The step '{NameOf(transition)}' takes child records along, so it cannot create a {typeof(TEntity).Name}.");
        }

        if (CarriedStateEntityRefusal(context.Model, entity, typeof(TEntity).Name) is { } carried)
        {
            this.log.LogInformation("Refused creating a {EntityType} with {TransitionKey} for user {UserId}: {Reason}", typeof(TEntity).Name, transition.Key, this.runtime.EffectiveUserId, carried);
            return (transition, carried);
        }

        context.Add(entity);
        if (await ResolveReferencesAsync(context, entity, cancellationToken) is { } notFound)
        {
            return (transition, notFound);
        }

        if (await LoadConditionPathsAsync(context, entity, transition, cancellationToken) is { } pathNotFound)
        {
            return (transition, pathNotFound);
        }

        await PrepareNewAsync<TEntity>(context, entity, cancellationToken);

        var reasons = workflow.GetBlockingReasons(entity, transition.Key, EnvironmentFor(context, customFields: await CustomFieldsAsync(context, workflow, entity, cancellationToken)));
        return (transition, reasons.Count > 0 ? string.Join(" ", reasons) : null);
    }

    private string? CreateOutsideATenantRefusal<TEntity>()
    {
        var session = this.runtime.Current;
        return typeof(ITenantEntity).IsAssignableFrom(typeof(TEntity)) && session.IsInPlatformTenant && !session.IsImpersonating
            ? $"A {typeof(TEntity).Name} belongs to a tenant: the platform console creates one only while impersonating a user of that tenant."
            : null;
    }

    private static TEntity CopyForInsert<TEntity>(ApplicationDbContext context, TEntity source)
        where TEntity : class =>
        (TEntity)CopyRow(context.Model, source);

    private static object CopyRow(IModel model, object source)
    {
        var type = model.FindRuntimeEntityType(source.GetType())
            ?? throw new InvalidOperationException($"{source.GetType().Name} is not an entity type of the model, so it cannot be created.");
        var copy = Activator.CreateInstance(type.ClrType, nonPublic: true)!;
        foreach (var column in type.GetProperties().Where(column => column.PropertyInfo is not null))
        {
            column.PropertyInfo!.SetValue(copy, column.PropertyInfo.GetValue(source));
        }

        foreach (var navigation in type.GetNavigations().Where(navigation => navigation.PropertyInfo is not null))
        {
            var value = navigation.PropertyInfo!.GetValue(source);
            if (value is null)
            {
                continue;
            }

            if (navigation.IsCollection)
            {
                foreach (var child in ((System.Collections.IEnumerable)value).Cast<object>().ToList())
                {
                    navigation.GetCollectionAccessor()!.Add(copy, CopyRow(model, child), forMaterialization: false);
                }
            }
            else if (navigation.IsOnDependent)
            {
                var foreignKey = navigation.ForeignKey;
                for (var i = 0; i < foreignKey.Properties.Count; i++)
                {
                    if (foreignKey.Properties[i].PropertyInfo is { } column && IsUnset(column.GetValue(copy), column.PropertyType))
                    {
                        column.SetValue(copy, foreignKey.PrincipalKey.Properties[i].PropertyInfo?.GetValue(value));
                    }
                }
            }
            else
            {
                navigation.PropertyInfo.SetValue(copy, CopyRow(model, value));
            }
        }

        return copy;
    }

    private static bool IsUnset(object? value, Type type) =>
        value is null || (type.IsValueType && value.Equals(Activator.CreateInstance(type)));

    private static string? CarriedStateEntityRefusal(IModel model, object row, string rootName)
    {
        if (model.FindRuntimeEntityType(row.GetType()) is not { } type)
        {
            return null;
        }

        foreach (var navigation in type.GetNavigations().Where(navigation => navigation.PropertyInfo is not null && !navigation.IsOnDependent))
        {
            if (navigation.PropertyInfo!.GetValue(row) is not { } value)
            {
                continue;
            }

            IEnumerable<object> children = navigation.IsCollection ? ((System.Collections.IEnumerable)value).Cast<object>().ToList() : [value];
            foreach (var child in children)
            {
                if (child is IStateEntity)
                {
                    var childName = model.FindRuntimeEntityType(child.GetType())?.ClrType.Name ?? child.GetType().Name;
                    return $"A new {rootName} is created on its own: each {childName} is added afterwards by its own step, so nothing was created.";
                }

                if (CarriedStateEntityRefusal(model, child, rootName) is { } deeper)
                {
                    return deeper;
                }
            }
        }

        return null;
    }

    private static async Task<string?> ResolveReferencesAsync(ApplicationDbContext context, object entity, CancellationToken cancellationToken)
    {
        foreach (var reference in context.Entry(entity).References.ToList())
        {
            if (await ResolveReferenceAsync(context, reference, cancellationToken) is { } notFound)
            {
                return notFound;
            }
        }

        return null;
    }

    private static async Task<string?> ResolveReferenceAsync(ApplicationDbContext context, ReferenceEntry reference, CancellationToken cancellationToken)
    {
        if (reference.Metadata is not INavigation { IsOnDependent: true } navigation)
        {
            reference.IsLoaded = reference.CurrentValue is not null;
            return null;
        }

        var keys = navigation.ForeignKey.Properties.Select(property => reference.EntityEntry.Property(property.Name).CurrentValue).ToArray();
        if (keys.Any(key => key is null || (key is Guid id && id == Guid.Empty)))
        {
            reference.IsLoaded = true;
            return null;
        }

        var principal = await context.FindAsync(navigation.TargetEntityType.ClrType, keys, cancellationToken);
        if (principal is null)
        {
            return $"The {navigation.TargetEntityType.ClrType.Name} was not found.";
        }

        reference.CurrentValue = principal;
        reference.IsLoaded = true;
        return null;
    }

    private static async Task<string?> LoadConditionPathsAsync<TState, TEntity>(ApplicationDbContext context, TEntity entity, Transition<TState, TEntity> transition, CancellationToken cancellationToken)
        where TState : struct, Enum
        where TEntity : class, IStateEntity<TState>
    {
        var cascades = ((IWorkflow)WorkflowRegistry.Default.Get<TState, TEntity>()).GetCascades(transition.Key).Select(cascade => cascade.Navigation);
        foreach (var path in transition.Conditions.SelectMany(condition => condition.Reads).Concat(cascades).Where(path => path.Contains('.', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            List<object> current = [entity];
            foreach (var segment in path.Split('.'))
            {
                var next = new List<object>();
                foreach (var row in current)
                {
                    var entry = context.Entry(row);
                    if (entry.References.FirstOrDefault(reference => reference.Metadata.Name == segment) is { } reference)
                    {
                        if (!reference.IsLoaded)
                        {
                            if (entry.State != EntityState.Added)
                            {
                                await reference.LoadAsync(cancellationToken);
                            }
                            else if (!ReferenceEquals(row, entity) && await ResolveReferenceAsync(context, reference, cancellationToken) is { } notFound)
                            {
                                return notFound;
                            }
                        }

                        if (reference.CurrentValue is { } value)
                        {
                            next.Add(value);
                        }
                    }
                    else if (entry.State == EntityState.Added && entry.Collections.FirstOrDefault(collection => collection.Metadata.Name == segment) is { CurrentValue: { } rows })
                    {
                        next.AddRange(rows.Cast<object>());
                    }
                    else if (entry.State != EntityState.Added && entry.Collections.FirstOrDefault(collection => collection.Metadata.Name == segment) is { } stored)
                    {
                        if (!stored.IsLoaded)
                        {
                            await stored.LoadAsync(cancellationToken);
                        }

                        next.AddRange((stored.CurrentValue ?? Array.Empty<object>()).Cast<object>());
                    }
                }

                current = next;
            }
        }

        return null;
    }

    private sealed class NewRecord<TEntity>
        where TEntity : class
    {
        public NewRecord(TEntity source)
        {
            Source = source;
        }

        public TEntity Source { get; }

        public Func<ApplicationDbContext, TEntity, CancellationToken, Task>? AfterInsert { get; init; }

        public Guid Id { get; set; }
    }
}
