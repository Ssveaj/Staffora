using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;

namespace Staffora.Services.CustomFields;

public static class CustomFieldTargets
{
    public static Type DefinitionType(CustomFieldTarget target) => target switch
    {
        CustomFieldTarget.OrganizationUnit => typeof(OrganizationUnitAttributeDefinition),
        CustomFieldTarget.Job => typeof(JobAttributeDefinition),
        _ => typeof(ApplicationUserAttributeDefinition),
    };

    public static bool CanBeRequired(CustomFieldTarget target) => Staffora.Core.Workflow.Conditions.CustomFieldRequirements.CanBeRequired(DefinitionType(target));

    public static string Label(CustomFieldTarget target) => target switch
    {
        CustomFieldTarget.User => "Users",
        CustomFieldTarget.Job => "Jobs",
        _ => "Organization units",
    };
}
