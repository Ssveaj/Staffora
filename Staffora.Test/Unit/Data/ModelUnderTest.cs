using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Staffora.Core.Entities;
using Staffora.Data;

namespace Staffora.Test.Unit.Data;

internal static class ModelUnderTest
{
    public const string UnopenedConnection = @"Server=(localdb)\MSSQLLocalDB;Database=Staffora_ModelConventions_test;Trusted_Connection=True";

    public static IModel Model { get; } = Build();

    public static IEnumerable<IEntityType> TenantRows =>
        Model.GetEntityTypes().Where(entityType => typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType));

    private static IModel Build()
    {
        using var context = new ApplicationDbContext(ApplicationDbContextOptions.Create(UnopenedConnection));
        return context.GetService<IDesignTimeModel>().Model;
    }
}
