using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Blazor.Helper.ScanSamples
{
    public static class NamespaceHelper
    {
        public static bool IsDraft(SampleParent parent) => parent.State == ParentState.Draft;
    }
}

namespace Staffora.Blazor.Components.Common.Dialogs.StateEntity.ScanSamples
{
    public static class DialogNamespaceHelper
    {
        public static bool IsDraft(SampleParent parent) => parent.State == ParentState.Draft;
    }
}
