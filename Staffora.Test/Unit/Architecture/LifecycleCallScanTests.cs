using System.Reflection;
using System.Text.RegularExpressions;
using Staffora.Blazor.Components.Common.Dialogs;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.Test.Unit.Architecture;

public sealed partial class LifecycleCallScanTests
{
    [Fact]
    public void No_page_or_component_outside_a_state_entity_dialogs_context_runs_a_step_or_deletes_a_state_entity()
    {
        Assert.Empty(Offenders(typeof(StateEntityDialogHost).Assembly.GetTypes()));
    }

    [Fact]
    public void The_scan_catches_a_page_that_steps_or_deletes_itself_and_passes_a_dialog_context_and_a_delete_without_a_lifecycle()
    {
        var found = Offenders([typeof(CardThatDeletesItsRole), typeof(PageThatStepsAUnit), typeof(OrganizationUnitDialogContext), typeof(TenantRoleDialogContext), typeof(PanelThatRemovesAHolding)]);

        Assert.Equal(
            [
                $"{nameof(CardThatDeletesItsRole)} calls {nameof(TenantRoleService)}.{nameof(TenantRoleService.UpdateTenantRoleAndTransitionAsync)}",
                $"{nameof(PageThatStepsAUnit)} calls {nameof(OrganizationService)}.{nameof(OrganizationService.UpdateOrganizationUnitAndTransitionAsync)}",
                $"{nameof(PageThatStepsAUnit)} calls {nameof(WorkflowService)}.{nameof(WorkflowService.TransitionEntityAsync)}",
            ],
            found);
    }

    private static List<string> Offenders(IEnumerable<Type> types) =>
        [.. types.SelectMany(WithNested).Distinct()
            .Where(type => !IsDialogContext(OuterType(type)))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .SelectMany(IlScan.DirectReferences)
                .Where(IsLifecycleCall)
                .Select(called => $"{OuterType(type).Name} calls {called.DeclaringType!.Name}.{called.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<Type> WithNested(Type type) =>
        [type, .. type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested)];

    private static bool IsDialogContext(Type type) =>
        type != typeof(StateEntityDialogContext) && type.IsSubclassOf(typeof(StateEntityDialogContext));

    private static bool IsLifecycleCall(MethodBase called)
    {
        if (called.DeclaringType?.Assembly != typeof(WorkflowService).Assembly)
        {
            return false;
        }

        if (called.GetParameters().Any(parameter => parameter.Name == "transitionKey"))
        {
            return true;
        }

        if (called.DeclaringType == typeof(WorkflowService)
            && (called.Name.StartsWith("TransitionEntity", StringComparison.Ordinal) || called.Name.EndsWith("AndTransitionAsync", StringComparison.Ordinal)))
        {
            return true;
        }

        return DeletesAStateEntity(called.Name);
    }

    private static bool DeletesAStateEntity(string name) =>
        DeleteOf().Match(name) is { Success: true } delete
            && stateEntities.Any(entity => entity.EndsWith(delete.Groups["entity"].Value, StringComparison.Ordinal));

    [Fact]
    public void A_delete_of_a_state_entity_is_caught_by_its_name_and_a_delete_without_a_lifecycle_is_not()
    {
        Assert.True(DeletesAStateEntity("DeleteTenantRoleAsync"));
        Assert.True(DeletesAStateEntity("DeleteOrganizationUnitAsync"));
        Assert.True(DeletesAStateEntity("DeleteUserAsync"));
        Assert.True(DeletesAStateEntity("DeleteDefinitionAsync"));
        Assert.False(DeletesAStateEntity("DeleteSettingAsync"));
        Assert.False(DeletesAStateEntity("RemoveUserFromRoleAsync"));
    }

    private static readonly string[] stateEntities = [.. typeof(Tenant).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && WorkflowRegistry.HasWorkflow(type))
        .Select(type => type.Name)];

    private static Type OuterType(Type type)
    {
        while (type.DeclaringType is { } outer && (type.Name.StartsWith('<') || type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)))
        {
            type = outer;
        }

        return type;
    }

    [GeneratedRegex(@"^Delete(?<entity>\w+)Async$")]
    private static partial Regex DeleteOf();

    private sealed class CardThatDeletesItsRole
    {
        private readonly TenantRoleService roles;

        public CardThatDeletesItsRole(TenantRoleService roles)
        {
            this.roles = roles;
        }

        public Func<Task> DeleteButton(Staffora.Data.DetachedEntityUpdater<TenantRole> role) => async () => await this.roles.UpdateTenantRoleAndTransitionAsync(role, "role-delete", null, CancellationToken.None);
    }

    private sealed class PageThatStepsAUnit
    {
        private readonly WorkflowService workflows;
        private readonly OrganizationService units;

        public PageThatStepsAUnit(WorkflowService workflows, OrganizationService units)
        {
            this.workflows = workflows;
            this.units = units;
        }

        public Task<Staffora.Core.Models.Result> Deactivate(Guid unitId) =>
            this.workflows.TransitionEntityAsync<Staffora.Core.Entities.Organization.OrganizationUnitState, Staffora.Core.Entities.Organization.OrganizationUnit>(unitId, "unit-deactivate");

        public async Task Save(Staffora.Data.DetachedEntityUpdater<Staffora.Core.Entities.Organization.OrganizationUnit> updater) =>
            await this.units.UpdateOrganizationUnitAndTransitionAsync(updater, "unit-save", null, CancellationToken.None);
    }

    private sealed class PanelThatRemovesAHolding
    {
        private readonly RoleHoldingService holdings;

        public PanelThatRemovesAHolding(RoleHoldingService holdings)
        {
            this.holdings = holdings;
        }

        public Task Remove(Guid holdingId) => this.holdings.RemoveUserFromRoleAsync(holdingId);
    }
}
