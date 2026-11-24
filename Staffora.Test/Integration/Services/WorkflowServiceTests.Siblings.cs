using Microsoft.EntityFrameworkCore;
using Staffora.Services.Workflow;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

public partial class WorkflowServiceTests
{
    [Fact]
    public async Task A_cascade_over_the_entitys_siblings_leaves_the_entity_itself_out_of_the_plan_the_gate_judges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTenantScenario.CreateAsync(this.database, cancellationToken);
        var holder = await TwoTenantScenario.AddUserAsync(this.database, scenario.A, "Filer", cancellationToken);

        await TwoTenantScenario.GrantAsync(this.database, scenario.A, holder, cancellationToken, TestFolder.DeletePermission);
        var (_, documents) = await AddFolderAsync(scenario.A, cancellationToken, TestDocumentState.Draft, TestDocumentState.Sealed);
        var (filed, sibling) = (documents[0], documents[1]);

        await using var host = ServiceHost.Create(this.database, scenario.A.RuntimeOf(holder));
        var service = host.Get<WorkflowService>();
        var offered = Assert.Single(
            (await service.GetAvailableTransitionsAsync<TestDocumentState, TestDocument>(filed, cancellationToken: cancellationToken)).Value!,
            answer => answer.Transition.Key == "test-document-file");
        Assert.True(offered.CanRun, offered.AccessRefusal ?? string.Join(" ", offered.BlockingReasons));

        var result = await service.TransitionEntityAsync<TestDocumentState, TestDocument>(filed, "test-document-file", cancellationToken: cancellationToken);

        Assert.True(result.IsSuccess, result.Error);
        await using var system = this.database.CreateSystemContext();
        var stored = await system.TestDocuments.AsNoTracking().Where(row => documents.Contains(row.Id)).ToDictionaryAsync(row => row.Id, row => (row.State, row.IsInTransition), cancellationToken);
        Assert.Equal((TestDocumentState.Filed, false), stored[filed]);
        Assert.Equal((TestDocumentState.Archived, false), stored[sibling]);
        Assert.Equal(["test-document-file"], (await HistoryOfAsync(filed, cancellationToken)).Select(entry => entry.TransitionKey));
        Assert.Equal(["test-document-archive-sealed"], (await HistoryOfAsync(sibling, cancellationToken)).Select(entry => entry.TransitionKey));
    }
}
