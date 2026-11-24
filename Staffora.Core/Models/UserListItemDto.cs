using Staffora.Core.Entities.Identity;

namespace Staffora.Core.Models;

public class UserListItemDto
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;

    public UserState State { get; set; }

    public DateTime Created { get; set; }

    public DateTime? LastSignedIn { get; set; }

    public string? PhoneNumber { get; set; }

    public IReadOnlyList<string>? Roles { get; set; }

    public IReadOnlyList<string>? RoleNames { get; set; }
}
