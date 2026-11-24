using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Radzen;
using Staffora.Blazor.Components;
using Staffora.Blazor.Components.Account;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Services.Authorization;
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

var command = args is [PlatformBootstrap.Command or AccountUnlockCommand.Command or PlatformOwnerResetCommand.Command, ..] ? args[0] : null;
var commandEmail = args is [_, var email, ..] && command is not null ? email : null;
if (command is not null && PlatformBootstrap.ParseEmail(commandEmail) is null)
{
    Console.Error.WriteLine(command switch
    {
        PlatformBootstrap.Command => $"Usage: dotnet run --project Staffora.Blazor -- {PlatformBootstrap.Command} <email>, with the first platform owner's email address, such as owner@example.com.",
        PlatformOwnerResetCommand.Command => $"Usage: dotnet run --project Staffora.Blazor -- {PlatformOwnerResetCommand.Command} <email>, with the email address of the platform owner whose password to reset.",
        _ => $"Usage: dotnet run --project Staffora.Blazor -- {AccountUnlockCommand.Command} <email>, with the email address of the account to unlock.",
    });
    Environment.ExitCode = 2;
    return;
}

var bootstrapEmail = command == PlatformBootstrap.Command ? commandEmail : null;
var builder = WebApplication.CreateBuilder(command is null ? args : args[2..]);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "Staffora.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    options.CheckEveryRequest();
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "Staffora.Antiforgery";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

builder.Services.AddProxyHeaders(builder.Configuration);

builder.Services.AddAuthorizationBuilder().AddSessionPolicies();
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.OnRefreshingPrincipal = context =>
{
    ImpersonationClaims.CarryOver(context.CurrentPrincipal, context.NewPrincipal);
    return Task.CompletedTask;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IRuntimeExecutionContext, RuntimeExecutionContext>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

var throwOnUnorderedRowLimit = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>(ApplicationDbContextOptions.ThrowOnUnorderedRowLimitSetting);
builder.Services.AddApplicationDbContexts(connectionString, throwOnUnorderedRowLimit);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, timeout: DatabaseHealthCheck.Timeout);

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = ApplicationDbContextOptions.IdentitySchemaVersion;

        options.User.RequireUniqueEmail = true;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<ApplicationRole>()
    .AddApplicationStores()

    .AddClaimsPrincipalFactory<ApplicationClaimsPrincipalFactory>()
    .AddSignInManager<ApplicationSignInManager>()
    .AddDefaultTokenProviders();

builder.Services.AddRateLimits(builder.Configuration);

builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<UserSessionService>();

builder.Services.AddScoped<ImpersonationAuthorizer>();
builder.Services.AddScoped<ImpersonationAuditWriter>();
builder.Services.AddScoped<ImpersonationService>();

builder.Services.AddTenantSettings(builder.Configuration);
builder.Services.AddScoped<CustomFieldService>();

builder.Services.AddWorkflowServices();

builder.Services.AddScoped<OrganizationService>();
builder.Services.AddScoped<TenantService>();
builder.Services.AddScoped<TenantRoleService>();
builder.Services.AddScoped<RoleHoldingService>();
builder.Services.AddScoped<TenantOnboardingService>();
builder.Services.AddScoped<PlatformConsoleService>();
builder.Services.AddScoped<UserService>();

builder.Services.AddScoped<ResourceService>();

builder.Services.AddScoped<QualificationService>();
builder.Services.AddScoped<WorkPatternService>();

builder.Services.AddScoped<Staffora.Services.Catalogue.ServiceCatalogueService>();

builder.Services.AddScoped<PatternAssignmentService>();
builder.Services.AddScoped<StaffAvailabilityService>();
builder.Services.AddScoped<ClosedDayService>();
builder.Services.AddScoped<IStaffAvailability>(provider => provider.GetRequiredService<StaffAvailabilityService>());

builder.Services.AddScoped<ResourceAbsenceService>();

builder.Services.AddScoped<CustomerContactService>();
builder.Services.AddScoped<CustomerService>();

builder.Services.AddScoped<StaffConversationService>();
builder.Services.AddScoped<SlotAssignmentService>();
builder.Services.AddScoped<RequestRoundService>();
builder.Services.AddScoped<StaffingService>();
builder.Services.AddScoped<JobSlotService>();
builder.Services.AddScoped<JobService>();

builder.Services.AddScoped<Staffora.Services.Coordination.CoordinationService>();

builder.Services.AddScoped<Staffora.Services.Home.TenantHomeService>();


builder.Services.AddScoped<Staffora.Services.Portal.OrderService>();


builder.Services.AddScoped<Staffora.Services.Search.GlobalSearchService>();

