using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace Staffora.Core.Workflow.Conditions;

public sealed class RequiredFieldCondition<TState, TEntity> : Condition<TState, TEntity>
    where TState : struct, Enum
    where TEntity : class, IStateEntity<TState>
{
    private readonly PropertyInfo property;

    public RequiredFieldCondition(Expression<Func<TEntity, object?>> field, string? description = null)
        : this(MemberPaths.PropertyOf(field), description)
    {
    }

    private RequiredFieldCondition(PropertyInfo property, string? description)
        : base(description ?? $"{Label(property)} is required", MemberPaths.NavigationTarget(property) is null ? [] : [property.Name])
    {
        this.property = property;
    }


    protected override bool IsMet(WorkflowContext<TState, TEntity> context) => this.property.GetValue(context.Entity) switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        Guid id => id != Guid.Empty,
        IEnumerable items => items.Cast<object>().Any(),
        _ => true,
    };

    private static string Label(PropertyInfo property) =>
        property.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? property.Name;
}
