using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Test.Integration.Infrastructure;

public sealed class RealDatabase : IAsyncLifetime
{
    private DatabaseLease? lease;
    private readonly DatabasePool pool;

    public RealDatabase(DatabasePool pool)
    {
        this.pool = pool;
    }

    public string Name => Lease.Name;

    public string ConnectionString => Lease.ConnectionString;

    private DatabaseLease Lease => this.lease ?? throw new InvalidOperationException("The database is leased when the class fixture starts.");

    public async ValueTask InitializeAsync() => this.lease = await this.pool.LeaseAsync(TestContext.Current.CancellationToken);

    public async Task<RealDatabase> CreateIsolatedAsync(string purpose, CancellationToken cancellationToken)
    {
        var isolated = new RealDatabase(this.pool);
        isolated.lease = await this.pool.LeaseIsolatedAsync(purpose, cancellationToken);
        return isolated;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.lease is not null)
        {
            await this.lease.DisposeAsync();
            this.lease = null;
        }
    }

    public TestDataContext CreateContext(IRuntimeExecutionContext? runtime, TimeProvider? clock = null) =>
        new(ApplicationDbContextOptions.Create(ConnectionString, throwOnUnorderedRowLimit: true)) { RuntimeContext = runtime, Clock = clock ?? TimeProvider.System };

    public TestDataContext CreateSystemContext()
    {
        var context = new TestDataContext(ApplicationDbContextOptions.Create(ConnectionString, throwOnUnorderedRowLimit: true));
        context.BeginSystemScope("test arrangement across tenants");
        return context;
    }
}
