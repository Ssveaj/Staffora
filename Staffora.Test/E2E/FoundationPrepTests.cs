using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.E2ESetup;
using Staffora.Services.Authorization;

namespace Staffora.Test.E2E;

[Trait("Category", "E2E")]
[Collection(E2EDatabaseCollection.Name)]
public sealed class FoundationPrepTests : IDisposable
{
    private readonly string statePath = E2EDatabase.NewStatePath();

    public void Dispose() => E2EDatabase.Remove(this.statePath);

    [Fact]
    public async Task Prep_writes_a_platform_owner_persona_whose_sign_in_and_permissions_are_verified()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);

        var written = FoundationStateFile.TryRead(this.statePath);
        Assert.NotNull(written);
        Assert.Equal(state.Personas, written.Personas);
        var owner = written.Persona(Personas.PlatformOwner.Key);
        Assert.Equal(Personas.PlatformOwner.Email, owner.Email);
        Assert.False(string.IsNullOrWhiteSpace(owner.Password));

        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await PersonaVerifier.VerifyAsync(services, Personas.PlatformOwner, owner.Password, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_persona_with_a_wrong_password_fails_verification()
    {
        await E2EDatabase.PrepareAsync(this.statePath);

        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        var refusal = await Assert.ThrowsAsync<PersonaVerificationException>(() => PersonaVerifier.VerifyAsync(services, Personas.PlatformOwner, "Not-the-issued-password-1", TestContext.Current.CancellationToken));

        Assert.Contains("platformOwner", refusal.Message);
    }

    [Fact]
    public async Task A_second_run_issues_a_new_password_and_the_old_one_stops_working()
    {
        var first = (await E2EDatabase.PrepareAsync(this.statePath)).Persona(Personas.PlatformOwner.Key);
        var second = (await E2EDatabase.PrepareAsync(this.statePath)).Persona(Personas.PlatformOwner.Key);

        Assert.NotEqual(first.Password, second.Password);
        Assert.Equal(second.Password, FoundationStateFile.TryRead(this.statePath)!.Persona(Personas.PlatformOwner.Key).Password);

        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await Assert.ThrowsAsync<PersonaVerificationException>(() => PersonaVerifier.VerifyAsync(services, Personas.PlatformOwner, first.Password, TestContext.Current.CancellationToken));
        await PersonaVerifier.VerifyAsync(services, Personas.PlatformOwner, second.Password, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_second_run_leaves_one_platform_tenant_and_one_owner()
    {
        await E2EDatabase.PrepareAsync(this.statePath);
        await E2EDatabase.PrepareAsync(this.statePath);

        Assert.Equal(1, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants WHERE IsRootTenant = 1"));
        Assert.Equal(3, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants WHERE ParentTenantId IS NOT NULL"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers u JOIN Tenants t ON t.Id = u.TenantId WHERE u.IsSystemAccount = 0 AND t.IsRootTenant = 1"));
        Assert.Equal(9 + CoordinationShowcaseFixture.StaffMembers.Count, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM AspNetUsers WHERE IsSystemAccount = 0"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Suppliers"));
        Assert.Equal(2 + CoordinationShowcaseFixture.StaffMembers.Count, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Resources"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Qualifications"));
        Assert.Equal(1 + CoordinationShowcaseFixture.Customers.Count, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Customers"));
        Assert.Equal(CoordinationShowcaseFixture.JobCount, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Jobs"));
    }

    [Fact]
    public async Task Prep_creates_only_the_platform_owner_and_onboards_the_journeys_tenant_through_the_services()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        Assert.Equal([Personas.PlatformOwner.Key, Personas.Coordinator.Key, Personas.Staff.Key, Personas.SecondStaff.Key, Personas.CustomerAdmin.Key, Personas.Orderer.Key], state.Personas.Select(persona => persona.Key));

        var tenant = $"(SELECT Id FROM Tenants WHERE Name = '{JourneyTenantFixture.TenantName}')";
        var owner = $"(SELECT Id FROM AspNetUsers WHERE NormalizedEmail = '{Personas.PlatformOwner.Email.ToUpperInvariant()}')";
        Assert.Equal(1, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM Tenants WHERE Id = {tenant} AND State = {(int)TenantState.Active}"));

        Assert.Equal(1, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM StateHistoryEntries WHERE EntityId = {tenant} AND TransitionKey = 'tenant-save-setup' AND ActorId = {owner}"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM StateHistoryEntries WHERE EntityId = {tenant} AND TransitionKey = 'tenant-activate' AND ActorId = {owner}"));

        foreach (var rows in new[]
        {
            $"SELECT Id FROM AspNetUsers WHERE TenantId = {tenant} AND IsSystemAccount = 0",
            $"SELECT Id FROM TenantRoles WHERE TenantId = {tenant}",
            $"SELECT Id FROM OrganizationUnits WHERE TenantId = {tenant} AND ParentId IS NOT NULL",
            $"SELECT Id FROM Resources WHERE TenantId = {tenant}",
            $"SELECT Id FROM Qualifications WHERE TenantId = {tenant}",
            $"SELECT Id FROM Customers WHERE TenantId = {tenant}",
        })
        {
            Assert.True(await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM ({rows}) r") > 0, rows);
            Assert.Equal(0, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM ({rows}) r WHERE NOT EXISTS (SELECT 1 FROM StateHistoryEntries h WHERE h.EntityId = r.Id)"));
        }
    }

    [Fact]
    public async Task Prep_writes_impersonation_targets_the_platform_owner_may_impersonate()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        Assert.Equal(state.ImpersonationTarget, FoundationStateFile.TryRead(this.statePath)?.ImpersonationTarget);
        Assert.Equal(state.TenantAdministrator, FoundationStateFile.TryRead(this.statePath)?.TenantAdministrator);
        Assert.Equal(state.TenantViewer, FoundationStateFile.TryRead(this.statePath)?.TenantViewer);

        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await users.FindByEmailAsync(Personas.PlatformOwner.Email);
        var listed = await scope.ServiceProvider.GetRequiredService<ImpersonationAuthorizer>().ListEligibleTargetsAsync(owner!.Id, TestContext.Current.CancellationToken);

        Assert.True(listed.IsSuccess, listed.Error);
        Assert.Equal(
            new[] { state.TenantAdministrator.Email, state.TenantViewer.Email, state.ImpersonationTarget.Email, Personas.Coordinator.Email, Personas.Staff.Email, Personas.SecondStaff.Email, Personas.CustomerAdmin.Email, Personas.Orderer.Email }
                .Concat(CoordinationShowcaseFixture.StaffMembers.Select(staff => staff.Email)).Order(StringComparer.Ordinal),
            listed.Value!.Select(candidate => candidate.Email).Order(StringComparer.Ordinal));
        Assert.All(listed.Value!, candidate => Assert.Equal(candidate.Email is var email && (email == Personas.CustomerAdmin.Email || email == Personas.Orderer.Email) ? JourneyTenantFixture.CustomerName : JourneyTenantFixture.TenantName, candidate.TenantName));

        var target = await users.FindByEmailAsync(state.ImpersonationTarget.Email);
        var administrator = await users.FindByEmailAsync(state.TenantAdministrator.Email);
        var viewer = await users.FindByEmailAsync(state.TenantViewer.Email);
        Assert.Empty(await PermissionsOfAsync(scope.ServiceProvider, target!));
        Assert.Equal(RolePresets.SystemOwner.Permissions.ToHashSet(), (await PermissionsOfAsync(scope.ServiceProvider, administrator!)).ToHashSet());
        Assert.Equal(JourneyTenantFixture.ViewerPermissions.ToHashSet(), (await PermissionsOfAsync(scope.ServiceProvider, viewer!)).ToHashSet());
    }
    [Fact]
    public async Task Prep_writes_the_coordinator_and_staff_member_personas_with_their_presets_and_the_staff_member_works_at_the_head_office()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var (contract, preset, name) in new[] { (Personas.Coordinator, RolePresets.Coordinator, "Casey Lind"), (Personas.Staff, RolePresets.Staff, "Kim Berg"), (Personas.SecondStaff, RolePresets.Staff, "Jamie Fors") })
        {
            var written = state.Persona(contract.Key);
            Assert.Equal(contract.Email, written.Email);
            Assert.Equal(name, written.Name);
            await PersonaVerifier.VerifyAsync(services, contract, written.Password, TestContext.Current.CancellationToken);
            Assert.Equal(preset.Permissions.ToHashSet(), (await PermissionsOfAsync(scope.ServiceProvider, (await users.FindByEmailAsync(contract.Email))!)).ToHashSet());
        }

        foreach (var contract in new[] { Personas.Staff, Personas.SecondStaff })
        {
            var staff = await users.FindByEmailAsync(contract.Email);
            Assert.Equal(1, await E2EDatabase.ScalarAsync(
                $"SELECT COUNT(*) FROM Resources r JOIN ResourceUnitMemberships m ON m.ResourceId = r.Id JOIN OrganizationUnits u ON u.Id = m.OrganizationUnitId "
                + $"WHERE r.UserId = '{staff!.Id}' AND r.State = 1 AND u.Name = '{JourneyTenantFixture.HeadOfficeName}'"));
        }
    }

    [Fact]
    public async Task Prep_gives_both_staff_members_the_qualification_and_the_coordinator_adds_the_customer_through_the_services()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        Assert.Equal(new FoundationStaffing(JourneyTenantFixture.CustomerName, JourneyTenantFixture.QualificationName, JourneyTenantFixture.ServiceName), state.Staffing);
        Assert.Equal(state.Staffing, FoundationStateFile.TryRead(this.statePath)?.Staffing);

        var qualification = $"(SELECT Id FROM Qualifications WHERE Name = '{JourneyTenantFixture.QualificationName}' AND State = 1)";
        foreach (var contract in new[] { Personas.Staff, Personas.SecondStaff })
        {
            Assert.Equal(1, await E2EDatabase.ScalarAsync(
                $"SELECT COUNT(*) FROM ResourceQualifications q JOIN Resources r ON r.Id = q.ResourceId JOIN AspNetUsers u ON u.Id = r.UserId "
                + $"WHERE q.QualificationId = {qualification} AND u.NormalizedEmail = '{contract.Email.ToUpperInvariant()}'"));
        }

        var coordinator = $"(SELECT Id FROM AspNetUsers WHERE NormalizedEmail = '{Personas.Coordinator.Email.ToUpperInvariant()}')";
        Assert.Equal(1, await E2EDatabase.ScalarAsync(
            $"SELECT COUNT(*) FROM Customers c JOIN StateHistoryEntries h ON h.EntityId = c.Id "
            + $"WHERE c.Name = '{JourneyTenantFixture.CustomerName}' AND c.State = 1 AND h.TransitionKey = 'customer-activate' AND h.ActorId = {coordinator}"));
    }

    [Fact]
    public async Task Prep_enables_the_customers_portal_through_the_services_and_writes_the_customer_admin_persona()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        var written = state.Persona(Personas.CustomerAdmin.Key);
        Assert.Equal((Personas.CustomerAdmin.Email, "Morgan Reed"), (written.Email, written.Name));
        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await PersonaVerifier.VerifyAsync(services, Personas.CustomerAdmin, written.Password, TestContext.Current.CancellationToken);

        await using var scope = services.CreateAsyncScope();
        var administrator = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(Personas.CustomerAdmin.Email))!;
        Assert.Equal(RolePresets.CustomerAdmin.Permissions.ToHashSet(), (await PermissionsOfAsync(scope.ServiceProvider, administrator)).ToHashSet());

        var company = $"(SELECT Id FROM Tenants WHERE Name = '{JourneyTenantFixture.TenantName}')";
        var owner = $"(SELECT Id FROM AspNetUsers WHERE NormalizedEmail = '{state.TenantAdministrator.Email.ToUpperInvariant()}')";
        Assert.Equal(1, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM Tenants WHERE Id = '{administrator.TenantId}' AND ParentTenantId = {company} AND State = {(int)TenantState.Active} AND Name = '{JourneyTenantFixture.CustomerName}'"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync(
            $"SELECT COUNT(*) FROM Customers c JOIN StateHistoryEntries h ON h.EntityId = c.Id "
            + $"WHERE c.Name = '{JourneyTenantFixture.CustomerName}' AND c.LinkedTenantId = '{administrator.TenantId}' AND h.TransitionKey = 'customer-enable-portal' AND h.ActorId = {owner}"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM Suppliers WHERE TenantId = '{administrator.TenantId}' AND LinkedTenantId = {company} AND Name = '{JourneyTenantFixture.TenantName}'"));
    }

    [Fact]
    public async Task Prep_has_the_customer_administrator_create_the_orderer_persona_through_the_services()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);
        var written = state.Persona(Personas.Orderer.Key);
        Assert.Equal((Personas.Orderer.Email, "Riley Quinn"), (written.Email, written.Name));
        await using var services = PrepServices.Build(E2EDatabase.ConnectionString, TimeProvider.System);
        await PersonaVerifier.VerifyAsync(services, Personas.Orderer, written.Password, TestContext.Current.CancellationToken);

        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var orderer = (await users.FindByEmailAsync(Personas.Orderer.Email))!;
        var administrator = (await users.FindByEmailAsync(Personas.CustomerAdmin.Email))!;
        Assert.Equal(administrator.TenantId, orderer.TenantId);
        Assert.Equal(RolePresets.Orderer.Permissions.ToHashSet(), (await PermissionsOfAsync(scope.ServiceProvider, orderer)).ToHashSet());
        Assert.Equal(1, await E2EDatabase.ScalarAsync(
            $"SELECT COUNT(*) FROM StateHistoryEntries WHERE EntityId = '{orderer.Id}' AND TransitionKey = '{ApplicationUserWorkflowAttribute.PortalCreateOrdererStep}' AND ActorId = '{administrator.Id}'"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync(
            $"SELECT COUNT(*) FROM OrganizationUnitUserRoles h JOIN TenantRoles r ON r.Id = h.TenantRoleId WHERE h.UserId = '{orderer.Id}' AND r.Name = '{RolePresets.Orderer.Name}'"));
    }

    [Fact]
    public async Task Prep_seeds_the_error_log_rows_the_smoke_journey_filters_and_names_them_in_the_state()
    {
        var state = await E2EDatabase.PrepareAsync(this.statePath);

        Assert.Equal(state.ErrorLog.Entries, FoundationStateFile.TryRead(this.statePath)?.ErrorLog.Entries);

        Assert.Contains(state.ErrorLog.Entries, entry => entry.Level == "Critical");
        Assert.Contains(state.ErrorLog.Entries, entry => entry.Level == "Error" && entry.DaysAgo > 7 && entry.DaysAgo < 30);
        Assert.Contains(state.ErrorLog.Entries, entry => !entry.Message.Contains(state.ErrorLog.Search, StringComparison.Ordinal));
        Assert.Equal(state.ErrorLog.Entries.Count, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM ErrorLogs WHERE Category = '{typeof(ErrorLogFixture).FullName}'"));
        Assert.Equal(1, await E2EDatabase.ScalarAsync($"SELECT COUNT(*) FROM ErrorLogs WHERE Category = '{typeof(ErrorLogFixture).FullName}' AND Occurred < DATEADD(day, -7, SYSUTCDATETIME()) AND Occurred > DATEADD(day, -30, SYSUTCDATETIME())"));
    }

    [Fact]
    public async Task Prep_pins_every_tenants_time_zone_to_the_apps_platform_zone_on_the_tenants_behalf()
    {
        await E2EDatabase.PrepareAsync(this.statePath);

        var zone = PrepServices.AppConfiguration()["Platform:TimeZone"];
        Assert.False(string.IsNullOrWhiteSpace(zone));
        Assert.DoesNotContain('\'', zone);

        Assert.True(await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants WHERE IsRootTenant = 0") >= 1);
        Assert.Equal(0, await E2EDatabase.ScalarAsync(
            $"SELECT COUNT(*) FROM Tenants t WHERE t.IsRootTenant = 0 AND (SELECT COUNT(*) FROM TenantSettings s JOIN AspNetUsers u ON u.Id = s.CreatedById "
            + $"WHERE s.TenantId = t.Id AND s.[Key] = 'General.TimeZone' AND s.[Value] = '{zone}' AND u.IsSystemAccount = 1 AND u.TenantId = t.Id) <> 1"));
        Assert.Equal(0, await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM TenantSettings s JOIN Tenants t ON t.Id = s.TenantId WHERE t.IsRootTenant = 1"));
    }

    private static async Task<IReadOnlySet<RolePermission>> PermissionsOfAsync(IServiceProvider services, ApplicationUser user)
    {
        var runtime = new RuntimeExecutionContext(user.TenantId, user.Id);
        var permissions = new PermissionService(services.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>(), runtime, TimeProvider.System);
        return await permissions.GetUserPermissionsAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_reset_empties_every_table_but_the_migration_history()
    {
        await E2EDatabase.PrepareAsync(this.statePath);
        Assert.True(await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM Tenants") > 0);

        await DatabaseReset.ResetAsync(E2EDatabase.ConnectionString, TestContext.Current.CancellationToken);

        var filled = await E2EDatabase.ScalarAsync("""
            SELECT COUNT(*) FROM (
                SELECT t.name FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
                GROUP BY t.name HAVING SUM(p.rows) > 0) filled
            """);
        Assert.Equal(0, filled);
        Assert.True(await E2EDatabase.ScalarAsync("SELECT COUNT(*) FROM __EFMigrationsHistory") > 0);

        await E2EDatabase.PrepareAsync(this.statePath);
    }

    [Fact]
    public async Task A_run_that_fails_after_seeding_writes_no_state_file_and_removes_the_previous_one()
    {
        await E2EDatabase.PrepareAsync(this.statePath);
        Assert.True(File.Exists(this.statePath));

        var result = await new FoundationPrep(TimeProvider.System, new FailingAt("wrote"))
            .RunAsync(E2EDatabase.ConnectionString, this.statePath, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(this.statePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(this.statePath)!, "*.tmp"));
    }

    [Fact]
    public async Task A_run_that_fails_before_writing_leaves_no_state_file()
    {
        await E2EDatabase.PrepareAsync(this.statePath);

        var result = await new FoundationPrep(TimeProvider.System, new FailingAt("verifying"))
            .RunAsync(E2EDatabase.ConnectionString, this.statePath, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(this.statePath));
    }

    private sealed class FailingAt : StringWriter
    {
        private readonly string marker;

        public FailingAt(string marker)
        {
            this.marker = marker;
        }

        public override void WriteLine(string? value)
        {
            if (value?.Contains(this.marker, StringComparison.Ordinal) is true)
            {
                throw new IOException($"The run failed at \"{value}\".");
            }

            base.WriteLine(value);
        }
    }
}
