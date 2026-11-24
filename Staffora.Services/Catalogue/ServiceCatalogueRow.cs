using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Jobs;
using Staffora.Services.Caching;
using Staffora.Services.Jobs;

namespace Staffora.Services.Catalogue;

public sealed record ServiceCatalogueRow(
    Guid Id,
    Guid? TenantId,
    string Name,
    string? Description,
    int DefaultSlotCount,
    bool OfferedInPortal,
    ServiceState State,
    IReadOnlyList<SlotRequirement> Qualifications) : ILookupRow
{
    public ServiceTemplate Template => new(Id, Name, Description, Qualifications);
}

public sealed record ServiceEditorOptions(bool FieldsLocked, IReadOnlyList<SlotQualificationChoice> Qualifications);
