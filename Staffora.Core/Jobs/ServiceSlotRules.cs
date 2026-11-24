using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Jobs;

namespace Staffora.Core.Jobs;

public sealed record ServiceTemplate(Guid ServiceId, string Name, string? Description, IReadOnlyList<SlotRequirement> Qualifications);

public static class ServiceSlotRules
{
    public static JobSlot Build(Guid tenantId, Guid jobId, ServiceTemplate service, IReadOnlySet<Guid> activeQualifications)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(activeQualifications);
        var slot = new JobSlot
        {
            TenantId = tenantId,
            JobId = jobId,
            State = JobSlotState.Active,
            ServiceId = service.ServiceId,
            Name = service.Name,
            Description = service.Description,
        };
        foreach (var requirement in Kept(service, activeQualifications))
        {
            slot.Qualifications.Add(new JobSlotQualification { TenantId = tenantId, QualificationId = requirement.QualificationId, Level = requirement.Level });
        }

        return slot;
    }

    private static IEnumerable<SlotRequirement> Kept(ServiceTemplate service, IReadOnlySet<Guid> activeQualifications) =>
        service.Qualifications
            .Where(requirement => activeQualifications.Contains(requirement.QualificationId))
            .GroupBy(requirement => requirement.QualificationId)
            .Select(group => new SlotRequirement(group.Key, group.Min(requirement => requirement.Level)));
}
