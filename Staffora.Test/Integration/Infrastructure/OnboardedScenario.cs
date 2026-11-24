using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Services;
using Staffora.Services.Workflow;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record OnboardedScenario(ScenarioTenant Platform, ScenarioTenant Tenant, string OwnerEmail, string OwnerPassword, PresetRoles Roles)
{
    public Guid TenantId => Tenant.TenantId;

    public Guid OwnerId => Tenant.OwnerId;

    public static async Task<OnboardedScenario> CreateAsync(RealDatabase database, CancellationToken cancellationToken, string name = "Northwind Staffing", bool activate = false)
    {
        var platform = await TwoTenantScenario.AddPlatformTenantAsync(database, cancellationToken);
        var tenantName = TwoTenantScenario.UniqueName(name);
        var email = $"owner.{Guid.NewGuid():N}@{tenantName.Replace(' ', '-').ToLowerInvariant()}.example";
        await using var host = ServiceHost.Create(database, platform.PlatformOwnerRuntime());
        var onboarded = await host.Get<TenantOnboardingService>().OnboardTenantAsync(new TenantOnboarding(tenantName, null, "Robin", "Hale", email), cancellationToken);
        Assert.True(onboarded.IsSuccess, onboarded.Error);
        var tenant = new ScenarioTenant(onboarded.Value!.TenantId, tenantName, onboarded.Value.OwnerId);
        if (activate)
        {
            var activated = await host.Get<WorkflowService>().TransitionEntityAsync<TenantState, Tenant>(tenant.TenantId, Staffora.Core.Workflow.WorkflowAttributes.TenantWorkflowAttribute.ActivateStep, cancellationToken: cancellationToken);
            Assert.True(activated.IsSuccess, activated.Error);
        }

        return new OnboardedScenario(platform, tenant, email, onboarded.Value.InitialPassword, await PresetRoles.ReadAsync(database, tenant.TenantId, cancellationToken));
    }

    public IRuntimeExecutionContext OwnerRuntime() => Tenant.Runtime();

    public IRuntimeExecutionContext RuntimeOf(Guid userId) => Tenant.RuntimeOf(userId);
}

public sealed record PresetRoles(Guid SystemOwner, Guid Administrator, Guid Coordinator, Guid Staff)
{
    public static async Task<PresetRoles> ReadAsync(RealDatabase database, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        var roles = await context.TenantRoles.AsNoTracking().Where(role => role.TenantId == tenantId).Select(role => new { role.Id, role.Name, role.IsSystemOwner }).ToListAsync(cancellationToken);
        Guid Named(string roleName) => roles.Single(role => role.Name == roleName && !role.IsSystemOwner).Id;
        return new PresetRoles(roles.Single(role => role.IsSystemOwner).Id, Named(RolePresets.Administrator.Name), Named(RolePresets.Coordinator.Name), Named(RolePresets.Staff.Name));
    }
}
