using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Services.Services;

namespace Staffora.Test.Integration.Infrastructure;

public sealed record StaffScenario(OnboardedScenario Onboarded, Guid RootId, Guid TeamId)
{
    public Guid TenantId => Onboarded.TenantId;

    public static async Task<StaffScenario> CreateAsync(RealDatabase database, CancellationToken cancellationToken, string name = "Brightline Staffing")
    {
        var onboarded = await OnboardedScenario.CreateAsync(database, cancellationToken, name, activate: true);
        var rootId = await TwoTenantScenario.RootUnitIdAsync(database, onboarded.Tenant, cancellationToken);
        await using var host = ServiceHost.Create(database, onboarded.OwnerRuntime());
        var teamId = await host.Get<OrganizationService>().CreateUnitIdAsync("Team North", OrganizationUnitType.Team, rootId, cancellationToken);
        return new StaffScenario(onboarded, rootId, teamId);
    }

    public async Task<Guid> AddPersonAsync(RealDatabase database, string firstName, CancellationToken cancellationToken)
    {
        await using var host = ServiceHost.Create(database, Onboarded.OwnerRuntime());
        var email = $"{firstName.ToLowerInvariant()}.{Guid.NewGuid():N}@brightline.example";
        var created = await host.Get<UserService>().CreateApplicationUserAsync(new ApplicationUser { FirstName = firstName, LastName = "Example", Email = email, UserName = email }, ApplicationUserWorkflowAttribute.CreateStep, cancellationToken);
        Assert.True(created.IsSuccess, created.Error);
        return created.Value;
    }

    public static async Task<List<Resource>> RecordsOfAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.Resources.AsNoTracking().Include(resource => resource.Memberships).Where(resource => resource.UserId == userId).ToListAsync(cancellationToken);
    }

    public static async Task<Resource> RecordOfAsync(RealDatabase database, Guid userId, CancellationToken cancellationToken) =>
        Assert.Single(await RecordsOfAsync(database, userId, cancellationToken));

    public static async Task<List<StateHistoryEntry>> HistoryOfAsync(RealDatabase database, Guid resourceId, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.StateHistoryEntries.AsNoTracking().Where(entry => entry.EntityType == nameof(Resource) && entry.EntityId == resourceId).OrderBy(entry => entry.Created).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
    }

    public static async Task<Guid> HoldingIdAsync(RealDatabase database, Guid userId, Guid roleId, Guid unitId, CancellationToken cancellationToken)
    {
        await using var context = database.CreateSystemContext();
        return await context.OrganizationUnitUserRoles.Where(holding => holding.UserId == userId && holding.TenantRoleId == roleId && holding.OrganizationUnitId == unitId).Select(holding => holding.Id).SingleAsync(cancellationToken);
    }
}
