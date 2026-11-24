using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Scheduling;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Jobs;
using Staffora.Services.Customers;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using static Staffora.Services.Demo.DemoSteps;

namespace Staffora.Services.Demo;

public sealed record CoordinationShowcaseResult(IReadOnlyDictionary<string, Guid> Customers, Guid UnitId);

public static class CoordinationShowcase
{
    public static IReadOnlyList<DemoPerson> StaffMembers { get; } =
    [
        new("Ada", "Holm", "ada.holm@company-a.example.com"),
        new("Ben", "Carter", "ben.carter@company-a.example.com"),
        new("Chloe", "Park", "chloe.park@company-a.example.com"),
        new("Dev", "Patel", "dev.patel@company-a.example.com"),
        new("Erin", "Walsh", "erin.walsh@company-a.example.com"),
        new("Felix", "Grant", "felix.grant@company-a.example.com"),
    ];

    public static IReadOnlyList<string> Customers { get; } = ["Lakeside College", "Hillview School"];

    public const int JobCount = 8;

    public static async Task<CoordinationShowcaseResult> SeedAsync(IServiceProvider services, TimeProvider clock, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var monday = ScheduleRules.MondayOf(today);
        var tomorrow = today.AddDays(1);
        var periodEnd = monday.AddDays(7 * 10 + 4);

        var userIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        Guid unitId;
        await using (var owner = await DemoSessions.SignInAsync(services, CompanyASetup.Administrator.Email))
        {
            var provider = owner.ServiceProvider;
            var runtime = provider.GetRequiredService<Staffora.Core.Execution.IRuntimeExecutionContext>();
            var tenantId = runtime.TenantId ?? throw new InvalidOperationException("The system owner has no tenant.");
            var tree = Expect(await provider.GetRequiredService<OrganizationService>().FetchUnitTreeAsync(tenantId), "read the tenant's tree");
            unitId = tree.Single(unit => unit.IsRoot).Id;

            var roles = provider.GetRequiredService<TenantRoleService>();
            var holdings = provider.GetRequiredService<RoleHoldingService>();
            var presets = Expect(await roles.GetTenantRolesAsync(tenantId), "read the role presets");
            var staffRole = presets.Single(role => role.Name == RolePresets.Staff.Name && !role.IsSystemOwner).Id;
            var users = provider.GetRequiredService<UserService>();
            foreach (var staff in StaffMembers)
            {
                var id = Expect(await users.CreateApplicationUserAsync(
                    new ApplicationUser { FirstName = staff.FirstName, LastName = staff.LastName, Email = staff.Email, UserName = staff.Email },
                    ApplicationUserWorkflowAttribute.CreateStep,
                    cancellationToken), $"create {staff.Email}");
                Expect(await holdings.AssignUserToRoleAsync(id, unitId, staffRole, tenantId), $"give {staff.Email} the Staff role");
                userIds[staff.FirstName] = id;
            }
        }

        await using var coordinator = await DemoSessions.SignInAsync(services, CompanyASetup.Coordinator.Email);
        var scope = coordinator.ServiceProvider;

        var patterns = scope.GetRequiredService<WorkPatternService>();
        var fullDays = await PublishPatternAsync(patterns, "Full days", new TimeOnly(8, 0), new TimeOnly(16, 0), cancellationToken);
        var mornings = await PublishPatternAsync(patterns, "Mornings", new TimeOnly(8, 0), new TimeOnly(12, 30), cancellationToken);
        var assignments = scope.GetRequiredService<PatternAssignmentService>();
        foreach (var (name, pattern) in new[] { ("Ada", fullDays), ("Ben", fullDays), ("Chloe", fullDays), ("Dev", mornings), ("Erin", fullDays), ("Felix", fullDays) })
        {
            var fresh = Expect(await assignments.NewResourcePatternAssignmentAsync(userIds[name], cancellationToken), $"prepare {name}'s pattern");
            fresh.WorkPatternId = pattern;
            fresh.EffectiveFrom = monday.AddDays(-7);
            fresh.CycleAnchor = monday.AddDays(-7);
            var id = Expect(await assignments.CreateResourcePatternAssignmentAsync(fresh, ResourcePatternAssignmentWorkflowAttribute.SaveStep, cancellationToken), $"save {name}'s pattern");
            var updater = Expect(await assignments.LoadResourcePatternAssignmentUpdaterAsync(id, cancellationToken), $"open {name}'s pattern");
            Expect(await assignments.UpdateResourcePatternAssignmentAndTransitionAsync(updater, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, null, cancellationToken), $"activate {name}'s pattern");
        }

        var customers = scope.GetRequiredService<CustomerService>();
        var customerIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var name in Customers)
        {
            customerIds[name] = Expect(await customers.CreateCustomerAsync(new Customer { Name = name }, CustomerWorkflowAttribute.ActivateStep, cancellationToken), $"add the customer {name}");
        }

