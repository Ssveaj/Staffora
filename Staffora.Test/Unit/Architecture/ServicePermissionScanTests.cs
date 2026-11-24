using System.Text.RegularExpressions;
using Staffora.Services.Authorization;
using Staffora.Services.Comments;
using Staffora.Services.Coordination;
using Staffora.Services.Jobs;
using Staffora.Services.CustomFields;
using Staffora.Services.Customers;
using Staffora.Services.Home;
using Staffora.Services.Messaging;
using Staffora.Services.Platform;
using Staffora.Services.Portal;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;
using Staffora.Test.Unit.Architecture.ServiceScanSamples;

namespace Staffora.Test.Unit.Architecture;

public class ServicePermissionScanTests
{
    private static readonly Type[] services =
    [
        typeof(UserService), typeof(OrganizationService), typeof(TenantService), typeof(TenantRoleService), typeof(RoleHoldingService), typeof(TenantOnboardingService), typeof(CustomFieldService),
        typeof(TenantSettingsService), typeof(CommentService), typeof(NoticeService), typeof(QualificationService), typeof(WorkPatternService),
        typeof(PatternAssignmentService), typeof(StaffAvailabilityService), typeof(ResourceAbsenceService), typeof(CustomerService),
        typeof(CustomerContactService), typeof(JobService), typeof(JobSlotService), typeof(CoordinationService),
        typeof(SlotAssignmentService), typeof(RequestRoundService), typeof(StaffConversationService), typeof(StaffingService), typeof(ResourceService),
        typeof(OrderService), typeof(Staffora.Services.Catalogue.ServiceCatalogueService),
        typeof(TenantHomeService), typeof(Staffora.Services.Search.GlobalSearchService), typeof(ClosedDayService),
    ];

    private static readonly Dictionary<Type, string> notHeld = new()
    {
        [typeof(PermissionService)] = "the permission check itself",
        [typeof(UserSessionService)] = "resolves the session from the cookie's claims, before any permission can be asked; the only claims reader (ADR 0014)",
        [typeof(ImpersonationService)] = "the impersonation endpoints: its authorizer (ImpersonationAuthorizer) judges platform reach and every target before anything is read or issued, a reach no tenant permission grants (ADR 0014)",
        [typeof(PlatformConsoleService)] = "held to platform reach by Every_platform_console_read_checks_platform_reach_before_it_touches_data, a reach no tenant permission grants (ADR 0012)",
        [typeof(StateHistoryService)] = "checks no read permission by design: each entity's service gates its history before it asks",
        [typeof(WorkflowService)] = "every step is judged by the access gate of the entity's own workflow (ADR 0002); the entity services that call it check their permission first",
        [typeof(TenantSettingsEnsureService)] = "a singleton that pins every tenant's time zone at startup and onboarding, in system scope on no one's behalf but the tenant's (docs/authorization.md, \"Services check\")",
        [typeof(JobClockService)] = "a hosted service with no public method; the job clock it runs acts as each tenant's system account",
        [typeof(RetentionService)] = "a hosted service with no public method; the retention pass it runs works in system scope under its own leases and archives a purged tenant as the platform tenant's system account, and the console reads its last pass through PlatformConsoleService, which checks platform reach (ADR 0042)",
        [typeof(Staffora.Services.Integration.IntegrationOutboxService)] = "a hosted service with no public method; the outbox dispatcher it runs works in system scope under its own leases (ADR 0007)",
    };

