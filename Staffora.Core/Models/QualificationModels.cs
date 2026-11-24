using Staffora.Core.Entities.Resources;

namespace Staffora.Core.Models;

public sealed record QualificationListItem(Guid Id, string Name, string? Description, QualificationState State, int Holders);

public sealed record StaffQualificationRow(Guid UserId, Guid QualificationId, string Name, string? Description, QualificationState State)
{
    public string NameLabel => State == QualificationState.Archived ? $"{Name} (archived)" : Name;
}

public sealed record QualificationChoice(Guid Id, string Name, string? Description);

public sealed record QualificationHolderRow(Guid ResourceId, Guid UserId, string Name, string Email, ResourceState State);
