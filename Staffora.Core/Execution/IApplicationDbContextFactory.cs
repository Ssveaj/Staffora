using Microsoft.EntityFrameworkCore;

namespace Staffora.Core.Execution;

public interface IApplicationDbContextFactory<T> where T : DbContext
{
    Task<T> CreateDbContextAsync(IRuntimeExecutionContext runtime);

    T CreateDbContext(IRuntimeExecutionContext runtime);

    T CreateSystemDbContext();
}
