using Staffora.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Unit.Testing;

public class DatabaseNameGuardTests
{
    [Theory]
    [InlineData("stafforadb")]
    [InlineData("Staffora")]
    [InlineData("master")]
    [InlineData("latest")]
    [InlineData("Staffora_attestation")]
    [InlineData("contest_db")]
    [InlineData("Staffora_tests")]
    [InlineData("testdb")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void The_guard_refuses_a_name_without_test_as_a_word_of_its_own(string? name)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => DatabaseNameGuard.EnsureTestDatabase(name));

        Assert.Contains("must contain \"test\" as a word of its own", refused.Message);
        Assert.False(DatabaseNameGuard.IsTestDatabase(name));
    }

    [Theory]
    [InlineData("Staffora_prod_test")]
    [InlineData("Staffora_Production_test")]
    [InlineData("PRODUCTION_TEST")]
    [InlineData("Staffora_stage_test")]
    [InlineData("Staffora_Staging_test")]
    [InlineData("test_STAGING")]
    public void The_guard_refuses_a_production_or_staging_name_even_when_it_contains_test(string name)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => DatabaseNameGuard.EnsureTestDatabase(name));

        Assert.Contains("marks a shared environment", refused.Message);
        Assert.False(DatabaseNameGuard.IsTestDatabase(name));
        Assert.Throws<InvalidOperationException>(() => DatabaseNameGuard.EnsureTestConnection($@"Server=(localdb)\MSSQLLocalDB;Database={name};Trusted_Connection=True"));
    }

    [Theory]
    [InlineData("Staffora_Integration_test")]
    [InlineData("Staffora_Integration_test_template")]
    [InlineData("TEST")]
    [InlineData("Staffora-test.db")]
    [InlineData("StafforaTestDb")]
    [InlineData("StafforaTESTDb")]
    [InlineData("Staffora_Integration_test_12")]
    public void The_guard_accepts_a_name_with_test_as_a_word_in_any_case(string name)
    {
        Assert.Equal(name, DatabaseNameGuard.EnsureTestDatabase(name));
    }

    [Fact]
    public void The_guard_refuses_the_development_database_of_the_app()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseNameGuard.EnsureTestConnection(DesignTimeApplicationDbContextFactory.LocalDevelopmentConnection));
    }

    [Fact]
    public void The_pools_default_connection_and_every_name_it_derives_pass_the_guard()
    {
        DatabaseNameGuard.EnsureTestConnection(DatabasePool.DefaultConnection);

        Assert.True(DatabaseNameGuard.IsTestDatabase(DatabasePool.TemplateName("Staffora_Integration_test")));
        Assert.True(DatabaseNameGuard.IsTestDatabase(DatabasePool.SlotName("Staffora_Integration_test", 12)));
        Assert.Throws<InvalidOperationException>(() => DatabasePool.SlotName("Staffora", 1));
    }
}
