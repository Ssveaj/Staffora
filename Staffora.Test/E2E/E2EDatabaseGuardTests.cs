using Staffora.E2ESetup;

namespace Staffora.Test.E2E;

[Trait("Category", "E2E")]
public class E2EDatabaseGuardTests
{
    [Theory]
    [InlineData("Staffora.E2E")]
    [InlineData("staffora_e2e")]
    [InlineData("StafforaE2ELocal")]
    [InlineData("e2e")]
    public void A_name_containing_e2e_is_an_end_to_end_database(string name)
    {
        Assert.True(E2EDatabaseGuard.IsE2EDatabase(name));
        Assert.Equal(name, E2EDatabaseGuard.EnsureE2EDatabase(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stafforadb")]
    [InlineData("Staffora_Integration_test")]
    [InlineData("Staffora-E-2-E")]
    public void A_name_without_e2e_is_refused(string? name)
    {
        Assert.False(E2EDatabaseGuard.IsE2EDatabase(name));
        var refusal = Assert.Throws<InvalidOperationException>(() => E2EDatabaseGuard.EnsureE2EDatabase(name));
        Assert.Contains("must contain \"e2e\"", refusal.Message);
    }

    [Theory]
    [InlineData("Staffora_e2e_prod")]
    [InlineData("Production.E2E")]
    [InlineData("Staffora.E2E.Stage")]
    [InlineData("e2e_staging")]
    public void A_name_marking_a_shared_environment_is_refused_even_with_e2e(string name)
    {
        Assert.False(E2EDatabaseGuard.IsE2EDatabase(name));
        var refusal = Assert.Throws<InvalidOperationException>(() => E2EDatabaseGuard.EnsureE2EDatabase(name));
        Assert.Contains("marks a shared environment", refusal.Message);
    }

    [Fact]
    public void A_missing_connection_string_is_refused()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => E2EDatabaseGuard.EnsureE2EConnection(null));

        Assert.Contains("STAFFORA_E2E_CONNECTION", refusal.Message);
    }

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=C:\data\stafforadb.mdf;Database=Staffora_e2e")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=C:\data\Staffora_e2e.mdf")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=Staffora.E2E;User Instance=true")]
    public void A_connection_string_that_attaches_a_file_or_a_user_instance_is_refused_whatever_its_name(string connection)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => E2EDatabaseGuard.EnsureE2EConnection(connection));

        Assert.Contains("AttachDbFilename or User Instance", refusal.Message);
    }

    [Fact]
    public void A_connection_string_naming_an_end_to_end_database_on_a_server_is_allowed()
    {
        const string connection = @"Server=(localdb)\MSSQLLocalDB;Database=Staffora.E2E;Trusted_Connection=True";

        Assert.Equal(connection, E2EDatabaseGuard.EnsureE2EConnection(connection));
    }

    [Fact]
    public async Task The_reset_refuses_a_database_without_e2e_before_it_connects()
    {
        const string connection = "Server=tcp:127.0.0.1,1;Database=Staffora_Integration_test;Connect Timeout=1";

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReset.ResetAsync(connection, TestContext.Current.CancellationToken));

        Assert.Contains("must contain \"e2e\"", refusal.Message);
    }

    [Fact]
    public async Task Prep_refuses_a_database_without_e2e_and_leaves_no_state_file()
    {
        var statePath = E2EDatabase.NewStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        await File.WriteAllTextAsync(statePath, "{ \"left\": \"by an earlier run\" }", TestContext.Current.CancellationToken);
        try
        {
            var result = await new FoundationPrep(TimeProvider.System, TextWriter.Null)
                .RunAsync("Server=tcp:127.0.0.1,1;Database=stafforadb;Connect Timeout=1", statePath, TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Contains("must contain \"e2e\"", result.Error);
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            E2EDatabase.Remove(statePath);
        }
    }
}
