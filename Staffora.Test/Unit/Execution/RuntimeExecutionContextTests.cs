using Staffora.Core.Execution;

namespace Staffora.Test.Unit.Execution;

public class RuntimeExecutionContextTests
{
    private static readonly Guid platformTenant = Guid.NewGuid();
    private static readonly Guid platformOwner = Guid.NewGuid();

    [Fact]
    public void A_signed_in_user_acts_in_the_base_tenant_as_itself_with_no_impersonator()
    {
        var runtime = new RuntimeExecutionContext(platformTenant, platformOwner);

        Assert.Equal(platformTenant, runtime.TenantId);
        Assert.Equal(platformTenant, runtime.BaseTenantId);
        Assert.Equal(platformOwner, runtime.EffectiveUserId);
        Assert.Null(runtime.ImpersonatedById);
        Assert.Null(runtime.ImpersonatedTenantId);
    }

    [Fact]
    public void While_impersonating_the_runtime_acts_in_the_targets_tenant_as_the_target_and_names_the_signed_in_user_as_impersonator()
    {
        var targetTenant = Guid.NewGuid();
        var target = Guid.NewGuid();
        var runtime = new RuntimeExecutionContext(platformTenant, platformOwner);

        runtime.StartImpersonation(target, targetTenant);

        Assert.Equal(targetTenant, runtime.TenantId);
        Assert.Equal(platformTenant, runtime.BaseTenantId);
        Assert.Equal(targetTenant, runtime.ImpersonatedTenantId);
        Assert.Equal(target, runtime.EffectiveUserId);
        Assert.Equal(platformOwner, runtime.UserId);
        Assert.Equal(platformOwner, runtime.ImpersonatedById);
    }

    [Fact]
    public void Impersonating_a_user_without_a_tenant_leaves_no_tenant_rather_than_the_base_tenant()
    {
        var runtime = new RuntimeExecutionContext(platformTenant, platformOwner);

        runtime.StartImpersonation(Guid.NewGuid(), tenantId: null);

        Assert.Null(runtime.TenantId);
        Assert.Equal(platformTenant, runtime.BaseTenantId);
    }

    [Fact]
    public void Ending_impersonation_returns_to_the_signed_in_user_and_the_base_tenant()
    {
        var runtime = new RuntimeExecutionContext(platformTenant, platformOwner);
        runtime.StartImpersonation(Guid.NewGuid(), Guid.NewGuid());

        runtime.EndImpersonation();

        Assert.False(runtime.IsImpersonating);
        Assert.Equal(platformTenant, runtime.TenantId);
        Assert.Equal(platformOwner, runtime.EffectiveUserId);
        Assert.Null(runtime.ImpersonatedById);
        Assert.Null(runtime.ImpersonatedTenantId);
    }
}
