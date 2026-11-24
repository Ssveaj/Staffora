using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Models;
using Staffora.Services.Search;
using Staffora.Test.Integration.Infrastructure;

namespace Staffora.Test.Integration.Services;

[Trait("Category", "Integration")]
public sealed class GlobalSearchTests : IClassFixture<RealDatabase>
{
    private static readonly DateOnly PeriodStart = new(2026, 11, 9);

    private static readonly DateOnly PeriodEnd = new(2026, 12, 18);

    private readonly RealDatabase database;

    public GlobalSearchTests(RealDatabase database)
    {
        this.database = database;
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_coordinator_finds_the_staff_members_jobs_and_customers_of_their_own_tenant_only_ranked_by_prefix_in_SQL()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var token = Token();
        var a = await ArrangeAsync("Search North", token, cancellationToken);
        var b = await ArrangeAsync("Search South", token, cancellationToken);

        await using var host = a.Scenario.HostOf(this.database, a.Scenario.Coordinator);
        var search = host.Get<GlobalSearchService>();
        Assert.Equal([SearchKind.StaffMembers, SearchKind.Jobs, SearchKind.Customers], await search.KindsAsync(cancellationToken));

        host.ClearCommandLog();
        var found = Answer(await search.SearchAsync($"  {token}  ", cancellationToken));
        Assert.Equal(token, found.Text);
        Assert.Equal([SearchKind.StaffMembers, SearchKind.Jobs], found.Groups.Select(group => group.Kind));
        var staff = Assert.Single(Group(found, SearchKind.StaffMembers).Hits);
        Assert.Equal((SearchOpens.StaffMember, a.StaffUser), (staff.Opens, staff.Id));
        Assert.Equal($"Marlowe{token} Example", staff.Title);
        Assert.Equal(
            [(a.Depot, $"{token} Depot"), (a.Harbour, $"Harbour {token}")],
            Group(found, SearchKind.Jobs).Hits.Select(hit => (hit.Id, hit.Title)));
        Assert.All(Group(found, SearchKind.Jobs).Hits, hit => Assert.Equal(SearchOpens.Job, hit.Opens));
        Assert.StartsWith("Brightpath Learning · ", Group(found, SearchKind.Jobs).Hits[0].Subtitle, StringComparison.Ordinal);
        Assert.Contains(host.CommandLog, command => command.Contains("[Resources]", StringComparison.Ordinal) && command.Contains("LIKE", StringComparison.Ordinal) && command.Contains("TOP(", StringComparison.Ordinal));
        Assert.Contains(host.CommandLog, command => command.Contains("[Jobs]", StringComparison.Ordinal) && command.Contains("LIKE", StringComparison.Ordinal) && command.Contains("TOP(", StringComparison.Ordinal));

        var customers = Answer(await search.SearchAsync("Brightpath", cancellationToken));
        var customer = Assert.Single(Assert.Single(customers.Groups).Hits);
        Assert.Equal((SearchKind.Customers, SearchOpens.Customer, a.Scenario.Customer), (customer.Kind, customer.Opens, customer.Id));

        var everyHit = found.Hits.Concat(customers.Hits).Select(hit => hit.Id).ToHashSet();
        Assert.DoesNotContain(b.StaffUser, everyHit);
        Assert.DoesNotContain(b.Harbour, everyHit);
        Assert.DoesNotContain(b.Depot, everyHit);
        Assert.DoesNotContain(b.Scenario.Customer, everyHit);
        Assert.Empty(Answer(await search.SearchAsync(b.Scenario.StaffMembers.Onboarded.Tenant.Name, cancellationToken)).Groups);
        Assert.Empty(Answer(await search.SearchAsync(token[..1], cancellationToken)).Groups);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task A_staff_member_finds_only_the_jobs_they_hold_an_assignment_on_and_opens_their_assignment()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var token = Token();
        var a = await ArrangeAsync("Search Own", token, cancellationToken);
        var b = await ArrangeAsync("Search Other", token, cancellationToken);

        await using var host = a.Scenario.HostOf(this.database, a.StaffUser);
        var search = host.Get<GlobalSearchService>();
        Assert.Equal([SearchKind.Jobs], await search.KindsAsync(cancellationToken));

        var found = Answer(await search.SearchAsync(token, cancellationToken));
        var group = Assert.Single(found.Groups);
        Assert.Equal(SearchKind.Jobs, group.Kind);
        var own = Assert.Single(group.Hits);
        Assert.Equal((SearchOpens.Assignment, a.Request, $"Harbour {token}", "Lead · Brightpath Learning"), (own.Opens, own.Id, own.Title, own.Subtitle));
        Assert.Empty(Answer(await search.SearchAsync("Brightpath", cancellationToken)).Groups);
        Assert.Empty(Answer(await search.SearchAsync($"Marlowe{token}", cancellationToken)).Groups);
        Assert.DoesNotContain(found.Hits, hit => hit.Id == b.Request || hit.Id == a.Depot || hit.Id == a.Harbour);
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task Each_kind_is_searched_only_for_a_reader_who_may_view_it()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var token = Token();
        var a = await ArrangeAsync("Search Kinds", token, cancellationToken);
        var tenant = a.Scenario.StaffMembers.Onboarded.Tenant;

        var customersOnly = await a.Scenario.StaffMembers.AddPersonAsync(this.database, "Cleo", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, tenant, customersOnly, cancellationToken, RolePermission.CustomersView);
        await using (var host = a.Scenario.HostOf(this.database, customersOnly))
        {
            var search = host.Get<GlobalSearchService>();
            Assert.Equal([SearchKind.Customers], await search.KindsAsync(cancellationToken));
            Assert.Empty(Answer(await search.SearchAsync(token, cancellationToken)).Groups);
            Assert.Equal([a.Scenario.Customer], Answer(await search.SearchAsync("Brightpath", cancellationToken)).Hits.Select(hit => hit.Id));
        }

        var staffOnly = await a.Scenario.StaffMembers.AddPersonAsync(this.database, "Remy", cancellationToken);
        await TwoTenantScenario.GrantAsync(this.database, tenant, staffOnly, cancellationToken, RolePermission.ResourcesView);
        await using (var host = a.Scenario.HostOf(this.database, staffOnly))
        {
            var search = host.Get<GlobalSearchService>();
            Assert.Equal([SearchKind.StaffMembers], await search.KindsAsync(cancellationToken));
            var found = Answer(await search.SearchAsync(token, cancellationToken));
            Assert.Equal([(SearchKind.StaffMembers, a.StaffUser)], found.Hits.Select(hit => (hit.Kind, hit.Id)));
            Assert.Empty(Answer(await search.SearchAsync("Brightpath", cancellationToken)).Groups);
        }

        var nobody = await a.Scenario.StaffMembers.AddPersonAsync(this.database, "Noor", cancellationToken);
        await using (var host = a.Scenario.HostOf(this.database, nobody))
        {
            var search = host.Get<GlobalSearchService>();
            Assert.Empty(await search.KindsAsync(cancellationToken));
            host.ClearCommandLog();
            Assert.Empty(Answer(await search.SearchAsync(token, cancellationToken)).Groups);
            Assert.DoesNotContain(host.CommandLog, command => command.Contains("LIKE", StringComparison.Ordinal));
        }
    }

