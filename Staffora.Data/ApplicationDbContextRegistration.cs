using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Staffora.Core.Execution;

namespace Staffora.Data;

public static class ApplicationDbContextRegistration
{
    public static IServiceCollection AddApplicationDbContexts(this IServiceCollection services, string connectionString, bool throwOnUnorderedRowLimit = false)
    {
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseApplicationSqlServer(connectionString, throwOnUnorderedRowLimit));
        services.AddSingleton<IApplicationDbContextFactory<ApplicationDbContext>, ApplicationDbContextFactory>();

        services.AddScoped(provider => provider.GetRequiredService<IApplicationDbContextFactory<ApplicationDbContext>>().CreateSystemDbContext());
        return services;
    }

    public static IdentityBuilder AddApplicationStores(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddUserStore<ApplicationUserStore>();
        builder.AddUserManager<ApplicationUserManager>();
        return builder.AddEntityFrameworkStores<ApplicationDbContext>();
    }
}
