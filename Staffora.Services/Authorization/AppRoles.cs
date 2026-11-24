namespace Staffora.Services.Authorization;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string StandardUser = "StandardUser";

    public static string DisplayName(string? role) => role switch
    {
        Admin => "Platform owner",
        StandardUser or null or "" => "Standard user",
        _ => role,
    };
}
