using Staffora.Test.E2E.DialogHost;

namespace Staffora.Test;

internal static class TestEntryPoint
{
    public static int Main(string[] args)
    {
        if (args is [DialogHostServer.Command, ..])
        {
            return DialogHostServer.RunAsync().GetAwaiter().GetResult();
        }

        return args.Any(arg => arg is "--server" or "--internal-msbuild-node")
            ? Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult()
            : Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }
}
