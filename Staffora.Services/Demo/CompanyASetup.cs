using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.CustomFields;
using Staffora.Services.Customers;
using Staffora.Services.Resources;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using static Staffora.Services.Demo.DemoSteps;

namespace Staffora.Services.Demo;

public sealed record CompanyASetupResult(Guid TenantId, Guid RootUnitId, Guid HeadOfficeId, Guid CustomerId, Guid PortalTenantId, Guid QualificationId, IReadOnlyDictionary<string, string> Passwords);

public static class CompanyASetup
{
    public const string TenantName = "Company A";

    public const string HeadOfficeName = "Head office";

    public const string TeamField = "Team";

    public const string GlobalField = "Employee number";

    public const string ViewerRole = "Viewer";

    public const string CustomerName = "Brightpath Learning";

    public const string QualificationName = "Mathematics";

    public static DemoPerson Administrator { get; } = new("Alex", "Morgan", "alex.morgan@company-a.example.com");

    public static DemoPerson Target { get; } = new("Sam", "Taylor", "sam.taylor@company-a.example.com");

    public static DemoPerson Viewer { get; } = new("Robin", "Lee", "robin.lee@company-a.example.com");

    public static DemoPerson Coordinator { get; } = new("Casey", "Lind", "casey.lind@company-a.example.com");

    public static DemoPerson Staff { get; } = new("Kim", "Berg", "kim.berg@company-a.example.com");

    public static DemoPerson SecondStaff { get; } = new("Jamie", "Fors", "jamie.fors@company-a.example.com");

    public static DemoPerson CustomerAdmin { get; } = new("Morgan", "Reed", "morgan.reed@brightpath.example.com");

    public static DemoPerson Orderer { get; } = new("Riley", "Quinn", "riley.quinn@brightpath.example.com");

    public static IReadOnlyList<RolePermission> ViewerPermissions { get; } = [RolePermission.UsersView, RolePermission.OrganizationUnitsView, RolePermission.SettingsView];

