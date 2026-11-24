using System.Reflection;
using Microsoft.AspNetCore.Components;
using Staffora.Blazor.Layout;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Test.Unit.Architecture;

public class CustomerPortalPageScanTests
{
    private static readonly Dictionary<string, string> portalPages = new(StringComparer.Ordinal)
    {
        ["/portal"] = "the portal's home, where every session of a portal lands",
        ["/portal/orders"] = "\"My orders\": the portal's own orders, the reader's by default",
        ["/portal/orders/new"] = "\"New order\": writing an order to the portal's staffing company",
        ["/portal/orders/{RequestId:guid}"] = "an order's page: what was asked for and where it stands, the portal's own rows only",
        ["/portal/users"] = "the portal's users: a customer administrator adds orderers and takes them out of use, the portal's own users only",
        ["/"] = "the start page, which only sends a session on to its home (the portal's for a portal)",
        ["/me/profile"] = "one's own profile, read only, the same for everyone",
        ["/not-found"] = "a system page every session reads",
        ["/Error"] = "a system page every session reads",
        ["/status/{Code:int}"] = "a system page every session reads",
        ["/Account/AccessDenied"] = "sign-in and its system pages, before any tenant is known",
        ["/Account/Lockout"] = "sign-in and its system pages, before any tenant is known",
        ["/Account/Login"] = "sign-in and its system pages, before any tenant is known",
    };

    [Fact]
    public void The_pages_marked_part_of_the_portal_are_exactly_the_allow_list()
    {
        var pages = RoutedPages();
        var marked = pages.Where(page => CustomerPortalPages.IsPortalPage(page.Type)).Select(page => page.Route).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(pages);
        Assert.Empty(marked.Where(route => !portalPages.ContainsKey(route)).Select(route => $"{route} is marked part of the portal without an entry here"));
        Assert.Empty(portalPages.Where(entry => !marked.Contains(entry.Key) || string.IsNullOrWhiteSpace(entry.Value)).Select(entry => $"{entry.Key}: the entry is stale"));
    }

    [Fact]
    public void No_page_of_a_staffing_company_or_of_the_platform_console_is_part_of_the_portal()
    {
        var staffing = RoutedPages().Where(page => page.Route.StartsWith("/staffing", StringComparison.Ordinal) || page.Route.StartsWith("/tenant", StringComparison.Ordinal)
            || page.Route.StartsWith("/superadmin", StringComparison.Ordinal) || page.Route == "/coordination" || (page.Route.StartsWith("/me/", StringComparison.Ordinal) && page.Route != "/me/profile")).ToList();
        Assert.NotEmpty(staffing);
        Assert.All(staffing, page => Assert.False(CustomerPortalPages.IsPortalPage(page.Type), page.Route));
    }

    [Fact]
    public void The_portals_navigation_and_home_lead_only_to_pages_of_the_portal()
    {
        var pages = RoutedPages().ToDictionary(page => page.Route, page => page.Type, StringComparer.Ordinal);
        Assert.All(AppNavigation.Portal.SelectMany(section => section.Items), item => Assert.True(CustomerPortalPages.IsPortalPage(pages[item.Path]), item.Path));
        Assert.True(CustomerPortalPages.IsPortalPage(pages[AppHome.PortalHome]));

        Assert.All(AppNavigation.Portal.SelectMany(section => section.Items).SelectMany(item => item.AnyOf), permission => Assert.True(RolePermissions.IsPortal(permission), permission.ToString()));
    }

    [Fact]
    public void The_scan_reads_the_mark_on_the_page_itself_and_nowhere_else()
    {
        Assert.True(CustomerPortalPages.IsPortalPage(typeof(MarkedSample)));
        Assert.False(CustomerPortalPages.IsPortalPage(typeof(UnmarkedSample)));
        Assert.False(CustomerPortalPages.IsPortalPage(typeof(DerivedSample)));
        Assert.False(CustomerPortalPages.IsPortalPage(null));
        Assert.Equal(["/sample/marked", "/sample/unmarked", "/sample/derived"], new[] { typeof(MarkedSample), typeof(UnmarkedSample), typeof(DerivedSample) }.SelectMany(type => type.GetCustomAttributes<RouteAttribute>(inherit: false)).Select(route => route.Template));
    }

    private static List<(string Route, Type Type)> RoutedPages() =>
        [.. typeof(AppHome).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>(inherit: false).Select(route => (route.Template, type)))];

    [Route("/sample/marked")]
    [CustomerPortalPage]
    private class MarkedSample : ComponentBase;

    [Route("/sample/unmarked")]
    private class UnmarkedSample : ComponentBase;

    [Route("/sample/derived")]
    private sealed class DerivedSample : MarkedSample;
}
