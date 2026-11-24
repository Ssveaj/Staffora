using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Scheduling;
using static Staffora.Services.Demo.DemoSteps;

namespace Staffora.Services.Demo;

internal sealed partial class DemoTenantBuilder
{
    public const string RejectReason = "Three others are away that week. Could you take the week after instead?";

    public const string QuestionToStaff = "Could you move it by a week? The team is short of staff that week.";

    public const string QuestionToCoordinator = "If my holiday days run out, may I take the last day unpaid?";

    public const string TakeOffReason = "Off sick that day, so the slot is open again for someone else.";

    private readonly HashSet<DemoStaffMember> absent = [];

    private PlannedAbsence PlanAbsence()
    {
        var planned = new PlannedAbsence(this.NextAbsent(), this.NextAbsent(), this.NextAbsent(), this.NextAbsent(), this.NextAbsent(), this.NextAbsent());
        this.bookings.BlockDays(this.ResourceOf(planned.Registered), this.today.AddDays(14), this.today.AddDays(18));
        this.bookings.BlockDays(this.ResourceOf(planned.DayOff), this.today.AddDays(10), this.today.AddDays(10));
        this.bookings.BlockDays(this.ResourceOf(planned.Leave), this.today.AddDays(21), this.today.AddDays(23));
        this.bookings.BlockDays(this.ResourceOf(planned.SickRegistered), this.today.AddDays(-12), this.today.AddDays(-10));
        this.bookings.BlockDays(this.ResourceOf(planned.SickReported), this.today.AddDays(-6), this.today.AddDays(-5));
        this.bookings.BlockDays(this.ResourceOf(planned.SickLongAgo), this.today.AddDays(-20), this.today.AddDays(-20));
        return planned;
    }

    private async Task AbsenceAsync(PlannedAbsence planned, CancellationToken cancellationToken)
    {
        var (senior, first, second) = (this.business.SeniorCoordinator, this.business.JuniorCoordinators[0], this.business.JuniorCoordinators[1]);
        await this.NewAbsenceAsync(senior, planned.Registered, AbsenceKind.PlannedLeave, 14, 18, ResourceAbsenceWorkflowAttribute.RegisterStep, cancellationToken);
        await this.DecideAsync(first, await this.ReportAsync(planned.DayOff, AbsenceKind.PlannedLeave, 10, 10, cancellationToken), ResourceAbsenceWorkflowAttribute.ApproveStep, null, cancellationToken);
        await this.DecideAsync(senior, await this.ReportAsync(planned.Leave, AbsenceKind.PlannedLeave, 21, 23, cancellationToken), ResourceAbsenceWorkflowAttribute.ApproveStep, null, cancellationToken);
        await this.NewAbsenceAsync(first, planned.SickRegistered, AbsenceKind.SickLeave, -12, -10, ResourceAbsenceWorkflowAttribute.RegisterStep, cancellationToken);
        await this.DecideAsync(senior, await this.ReportAsync(planned.SickReported, AbsenceKind.SickLeave, -6, -5, cancellationToken), ResourceAbsenceWorkflowAttribute.HandleSickStep, null, cancellationToken);
        await this.DecideAsync(second, await this.ReportAsync(planned.SickLongAgo, AbsenceKind.SickLeave, -20, -20, cancellationToken), ResourceAbsenceWorkflowAttribute.HandleSickStep, null, cancellationToken);

        await this.DecideAsync(senior, await this.ReportAsync(this.NextAbsent(), AbsenceKind.PlannedLeave, 30, 31, cancellationToken), ResourceAbsenceWorkflowAttribute.RejectStep, RejectReason, cancellationToken);
        foreach (var (from, to) in new[] { (25, 29), (35, 35), (40, 42) })
        {
            await this.ReportAsync(this.NextAbsent(), AbsenceKind.PlannedLeave, from, to, cancellationToken);
        }

        await this.DecideAsync(first, await this.ReportAsync(this.NextAbsent(), AbsenceKind.PlannedLeave, 33, 33, cancellationToken), ResourceAbsenceWorkflowAttribute.AskStaffStep, QuestionToStaff, cancellationToken);
        var asking = this.NextAbsent();
        await this.DecideAsync(asking.Person, await this.ReportAsync(asking, AbsenceKind.PlannedLeave, 38, 39, cancellationToken), ResourceAbsenceWorkflowAttribute.AskCoordinatorStep, QuestionToCoordinator, cancellationToken);

        var overWork = this.business.StaffMembers.FirstOrDefault(staff => !this.absent.Contains(staff) && this.bookings.IsBooked(this.ResourceOf(staff), this.today, this.today.AddDays(1))) ?? this.NextAbsent();
        this.absent.Add(overWork);
        await this.ReportAsync(overWork, AbsenceKind.SickLeave, 0, 1, cancellationToken);
        foreach (var free in this.business.StaffMembers.Where(staff => !this.absent.Contains(staff) && !this.bookings.IsBooked(this.ResourceOf(staff), this.today, this.today.AddDays(2))).Take(2).ToList())
        {
            this.absent.Add(free);
            await this.ReportAsync(free, AbsenceKind.SickLeave, 0, 2, cancellationToken);
        }

        var (soon, later) = (this.today.AddDays(2), this.today.AddDays(9));
        if (this.held.Where(entry => !this.absent.Contains(this.staffOf[entry.Resource]))
            .Select(entry => (Work: entry, Day: this.Occurrences(entry.Plan).Select(occurrence => DateOnly.FromDateTime(occurrence.From)).FirstOrDefault(day => day >= soon && day <= later)))
            .FirstOrDefault(found => found.Day != default) is { Work: { } work, Day: var sickDay })
        {
            var staff = this.staffOf[work.Resource];
            this.absent.Add(staff);
            var day = sickDay.DayNumber - this.today.DayNumber;
            await this.NewAbsenceAsync(second, staff, AbsenceKind.SickLeave, day, day, ResourceAbsenceWorkflowAttribute.RegisterStep, cancellationToken);
            var assignments = (await this.SessionAsync(work.Coordinator)).GetRequiredService<SlotAssignmentService>();
            var updater = Expect(await assignments.LoadSlotAssignmentUpdaterAsync(work.Assignment, cancellationToken), $"open the assignment of {staff.Person.Email}");
            Expect(await assignments.UpdateSlotAssignmentAndTransitionAsync(updater, SlotAssignmentWorkflowAttribute.TakeOffStep, TakeOffReason, cancellationToken), $"take {staff.Person.Email} off for absence");
        }
    }

