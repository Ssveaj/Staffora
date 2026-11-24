using Microsoft.EntityFrameworkCore;

namespace Staffora.Core.Workflow.Conditions;

public interface INavigationLoadState
{
    bool IsLoaded(object entity, string navigation);
}

public static class NavigationLoadState
{
    public static INavigationLoadState None { get; } = new FixedLoadState(false);

    public static INavigationLoadState Complete { get; } = new FixedLoadState(true);

    public static INavigationLoadState From(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new TrackedLoadState(context);
    }

    private sealed class FixedLoadState : INavigationLoadState
    {
        private readonly bool isLoaded;

        public FixedLoadState(bool isLoaded)
        {
            this.isLoaded = isLoaded;
        }

        public bool IsLoaded(object entity, string navigation) => this.isLoaded;
    }

    private sealed class TrackedLoadState : INavigationLoadState
    {
        private readonly DbContext context;

        public TrackedLoadState(DbContext context)
        {
            this.context = context;
        }

        public bool IsLoaded(object entity, string navigation)
        {
            var entry = this.context.Entry(entity);
            return entry.State switch
            {
                EntityState.Detached => false,
                EntityState.Added => true,
                _ => entry.Navigations.FirstOrDefault(n => n.Metadata.Name == navigation)?.IsLoaded ?? false,
            };
        }
    }
}
