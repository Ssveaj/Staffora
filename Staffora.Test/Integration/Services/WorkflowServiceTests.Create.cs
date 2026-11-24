using Microsoft.EntityFrameworkCore;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public partial class WorkflowServiceTests
{
    [Fact]
    public async Task A_creates_after_insert_work_runs_in_its_steps_transaction_on_the_inserted_row_and_goes_or_stays_with_the_step()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        await using var host = ServiceHost.Create(this.database, scenario.A.Runtime());
        var service = host.Get<WorkflowService>();

        var request = new TestParent { Name = "Created" };
        TestParent? seen = null;
        var created = await service.CreateAndTransitionAsync<TestParentState, TestParent>(request, "test-parent-save", afterInsert: async (context, row, ct) =>
        {
            seen = row;
            Assert.NotNull(context.Database.CurrentTransaction);
            context.Set<TestParent>().Add(new TestParent { Name = $"Companion of {row.Id}" });
            await context.SaveChangesAsync(ct);
        }, cancellationToken: cancellationToken);

        Assert.True(created.IsSuccess, created.Error);
        Assert.NotSame(request, seen);
        Assert.Equal(created.Value, seen!.Id);
        Assert.Equal(Guid.Empty, request.Id);
        Assert.Single(await HistoryOfAsync(created.Value, cancellationToken));
        Assert.True(await CompanionStoredAsync(created.Value, cancellationToken));

        Guid insertedId = Guid.Empty;
        var refused = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = "Refused" }, "test-parent-save", afterInsert: async (context, row, ct) =>
        {
            insertedId = row.Id;
            context.Set<TestParent>().Add(new TestParent { Name = $"Companion of {row.Id}" });
            await context.SaveChangesAsync(ct);
            throw new InvalidOperationException("secret: the after-insert work broke");
        }, cancellationToken: cancellationToken);

        Assert.False(refused.IsSuccess);
        Assert.DoesNotContain("secret", refused.Error, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, insertedId);
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: look for the rolled-back create");
        Assert.False(await system.TestParents.AnyAsync(row => row.Id == insertedId, cancellationToken));
        Assert.False(await CompanionStoredAsync(insertedId, cancellationToken));
        Assert.Empty(await HistoryOfAsync(insertedId, cancellationToken));

        var ran = false;
        var blank = await service.CreateAndTransitionAsync<TestParentState, TestParent>(new TestParent { Name = " " }, "test-parent-save", afterInsert: (_, _, _) => { ran = true; return Task.CompletedTask; }, cancellationToken: cancellationToken);
        Assert.False(blank.IsSuccess);
        Assert.False(ran);
    }

    private async Task<bool> CompanionStoredAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var system = this.database.CreateSystemContext();
        using var lease = system.BeginSystemScope("test: look for a create's companion row");
        var name = $"Companion of {parentId}";
        return await system.TestParents.AnyAsync(row => row.Name == name, cancellationToken);
    }
}