builder.Services.AddJobClock(builder.Configuration);

builder.Services.AddIntegrationOutbox(builder.Configuration);

builder.Services.AddRetention(builder.Configuration);

builder.Services.AddScoped<UserPasswordIssuer>();
builder.Services.AddScoped<IUserPasswordIssuer>(services => services.GetRequiredService<UserPasswordIssuer>());

builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddScoped<PlatformBootstrap>();

builder.Services.AddScoped<Staffora.Services.Demo.DemoDataSeeder>();

if (ServerCommands.Runs(command))
{
    builder.Services.AddServerCommands();
}

builder.Services.AddSingleton<SystemAccountService>();
builder.Services.AddSingleton<SystemOwnerPermissionsEnsure>();

builder.Services.AddErrorLog();
builder.Services.AddRequestLogScopes();
builder.Services.AddTelemetry(builder.Configuration);

builder.Services.AddRadzenComponents();

builder.Services.AddScoped<OpenStateEntityDialogs>();
builder.Services.AddDialogRoutes(typeof(Program).Assembly);

builder.Services.AddUrlState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    if (ServerCommands.Runs(command))
    {
        Environment.ExitCode = await ServerCommands.RunAsync(scope.ServiceProvider, command!, commandEmail!, Console.Out, Console.Error);
        return;
    }

    await using (var context = app.Services.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>().CreateSystemDbContext())
    {
        await ApplicationDatabase.MigrateAsync(context.Database);
    }

    if (bootstrapEmail is not null)
    {
        var bootstrap = await scope.ServiceProvider.GetRequiredService<PlatformBootstrap>().BootstrapAsync(bootstrapEmail);
        if (!bootstrap.IsSuccess)
        {
            Console.Error.WriteLine(bootstrap.Error);
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"Created the platform tenant and its first owner, {bootstrap.Value!.Email}.");
        Console.WriteLine($"Initial password, shown this once: {bootstrap.Value.InitialPassword}");
        Console.WriteLine("Sign in with it and keep it safe: no page changes it, and running the command again will not show it.");
        return;
    }

    Guid? demoPlatformOwnerId = null;
    if (StartupSeeding.IsSkipped(app.Configuration, app.Environment))
    {
        app.Logger.LogWarning("Startup seeding is switched off by {Setting}; the database is used as it was prepared.", StartupSeeding.SkipSetting);
    }
    else
    {
        if (StartupSeeding.IsRequested(app.Configuration))
        {
            app.Logger.LogWarning("{Setting} is set but ignored in the {Environment} environment; it counts only in Development, so startup seeds as usual.", StartupSeeding.SkipSetting, app.Environment.EnvironmentName);
        }

        demoPlatformOwnerId = await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync(includeDevelopmentData: app.Environment.IsDevelopment());
    }

    var systemAccounts = await scope.ServiceProvider.GetRequiredService<SystemAccountService>().EnsureForEveryTenantAsync();
    if (!systemAccounts.IsSuccess)
    {
        throw new InvalidOperationException(systemAccounts.Error);
    }

    var timeZones = await scope.ServiceProvider.GetRequiredService<TenantSettingsEnsureService>().EnsureTimeZoneForEveryTenantAsync();
    if (!timeZones.IsSuccess)
    {
        throw new InvalidOperationException(timeZones.Error);
    }

    var systemOwners = await scope.ServiceProvider.GetRequiredService<SystemOwnerPermissionsEnsure>().EnsureForEveryTenantAsync();
    if (!systemOwners.IsSuccess)
    {
        throw new InvalidOperationException(systemOwners.Error);
    }

    if (demoPlatformOwnerId is { } platformOwnerId && app.Environment.IsDevelopment())
    {
        var demo = await scope.ServiceProvider.GetRequiredService<Staffora.Services.Demo.DemoDataSeeder>().SeedAsync(platformOwnerId);
        if (!demo.IsSuccess)
        {
            throw new InvalidOperationException(demo.Error);
        }
    }
}

app.UseForwardedHeaders();

app.UseRequestLogIdentity();
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseSecurityHeaders();

app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);
app.UseRefusedRequests();

app.UseRequestStampOutage();
app.UseHttpsRedirection();

app.UseAuthorization();

app.UseReExecutedPageSession();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null)
    .Add(endpoint =>
    {
        if (endpoint.Metadata.OfType<ComponentTypeMetadata>().Any())
        {
            endpoint.Metadata.Add(new AuthorizeAttribute());
        }
    });

app.MapAdditionalIdentityEndpoints();

app.MapImpersonationEndpoints();

app.Run();
