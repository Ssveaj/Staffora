using System.Reflection;
using Microsoft.AspNetCore.Components;
using Staffora.Blazor.Layout;

namespace Staffora.Test.Unit.Architecture;

public class PlatformConsoleScanTests
{
    private static readonly Assembly app = typeof(AppNavigation).Assembly;

    [Fact]
    public void The_console_navigation_offers_exactly_its_pages_and_each_is_declared()
    {
        var links = AppNavigation.Platform.SelectMany(section => section.Items).Select(item => item.Path).ToList();
        Assert.Equal(["/superadmin/overview", "/superadmin/tenants", "/superadmin/settings", "/superadmin/errorlog"], links);

        var routes = app.GetTypes().SelectMany(type => type.GetCustomAttributes<RouteAttribute>()).Select(route => route.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(links, link => Assert.Contains(link, routes));
        Assert.Contains("/superadmin/tenants/{TenantId:guid}", routes);
        Assert.Contains("/superadmin/tenants/{TenantId:guid}/{Tab}", routes);
    }
}
