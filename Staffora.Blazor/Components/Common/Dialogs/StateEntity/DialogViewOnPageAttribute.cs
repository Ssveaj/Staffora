namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DialogViewOnPageAttribute : Attribute
{
    public DialogViewOnPageAttribute(string token, string view)
    {
        Token = token;
        View = view;
    }

    public string Token { get; }

    public string View { get; }

    public static string? For(Type? pageType, string token) =>
        pageType?.GetCustomAttributes(typeof(DialogViewOnPageAttribute), inherit: false).OfType<DialogViewOnPageAttribute>().FirstOrDefault(attribute => attribute.Token == token)?.View;
}
