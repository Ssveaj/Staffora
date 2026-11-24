namespace Staffora.Core.Entities.Jobs;

public sealed record StoredJobReferences(Guid CustomerId, Guid? ContactId, Guid OrganizationUnitId);

public interface IJobGuard
{
    StoredJobReferences StoredReferences(Job job);

    TimeZoneInfo TimeZoneOf(Job job);

    IReadOnlyList<string> HolderClashes(Job job);
}
