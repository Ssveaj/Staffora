using Microsoft.EntityFrameworkCore;
using Staffora.Core.Workflow;

namespace Staffora.Data.Configurations;

public static class StateEntityConfiguration
{
    public static ModelBuilder ConfigureStateEntities(this ModelBuilder builder)
    {
        var roots = builder.Model.GetEntityTypes()
            .Where(entityType => entityType.BaseType is null && typeof(IStateEntity).IsAssignableFrom(entityType.ClrType))
            .ToList();

        foreach (var entityType in roots)
        {
            var entity = builder.Entity(entityType.ClrType);
            entity.Property(nameof(IStateEntity<>.State)).IsConcurrencyToken();
            entity.Property(nameof(IStateEntity.IsInTransition)).IsConcurrencyToken();
        }

        return builder;
    }
}
