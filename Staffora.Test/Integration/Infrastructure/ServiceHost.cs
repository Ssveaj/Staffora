using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Services.Authorization;
using Staffora.Services.Caching;
using Staffora.Services.Jobs;
using Staffora.Services.CustomFields;
using Staffora.Services.Customers;
using Staffora.Services.Diagnostics;
using Staffora.Services.Integration;
using Staffora.Services.Platform;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.Test.Integration.Infrastructure;

public sealed class ServiceHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly AsyncServiceScope scope;
    private readonly ConcurrentQueue<string> contextLog = new();
    private readonly CommandRecorder commands = new();

    private ServiceHost(RealDatabase database, IRuntimeExecutionContext runtime, Action<IServiceCollection>? configure, IReadOnlyList<IInterceptor> interceptors)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new Recorder(this.contextLog)).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(_ => runtime);
        services.AddApplicationDbContexts(database.ConnectionString, throwOnUnorderedRowLimit: true);

        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(provider => new TestContextFactory(provider.GetRequiredService<DbContextOptions<ApplicationDbContext>>(), [this.commands, .. interceptors]));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        services.AddAuthentication();
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Stores.SchemaVersion = ApplicationDbContextOptions.IdentitySchemaVersion;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddApplicationStores()
            .AddClaimsPrincipalFactory<ApplicationClaimsPrincipalFactory>()
            .AddSignInManager<ApplicationSignInManager>()
            .AddDefaultTokenProviders();
        services.AddScoped<PermissionService>();
        services.AddScoped<ImpersonationAuthorizer>();
        services.AddScoped<ImpersonationAuditWriter>();
        services.AddScoped<ImpersonationService>();
        services.AddScoped<PlatformBootstrap>();
        services.AddTenantSettings(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:TimeZone"] = PlatformTimeZone }).Build());
        services.AddScoped<CustomFieldService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<TenantService>();
        services.AddScoped<TenantRoleService>();
        services.AddScoped<RoleHoldingService>();
        services.AddScoped<TenantOnboardingService>();
        services.AddScoped<PlatformConsoleService>();
        services.AddScoped<PlatformOwnerResetCommand>();
        services.AddScoped<UserService>();
        services.AddScoped<ResourceService>();
        services.AddScoped<QualificationService>();
        services.AddScoped<WorkPatternService>();
        services.AddScoped<Staffora.Services.Catalogue.ServiceCatalogueService>();
        services.AddScoped<PatternAssignmentService>();
        services.AddScoped<StaffAvailabilityService>();
        services.AddScoped<ClosedDayService>();
        services.AddScoped<IStaffAvailability>(provider => provider.GetRequiredService<StaffAvailabilityService>());
        services.AddScoped<ResourceAbsenceService>();
        services.AddScoped<CustomerContactService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<StaffConversationService>();
        services.AddScoped<SlotAssignmentService>();
        services.AddScoped<RequestRoundService>();
        services.AddScoped<StaffingService>();
        services.AddScoped<JobSlotService>();
        services.AddScoped<JobService>();
        services.AddScoped<Staffora.Services.Coordination.CoordinationService>();
        services.AddScoped<Staffora.Services.Search.GlobalSearchService>();
        services.AddScoped<Staffora.Services.Home.TenantHomeService>();
        services.AddScoped<Staffora.Services.Portal.OrderService>();
        services.AddSingleton<SystemOwnerPermissionsEnsure>();
        services.AddScoped<UserPasswordIssuer>();
        services.AddScoped<IUserPasswordIssuer>(provider => provider.GetRequiredService<UserPasswordIssuer>());
        services.AddSingleton<SystemAccountService>();
        services.AddSingleton<ErrorLogQueue>();
        services.AddWorkflowServices();

        services.AddJobClock(new ConfigurationBuilder().Build());

        services.AddIntegrationOutbox(new ConfigurationBuilder().Build());

        services.AddRetention(new ConfigurationBuilder().Build());
        services.AddTestDeleteCleanups();
        configure?.Invoke(services);

        this.provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        this.scope = this.provider.CreateAsyncScope();
    }

    public const string PlatformTimeZone = "Europe/Stockholm";

    public static Action<IServiceCollection> SharingLookupCacheOf(ServiceHost owner) =>
        services => services.AddSingleton(owner.Get<TenantLookupCache>());

    public IReadOnlyList<string> ContextLog => [.. this.contextLog];

    public IReadOnlyList<string> CommandLog => [.. this.commands.Commands];

    public void ClearCommandLog() => this.commands.Commands.Clear();

    public static ServiceHost Create(RealDatabase database, IRuntimeExecutionContext runtime, Action<IServiceCollection>? configure = null, IReadOnlyList<IInterceptor>? interceptors = null) =>
        new(database, runtime, configure, interceptors ?? []);

    public T Get<T>() where T : notnull => this.scope.ServiceProvider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await this.scope.DisposeAsync();
        await this.provider.DisposeAsync();
    }

    private sealed class TestContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options;

        public TestContextFactory(DbContextOptions<ApplicationDbContext> options, IReadOnlyList<IInterceptor> interceptors)
        {
            this.options = new DbContextOptionsBuilder<ApplicationDbContext>(options).AddInterceptors(interceptors).Options;
        }

        public ApplicationDbContext CreateDbContext() => new TestDataContext(this.options);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            Record(command, result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Record(command, result));

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) =>
            Record(command, result);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Record(command, result));

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) =>
            Record(command, result);

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Record(command, result));

        private T Record<T>(DbCommand command, T result)
        {
            Commands.Enqueue(command.CommandText);
            return result;
        }
    }

    private sealed class Recorder : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> messages;

        public Recorder(ConcurrentQueue<string> messages)
        {
            this.messages = messages;
        }

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(ApplicationDbContext).FullName ? this : NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.messages.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
