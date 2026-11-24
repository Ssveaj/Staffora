using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;

namespace Staffora.Services.CustomFields;

public static class ApplicationUserAttributeDefinitionWorkflowQueries
{
    public static IQueryable<ApplicationUserAttributeDefinition> WithWorkflowGraph(this IQueryable<ApplicationUserAttributeDefinition> query) => query;
}

public static class OrganizationUnitAttributeDefinitionWorkflowQueries
{
    public static IQueryable<OrganizationUnitAttributeDefinition> WithWorkflowGraph(this IQueryable<OrganizationUnitAttributeDefinition> query) => query;
}

public static class JobAttributeDefinitionWorkflowQueries
{
    public static IQueryable<JobAttributeDefinition> WithWorkflowGraph(this IQueryable<JobAttributeDefinition> query) => query;
}
