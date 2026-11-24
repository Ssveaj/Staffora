using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Jobs;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Customers;
using Staffora.Services.Jobs;

namespace Staffora.Services.Search;

public sealed class GlobalSearchService
{
    public const int PerKind = 5;

    public const string ReadFailed = "The search could not be completed. Try again.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly ILogger<GlobalSearchService> log;

    public GlobalSearchService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, ILogger<GlobalSearchService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.log = log;
    }

    public async Task<IReadOnlyList<SearchKind>> KindsAsync(CancellationToken cancellationToken = default) =>
        (await ReachOfAsync(this.runtime.Current, cancellationToken)).Kinds;

    public async Task<Result<SearchAnswer>> SearchAsync(string? text, CancellationToken cancellationToken = default)
    {
        var session = this.runtime.Current;
        var reach = await ReachOfAsync(session, cancellationToken);
        var phrase = SearchText.Normalize(text);
        if (phrase is null || reach.Kinds.Count == 0)
        {
            return Result<SearchAnswer>.Success(SearchAnswer.None(phrase ?? string.Empty));
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var groups = new List<SearchGroup>();
            foreach (var kind in reach.Kinds)
            {
                var hits = kind switch
                {
                    SearchKind.StaffMembers => await StaffMembersAsync(context, reach.TenantId, phrase, cancellationToken),
                    SearchKind.Jobs when reach.AllJobs => await JobsAsync(context, reach.TenantId, phrase, cancellationToken),
                    SearchKind.Jobs => await OwnAssignmentsAsync(context, reach.TenantId, reach.UserId, phrase, cancellationToken),
                    SearchKind.Customers => await CustomersAsync(context, reach.TenantId, phrase, cancellationToken),
                    SearchKind.Tenants => await TenantsAsync(context, phrase, cancellationToken),
                    _ => [],
                };
                if (hits.Count > 0)
                {
                    groups.Add(new SearchGroup(kind, hits));
                }
            }

            return Result<SearchAnswer>.Success(new SearchAnswer(phrase, groups));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Searching for {Length} characters in tenant {TenantId} failed.", phrase.Length, reach.TenantId);
            return Result<SearchAnswer>.Failure(ReadFailed);
        }
    }

    private async Task<SearchReach> ReachOfAsync(RuntimeSnapshot session, CancellationToken cancellationToken)
    {
        if (session.HasPlatformReach())
        {
            return new SearchReach([SearchKind.Tenants], session.TenantId ?? Guid.Empty, session.EffectiveUserId ?? Guid.Empty, AllJobs: false);
        }

        if (session.TenantId is not { } tenantId || session.EffectiveUserId is not { } userId)
        {
            return SearchReach.Nothing;
        }

        var kinds = new List<SearchKind>();
        if (await this.permissionService.HasPermissionAsync(RolePermission.ResourcesView, cancellationToken))
        {
            kinds.Add(SearchKind.StaffMembers);
        }

        var allJobs = await this.permissionService.HasAnyPermissionAsync(JobService.Readers, cancellationToken);
        if (allJobs || await this.permissionService.HasPermissionAsync(RolePermission.ViewOwnAssignments, cancellationToken))
        {
            kinds.Add(SearchKind.Jobs);
        }

        if (await this.permissionService.HasAnyPermissionAsync(CustomerService.Readers, cancellationToken))
        {
            kinds.Add(SearchKind.Customers);
        }

        return new SearchReach(kinds, tenantId, userId, allJobs);
    }

    private static async Task<IReadOnlyList<SearchHit>> StaffMembersAsync(ApplicationDbContext context, Guid tenantId, string phrase, CancellationToken cancellationToken)
    {
        var rows = await context.Resources.AsNoTracking()
            .Where(resource => resource.TenantId == tenantId && (resource.User.FirstName + " " + resource.User.LastName).Contains(phrase))
            .OrderBy(resource => resource.User.FirstName.StartsWith(phrase) || resource.User.LastName.StartsWith(phrase) || (resource.User.FirstName + " " + resource.User.LastName).StartsWith(phrase) ? 0 : 1)
            .ThenBy(resource => resource.User.FirstName)
            .ThenBy(resource => resource.User.LastName)
            .ThenBy(resource => resource.Id)
            .Take(PerKind)
            .Select(resource => new { resource.UserId, resource.User.FirstName, resource.User.LastName, resource.User.Email, resource.State })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new SearchHit(SearchKind.StaffMembers, SearchOpens.StaffMember, row.UserId, $"{row.FirstName} {row.LastName}".Trim(), row.Email, row.State))];
    }

    private static async Task<IReadOnlyList<SearchHit>> JobsAsync(ApplicationDbContext context, Guid tenantId, string phrase, CancellationToken cancellationToken)
    {
        var rows = await context.Jobs.AsNoTracking()
            .Where(job => job.TenantId == tenantId && job.State != JobState.Deleted && job.Name.Contains(phrase))
            .OrderBy(job => job.Name.StartsWith(phrase) ? 0 : 1)
            .ThenBy(job => job.Name)
            .ThenBy(job => job.Id)
            .Take(PerKind)
            .Select(job => new { job.Id, job.Name, Customer = job.Customer.Name, job.Schedule, job.PeriodStart, job.PeriodEnd, job.StartTime, job.EndTime, job.State })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new SearchHit(
            SearchKind.Jobs,
            SearchOpens.Job,
            row.Id,
            row.Name,
            $"{row.Customer} · {JobRules.WhenText(new JobTimes(row.Schedule, row.PeriodStart, row.PeriodEnd, row.StartTime, row.EndTime, []))}",
            row.State))];
    }

    private static async Task<IReadOnlyList<SearchHit>> OwnAssignmentsAsync(ApplicationDbContext context, Guid tenantId, Guid userId, string phrase, CancellationToken cancellationToken)
    {
        var rows = await context.SlotAssignments.AsNoTracking()
            .Where(assignment => assignment.TenantId == tenantId && assignment.Resource.UserId == userId && assignment.Slot.Job.Name.Contains(phrase))
            .OrderBy(assignment => assignment.Slot.Job.Name.StartsWith(phrase) ? 0 : 1)
            .ThenBy(assignment => assignment.State == SlotAssignmentState.Requested || assignment.State == SlotAssignmentState.StaffQuestion || assignment.State == SlotAssignmentState.Accepted || assignment.State == SlotAssignmentState.Assigned ? 0 : 1)
            .ThenBy(assignment => assignment.Slot.Job.Name)
            .ThenByDescending(assignment => assignment.Created)
            .ThenBy(assignment => assignment.Id)
            .Take(PerKind)
            .Select(assignment => new { assignment.Id, Job = assignment.Slot.Job.Name, Slot = assignment.Slot.Name, Customer = assignment.Slot.Job.Customer.Name, assignment.State })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new SearchHit(SearchKind.Jobs, SearchOpens.Assignment, row.Id, row.Job, $"{row.Slot} · {row.Customer}", row.State))];
    }

    private static async Task<IReadOnlyList<SearchHit>> CustomersAsync(ApplicationDbContext context, Guid tenantId, string phrase, CancellationToken cancellationToken)
    {
        var rows = await context.Customers.AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.State != CustomerState.Deleted && customer.Name.Contains(phrase))
            .OrderBy(customer => customer.Name.StartsWith(phrase) ? 0 : 1)
            .ThenBy(customer => customer.Name)
            .ThenBy(customer => customer.Id)
            .Take(PerKind)
            .Select(customer => new { customer.Id, customer.Name, customer.Description, customer.State })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new SearchHit(SearchKind.Customers, SearchOpens.Customer, row.Id, row.Name, row.Description, row.State))];
    }

    private async Task<IReadOnlyList<SearchHit>> TenantsAsync(ApplicationDbContext context, string phrase, CancellationToken cancellationToken)
    {
        using var lease = context.BeginPlatformScope(this.runtime, "search the tenants by name");
        if (lease is null)
        {
            return [];
        }

        var rows = await context.Tenants.AsNoTracking()
            .Where(tenant => !tenant.IsRootTenant && tenant.Name.Contains(phrase))
            .OrderBy(tenant => tenant.Name.StartsWith(phrase) ? 0 : 1)
            .ThenBy(tenant => tenant.Name)
            .ThenBy(tenant => tenant.Id)
            .Take(PerKind)
            .Select(tenant => new
            {
                tenant.Id,
                tenant.Name,
                tenant.Description,
                tenant.State,
                ManagedBy = context.Tenants.Where(company => company.Id == tenant.ParentTenantId).Select(company => company.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new SearchHit(SearchKind.Tenants, SearchOpens.TenantConsole, row.Id, row.Name, row.ManagedBy is { } company ? $"Customer portal of {company}" : row.Description, row.State))];
    }

    private sealed record SearchReach(IReadOnlyList<SearchKind> Kinds, Guid TenantId, Guid UserId, bool AllJobs)
    {
        public static SearchReach Nothing { get; } = new([], Guid.Empty, Guid.Empty, AllJobs: false);
    }
}
