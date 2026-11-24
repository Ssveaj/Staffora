namespace Staffora.Core.Entities.Jobs;

public interface IJobSlotGuard
{
    IReadOnlySet<Guid> StoredQualifications(JobSlot slot);

    Guid? StoredService(JobSlot slot);
}
