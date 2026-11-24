using System.Reflection;
using Staffora.Core.Annotations;

namespace Staffora.Core;

public static class CoreExtensions
{
    public const string FallbackIcon = "circle";

    public const string FallbackStateColor = "var(--rz-primary)";

    public const string FallbackButtonStyle = "Secondary";

    public const StateCategory FallbackStateCategory = StateCategory.Open;

    public static string GetIcon(this Enum value) =>
        value.GetFieldAttribute<IconAttribute>()?.Name ?? FallbackIcon;

    public static string GetStateColor(this Enum value) =>
        value.GetFieldAttribute<StateStylingAttribute>()?.Color ?? FallbackStateColor;

    public static string GetButtonStyle(this Enum value) =>
        value.GetFieldAttribute<ButtonStyleAttribute>()?.Name ?? FallbackButtonStyle;

    public static StateCategory GetStateCategory(this Enum value) =>
        value.GetFieldAttribute<EntityStateCategoryAttribute>()?.Category ?? FallbackStateCategory;

    public static bool IsDeleteState(this Enum value) =>
        value.GetFieldAttribute<DeleteStateAttribute>() is not null;

    public static bool IsEntryState(this Enum value) =>
        value.GetFieldAttribute<EntryStateAttribute>() is not null
        || string.Equals(Enum.GetName(value.GetType(), value), "Create", StringComparison.Ordinal);

    private static TAttribute? GetFieldAttribute<TAttribute>(this Enum value)
        where TAttribute : Attribute
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = Enum.GetName(value.GetType(), value);
        return name is null ? null : value.GetType().GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetCustomAttribute<TAttribute>(inherit: false);
    }
}
