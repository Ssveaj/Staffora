using Staffora.Core.Workflow;
using Staffora.Core.Workflow.Conditions;

namespace Staffora.Test.Unit.Workflow.Samples;

internal static class Environments
{
    public static readonly DateTimeOffset Noon = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    public static WorkflowEnvironment Complete(TimeProvider? clock = null) =>
        new(clock ?? new FakeClock(Noon), NavigationLoadState.Complete);

    public static WorkflowEnvironment NothingLoaded() =>
        new(new FakeClock(Noon), NavigationLoadState.None);

    public static Workflow<ParentState, SampleParent> Parent => WorkflowRegistry.Default.Get<ParentState, SampleParent>();

    public static Workflow<ProbeState, ProbeEntity> Probe => WorkflowRegistry.Default.Get<ProbeState, ProbeEntity>();
}
