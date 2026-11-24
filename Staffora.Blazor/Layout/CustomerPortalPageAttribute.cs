namespace Staffora.Blazor.Layout;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CustomerPortalPageAttribute : Attribute;

public static class CustomerPortalPages
{
    public const string NotPartTitle = "Not part of your portal";

    public const string NotPartMessage = "This page is not part of your portal. Your portal has its own pages: start from its home.";

    public static bool IsPortalPage(Type? page) => page is not null && Attribute.IsDefined(page, typeof(CustomerPortalPageAttribute), inherit: false);
}
