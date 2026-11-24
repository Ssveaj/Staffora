using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Staffora.Core.Entities.Identity;

namespace Staffora.Test.Unit.Data;

public class SystemAccountIdTests
{
    private static readonly Guid tenant = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void The_id_is_the_name_based_UUID_of_the_tenant_id_under_the_fixed_namespace()
    {
        Assert.Equal(new Guid("044a9efe-2e4f-5ce0-8177-e6d225e2b927"), SystemAccount.IdFor(tenant));
        Assert.Equal(new Guid("6f1c2a9e-4b7d-4e35-8a60-2d9b7c41e8f3"), SystemAccount.IdNamespace);
    }

    [Fact]
    public void The_id_is_stable_and_differs_per_tenant()
    {
        var tenants = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToList();

        Assert.Equal(tenants.Select(SystemAccount.IdFor), tenants.Select(SystemAccount.IdFor));
        Assert.Equal(tenants.Count, tenants.Select(SystemAccount.IdFor).Distinct().Count());
        Assert.DoesNotContain(tenants, tenant => SystemAccount.IdFor(tenant) == tenant);
    }

    [Fact]
    public void The_id_carries_version_5_while_ids_EF_and_Guid_NewGuid_generate_carry_version_4()
    {
        var sequential = new SequentialGuidValueGenerator();
        var ordinary = Enumerable.Range(0, 100).Select(_ => sequential.Next(null!)).Concat(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()));

        Assert.All(Enumerable.Range(0, 100).Select(_ => SystemAccount.IdFor(Guid.NewGuid())), id => Assert.Equal(5, id.Version));
        Assert.All(ordinary, id => Assert.Equal(4, id.Version));
    }

    [Fact]
    public void The_user_name_holds_a_character_Identity_refuses_in_an_ordinary_user_name()
    {
        var userName = SystemAccount.UserNameFor(tenant);

        Assert.Equal("system:11111111222233334444555555555555", userName);
        Assert.Contains(userName, character => !new IdentityOptions().User.AllowedUserNameCharacters.Contains(character));
    }
}
