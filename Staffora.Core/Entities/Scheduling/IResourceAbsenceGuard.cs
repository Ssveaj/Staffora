namespace Staffora.Core.Entities.Scheduling;

public interface IResourceAbsenceGuard
{
    bool MayChange(ResourceAbsence absence);

    bool KindChanged(ResourceAbsence absence);

    bool SessionManagesAbsence();
}
