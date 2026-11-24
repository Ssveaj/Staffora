using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Staffora.Data;

public static class WorkflowGraphPaths
{
    public static IReadOnlySet<string> Of<TEntity>(DbContext context, Func<IQueryable<TEntity>, IQueryable<TEntity>>? graph)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return graph is null ? new HashSet<string>(StringComparer.Ordinal) : Of(graph(context.Set<TEntity>()).Expression);
    }

    public static IReadOnlySet<string> Of(Expression query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var calls = new List<MethodCallExpression>();
        for (var current = query; current is MethodCallExpression call && call.Arguments.Count > 0; current = call.Arguments[0])
        {
            calls.Add(call);
        }

        calls.Reverse();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? last = null;
        foreach (var call in calls.Where(call => call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)))
        {
            var path = call.Method.Name switch
            {
                nameof(EntityFrameworkQueryableExtensions.Include) when call.Arguments[1] is ConstantExpression { Value: string named } => named,
                nameof(EntityFrameworkQueryableExtensions.Include) => PathOf(call.Arguments[1]),
                nameof(EntityFrameworkQueryableExtensions.ThenInclude) when last is not null && PathOf(call.Arguments[1]) is { } next => $"{last}.{next}",
                _ => null,
            };

            if (path is null)
            {
                continue;
            }

            last = path;
            var segments = path.Split('.');
            for (var length = 1; length <= segments.Length; length++)
            {
                paths.Add(string.Join('.', segments.Take(length)));
            }
        }

        return paths;
    }

    private static string? PathOf(Expression argument)
    {
        var body = (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : argument) is LambdaExpression lambda ? lambda.Body : null;
        var members = new List<string>();
        while (body is not null and not ParameterExpression)
        {
            switch (body)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } cast:
                    body = cast.Operand;
                    break;
                case MemberExpression member:
                    members.Add(member.Member.Name);
                    body = member.Expression;
                    break;
                case MethodCallExpression { Arguments.Count: > 0 } filter when filter.Method.IsStatic:
                    body = filter.Arguments[0];
                    break;
                default:
                    return null;
            }
        }

        members.Reverse();
        return members.Count == 0 ? null : string.Join('.', members);
    }
}
