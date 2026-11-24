using System.Reflection;
using Staffora.Core.Entities.Resources;
using Staffora.Data;
using Staffora.Services.Jobs;
using Staffora.Services.Platform;
using Staffora.Services.Portal;
using Staffora.Services.Resources;
using Staffora.Services.Workflow;

namespace Staffora.Test.Unit.Architecture;

public class StaffRecordScanTests
{
    private static readonly Assembly[] productAssemblies =
    [
        typeof(Resource).Assembly,
        typeof(ApplicationDbContext).Assembly,
        typeof(ResourceLinker).Assembly,
        typeof(Staffora.Blazor.Helper.DialogServiceExtensions).Assembly,
        typeof(Staffora.E2ESetup.JourneyTenantUsers).Assembly,
    ];

    private static readonly string clockStep = $"{nameof(JobClock)} runs a step inside another write";

    private static readonly string groupStep = $"{nameof(SlotGroupFollowUp)} runs a step inside another write";

    private static readonly string purgeStep = $"{nameof(Retention)} runs a step inside another write";

    private static readonly string[] handlerSteps = [$"{nameof(JobStateSyncHandler)} runs a step inside another write", $"{nameof(OrderCancelHandler)} runs a step inside another write"];

    [Fact]
    public void Only_the_resource_linker_builds_a_staff_record_or_a_membership_and_only_it_the_job_clock_the_retention_pass_and_the_portal_handlers_run_a_step_inside_another_write()
    {
        Assert.Empty(productAssemblies.SelectMany(assembly => assembly.GetTypes()).Where(type => type.DeclaringType is null && !IsLinker(type)).SelectMany(Offences).Except([clockStep, groupStep, purgeStep, .. handlerSteps]));
        Assert.Equal([purgeStep], Offences(typeof(Retention)).Distinct());
        Assert.Equal([handlerSteps[0]], Offences(typeof(JobStateSyncHandler)).Distinct());
        Assert.Equal([handlerSteps[1]], Offences(typeof(OrderCancelHandler)).Distinct());
    }

    [Fact]
    public void The_scan_reads_the_linker_and_the_clock_and_catches_a_record_a_membership_and_a_step_elsewhere()
    {
        Assert.Equal(["builds a Resource", "builds a ResourceUnitMembership", "runs a step inside another write"], Offences(typeof(ResourceLinker)).Select(offence => offence[(offence.IndexOf(' ') + 1)..]).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal([clockStep], Offences(typeof(JobClock)).Distinct());
        Assert.Equal(
            ["StaffForgerySample builds a Resource", "StaffForgerySample builds a ResourceUnitMembership", "StaffForgerySample runs a step inside another write"],
            Offences(typeof(StaffForgerySample)).Distinct().Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> Offences(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> bodies = [.. type.GetMethods(all), .. type.GetConstructors(all)];
        foreach (var method in bodies.Where(method => method.GetMethodBody() is not null))
        {
            foreach (var called in IlScan.DirectReferences(method))
            {
                if (called is ConstructorInfo { DeclaringType: { } built } && (built == typeof(Resource) || built == typeof(ResourceUnitMembership)))
                {
                    yield return $"{Owner(type).Name} builds a {built.Name}";
                }
                else if (called is MethodInfo { Name: nameof(WorkflowService.RunSystemStepInTransactionAsync) } step && step.DeclaringType == typeof(WorkflowService))
                {
                    yield return $"{Owner(type).Name} runs a step inside another write";
                }
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var offence in Offences(nested))
            {
                yield return offence;
            }
        }
    }

    private static bool IsLinker(Type type) => Owner(type) == typeof(ResourceLinker);

    private static Type Owner(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
    }
}

internal sealed class StaffForgerySample
{
    private readonly WorkflowService workflows;

    public StaffForgerySample(WorkflowService workflows)
    {
        this.workflows = workflows;
    }

    public Resource Forge() => new() { Memberships = [new ResourceUnitMembership()] };

    public async Task MoveAsync(ApplicationDbContext context, Resource record) =>
        await this.workflows.RunSystemStepInTransactionAsync<ResourceState, Resource>(context, record, "resource-activate", CancellationToken.None);
}