    public static async Task<CompanyASetupResult> SeedAsync(IServiceProvider services, Guid platformOwnerId, Func<DemoPerson, string?> chosenPassword, CancellationToken cancellationToken)
    {
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Guid tenantId;
        string ownerInitialPassword;
        await using (var console = await DemoSessions.SignInAsync(services, platformOwnerId))
        {
            var onboarded = Expect(await console.ServiceProvider.GetRequiredService<TenantOnboardingService>().OnboardTenantAsync(
                new TenantOnboarding(TenantName, "A staffing company that sends staff to its customers' jobs.", Administrator.FirstName, Administrator.LastName, Administrator.Email),
                cancellationToken), $"onboard {TenantName}");
            (tenantId, ownerInitialPassword) = (onboarded.TenantId, onboarded.InitialPassword);

            await AddActiveFieldAsync(console.ServiceProvider.GetRequiredService<CustomFieldService>(), new ApplicationUserAttributeDefinition { Name = GlobalField, ValueType = AttributeValueType.Text }, cancellationToken);
        }

        Guid rootId;
        Guid headOfficeId;
        Guid qualificationId;
        await using (var owner = await DemoSessions.SignInAsync(services, Administrator.Email))
        {
            var provider = owner.ServiceProvider;
            await KeepChosenPasswordAsync(provider, Administrator, ownerInitialPassword, chosenPassword, passwords);

            var users = provider.GetRequiredService<UserService>();
            await CreateUserAsync(users, Target, cancellationToken);
            var viewerId = await CreateUserAsync(users, Viewer, cancellationToken);

            var tree = Expect(await provider.GetRequiredService<OrganizationService>().FetchUnitTreeAsync(tenantId), "read the tenant's tree");
            rootId = tree.Single(unit => unit.IsRoot).Id;

            var roles = provider.GetRequiredService<TenantRoleService>();
            var holdings = provider.GetRequiredService<RoleHoldingService>();
            var viewerRole = new TenantRole { Name = ViewerRole, Description = "Views users, units and settings but changes nothing." };
            foreach (var permission in ViewerPermissions)
            {
                viewerRole.Permissions.Add(new TenantRolePermission { Permission = permission });
            }

            var viewerRoleId = Expect(await roles.CreateTenantRoleAsync(viewerRole, TenantRoleWorkflowAttribute.CreateStep, cancellationToken), $"create the {ViewerRole} role");
            Expect(await holdings.AssignUserToRoleAsync(viewerId, rootId, viewerRoleId, tenantId), $"give {Viewer.Email} the {ViewerRole} role");

            headOfficeId = Expect(await provider.GetRequiredService<OrganizationService>().CreateOrganizationUnitAsync(
                new OrganizationUnit { Name = HeadOfficeName, UnitType = OrganizationUnitType.Branch, ParentId = rootId },
                OrganizationUnitWorkflowAttribute.CreateStep,
                cancellationToken), $"add the {HeadOfficeName}");

            var presets = Expect(await roles.GetTenantRolesAsync(tenantId), "read the role presets");
            Guid Preset(RolePreset preset) => presets.Single(role => role.Name == preset.Name && !role.IsSystemOwner).Id;
            var staffIds = new List<Guid>();
            foreach (var (person, unitId, preset) in new[]
            {
                (Coordinator, rootId, RolePresets.Coordinator),
                (Staff, headOfficeId, RolePresets.Staff),
                (SecondStaff, headOfficeId, RolePresets.Staff),
            })
            {
                var password = chosenPassword(person) ?? UserPasswords.Generate();
                var created = Expect(await users.CreateUserAsync(new ApplicationUser { FirstName = person.FirstName, LastName = person.LastName, Email = person.Email, UserName = person.Email }, tenantId, password), $"create {person.Email}");
                Expect(await holdings.AssignUserToRoleAsync(created.Id, unitId, Preset(preset), tenantId), $"give {person.Email} the {preset.Name} role");
                passwords[person.Email] = password;
                if (preset == RolePresets.Staff)
                {
                    staffIds.Add(created.Id);
                }
            }

            var qualifications = provider.GetRequiredService<QualificationService>();
            qualificationId = Expect(await qualifications.CreateQualificationAsync(new Qualification { Name = QualificationName }, QualificationWorkflowAttribute.CreateStep, cancellationToken), $"add the qualification {QualificationName}");
            foreach (var staffId in staffIds)
            {
                Expect(await qualifications.GiveQualificationAsync(staffId, qualificationId, cancellationToken), $"give a staff member {QualificationName}");
            }
        }

        await using (var console = await DemoSessions.SignInAsync(services, platformOwnerId))
        {
            var fields = console.ServiceProvider.GetRequiredService<CustomFieldService>();
            foreach (var definition in new IEntityAttributeDefinition[]
            {
                new ApplicationUserAttributeDefinition
                {
                    Name = TeamField,
                    ValueType = AttributeValueType.Choice,
                    Options = [new(string.Empty, "Teaching"), new(string.Empty, "Planning"), new(string.Empty, "Support", IsRetired: true)],
                },
                new ApplicationUserAttributeDefinition { Name = "Badge number", ValueType = AttributeValueType.Text },
                new ApplicationUserAttributeDefinition { Name = "Has keys", ValueType = AttributeValueType.Boolean },
                new OrganizationUnitAttributeDefinition { Name = "Opened", ValueType = AttributeValueType.Date },
                new OrganizationUnitAttributeDefinition { Name = "Rooms", ValueType = AttributeValueType.Integer },
                new OrganizationUnitAttributeDefinition { Name = "Floor area", ValueType = AttributeValueType.Number },
                new JobAttributeDefinition { Name = "Room", ValueType = AttributeValueType.Text, Audience = JobFieldAudience.AssignedStaff },
                new JobAttributeDefinition { Name = "Budget code", ValueType = AttributeValueType.Text },
            })
            {
                definition.TenantId = tenantId;
                await AddActiveFieldAsync(fields, definition, cancellationToken);
            }

            var tenants = console.ServiceProvider.GetRequiredService<TenantService>();
            var updater = Expect(await tenants.LoadTenantUpdaterAsync(tenantId, cancellationToken), $"open {TenantName}");
            Expect(await tenants.UpdateTenantAndTransitionAsync(updater, TenantWorkflowAttribute.ActivateStep, comment: null, cancellationToken), $"activate {TenantName}");
        }

        Guid customerId;
        await using (var coordinator = await DemoSessions.SignInAsync(services, Coordinator.Email))
        {
            customerId = Expect(await coordinator.ServiceProvider.GetRequiredService<CustomerService>().CreateCustomerAsync(new Customer { Name = CustomerName }, CustomerWorkflowAttribute.ActivateStep, cancellationToken), $"add the customer {CustomerName}");
        }

        string adminInitialPassword;
        await using (var owner = await DemoSessions.SignInAsync(services, Administrator.Email))
        {
            var customers = owner.ServiceProvider.GetRequiredService<CustomerService>();
            var updater = Expect(await customers.LoadCustomerUpdaterAsync(customerId, cancellationToken), $"open {CustomerName}");
            adminInitialPassword = Expect(await customers.EnableCustomerPortalAsync(updater, new CustomerPortalRequest(rootId, CustomerAdmin.FirstName, CustomerAdmin.LastName, CustomerAdmin.Email), cancellationToken), $"enable the portal of {CustomerName}").InitialPassword;
        }

        Guid portalTenantId;
        await using (var customerAdmin = await DemoSessions.SignInAsync(services, CustomerAdmin.Email))
        {
            var provider = customerAdmin.ServiceProvider;
            await KeepChosenPasswordAsync(provider, CustomerAdmin, adminInitialPassword, chosenPassword, passwords);
            portalTenantId = provider.GetRequiredService<IRuntimeExecutionContext>().TenantId!.Value;
            var password = chosenPassword(Orderer) ?? UserPasswords.Generate();
            Expect(await provider.GetRequiredService<UserService>().CreateUserAsync(
                new ApplicationUser { FirstName = Orderer.FirstName, LastName = Orderer.LastName, Email = Orderer.Email, UserName = Orderer.Email },
                portalTenantId,
                password,
                ApplicationUserWorkflowAttribute.PortalCreateOrdererStep), $"create the orderer {Orderer.Email}");
            passwords[Orderer.Email] = password;
        }

        return new CompanyASetupResult(tenantId, rootId, headOfficeId, customerId, portalTenantId, qualificationId, passwords);
    }

