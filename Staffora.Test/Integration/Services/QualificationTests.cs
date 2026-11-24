using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class QualificationTests : IClassFixture<RealDatabase>
{
    private readonly RealDatabase database;

    public QualificationTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_name_is_unique_in_its_tenant_and_free_in_another()
    {
        var (scenario, coordinator, _) = await ArrangeAsync("Naming Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var qualifications = host.Get<QualificationService>();
        var first = await CreateAsync(qualifications, "First aid");
        var second = await CreateAsync(qualifications, "Chemistry");

        var twin = await qualifications.CreateQualificationAsync(new Qualification { Name = " first AID " }, QualificationWorkflowAttribute.CreateStep, Ct);
        Assert.Equal(QualificationService.NameTaken, twin.Error);
        var updater = (await qualifications.LoadQualificationUpdaterAsync(second, Ct)).Value!;
        updater.Entity.Name = "First aid";
        Assert.Equal(QualificationService.NameTaken, (await qualifications.UpdateQualificationAndTransitionAsync(updater, QualificationWorkflowAttribute.SaveStep, null, Ct)).Error);
        Assert.Equal(["Chemistry", "First aid"], (await StoredAsync(scenario.TenantId)).Select(row => row.Name));

        var other = await StaffScenario.CreateAsync(this.database, Ct, "Other Naming Staffing");
        await using var otherHost = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime());
        await CreateAsync(otherHost.Get<QualificationService>(), "First aid");
        Assert.Equal(first, (await StoredAsync(scenario.TenantId)).Single(row => row.Name == "First aid").Id);
    }

    [Fact]
    public async Task An_archived_qualification_stays_on_its_staff_members_and_is_given_to_nobody_new_until_restored()
    {
        var (scenario, coordinator, kim) = await ArrangeAsync("Archiving Staffing");
        var lee = await scenario.AddPersonAsync(this.database, "Lee", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(lee, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        var qualifications = host.Get<QualificationService>();
        var welding = await CreateAsync(qualifications, "Welding");
        Assert.True((await qualifications.GiveQualificationAsync(kim, welding, Ct)).IsSuccess);
        Assert.True((await qualifications.UpdateQualificationAndTransitionAsync((await qualifications.LoadQualificationUpdaterAsync(welding, Ct)).Value!, QualificationWorkflowAttribute.ArchiveStep, null, Ct)).IsSuccess);

        var kept = (await host.Get<UserService>().QueryApplicationUserQualificationsAsync(kim, new ListQuery(), Ct)).Value!.Rows.Single();
        Assert.Equal(QualificationState.Archived, kept.State);
        Assert.Equal("Welding (archived)", kept.NameLabel);
        Assert.Equal(QualificationService.NotActive, (await qualifications.GiveQualificationAsync(lee, welding, Ct)).Error);
        Assert.DoesNotContain((await qualifications.GetChoicesForAsync(lee, Ct)).Value!, choice => choice.Id == welding);

        Assert.True((await qualifications.UpdateQualificationAndTransitionAsync((await qualifications.LoadQualificationUpdaterAsync(welding, Ct)).Value!, QualificationWorkflowAttribute.RestoreStep, null, Ct)).IsSuccess);
        Assert.Contains((await qualifications.GetChoicesForAsync(lee, Ct)).Value!, choice => choice.Id == welding);
        Assert.True((await qualifications.GiveQualificationAsync(lee, welding, Ct)).IsSuccess);
    }

    [Fact]
    public async Task A_reader_of_staff_reads_the_catalogue_and_changes_nothing()
    {
        var (scenario, coordinator, kim) = await ArrangeAsync("Reading Staffing");
        await using var keeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var music = await CreateAsync(keeper.Get<QualificationService>(), "Music");
        var reader = await scenario.AddPersonAsync(this.database, "Reader", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, reader, Ct, RolePermission.ResourcesView, RolePermission.UsersView);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(reader));
        var qualifications = host.Get<QualificationService>();

        Assert.Equal(["Music"], (await qualifications.QueryQualificationsAsync(new ListQuery(), Ct)).Value!.Rows.Select(item => item.Name));
        Assert.True((await qualifications.LoadQualificationUpdaterAsync(music, Ct)).IsSuccess);
        Assert.True((await qualifications.GetQualificationEditorOptionsAsync(music, Ct)).Value!.FieldsLocked);
        Assert.Equal(QualificationService.NoManage, (await qualifications.CreateQualificationAsync(new Qualification { Name = "Art" }, QualificationWorkflowAttribute.CreateStep, Ct)).Error);
        Assert.Equal(QualificationService.NoManage, (await qualifications.GiveQualificationAsync(kim, music, Ct)).Error);
        Assert.False(await qualifications.MayManageQualificationsOfAsync(kim, Ct));

        var stranger = await scenario.AddPersonAsync(this.database, "Stranger", Ct);
        await using var strangerHost = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(stranger));
        Assert.Equal(QualificationService.NoView, (await strangerHost.Get<QualificationService>().QueryQualificationsAsync(new ListQuery(), Ct)).Error);
        Assert.Equal(QualificationService.NoView, (await strangerHost.Get<QualificationService>().GetCatalogueAsync(Ct)).Error);
    }

    [Fact]
    public async Task Only_a_staff_member_who_is_not_inactive_is_given_or_loses_a_qualification()
    {
        var (scenario, coordinator, kim) = await ArrangeAsync("Inactive Staffing");
        var plain = await scenario.AddPersonAsync(this.database, "Plain", Ct);
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var qualifications = host.Get<QualificationService>();
        var drama = await CreateAsync(qualifications, "Drama");
        Assert.True((await qualifications.GiveQualificationAsync(kim, drama, Ct)).IsSuccess);

        Assert.Equal(QualificationService.StaffNotFound, (await qualifications.GiveQualificationAsync(plain, drama, Ct)).Error);
        Assert.Equal(QualificationService.NotFound, (await qualifications.GiveQualificationAsync(kim, Guid.NewGuid(), Ct)).Error);

        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            var holding = await StaffScenario.HoldingIdAsync(this.database, kim, scenario.Onboarded.Roles.Staff, scenario.TeamId, Ct);
            Assert.True((await owner.Get<RoleHoldingService>().RemoveUserFromRoleAsync(holding)).IsSuccess);
        }

        Assert.Equal(QualificationService.StaffInactive, (await qualifications.TakeQualificationAsync(kim, drama, Ct)).Error);
        Assert.False(await qualifications.MayManageQualificationsOfAsync(kim, Ct));
        Assert.Single(await HeldAsync((await StaffScenario.RecordOfAsync(this.database, kim, Ct)).Id));
    }

    [Fact]
    public async Task Taking_a_qualification_away_removes_its_row_alone()
    {
        var (scenario, coordinator, kim) = await ArrangeAsync("Taking Staffing");
        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var qualifications = host.Get<QualificationService>();
        var art = await CreateAsync(qualifications, "Art");
        var dance = await CreateAsync(qualifications, "Dance");
        Assert.True((await qualifications.GiveQualificationAsync(kim, art, Ct)).IsSuccess);
        Assert.True((await qualifications.GiveQualificationAsync(kim, dance, Ct)).IsSuccess);
        Assert.Equal(QualificationService.AlreadyHeld, (await qualifications.GiveQualificationAsync(kim, art, Ct)).Error);

        Assert.True((await qualifications.TakeQualificationAsync(kim, art, Ct)).IsSuccess);
        Assert.Equal(QualificationService.NotHeld, (await qualifications.TakeQualificationAsync(kim, art, Ct)).Error);
        Assert.Equal([dance], await HeldAsync((await StaffScenario.RecordOfAsync(this.database, kim, Ct)).Id));
        Assert.Equal(2, (await StoredAsync(scenario.TenantId)).Count);
    }

    [Fact]
    public async Task The_cached_catalogue_is_read_once_again_after_every_step_and_never_another_tenants()
    {
        var (scenario, coordinator, _) = await ArrangeAsync("Caching Staffing");
        var other = await StaffScenario.CreateAsync(this.database, Ct, "Other Caching Staffing");
        await using var app = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        await using var second = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime(), ServiceHost.SharingLookupCacheOf(app));
        await using var ofOther = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime(), ServiceHost.SharingLookupCacheOf(app));
        var qualifications = app.Get<QualificationService>();
        var history = await CreateAsync(qualifications, "History");
        await CreateAsync(ofOther.Get<QualificationService>(), "Geography");

        Assert.Equal(["History"], (await qualifications.GetCatalogueAsync(Ct)).Value!.Select(row => row.Name));
        app.ClearCommandLog();
        second.ClearCommandLog();
        Assert.Equal(["History"], (await second.Get<QualificationService>().GetCatalogueAsync(Ct)).Value!.Select(row => row.Name));
        Assert.Equal(0, CatalogueQueries(app, second));

        Assert.Equal(["Geography"], (await ofOther.Get<QualificationService>().GetCatalogueAsync(Ct)).Value!.Select(row => row.Name));

        var updater = (await qualifications.LoadQualificationUpdaterAsync(history, Ct)).Value!;
        updater.Entity.Name = "Modern history";
        Assert.True((await qualifications.UpdateQualificationAndTransitionAsync(updater, QualificationWorkflowAttribute.SaveStep, null, Ct)).IsSuccess);
        app.ClearCommandLog();
        second.ClearCommandLog();
        Assert.Equal(["Modern history"], (await second.Get<QualificationService>().GetCatalogueAsync(Ct)).Value!.Select(row => row.Name));
        Assert.Equal(1, CatalogueQueries(app, second));
        await CreateAsync(qualifications, "Ancient history");
        Assert.Equal(["Ancient history", "Modern history"], (await second.Get<QualificationService>().GetCatalogueAsync(Ct)).Value!.Select(row => row.Name));
    }

    [Fact]
    public async Task The_qualifications_of_many_staff_members_are_read_in_one_query_and_another_tenants_are_not_found()
    {
        var (scenario, coordinator, kim) = await ArrangeAsync("Bulk Staffing");
        var lee = await scenario.AddPersonAsync(this.database, "Lee", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(lee, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var qualifications = host.Get<QualificationService>();
        var biology = await CreateAsync(qualifications, "Biology");
        var swimming = await CreateAsync(qualifications, "Swimming");
        Assert.True((await qualifications.GiveQualificationAsync(kim, biology, Ct)).IsSuccess);
        Assert.True((await qualifications.GiveQualificationAsync(kim, swimming, Ct)).IsSuccess);
        var kimRecord = (await StaffScenario.RecordOfAsync(this.database, kim, Ct)).Id;
        var leeRecord = (await StaffScenario.RecordOfAsync(this.database, lee, Ct)).Id;

        host.ClearCommandLog();
        var bulk = await qualifications.ReadQualificationsOfStaffMembersAsync([kimRecord, leeRecord], Ct);
        Assert.True(bulk.IsSuccess, bulk.Error);
        Assert.Single(host.CommandLog, command => command.Contains("ResourceQualifications", StringComparison.Ordinal));
        Assert.Equal(new[] { biology, swimming }.Order(), bulk.Value![kimRecord].Order());
        Assert.Empty(bulk.Value[leeRecord]);

        var other = await StaffScenario.CreateAsync(this.database, Ct, "Other Bulk Staffing");
        await using var ofOther = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime());
        Assert.Empty((await ofOther.Get<QualificationService>().ReadQualificationsOfStaffMembersAsync([kimRecord], Ct)).Value![kimRecord]);
        Assert.Equal(QualificationService.NotFound, (await ofOther.Get<QualificationService>().LoadQualificationUpdaterAsync(biology, Ct)).Error);
        Assert.Equal(QualificationService.StaffNotFound, (await ofOther.Get<QualificationService>().GiveQualificationAsync(kim, biology, Ct)).Error);
    }

    [Fact]
    public async Task The_holders_of_a_qualification_are_listed_for_a_reader_of_staff_only_and_never_another_tenants()
    {
        var (scenario, coordinator, kim) = await ArrangeAsync("Holder Staffing");
        var lee = await scenario.AddPersonAsync(this.database, "Lee", Ct);
        await using (var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime()))
        {
            Assert.True((await owner.Get<RoleHoldingService>().AssignUserToRoleAsync(lee, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        }

        await using var host = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(coordinator));
        var qualifications = host.Get<QualificationService>();
        var chemistry = await CreateAsync(qualifications, "Chemistry");
        var drama = await CreateAsync(qualifications, "Drama");
        Assert.True((await qualifications.GiveQualificationAsync(kim, chemistry, Ct)).IsSuccess);
        Assert.True((await qualifications.GiveQualificationAsync(lee, chemistry, Ct)).IsSuccess);
        Assert.True((await qualifications.GiveQualificationAsync(lee, drama, Ct)).IsSuccess);

        var holders = await qualifications.QueryQualificationHoldersAsync(chemistry, new ListQuery(), Ct);
        Assert.True(holders.IsSuccess, holders.Error);
        Assert.Equal(2, holders.Value!.TotalCount);
        Assert.Equal([kim, lee], holders.Value.Rows.Select(row => row.UserId));
        Assert.All(holders.Value.Rows, row => Assert.Equal(ResourceState.Active, row.State));
        Assert.Equal([lee], (await qualifications.QueryQualificationHoldersAsync(chemistry, new ListQuery { Search = "Lee" }, Ct)).Value!.Rows.Select(row => row.UserId));
        Assert.Empty((await qualifications.QueryQualificationHoldersAsync(chemistry, new ListQuery().WithFilter(QualificationHolderList.Status, "inactive"), Ct)).Value!.Rows);
        Assert.Equal([lee], (await qualifications.QueryQualificationHoldersAsync(drama, new ListQuery(), Ct)).Value!.Rows.Select(row => row.UserId));

        var keeper = await scenario.AddPersonAsync(this.database, "Keeper", Ct);
        await TwoTenantScenario.GrantAsync(this.database, scenario.Onboarded.Tenant, keeper, Ct, RolePermission.QualificationsManage);
        await using (var ofKeeper = ServiceHost.Create(this.database, scenario.Onboarded.RuntimeOf(keeper)))
        {
            Assert.Equal(QualificationService.NoView, (await ofKeeper.Get<QualificationService>().QueryQualificationHoldersAsync(chemistry, new ListQuery(), Ct)).Error);
        }

        var other = await StaffScenario.CreateAsync(this.database, Ct, "Other Holder Staffing");
        await using var ofOther = ServiceHost.Create(this.database, other.Onboarded.OwnerRuntime());
        var foreign = await ofOther.Get<QualificationService>().QueryQualificationHoldersAsync(chemistry, new ListQuery(), Ct);
        Assert.True(foreign.IsSuccess, foreign.Error);
        Assert.Equal(0, foreign.Value!.TotalCount);
    }

    private async Task<(StaffScenario Scenario, Guid Coordinator, Guid Kim)> ArrangeAsync(string name)
    {
        var scenario = await StaffScenario.CreateAsync(this.database, Ct, TwoTenantScenario.UniqueName(name));
        var coordinator = await scenario.AddPersonAsync(this.database, "Casey", Ct);
        var kim = await scenario.AddPersonAsync(this.database, "Kim", Ct);
        await using var owner = ServiceHost.Create(this.database, scenario.Onboarded.OwnerRuntime());
        var tenants = owner.Get<RoleHoldingService>();
        Assert.True((await tenants.AssignUserToRoleAsync(coordinator, scenario.RootId, scenario.Onboarded.Roles.Coordinator, scenario.TenantId)).IsSuccess);
        Assert.True((await tenants.AssignUserToRoleAsync(kim, scenario.TeamId, scenario.Onboarded.Roles.Staff, scenario.TenantId)).IsSuccess);
        return (scenario, coordinator, kim);
    }

    private async Task<Guid> CreateAsync(QualificationService qualifications, string name)
    {
        var created = await qualifications.CreateQualificationAsync(new Qualification { Name = name }, QualificationWorkflowAttribute.CreateStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<List<Qualification>> StoredAsync(Guid tenantId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Qualifications.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Name).ToListAsync(Ct);
    }

    private async Task<List<Guid>> HeldAsync(Guid resourceId)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.ResourceQualifications.AsNoTracking().Where(row => row.ResourceId == resourceId).Select(row => row.QualificationId).ToListAsync(Ct);
    }

    private static int CatalogueQueries(params ServiceHost[] hosts) =>
        hosts.SelectMany(host => host.CommandLog).Count(command => command.Contains("FROM [Qualifications]", StringComparison.Ordinal));
}
