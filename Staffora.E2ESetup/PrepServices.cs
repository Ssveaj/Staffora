using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Services.Authorization;
using Staffora.Services.CustomFields;
using Staffora.Services.Customers;
using Staffora.Services.Integration;
using Staffora.Services.Resources;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Settings;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.E2ESetup;

public static class PrepServices
{
    public static ServiceProvider Build(string connectionString, TimeProvider clock, Action<ILoggingBuilder>? configureLogging = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            configureLogging?.Invoke(logging);
        });

        services.AddSingleton(clock);

        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Development, ApplicationName = "Staffora.E2ESetup" });
        services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();
        services.AddApplicationDbContexts(connectionString, throwOnUnorderedRowLimit: true);

        services.AddDataProtection().UseEphemeralDataProtectionProvider();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
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
        services.AddScoped<UserSessionService>();
        services.AddScoped<PersonaCookie>();
        services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<PersonaCookie>());
        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<PlatformBootstrap>();
        services.AddSingleton<SystemAccountService>();
        services.AddTenantSettings(AppConfiguration());

        services.AddScoped<CustomFieldService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<TenantService>();
        services.AddScoped<TenantRoleService>();
        services.AddScoped<RoleHoldingService>();
        services.AddScoped<UserService>();
        services.AddScoped<ResourceService>();
        services.AddScoped<QualificationService>();
        services.AddScoped<PatternAssignmentService>();
        services.AddScoped<StaffAvailabilityService>();
        services.AddScoped<ClosedDayService>();
        services.AddScoped<IStaffAvailability>(provider => provider.GetRequiredService<StaffAvailabilityService>());
        services.AddScoped<ResourceAbsenceService>();
        services.AddScoped<WorkPatternService>();
        services.AddScoped<Staffora.Services.Catalogue.ServiceCatalogueService>();
        services.AddScoped<Staffora.Services.Jobs.StaffConversationService>();
        services.AddScoped<Staffora.Services.Jobs.SlotAssignmentService>();
        services.AddScoped<Staffora.Services.Jobs.RequestRoundService>();
        services.AddScoped<Staffora.Services.Jobs.StaffingService>();
        services.AddScoped<Staffora.Services.Jobs.JobSlotService>();
        services.AddScoped<Staffora.Services.Jobs.JobService>();
        services.AddScoped<CustomerContactService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<UserPasswordIssuer>();
        services.AddScoped<IUserPasswordIssuer>(provider => provider.GetRequiredService<UserPasswordIssuer>());
        services.AddScoped<TenantOnboardingService>();
        services.AddWorkflowServices();

        services.AddIntegrationOutbox(AppConfiguration());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static IConfiguration AppConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(Path.Combine(FoundationStateFile.FindRepositoryRoot(), "Staffora.Blazor"))
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
}

public sealed class PersonaCookie : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
{
    private Task<AuthenticationState> state = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    public void SignIn(ClaimsPrincipal principal) => this.state = Task.FromResult(new AuthenticationState(principal));

    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask) => this.state = authenticationStateTask;

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => this.state;
}