    [Fact(Timeout = WalkTimeout.Database)]
    public async Task The_platform_owner_finds_tenants_on_the_console_and_only_the_impersonated_tenants_records_while_impersonating()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var token = Token();
        var a = await ArrangeAsync("Search Console", token, cancellationToken);
        var b = await ArrangeAsync("Search Console", token, cancellationToken);
        var onboarded = a.Scenario.StaffMembers.Onboarded;

        await using (var host = ServiceHost.Create(this.database, onboarded.Platform.PlatformOwnerRuntime()))
        {
            var search = host.Get<GlobalSearchService>();
            Assert.Equal([SearchKind.Tenants], await search.KindsAsync(cancellationToken));
            var tenants = Answer(await search.SearchAsync(onboarded.Tenant.Name, cancellationToken));
            var hit = Assert.Single(Assert.Single(tenants.Groups).Hits);
            Assert.Equal((SearchKind.Tenants, SearchOpens.TenantConsole, onboarded.TenantId, onboarded.Tenant.Name, (Enum?)TenantState.Active), (hit.Kind, hit.Opens, hit.Id, hit.Title, hit.State));
            var both = Answer(await search.SearchAsync("Search Console", cancellationToken)).Hits.Select(found => found.Id).ToHashSet();
            Assert.Superset(new HashSet<Guid> { a.Scenario.TenantId, b.Scenario.TenantId }, both);
            Assert.Empty(Answer(await search.SearchAsync(token, cancellationToken)).Groups);
            Assert.Contains(host.ContextLog, message => message.Contains("search the tenants by name", StringComparison.Ordinal));
        }

        var impersonating = onboarded.Platform.PlatformOwnerRuntime();
        impersonating.StartImpersonation(a.Scenario.Coordinator, a.Scenario.TenantId);
        await using (var host = ServiceHost.Create(this.database, impersonating, AssignmentScenario.UseClock))
        {
            var search = host.Get<GlobalSearchService>();
            Assert.Equal([SearchKind.StaffMembers, SearchKind.Jobs, SearchKind.Customers], await search.KindsAsync(cancellationToken));
            Assert.Empty(Answer(await search.SearchAsync(onboarded.Tenant.Name, cancellationToken)).Groups);
            var found = Answer(await search.SearchAsync(token, cancellationToken)).Hits.Select(hit => hit.Id).ToHashSet();
            Assert.Equal(new HashSet<Guid> { a.StaffUser, a.Depot, a.Harbour }, found);
            Assert.DoesNotContain(host.ContextLog, message => message.Contains("System scope opened", StringComparison.Ordinal));
        }
    }

    private static string Token() => $"Q{Guid.NewGuid():N}"[..9];

    private static SearchAnswer Answer(Result<SearchAnswer> result)
    {
        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private static SearchGroup Group(SearchAnswer answer, SearchKind kind) => Assert.Single(answer.Groups, group => group.Kind == kind);

    private async Task<Arranged> ArrangeAsync(string name, string token, CancellationToken cancellationToken)
    {
        var scenario = await AssignmentScenario.CreateAsync(this.database, name, cancellationToken);
        var staffUser = await scenario.AddStaffAsync(this.database, $"Marlowe{token}", cancellationToken);
        var (harbour, slots) = await scenario.StartedJobAsync(this.database, $"Harbour {token}", PeriodStart, PeriodEnd, cancellationToken);
        var (depot, _) = await scenario.StartedJobAsync(this.database, $"{token} Depot", PeriodStart, PeriodEnd, cancellationToken);
        var request = await scenario.RequestAsync(this.database, scenario.Coordinator, slots[0], staffUser, cancellationToken);
        Assert.True(request.IsSuccess, request.Error);
        return new Arranged(scenario, staffUser, harbour, depot, request.Value);
    }

    private sealed record Arranged(AssignmentScenario Scenario, Guid StaffUser, Guid Harbour, Guid Depot, Guid Request);
}
