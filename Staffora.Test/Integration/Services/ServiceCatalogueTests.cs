using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Catalogue;
using Staffora.Services.Jobs;
using Staffora.Services.Resources;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class ServiceCatalogueTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 31);

    private readonly RealDatabase database;

    public ServiceCatalogueTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_administrator_creates_archives_and_restores_a_service_and_a_slot_made_from_it_carries_its_qualifications_at_their_levels()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Catalogue Staffing", Ct);
        var admin = scenario.Absence.Schedule.Admin;
        Assert.Contains(RolePermission.ServicesEdit, RolePresets.Administrator.Permissions);
        Assert.DoesNotContain(RolePermission.ServicesEdit, RolePresets.Coordinator.Permissions);
        await using var keeper = scenario.HostOf(this.database, admin);
        var catalogue = keeper.Get<ServiceCatalogueService>();
        var cleaning = await QualificationAsync(keeper, "Cleaning");
        var firstAid = await QualificationAsync(keeper, "First aid");
        var windows = await QualificationAsync(keeper, "Windows");

        var draft = new Service { Name = "  Cleaning supervisor ", Description = "Leads the crew", DefaultSlotCount = 2 };
        draft.Qualifications.Add(new ServiceQualification { QualificationId = cleaning, Level = RequirementLevel.Required });
        draft.Qualifications.Add(new ServiceQualification { QualificationId = firstAid, Level = RequirementLevel.Preferred });
        draft.Qualifications.Add(new ServiceQualification { QualificationId = windows, Level = RequirementLevel.Preferred });
        var created = await catalogue.CreateServiceAsync(draft, ServiceWorkflowAttribute.CreateStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        var service = created.Value;
        var stored = await StoredAsync(service);
        Assert.Equal(("Cleaning supervisor", ServiceState.Active, 2, false), (stored.Name, stored.State, stored.DefaultSlotCount, stored.OfferedInPortal));
        Assert.NotEqual(Guid.Empty, stored.CrossTenantId);
        Assert.Equal(
            new[] { (cleaning, RequirementLevel.Required), (firstAid, RequirementLevel.Preferred), (windows, RequirementLevel.Preferred) }.OrderBy(row => row.Item1),
            stored.Qualifications.OrderBy(row => row.QualificationId).Select(row => (row.QualificationId, row.Level)));
        Assert.Equal([ServiceWorkflowAttribute.CreateStep], await HistoryAsync(service));

        Assert.Equal(ServiceCatalogueService.NameTaken, (await catalogue.CreateServiceAsync(new Service { Name = "cleaning SUPERVISOR" }, ServiceWorkflowAttribute.CreateStep, Ct)).Error);
        Assert.Equal(ServiceCatalogueService.CountOutOfRange, (await catalogue.CreateServiceAsync(new Service { Name = "Crew", DefaultSlotCount = 21 }, ServiceWorkflowAttribute.CreateStep, Ct)).Error);

        var listed = await catalogue.QueryServicesAsync(new ListQuery().WithFilter(ServiceList.Status, "active"), Ct);
        Assert.True(listed.IsSuccess, listed.Error);
        Assert.Equal([("Cleaning supervisor", 2, 3)], listed.Value!.Rows.Select(row => (row.Name, row.DefaultSlotCount, row.QualificationCount)));
        var (job, _) = await scenario.StartedJobAsync(this.database, "Office Park", PeriodStart, PeriodEnd, Ct);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var options = await coordinator.Get<JobService>().GetJobEditorOptionsAsync(job, Ct);
            Assert.True(options.IsSuccess, options.Error);
            Assert.Equal([("Cleaning supervisor", 2)], options.Value!.Services.Select(choice => (choice.Name, choice.DefaultSlotCount)));

            var slot = await SlotFromServiceAsync(coordinator.Get<JobSlotService>(), job, service);
            var made = await StoredSlotAsync(slot);
            Assert.Equal((service, "Cleaning supervisor", "Leads the crew"), (made.ServiceId, made.Name, made.Description));
            Assert.Equal(
                new[] { (cleaning, RequirementLevel.Required), (firstAid, RequirementLevel.Preferred), (windows, RequirementLevel.Preferred) }.OrderBy(row => row.Item1),
                made.Qualifications.OrderBy(row => row.QualificationId).Select(row => (row.QualificationId, row.Level)));
        }

        var archivedQualification = await keeper.Get<QualificationService>().UpdateQualificationAndTransitionAsync(
            (await keeper.Get<QualificationService>().LoadQualificationUpdaterAsync(windows, Ct)).Value!, QualificationWorkflowAttribute.ArchiveStep, null, Ct);
        Assert.True(archivedQualification.IsSuccess, archivedQualification.Error);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var second = await StoredSlotAsync(await SlotFromServiceAsync(coordinator.Get<JobSlotService>(), job, service));
            Assert.Equal(new[] { cleaning, firstAid }.Order(), second.Qualifications.Select(row => row.QualificationId).Order());
        }

        Assert.True((await StepAsync(catalogue, service, ServiceWorkflowAttribute.ArchiveStep)).IsSuccess);
        Assert.Equal(ServiceState.Archived, (await StoredAsync(service)).State);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            var slots = coordinator.Get<JobSlotService>();
            Assert.Empty((await coordinator.Get<JobService>().GetJobEditorOptionsAsync(job, Ct)).Value!.Services);
            Assert.Equal(JobSlotService.ServiceNotActive, (await slots.NewJobSlotFromServiceAsync(job, service, Ct)).Error);
            var named = await slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Supervisor", ServiceId = service }, JobSlotWorkflowAttribute.AddStep, Ct);
            Assert.Equal(JobSlotService.ServiceNotActive, named.Error);
        }

        Assert.True((await StepAsync(catalogue, service, ServiceWorkflowAttribute.RestoreStep)).IsSuccess);
        await using (var coordinator = scenario.HostOf(this.database, scenario.Coordinator))
        {
            await SlotFromServiceAsync(coordinator.Get<JobSlotService>(), job, service);
        }

        Assert.Equal([ServiceWorkflowAttribute.CreateStep, ServiceWorkflowAttribute.ArchiveStep, ServiceWorkflowAttribute.RestoreStep], await HistoryAsync(service));
    }

    [Fact]
    public async Task A_coordinator_reads_no_catalogue_and_changes_nothing_and_a_preferred_qualification_only_ranks_the_candidates()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Ranking Staffing", Ct);
        await using var keeper = scenario.HostOf(this.database, scenario.Absence.Schedule.Admin);
        var cleaning = await QualificationAsync(keeper, "Cleaning");
        var firstAid = await QualificationAsync(keeper, "First aid");
        var draft = new Service { Name = "Office cleaning" };
        draft.Qualifications.Add(new ServiceQualification { QualificationId = cleaning, Level = RequirementLevel.Required });
        draft.Qualifications.Add(new ServiceQualification { QualificationId = firstAid, Level = RequirementLevel.Preferred });
        var service = (await keeper.Get<ServiceCatalogueService>().CreateServiceAsync(draft, ServiceWorkflowAttribute.CreateStep, Ct)).Value;

        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var bo = await scenario.AddStaffAsync(this.database, "Bo", Ct);
        var cy = await scenario.AddStaffAsync(this.database, "Cy", Ct);
        var qualifications = keeper.Get<QualificationService>();
        foreach (var (user, held) in new[] { (ada, cleaning), (bo, cleaning), (bo, firstAid), (cy, firstAid) })
        {
            Assert.True((await qualifications.GiveQualificationAsync(user, held, Ct)).IsSuccess);
        }

        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var catalogue = host.Get<ServiceCatalogueService>();

        Assert.Equal(ServiceCatalogueService.NoView, (await catalogue.QueryServicesAsync(new ListQuery(), Ct)).Error);
        Assert.Equal(ServiceCatalogueService.NoView, (await catalogue.LoadServiceUpdaterAsync(service, Ct)).Error);
        Assert.Equal(ServiceCatalogueService.NoEdit, (await catalogue.CreateServiceAsync(new Service { Name = "Window cleaning" }, ServiceWorkflowAttribute.CreateStep, Ct)).Error);
        Assert.Equal(ServiceCatalogueService.NoEdit, (await catalogue.NewServiceAsync(Ct)).Error);
        var (job, _) = await scenario.StartedJobAsync(this.database, "Head office", PeriodStart, PeriodEnd, Ct);
        var slot = await SlotFromServiceAsync(host.Get<JobSlotService>(), job, service);

        var candidates = await host.Get<StaffingService>().QuerySlotCandidatesAsync(slot, new ListQuery(), Ct);
        Assert.True(candidates.IsSuccess, candidates.Error);
        var rows = candidates.Value!.Rows.Where(row => row.Name.StartsWith("Ada", StringComparison.Ordinal) || row.Name.StartsWith("Bo", StringComparison.Ordinal) || row.Name.StartsWith("Cy", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Bo", "Ada", "Cy"], rows.Select(row => row.Name.Split(' ')[0]));
        Assert.Equal(["The required one, 1 of 1 preferred", "The required one, 0 of 1 preferred", "0 of 1 required (missing: Cleaning), 1 of 1 preferred"], rows.Select(row => row.MatchLabel));
        Assert.Equal(["", "", "Cleaning"], rows.Select(row => string.Join(", ", row.Missing)));
    }

    private async Task<Guid> QualificationAsync(ServiceHost host, string name)
    {
        var created = await host.Get<QualificationService>().CreateQualificationAsync(new Qualification { Name = name }, QualificationWorkflowAttribute.CreateStep, Ct);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    private async Task<Guid> SlotFromServiceAsync(JobSlotService slots, Guid job, Guid service)
    {
        var made = await slots.NewJobSlotFromServiceAsync(job, service, Ct);
        Assert.True(made.IsSuccess, made.Error);
        var added = await slots.CreateJobSlotAsync(made.Value!, JobSlotWorkflowAttribute.AddStep, Ct);
        Assert.True(added.IsSuccess, added.Error);
        return added.Value;
    }

    private async Task<Staffora.Core.Models.Result> StepAsync(ServiceCatalogueService catalogue, Guid service, string step)
    {
        var updater = await catalogue.LoadServiceUpdaterAsync(service, Ct);
        Assert.True(updater.IsSuccess, updater.Error);
        return await catalogue.UpdateServiceAndTransitionAsync(updater.Value!, step, null, Ct);
    }

    private async Task<Service> StoredAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.Services.AsNoTracking().Include(service => service.Qualifications).SingleAsync(service => service.Id == id, Ct);
    }

    private async Task<JobSlot> StoredSlotAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.JobSlots.AsNoTracking().Include(slot => slot.Qualifications).SingleAsync(slot => slot.Id == id, Ct);
    }

    private async Task<List<string>> HistoryAsync(Guid id)
    {
        await using var context = this.database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.EntityType == nameof(Service) && entry.EntityId == id)
            .OrderBy(entry => entry.Created).ThenBy(entry => entry.Id)
            .Select(entry => entry.TransitionKey)
            .ToListAsync(Ct);
    }
}
