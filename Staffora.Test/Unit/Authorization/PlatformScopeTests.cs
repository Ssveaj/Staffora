using Staffora.Core.Execution;
using Staffora.Services.Authorization;

namespace Staffora.Test.Unit.Authorization;

public class PlatformScopeTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void Platform_reach_needs_the_Admin_role_and_the_platform_tenant_while_not_impersonating(bool admin, bool platformTenant, bool impersonating, bool reach)
    {
        var runtime = new RuntimeExecutionContext();
        runtime.SetUser(Guid.NewGuid(), isSuperAdmin: admin);
        runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: platformTenant);
        if (impersonating)
        {
            runtime.StartImpersonation(Guid.NewGuid(), Guid.NewGuid());
        }

        Assert.Equal(reach, runtime.HasPlatformReach());
        Assert.Equal(admin && platformTenant, runtime.IsPlatformOwner());
    }

    [Fact]
    public void No_base_tenant_is_never_the_platform_tenant_and_a_reset_forgets_it()
    {
        var runtime = new RuntimeExecutionContext();
        runtime.SetUser(Guid.NewGuid(), isSuperAdmin: true);
        runtime.SetBaseTenant(null, isPlatformTenant: true);
        Assert.False(runtime.IsInPlatformTenant);

        runtime.SetBaseTenant(Guid.NewGuid(), isPlatformTenant: true);
        Assert.True(runtime.HasPlatformReach());

        runtime.Reset();
        Assert.False(runtime.IsInPlatformTenant);
        Assert.False(runtime.HasPlatformReach());
    }
}
