using Microsoft.Extensions.Logging;

namespace Staffora.E2ESetup;

internal static class Program
{
    public const string ConnectionVariable = "STAFFORA_E2E_CONNECTION";

    public const string StateFileVariable = "STAFFORA_E2E_STATE_FILE";

    public static async Task<int> Main()
    {
        string stateFilePath;
        try
        {
            var chosen = Environment.GetEnvironmentVariable(StateFileVariable);
            stateFilePath = string.IsNullOrWhiteSpace(chosen) ? FoundationStateFile.DefaultPath : Path.GetFullPath(chosen);
        }
        catch (InvalidOperationException ex)
        {
            await Console.Error.WriteLineAsync($"Staffora.E2ESetup: {ex.Message}");
            return 1;
        }

        var prep = new FoundationPrep(TimeProvider.System, Console.Out, logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        var result = await prep.RunAsync(Environment.GetEnvironmentVariable(ConnectionVariable), stateFilePath);
        if (!result.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"Staffora.E2ESetup: preparation failed, no foundation state written. {result.Error}");
            return 1;
        }

        Console.WriteLine($"Staffora.E2ESetup: the foundation is ready ({string.Join(", ", result.Value!.Personas)}).");
        return 0;
    }
}
