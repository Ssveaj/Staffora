using System.Collections.Concurrent;
using System.Reflection;
using Staffora.Blazor.Helper.UrlState;

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DialogRouteAttribute : Attribute
{
    private static readonly ConcurrentDictionary<Type, string?> tokens = new();

    public DialogRouteAttribute(string token)
    {
        if (!DialogStackSegment.IsValidToken(token))
        {
            throw new ArgumentException($"\"{token}\" is not a dialog token: use 1 to 32 lowercase ASCII letters or digits, starting with a letter.", nameof(token));
        }

        Token = token;
    }

    public string Token { get; }

    public static string? TokenOf(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        return tokens.GetOrAdd(contextType, type => type.GetCustomAttribute<DialogRouteAttribute>(inherit: false)?.Token);
    }
}
