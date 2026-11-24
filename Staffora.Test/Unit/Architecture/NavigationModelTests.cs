using Microsoft.AspNetCore.Components;
using Staffora.Blazor.Layout;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Test.Unit.Architecture;

public class NavigationModelTests
{
    [Fact]
    public void A_holder_of_one_permission_sees_that_link_and_not_its_neighbours()
    {
        var visible = AppNavigation.VisibleTo(AppNavigation.Tenant, new HashSet<RolePermission> { RolePermission.UsersView });

        var administration = Assert.Single(visible, section => section.Title == "Organization");
        Assert.Equal(["Users"], administration.Items.Select(item => item.Text));
        Assert.Contains(visible, section => section.Title == "My work");
    }

    [Fact]
    public void A_section_without_a_visible_link_is_left_out()
    {
        var visible = AppNavigation.VisibleTo(AppNavigation.Tenant, new HashSet<RolePermission>());

        Assert.Equal(["My work"], visible.Select(section => section.Title));
    }

    [Fact]
    public void Every_link_leads_to_a_page_of_the_app()
    {
        var routes = typeof(Staffora.Blazor.Components.App).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>())
            .Select(route => route.Template)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var links = AppNavigation.Tenant.Concat(AppNavigation.Platform).SelectMany(section => section.Items).Select(item => item.Path).ToList();

        Assert.NotEmpty(links);
        Assert.All(links, path => Assert.Contains(path, routes));
    }

    [Theory]
    [InlineData("tenant/management", "Organization", "Overview", true)]
    [InlineData("tenant/management/users", "Organization", "Users", true)]
    [InlineData("/TENANT/management/users/?page=2", "Organization", "Users", true)]
    [InlineData("tenant/management/users/0b7a6f3e-55f5-4a43-9a7c-3f8c2a3b9d10", "Organization", "Users", false)]
    [InlineData("tenant/management/settings/custom-fields", "Organization", "Settings", false)]
    [InlineData("staffing/jobs#slot", "Staffing", "Jobs", true)]
    public void A_page_is_located_at_its_own_link_or_under_the_longest_link_above_it(string path, string section, string item, bool exact)
    {
        var location = AppNavigation.Locate(AppNavigation.Tenant, path);

        Assert.NotNull(location);
        Assert.Equal((section, item, exact), (location.Section.Title, location.Item.Text, location.Exact));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Account/Login")]
    [InlineData("not-found")]
    [InlineData("tenant/managementx")]
    public void A_page_outside_the_navigation_has_no_place_in_it(string path)
    {
        Assert.Null(AppNavigation.Locate(AppNavigation.Tenant, path));
    }
}
