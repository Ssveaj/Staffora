using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Staffora.Blazor.Helper;

public sealed class ProxyOptions
{
    public const string Section = "ForwardedHeaders";

    public string[] KnownProxies { get; set; } = [];

    public string[] KnownNetworks { get; set; } = [];

    public bool TrustsAnyProxy => KnownProxies.Length > 0 || KnownNetworks.Length > 0;
}

public static class ProxyHeaders
{
    public static IServiceCollection AddProxyHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var proxies = configuration.GetSection(ProxyOptions.Section).Get<ProxyOptions>() ?? new ProxyOptions();
        var addresses = proxies.KnownProxies.Select(IPAddress.Parse).ToList();
        var networks = proxies.KnownNetworks.Select(System.Net.IPNetwork.Parse).ToList();

        services.PostConfigure<ForwardedHeadersOptions>(options =>
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            addresses.ForEach(options.KnownProxies.Add);
            networks.ForEach(options.KnownIPNetworks.Add);

            options.ForwardLimit = 1;
            options.ForwardedHeaders = proxies.TrustsAnyProxy ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
        });

        return services;
    }
}