    private static async Task AddActiveFieldAsync(CustomFieldService fields, IEntityAttributeDefinition definition, CancellationToken cancellationToken)
    {
        switch (definition)
        {
            case ApplicationUserAttributeDefinition user:
                var userField = Expect(await fields.CreateApplicationUserAttributeDefinitionAsync(user, UserFieldWorkflowAttribute.CreateStep, cancellationToken), $"add the field {user.Name}");
                var userUpdater = Expect(await fields.LoadApplicationUserAttributeDefinitionUpdaterAsync(userField, cancellationToken), $"open the field {user.Name}");
                Expect(await fields.UpdateApplicationUserAttributeDefinitionAndTransitionAsync(userUpdater, UserFieldWorkflowAttribute.ActivateStep, comment: null, cancellationToken), $"activate the field {user.Name}");
                break;

            case OrganizationUnitAttributeDefinition unit:
                var unitField = Expect(await fields.CreateOrganizationUnitAttributeDefinitionAsync(unit, UnitFieldWorkflowAttribute.CreateStep, cancellationToken), $"add the field {unit.Name}");
                var unitUpdater = Expect(await fields.LoadOrganizationUnitAttributeDefinitionUpdaterAsync(unitField, cancellationToken), $"open the field {unit.Name}");
                Expect(await fields.UpdateOrganizationUnitAttributeDefinitionAndTransitionAsync(unitUpdater, UnitFieldWorkflowAttribute.ActivateStep, comment: null, cancellationToken), $"activate the field {unit.Name}");
                break;

            case JobAttributeDefinition job:
                var jobField = Expect(await fields.CreateJobAttributeDefinitionAsync(job, JobFieldWorkflowAttribute.CreateStep, cancellationToken), $"add the field {job.Name}");
                var jobUpdater = Expect(await fields.LoadJobAttributeDefinitionUpdaterAsync(jobField, cancellationToken), $"open the field {job.Name}");
                Expect(await fields.UpdateJobAttributeDefinitionAndTransitionAsync(jobUpdater, JobFieldWorkflowAttribute.ActivateStep, comment: null, cancellationToken), $"activate the field {job.Name}");
                break;

            default:
                throw new ArgumentException($"{definition.GetType().Name} is no custom field table.", nameof(definition));
        }
    }

    private static async Task KeepChosenPasswordAsync(IServiceProvider provider, DemoPerson person, string issued, Func<DemoPerson, string?> chosenPassword, Dictionary<string, string> passwords)
    {
        if (chosenPassword(person) is not { } chosen)
        {
            passwords[person.Email] = issued;
            return;
        }

        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(person.Email) ?? throw new InvalidOperationException($"{person.Email} does not exist.");
        Expect(await users.ChangePasswordAsync(user, issued, chosen), $"change the password of {person.Email}");
        passwords[person.Email] = chosen;
    }

    private static async Task<Guid> CreateUserAsync(UserService users, DemoPerson person, CancellationToken cancellationToken) =>
        Expect(await users.CreateApplicationUserAsync(
            new ApplicationUser { FirstName = person.FirstName, LastName = person.LastName, Email = person.Email, UserName = person.Email },
            ApplicationUserWorkflowAttribute.CreateStep,
            cancellationToken), $"create {person.Email}");
}
