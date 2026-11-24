using System.Linq.Expressions;

namespace Staffora.Core.Lists;

public sealed class ListOrder<T>
{
    private IOrderedQueryable<T>? ordered;
    private readonly IQueryable<T> source;

    public ListOrder(IQueryable<T> source)
    {
        this.source = source;
    }

    public ListOrder<T> By<TKey>(Expression<Func<T, TKey>> key, bool descending = false)
    {
        this.ordered = (this.ordered, descending) switch
        {
            (null, false) => this.source.OrderBy(key),
            (null, true) => this.source.OrderByDescending(key),
            (_, false) => this.ordered.ThenBy(key),
            (_, true) => this.ordered.ThenByDescending(key),
        };
        return this;
    }

    public IQueryable<T> Query => this.ordered ?? this.source;
}