        var resources = Expect(await scope.GetRequiredService<ResourceService>().QueryStaffMembersAsync(new ListQuery { PageSize = 100 }, cancellationToken), "read the staff members");
        Guid Resource(string first) => resources.Rows.Single(row => row.UserId == userIds[first]).ResourceId;

        var jobs = scope.GetRequiredService<JobService>();
        var staffing = scope.GetRequiredService<SlotAssignmentService>();
        var (college, hillview) = (customerIds[Customers[0]], customerIds[Customers[1]]);

        var plan = new (string Name, Guid Customer, DateOnly Start, JobTimeSlot[] Occurrences, (string Slot, string? Staff)[] Slots)[]
        {
            ("Algebra 1A", college, monday, [Occurrence(Weekday.Monday, 8, 30, 10, 0), Occurrence(Weekday.Wednesday, 8, 30, 10, 0), Occurrence(Weekday.Friday, 8, 30, 10, 0)], [("Lead", "Ada"), ("Assistant", null)]),
            ("Biology 2B", hillview, monday, [Occurrence(Weekday.Tuesday, 10, 15, 11, 45), Occurrence(Weekday.Thursday, 10, 15, 11, 45)], [("Lead", "Ben")]),
            ("English 1C", college, monday, [Occurrence(Weekday.Monday, 13, 0, 14, 30), Occurrence(Weekday.Tuesday, 13, 0, 14, 30), Occurrence(Weekday.Wednesday, 13, 0, 14, 30), Occurrence(Weekday.Thursday, 13, 0, 14, 30), Occurrence(Weekday.Friday, 13, 0, 14, 30)], [("Lead", "Chloe")]),
            ("Physics 3A", hillview, tomorrow, [Occurrence(Weekday.Monday, 10, 0, 11, 30), Occurrence(Weekday.Wednesday, 10, 0, 11, 30), Occurrence(Weekday.Friday, 10, 30, 12, 0)], [("Lead", "?Dev")]),
            ("History 2A", college, tomorrow, [Occurrence(Weekday.Tuesday, 8, 0, 9, 30), Occurrence(Weekday.Thursday, 8, 0, 9, 30), Occurrence(Weekday.Friday, 10, 30, 12, 0)], [("Lead", "Erin")]),
            ("Chemistry 1B", hillview, monday, [Occurrence(Weekday.Wednesday, 11, 0, 12, 30), Occurrence(Weekday.Thursday, 14, 45, 16, 0)], [("Lead", null)]),
            ("Music 1A", college, tomorrow, [Occurrence(Weekday.Monday, 14, 45, 15, 45), Occurrence(Weekday.Tuesday, 9, 0, 10, 0), Occurrence(Weekday.Thursday, 14, 45, 15, 45)], [("Lead", "Felix"), ("Accompanist", null)]),
            ("Art 2C", hillview, monday, [Occurrence(Weekday.Tuesday, 11, 0, 12, 30), Occurrence(Weekday.Thursday, 11, 0, 12, 30)], [("Lead", "Ada")]),
        };

