using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Services.Caching;

public sealed class TenantLookupCache : IDisposable
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    public const long EntryLimit = 4096;

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly ILogger<TenantLookupCache> logger;
    private readonly MemoryCache entries;

    private readonly ConcurrentDictionary<(object Catalogue, Guid TenantId), long> tenantGenerations = new();
    private readonly ConcurrentDictionary<object, long> catalogueGenerations = new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<EntryKey, Lazy<Task<object>>> loading = new();

    public TenantLookupCache(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock, ILogger<TenantLookupCache> logger)
        : this(dbFactory, clock, logger, EntryLimit)
    {
    }

    internal TenantLookupCache(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider clock, ILogger<TenantLookupCache> logger, long entryLimit)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.dbFactory = dbFactory;
        this.logger = logger;

        this.entries = new MemoryCache(new MemoryCacheOptions { SizeLimit = entryLimit, Clock = new ClockOf(clock) });
    }

    internal int EntryCount => this.entries.Count;

    public async Task<IReadOnlyList<TRow>> GetAsync<TRow>(LookupCatalogue<TRow> catalogue, Guid tenantId, CancellationToken cancellationToken = default)
        where TRow : class, ILookupRow
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A lookup is always one tenant's.", nameof(tenantId));

        var key = KeyOf(catalogue, tenantId);
        if (this.entries.TryGetValue(key, out var cached))
        {
            return (IReadOnlyList<TRow>)cached!;
        }

        var load = this.loading.GetOrAdd(key, entry => new Lazy<Task<object>>(() => LoadAsync(catalogue, entry)));

        return (IReadOnlyList<TRow>)await load.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate<TRow>(LookupCatalogue<TRow> catalogue, Guid tenantId)
        where TRow : class, ILookupRow
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        var previous = KeyOf(catalogue, tenantId);
        this.tenantGenerations.AddOrUpdate((catalogue, tenantId), 1, (_, generation) => generation + 1);

        this.entries.Remove(previous);
    }

    public void InvalidateEveryTenant<TRow>(LookupCatalogue<TRow> catalogue)
        where TRow : class, ILookupRow
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        this.catalogueGenerations.AddOrUpdate(catalogue, 1, (_, generation) => generation + 1);
    }

    public void Dispose() => this.entries.Dispose();

    private EntryKey KeyOf(object catalogue, Guid tenantId) => new(
        catalogue,
        tenantId,
        this.catalogueGenerations.TryGetValue(catalogue, out var catalogueGeneration) ? catalogueGeneration : 0,
        this.tenantGenerations.TryGetValue((catalogue, tenantId), out var tenantGeneration) ? tenantGeneration : 0);

    private async Task<object> LoadAsync<TRow>(LookupCatalogue<TRow> catalogue, EntryKey key)
        where TRow : class, ILookupRow
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (this.entries.TryGetValue(key, out var cached))
            {
                return cached!;
            }

            var rows = await ReadAsync(catalogue, key.TenantId).ConfigureAwait(false);
            if (rows.FirstOrDefault(row => row.TenantId is { } owner && owner != key.TenantId) is not null)
            {
                this.logger.LogError("The lookup of {Catalogue} for tenant {TenantId} read a row of another tenant; nothing was cached.", catalogue.Name, key.TenantId);
                throw new InvalidOperationException($"The lookup of {catalogue.Name} read a row of another tenant; nothing was cached.");
            }

            var snapshot = new ReadOnlyCollection<TRow>([.. rows]);
            if (KeyOf(catalogue, key.TenantId) == key)
            {
                this.entries.Set(key, (object)snapshot, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeToLive });
            }

            return snapshot;
        }
        finally
        {
            this.loading.TryRemove(key, out _);
        }
    }

    private async Task<List<TRow>> ReadAsync<TRow>(LookupCatalogue<TRow> catalogue, Guid tenantId)
        where TRow : class, ILookupRow
    {
        await using var context = this.dbFactory.CreateSystemDbContext();
        using var lease = context.BeginSystemScope($"Lookup cache: read the {catalogue.Name} of one tenant");
        return await catalogue.Load(context, tenantId, CancellationToken.None).ConfigureAwait(false);
    }

    private readonly record struct EntryKey(object Catalogue, Guid TenantId, long CatalogueGeneration, long TenantGeneration);

    private sealed class ClockOf : ISystemClock
    {
        private readonly TimeProvider clock;

        public ClockOf(TimeProvider clock)
        {
            this.clock = clock;
        }

        public DateTimeOffset UtcNow => this.clock.GetUtcNow();
    }
}
