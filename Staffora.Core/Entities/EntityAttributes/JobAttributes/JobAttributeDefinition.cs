using Staffora.Core.Entities.Jobs;
using Staffora.Core.Workflow.WorkflowAttributes;

namespace Staffora.Core.Entities.EntityAttributes.JobAttributes;

[JobFieldWorkflow]
public class JobAttributeDefinition : EntityAttributeDefinitionBase<Job>
{
    public JobFieldAudience Audience { get; set; }
}
