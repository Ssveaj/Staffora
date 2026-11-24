using Staffora.Data;

namespace Staffora.Services.Caching;

public interface ILookupRow
{
    Guid? TenantId { get; }
}

public sealed class LookupCatalogue<TRow>
    where TRow : class, ILookupRow
{
    public LookupCatalogue(string name, Func<ApplicationDbContext, Guid, CancellationToken, Task<List<TRow>>> load)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A catalogue needs a name.", nameof(name)) : name;
        Load = load ?? throw new ArgumentNullException(nameof(load));
    }

    public string Name { get; }

    internal Func<ApplicationDbContext, Guid, CancellationToken, Task<List<TRow>>> Load { get; }

    public override string ToString() => Name;
}
