using Staffora.Core.Execution;
using Staffora.Data;

namespace Staffora.Test.Web;

internal sealed class UnreachableDbContextFactory : IApplicationDbContextFactory<ApplicationDbContext>
{
    private readonly string reachedBy;

    public UnreachableDbContextFactory(string reachedBy)
    {
        this.reachedBy = reachedBy;
    }

    public Task<ApplicationDbContext> CreateDbContextAsync(IRuntimeExecutionContext runtime) => throw Unreachable();

    public ApplicationDbContext CreateDbContext(IRuntimeExecutionContext runtime) => throw Unreachable();

    public ApplicationDbContext CreateSystemDbContext() => throw Unreachable();

    private InvalidOperationException Unreachable() => new($"{this.reachedBy} reached the database.");
}
