using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Staffora.Core.Entities.Tenancy;

namespace Staffora.Blazor.Helper;

public static class EnumDisplayHelper
{
    public static string GetDisplayName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var member = typeof(TEnum).GetField(value.ToString());
        var display = member?.GetCustomAttribute<DisplayAttribute>();
        return display?.Name ?? value.ToString();
    }

    public static string GetDisplayName(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.ToString();
        var display = value.GetType().GetField(name)?.GetCustomAttribute<DisplayAttribute>();
        return display?.Name ?? name;
    }

    public static string? GetDescription<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var member = typeof(TEnum).GetField(value.ToString());
        var description = member?.GetCustomAttribute<DescriptionAttribute>();
        return description?.Description;
    }

    public static PermissionArea? GetPermissionArea(RolePermission permission)
    {
        var member = typeof(RolePermission).GetField(permission.ToString());
        var attribute = member?.GetCustomAttribute<PermissionAreaAttribute>();
        return attribute?.Area;
    }

    public static IEnumerable<IGrouping<PermissionArea, RolePermission>> GetPermissionsByArea()
    {
        return RolePermissions.Grantable
            .Where(p => GetPermissionArea(p) is not null)
            .GroupBy(p => GetPermissionArea(p)!.Value);
    }
}
