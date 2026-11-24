using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Workflow;

namespace Staffora.Test.Integration.Infrastructure;

public static class TestDeleteCleanups
{
    public static IServiceCollection AddTestDeleteCleanups(this IServiceCollection services) =>
        services.AddSingleton<IDeleteCleanup, TestParentLinesCleanup>();
}

public sealed class TestParentLinesCleanup : IDeleteCleanup
{
    public Type EntityType => typeof(TestParent);

    public IReadOnlyList<Type> Dependents => [typeof(TestParentLine)];

    public async Task BeforeRemoveAsync(ApplicationDbContext context, IStateEntity entity, CancellationToken cancellationToken)
    {
        var lines = await context.Set<TestParentLine>().Where(line => line.ParentId == entity.Id).ToListAsync(cancellationToken);
        context.RemoveRange(lines);
    }
}
