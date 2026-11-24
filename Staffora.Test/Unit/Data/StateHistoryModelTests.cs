using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Workflow;

namespace Staffora.Test.Unit.Data;

public class StateHistoryModelTests
{
    [Fact]
    public void A_history_entry_is_a_filtered_tenant_row_with_no_foreign_key_but_the_audit_columns()
    {
        var entry = ModelUnderTest.Model.FindEntityType(typeof(StateHistoryEntry))!;

        Assert.NotEmpty(entry.GetDeclaredQueryFilters());
        Assert.Equal(
            [nameof(ITenantEntity.CreatedById), nameof(ITenantEntity.ImpersonatedById), nameof(ITenantEntity.ModifiedById)],
            entry.GetForeignKeys().Select(foreignKey => foreignKey.Properties.Single().Name).Order());
        Assert.All(entry.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.NoAction, foreignKey.DeleteBehavior));
    }

    [Fact]
    public void An_entitys_history_is_indexed_within_its_tenant()
    {
        var entry = ModelUnderTest.Model.FindEntityType(typeof(StateHistoryEntry))!;

        Assert.Contains(entry.GetIndexes(), index => index.Properties.Select(property => property.Name).SequenceEqual(
            [nameof(ITenantEntity.TenantId), nameof(StateHistoryEntry.EntityType), nameof(StateHistoryEntry.EntityId), nameof(ITenantEntity.Created)]));
        Assert.Contains(entry.GetIndexes(), index => index.Properties.Select(property => property.Name).SequenceEqual(
            [nameof(ITenantEntity.TenantId), nameof(StateHistoryEntry.CorrelationId)]));
    }
}
