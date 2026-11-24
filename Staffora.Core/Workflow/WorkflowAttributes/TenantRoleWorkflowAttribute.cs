using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class TenantRoleWorkflowAttribute : WorkflowAttribute
{
    public const string AreaPermissions = "Permissions";

    public const string AreaHolders = "Holders";

    public const string CreateStep = "role-create";

    public const string SaveStep = "role-save";

    public const string DeleteStep = "role-delete";

    public const string OwnTenantReason = "Only a user of this organization changes its roles";

    public const string StoredWithinCeilingReason = "The role grants permissions you do not hold";

    public const string ChangesWithinCeilingReason = "You can only give or take away permissions you hold yourself";

    public const string NotLockedReason = "The System owner role is locked: it is never edited or deleted";

    public const string ResourceFlagReason = "Only someone who manages staff members changes whether the role's holders are staff members";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TenantRoleState, TenantRole>();

        workflow.AddState(TenantRoleState.Create)
            .ConfigureFields(f => Editable(f).Area(AreaHolders, FieldAccessFlags.None));

        workflow.AddState(TenantRoleState.Active)
            .ConfigureFields(f => Editable(f).Area(AreaHolders, FieldAccessFlags.ReadWrite));

        workflow.AddState(TenantRoleState.Deleted)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Area(AreaPermissions, FieldAccessFlags.ReadOnly, a => a.Field(e => e.Permissions))
                .Area(AreaHolders, FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(CreateStep, TenantRoleState.Create, TenantRoleState.Active)
        {
            Label = "Create",
            Description = "Create the role with its permissions. It can be given to people at once",
            Icon = "add_moderator",
            RequiredPermission = RolePermission.RolesCreate,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<TenantRoleState, TenantRole>(e => e.Name))
        .AddCondition(InOwnTenant())
        .AddCondition(DelegateCondition<TenantRoleState, TenantRole>.FromContext(ResourceFlagReason, context => !Guard(context).NewRoleFlagBeyondTheSession(context.Entity)));

        workflow.AddTransition(new(SaveStep, TenantRoleState.Active, TenantRoleState.Active)
        {
            Label = "Save",
            Description = "Save the role's name, description and permissions. Everyone holding it gets the change",
            Icon = "save",
            RequiredPermission = RolePermission.RolesEdit,
            SortOrder = 20,
            IsSave = true,
        })
        .AddCondition(new RequiredFieldCondition<TenantRoleState, TenantRole>(e => e.Name))
        .AddCondition(NotLocked())
        .AddCondition(InOwnTenant())
        .AddCondition(StoredWithinCeiling())
        .AddCondition(DelegateCondition<TenantRoleState, TenantRole>.FromContext(ChangesWithinCeilingReason, context => !Guard(context).ChangesBeyondTheSession(context.Entity), nameof(TenantRole.Permissions)))
        .AddCondition(DelegateCondition<TenantRoleState, TenantRole>.FromContext(ResourceFlagReason, context => !Guard(context).ResourceFlagChangeBeyondTheSession(context.Entity)));

        workflow.AddTransition(new(DeleteStep, TenantRoleState.Active, TenantRoleState.Deleted)
        {
            Label = "Delete",
            Description = "Remove the role, but its history is kept. Only a role nobody holds is deleted",
            Icon = "delete",
            ConfirmationMessage = "Delete this role? It is removed for good, but its history is kept.",
            RequiredPermission = RolePermission.RolesDelete,
            ShowWhenBlocked = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        })
        .AddCondition(NotLocked())
        .AddCondition(InOwnTenant())
        .AddCondition(StoredWithinCeiling());

        return workflow;
    }

    private static StateFieldBuilder<TenantRole> Editable(StateFieldBuilder<TenantRole> fields) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.Name, FieldAccessFlags.Required)
        .Field(e => e.Description, FieldAccessFlags.ReadWrite)
        .Field(e => e.ApplyResourceMembershipToUser, FieldAccessFlags.ReadWrite)
        .Area(AreaPermissions, FieldAccessFlags.ReadWrite, a => a.Field(e => e.Permissions));

    private static DelegateCondition<TenantRoleState, TenantRole> NotLocked() => new(NotLockedReason, role => !role.IsSystemOwner);

    private static DelegateCondition<TenantRoleState, TenantRole> InOwnTenant() =>
        DelegateCondition<TenantRoleState, TenantRole>.FromContext(OwnTenantReason, context =>
            context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) is IRuntimeExecutionContext { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static DelegateCondition<TenantRoleState, TenantRole> StoredWithinCeiling() =>
        DelegateCondition<TenantRoleState, TenantRole>.FromContext(StoredWithinCeilingReason, context => !Guard(context).StoredBeyondTheSession(context.Entity));

    private static ITenantRoleGuard Guard(WorkflowContext<TenantRoleState, TenantRole> context) =>
        context.ServiceProvider?.GetService(typeof(ITenantRoleGuard)) as ITenantRoleGuard
            ?? throw new InvalidOperationException("The role workflow's steps are judged only where its role guard is registered.");
}
