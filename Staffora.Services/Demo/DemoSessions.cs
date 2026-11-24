using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Services.Authorization;

namespace Staffora.Services.Demo;

public sealed record DemoPerson(string FirstName, string LastName, string Email)
{
    public string Name => $"{this.FirstName} {this.LastName}";
}

public static class DemoSessions
{
    public const string NotDevelopment = "A demo session opens in Development only.";

    public static async Task<AsyncServiceScope> SignInAsync(IServiceProvider services, string email)
    {
        EnsureDevelopment(services);
        var scope = services.CreateAsyncScope();
        try
        {
            var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"{email} does not exist, so nobody signs in as them.");
            await OpenSessionAsync(scope.ServiceProvider, user);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    public static async Task<AsyncServiceScope> SignInAsync(IServiceProvider services, Guid userId)
    {
        EnsureDevelopment(services);
        var scope = services.CreateAsyncScope();
        try
        {
            var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(userId.ToString())
                ?? throw new InvalidOperationException($"The user {userId} does not exist, so nobody signs in as them.");
            await OpenSessionAsync(scope.ServiceProvider, user);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private static void EnsureDevelopment(IServiceProvider services)
    {
        if (services.GetService<IHostEnvironment>() is not { } environment || !environment.IsDevelopment())
        {
            throw new InvalidOperationException(NotDevelopment);
        }
    }

    private static async Task OpenSessionAsync(IServiceProvider provider, ApplicationUser user)
    {
        var principal = await provider.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);
        if (provider.GetRequiredService<AuthenticationStateProvider>() is not IHostEnvironmentAuthenticationStateProvider host)
        {
            throw new InvalidOperationException("The scope's authentication state takes no signed-in user from its host.");
        }

        host.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await provider.GetRequiredService<UserSessionService>().InitializeFromClaimsAsync();
        if (provider.GetRequiredService<IRuntimeExecutionContext>().Current.UserId != user.Id)
        {
            throw new InvalidOperationException($"{user.Email} could not open a session.");
        }
    }
}

internal static class DemoSteps
{
    public static T Expect<T>(Result<T> result, string what) =>
        result.IsSuccess && result.Value is { } value ? value : throw new InvalidOperationException($"Could not {what}: {result.Error}");

    public static void Expect(Result result, string what)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Could not {what}: {result.Error}");
        }
    }

    public static void Expect(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not {what}: {string.Join(" ", result.Errors.Select(error => error.Description))}");
        }
    }
}
