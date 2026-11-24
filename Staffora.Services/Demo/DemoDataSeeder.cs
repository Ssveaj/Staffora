using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Services.Jobs;
using Staffora.Services.Platform;
using static Staffora.Services.Demo.DemoSteps;

namespace Staffora.Services.Demo;

public sealed record DemoDataOutcome(IReadOnlyList<string> Built);

public sealed class DemoDataSeeder
{
    public const string NotDevelopment = "The demo data is built in Development only.";

    public const string OwnerPassword = "Tenant123!";

    public const string MemberPassword = "User123!";

    private readonly IServiceProvider services;
    private readonly IHostEnvironment environment;
    private readonly TimeProvider clock;
    private readonly JobClock jobClock;
    private readonly ILogger<DemoDataSeeder> log;

    public DemoDataSeeder(IServiceProvider services, IHostEnvironment environment, TimeProvider clock, JobClock jobClock, ILogger<DemoDataSeeder> log)
    {
        this.services = services;
        this.environment = environment;
        this.clock = clock;
        this.jobClock = jobClock;
        this.log = log;
    }

    public static IReadOnlyList<DemoBusiness> Businesses { get; } = [LindaleDemo.Business, ClearshineDemo.Business, NordbyggDemo.Business, OmsorgenDemo.Business, NordvaktDemo.Business];

    public static string PasswordOf(DemoPerson person) =>
        Businesses.Any(business => business.Owners.Contains(person) || business.Portal.CustomerAdministrator == person) ? OwnerPassword : MemberPassword;

    public async Task<Result<DemoDataOutcome>> SeedAsync(Guid platformOwnerId, CancellationToken cancellationToken = default)
    {
        if (!this.environment.IsDevelopment())
        {
            return Result<DemoDataOutcome>.Failure(NotDevelopment);
        }

        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var console = await DemoSessions.SignInAsync(this.services, platformOwnerId))
            {
                var tenants = console.ServiceProvider.GetRequiredService<PlatformConsoleService>();
                foreach (var name in Businesses.Select(business => business.TenantName))
                {
                    var found = Expect(await tenants.QueryTenantsAsync(new ListQuery { Search = name }), "list the tenants");
                    if (found.Rows.Any(tenant => string.Equals(tenant.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        existing.Add(name);
                    }
                }
            }

            var pending = Businesses.Where(business => !existing.Contains(business.TenantName)).ToList();
            foreach (var business in Businesses.Except(pending))
            {
                this.log.LogInformation("The demo tenant {Tenant} exists already, so the demo leaves it as it is; a fresh database builds it anew.", business.TenantName);
            }

            foreach (var business in pending)
            {
                await using var builder = new DemoTenantBuilder(this.services, business, PasswordOf, this.clock);
                await builder.BuildAsync(platformOwnerId, cancellationToken);
                this.log.LogInformation("Built the demo tenant {Tenant} ({Trade}) with {Customer}'s portal.", business.TenantName, business.Trade, business.Portal.Customer);
            }

            var built = pending.Select(business => business.TenantName).ToList();
            if (built.Count > 0)
            {
                await this.jobClock.RunOnceAsync(cancellationToken);
                await this.jobClock.RunOnceAsync(cancellationToken);
                await DemoTenantBuilder.DeliverAsync(this.services, cancellationToken);

                await DemoErrorLog.SeedAsync(this.services, this.clock, platformOwnerId, cancellationToken);
            }

            return Result<DemoDataOutcome>.Success(new DemoDataOutcome(built));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Building the demo data failed.");
            return Result<DemoDataOutcome>.Failure($"The demo data could not be built: {exception.Message}");
        }
    }
}
