namespace Staffora.Blazor.Helper.UrlState;

public static class UrlStateServiceCollectionExtensions
{
    public static IServiceCollection AddUrlState(this IServiceCollection services)
    {
        services.AddScoped<IUrlStateWriter, JsUrlStateWriter>();
        services.AddScoped<UrlStateService>();
        return services;
    }
}