    private static readonly Dictionary<(string Service, string Method), string> allowList = new()
    {
        [("RoleHoldingService", "FetchOwnRoleHoldingsAsync")] = "the caller reads their own role holdings (overview page)",
        [("TenantHomeService", "ReadFirstNameAsync")] = "the caller reads their own first name for a home's greeting, only the session's effective user's in its own tenant; the platform console is refused",
        [("TenantService", "LoadTenantUpdaterAsync")] = "the tenant dialog is the platform console's, a reach no tenant permission grants (ADR 0012); it checks platform reach",
        [("TenantOnboardingService", "OnboardTenantAsync")] = "onboarding a tenant is a platform move no tenant permission grants (ADR 0012); it checks platform reach",
        [("TenantService", "UpdateTenantAndTransitionAsync")] = "a tenant's edits and steps are the platform owner's (ADR 0012); it checks platform reach, and WorkflowService's gate the steps' RequiresPlatformOwner",
        [("TenantService", "GetTenantHistoryAsync")] = "a tenant's history shows to those who may open its dialog, the platform console; it checks platform reach",
        [("TenantRoleService", "GetGrantablePermissionsAsync")] = "the session's own grant ceiling (PermissionService.MissingAsync): the permissions it holds itself, which a role dialog offers; it reads nothing but the session's own permission set (only a permissionService.Has question counts as a check)",
        [("NoticeService", "Subscribe")] = "the caller listens for their own notices (the bell), on the bus channel of the session's own tenant and effective user; it reads nothing",
        [("NoticeService", "GetUnreadCountAsync")] = "the caller counts their own notices, only the session's effective user's in its tenant",
        [("NoticeService", "GetNoticesAsync")] = "the caller reads their own notices, only the session's effective user's in its tenant",
        [("NoticeService", "MarkReadAsync")] = "the caller marks one of their own notices read, refused while impersonating (docs/authorization.md, \"Notices\")",
        [("NoticeService", "MarkAllReadAsync")] = "the caller marks their own notices read, refused while impersonating (docs/authorization.md, \"Notices\")",
        [("TenantSettingsService", "GetAsync")] = "every user of a tenant works with its settings (each time shown converts through its time zone); it reads the runtime tenant's own value only, never a tenant the caller names, and answers no row or version",
        [("TenantSettingsService", "GetOfTenantAsync")] = "the value in force of a named tenant's setting for code acting on that tenant's record (the console's state history, the job clock as the tenant's system account): it reads only the runtime's own tenant, whose users all work with its settings, or any tenant with platform reach, checked from one snapshot before the cache is asked, and answers no row or version",
        [("CustomFieldService", "QueryTenantDefinitionsAsync")] = "a tenant's fields on its platform console, a reach no tenant permission grants; it checks platform reach (Every_platform_console_read_checks_platform_reach_before_it_touches_data)",
    };

    private static readonly (Type Service, string Method)[] consoleReads =
    [
        (typeof(CustomFieldService), nameof(CustomFieldService.QueryTenantDefinitionsAsync)),
        (typeof(TenantService), nameof(TenantService.LoadTenantUpdaterAsync)),
        (typeof(TenantService), nameof(TenantService.UpdateTenantAndTransitionAsync)),
        (typeof(TenantService), nameof(TenantService.GetTenantHistoryAsync)),
    ];

    internal static readonly string[] PermissionChecks = ["permissionService.Has"];

    internal static IReadOnlyList<Type> HeldServices => services;

    internal static bool HoldsItsOwnCheck(Type service, string method) =>
        services.Contains(service) && !allowList.ContainsKey((service.Name, method)) && ServiceSources.PublicMethodNames(service).Contains(method);

