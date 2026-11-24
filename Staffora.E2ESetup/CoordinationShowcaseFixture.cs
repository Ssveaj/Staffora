using Staffora.Services.Demo;

namespace Staffora.E2ESetup;

public static class CoordinationShowcaseFixture
{
    public static IReadOnlyList<DemoPerson> StaffMembers => CoordinationShowcase.StaffMembers;

    public static IReadOnlyList<string> Customers => CoordinationShowcase.Customers;

    public const int JobCount = CoordinationShowcase.JobCount;

    public static Task SeedAsync(IServiceProvider services, TimeProvider clock, CancellationToken cancellationToken) =>
        CoordinationShowcase.SeedAsync(services, clock, cancellationToken);
}
