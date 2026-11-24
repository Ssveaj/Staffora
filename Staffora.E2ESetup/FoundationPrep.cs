using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Data;
using Staffora.Data.Seed;
using Staffora.Services.Settings;

namespace Staffora.E2ESetup;

public sealed class FoundationPrep
{
    private readonly TimeProvider clock;
    private readonly TextWriter log;
    private readonly Action<ILoggingBuilder>? configureLogging;

    public FoundationPrep(TimeProvider clock, TextWriter log, Action<ILoggingBuilder>? configureLogging = null)
    {
        this.clock = clock;
        this.log = log;
        this.configureLogging = configureLogging;
    }

    public async Task<Result<FoundationState>> RunAsync(string? connectionString, string stateFilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            FoundationStateFile.RemoveStale(stateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<FoundationState>.Failure($"The previous foundation state at {stateFilePath} could not be removed: {ex.Message}");
        }

        try
        {
            var connection = E2EDatabaseGuard.EnsureE2EConnection(connectionString);
            var database = new SqlConnectionStringBuilder(connection).InitialCatalog;
            await using var services = PrepServices.Build(connection, this.clock, this.configureLogging);

            Progress($"migrating {database}");
            await using (var context = services.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>().CreateSystemDbContext())
            {
                await ApplicationDatabase.MigrateAsync(context.Database, cancellationToken);
            }

            Progress("emptying every table except the migration history");
            await DatabaseReset.ResetAsync(connection, cancellationToken);

            Progress("seeding through the application's services");
            var passwords = await SeedAsync(services, cancellationToken);

            Progress($"onboarding and setting up {JourneyTenantFixture.TenantName} through the services, as its people do");
            var targets = await JourneyTenantFixture.SeedAsync(services, cancellationToken);

            Progress("setting up the busy week of the coordination workspace through the services");
            await CoordinationShowcaseFixture.SeedAsync(services, this.clock, cancellationToken);

            Progress("seeding the error-log rows the smoke journey filters");
            var errorLog = await ErrorLogFixture.SeedAsync(services, this.clock, cancellationToken);

            Progress("pinning every tenant's time zone to the platform's");
            var timeZones = await services.GetRequiredService<TenantSettingsEnsureService>().EnsureTimeZoneForEveryTenantAsync(cancellationToken);
            if (!timeZones.IsSuccess || timeZones.Value!.FailedTenantIds.Count > 0)
            {
                throw new InvalidOperationException($"The tenants' time zones could not be pinned: {timeZones.Error ?? string.Join(", ", timeZones.Value!.FailedTenantIds)}");
            }

            var tenantPersonas = targets.Personas.ToDictionary(each => each.Key);
            var personas = new List<FoundationPersona>();
            foreach (var contract in Personas.All)
            {
                Progress($"verifying the persona {contract.Key}");
                var prepared = tenantPersonas.TryGetValue(contract.Key, out var tenantPersona)
                    ? tenantPersona
                    : new FoundationPersona(contract.Key, contract.Actor, contract.Email, passwords[contract.Key]);
                if (prepared.Email != contract.Email)
                {
                    throw new InvalidOperationException($"The persona '{contract.Key}' was prepared for {prepared.Email}, not {contract.Email}.");
                }

                await PersonaVerifier.VerifyAsync(services, contract, prepared.Password, cancellationToken);
                personas.Add(prepared);
            }

            var state = new FoundationState(this.clock.GetUtcNow(), database, personas, targets.Target, targets.Administrator, targets.Viewer, errorLog) { Staffing = targets.Staffing };
            await FoundationStateFile.WriteAtomicallyAsync(stateFilePath, state, cancellationToken);
            Progress($"wrote {Path.GetFullPath(stateFilePath)}");
            return Result<FoundationState>.Success(state);
        }
        catch (Exception ex)
        {
            TryRemove(stateFilePath);
            return Result<FoundationState>.Failure(ex.Message);
        }
    }

    private static async Task<Dictionary<string, string>> SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        await provider.GetRequiredService<DatabaseSeeder>().SeedAsync(includeDevelopmentData: false, cancellationToken);

        var owner = await provider.GetRequiredService<PlatformBootstrap>().BootstrapAsync(Personas.PlatformOwner.Email, cancellationToken);
        if (!owner.IsSuccess)
        {
            throw new InvalidOperationException($"The platform owner could not be bootstrapped: {owner.Error}");
        }

        var accounts = await provider.GetRequiredService<SystemAccountService>().EnsureForEveryTenantAsync(cancellationToken);
        if (!accounts.IsSuccess)
        {
            throw new InvalidOperationException($"The system accounts could not be ensured: {accounts.Error}");
        }

        return new Dictionary<string, string> { [Personas.PlatformOwner.Key] = owner.Value!.InitialPassword };
    }

    private void Progress(string message) => this.log.WriteLine($"Staffora.E2ESetup: {message}");

    private static void TryRemove(string stateFilePath)
    {
        try
        {
            FoundationStateFile.RemoveStale(stateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
