using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Catalogue;
using Staffora.Services.Demo;
using Staffora.Services.Integration;

namespace Staffora.E2ESetup;

public sealed record FoundationTenantUser(string Tenant, string Name, string Email);

public sealed record JourneyTenantUsers(FoundationTenantUser Target, FoundationTenantUser Administrator, FoundationTenantUser Viewer, IReadOnlyList<FoundationPersona> Personas, FoundationStaffing Staffing);

public static class JourneyTenantFixture
{
    public const string TenantName = CompanyASetup.TenantName;

    public const string HeadOfficeName = CompanyASetup.HeadOfficeName;

    public const string TeamField = CompanyASetup.TeamField;

    public const string GlobalField = CompanyASetup.GlobalField;

    public const string ViewerRole = CompanyASetup.ViewerRole;

    public const string CustomerName = CompanyASetup.CustomerName;

    public const string QualificationName = CompanyASetup.QualificationName;

    public const string ServiceName = "Classroom assistant";

    public static string AdministratorEmail => CompanyASetup.Administrator.Email;

    public static IReadOnlyList<RolePermission> ViewerPermissions => CompanyASetup.ViewerPermissions;

    public static DemoPerson Coordinator => CompanyASetup.Coordinator;

    public static DemoPerson Staff => CompanyASetup.Staff;

    public static DemoPerson SecondStaff => CompanyASetup.SecondStaff;

    public static DemoPerson CustomerAdmin => CompanyASetup.CustomerAdmin;

    public static DemoPerson Orderer => CompanyASetup.Orderer;

    public static async Task<JourneyTenantUsers> SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        Guid platformOwnerId;
        await using (var scope = services.CreateAsyncScope())
        {
            platformOwnerId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(Personas.PlatformOwner.Email)
                ?? throw new InvalidOperationException($"{Personas.PlatformOwner.Email}, who onboards {TenantName}, does not exist.")).Id;
        }

        var built = await CompanyASetup.SeedAsync(services, platformOwnerId, _ => null, cancellationToken);

        await using (var administrator = await DemoSessions.SignInAsync(services, CompanyASetup.Administrator.Email))
        {
            var service = new Service { Name = ServiceName, Description = "Helps in the classroom.", DefaultSlotCount = 2, OfferedInPortal = true };
            service.Qualifications.Add(new ServiceQualification { QualificationId = built.QualificationId, Level = RequirementLevel.Required });
            var created = await administrator.ServiceProvider.GetRequiredService<ServiceCatalogueService>().CreateServiceAsync(service, ServiceWorkflowAttribute.CreateStep, cancellationToken);
            if (!created.IsSuccess)
            {
                throw new InvalidOperationException($"Could not add the service {ServiceName}: {created.Error}");
            }
        }

        await services.GetRequiredService<IntegrationOutboxDispatcher>().RunOnceAsync(cancellationToken);
        var personas = new[]
        {
            (Personas.Coordinator, Coordinator),
            (Personas.Staff, Staff),
            (Personas.SecondStaff, SecondStaff),
            (Personas.CustomerAdmin, CustomerAdmin),
            (Personas.Orderer, Orderer),
        }.Select(each => new FoundationPersona(each.Item1.Key, each.Item1.Actor, each.Item2.Email, built.Passwords[each.Item2.Email]) { Name = each.Item2.Name }).ToList();

        return new JourneyTenantUsers(InTenant(CompanyASetup.Target), InTenant(CompanyASetup.Administrator), InTenant(CompanyASetup.Viewer), personas, new FoundationStaffing(CustomerName, QualificationName, ServiceName));
    }

    private static FoundationTenantUser InTenant(DemoPerson person) => new(TenantName, person.Name, person.Email);
}
