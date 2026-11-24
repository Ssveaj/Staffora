namespace Staffora.Core.Workflow;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public abstract class WorkflowAttribute : Attribute
{
    public abstract IWorkflow CreateWorkflow();
}
