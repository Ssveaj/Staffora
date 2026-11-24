using System.Linq.Expressions;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Resources;

namespace Staffora.Core.Scheduling;

public static class StaffServiceRules
{
    public static Expression<Func<Resource, bool>> SetUpOpen { get; } = resource => resource.State != ResourceState.Inactive;

    public static bool IsSetUpOpen(ResourceState record) => record != ResourceState.Inactive;

    public static Expression<Func<Resource, bool>> InService { get; } = resource => resource.State == ResourceState.Active && resource.User.State == UserState.Active;

    public static bool IsInService(ResourceState record, UserState account) => record == ResourceState.Active && account == UserState.Active;

    public static bool IsInService(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource.User is { } user && IsInService(resource.State, user.State);
    }
}