    [Fact]
    public void Every_platform_console_read_checks_platform_reach_before_it_touches_data()
    {
        string[] reach = ["runtime.HasPlatformReach()"];
        string[] uses = ["dbFactory.", "userManager.", "users.", "settings.", "lookupCache.", "workflows.", "history."];
        var console = typeof(PlatformConsoleService);
        var methods = ServiceSources.PublicMethodNames(console).ToList();
        Assert.NotEmpty(methods);
        var offenders = UncheckedMethods(console.Name, ServiceSources.Of(console), methods, reach, uses)
            .Concat(consoleReads.SelectMany(read => UncheckedMethods(read.Service.Name, ServiceSources.Of(read.Service), [read.Method], reach, uses)))
            .Select(hit => $"{hit.Service}.{hit.Method}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_public_service_method_checks_a_permission_before_it_touches_data()
    {
        var offenders = services
            .SelectMany(UncheckedMethods)
            .Where(hit => !allowList.ContainsKey(hit))
            .Select(hit => $"{hit.Service}.{hit.Method}")
            .ToList();

        Assert.True(offenders.Count == 0, $"Public methods that touch data before a permission check: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_allow_list_entry_is_a_public_method_without_a_check_and_names_a_reason()
    {
        var withoutCheck = services
            .SelectMany(UncheckedMethods)
            .ToHashSet();

        var stale = allowList
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !withoutCheck.Contains(entry.Key))
            .Select(entry => $"{entry.Key.Service}.{entry.Key.Method}");

        Assert.Empty(stale);
    }

    [Fact]
    public void Every_service_is_held_by_the_scan_or_named_with_the_reason_it_is_not()
    {
        var all = ServiceSources.AllServices();

        Assert.Empty(all.Where(service => !services.Contains(service) && !notHeld.ContainsKey(service)).Select(service => service.FullName));
        Assert.Empty(services.Where(notHeld.ContainsKey).Select(service => service.FullName));
        Assert.Empty(notHeld.Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !all.Contains(entry.Key)).Select(entry => entry.Key.FullName));
        Assert.Equal(services.Length, services.Distinct().Count());
    }

    [Fact]
    public void The_source_lookup_reads_every_file_the_compiler_built_each_service_from()
    {
        var missed = ServiceSources.AllServices()
            .Select(service => (service, Read: ServiceSources.FilesOf(service), Compiled: ServiceSources.CompiledFilesOf(service)))
            .Where(each => each.Read.Count == 0 || each.Compiled.Count == 0 || each.Compiled.Except(each.Read).Any())
            .Select(each => $"{each.service.Name}: read [{string.Join(", ", each.Read)}], compiled [{string.Join(", ", each.Compiled)}]");

        Assert.Empty(missed);
    }

    [Fact]
    public void The_source_lookup_reads_both_files_of_a_partial_service_by_its_type()
    {
        var sample = typeof(PartialSampleService);

        Assert.Equal(
            ["Staffora.Test/Unit/Architecture/ServiceScanSamples/PartialSampleService.Reads.cs", "Staffora.Test/Unit/Architecture/ServiceScanSamples/PartialSampleService.cs"],
            ServiceSources.FilesOf(sample));
        Assert.Equal(ServiceSources.FilesOf(sample), ServiceSources.CompiledFilesOf(sample));
    }

    [Fact]
    public void The_scan_catches_a_method_without_a_check_in_a_second_partial_file()
    {
        Assert.Equal(
            [nameof(PartialSampleService.AsksAnotherServiceBeforeItChecksAsync), nameof(PartialSampleService.ForwardInTheSecondPartAsync), nameof(PartialSampleService.UncheckedInTheSecondPartAsync)],
            UncheckedMethods(typeof(PartialSampleService)).Select(hit => hit.Method).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_scan_catches_a_method_without_a_check_a_late_check_and_a_method_it_cannot_find()
    {
        const string sample = """
            public class SampleService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, PermissionService permissionService)
            {
                public async Task<Result> CheckedAsync(Guid id)
                {
                    if (!await permissionService.HasPermissionAsync(RolePermission.UsersView))
                        return Result.Failure("No.");
                    await using var context = dbFactory.CreateDbContext(runtime);
                    return Result.Success();
                }

                public async Task<List<T>> ThroughAHelperAsync<T>(Guid? id) where T : class
                {
                    await DemandAsync(RolePermission.SettingsView);
                    await using var context = dbFactory.CreateDbContext(runtime);
                    return [];
                }

                public async Task<Result> UncheckedAsync()
                {
                    // permissionService.HasPermissionAsync in a comment is no check.
                    await using var context = dbFactory.CreateDbContext(runtime);
                    return Result.Success();
                }

                public async Task<Result> CheckedInAStringAsync()
                {
                    var text = "permissionService.HasPermissionAsync in a message is no check either";
                    await using var context = dbFactory.CreateDbContext(runtime);
                    return Result.Success();
                }

                public async Task<Result> CheckedTooLateAsync()
                {
                    await using var context = dbFactory.CreateDbContext(runtime);
                    if (!await permissionService.HasPermissionAsync(RolePermission.UsersEdit))
                        return Result.Failure("No.");
                    return Result.Success();
                }

                public Task<bool> ExpressionBodiedAsync() => Task.FromResult(true);

                private async Task DemandAsync(RolePermission permission)
                {
                    if (!await permissionService.HasPermissionAsync(permission))
                        throw new UnauthorizedAccessException("No.");
                }
            }
            """;

        Assert.Equal(
            [("SampleService", "UncheckedAsync"), ("SampleService", "CheckedInAStringAsync"), ("SampleService", "CheckedTooLateAsync"), ("SampleService", "ExpressionBodiedAsync"), ("SampleService", "MissingAsync")],
            UncheckedMethods("SampleService", sample, ["CheckedAsync", "ThroughAHelperAsync", "UncheckedAsync", "CheckedInAStringAsync", "CheckedTooLateAsync", "ExpressionBodiedAsync", "MissingAsync"]));
    }

    internal static HashSet<string> CheckingMethods(string code, IReadOnlyList<string> checks, IReadOnlyList<string> uses)
    {
        var privateHelpers = ServiceSources.Members(code, "private");
        var publicMethods = ServiceSources.Members(code, "public")
            .GroupBy(method => method.Name, method => method.Body)
            .ToList();
        var checking = new HashSet<string>(StringComparer.Ordinal);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var helper in privateHelpers.Where(helper => !checking.Contains(helper.Name)))
            {
                if (checks.Any(check => helper.Body.Contains(check, StringComparison.Ordinal)) || CallsAny(helper.Body, checking))
                {
                    changed |= checking.Add(helper.Name);
                }
            }

            foreach (var method in publicMethods.Where(method => !checking.Contains(method.Key)))
            {
                if (BodiesCheckFirst([.. method], method.Key, checking, checks, uses))
                {
                    changed |= checking.Add(method.Key);
                }
            }
        }

        return checking;
    }

    private static List<(string Service, string Method)> UncheckedMethods(string service, string source, IEnumerable<string> methods) =>
        UncheckedMethods(service, source, methods, PermissionChecks, ["dbFactory.", "userManager."]);

    private static List<(string Service, string Method)> UncheckedMethods(Type service) =>
        UncheckedMethods(
            service.Name,
            ServiceSources.Of(service),
            ServiceSources.PublicMethodNames(service),
            PermissionChecks,
            ["dbFactory.", "userManager.", .. ServiceSources.InjectedServices(service).Where(held => held.Value != typeof(PermissionService)).Select(held => held.Key + ".")]);

    private static List<(string Service, string Method)> UncheckedMethods(string service, string source, IEnumerable<string> methods, IReadOnlyList<string> checks, IReadOnlyList<string> uses)
    {
        var code = ServiceSources.Code(source);
        var checking = CheckingMethods(code, checks, uses);

        return methods
            .Where(method =>
            {
                var bodies = ServiceSources.PublicBodiesOf(code, method);
                return !BodiesCheckFirst(bodies, method, checking, checks, uses);
            })
            .Select(method => (service, method))
            .ToList();
    }

    private static bool ChecksFirst(string body, IReadOnlySet<string> checkingMethods, IReadOnlyList<string> checks, IReadOnlyList<string> uses)
    {
        var check = checks
            .Select(marker => body.IndexOf(marker, StringComparison.Ordinal))
            .Concat(checkingMethods.Select(name => FirstCall(body, name)))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (check < 0)
        {
            return false;
        }

        var firstUse = uses
            .Select(marker => body.IndexOf(marker, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return check < firstUse;
    }

    private static bool BodiesCheckFirst(IReadOnlyList<string> bodies, string name, IReadOnlySet<string> checking, IReadOnlyList<string> checks, IReadOnlyList<string> uses)
    {
        var others = checking.Where(each => each != name).ToHashSet(StringComparer.Ordinal);
        var direct = bodies.Count(body => ChecksFirst(body, others, checks, uses));
        if (direct == 0 || direct == bodies.Count)
        {
            return direct > 0;
        }

        others.Add(name);
        return bodies.All(body => ChecksFirst(body, others, checks, uses));
    }

    private static bool CallsAny(string body, IEnumerable<string> names) => names.Any(name => FirstCall(body, name) >= 0);

    private static int FirstCall(string body, string name) =>
        Regex.Match(body, $@"(?<![\w.])(?:this\.)?{Regex.Escape(name)}\s*(?:<[^<>()]*>)?\s*\(") is { Success: true } call ? call.Index : -1;
}
