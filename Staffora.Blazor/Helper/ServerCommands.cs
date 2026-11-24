using Staffora.Services.Authorization;

namespace Staffora.Blazor.Helper;

public static class ServerCommands
{
    public const int Succeeded = 0;

    public const int Failed = 1;

    public static bool Runs(string? command) => command is AccountUnlockCommand.Command or PlatformOwnerResetCommand.Command;

    public static string FailedLine(string command, Exception exception) =>
        $"{command} failed ({exception.GetType().Name}): the database did not answer or the account changed meanwhile. Check the connection string and run the command again.";

    public static IServiceCollection AddServerCommands(this IServiceCollection services)
    {
        services.AddScoped<AccountUnlockCommand>();
        services.AddScoped<PlatformOwnerResetCommand>();
        return services;
    }

    public static async Task<int> RunAsync(IServiceProvider services, string command, string email, TextWriter output, TextWriter error)
    {
        try
        {
            if (command == AccountUnlockCommand.Command)
            {
                var unlocked = await services.GetRequiredService<AccountUnlockCommand>().UnlockAsync(email);
                if (!unlocked.IsSuccess)
                {
                    await error.WriteLineAsync(unlocked.Error);
                    return Failed;
                }

                await output.WriteLineAsync($"Lifted the sign-in lockout of {email}. It can sign in again with its password.");
                return Succeeded;
            }

            var reset = await services.GetRequiredService<PlatformOwnerResetCommand>().ResetAsync(email);
            if (!reset.IsSuccess)
            {
                await error.WriteLineAsync(reset.Error);
                return Failed;
            }

            await output.WriteLineAsync($"Reset the password of the platform owner {reset.Value!.Email}. Every earlier sign-in of theirs has ended.");
            await output.WriteLineAsync($"New password, shown this once: {reset.Value.InitialPassword}");
            await output.WriteLineAsync("Sign in with it and keep it safe: no page changes it, and running the command again issues another.");
            return Succeeded;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(FailedLine(command, exception));
            return Failed;
        }
    }
}
