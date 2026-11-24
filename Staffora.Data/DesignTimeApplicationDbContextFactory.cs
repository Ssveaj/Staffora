using Microsoft.EntityFrameworkCore.Design;

namespace Staffora.Data;

public sealed class DesignTimeApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public const string ConnectionArgument = "--connection";
    public const string ConnectionEnvironmentVariable = "ConnectionStrings__DefaultConnection";
    public const string LocalDevelopmentConnection = @"Server=(localdb)\mssqllocaldb;Database=stafforadb;Trusted_Connection=True;MultipleActiveResultSets=true";

    public ApplicationDbContext CreateDbContext(string[] args) => new(ApplicationDbContextOptions.Create(ResolveConnectionString(args)));

    public static string ResolveConnectionString(string[] args)
    {
        var index = Array.IndexOf(args, ConnectionArgument);
        if (index >= 0)
        {
            return index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])
                ? args[index + 1]
                : throw new ArgumentException($"{ConnectionArgument} needs a connection string after it.", nameof(args));
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? LocalDevelopmentConnection : fromEnvironment;
    }
}
