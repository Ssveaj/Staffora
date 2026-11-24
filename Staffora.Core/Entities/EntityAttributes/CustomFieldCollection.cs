using System.Collections.Concurrent;
using System.Reflection;

namespace Staffora.Core.Entities.EntityAttributes;

public sealed record CustomFieldCollection(PropertyInfo Navigation, Type ValueType, Type DefinitionType)
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<CustomFieldCollection>> found = new();

    public static IReadOnlyList<CustomFieldCollection> Of(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return found.GetOrAdd(entityType, static type => [.. type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (Property: property, Element: ElementOf(property.PropertyType)))
            .Where(candidate => candidate.Element is not null)
            .Select(candidate => (candidate.Property, Value: candidate.Element!, Definition: DefinitionOf(candidate.Element!, type)))
            .Where(candidate => candidate.Definition is not null)
            .Select(candidate => new CustomFieldCollection(candidate.Property, candidate.Value, candidate.Definition!))]);
    }

    public static Type? DefinitionTypeOf(Type valueType)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        for (var current = valueType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(EntityAttributeBase<,>))
            {
                return current.GetGenericArguments()[1];
            }
        }

        return null;
    }

    private static Type? ElementOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static Type? DefinitionOf(Type valueType, Type entityType)
    {
        for (var current = valueType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(EntityAttributeBase<,>))
            {
                var arguments = current.GetGenericArguments();
                return arguments[0] == entityType ? arguments[1] : null;
            }
        }

        return null;
    }
}
