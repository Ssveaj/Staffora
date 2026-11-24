using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Jobs;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Settings;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Catalogue;
using Staffora.Services.Customers;
using Staffora.Services.Integration;
using Staffora.Services.Jobs;
using Staffora.Services.Portal;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using static Staffora.Services.Demo.DemoSteps;

namespace Staffora.Services.Demo;

internal sealed partial class DemoTenantBuilder : IAsyncDisposable
{
    public const string SeniorCoordinatorRole = "Senior coordinator";

    public const string JuniorCoordinatorRole = "Junior coordinator";

    public const int WeeksBack = 4;

    public const int WeeksAhead = 8;

    public static IReadOnlyList<RolePermission> SeniorCoordinatorPermissions { get; } =
        [.. RolePresets.Coordinator.Permissions, RolePermission.RolesView, RolePermission.RolesEdit];

    public static IReadOnlyList<RolePermission> JuniorCoordinatorPermissions { get; } =
        [.. RolePresets.Coordinator.Permissions.Where(permission => permission is not (RolePermission.ManageWorkPatterns or RolePermission.AssignWorkPatterns))];

    private readonly IServiceProvider services;
    private readonly DemoBusiness business;
    private readonly Func<DemoPerson, string> passwordOf;
    private readonly TimeProvider clock;
    private readonly Random random;
    private readonly DateOnly today;
    private readonly DateOnly monday;
    private readonly DateOnly firstDay;
    private readonly DateOnly lastDay;
    private readonly HashSet<DateOnly> closedDates = [];
    private readonly DemoBookings bookings = new();
    private readonly Dictionary<string, AsyncServiceScope> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> userIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> qualificationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> serviceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> customerIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> unitIds = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, DemoStaffMember> staffOf = [];
    private readonly Dictionary<string, Guid> resourceByName = new(StringComparer.Ordinal);
    private Guid tenantId;
    private Guid rootId;
    private Guid juniorRoleId;

    public DemoTenantBuilder(IServiceProvider services, DemoBusiness business, Func<DemoPerson, string> passwordOf, TimeProvider clock)
    {
        this.services = services;
        this.business = business;
        this.passwordOf = passwordOf;
        this.clock = clock;
        this.random = new Random(business.Seed);
        this.today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        this.monday = ScheduleRules.MondayOf(this.today);
        this.firstDay = this.monday.AddDays(-7 * WeeksBack);
        this.lastDay = this.today.AddDays(7 * WeeksAhead);
    }

    public async Task BuildAsync(Guid platformOwnerId, CancellationToken cancellationToken)
    {
        var issued = await this.OnboardAsync(platformOwnerId, cancellationToken);
        await this.SetUpAsync(issued, cancellationToken);
        await this.ActivateAsync(platformOwnerId, cancellationToken);
        await this.OrganizeAsync(cancellationToken);
        var portalPassword = await this.CatalogueAsync(cancellationToken);
        await this.OpenPortalAsync(portalPassword, cancellationToken);
        await DeliverAsync(this.services, cancellationToken);

        this.KeepClosedDays();
        var absent = this.PlanAbsence();
        await this.OrdersAsync(cancellationToken);
        await this.JobsAsync(cancellationToken);
        await this.TalksAsync(cancellationToken);
        await this.AbsenceAsync(absent, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in this.sessions.Values)
        {
            await session.DisposeAsync();
        }

        this.sessions.Clear();
    }

