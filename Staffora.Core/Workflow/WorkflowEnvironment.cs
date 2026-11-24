using Staffora.Core.Workflow.Conditions;

namespace Staffora.Core.Workflow;

public sealed class WorkflowEnvironment
{
    public WorkflowEnvironment(TimeProvider timeProvider, INavigationLoadState? navigationLoadState = null, IServiceProvider? serviceProvider = null, Func<IStateEntity, ITransition, string?>? stepAccess = null, ICustomFieldCatalog? customFields = null)
    {
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        NavigationLoadState = navigationLoadState ?? Conditions.NavigationLoadState.None;
        ServiceProvider = serviceProvider;
        StepAccess = stepAccess;
        CustomFields = customFields;
    }

    public TimeProvider TimeProvider { get; }

    public INavigationLoadState NavigationLoadState { get; }

    public IServiceProvider? ServiceProvider { get; }

    public Func<IStateEntity, ITransition, string?>? StepAccess { get; }

    public ICustomFieldCatalog? CustomFields { get; }
}
