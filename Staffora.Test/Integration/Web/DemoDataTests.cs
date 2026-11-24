using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Staffora.Blazor.Helper;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Scheduling;
using Staffora.Core.Settings;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data.Seed;
using Staffora.Services.Authorization;
using Staffora.Services.Coordination;
using Staffora.Services.Demo;
using Staffora.Test.Integration.Data;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Web;

[Trait("Category", "Integration")]
public sealed class DemoDataTests : IClassFixture<RealDatabase>, IClassFixture<DemoDataTests.DevelopmentStart>
{
    private static readonly DateTimeOffset anchor = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] sourcePasswords = ["Admin123!", DemoDataSeeder.OwnerPassword, DemoDataSeeder.MemberPassword];

    private readonly RealDatabase database;
    private readonly DevelopmentStart start;

    public DemoDataTests(RealDatabase database, DevelopmentStart start)
    {
        this.database = database;
        this.start = start;
    }

    public static TheoryData<string> Businesses => [.. DemoDataSeeder.Businesses.Select(business => business.TenantName)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(anchor.ToLocalTime().DateTime);

    [Theory]
    [MemberData(nameof(Businesses))]
    public async Task Each_company_has_two_owners_three_coordinators_its_staff_units_and_customers_and_at_least_a_hundred_jobs_thirty_of_them_with_open_slots(string tenant)
    {
        var business = BusinessOf(tenant);
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, tenant);

        var holders = await context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => holding.TenantId == tenantId)
            .Select(holding => new { holding.TenantRole.Name, holding.TenantRole.IsSystemOwner, Email = holding.User.Email! })
            .ToListAsync(Ct);
        Assert.Equal(business.Owners.Select(owner => owner.Email).Order(), holders.Where(holding => holding.IsSystemOwner).Select(holding => holding.Email).Order());
        Assert.Equal([business.SeniorCoordinator.Email], holders.Where(holding => holding.Name == "Senior coordinator").Select(holding => holding.Email));
        Assert.Equal(business.JuniorCoordinators.Select(junior => junior.Email).Order(), holders.Where(holding => holding.Name == "Junior coordinator").Select(holding => holding.Email).Order());

        var resources = await context.Resources.AsNoTracking().Where(resource => resource.TenantId == tenantId).Select(resource => resource.Id).ToListAsync(Ct);
        Assert.True(resources.Count >= 24, $"{tenant} has {resources.Count} staff members.");
        Assert.Equal(business.StaffMembers.Count, resources.Count);
        var qualified = await context.ResourceQualifications.AsNoTracking().Where(row => row.TenantId == tenantId).Select(row => row.ResourceId).Distinct().ToListAsync(Ct);
        var patterned = await context.ResourcePatternAssignments.AsNoTracking().Where(row => row.TenantId == tenantId && row.State == ResourcePatternAssignmentState.Active).Select(row => row.ResourceId).Distinct().ToListAsync(Ct);
        Assert.Equal(resources.Order(), qualified.Order());
        Assert.Equal(resources.Order(), patterned.Order());

        var units = await context.OrganizationUnits.AsNoTracking().Where(unit => unit.TenantId == tenantId && unit.UnitType != OrganizationUnitType.Company).Select(unit => unit.Name).ToListAsync(Ct);
        Assert.Equal(business.Units.Select(unit => unit.Name).Order(StringComparer.Ordinal), units.Order(StringComparer.Ordinal));
        var memberships = await context.ResourceUnitMemberships.AsNoTracking().Where(membership => membership.TenantId == tenantId).Select(membership => membership.OrganizationUnit.Name).Distinct().ToListAsync(Ct);
        Assert.All(business.Units.Where(unit => unit.Parent is null), city => Assert.Contains(city.Name, memberships));

        Assert.Equal(8, await context.Customers.AsNoTracking().CountAsync(customer => customer.TenantId == tenantId && customer.State == CustomerState.Active, Ct));
        Assert.InRange(business.Services.Count, 4, 6);

        var jobs = await context.Jobs.AsNoTracking().Where(job => job.TenantId == tenantId).ToListAsync(Ct);
        Assert.True(jobs.Count >= 100, $"{tenant} has {jobs.Count} jobs.");
        Assert.All(jobs, job => Assert.False(string.IsNullOrEmpty(job.PlaceName), job.Name));
        Assert.Contains(jobs, job => job.PeriodStart <= Today.AddDays(-21));
        Assert.Contains(jobs, job => job.PeriodEnd >= Today.AddDays(42));
        Assert.True(jobs.Select(job => job.PlaceName).Distinct().Count() >= 8, $"{tenant} works at {jobs.Select(job => job.PlaceName).Distinct().Count()} places.");

        var open = await OpenSlotsByJobAsync(context, tenantId);
        Assert.True(open.Count(job => job.Value > 0) >= 30, $"{tenant} has {open.Count(job => job.Value > 0)} jobs with open slots.");
    }

    [Theory]
    [MemberData(nameof(Businesses))]
    public async Task Each_company_has_jobs_requests_rounds_and_orders_in_every_state_the_demo_lists(string tenant)
    {
        var business = BusinessOf(tenant);
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, tenant);
        var jobs = await context.Jobs.AsNoTracking().Where(job => job.TenantId == tenantId).ToListAsync(Ct);
        Assert.Equal(Enum.GetValues<JobState>().Where(state => state is not (JobState.Create or JobState.Deleted)).Order(), jobs.Select(job => job.State).Distinct().Order());
        Assert.True(jobs.Count(job => job.State == JobState.Completed) >= 15, $"{tenant} completed {jobs.Count(job => job.State == JobState.Completed)} jobs.");
        Assert.True(jobs.Count(job => job.State == JobState.InProgress) >= 5, $"{tenant} has {jobs.Count(job => job.State == JobState.InProgress)} jobs in progress.");
        Assert.True(jobs.Count(job => job.State is JobState.CanceledBySupplier or JobState.CanceledByCustomer) >= 4);
        Assert.Contains(jobs, job => job.Schedule == JobSchedule.Single);
        Assert.Contains(jobs, job => job.Schedule == JobSchedule.Recurring);

        var open = await OpenSlotsByJobAsync(context, tenantId);
        var confirmed = jobs.Count(job => job.State == JobState.Confirmed && open.GetValueOrDefault(job.Id) == 0);
        Assert.True(confirmed >= 20, $"{tenant} has {confirmed} confirmed jobs with every slot held.");
        Assert.Contains(jobs, job => job.State == JobState.Coordinating && open.GetValueOrDefault(job.Id) > 0 && job.PeriodStart > Today && job.PeriodStart <= Today.AddDays(3));

        var requests = await context.SlotAssignments.AsNoTracking().Where(assignment => assignment.TenantId == tenantId).Select(assignment => new { assignment.State, assignment.RespondBy }).ToListAsync(Ct);
        foreach (var state in new[] { SlotAssignmentState.Requested, SlotAssignmentState.Accepted, SlotAssignmentState.Assigned, SlotAssignmentState.NotSelected, SlotAssignmentState.StaffQuestion, SlotAssignmentState.Expired, SlotAssignmentState.Canceled })
        {
            Assert.True(requests.Any(request => request.State == state), $"{tenant} has no request {state}.");
        }

        Assert.True(requests.Count(request => request.State is SlotAssignmentState.Requested or SlotAssignmentState.StaffQuestion) >= 8);
        Assert.True(requests.Count(request => request.State == SlotAssignmentState.Expired) >= 3);
        Assert.True(requests.Count(request => request.State == SlotAssignmentState.Accepted) >= 2);
        Assert.Contains(requests, request => request.State == SlotAssignmentState.Requested && request.RespondBy is { } by && by > anchor.UtcDateTime && by <= anchor.UtcDateTime.AddHours(12));

        var rounds = await context.RequestRounds.AsNoTracking().Where(round => round.TenantId == tenantId).Select(round => round.State).ToListAsync(Ct);
        Assert.Equal(new[] { RequestRoundState.Open, RequestRoundState.Filled, RequestRoundState.Expired }.Order(), rounds.Distinct().Order());

        var portalId = await TenantIdAsync(context, business.Portal.Customer);
        var orders = await context.Orders.AsNoTracking().Where(order => order.TenantId == portalId).Select(order => order.State).ToListAsync(Ct);
        Assert.Contains(OrderState.Confirmed, orders);
        Assert.Contains(OrderState.Coordinating, orders);
        Assert.Equal(3, orders.Count(state => state == OrderState.Sent));
        Assert.Equal(3, jobs.Count(job => job.State == JobState.AwaitingCoordination && (job.CrossTenantId ?? job.LinkedCrossTenantId) != null));

        var historied = await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.TenantId == tenantId).Select(entry => entry.EntityId).Distinct().ToListAsync(Ct);
        Assert.All(jobs, job => Assert.Contains(job.Id, historied));
    }

    [Theory]
    [MemberData(nameof(Businesses))]
    public async Task Each_company_has_absence_in_every_state_the_demo_lists_and_a_staff_member_taken_off_for_absence(string tenant)
    {
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, tenant);
        var absences = await context.ResourceAbsences.AsNoTracking().Where(absence => absence.TenantId == tenantId).ToListAsync(Ct);
        Assert.InRange(absences.Count, 14, 18);
        int Count(AbsenceKind kind, ResourceAbsenceState state) => absences.Count(absence => absence.Kind == kind && absence.State == state);
        Assert.Equal(3, Count(AbsenceKind.SickLeave, ResourceAbsenceState.Requested));
        Assert.True(Count(AbsenceKind.SickLeave, ResourceAbsenceState.Approved) >= 3);
        Assert.Equal(3, Count(AbsenceKind.PlannedLeave, ResourceAbsenceState.Requested));
        Assert.Equal(3, Count(AbsenceKind.PlannedLeave, ResourceAbsenceState.Approved));
        Assert.Equal(1, Count(AbsenceKind.PlannedLeave, ResourceAbsenceState.Question));
        Assert.Equal(1, Count(AbsenceKind.PlannedLeave, ResourceAbsenceState.StaffQuestion));
        Assert.Equal(1, Count(AbsenceKind.PlannedLeave, ResourceAbsenceState.Rejected));

        var held = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId && assignment.State == SlotAssignmentState.Assigned)
            .Select(assignment => new { assignment.ResourceId, assignment.Slot.Job.PeriodStart, assignment.Slot.Job.PeriodEnd })
            .ToListAsync(Ct);
        Assert.Contains(absences, absence => absence.Kind == AbsenceKind.SickLeave && absence.State == ResourceAbsenceState.Requested
            && held.Any(work => work.ResourceId == absence.ResourceId && work.PeriodStart <= absence.LastDay && work.PeriodEnd >= absence.FirstDay));

        var takenOff = await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.TenantId == tenantId && entry.TransitionKey == SlotAssignmentWorkflowAttribute.TakeOffStep).Select(entry => entry.EntityId).ToListAsync(Ct);
        var slot = await context.SlotAssignments.AsNoTracking().Where(assignment => takenOff.Contains(assignment.Id)).Select(assignment => assignment.JobSlotId).SingleAsync(Ct);
        Assert.False(await context.SlotAssignments.AsNoTracking().AnyAsync(assignment => assignment.JobSlotId == slot && assignment.State == SlotAssignmentState.Assigned, Ct), "the slot is open again");
    }

    [Theory]
    [MemberData(nameof(Businesses))]
    public async Task Lindale_and_Nordbygg_close_on_public_holidays_and_book_nobody_on_one_and_every_company_keeps_closed_days_of_its_own(string tenant)
    {
        var business = BusinessOf(tenant);
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, tenant);
        var closed = await context.TenantSettings.AsNoTracking().Where(setting => setting.TenantId == tenantId && setting.Key == TenantSettingDefinitions.ClosedOnPublicHolidays.Key).Select(setting => setting.Value).SingleOrDefaultAsync(Ct);
        Assert.Equal(business.ClosedOnPublicHolidays, closed == TenantSettingDefinitions.FormatSwitch(true));
        Assert.Equal(tenant is LindaleDemo.TenantName or NordbyggDemo.TenantName, business.ClosedOnPublicHolidays);
        Assert.True(await context.ClosedDays.AsNoTracking().CountAsync(day => day.TenantId == tenantId, Ct) >= 2);

        if (business.ClosedOnPublicHolidays)
        {
            var holidays = new TenantHolidays(HolidayCalendar.Sweden, ClosedOnHolidays: true).Between(Today.AddDays(-35), Today.AddDays(70)).Select(holiday => holiday.Date).ToHashSet();
            var booked = await context.SlotAssignments.AsNoTracking()
                .Where(assignment => assignment.TenantId == tenantId && assignment.State == SlotAssignmentState.Assigned && assignment.Slot.Job.Schedule == JobSchedule.Single)
                .Select(assignment => assignment.Slot.Job.PeriodStart)
                .ToListAsync(Ct);
            Assert.DoesNotContain(booked, holidays.Contains);
        }
    }

    [Fact]
    public async Task Lindale_keeps_at_least_twenty_jobs_this_week_and_next_on_its_coordination_workspace_with_both_weeks_marked()
    {
        await using var senior = await DemoSessions.SignInAsync(this.start.App.Services, LindaleDemo.Business.SeniorCoordinator.Email);
        var read = await senior.ServiceProvider.GetRequiredService<CoordinationService>().ReadAsync(Ct);
        Assert.True(read.IsSuccess, read.Error);
        var jobsShown = read.Value!.All.Select(row => row.JobId).Distinct().Count();
        Assert.True(jobsShown >= 20, $"Lindale shows {jobsShown} jobs on its workspace.");

        var monday = ScheduleRules.MondayOf(read.Value.Today);
        Assert.NotEmpty(read.Value.DayMarks(monday, monday.AddDays(6)));
        Assert.NotEmpty(read.Value.DayMarks(monday.AddDays(7), monday.AddDays(13)));
    }

    [Fact]
    public async Task Lindales_owners_hold_every_permission_its_senior_coordinator_work_patterns_and_roles_and_its_junior_coordinators_coordination_alone()
    {
        var business = LindaleDemo.Business;
        RolePermission[] coordination = [RolePermission.ViewCoordination, RolePermission.PerformCoordination, RolePermission.JobsEdit, RolePermission.ManageResourceAbsence];
        RolePermission[] patternsAndRoles = [RolePermission.ManageWorkPatterns, RolePermission.AssignWorkPatterns, RolePermission.RolesEdit];

        foreach (var owner in business.Owners)
        {
            Assert.All(await this.PermissionsAsync(owner, Enum.GetValues<RolePermission>()), held =>
                Assert.Equal(RolePermissions.Grantable.Contains(held.Permission) && !RolePermissions.IsPersonal(held.Permission) && !RolePermissions.IsPortal(held.Permission), held.Held));
        }

        Assert.All(await this.PermissionsAsync(business.SeniorCoordinator, [.. coordination, .. patternsAndRoles]), held => Assert.True(held.Held, $"{business.SeniorCoordinator.Email} {held.Permission}"));
        foreach (var junior in business.JuniorCoordinators)
        {
            Assert.All(await this.PermissionsAsync(junior, coordination), held => Assert.True(held.Held, $"{junior.Email} {held.Permission}"));
            Assert.All(await this.PermissionsAsync(junior, patternsAndRoles), held => Assert.False(held.Held, $"{junior.Email} {held.Permission}"));
        }
    }

    [Fact]
    public async Task Lindales_coordinators_and_staff_members_talk_on_assignments_and_its_portals_orderer_and_a_coordinator_in_the_customer_thread()
    {
        var business = LindaleDemo.Business;
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, business.TenantName);
        var portalId = await TenantIdAsync(context, business.Portal.Customer);
        var ids = await context.Users.AsNoTracking().Where(user => (user.TenantId == tenantId || user.TenantId == portalId) && user.Email != null).ToDictionaryAsync(user => user.Email!, user => user.Id, StringComparer.OrdinalIgnoreCase, Ct);
        var coordinators = business.Coordinators.Select(person => ids[person.Email]).ToHashSet();
        var staff = business.StaffMembers.Select(member => ids[member.Person.Email]).ToHashSet();

        var comments = await context.Comments.AsNoTracking()
            .Where(comment => comment.TenantId == tenantId || comment.TenantId == portalId)
            .Select(comment => new { comment.TenantId, comment.CommentContainer!.Audience, comment.CreatedById })
            .ToListAsync(Ct);
        Assert.Contains(comments, comment => comment.TenantId == tenantId && comment.Audience == CommentAudience.Staff && coordinators.Contains(comment.CreatedById));
        Assert.Contains(comments, comment => comment.TenantId == tenantId && comment.Audience == CommentAudience.Staff && staff.Contains(comment.CreatedById));
        Assert.Contains(comments, comment => comment.TenantId == tenantId && comment.Audience == CommentAudience.Customer && coordinators.Contains(comment.CreatedById));
        Assert.Contains(comments, comment => comment.TenantId == portalId && comment.Audience == CommentAudience.Customer && comment.CreatedById == ids[business.Portal.Orderer.Email]);
    }

    [Fact]
    public async Task Lindales_portal_holds_the_offered_services_and_its_orders_with_lines_one_confirmed_one_being_staffed_and_three_waiting()
    {
        var business = LindaleDemo.Business;
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, business.TenantName);
        var portal = await context.Tenants.AsNoTracking().SingleAsync(row => row.Name == business.Portal.Customer, Ct);
        Assert.Equal((tenantId, TenantState.Active), (portal.ParentTenantId!.Value, portal.State));
        Assert.Equal(
            new[] { business.Portal.CustomerAdministrator.Email, business.Portal.Orderer.Email }.Order(StringComparer.OrdinalIgnoreCase),
            (await context.Users.AsNoTracking().Where(user => user.TenantId == portal.Id && !user.IsSystemAccount).Select(user => user.Email!).ToListAsync(Ct)).Order(StringComparer.OrdinalIgnoreCase));

        var copies = await context.OfferedServices.AsNoTracking().Where(copy => copy.TenantId == portal.Id && copy.Offered).Select(copy => copy.Name).ToListAsync(Ct);
        Assert.Equal(business.Services.Where(service => service.OfferedInPortal).Select(service => service.Name).Order(), copies.Order());

        var orders = await context.Orders.AsNoTracking().Where(order => order.TenantId == portal.Id).Select(order => new { order.Name, order.State, People = order.Lines.Sum(line => line.Count) }).ToListAsync(Ct);
        Assert.Equal(2 + DemoTenantBuilder.WaitingOrders, orders.Count);
        Assert.Contains(orders, order => order.Name == business.Portal.ConfirmedOrder.Name && order.State == OrderState.Confirmed && order.People == business.Portal.ConfirmedOrder.Lines.Sum(line => line.Count));
        Assert.Contains(orders, order => order.Name == business.Portal.StaffingOrder.Name && order.State == OrderState.Coordinating && order.People == business.Portal.StaffingOrder.Lines.Sum(line => line.Count));
        foreach (var order in new[] { business.Portal.ConfirmedOrder, business.Portal.StaffingOrder })
        {
            var job = await context.Jobs.AsNoTracking().SingleAsync(row => row.TenantId == tenantId && row.Name == order.Name, Ct);
            var slots = await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == job.Id).ToListAsync(Ct);
            Assert.Equal(order.Lines.Sum(line => line.Count), slots.Count);
            Assert.All(slots, slot => Assert.NotNull(slot.ServiceId));
        }
    }

    [Fact]
    public async Task Lindales_first_to_accept_round_filled_a_slot_group_its_other_requests_not_selected_and_its_expired_requests_expired_by_the_job_clock()
    {
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, LindaleDemo.TenantName);
        var filled = await context.RequestRounds.AsNoTracking()
            .Where(round => round.TenantId == tenantId && round.State == RequestRoundState.Filled && round.FirstToAccept)
            .Select(round => new { round.JobSlotId, round.Slot.JobId, round.Slot.ServiceId, Requests = round.Requests.Select(request => request.State).ToList() })
            .ToListAsync(Ct);
        var round = Assert.Single(filled);

        var group = await context.JobSlots.AsNoTracking().Where(slot => slot.JobId == round.JobId && slot.ServiceId == round.ServiceId && slot.State == JobSlotState.Active).Select(slot => slot.Id).ToListAsync(Ct);
        var holders = await context.SlotAssignments.AsNoTracking().CountAsync(assignment => group.Contains(assignment.JobSlotId) && assignment.State == SlotAssignmentState.Assigned, Ct);
        Assert.True(group.Count >= 2, $"a group of {group.Count}");
        Assert.Equal(group.Count, holders);
        Assert.Equal(group.Count, round.Requests.Count(state => state == SlotAssignmentState.Assigned));
        Assert.Contains(SlotAssignmentState.NotSelected, round.Requests);

        var expired = await context.SlotAssignments.AsNoTracking().Where(request => request.TenantId == tenantId && request.State == SlotAssignmentState.Expired).Select(request => new { request.Id, request.RespondBy }).ToListAsync(Ct);
        Assert.NotEmpty(expired);
        Assert.All(expired, request => Assert.True(request.RespondBy < anchor.UtcDateTime, "its answer-by time passed"));
        var expirySteps = await context.StateHistoryEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && (entry.TransitionKey == SlotAssignmentWorkflowAttribute.ExpireStep || entry.TransitionKey == RequestRoundWorkflowAttribute.ExpireStep))
            .Select(entry => entry.EntityId)
            .ToListAsync(Ct);
        Assert.All(expired, request => Assert.Contains(request.Id, expirySteps));
    }

    [Fact]
    public async Task Lindale_has_its_city_units_below_the_root_and_places_its_owners_coordinators_staff_portal_customer_and_jobs_on_them()
    {
        var business = LindaleDemo.Business;
        await using var context = this.start.Database.CreateSystemContext();
        var tenantId = await TenantIdAsync(context, business.TenantName);
        var units = await context.OrganizationUnits.AsNoTracking().Where(unit => unit.TenantId == tenantId).ToListAsync(Ct);
        var root = Assert.Single(units, unit => unit.UnitType == OrganizationUnitType.Company);
        var byName = units.ToDictionary(unit => unit.Name, StringComparer.Ordinal);

        Assert.Equal(new[] { "Göteborg", "Stockholm", "Örebro" }.Order(StringComparer.Ordinal), units.Where(unit => unit.ParentId == root.Id).Select(unit => unit.Name).Order(StringComparer.Ordinal));
        Assert.Equal(business.Units.Count + 1, units.Count);
        foreach (var unit in business.Units)
        {
            var stored = byName[unit.Name];
            Assert.Equal((unit.Type, OrganizationUnitState.Active, unit.Parent is { } parent ? byName[parent].Id : root.Id), (stored.UnitType, stored.State, stored.ParentId!.Value));
        }

        Assert.Equal(byName[LindaleDemo.Stockholm].Id, byName[LindaleDemo.SupportTeam].ParentId);

        var holdings = await context.OrganizationUnitUserRoles.AsNoTracking()
            .Where(holding => holding.TenantId == tenantId)
            .Select(holding => new { Email = holding.User.Email!, holding.OrganizationUnitId })
            .ToListAsync(Ct);
        List<Guid> HeldBy(DemoPerson person) => [.. holdings.Where(holding => string.Equals(holding.Email, person.Email, StringComparison.OrdinalIgnoreCase)).Select(holding => holding.OrganizationUnitId)];
        Assert.All(business.Owners, owner => Assert.Equal([root.Id], HeldBy(owner)));
        Assert.All(business.Coordinators.Concat(business.StaffMembers.Select(staff => staff.Person)), person => Assert.Equal([byName[business.UnitOf(person)!].Id], HeldBy(person)));

        var memberships = await context.ResourceUnitMemberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenantId)
            .Select(membership => new { Email = membership.Resource.User.Email!, membership.OrganizationUnitId })
            .ToListAsync(Ct);
        Assert.All(business.StaffMembers, staff => Assert.Equal(
            [byName[staff.Unit].Id],
            memberships.Where(membership => string.Equals(membership.Email, staff.Person.Email, StringComparison.OrdinalIgnoreCase)).Select(membership => membership.OrganizationUnitId)));

        var portalCustomer = business.Customers.Single(customer => customer.Name == business.Portal.Customer);
        Assert.Equal(byName[portalCustomer.Unit].Id, await context.Customers.AsNoTracking().Where(customer => customer.TenantId == tenantId && customer.Name == portalCustomer.Name).Select(customer => customer.DefaultUnitId).SingleAsync(Ct));

        var jobs = await context.Jobs.AsNoTracking().Where(job => job.TenantId == tenantId).Select(job => new { Customer = job.Customer.Name, job.OrganizationUnitId, job.City }).ToListAsync(Ct);
        Assert.All(jobs, job => Assert.Equal(byName[business.Customers.Single(customer => customer.Name == job.Customer).Unit].Id, job.OrganizationUnitId));
        Assert.All(jobs, job => Assert.Equal(business.CityOf(business.Customers.Single(customer => customer.Name == job.Customer).Unit), job.City));
        Assert.All(new[] { LindaleDemo.Orebro, LindaleDemo.Stockholm, LindaleDemo.Goteborg }, city => Assert.Contains(jobs, job => job.OrganizationUnitId == byName[city].Id));
    }

    [Fact]
    public async Task Every_demo_person_exists_and_Lindales_people_and_every_owner_and_portal_person_sign_in_with_the_demo_password_which_no_log_line_names()
    {
        var people = DemoDataSeeder.Businesses.SelectMany(business => business.People).ToList();
        Assert.Equal(people.Count, people.Select(person => person.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(people, person => Assert.Matches(@"^[a-z\-]+\.[a-z\-]+@[a-z]+\.example\.com$", person.Email));

        await using (var context = this.start.Database.CreateSystemContext())
        {
            var stored = (await context.Users.AsNoTracking().Where(user => user.Email != null).Select(user => user.Email!).ToListAsync(Ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(people, person => Assert.Contains(person.Email, stored));
        }

        await using var scope = this.start.App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signing = LindaleDemo.Business.People.Concat(DemoDataSeeder.Businesses.SelectMany(business => business.Owners.Append(business.Portal.CustomerAdministrator).Append(business.Portal.Orderer))).Distinct();
        foreach (var person in signing)
        {
            var user = await users.FindByEmailAsync(person.Email);
            Assert.NotNull(user);
            Assert.True(await users.CheckPasswordAsync(user, DemoDataSeeder.PasswordOf(person)), person.Email);
        }

        Assert.All(DemoDataSeeder.Businesses, business => Assert.All(business.Owners, owner => Assert.Equal(DemoDataSeeder.OwnerPassword, DemoDataSeeder.PasswordOf(owner))));
        Assert.All(DemoDataSeeder.Businesses, business => Assert.Equal(DemoDataSeeder.OwnerPassword, DemoDataSeeder.PasswordOf(business.Portal.CustomerAdministrator)));
        Assert.All(DemoDataSeeder.Businesses, business => Assert.Equal(DemoDataSeeder.MemberPassword, DemoDataSeeder.PasswordOf(business.SeniorCoordinator)));

        Assert.Equal(DemoDataSeeder.Businesses.Count, this.start.Log.Lines.Count(line => line.Contains("Built the demo tenant", StringComparison.Ordinal)));
        Assert.DoesNotContain(this.start.Log.Lines, line => sourcePasswords.Any(password => line.Contains(password, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_second_development_start_adds_nothing()
    {
        var afterFirstStart = await SeedingHost.SnapshotAsync(this.start.Database, Ct);
        Assert.NotEmpty(afterFirstStart);
        await using (StartDevelopment(this.start.Database))
        {
        }

        Assert.Equal(afterFirstStart, await SeedingHost.SnapshotAsync(this.start.Database, Ct));
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task The_demo_refuses_to_run_outside_development_and_no_other_start_builds_it(string environment)
    {
        await using var fresh = await this.database.CreateIsolatedAsync("demorefused", Ct);

        await using (var app = AppHost.Start(fresh.ConnectionString, environment, configure: AnchorClock))
        {
            await using var scope = app.Services.CreateAsyncScope();
            var registered = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(Guid.NewGuid(), Ct);
            var production = await ActivatorUtilities.CreateInstance<DemoDataSeeder>(scope.ServiceProvider, new HostingEnvironment { EnvironmentName = Environments.Production }).SeedAsync(Guid.NewGuid(), Ct);
            Assert.Equal(DemoDataSeeder.NotDevelopment, registered.Error);
            Assert.Equal(DemoDataSeeder.NotDevelopment, production.Error);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoSessions.SignInAsync(scope.ServiceProvider, LindaleDemo.Business.Owners[0].Email));
            Assert.Equal(DemoSessions.NotDevelopment, refused.Message);
        }

        await using (var seeded = fresh.CreateSystemContext())
        {
            Assert.Equal(0, await seeded.Tenants.CountAsync(Ct));
            Assert.Equal(0, await seeded.Users.CountAsync(Ct));
            Assert.True(await seeded.Roles.AnyAsync(role => role.Name == DatabaseSeeder.AdminRole, Ct), "the production data was seeded");
        }

        await using (AppHost.Start(fresh.ConnectionString, "Development", new Dictionary<string, string> { [StartupSeeding.SkipSetting] = "1" }, AnchorClock))
        {
        }

        await using var context = fresh.CreateSystemContext();
        Assert.Equal(0, await context.Tenants.CountAsync(Ct));
        Assert.Equal(0, await context.Users.CountAsync(Ct));
    }

    private static DemoBusiness BusinessOf(string tenant) => DemoDataSeeder.Businesses.Single(business => business.TenantName == tenant);

    private static async Task<Guid> TenantIdAsync(TestDataContext context, string name) =>
        await context.Tenants.AsNoTracking().Where(tenant => tenant.Name == name).Select(tenant => tenant.Id).SingleAsync(Ct);

    private static async Task<Dictionary<Guid, int>> OpenSlotsByJobAsync(TestDataContext context, Guid tenantId)
    {
        var live = new[] { JobState.Coordinating, JobState.Confirmed, JobState.InProgress };
        var slots = await context.JobSlots.AsNoTracking()
            .Where(slot => slot.TenantId == tenantId && slot.State == JobSlotState.Active && live.Contains(slot.Job.State))
            .Select(slot => new { slot.JobId, Held = context.SlotAssignments.Any(assignment => assignment.JobSlotId == slot.Id && assignment.State == SlotAssignmentState.Assigned) })
            .ToListAsync(Ct);
        return slots.GroupBy(slot => slot.JobId).ToDictionary(job => job.Key, job => job.Count(slot => !slot.Held));
    }

    private async Task<List<(RolePermission Permission, bool Held)>> PermissionsAsync(DemoPerson person, IEnumerable<RolePermission> permissions)
    {
        await using var session = await DemoSessions.SignInAsync(this.start.App.Services, person.Email);
        var service = session.ServiceProvider.GetRequiredService<PermissionService>();
        var held = new List<(RolePermission, bool)>();
        foreach (var permission in permissions.Where(permission => permission != RolePermission.None))
        {
            held.Add((permission, await service.HasPermissionAsync(permission, Ct)));
        }

        return held;
    }

    private static AppHost StartDevelopment(RealDatabase target) => AppHost.Start(target.ConnectionString, "Development", configure: AnchorClock);

    private static void AnchorClock(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new AnchoredClock(anchor));
    }

    public sealed class DevelopmentStart : IAsyncLifetime
    {
        private readonly DatabasePool pool;
        private RealDatabase? database;
        private AppHost? app;

        public DevelopmentStart(DatabasePool pool)
        {
            this.pool = pool;
        }

        public RealDatabase Database => this.database ?? throw new InvalidOperationException("The demo's database is made when the class fixture starts.");

        public AppHost App => this.app ?? throw new InvalidOperationException("The app starts when the class fixture starts.");

        public CapturedLog Log { get; } = new();

        public async ValueTask InitializeAsync()
        {
            this.database = await new RealDatabase(this.pool).CreateIsolatedAsync("demo", TestContext.Current.CancellationToken);
            this.app = AppHost.Start(this.database.ConnectionString, "Development", configure: services =>
            {
                AnchorClock(services);
                this.Log.AddTo(services);
            });
        }

        public async ValueTask DisposeAsync()
        {
            if (this.app is not null)
            {
                await this.app.DisposeAsync();
            }

            if (this.database is not null)
            {
                await this.database.DisposeAsync();
            }
        }
    }

    public sealed class CapturedLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public IReadOnlyList<string> Lines => [.. this.lines];

        public void AddTo(IServiceCollection services) =>
            services.AddLogging(logging => logging.AddProvider(this).AddFilter<CapturedLog>(null, LogLevel.Trace));

        public ILogger CreateLogger(string categoryName) => new Recorder(this.lines);

        public void Dispose()
        {
        }

        private sealed class Recorder : ILogger
        {
            private readonly ConcurrentQueue<string> lines;

            public Recorder(ConcurrentQueue<string> lines)
            {
                this.lines = lines;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                this.lines.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
