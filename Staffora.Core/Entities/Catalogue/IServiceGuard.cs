namespace Staffora.Core.Entities.Catalogue;

public interface IServiceGuard
{
    IReadOnlySet<Guid> StoredQualifications(Service service);
}
