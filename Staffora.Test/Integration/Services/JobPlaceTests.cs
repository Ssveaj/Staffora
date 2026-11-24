using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Jobs;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.FieldAccess;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class JobPlaceTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2027, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 3, 26);
    private static readonly DateOnly Monday = new(2027, 3, 8);

    private readonly RealDatabase database;

    public JobPlaceTests(RealDatabase database)
    {
        this.database = database;
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_jobs_place_saves_and_reads_back_through_the_dialogs_updater()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Place Saved", Ct);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();

        var job = Made(await jobs.CreateJobAsync(new Job
        {
            Name = "Office cleaning",
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            PlaceName = "  Hillview School ",
            StreetAddress = "   ",
            City = " Stockholm",
            TimeSlots = [AssignmentScenario.Occurrence(Weekday.Monday, new TimeOnly(8, 0), new TimeOnly(10, 0))],
        }, JobWorkflowAttribute.RegisterStep, Ct));
        Assert.Equal(("Hillview School", null, null, "Stockholm", null), await PlaceAsync(job));

        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        Assert.Equal("Hillview School", updater.Entity.PlaceName);
        (updater.Entity.PlaceName, updater.Entity.StreetAddress, updater.Entity.PostalCode, updater.Entity.City, updater.Entity.AccessNotes) =
            (" Hillview School, block B ", " Main Street 1 ", "111 22", "  ", "  Door code 4512, key box by the gate. ");
        var saved = await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveRegistrationStep, null, Ct);
        Assert.True(saved.IsSuccess, saved.Error);
        var reread = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!.Entity;
        Assert.Equal(
            ("Hillview School, block B", "Main Street 1", "111 22", (string?)null, "Door code 4512, key box by the gate."),
            (reread.PlaceName, reread.StreetAddress, reread.PostalCode, reread.City, reread.AccessNotes));
        Assert.Equal("Hillview School, block B, Main Street 1, 111 22", JobPlace.Of(reread).Text);

        Made(await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = "Cleaner" }, JobSlotWorkflowAttribute.AddStep, Ct));
        Made(await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.StartFromRegistrationStep, Ct));
        Made(await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.ConfirmStep, Ct));
        var confirmed = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        confirmed.Entity.City = "Uppsala";
        Made(await jobs.UpdateJobAndTransitionAsync(confirmed, JobWorkflowAttribute.SaveConfirmedStep, null, Ct));
        Assert.Equal("Uppsala", (await PlaceAsync(job)).City);
    }

    [Fact]
    public async Task A_place_over_its_length_is_refused_and_a_canceled_or_completed_jobs_place_is_read_only()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Place Refused", Ct);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();

        var tooLong = await jobs.CreateJobAsync(new Job
        {
            Name = "Office cleaning",
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            City = new string('c', Job.CityMaxLength + 1),
        }, JobWorkflowAttribute.RegisterStep, Ct);
        Assert.Equal(JobService.PlaceTooLong, tooLong.Error);

        var job = Made(await jobs.CreateJobAsync(new Job
        {
            Name = "Office cleaning",
            CustomerId = scenario.Customer,
            OrganizationUnitId = scenario.StaffMembers.RootId,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            PlaceName = "Hillview School",
        }, JobWorkflowAttribute.RegisterStep, Ct));
        foreach (var edit in new Action<Job>[]
        {
            entity => entity.PlaceName = new string('n', Job.PlaceNameMaxLength + 1),
            entity => entity.StreetAddress = new string('s', Job.StreetAddressMaxLength + 1),
            entity => entity.PostalCode = new string('1', Job.PostalCodeMaxLength + 1),
            entity => entity.AccessNotes = new string('a', Job.AccessNotesMaxLength + 1),
        })
        {
            var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
            edit(updater.Entity);
            Assert.Equal(JobService.PlaceTooLong, (await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveRegistrationStep, null, Ct)).Error);
        }

        Assert.Equal(("Hillview School", null, null, null, null), await PlaceAsync(job));

        var workflow = WorkflowRegistry.Default.Get<JobState, Job>();
        foreach (var kept in new[] { JobState.Completed, JobState.CanceledByCustomer, JobState.CanceledBySupplier, JobState.Deleted })
        {
            Assert.Equal(FieldAccessFlags.ReadOnly, workflow.GetFieldConfig(new Job { State = kept }).GetAreaAccess(JobWorkflowAttribute.AreaPlace));
        }

        foreach (var open in new[] { JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress })
        {
            Assert.Equal(FieldAccessFlags.ReadWrite, workflow.GetFieldConfig(new Job { State = open }).GetAreaAccess(JobWorkflowAttribute.AreaPlace));
        }

        Made(await AssignmentScenario.StepJobAsync(jobs, job, JobWorkflowAttribute.CancelStepFrom(JobState.Registration), Ct));
        await using var context = this.database.CreateContext(scenario.StaffMembers.Onboarded.RuntimeOf(scenario.Coordinator));
        var row = await context.Jobs.SingleAsync(stored => stored.Id == job, Ct);
        row.City = "Uppsala";
        await Assert.ThrowsAsync<WorkflowFieldEditException>(() => context.SaveChangesAsync(Ct));
        Assert.Null((await PlaceAsync(job)).City);
    }

    [Fact]
    public async Task A_sent_order_carries_its_place_to_the_job_and_no_access_notes()
    {
        var portal = await PortalScenario.CreateAsync(this.database, "Place Ordered", Ct);
        var orderer = await portal.AddOrdererAsync(this.database, "Olive", Ct);
        await using var host = ServiceHost.Create(this.database, portal.RuntimeOf(orderer));
        var orders = host.Get<OrderService>();

        var fresh = (await orders.NewOrderAsync(Ct)).Value!;
        (fresh.Name, fresh.PeriodStart, fresh.PeriodEnd) = ("Office cleaning", PeriodStart, PeriodEnd);
        fresh.Schedule = JobSchedule.Recurring;
        (fresh.PlaceName, fresh.StreetAddress, fresh.PostalCode, fresh.City) = (" Hillview School ", "Main Street 1", " ", null);
        var order = Made(await orders.CreateOrderAsync(fresh, OrderWorkflowAttribute.SaveDraftStep, Ct));
        var draft = (await orders.LoadOrderUpdaterAsync(order, Ct)).Value!;
        (draft.Entity.PostalCode, draft.Entity.City) = ("111 22", " Stockholm ");
        Made(await orders.UpdateOrderAndTransitionAsync(draft, OrderWorkflowAttribute.SaveStep, null, Ct));

        var refused = (await orders.LoadOrderUpdaterAsync(order, Ct)).Value!;
        refused.Entity.StreetAddress = new string('s', Job.StreetAddressMaxLength + 1);
        Assert.Equal(OrderService.PlaceTooLong, (await orders.UpdateOrderAndTransitionAsync(refused, OrderWorkflowAttribute.SaveStep, null, Ct)).Error);

        var sending = (await orders.LoadOrderUpdaterAsync(order, Ct)).Value!;
        Made(await orders.UpdateOrderAndTransitionAsync(sending, OrderWorkflowAttribute.SendStep, null, Ct));
        await using (var system = this.database.CreateSystemContext())
        {
            var job = await system.Jobs.AsNoTracking().SingleAsync(row => row.TenantId == portal.Supplier.TenantId, Ct);
            Assert.Equal(("Hillview School", "Main Street 1", "111 22", "Stockholm", (string?)null), (job.PlaceName, job.StreetAddress, job.PostalCode, job.City, job.AccessNotes));
        }

        var details = await orders.ReadRequestAsync(order, Ct);
        Assert.True(details.IsSuccess, details.Error);
        Assert.Equal("Hillview School, Main Street 1, 111 22 Stockholm", details.Value!.Place.Text);
    }

    [Fact]
    public async Task The_timeline_cards_name_the_place_of_a_booking_and_of_a_slot_to_staff()
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, "Place Timeline", Ct);
        var ada = await scenario.AddStaffAsync(this.database, "Ada", Ct);
        var (job, slots) = await scenario.StartedJobAsync(this.database, "Office cleaning", PeriodStart, PeriodEnd, Ct, ["Cleaner", "Supervisor"]);
        await using var host = scenario.HostOf(this.database, scenario.Coordinator);
        var jobs = host.Get<JobService>();
        var updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        (updater.Entity.PlaceName, updater.Entity.City, updater.Entity.AccessNotes) = ("Hillview School", "Stockholm", "Door code 4512");
        Made(await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct));
        Made(await scenario.AssignAsync(this.database, scenario.Coordinator, slots[0], ada, Ct));

        var board = host.Get<CoordinationService>();
        var staffMembers = await board.ReadStaffMembersAsync(Monday, new BoardFilter(), Ct);
        Assert.True(staffMembers.IsSuccess, staffMembers.Error);
        var snapshot = await host.Get<CoordinationService>().ReadAsync(Ct);
        Assert.True(snapshot.IsSuccess, snapshot.Error);
        var bookings = await board.ReadBookingsAsync(staffMembers.Value!, snapshot.Value!, Ct);
        Assert.True(bookings.IsSuccess, bookings.Error);

        var held = Assert.Single(bookings.Value!.OccurrencesOf(await AssignmentScenario.ResourceOfAsync(this.database, ada, Ct), Monday));
        Assert.Equal("Hillview School", held.Place);
        var toStaff = Assert.Single(bookings.Value.ToStaff);
        Assert.Equal((slots[1], "Hillview School"), (toStaff.SlotId, toStaff.Place));

        updater = (await jobs.LoadJobUpdaterAsync(job, Ct)).Value!;
        updater.Entity.PlaceName = null;
        Made(await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.SaveCoordinatingStep, null, Ct));
        snapshot = await host.Get<CoordinationService>().ReadAsync(Ct);
        bookings = await board.ReadBookingsAsync(staffMembers.Value!, snapshot.Value!, Ct);
        Assert.Equal("Stockholm", Assert.Single(bookings.Value!.ToStaff).Place);
    }

    private async Task<(string? Name, string? Street, string? PostalCode, string? City, string? AccessNotes)> PlaceAsync(Guid job)
    {
        await using var system = this.database.CreateSystemContext();
        var row = await system.Jobs.AsNoTracking().SingleAsync(stored => stored.Id == job, Ct);
        return (row.PlaceName, row.StreetAddress, row.PostalCode, row.City, row.AccessNotes);
    }

    private static Guid Made(Result<Guid> made)
    {
        Assert.True(made.IsSuccess, made.Error);
        return made.Value;
    }

    private static void Made(Result done) => Assert.True(done.IsSuccess, done.Error);
}