    private DemoStaffMember NextAbsent()
    {
        var open = this.business.StaffMembers.Where(staff => !this.absent.Contains(staff)).ToList();
        var chosen = open[this.random.Next(open.Count)];
        this.absent.Add(chosen);
        return chosen;
    }

    private Task<Guid> ReportAsync(DemoStaffMember staff, AbsenceKind kind, int fromToday, int toToday, CancellationToken cancellationToken) =>
        this.NewAbsenceAsync(staff.Person, staff, kind, fromToday, toToday, ResourceAbsenceWorkflowAttribute.ReportStep, cancellationToken);

    private async Task<Guid> NewAbsenceAsync(DemoPerson author, DemoStaffMember staff, AbsenceKind kind, int fromToday, int toToday, string step, CancellationToken cancellationToken)
    {
        var absences = (await this.SessionAsync(author)).GetRequiredService<ResourceAbsenceService>();
        var absence = Expect(await absences.NewResourceAbsenceAsync(this.userIds[staff.Person.Email], cancellationToken), $"prepare an absence of {staff.Person.Email}");
        (absence.Kind, absence.FirstDay, absence.LastDay) = (kind, this.today.AddDays(fromToday), this.today.AddDays(toToday));
        return Expect(await absences.CreateResourceAbsenceAsync(absence, step, cancellationToken), $"write an absence of {staff.Person.Email}");
    }

    private async Task DecideAsync(DemoPerson person, Guid absence, string step, string? comment, CancellationToken cancellationToken)
    {
        var absences = (await this.SessionAsync(person)).GetRequiredService<ResourceAbsenceService>();
        var updater = Expect(await absences.LoadResourceAbsenceUpdaterAsync(absence, cancellationToken), "open an absence");
        Expect(await absences.UpdateResourceAbsenceAndTransitionAsync(updater, step, comment, cancellationToken), $"take the step {step} on an absence");
    }

    private sealed record PlannedAbsence(DemoStaffMember Registered, DemoStaffMember DayOff, DemoStaffMember Leave, DemoStaffMember SickRegistered, DemoStaffMember SickReported, DemoStaffMember SickLongAgo);
}
