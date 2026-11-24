using Staffora.Core.Entities.Catalogue;

namespace Staffora.Core.Models;

public sealed record ServiceListItem(Guid Id, string Name, string? Description, ServiceState State, int DefaultSlotCount, bool OfferedInPortal, int QualificationCount)
{
    public string PortalLabel => OfferedInPortal ? "Offered" : "–";
}

public sealed record ServiceChoice(Guid Id, string Name, int DefaultSlotCount);