    public static async Task DeliverAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var dispatcher = services.GetRequiredService<IntegrationOutboxDispatcher>();
        for (var pass = 0; pass < 10; pass++)
        {
            var done = await dispatcher.RunOnceAsync(cancellationToken);
            if (done.Delivered == 0 && done.NoticesDelivered == 0)
            {
                return;
            }
        }
    }

    private async Task<string> OnboardAsync(Guid platformOwnerId, CancellationToken cancellationToken)
    {
        var owner = this.business.Owners[0];
        await using var console = await DemoSessions.SignInAsync(this.services, platformOwnerId);
        var onboarded = Expect(await console.ServiceProvider.GetRequiredService<TenantOnboardingService>().OnboardTenantAsync(
            new TenantOnboarding(this.business.TenantName, this.business.Description, owner.FirstName, owner.LastName, owner.Email),
            cancellationToken), $"onboard {this.business.TenantName}");
        this.tenantId = onboarded.TenantId;
        return onboarded.InitialPassword;
    }

    private async Task SetUpAsync(string issued, CancellationToken cancellationToken)
    {
        var owner = this.business.Owners[0];
        await using var scope = await DemoSessions.SignInAsync(this.services, owner.Email);
        var provider = scope.ServiceProvider;
        await this.ChangePasswordAsync(provider, owner, issued);

        var units = provider.GetRequiredService<OrganizationService>();
        var tree = Expect(await units.FetchUnitTreeAsync(this.tenantId), "read the tenant's tree");
        this.rootId = tree.Single(unit => unit.IsRoot).Id;
        foreach (var unit in this.business.Units)
        {
            this.unitIds[unit.Name] = Expect(await units.CreateOrganizationUnitAsync(
                new OrganizationUnit { Name = unit.Name, Description = unit.Description, UnitType = unit.Type, ParentId = this.UnitIdOf(unit.Parent) },
                OrganizationUnitWorkflowAttribute.CreateStep,
                cancellationToken), $"add the unit {unit.Name}");
        }

        var roles = provider.GetRequiredService<TenantRoleService>();
        var seniorRoleId = await CreateRoleAsync(roles, SeniorCoordinatorRole, "Coordinates and staffs jobs, makes and gives work patterns, and gives roles.", SeniorCoordinatorPermissions, cancellationToken);
        this.juniorRoleId = await CreateRoleAsync(roles, JuniorCoordinatorRole, "Coordinates and staffs jobs and registers absence. No work patterns or roles.", JuniorCoordinatorPermissions, cancellationToken);
        var presets = Expect(await roles.GetTenantRolesAsync(this.tenantId), "read the role presets");
        var staffRoleId = presets.Single(role => role.Name == RolePresets.Staff.Name && !role.IsSystemOwner).Id;
        var ownerRoleId = presets.Single(role => role.IsSystemOwner).Id;

        var users = provider.GetRequiredService<UserService>();
        var holdings = provider.GetRequiredService<RoleHoldingService>();
        var people = new List<(DemoPerson Person, Guid? Role)> { (this.business.Owners[1], ownerRoleId), (this.business.SeniorCoordinator, seniorRoleId), (this.business.JuniorCoordinators[0], this.juniorRoleId), (this.business.JuniorCoordinators[1], null) };
        people.AddRange(this.business.StaffMembers.Select(staff => (staff.Person, (Guid?)staffRoleId)));
        foreach (var (person, role) in people)
        {
            var created = Expect(await users.CreateUserAsync(new ApplicationUser { FirstName = person.FirstName, LastName = person.LastName, Email = person.Email, UserName = person.Email }, this.tenantId, this.passwordOf(person)), $"create {person.Email}");
            this.userIds[person.Email] = created.Id;
            if (role is { } roleId)
            {
                Expect(await holdings.AssignUserToRoleAsync(created.Id, this.UnitIdOf(role == ownerRoleId ? null : this.business.UnitOf(person)), roleId, this.tenantId), $"give {person.Email} a role");
            }
        }

        var closedDays = provider.GetRequiredService<ClosedDayService>();
        foreach (var closed in this.business.ClosedDays)
        {
            Expect(await closedDays.CreateClosedDayAsync(new ClosedDayDraft(closed.Name, this.monday.AddDays(closed.FirstDayFromMonday), this.monday.AddDays(closed.LastDayFromMonday), closed.Unit is null ? null : this.unitIds[closed.Unit]), cancellationToken), $"add the closed day {closed.Name}");
        }

        if (this.business.ClosedOnPublicHolidays)
        {
            var settings = provider.GetRequiredService<TenantSettingsService>();
            var key = TenantSettingDefinitions.ClosedOnPublicHolidays.Key;
            var loaded = Expect(await settings.LoadAsync(cancellationToken), "read the settings").Settings.Single(setting => setting.Definition.Key == key);
            Expect(await settings.SaveAsync(key, TenantSettingDefinitions.FormatSwitch(true), loaded.Version, cancellationToken), "close on public holidays");
        }

        var qualifications = provider.GetRequiredService<QualificationService>();
        foreach (var name in this.business.Qualifications)
        {
            this.qualificationIds[name] = Expect(await qualifications.CreateQualificationAsync(new Qualification { Name = name }, QualificationWorkflowAttribute.CreateStep, cancellationToken), $"add the qualification {name}");
        }

        foreach (var staff in this.business.StaffMembers)
        {
            foreach (var name in staff.Qualifications)
            {
                Expect(await qualifications.GiveQualificationAsync(this.userIds[staff.Person.Email], this.qualificationIds[name], cancellationToken), $"give {staff.Person.Email} {name}");
            }
        }
    }

    private async Task ActivateAsync(Guid platformOwnerId, CancellationToken cancellationToken)
    {
        await using var console = await DemoSessions.SignInAsync(this.services, platformOwnerId);
        var tenants = console.ServiceProvider.GetRequiredService<TenantService>();
        var updater = Expect(await tenants.LoadTenantUpdaterAsync(this.tenantId, cancellationToken), $"open {this.business.TenantName}");
        Expect(await tenants.UpdateTenantAndTransitionAsync(updater, TenantWorkflowAttribute.ActivateStep, comment: null, cancellationToken), $"activate {this.business.TenantName}");
    }

    private async Task OrganizeAsync(CancellationToken cancellationToken)
    {
        var provider = await this.SessionAsync(this.business.SeniorCoordinator);
        var junior = this.business.JuniorCoordinators[1];
        Expect(await provider.GetRequiredService<RoleHoldingService>().AssignUserToRoleAsync(this.userIds[junior.Email], this.UnitIdOf(this.business.UnitOf(junior)), this.juniorRoleId, this.tenantId), $"give {junior.Email} the {JuniorCoordinatorRole} role");

        var patterns = provider.GetRequiredService<WorkPatternService>();
        var patternIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var pattern in this.business.WorkPatterns)
        {
            var draft = new WorkPattern { Name = pattern.Name, CycleWeeks = 1 };
            foreach (var day in pattern.Days.Select(day => ((int)day + 6) % 7))
            {
                if (pattern.Overnight)
                {
                    draft.Segments.Add(new WorkPatternSegment { DayIndex = day, Start = pattern.Start, End = new TimeOnly(23, 59) });
                    draft.Segments.Add(new WorkPatternSegment { DayIndex = (day + 1) % 7, Start = TimeOnly.MinValue, End = pattern.End });
                    continue;
                }

                draft.Segments.Add(new WorkPatternSegment { DayIndex = day, Start = pattern.Start, End = pattern.End });
            }

            var id = Expect(await patterns.CreateWorkPatternAsync(draft, WorkPatternWorkflowAttribute.SaveStep, cancellationToken), $"save the pattern {pattern.Name}");
            var updater = Expect(await patterns.LoadWorkPatternUpdaterAsync(id, cancellationToken), $"open the pattern {pattern.Name}");
            Expect(await patterns.UpdateWorkPatternAndTransitionAsync(updater, WorkPatternWorkflowAttribute.PublishStep, null, cancellationToken), $"publish the pattern {pattern.Name}");
            patternIds[pattern.Name] = id;
        }

        var assignments = provider.GetRequiredService<PatternAssignmentService>();
        foreach (var staff in this.business.StaffMembers)
        {
            var fresh = Expect(await assignments.NewResourcePatternAssignmentAsync(this.userIds[staff.Person.Email], cancellationToken), $"prepare the pattern of {staff.Person.Email}");
            fresh.WorkPatternId = patternIds[staff.Pattern];
            fresh.EffectiveFrom = this.firstDay.AddDays(-28);
            fresh.CycleAnchor = this.firstDay.AddDays(-28);
            var id = Expect(await assignments.CreateResourcePatternAssignmentAsync(fresh, ResourcePatternAssignmentWorkflowAttribute.SaveStep, cancellationToken), $"save the pattern of {staff.Person.Email}");
            var updater = Expect(await assignments.LoadResourcePatternAssignmentUpdaterAsync(id, cancellationToken), $"open the pattern of {staff.Person.Email}");
            Expect(await assignments.UpdateResourcePatternAssignmentAndTransitionAsync(updater, ResourcePatternAssignmentWorkflowAttribute.ActivateStep, null, cancellationToken), $"activate the pattern of {staff.Person.Email}");
        }

        var customers = provider.GetRequiredService<CustomerService>();
        foreach (var customer in this.business.Customers)
        {
            this.customerIds[customer.Name] = Expect(await customers.CreateCustomerAsync(new Customer { Name = customer.Name, Description = $"A customer in {this.business.CityOf(customer.Unit)}." }, CustomerWorkflowAttribute.ActivateStep, cancellationToken), $"add the customer {customer.Name}");
        }

        var rows = Expect(await provider.GetRequiredService<ResourceService>().QueryStaffMembersAsync(new ListQuery { PageSize = ListDefinition.MaxPageSize }, cancellationToken), "read the staff members").Rows;
        foreach (var staff in this.business.StaffMembers)
        {
            var resource = rows.Single(row => string.Equals(row.Email, staff.Person.Email, StringComparison.OrdinalIgnoreCase)).ResourceId;
            this.staffOf[resource] = staff;
            this.resourceByName[staff.Person.Name] = resource;
        }
    }

    private async Task<string> CatalogueAsync(CancellationToken cancellationToken)
    {
        var provider = await this.SessionAsync(this.business.Owners[0]);
        var catalogue = provider.GetRequiredService<ServiceCatalogueService>();
        foreach (var service in this.business.Services)
        {
            var draft = new Service { Name = service.Name, Description = service.Description, DefaultSlotCount = service.DefaultCount, OfferedInPortal = service.OfferedInPortal };
            foreach (var requirement in service.Requirements)
            {
                draft.Qualifications.Add(new ServiceQualification { QualificationId = this.qualificationIds[requirement.Qualification], Level = requirement.Level });
            }

            this.serviceIds[service.Name] = Expect(await catalogue.CreateServiceAsync(draft, ServiceWorkflowAttribute.CreateStep, cancellationToken), $"add the service {service.Name}");
        }

        var portal = this.business.Portal;
        var customers = provider.GetRequiredService<CustomerService>();
        var updater = Expect(await customers.LoadCustomerUpdaterAsync(this.customerIds[portal.Customer], cancellationToken), $"open {portal.Customer}");
        var admin = portal.CustomerAdministrator;
        var unit = this.business.Customers.Single(customer => customer.Name == portal.Customer).Unit;
        return Expect(await customers.EnableCustomerPortalAsync(updater, new CustomerPortalRequest(this.unitIds[unit], admin.FirstName, admin.LastName, admin.Email), cancellationToken), $"enable the portal of {portal.Customer}").InitialPassword;
    }

    private async Task OpenPortalAsync(string issued, CancellationToken cancellationToken)
    {
        var portal = this.business.Portal;
        await using var admin = await DemoSessions.SignInAsync(this.services, portal.CustomerAdministrator.Email);
        var provider = admin.ServiceProvider;
        await this.ChangePasswordAsync(provider, portal.CustomerAdministrator, issued);
        var portalTenantId = provider.GetRequiredService<IRuntimeExecutionContext>().TenantId!.Value;
        Expect(await provider.GetRequiredService<UserService>().CreateUserAsync(
            new ApplicationUser { FirstName = portal.Orderer.FirstName, LastName = portal.Orderer.LastName, Email = portal.Orderer.Email, UserName = portal.Orderer.Email },
            portalTenantId,
            this.passwordOf(portal.Orderer),
            ApplicationUserWorkflowAttribute.PortalCreateOrdererStep), $"create the orderer {portal.Orderer.Email}");
    }

    private async Task OrdersAsync(CancellationToken cancellationToken)
    {
        var portal = this.business.Portal;
        var place = this.business.PlacesOf(this.business.Customers.Single(customer => customer.Name == portal.Customer))[0];
        var orders = (await this.SessionAsync(portal.Orderer)).GetRequiredService<OrderService>();
        var offered = Expect(await orders.GetOrderEditorOptionsAsync(null, cancellationToken), "read the services offered").Services;
        foreach (var order in new[] { portal.ConfirmedOrder, portal.StaffingOrder })
        {
            var id = await SendOrderAsync(orders, order.Name, order.Description, this.DayAhead(order.DaysAhead), new DemoShift(order.Start, order.End), order.Lines, place, offered, cancellationToken);
            Expect(await orders.AddOrderCommentAsync(id, CommentAudience.Customer, order.Question, cancellationToken), $"ask in the thread of {order.Name}");
        }

        var kinds = this.business.JobKinds.Where(kind => this.ServiceOf(kind.Service).OfferedInPortal).ToList();
        for (var index = 0; index < WaitingOrders; index++)
        {
            var kind = kinds[this.random.Next(kinds.Count)];
            var name = kind.NamePrefix + kind.Variants[this.random.Next(kind.Variants.Count)];
            var day = this.DayAhead(this.random.Next(4, 20));
            var shift = this.business.Shifts[this.random.Next(this.business.Shifts.Count)];
            var where = this.business.PlacesOf(this.business.Customers.Single(customer => customer.Name == portal.Customer));
            await SendOrderAsync(orders, name, kind.Description, day, shift, [new DemoOrderLine(kind.Service, this.ServiceOf(kind.Service).DefaultCount)], where[this.random.Next(where.Count)], offered, cancellationToken);
        }

        await DeliverAsync(this.services, cancellationToken);
        var coordinator = this.CoordinatorFor(portal.Customer);
        var provider = await this.SessionAsync(coordinator);
        var jobs = provider.GetRequiredService<JobService>();
        var staffing = provider.GetRequiredService<SlotAssignmentService>();
        foreach (var (order, confirm) in new[] { (portal.ConfirmedOrder, true), (portal.StaffingOrder, false) })
        {
            var jobId = await FindJobAsync(jobs, order.Name, cancellationToken);
            await StepAsync(jobs, jobId, JobWorkflowAttribute.StartStep, null, cancellationToken);
            var day = this.DayAhead(order.DaysAhead);
            var shift = new DemoShift(order.Start, order.End);
            var plan = new PlannedJob(order.Name, null, portal.Customer, false, day, JobRules.SingleLastDay(day, shift.Start, shift.End), [], shift, 1, null);
            var rows = Expect(await jobs.QueryJobSlotsAsync(jobId, new ListQuery { PageSize = ListDefinition.MaxPageSize }, cancellationToken), $"read the slots of {order.Name}").Rows;
            foreach (var line in order.Lines)
            {
                var row = rows.Single(slot => slot.Name == line.Service);
                if (confirm)
                {
                    await this.AssignAsync(staffing, plan, row.Id, line.Service, line.Count, coordinator, cancellationToken);
                    continue;
                }

                await this.AssignAsync(staffing, plan, row.Id, line.Service, 1, coordinator, cancellationToken);
                if (line.Count > 1)
                {
                    await this.RequestAsync(staffing, plan, row.Id, line.Service, null, cancellationToken);
                    break;
                }
            }

            if (confirm)
            {
                await StepAsync(jobs, jobId, JobWorkflowAttribute.ConfirmStep, null, cancellationToken);
            }

            Expect(await jobs.AddJobCommentAsync(jobId, CommentAudience.Customer, order.Answer, cancellationToken), $"answer in the customer thread of {order.Name}");
        }
    }

    private void KeepClosedDays()
    {
        foreach (var closed in this.business.ClosedDays)
        {
            var (first, last) = (this.monday.AddDays(closed.FirstDayFromMonday), this.monday.AddDays(closed.LastDayFromMonday));
            if (closed.Unit is not { } unit)
            {
                for (var day = first; day <= last; day = day.AddDays(1))
                {
                    this.closedDates.Add(day);
                }

                continue;
            }

            foreach (var resource in this.staffOf.Where(entry => this.business.IsWithin(entry.Value.Unit, unit)).Select(entry => entry.Key))
            {
                this.bookings.BlockDays(resource, first, last);
            }
        }

        if (this.business.ClosedOnPublicHolidays)
        {
            this.closedDates.UnionWith(new TenantHolidays(HolidayCalendar.Sweden, ClosedOnHolidays: true).Between(this.firstDay, this.lastDay.AddDays(7 * 4)).Select(holiday => holiday.Date));
        }
    }

    private async Task<IServiceProvider> SessionAsync(DemoPerson person)
    {
        if (!this.sessions.TryGetValue(person.Email, out var session))
        {
            session = await DemoSessions.SignInAsync(this.services, person.Email);
            this.sessions[person.Email] = session;
        }

        return session.ServiceProvider;
    }

    private Guid UnitIdOf(string? unit) => unit is null ? this.rootId : this.unitIds[unit];

    private DemoCatalogueItem ServiceOf(string name) => this.business.Services.Single(service => service.Name == name);

    private Guid ServiceIdOf(string name) => this.serviceIds.TryGetValue(name, out var id) ? id : throw new InvalidOperationException($"The service {name} is not in the catalogue.");

    private Guid ResourceOf(DemoStaffMember staff) => this.resourceByName[staff.Person.Name];

    private async Task ChangePasswordAsync(IServiceProvider provider, DemoPerson person, string issued)
    {
        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(person.Email) ?? throw new InvalidOperationException($"{person.Email} does not exist.");
        Expect(await users.ChangePasswordAsync(user, issued, this.passwordOf(person)), $"change the password of {person.Email}");
    }

    private static async Task<Guid> CreateRoleAsync(TenantRoleService roles, string name, string description, IReadOnlyList<RolePermission> permissions, CancellationToken cancellationToken)
    {
        var role = new TenantRole { Name = name, Description = description };
        foreach (var permission in permissions)
        {
            role.Permissions.Add(new TenantRolePermission { Permission = permission });
        }

        return Expect(await roles.CreateTenantRoleAsync(role, TenantRoleWorkflowAttribute.CreateStep, cancellationToken), $"create the role {name}");
    }

    private static async Task<Guid> SendOrderAsync(OrderService orders, string name, string description, DateOnly day, DemoShift shift, IReadOnlyList<DemoOrderLine> lines, DemoPlace place, IReadOnlyList<OfferedServiceChoice> offered, CancellationToken cancellationToken)
    {
        var draft = Expect(await orders.NewOrderAsync(cancellationToken), "start an order");
        (draft.Name, draft.Description, draft.Schedule, draft.PeriodStart, draft.PeriodEnd, draft.StartTime, draft.EndTime) = (name, description, JobSchedule.Single, day, JobRules.SingleLastDay(day, shift.Start, shift.End), shift.Start, shift.End);
        (draft.PlaceName, draft.StreetAddress, draft.PostalCode, draft.City) = (place.Name, place.StreetAddress, place.PostalCode, place.City);
        var id = Expect(await orders.CreateOrderAsync(draft, OrderWorkflowAttribute.SaveDraftStep, cancellationToken), $"save the order {name}");

        var updater = Expect(await orders.LoadOrderUpdaterAsync(id, cancellationToken), $"open the order {name}");
        foreach (var line in lines)
        {
            var copy = offered.Single(service => service.Name == line.Service);
            updater.Entity.Lines.Add(new OrderLine { TenantId = updater.Entity.TenantId, OrderId = id, OfferedServiceId = copy.Id, Count = line.Count });
        }

        Expect(await orders.UpdateOrderAndTransitionAsync(updater, OrderWorkflowAttribute.SaveStep, null, cancellationToken), $"save the lines of {name}");
        var sending = Expect(await orders.LoadOrderUpdaterAsync(id, cancellationToken), $"open the order {name}");
        Expect(await orders.UpdateOrderAndTransitionAsync(sending, OrderWorkflowAttribute.SendStep, null, cancellationToken), $"send the order {name}");
        return id;
    }

    private static async Task<Guid> FindJobAsync(JobService jobs, string name, CancellationToken cancellationToken) =>
        Expect(await jobs.QueryJobsAsync(new ListQuery { Search = name, PageSize = ListDefinition.MaxPageSize }, cancellationToken), $"find the job {name}").Rows.Single(row => row.Name == name).Id;

    private static async Task StepAsync(JobService jobs, Guid job, string step, string? reason, CancellationToken cancellationToken)
    {
        var updater = Expect(await jobs.LoadJobUpdaterAsync(job, cancellationToken), "open a job");
        Expect(await jobs.UpdateJobAndTransitionAsync(updater, step, reason, cancellationToken), $"take the step {step}");
    }
}
