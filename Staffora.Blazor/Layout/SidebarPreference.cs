namespace Staffora.Blazor.Layout;

public static class SidebarPreference
{
    public const string CookieName = "staffora.sidebar";

    public const string Collapsed = "collapsed";

    public static string? Attribute(HttpContext? context) =>
        context?.Request.Cookies.TryGetValue(CookieName, out var value) == true && value == Collapsed ? Collapsed : null;
}
