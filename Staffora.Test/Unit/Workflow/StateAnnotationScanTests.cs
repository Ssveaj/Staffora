using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Staffora.Blazor.Components.Common;
using Staffora.Core.Annotations;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Data;
using Staffora.Services.Tenancy;
using Staffora.Test.Unit.Workflow.Samples;

namespace Staffora.Test.Unit.Workflow;

public class StateAnnotationScanTests
{
    [Fact]
    public void Every_registered_state_member_and_transition_is_annotated()
    {
        var violations = ProductionWorkflows().SelectMany(FindViolations);

        Assert.Empty(violations);
    }

    [Fact]
    public void The_scan_covers_the_tenant_workflow()
    {
        Assert.Contains(typeof(Tenant), ProductionWorkflows().Select(workflow => workflow.EntityType));
    }

    [Fact]
    public void The_scan_reports_each_missing_annotation_and_description()
    {
        var workflow = new WorkflowRegistry().Get(typeof(UntidyEntity));

        Assert.Equal(
            [
                "UntidyState.Closed has no [Display(Name)]",
                "UntidyState.Closed has no [Description]",
                "UntidyState.Closed has no [Icon]",
                "UntidyState.Closed has no [StateStyling]",
                "UntidyState.Closed has no [EntityStateCategory]",
                "UntidyState.Gone has no [Display(Name)]",
                "UntidyState.Gone has no [Description]",
                "UntidyEntity transition 'untidy-drop' has no Description",
            ],
            FindViolations(workflow));
    }

    [Fact]
    public void The_scan_passes_a_fully_annotated_workflow()
    {
        Assert.Empty(FindViolations(new WorkflowRegistry().Get(typeof(TidyEntity))));
    }

    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Tenant).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(TenantService).Assembly,
        typeof(PermissionView).Assembly,
    ];

    internal static IReadOnlyList<IWorkflow> ProductionWorkflows() =>
        ProductionAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(WorkflowRegistry.HasWorkflow)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(WorkflowRegistry.Default.Get)
            .ToList();

    internal static IReadOnlyList<string> FindViolations(IWorkflow workflow)
    {
        var violations = new List<string>();
        var stateType = workflow.StateType;

        foreach (var field in stateType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var member = $"{stateType.Name}.{field.Name}";
            if (string.IsNullOrWhiteSpace(field.GetCustomAttribute<DisplayAttribute>()?.Name))
            {
                violations.Add($"{member} has no [Display(Name)]");
            }

            if (string.IsNullOrWhiteSpace(field.GetCustomAttribute<DescriptionAttribute>()?.Description))
            {
                violations.Add($"{member} has no [Description]");
            }

            if (string.IsNullOrWhiteSpace(field.GetCustomAttribute<IconAttribute>()?.Name))
            {
                violations.Add($"{member} has no [Icon]");
            }

            if (string.IsNullOrWhiteSpace(field.GetCustomAttribute<StateStylingAttribute>()?.Color))
            {
                violations.Add($"{member} has no [StateStyling]");
            }

            if (field.GetCustomAttribute<EntityStateCategoryAttribute>() is null)
            {
                violations.Add($"{member} has no [EntityStateCategory]");
            }
        }

        violations.AddRange(workflow.Transitions
            .Where(transition => string.IsNullOrWhiteSpace(transition.Description))
            .Select(transition => $"{workflow.EntityType.Name} transition '{transition.Key}' has no Description"));

        return violations;
    }
}
