using System.Linq.Expressions;
using System.Reflection;

namespace Staffora.Data;

public sealed class DetachedEntityUpdaterOptions<T>
    where T : class
{
    private readonly List<string> collections = [];
    private readonly Dictionary<string, Func<object?>?> excluded = new(StringComparer.Ordinal);

    public static DetachedEntityUpdaterOptions<T> RootOnly => new();

    public IReadOnlyList<string> Collections => this.collections;

    public IReadOnlyDictionary<string, Func<object?>?> ExcludedColumns => this.excluded;

    public DetachedEntityUpdaterOptions<T> Excluding(Expression<Func<T, object?>> column, Func<object?>? renew = null)
    {
        ArgumentNullException.ThrowIfNull(column);
        var body = column.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : column.Body;
        if (body is not MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression })
        {
            throw new ArgumentException($"'{column}' must select a column property of {typeof(T).Name} directly.", nameof(column));
        }

        this.excluded[property.Name] = renew;
        return this;
    }

    public DetachedEntityUpdaterOptions<T> WithCollection<TChild>(Expression<Func<T, IEnumerable<TChild>?>> navigation)
        where TChild : class
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var body = navigation.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : navigation.Body;
        if (body is not MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression })
        {
            throw new ArgumentException($"'{navigation}' must select a collection property of {typeof(T).Name} directly.", nameof(navigation));
        }

        if (!this.collections.Contains(property.Name))
        {
            this.collections.Add(property.Name);
        }

        return this;
    }
}