        foreach (var (name, customer, start, occurrences, slots) in plan)
        {
            var job = Expect(await jobs.CreateJobAsync(
                new Job { Name = name, CustomerId = customer, OrganizationUnitId = unitId, PeriodStart = start, PeriodEnd = periodEnd, TimeSlots = [.. occurrences] },
                JobWorkflowAttribute.RegisterStep,
                cancellationToken), $"register {name}");
            var slotIds = new List<(Guid Id, string? Staff)>();
            foreach (var (slot, staff) in slots)
            {
                slotIds.Add((Expect(await jobs.Slots.CreateJobSlotAsync(new JobSlot { JobId = job, Name = slot }, JobSlotWorkflowAttribute.AddStep, cancellationToken), $"add the slot {slot} to {name}"), staff));
            }

            var updater = Expect(await jobs.LoadJobUpdaterAsync(job, cancellationToken), $"open {name}");
            Expect(await jobs.UpdateJobAndTransitionAsync(updater, JobWorkflowAttribute.StartFromRegistrationStep, null, cancellationToken), $"start {name}");
            foreach (var (slot, staff) in slotIds)
            {
                if (staff is null)
                {
                    continue;
                }

                var asked = staff.StartsWith('?');
                var first = staff.TrimStart('?');
                Expect(asked
                    ? await staffing.RequestStaffAsync(slot, Resource(first), cancellationToken)
                    : await staffing.AssignStaffAsync(slot, Resource(first), cancellationToken), $"{(asked ? "ask" : "assign")} {first} for {name}");
            }
        }

        var absences = scope.GetRequiredService<ResourceAbsenceService>();
        await RegisterAbsenceAsync(absences, userIds["Felix"], AbsenceKind.SickLeave, today, tomorrow, null, null, cancellationToken);
        await RegisterAbsenceAsync(absences, userIds["Erin"], AbsenceKind.PlannedLeave, monday.AddDays(4), monday.AddDays(4), new TimeOnly(12, 0), null, cancellationToken);
        return new CoordinationShowcaseResult(customerIds, unitId);
    }

    internal static JobTimeSlot Occurrence(Weekday day, int startHour, int startMinute, int endHour, int endMinute) =>
        new() { Weekday = day, Start = new TimeOnly(startHour, startMinute), End = new TimeOnly(endHour, endMinute) };

    private static async Task<Guid> PublishPatternAsync(WorkPatternService patterns, string name, TimeOnly start, TimeOnly end, CancellationToken cancellationToken)
    {
        var draft = new WorkPattern { Name = name, CycleWeeks = 1 };
        foreach (var day in Enumerable.Range(0, 5))
        {
            draft.Segments.Add(new WorkPatternSegment { DayIndex = day, Start = start, End = end });
        }

        var id = Expect(await patterns.CreateWorkPatternAsync(draft, WorkPatternWorkflowAttribute.SaveStep, cancellationToken), $"save the pattern {name}");
        var updater = Expect(await patterns.LoadWorkPatternUpdaterAsync(id, cancellationToken), $"open the pattern {name}");
        Expect(await patterns.UpdateWorkPatternAndTransitionAsync(updater, WorkPatternWorkflowAttribute.PublishStep, null, cancellationToken), $"publish the pattern {name}");
        return id;
    }

    private static async Task RegisterAbsenceAsync(ResourceAbsenceService absences, Guid userId, AbsenceKind kind, DateOnly first, DateOnly last, TimeOnly? start, TimeOnly? end, CancellationToken cancellationToken)
    {
        var absence = Expect(await absences.NewResourceAbsenceAsync(userId, cancellationToken), "prepare an absence");
        (absence.Kind, absence.FirstDay, absence.LastDay, absence.StartTime, absence.EndTime) = (kind, first, last, start, end);
        Expect(await absences.CreateResourceAbsenceAsync(absence, ResourceAbsenceWorkflowAttribute.RegisterStep, cancellationToken), "register an absence");
    }
}
