using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Home;
using Staffora.Services.Authorization;
using Staffora.Services.Portal;
using Staffora.Services.Services;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class PortalUserTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public PortalUserTests(RealDatabase database)
    {
        this.database = database;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_customer_administrator_creates_an_orderer_who_signs_in_and_sees_only_their_own_requests_by_default()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Orderer Staffing", Ct);
        var email = $"olive.{Guid.NewGuid():N}@portal.example";

        await using (var admin = ServiceHost.Create(this.database, scenario.AdministratorRuntime()))
        {
            await WriteRequestAsync(admin.Get<OrderService>(), "The administrator's job");
        }

        Result<Guid> created;
        await using (var admin = ServiceHost.Create(this.database, scenario.AdministratorRuntime()))
        {
            var users = admin.Get<UserService>();
            var draft = await users.NewApplicationUserAsync(Ct);
            Assert.True(draft.IsSuccess, draft.Error);
            var user = draft.Value!;
            (user.FirstName, user.LastName, user.Email, user.UserName) = ("Olive", "Orderer", email, email);

            var fields = await users.GetApplicationUserCustomFieldsAsync(null, Ct);
            Assert.True(fields.IsSuccess, fields.Error);
            Assert.Empty(fields.Value!);
            Assert.True((await users.GetApplicationUserEditorOptionsAsync(null, Ct)).IsSuccess);
            created = await users.CreateApplicationUserAsync(user, ApplicationUserWorkflowAttribute.PortalCreateOrdererStep, Ct);
        }

        Assert.True(created.IsSuccess, created.Error);
        var password = PasswordIn(created.Message!);
        Assert.Equal(16, password.Length);
        Assert.DoesNotContain(password, created.Value.ToString(), StringComparison.Ordinal);

        await using (var system = this.database.CreateSystemContext())
        {
            var stored = await system.Users.AsNoTracking().SingleAsync(row => row.Id == created.Value, Ct);
            Assert.Equal((UserState.Active, scenario.PortalTenantId, scenario.AdministratorId), (stored.State, stored.TenantId, stored.CreatedById));
            Assert.NotEqual(password, stored.PasswordHash);
            var holdings = await system.OrganizationUnitUserRoles.AsNoTracking().Where(row => row.UserId == created.Value).Select(row => new { Role = row.TenantRole.Name, row.OrganizationUnit.UnitType, row.TenantId }).ToListAsync(Ct);
            var holding = Assert.Single(holdings);
            Assert.Equal((RolePresets.Orderer.Name, Staffora.Core.Entities.Organization.OrganizationUnitType.Company, scenario.PortalTenantId), (holding.Role, holding.UnitType, holding.TenantId));
        }

        Assert.Empty(await PortalDatabase.ColumnsHoldingAsync(this.database, password, Ct));

        Assert.True(await this.SignsInAsync(email, password));
        var session = await SessionCircuit.ResolveAsync(this.database, created.Value);
        Assert.Equal((created.Value, (Guid?)scenario.PortalTenantId), (session.UserId, session.TenantId));
        await using var orderer = ServiceHost.Create(this.database, session);
        Assert.Equal(RolePresets.Orderer.Permissions.Order(), (await orderer.Get<PermissionService>().GetUserPermissionsAsync(Ct)).Order());

        var requests = orderer.Get<OrderService>();
        var empty = await requests.QueryRequestsAsync(new ListQuery(), Ct);
        Assert.True(empty.IsSuccess, empty.Error);
        Assert.Empty(empty.Value!.Rows);
        Assert.Equal(1, empty.Value.HiddenCounts[OrderList.Everyone]);
        var own = await WriteRequestAsync(requests, "Olive's job");
        var mine = await requests.QueryRequestsAsync(new ListQuery(), Ct);
        Assert.Equal([own], mine.Value!.Rows.Select(row => row.Id));
        Assert.False((await orderer.Get<UserService>().QueryUsersAsync(new ListQuery())).IsSuccess);
        var home = await orderer.Get<TenantHomeService>().ReadPortalAsync(Ct);
        Assert.Equal((true, true, false), (home.Value!.MayFollowRequests, home.Value.MayRequest, home.Value.MayManageUsers));
    }

    [Fact]
    public async Task A_deactivated_orderer_cannot_sign_in_and_a_new_password_from_the_administrator_replaces_the_old_one()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Deactivate Staffing", Ct);
        var ordererId = await scenario.AddOrdererAsync(this.database, "Dana", Ct);
        var email = await this.EmailOfAsync(ordererId);
        Assert.True(await this.SignsInAsync(email, PortalScenario.OrdererPassword));

        await using (var admin = ServiceHost.Create(this.database, scenario.AdministratorRuntime()))
        {
            var listed = await admin.Get<UserService>().QueryUsersAsync(new ListQuery());
            Assert.True(listed.IsSuccess, listed.Error);
            var row = Assert.Single(listed.Value!.Rows, row => row.Id == ordererId);
            Assert.StartsWith($"{RolePresets.Orderer.Name} on ", Assert.Single(row.Roles!), StringComparison.Ordinal);
        }

        Assert.True((await this.UserStepAsync(scenario.AdministratorRuntime(), ordererId, ApplicationUserWorkflowAttribute.PortalDeactivateStep)).IsSuccess);
        Assert.False(await this.SignsInAsync(email, PortalScenario.OrdererPassword));
        Assert.True((await this.UserStepAsync(scenario.AdministratorRuntime(), ordererId, ApplicationUserWorkflowAttribute.PortalReactivateStep)).IsSuccess);
        Assert.True(await this.SignsInAsync(email, PortalScenario.OrdererPassword));

        var reset = await this.UserStepAsync(scenario.AdministratorRuntime(), ordererId, ApplicationUserWorkflowAttribute.PortalNewPasswordStep);
        Assert.True(reset.IsSuccess, reset.Error);
        var issued = PasswordIn(reset.Message!);
        Assert.False(await this.SignsInAsync(email, PortalScenario.OrdererPassword));
        Assert.True(await this.SignsInAsync(email, issued));
        Assert.Empty(await PortalDatabase.ColumnsHoldingAsync(this.database, issued, Ct));

        Assert.False((await this.UserStepAsync(scenario.AdministratorRuntime(), scenario.AdministratorId, ApplicationUserWorkflowAttribute.PortalDeactivateStep)).IsSuccess);
        Assert.False((await this.UserStepAsync(scenario.AdministratorRuntime(), scenario.AdministratorId, ApplicationUserWorkflowAttribute.PortalNewPasswordStep)).IsSuccess);
        Assert.True(await this.SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));
    }

    [Fact]
    public async Task A_second_administrator_gives_the_first_one_whose_initial_password_was_lost_a_new_one()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Second Admin Staffing", Ct);
        var email = $"sky.{Guid.NewGuid():N}@portal.example";
        Guid second;
        await using (var admin = ServiceHost.Create(this.database, scenario.AdministratorRuntime()))
        {
            var created = await admin.Get<UserService>().CreateUserAsync(new ApplicationUser { FirstName = "Sky", LastName = "Second", Email = email, UserName = email }, scenario.PortalTenantId, PortalScenario.OrdererPassword, ApplicationUserWorkflowAttribute.PortalCreateAdministratorStep);
            Assert.True(created.IsSuccess, created.Error);
            second = created.Value!.Id;
        }

        var session = await SessionCircuit.ResolveAsync(this.database, second);
        await using (var host = ServiceHost.Create(this.database, session))
        {
            Assert.Equal(RolePresets.CustomerAdmin.Permissions.Order(), (await host.Get<PermissionService>().GetUserPermissionsAsync(Ct)).Order());
        }

        var reset = await this.UserStepAsync(session, scenario.AdministratorId, ApplicationUserWorkflowAttribute.PortalNewPasswordStep);
        Assert.True(reset.IsSuccess, reset.Error);
        Assert.False(await this.SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));
        Assert.True(await this.SignsInAsync(scenario.AdministratorEmail, PasswordIn(reset.Message!)));
    }

    [Fact]
    public async Task The_portal_keeps_an_active_customer_administrator_even_from_a_session_read_before_its_own_deactivation()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Last Admin Staffing", Ct);
        var second = await this.CreateAdministratorAsync(scenario.AdministratorRuntime(), scenario.PortalTenantId);

        var clock = new ManualClock(new DateTimeOffset(2027, 1, 11, 8, 0, 0, TimeSpan.Zero));
        var session = await SessionCircuit.ResolveAsync(this.database, second);
        await using var open = ServiceHost.Create(this.database, session, services => services.AddSingleton<TimeProvider>(clock));
        await open.Get<PermissionService>().LoadPermissionsAsync(Ct);
        Assert.True((await this.UserStepAsync(scenario.AdministratorRuntime(), second, ApplicationUserWorkflowAttribute.PortalDeactivateStep)).IsSuccess);

        var users = open.Get<UserService>();
        var updater = await users.LoadApplicationUserUpdaterAsync(scenario.AdministratorId, Ct);
        Assert.True(updater.IsSuccess, updater.Error);
        var refused = await users.UpdateApplicationUserAndTransitionAsync(updater.Value!, ApplicationUserWorkflowAttribute.PortalDeactivateStep, null, Ct);
        Assert.False(refused.IsSuccess);
        Assert.Contains(ApplicationUserWorkflowAttribute.LastCustomerAdministratorReason, refused.Error, StringComparison.Ordinal);
        await using (var system = this.database.CreateSystemContext())
        {
            Assert.Equal(UserState.Active, await system.Users.AsNoTracking().Where(user => user.Id == scenario.AdministratorId).Select(user => user.State).SingleAsync(Ct));
        }

        Assert.True(await this.SignsInAsync(scenario.AdministratorEmail, scenario.AdministratorPassword));

        Assert.True((await this.UserStepAsync(scenario.AdministratorRuntime(), second, ApplicationUserWorkflowAttribute.PortalReactivateStep)).IsSuccess);
        var deactivated = await this.UserStepAsync(await SessionCircuit.ResolveAsync(this.database, second), scenario.AdministratorId, ApplicationUserWorkflowAttribute.PortalDeactivateStep);
        Assert.True(deactivated.IsSuccess, deactivated.Error);
    }

    [Fact]
    public async Task A_portal_whose_only_administrator_lost_their_password_is_helped_by_the_platform_owner_impersonating_them()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Rescue Staffing", Ct);
        var platformOwner = scenario.Supplier.Platform.OwnerId;
        await using (var console = ServiceHost.Create(this.database, scenario.Supplier.Platform.PlatformOwnerRuntime()))
        {
            var allowed = await console.Get<ImpersonationAuthorizer>().AuthorizeAsync(platformOwner, scenario.AdministratorId, Ct);
            Assert.True(allowed.IsSuccess, allowed.Error);
        }

        var impersonating = scenario.Supplier.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(scenario.AdministratorId, scenario.PortalTenantId);
        var second = await this.CreateAdministratorAsync(impersonating, scenario.PortalTenantId);
        await using (var system = this.database.CreateSystemContext())
        {
            var stored = await system.Users.AsNoTracking().SingleAsync(user => user.Id == second, Ct);
            Assert.Equal((scenario.PortalTenantId, scenario.AdministratorId, (Guid?)platformOwner), (stored.TenantId, stored.CreatedById, stored.ImpersonatedById));
        }

        var reset = await this.UserStepAsync(await SessionCircuit.ResolveAsync(this.database, second), scenario.AdministratorId, ApplicationUserWorkflowAttribute.PortalNewPasswordStep);
        Assert.True(reset.IsSuccess, reset.Error);
        Assert.True(await this.SignsInAsync(scenario.AdministratorEmail, PasswordIn(reset.Message!)));
    }

    [Fact]
    public async Task The_edition_and_grant_ceilings_hold_on_the_portals_users_and_a_refusal_writes_nothing()
    {
        var scenario = await PortalScenario.CreateAsync(this.database, "Ceiling Staffing", Ct);
        var ordererId = await scenario.AddOrdererAsync(this.database, "Owen", Ct);
        var before = await this.UsersOfAsync(scenario.PortalTenantId);

        var owner = scenario.Supplier.OwnerRuntime();
        Assert.False((await this.CreateAsync(owner, scenario.Supplier.TenantId, ApplicationUserWorkflowAttribute.PortalCreateOrdererStep)).IsSuccess);

        Assert.False((await this.CreateAsync(scenario.AdministratorRuntime(), scenario.PortalTenantId, ApplicationUserWorkflowAttribute.CreateStep)).IsSuccess);
        Assert.False((await this.UserStepAsync(scenario.AdministratorRuntime(), ordererId, ApplicationUserWorkflowAttribute.DeactivateStep)).IsSuccess);

        var orderer = scenario.RuntimeOf(ordererId);
        Assert.False((await this.CreateAsync(orderer, scenario.PortalTenantId, ApplicationUserWorkflowAttribute.PortalCreateOrdererStep)).IsSuccess);
        Assert.False((await this.UserStepAsync(orderer, scenario.AdministratorId, ApplicationUserWorkflowAttribute.PortalDeactivateStep)).IsSuccess);

        Assert.Equal(before, await this.UsersOfAsync(scenario.PortalTenantId));
        await this.NarrowAdministratorAsync(scenario);
        await using (var narrowed = ServiceHost.Create(this.database, scenario.AdministratorRuntime()))
        {
            Assert.Equal([RolePermission.OrdersView, RolePermission.CustomerUsersManage], (await narrowed.Get<PermissionService>().GetUserPermissionsAsync(Ct)).Order());
        }

        before = await this.UsersOfAsync(scenario.PortalTenantId);
        var refused = await this.CreateAsync(scenario.AdministratorRuntime(), scenario.PortalTenantId, ApplicationUserWorkflowAttribute.PortalCreateAdministratorStep);
        Assert.False(refused.IsSuccess);
        Assert.Equal(PortalUserRoleFollowUp.RoleBeyondCeiling, refused.Error);
        Assert.Equal(before, await this.UsersOfAsync(scenario.PortalTenantId));
        var tooMuchForOrderer = await this.CreateAsync(scenario.AdministratorRuntime(), scenario.PortalTenantId, ApplicationUserWorkflowAttribute.PortalCreateOrdererStep);
        Assert.Equal(PortalUserRoleFollowUp.RoleBeyondCeiling, tooMuchForOrderer.Error);
        Assert.Equal(before, await this.UsersOfAsync(scenario.PortalTenantId));
    }

    private static string PasswordIn(string message)
    {
        var start = message.IndexOf("password is ", StringComparison.Ordinal) + "password is ".Length;
        return message[start..message.IndexOf(' ', start)];
    }

    private static async Task<Guid> WriteRequestAsync(OrderService service, string name)
    {
        var fresh = await service.NewOrderAsync(Ct);
        Assert.True(fresh.IsSuccess, fresh.Error);
        var request = fresh.Value!;
        (request.Name, request.PeriodStart, request.PeriodEnd) = (name, new DateOnly(2027, 2, 1), new DateOnly(2027, 5, 31));
        request.Schedule = JobSchedule.Recurring;
        var created = await service.CreateOrderAsync(request, OrderWorkflowAttribute.SaveDraftStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Result<ApplicationUser>> CreateAsync(IRuntimeExecutionContext runtime, Guid tenantId, string step)
    {
        var email = $"made.{Guid.NewGuid():N}@portal.example";
        await using var host = ServiceHost.Create(this.database, runtime);
        return await host.Get<UserService>().CreateUserAsync(new ApplicationUser { FirstName = "Made", LastName = "Here", Email = email, UserName = email }, tenantId, PortalScenario.OrdererPassword, step);
    }

    private async Task<Guid> CreateAdministratorAsync(IRuntimeExecutionContext runtime, Guid tenantId)
    {
        var email = $"sky.{Guid.NewGuid():N}@portal.example";
        await using var host = ServiceHost.Create(this.database, runtime);
        var created = await host.Get<UserService>().CreateUserAsync(new ApplicationUser { FirstName = "Sky", LastName = "Second", Email = email, UserName = email }, tenantId, PortalScenario.OrdererPassword, ApplicationUserWorkflowAttribute.PortalCreateAdministratorStep);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value!.Id;
    }

    private async Task<Result> UserStepAsync(IRuntimeExecutionContext runtime, Guid userId, string step)
    {
        await using var host = ServiceHost.Create(this.database, runtime);
        var users = host.Get<UserService>();
        var updater = await users.LoadApplicationUserUpdaterAsync(userId, Ct);
        return updater.IsSuccess ? await users.UpdateApplicationUserAndTransitionAsync(updater.Value!, step, null, Ct) : Result.Failure(updater.Error);
    }

    private async Task<bool> SignsInAsync(string email, string password)
    {
        await using var host = ServiceHost.Create(this.database, new RuntimeExecutionContext());
        var user = await host.Get<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        Assert.NotNull(user);
        return (await host.Get<SignInManager<ApplicationUser>>().CheckPasswordSignInAsync(user, password, lockoutOnFailure: false)).Succeeded;
    }

    private async Task<string> EmailOfAsync(Guid userId)
    {
        await using var system = this.database.CreateSystemContext();
        return await system.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.Email!).SingleAsync(Ct);
    }

    private async Task<(int Users, int Holdings)> UsersOfAsync(Guid tenantId)
    {
        await using var system = this.database.CreateSystemContext();
        return (await system.Users.CountAsync(user => user.TenantId == tenantId, Ct), await system.OrganizationUnitUserRoles.CountAsync(holding => holding.TenantId == tenantId, Ct));
    }

    private async Task NarrowAdministratorAsync(PortalScenario scenario)
    {
        await using (var system = this.database.CreateSystemContext())
        {
            using var lease = system.BeginSystemScope("test: narrow a portal administrator's roles");
            system.OrganizationUnitUserRoles.RemoveRange(await system.OrganizationUnitUserRoles.Where(holding => holding.UserId == scenario.AdministratorId).ToListAsync(Ct));
            await system.SaveChangesAsync(Ct);
        }

        await TwoTenantScenario.GrantAsync(this.database, scenario.Portal, scenario.AdministratorId, Ct, RolePermission.CustomerUsersManage, RolePermission.OrdersView);
    }
}

internal static class PortalDatabase
{
    public static async Task<List<string>> ColumnsHoldingAsync(RealDatabase database, string text, CancellationToken cancellationToken)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var columns = new List<(string Schema, string Table, string Column)>();
        await using (var list = new Microsoft.Data.SqlClient.SqlCommand("SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE DATA_TYPE IN ('nvarchar', 'varchar', 'nchar', 'char', 'ntext', 'text')", connection))
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var holding = new List<string>();
        foreach (var (schema, table, column) in columns)
        {
            await using var probe = new Microsoft.Data.SqlClient.SqlCommand($"SELECT COUNT(*) FROM [{schema}].[{table}] WHERE CHARINDEX(@text, CAST([{column}] AS nvarchar(max))) > 0", connection);
            probe.Parameters.AddWithValue("@text", text);
            if ((int)(await probe.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                holding.Add($"{schema}.{table}.{column}");
            }
        }

        return holding;
    }
}
