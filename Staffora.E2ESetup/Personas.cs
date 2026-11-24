using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Services.Authorization;

namespace Staffora.E2ESetup;

public sealed record PersonaContract(string Key, string Actor, string Email, bool HasPlatformReach, IReadOnlyList<RolePermission> RequiredPermissions);

public static class Personas
{
    public static readonly PersonaContract PlatformOwner = new("platformOwner", "Platform owner", "platform.owner@example.com", HasPlatformReach: true, Enum.GetValues<RolePermission>());

    public static readonly PersonaContract Coordinator = new("coordinator", "Coordinator", JourneyTenantFixture.Coordinator.Email, HasPlatformReach: false, RolePresets.Coordinator.Permissions);

    public static readonly PersonaContract Staff = new("staff", "Staff", JourneyTenantFixture.Staff.Email, HasPlatformReach: false, RolePresets.Staff.Permissions);

    public static readonly PersonaContract SecondStaff = new("secondStaff", "Staff", JourneyTenantFixture.SecondStaff.Email, HasPlatformReach: false, RolePresets.Staff.Permissions);

    public static readonly PersonaContract CustomerAdmin = new("customerAdmin", "Customer administrator", JourneyTenantFixture.CustomerAdmin.Email, HasPlatformReach: false, RolePresets.CustomerAdmin.Permissions);

    public static readonly PersonaContract Orderer = new("orderer", "Orderer", JourneyTenantFixture.Orderer.Email, HasPlatformReach: false, RolePresets.Orderer.Permissions);

    public static readonly IReadOnlyList<PersonaContract> All = [PlatformOwner, Coordinator, Staff, SecondStaff, CustomerAdmin, Orderer];
}

public sealed class PersonaVerificationException : Exception
{
    public PersonaVerificationException(string message) : base(message)
    {
    }
}

public static class PersonaVerifier
{
    public static async Task VerifyAsync(IServiceProvider services, PersonaContract contract, string password, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(contract.Email)
            ?? throw Refused(contract, "no user with that email exists after seeding");

        var signIn = provider.GetRequiredService<SignInManager<ApplicationUser>>();
        var checkedPassword = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);
        if (!checkedPassword.Succeeded)
        {
            throw Refused(contract, $"signing in with the issued password failed ({checkedPassword})");
        }

        provider.GetRequiredService<PersonaCookie>().SignIn(await signIn.CreateUserPrincipalAsync(user));
        await provider.GetRequiredService<UserSessionService>().InitializeFromClaimsAsync();
        var session = provider.GetRequiredService<IRuntimeExecutionContext>().Current;
        if (session.UserId != user.Id || session.IsImpersonating)
        {
            throw Refused(contract, "its sign-in cookie does not resolve to a session of its own");
        }

        if (session.HasPlatformReach() != contract.HasPlatformReach)
        {
            throw Refused(contract, contract.HasPlatformReach ? "its session has no platform reach" : "its session has platform reach it must not have");
        }

        var permissions = provider.GetRequiredService<PermissionService>();
        var missing = new List<RolePermission>();
        foreach (var permission in contract.RequiredPermissions)
        {
            if (!await permissions.HasPermissionAsync(permission, cancellationToken))
            {
                missing.Add(permission);
            }
        }

        if (missing.Count > 0)
        {
            throw Refused(contract, $"its session lacks {string.Join(", ", missing)}");
        }
    }

    private static PersonaVerificationException Refused(PersonaContract contract, string problem) =>
        new($"The persona '{contract.Key}' ({contract.Actor}, {contract.Email}) failed verification: {problem}.");
}
