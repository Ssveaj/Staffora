using Staffora.E2ESetup;

namespace Staffora.Test.E2E;

[Trait("Category", "E2E")]
public sealed class FoundationStateFileTests : IDisposable
{
    private readonly string statePath = E2EDatabase.NewStatePath();

    private static readonly FoundationState state = new(
        new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero),
        "Staffora.E2E",
        [new FoundationPersona("platformOwner", "Platform owner", "platform.owner@example.com", "Secret-1")],
        new FoundationTenantUser("Company A", "Sam Taylor", "sam.taylor@company-a.example.com"),
        new FoundationTenantUser("Company A", "Alex Morgan", "alex.morgan@company-a.example.com"),
        new FoundationTenantUser("Company A", "Robin Lee", "robin.lee@company-a.example.com"),
        new FoundationErrorLog("timetable", [new FoundationErrorLogEntry("errorToday", "Error", "the timetable export failed", 0)]));

    public void Dispose() => E2EDatabase.Remove(this.statePath);

    [Fact]
    public async Task The_state_is_written_in_camelCase_and_reads_back()
    {
        await FoundationStateFile.WriteAtomicallyAsync(this.statePath, state, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(this.statePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"preparedAt\"", json);
        Assert.Contains("\"personas\"", json);
        Assert.Contains("\"password\": \"Secret-1\"", json);

        var read = FoundationStateFile.TryRead(this.statePath);
        Assert.NotNull(read);
        Assert.Equal(state.Personas, read.Personas);
        Assert.Equal("platform.owner@example.com", read.Persona("platformOwner").Email);
        Assert.Equal(state.ImpersonationTarget, read.ImpersonationTarget);
        Assert.Equal(state.TenantAdministrator, read.TenantAdministrator);
        Assert.Equal(state.TenantViewer, read.TenantViewer);
        Assert.Equal(state.ErrorLog.Search, read.ErrorLog.Search);
        Assert.Equal(state.ErrorLog.Entries, read.ErrorLog.Entries);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_write_that_fails_part_way_leaves_no_state_file()
    {
        var failing = new { personas = new FailingPersona() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => FoundationStateFile.WriteAtomicallyAsync(this.statePath, failing, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(this.statePath));
        Assert.Empty(Leftovers());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [] }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\" }] }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\", \"password\": \"Secret-1\" }] }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\", \"password\": \"Secret-1\" }], \"impersonationTarget\": { \"tenant\": \"Company A\", \"name\": \"Sam Taylor\" } }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\", \"password\": \"Secret-1\" }], \"impersonationTarget\": { \"tenant\": \"Company A\", \"name\": \"Sam Taylor\", \"email\": \"sam.taylor@company-a.example.com\" } }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\", \"password\": \"Secret-1\" }], \"impersonationTarget\": { \"tenant\": \"Company A\", \"name\": \"Sam Taylor\", \"email\": \"sam.taylor@company-a.example.com\" }, \"tenantAdministrator\": { \"tenant\": \"Company A\", \"name\": \"Alex Morgan\", \"email\": \"alex.morgan@company-a.example.com\" } }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\", \"password\": \"Secret-1\" }], \"impersonationTarget\": { \"tenant\": \"Company A\", \"name\": \"Sam Taylor\", \"email\": \"sam.taylor@company-a.example.com\" }, \"tenantAdministrator\": { \"tenant\": \"Company A\", \"name\": \"Alex Morgan\", \"email\": \"alex.morgan@company-a.example.com\" }, \"errorLog\": { \"search\": \"timetable\", \"entries\": [{ \"key\": \"errorToday\", \"level\": \"Error\" }] } }")]
    [InlineData("{ \"preparedAt\": \"2026-11-02T09:30:00Z\", \"database\": \"Staffora.E2E\", \"personas\": [{ \"key\": \"platformOwner\", \"actor\": \"Platform owner\", \"email\": \"platform.owner@example.com\", \"password\": \"Secret-1\" }], \"impersonationTarget\": { \"tenant\": \"Company A\", \"name\": \"Sam Taylor\", \"email\": \"sam.taylor@company-a.example.com\" }, \"tenantAdministrator\": { \"tenant\": \"Company A\", \"name\": \"Alex Morgan\", \"email\": \"alex.morgan@company-a.example.com\" }, \"errorLog\": { \"search\": \"timetable\", \"entries\": [{ \"key\": \"errorToday\", \"level\": \"Error\", \"message\": \"the timetable export failed\", \"daysAgo\": 0 }] } }")]
    [InlineData("{ \"personas\": [")]
    public async Task An_incomplete_state_file_reads_as_no_state(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.statePath)!);
        await File.WriteAllTextAsync(this.statePath, content, TestContext.Current.CancellationToken);

        Assert.Null(FoundationStateFile.TryRead(this.statePath));
    }

    [Fact]
    public async Task Removing_the_stale_state_also_removes_a_temporary_file_a_killed_write_left()
    {
        await FoundationStateFile.WriteAtomicallyAsync(this.statePath, state, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync($"{this.statePath}.0123456789abcdef.tmp", "{ \"personas\":", TestContext.Current.CancellationToken);

        FoundationStateFile.RemoveStale(this.statePath);

        Assert.False(File.Exists(this.statePath));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_run_that_fails_before_the_database_answers_removes_the_previous_state()
    {
        await FoundationStateFile.WriteAtomicallyAsync(this.statePath, state, TestContext.Current.CancellationToken);

        var result = await new FoundationPrep(TimeProvider.System, TextWriter.Null)
            .RunAsync("Server=tcp:127.0.0.1,1;Database=Staffora_unreachable_e2e;Connect Timeout=1", this.statePath, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(this.statePath));
    }

    [Fact]
    public void A_persona_formatted_into_a_message_shows_no_password()
    {
        Assert.DoesNotContain("Secret-1", state.Personas[0].ToString());
    }

    private IEnumerable<string> Leftovers()
    {
        var folder = Path.GetDirectoryName(this.statePath)!;
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.tmp") : [];
    }

    private sealed class FailingPersona
    {
        public string Email => "platform.owner@example.com";

        public string Password => throw new InvalidOperationException("The serializer failed part-way.");
    }
}
