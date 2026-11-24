using Staffora.Core.Execution;
using Staffora.Services.Authorization;
using Staffora.Test.Web;

namespace Staffora.Test.Unit.Authorization;

public class PermissionSnapshotTests
{
    [Fact]
    public async Task The_permissions_of_a_snapshot_are_that_snapshots_even_after_the_runtime_changed()
    {
        var runtime = new RuntimeExecutionContext();
        runtime.SetUser(Guid.NewGuid());
        var snapshot = runtime.Current;
        var permissions = new PermissionService(new UnreachableDbContextFactory("The permission lookup"), runtime, TimeProvider.System);

        runtime.SetBaseTenant(Guid.NewGuid());

        Assert.Empty(await permissions.GetUserPermissionsAsync(snapshot, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => permissions.GetUserPermissionsAsync(TestContext.Current.CancellationToken));
    }
}
