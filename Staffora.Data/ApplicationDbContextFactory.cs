using Microsoft.EntityFrameworkCore;
using Staffora.Core.Execution;

namespace Staffora.Data;

public sealed class ApplicationDbContextFactory : IApplicationDbContextFactory<ApplicationDbContext>
{
    private readonly IDbContextFactory<ApplicationDbContext> factory;
    private readonly TimeProvider clock;

    public ApplicationDbContextFactory(IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock)
    {
        this.factory = factory;
        this.clock = clock;
    }

    public ApplicationDbContext CreateDbContext(IRuntimeExecutionContext runtime) => Prepare(this.factory.CreateDbContext(), runtime);

    public async Task<ApplicationDbContext> CreateDbContextAsync(IRuntimeExecutionContext runtime) => Prepare(await this.factory.CreateDbContextAsync(), runtime);

    public ApplicationDbContext CreateSystemDbContext() => Prepare(this.factory.CreateDbContext(), runtime: null);

    private ApplicationDbContext Prepare(ApplicationDbContext context, IRuntimeExecutionContext? runtime)
    {
        context.RuntimeContext = runtime;
        context.Clock = this.clock;
        return context;
    }
}
