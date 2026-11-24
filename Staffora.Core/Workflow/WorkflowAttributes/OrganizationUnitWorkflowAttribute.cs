using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class OrganizationUnitWorkflowAttribute : WorkflowAttribute
{
    public const string AreaAttributes = WorkflowAreas.Attributes;

    public const string AreaUnits = "Units";

    public const string AreaHoldings = "Holdings";

    public const string CreateStep = "unit-create";

    public const string SaveStep = "unit-save";

    public const string DeactivateStep = "unit-deactivate";

    public const string ReactivateStep = "unit-reactivate";

    public const string ArchiveStep = "unit-archive";

    public const string ArchiveInactiveStep = "unit-archive-inactive";

    public const string RestoreStep = "unit-restore";

    public const string DeleteStep = "unit-delete";

    public const string DeleteInactiveStep = "unit-delete-inactive";

    public const string DeleteArchivedStep = "unit-delete-archived";

    public const string RootStaysActive = OrganizationStructurePolicy.RootStaysActive;

    public const string RootIsNeverArchived = "The Company unit is the whole company and is never archived";

    public const string RootIsNeverDeleted = OrganizationStructurePolicy.RootIsNeverDeleted;

    public const string NoUnitsBelowReason = "It has units below it: delete or move them first, or archive it instead";

    public const string EveryUnitBelowArchivedReason = "Every unit below it is archived first";

    public const string ParentInUseReason = "The unit above it is in use";

    public const string ParentNotArchivedReason = "The unit above it is not archived";

    public const string NotACompanyReason = "A tenant has one Company unit, the root of its tree, which comes with the tenant";

    public const string OwnTenantReason = "Only a user of this organization changes its units";

    public const string WithinCeilingReason = "Someone holds a role on this unit that grants permissions you do not hold";

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<OrganizationUnitState, OrganizationUnit>();

        workflow.AddState(OrganizationUnitState.Create)
            .ConfigureFields(f => Editable(f)
                .Area(AreaUnits, FieldAccessFlags.None)
                .Area(AreaHoldings, FieldAccessFlags.None));

        workflow.AddState(OrganizationUnitState.Active)
            .ConfigureFields(f => Editable(f)
                .Area(AreaUnits, FieldAccessFlags.ReadOnly)
                .Area(AreaHoldings, FieldAccessFlags.ReadWrite));

        workflow.AddState(OrganizationUnitState.Inactive)
            .ConfigureFields(f => Kept(f))
            .Cascade(e => e.Children, OrganizationUnitState.Inactive, OrganizationUnitState.Archived, OrganizationUnitState.Create);

        workflow.AddState(OrganizationUnitState.Archived)
            .ConfigureFields(f => Kept(f));

        workflow.AddState(OrganizationUnitState.Deleted)
            .ConfigureFields(f => Kept(f));

        workflow.AddTransition(new(CreateStep, OrganizationUnitState.Create, OrganizationUnitState.Active)
        {
            Label = "Create",
            Description = "Create the unit below its parent. It is in use at once",
            Icon = "add_business",
            RequiredPermission = RolePermission.OrganizationUnitsCreate,
            SortOrder = Forward,
        })
        .AddCondition(Required(e => e.Name))
        .AddCondition(new RequiredFieldCondition<OrganizationUnitState, OrganizationUnit>(e => e.ParentId, "Parent unit is required"))
        .AddCondition(new DelegateCondition<OrganizationUnitState, OrganizationUnit>(NotACompanyReason, e => e.UnitType != OrganizationUnitType.Company))
        .AddCondition(ParentInUse())
        .AddCondition(InOwnTenant());

        workflow.AddTransition(new(SaveStep, OrganizationUnitState.Active, OrganizationUnitState.Active)
        {
            Label = "Save",
            Description = "Save the unit's details and place. It stays active",
            Icon = "save",
            RequiredPermission = RolePermission.OrganizationUnitsEdit,
            SortOrder = Neutral,
            IsSave = true,
        })
        .AddCondition(Required(e => e.Name))
        .AddCondition(ParentInUse())
        .AddCondition(InOwnTenant());

        workflow.AddTransition(new(DeactivateStep, OrganizationUnitState.Active, OrganizationUnitState.Inactive)
        {
            Label = "Deactivate",
            Description = "Take the unit out of use with every active unit below it: they take no new role holdings, and the roles held on them grant nothing until they are reactivated",
            Icon = "pause_circle",
            ConfirmationMessage = "Deactivate this unit and every active unit below it? The roles held on them grant nothing until each is reactivated.",
            RequiredPermission = RolePermission.OrganizationUnitsEdit,
            ShowWhenBlocked = true,
            SortOrder = Back,
            Placement = TransitionPlacement.Leading,
        })
        .AddCondition(NotTheRoot(RootStaysActive))
        .AddCondition(InOwnTenant())
        .AddCondition(WithinCeiling());

        workflow.AddTransition(new(ReactivateStep, OrganizationUnitState.Inactive, OrganizationUnitState.Active)
        {
            Label = "Reactivate",
            Description = "Put the unit in use again: the roles held on it grant their permissions, and the units below it are reactivated one by one",
            Icon = "play_circle",
            RequiredPermission = RolePermission.OrganizationUnitsEdit,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })
        .AddCondition(ParentInUse())
        .AddCondition(InOwnTenant())
        .AddCondition(WithinCeiling());

        foreach (var (key, from) in new[] { (ArchiveStep, OrganizationUnitState.Active), (ArchiveInactiveStep, OrganizationUnitState.Inactive) })
        {
            var archive = workflow.AddTransition(new(key, from, OrganizationUnitState.Archived)
            {
                Label = "Archive",
                Description = "Keep the unit for the record, out of use. Every unit below it is archived first",
                Icon = "archive",
                ConfirmationMessage = "Archive this unit? It is kept for the record, and the roles held on it grant nothing.",
                RequiredPermission = RolePermission.OrganizationUnitsEdit,
                ShowWhenBlocked = true,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
            })
            .AddCondition(NotTheRoot(RootIsNeverArchived))
            .AddCondition(new DelegateCondition<OrganizationUnitState, OrganizationUnit>(EveryUnitBelowArchivedReason, e => e.Children.All(child => child.State == OrganizationUnitState.Archived), nameof(OrganizationUnit.Children)))
            .AddCondition(InOwnTenant());

            if (from == OrganizationUnitState.Active)
            {
                archive.AddCondition(WithinCeiling());
            }
        }

        workflow.AddTransition(new(RestoreStep, OrganizationUnitState.Archived, OrganizationUnitState.Inactive)
        {
            Label = "Restore",
            Description = "Take the unit out of the archive, inactive. Reactivate it to put it in use",
            Icon = "unarchive",
            RequiredPermission = RolePermission.OrganizationUnitsEdit,
            ShowWhenBlocked = true,
            SortOrder = Forward,
        })
        .AddCondition(new DelegateCondition<OrganizationUnitState, OrganizationUnit>(ParentNotArchivedReason, e => e.ParentId is null || (e.Parent is { } parent && parent.State != OrganizationUnitState.Archived), nameof(OrganizationUnit.Parent)))
        .AddCondition(InOwnTenant());

        foreach (var (key, from) in new[] { (DeleteStep, OrganizationUnitState.Active), (DeleteInactiveStep, OrganizationUnitState.Inactive), (DeleteArchivedStep, OrganizationUnitState.Archived) })
        {
            workflow.AddTransition(new(key, from, OrganizationUnitState.Deleted)
            {
                Label = "Delete",
                Description = "Remove the unit, but its history is kept. Only a unit with no units below it that nothing uses is deleted",
                Icon = "delete",
                ConfirmationMessage = "Delete this unit? It is removed for good, but its history is kept.",
                RequiredPermission = RolePermission.OrganizationUnitsDelete,
                ShowWhenBlocked = true,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
            })
            .AddCondition(NotTheRoot(RootIsNeverDeleted))
            .AddCondition(new DelegateCondition<OrganizationUnitState, OrganizationUnit>(NoUnitsBelowReason, e => e.Children.Count == 0, nameof(OrganizationUnit.Children)))
            .AddCondition(InOwnTenant())
            .AddCondition(WithinCeiling());
        }

        return workflow;
    }

    private static StateFieldBuilder<OrganizationUnit> Editable(StateFieldBuilder<OrganizationUnit> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.UnitType, FieldAccessFlags.ReadWrite)
        .Field(e => e.ParentId, FieldAccessFlags.ReadWrite)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite)
        .Area(AreaAttributes, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Attributes));

    private static StateFieldBuilder<OrganizationUnit> Kept(StateFieldBuilder<OrganizationUnit> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Area(AreaAttributes, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Attributes))
        .Area(AreaUnits, FieldAccessFlags.ReadOnly)
        .Area(AreaHoldings, FieldAccessFlags.ReadOnly);

    private static RequiredFieldCondition<OrganizationUnitState, OrganizationUnit> Required(System.Linq.Expressions.Expression<Func<OrganizationUnit, object?>> field) => new(field);

    private static DelegateCondition<OrganizationUnitState, OrganizationUnit> NotTheRoot(string reason) =>
        new(reason, e => e.UnitType != OrganizationUnitType.Company && e.ParentId is not null);

    private static DelegateCondition<OrganizationUnitState, OrganizationUnit> ParentInUse() =>
        new(ParentInUseReason, e => e.ParentId is null || (e.Parent is { } parent && parent.Id == e.ParentId && parent.IsInUse), nameof(OrganizationUnit.Parent));

    private static DelegateCondition<OrganizationUnitState, OrganizationUnit> InOwnTenant() =>
        DelegateCondition<OrganizationUnitState, OrganizationUnit>.FromContext(OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static DelegateCondition<OrganizationUnitState, OrganizationUnit> WithinCeiling() =>
        DelegateCondition<OrganizationUnitState, OrganizationUnit>.FromContext(WithinCeilingReason, context => !Guard(context).HoldingsBeyondTheSession(context.Entity));

    private static IOrganizationUnitGuard Guard(WorkflowContext<OrganizationUnitState, OrganizationUnit> context) =>
        context.ServiceProvider?.GetService(typeof(IOrganizationUnitGuard)) as IOrganizationUnitGuard
            ?? throw new InvalidOperationException("The unit workflow's steps are judged only where its unit guard is registered.");
}
