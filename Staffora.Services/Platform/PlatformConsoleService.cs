using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Settings;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Diagnostics;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;

namespace Staffora.Services.Platform;

public sealed partial class PlatformConsoleService
{
    public const string ConsoleOnly = "Only a platform owner on the platform console, not impersonating, sees this.";

    public const string TenantNotFound = TenantService.TenantNotFound;

    public const string TenantUnreadable = "The tenant could not be read. Try again.";

    public const int RecentCount = 5;

    public const int RecentErrorDays = 7;

    public const int RecentTargetCount = 3;

    private const int RecentTargetWindow = 20;

    private static readonly JobState[] WeekJobStates = [JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress, JobState.Completed];

    private static readonly JobState[] LiveJobStates = [JobState.Registration, JobState.AwaitingCoordination, JobState.Coordinating, JobState.Confirmed, JobState.InProgress];

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly UserService users;
    private readonly TimeProvider clock;
    private readonly ErrorLogQueue queue;
    private readonly Retention retention;
    private readonly IOptions<PlatformOptions> platform;
    private readonly ILogger<PlatformConsoleService> logger;

    public PlatformConsoleService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, UserService users, TimeProvider clock, ErrorLogQueue queue, Retention retention, IOptions<PlatformOptions> platform, ILogger<PlatformConsoleService> logger)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.users = users;
        this.clock = clock;
        this.queue = queue;
        this.retention = retention;
        this.platform = platform;
        this.logger = logger;
    }

    public async Task<Result<PlatformOverview>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<PlatformOverview>.Failure(ConsoleOnly);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "read the platform overview");
            if (lease is null)
            {
                return Result<PlatformOverview>.Failure(ConsoleOnly);
            }

            var zone = IanaTimeZones.TryFind(this.platform.Value.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
            var now = this.clock.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var dayStart = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), zone);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var (tuesday, wednesday, thursday, friday, saturday, sunday) = (monday.AddDays(1), monday.AddDays(2), monday.AddDays(3), monday.AddDays(4), monday.AddDays(5), monday.AddDays(6));
            var weekAgo = nowUtc.AddDays(-RecentErrorDays);
            var dayAgo = nowUtc.AddDays(-1);

            var tenants = await context.Tenants.AsNoTracking()
                .Select(tenant => new
                {
                    tenant.Id,
                    tenant.Name,
                    tenant.Description,
                    tenant.State,
                    tenant.IsRootTenant,
                    tenant.ParentTenantId,
                    tenant.Created,
                    Users = context.Users.Count(user => user.TenantId == tenant.Id && !user.IsSystemAccount),
                    ActiveUsers = context.Users.Count(user => user.TenantId == tenant.Id && !user.IsSystemAccount && user.State == UserState.Active),
                })
                .ToListAsync(cancellationToken);

            var jobsThisWeek = await context.Jobs.AsNoTracking()
                .Where(job => WeekJobStates.Contains(job.State))
                .Where(job => job.Schedule == JobSchedule.Single
                    ? job.PeriodStart >= monday && job.PeriodStart <= sunday
                    : job.PeriodStart <= sunday && job.PeriodEnd >= monday && job.TimeSlots.Any(time =>
                        (time.Weekday == Weekday.Monday && job.PeriodStart <= monday && job.PeriodEnd >= monday)
                        || (time.Weekday == Weekday.Tuesday && job.PeriodStart <= tuesday && job.PeriodEnd >= tuesday)
                        || (time.Weekday == Weekday.Wednesday && job.PeriodStart <= wednesday && job.PeriodEnd >= wednesday)
                        || (time.Weekday == Weekday.Thursday && job.PeriodStart <= thursday && job.PeriodEnd >= thursday)
                        || (time.Weekday == Weekday.Friday && job.PeriodStart <= friday && job.PeriodEnd >= friday)
                        || (time.Weekday == Weekday.Saturday && job.PeriodStart <= saturday && job.PeriodEnd >= saturday)
                        || (time.Weekday == Weekday.Sunday && job.PeriodStart <= sunday && job.PeriodEnd >= sunday)))
                .GroupBy(job => job.TenantId)
                .Select(group => new { TenantId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(count => count.TenantId, count => count.Count, cancellationToken);

            var openSlots = await context.JobSlots.AsNoTracking()
                .Where(slot => slot.State == JobSlotState.Active
                    && LiveJobStates.Contains(slot.Job.State)
                    && !slot.Assignments.Any(assignment => assignment.State == SlotAssignmentState.Assigned))
                .GroupBy(slot => slot.TenantId)
                .Select(group => new { TenantId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(count => count.TenantId, count => count.Count, cancellationToken);

            var messages = await context.IntegrationMessages.AsNoTracking()
                .Where(message => message.State == IntegrationMessageState.DeadLettered
                    || (message.State == IntegrationMessageState.Delivered && message.Finished >= dayStart))
                .GroupBy(message => message.State)
                .Select(group => new { State = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);
            var deadLetters = messages.Where(group => group.State == IntegrationMessageState.DeadLettered).ToList();

            var errorCounts = await context.ErrorLogs.AsNoTracking()
                .Where(log => log.Occurred >= weekAgo)
                .GroupBy(log => log.Occurred >= dayAgo)
                .Select(group => new { LastDay = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);

            var errors = await context.ErrorLogs.AsNoTracking()
                .Where(log => log.Occurred >= weekAgo)
                .OrderByDescending(log => log.Occurred)
                .ThenByDescending(log => log.Id)
                .Take(RecentCount)
                .Select(log => new PlatformRecentError(
                    log.Id,
                    log.Occurred,
                    log.Level,
                    log.Message,
                    context.Tenants.Where(tenant => tenant.Id == log.TenantId).Select(tenant => tenant.Name).FirstOrDefault()))
                .ToListAsync(cancellationToken);
            var lastError = await context.ErrorLogs.AsNoTracking().MaxAsync(log => (DateTime?)log.Occurred, cancellationToken);

            var impersonations = await context.ImpersonationAudits.AsNoTracking()
                .OrderByDescending(audit => audit.Started)
                .ThenByDescending(audit => audit.Id)
                .Take(RecentTargetWindow)
                .Select(audit => new ImpersonationRead
                {
                    Id = audit.Id,
                    Started = audit.Started,
                    Ended = audit.Ended,
                    TargetUserId = audit.TargetUserId,
                    TargetTenantId = audit.TargetTenantId,
                    Impersonator = context.Users.Where(user => user.Id == audit.ImpersonatorId).Select(user => new PersonRead { FirstName = user.FirstName, LastName = user.LastName, Email = user.Email }).FirstOrDefault(),
                    Target = context.Users.Where(user => user.Id == audit.TargetUserId).Select(user => new PersonRead { FirstName = user.FirstName, LastName = user.LastName, Email = user.Email, State = user.State, IsSystemAccount = user.IsSystemAccount, TenantId = user.TenantId }).FirstOrDefault(),
                    TenantName = context.Tenants.Where(tenant => tenant.Id == audit.TargetTenantId).Select(tenant => tenant.Name).FirstOrDefault(),
                    TenantState = context.Tenants.Where(tenant => tenant.Id == audit.TargetTenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

            var startedToday = await context.ImpersonationAudits.AsNoTracking().CountAsync(audit => audit.Started >= dayStart, cancellationToken);
            var openToday = await context.ImpersonationAudits.AsNoTracking()
                .Where(audit => audit.Started >= dayStart && audit.Ended == null)
                .OrderByDescending(audit => audit.Started)
                .ThenByDescending(audit => audit.Id)
                .Select(audit => new ImpersonationRead
                {
                    Id = audit.Id,
                    Started = audit.Started,
                    Ended = audit.Ended,
                    TargetUserId = audit.TargetUserId,
                    TargetTenantId = audit.TargetTenantId,
                    Impersonator = context.Users.Where(user => user.Id == audit.ImpersonatorId).Select(user => new PersonRead { FirstName = user.FirstName, LastName = user.LastName, Email = user.Email }).FirstOrDefault(),
                    Target = context.Users.Where(user => user.Id == audit.TargetUserId).Select(user => new PersonRead { FirstName = user.FirstName, LastName = user.LastName, Email = user.Email }).FirstOrDefault(),
                    TenantName = context.Tenants.Where(tenant => tenant.Id == audit.TargetTenantId).Select(tenant => tenant.Name).FirstOrDefault(),
                })
                .FirstOrDefaultAsync(cancellationToken);

            var targets = impersonations
                .Where(audit => audit.Target is { State: UserState.Active, IsSystemAccount: false } target && target.TenantId == audit.TargetTenantId && audit.TenantState == TenantState.Active)
                .DistinctBy(audit => audit.TargetUserId)
                .Take(RecentTargetCount)
                .Select(audit => new PlatformRecentTarget(audit.TargetUserId, NameOf(audit.Target!.FirstName, audit.Target.LastName, audit.Target.Email, "A user"), audit.TargetTenantId, audit.TenantName!))
                .ToList();

            var businesses = tenants.Where(tenant => !tenant.IsRootTenant && tenant.ParentTenantId == null).ToList();
            var rows = businesses
                .OrderBy(tenant => tenant.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(tenant => tenant.Id)
                .Select(tenant => new PlatformOverviewTenant(tenant.Id, tenant.Name, tenant.Description, tenant.State, tenant.Users, tenant.Created)
                {
                    JobsThisWeek = jobsThisWeek.GetValueOrDefault(tenant.Id),
                    OpenSlots = openSlots.GetValueOrDefault(tenant.Id),
                })
                .ToList();

            var byState = Enum.GetValues<TenantState>()
                .Select(state => new TenantStateCount(state, businesses.Count(tenant => tenant.State == state)))
                .ToList();

            return Result<PlatformOverview>.Success(new PlatformOverview(byState, errors, [.. impersonations.Take(RecentCount).Select(Recent)])
            {
                Tenants = rows,
                ReadAt = nowUtc,
                ErrorsLastDay = errorCounts.Where(group => group.LastDay).Sum(group => group.Count),
                ErrorsLastWeek = errorCounts.Sum(group => group.Count),
                LastError = lastError,
                DeadLetters = deadLetters.Sum(group => group.Count),
                DeliveredToday = messages.Where(group => group.State == IntegrationMessageState.Delivered).Sum(group => group.Count),
                ActiveUsers = businesses.Sum(tenant => tenant.ActiveUsers),
                ImpersonationsToday = startedToday,
                OpenImpersonation = openToday is null ? null : Recent(openToday),
                RecentTargets = targets,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.logger.LogError(exception, "Reading the platform overview failed.");
            return Result<PlatformOverview>.Failure("The platform overview could not be read. Try again.");
        }
    }

    public async Task<Result<ListPage<PlatformTenantRow>>> QueryTenantsAsync(ListQuery query)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<ListPage<PlatformTenantRow>>.Failure(ConsoleOnly);
        }

        var list = PlatformTenantList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "list every tenant");
            if (lease is null)
            {
                return Result<ListPage<PlatformTenantRow>>.Failure(ConsoleOnly);
            }

            var tenants = context.Tenants.AsNoTracking().Where(tenant => !tenant.IsRootTenant && tenant.ParentTenantId == null);
            if (list.Search is { Length: > 0 } search)
            {
                tenants = tenants.Where(tenant => tenant.Name.Contains(search)
                    || (tenant.Description != null && tenant.Description.Contains(search)));
            }

            var total = await tenants.CountAsync();
            var page = ListPage<PlatformTenantRow>.PageWithin(list, total);

            var families = tenants.Select(tenant => new TenantInFamily
            {
                Id = tenant.Id,
                Name = tenant.Name,
                Description = tenant.Description,
                State = tenant.State,
                Created = tenant.Created,
                ParentTenantId = tenant.ParentTenantId,
                FamilyId = tenant.ParentTenantId ?? tenant.Id,
                FamilyName = context.Tenants.Where(company => company.Id == tenant.ParentTenantId).Select(company => company.Name).FirstOrDefault() ?? tenant.Name,
                FamilyState = context.Tenants.Where(company => company.Id == tenant.ParentTenantId).Select(company => (TenantState?)company.State).FirstOrDefault() ?? tenant.State,
                FamilyCreated = context.Tenants.Where(company => company.Id == tenant.ParentTenantId).Select(company => (DateTime?)company.Created).FirstOrDefault() ?? tenant.Created,
            });

            var order = new ListOrder<TenantInFamily>(families);
            var grouped = list.Group == PlatformTenantList.ByState;
            if (grouped)
            {
                order.By(tenant => tenant.State);
            }

            _ = list.Sort switch
            {
                PlatformTenantList.SortState => order.By(tenant => tenant.FamilyState, list.Descending).By(tenant => tenant.FamilyName),
                PlatformTenantList.SortCreated => order.By(tenant => tenant.FamilyCreated, list.Descending),
                _ => order.By(tenant => tenant.FamilyName, list.Descending),
            };

            var read = await order.By(tenant => tenant.FamilyId).By(tenant => tenant.ParentTenantId != null).By(tenant => tenant.Name).By(tenant => tenant.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(tenant => new
                {
                    tenant.Id,
                    tenant.Name,
                    tenant.Description,
                    tenant.State,
                    UserCount = context.Users.Count(user => user.TenantId == tenant.Id && !user.IsSystemAccount),
                    tenant.Created,
                    tenant.ParentTenantId,
                    tenant.FamilyName,
                })
                .ToListAsync();

            var ids = read.Select(tenant => tenant.Id).ToList();
            var managed = (await context.Tenants.AsNoTracking()
                    .Where(customer => customer.ParentTenantId != null && ids.Contains(customer.ParentTenantId.Value))
                    .OrderBy(customer => customer.Name).ThenBy(customer => customer.Id)
                    .Select(customer => new { CompanyId = customer.ParentTenantId!.Value, customer.Name })
                    .ToListAsync())
                .ToLookup(customer => customer.CompanyId, customer => customer.Name);

            var rows = read
                .Select(tenant => new PlatformTenantRow(tenant.Id, tenant.Name, tenant.Description, tenant.State, tenant.UserCount, tenant.Created)
                {
                    ManagedCustomers = [.. managed[tenant.Id]],
                    ManagedById = tenant.ParentTenantId,
                    ManagedBy = tenant.ParentTenantId is null ? null : tenant.FamilyName,
                })
                .ToList();

            if (!grouped)
            {
                return Result<ListPage<PlatformTenantRow>>.Success(ListPage<PlatformTenantRow>.Ungrouped(rows, total, page, list.PageSize));
            }

            var counts = await tenants.GroupBy(tenant => tenant.State).Select(group => new { group.Key, Count = group.Count() }).ToListAsync();
            var groups = counts.ToDictionary(
                count => PlatformTenantList.StateValue(count.Key),
                count => (PlatformTenantList.StateLabel(count.Key), count.Count));
            return Result<ListPage<PlatformTenantRow>>.Success(ListPage<PlatformTenantRow>.Grouped(rows, total, page, list.PageSize, row => PlatformTenantList.StateValue(row.State), groups));
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Listing the tenants failed.");
            return Result<ListPage<PlatformTenantRow>>.Failure("The tenants could not be read. Try again.");
        }
    }

    public async Task<Result<TenantConsoleSummary>> GetTenantSummaryAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<TenantConsoleSummary>.Failure(ConsoleOnly);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "read a tenant's console overview");
            if (lease is null)
            {
                return Result<TenantConsoleSummary>.Failure(ConsoleOnly);
            }

            var tenant = await context.Tenants.AsNoTracking()
                .Where(row => row.Id == tenantId)
                .Select(row => new { row.Id, row.Name, row.Description, row.State, row.IsRootTenant, row.Created, row.ParentTenantId })
                .FirstOrDefaultAsync(cancellationToken);
            if (tenant is null)
            {
                return Result<TenantConsoleSummary>.Failure(TenantNotFound);
            }

            var people = await context.Users.AsNoTracking()
                .Where(user => user.TenantId == tenantId && !user.IsSystemAccount)
                .GroupBy(user => user.State == UserState.Active)
                .Select(group => new { Active = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);

            var ownerIds = SystemOwners.ActiveOwnerIds(context, tenantId);
            var owners = await context.Users.AsNoTracking()
                .Where(user => user.TenantId == tenantId && ownerIds.Contains(user.Id))
                .OrderBy(user => user.FirstName).ThenBy(user => user.LastName).ThenBy(user => user.Id)
                .Select(user => new { user.Id, user.FirstName, user.LastName, user.Email, user.State })
                .ToListAsync(cancellationToken);

            var units = await context.OrganizationUnits.AsNoTracking()
                .Where(unit => unit.TenantId == tenantId)
                .OrderBy(unit => unit.Name)
                .Select(unit => new OrganizationUnitNode(unit.Id, unit.ParentId, unit.Name, unit.Description, unit.UnitType, unit.State))
                .ToListAsync(cancellationToken);

            var roles = await context.TenantRoles.AsNoTracking().CountAsync(role => role.TenantId == tenantId, cancellationToken);

            var managed = await context.Tenants.AsNoTracking()
                .Where(customer => customer.ParentTenantId == tenantId)
                .OrderBy(customer => customer.Name).ThenBy(customer => customer.Id)
                .Select(customer => new TenantConsoleLink(customer.Id, customer.Name, customer.State))
                .ToListAsync(cancellationToken);
            var managedBy = tenant.ParentTenantId is { } companyId
                ? await context.Tenants.AsNoTracking().Where(company => company.Id == companyId).Select(company => new TenantConsoleLink(company.Id, company.Name, company.State)).FirstOrDefaultAsync(cancellationToken)
                : null;
            var deadLetters = await DeadLettersOf(context, tenantId).CountAsync(cancellationToken);

            return Result<TenantConsoleSummary>.Success(new TenantConsoleSummary(
                tenant.Id,
                tenant.Name,
                tenant.Description,
                tenant.State,
                tenant.IsRootTenant,
                tenant.Created,
                people.Where(count => count.Active).Sum(count => count.Count),
                people.Where(count => !count.Active).Sum(count => count.Count),
                [.. owners.Select(owner => new TenantConsoleOwner(owner.Id, NameOf(owner.FirstName, owner.LastName, owner.Email, "A system owner"), owner.Email ?? string.Empty, owner.State))],
                units,
                roles)
            {
                ManagedCustomers = managed,
                ManagedBy = managedBy,
                DeadLetters = deadLetters,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.logger.LogError(exception, "Reading the console overview of tenant {TenantId} failed.", tenantId);
            return Result<TenantConsoleSummary>.Failure(TenantUnreadable);
        }
    }

    public async Task<Result<ListPage<UserListItemDto>>> QueryTenantUsersAsync(Guid tenantId, ListQuery query)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<ListPage<UserListItemDto>>.Failure(ConsoleOnly);
        }

        if (await TenantExistsAsync(tenantId) is { IsSuccess: false } missing)
        {
            return Result<ListPage<UserListItemDto>>.Failure(missing.Error!);
        }

        return await this.users.QueryConsoleUsersAsync(tenantId, query);
    }

    public async Task<Result<ListPage<TenantConsoleRole>>> QueryTenantRolesAsync(Guid tenantId, ListQuery query)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<ListPage<TenantConsoleRole>>.Failure(ConsoleOnly);
        }

        var list = PlatformTenantRoleList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "list a tenant's roles on its console");
            if (lease is null)
            {
                return Result<ListPage<TenantConsoleRole>>.Failure(ConsoleOnly);
            }

            if (!await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId))
            {
                return Result<ListPage<TenantConsoleRole>>.Failure(TenantNotFound);
            }

            var roles = context.TenantRoles.AsNoTracking().Where(role => role.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                roles = roles.Where(role => role.Name.Contains(search) || (role.Description != null && role.Description.Contains(search)));
            }

            var total = await roles.CountAsync();
            var page = ListPage<TenantConsoleRole>.PageWithin(list, total);
            var order = new ListOrder<TenantRole>(roles);
            _ = list.Sort switch
            {
                PlatformTenantRoleList.SortPermissions => order.By(role => role.Permissions.Count, list.Descending).By(role => role.Name),
                _ => order.By(role => role.Name, list.Descending),
            };

            var rows = await order.By(role => role.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(role => new TenantConsoleRole(
                    role.Id,
                    role.Name,
                    role.Description,
                    role.State,
                    role.IsSystemOwner,
                    role.Permissions.Count,
                    context.OrganizationUnitUserRoles.Where(holding => holding.TenantId == tenantId && holding.TenantRoleId == role.Id).Select(holding => holding.UserId).Distinct().Count()))
                .ToListAsync();
            return Result<ListPage<TenantConsoleRole>>.Success(ListPage<TenantConsoleRole>.Ungrouped(rows, total, page, list.PageSize));
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Listing the roles of tenant {TenantId} on its console failed.", tenantId);
            return Result<ListPage<TenantConsoleRole>>.Failure("The roles could not be read. Try again.");
        }
    }

    public async Task<Result<ListPage<PlatformDeadLetterRow>>> QueryDeadLettersAsync(Guid tenantId, ListQuery query)
    {
        if (!this.runtime.HasPlatformReach())
        {
            return Result<ListPage<PlatformDeadLetterRow>>.Failure(ConsoleOnly);
        }

        var list = PlatformDeadLetterList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "list a tenant's dead letters on its console");
            if (lease is null)
            {
                return Result<ListPage<PlatformDeadLetterRow>>.Failure(ConsoleOnly);
            }

            if (!await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId))
            {
                return Result<ListPage<PlatformDeadLetterRow>>.Failure(TenantNotFound);
            }

            var letters = DeadLettersOf(context, tenantId);
            letters = list.Filter(PlatformDeadLetterList.Direction) switch
            {
                PlatformDeadLetterList.Sent => letters.Where(message => message.TenantId == tenantId),
                PlatformDeadLetterList.Received => letters.Where(message => message.TargetTenantId == tenantId),
                _ => letters,
            };

            if (list.Search is { Length: > 0 } search)
            {
                letters = letters.Where(message => message.Kind.Contains(search));
            }

            var total = await letters.CountAsync();
            var page = ListPage<PlatformDeadLetterRow>.PageWithin(list, total);
            var order = new ListOrder<IntegrationMessage>(letters);
            _ = list.Sort switch
            {
                PlatformDeadLetterList.SortKind => order.By(message => message.Kind, list.Descending).By(message => message.Finished, descending: true),
                PlatformDeadLetterList.SortWritten => order.By(message => message.Created, list.Descending),
                _ => order.By(message => message.Finished, list.Descending),
            };

            var rows = await order.By(message => message.Sequence).By(message => message.Id).Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(message => new PlatformDeadLetterRow(
                    message.Id,
                    message.Kind,
                    message.TenantId == tenantId,
                    message.TenantId == tenantId ? message.TargetTenantId : message.TenantId,
                    context.Tenants.Where(other => other.Id == (message.TenantId == tenantId ? message.TargetTenantId : message.TenantId)).Select(other => other.Name).FirstOrDefault() ?? "A removed tenant",
                    message.CorrelationId,
                    message.Sequence,
                    message.Attempts,
                    message.Created,
                    message.Finished,
                    message.LastFailure))
                .ToListAsync();
            return Result<ListPage<PlatformDeadLetterRow>>.Success(ListPage<PlatformDeadLetterRow>.Ungrouped(rows, total, page, list.PageSize));
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Listing the dead letters of tenant {TenantId} on its console failed.", tenantId);
            return Result<ListPage<PlatformDeadLetterRow>>.Failure("The dead letters could not be read. Try again.");
        }
    }

    private async Task<Result> TenantExistsAsync(Guid tenantId)
    {
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            using var lease = context.BeginPlatformScope(this.runtime, "check that a tenant exists");
            if (lease is null)
            {
                return Result.Failure(ConsoleOnly);
            }

            return await context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId) ? Result.Success() : Result.Failure(TenantNotFound);
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Checking that tenant {TenantId} exists for its console failed.", tenantId);
            return Result.Failure(TenantUnreadable);
        }
    }

    private static IQueryable<IntegrationMessage> DeadLettersOf(ApplicationDbContext context, Guid tenantId) =>
        context.IntegrationMessages.AsNoTracking()
            .Where(message => message.State == IntegrationMessageState.DeadLettered && (message.TenantId == tenantId || message.TargetTenantId == tenantId));

    private static PlatformRecentImpersonation Recent(ImpersonationRead audit) => new(
        audit.Id,
        NameOf(audit.Impersonator?.FirstName, audit.Impersonator?.LastName, audit.Impersonator?.Email, "A former platform owner"),
        NameOf(audit.Target?.FirstName, audit.Target?.LastName, audit.Target?.Email, "A removed user"),
        audit.TenantName ?? "A removed tenant",
        audit.Started,
        audit.Ended);

    private static string NameOf(string? firstName, string? lastName, string? email, string fallback) =>
        $"{firstName} {lastName}".Trim() is { Length: > 0 } name ? name
        : !string.IsNullOrWhiteSpace(email) ? email
        : fallback;

    private sealed class ImpersonationRead
    {
        public Guid Id { get; init; }

        public DateTime Started { get; init; }

        public DateTime? Ended { get; init; }

        public Guid TargetUserId { get; init; }

        public Guid TargetTenantId { get; init; }

        public PersonRead? Impersonator { get; init; }

        public PersonRead? Target { get; init; }

        public string? TenantName { get; init; }

        public TenantState? TenantState { get; init; }
    }

    private sealed class PersonRead
    {
        public string? FirstName { get; init; }

        public string? LastName { get; init; }

        public string? Email { get; init; }

        public UserState State { get; init; }

        public bool IsSystemAccount { get; init; }

        public Guid TenantId { get; init; }
    }

    private sealed class TenantInFamily
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        public TenantState State { get; init; }

        public DateTime Created { get; init; }

        public Guid? ParentTenantId { get; init; }

        public Guid FamilyId { get; init; }

        public string FamilyName { get; init; } = string.Empty;

        public TenantState FamilyState { get; init; }

        public DateTime FamilyCreated { get; init; }
    }
}
