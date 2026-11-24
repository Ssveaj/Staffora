using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;

namespace Staffora.Core.Workflow;

internal static class MemberPaths
{
    public static PropertyInfo PropertyOf<TEntity, TValue>(Expression<Func<TEntity, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? convert.Operand
            : selector.Body;

        if (body is MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression })
        {
            return property;
        }

        throw new ArgumentException($"'{selector}' must select a property of {typeof(TEntity).Name} directly.", nameof(selector));
    }

    public static IReadOnlyList<PropertyInfo> PathOf<TEntity, TValue>(Expression<Func<TEntity, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? convert.Operand
            : selector.Body;

        var path = new List<PropertyInfo>();
        while (body is MemberExpression { Member: PropertyInfo property } member)
        {
            path.Insert(0, property);
            body = member.Expression!;
        }

        return body is ParameterExpression && path.Count > 0
            ? path
            : throw new ArgumentException($"'{selector}' must select a property of {typeof(TEntity).Name}, or a chain of them.", nameof(selector));
    }

    public static bool IsNavigationPath(Type type, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            var target = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance) is { } property
                ? NavigationTarget(property)
                : null;
            if (target is null)
            {
                return false;
            }

            type = target;
        }

        return true;
    }

    public static Type? NavigationTarget(PropertyInfo property)
    {
        var target = ElementType(property.PropertyType) ?? property.PropertyType;
        return target.IsClass && target != typeof(string) && target.GetCustomAttribute<ComplexTypeAttribute>() is null
            ? target
            : null;
    }

    public static IEnumerable<object> Values(object? navigationValue) => navigationValue switch
    {
        null => [],
        string => [navigationValue],
        IEnumerable items => items.Cast<object>(),
        _ => [navigationValue],
    };

    public static Type? ElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerable?.GetGenericArguments()[0];
    }
}
