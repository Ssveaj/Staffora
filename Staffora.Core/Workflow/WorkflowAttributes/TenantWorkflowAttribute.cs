using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class TenantWorkflowAttribute : WorkflowAttribute
{
    private const string NotPlatformTenant = "The platform tenant is never suspended or deleted";

    public const string ActivateStep = "tenant-activate";

    public const string SaveSetupStep = "tenant-save-setup";

    public const string PurgedStep = "tenant-purged";

    public const string ActiveOwnerReason = "The tenant has no active system owner yet, so nobody could sign in to keep it";

    private const string PlatformTenantFixed = "The platform tenant's name and description are fixed";

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<TenantState, Tenant>();

        workflow.AddState(TenantState.Setup)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Description, FieldAccessFlags.ReadWrite));

        workflow.AddState(TenantState.Active)
            .ConfigureFields(f => f
                .Default(FieldAccessFlags.ReadOnly)
                .Field(e => e.Name, FieldAccessFlags.Required)
                .Field(e => e.Description, FieldAccessFlags.ReadWrite));

        workflow.AddState(TenantState.Suspended).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(TenantState.PendingDeletion).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));
        workflow.AddState(TenantState.Archived).ConfigureFields(f => f.Default(FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(SaveSetupStep, TenantState.Setup, TenantState.Setup)
        {
            Label = "Save",
            Description = "Save the tenant's name and description. It stays in setup",
            Icon = "save",
            RequiresPlatformOwner = true,
            SortOrder = 20,
        })
        .AddCondition(new RequiredFieldCondition<TenantState, Tenant>(e => e.Name));

        workflow.AddTransition(new(ActivateStep, TenantState.Setup, TenantState.Active)
        {
            Label = "Activate",
            Description = "Put the tenant in normal use. Its users can sign in",
            RequiresPlatformOwner = true,
            ShowWhenBlocked = true,
            SortOrder = 30,
        })
        .AddCondition(new RequiredFieldCondition<TenantState, Tenant>(e => e.Name))
        .AddCondition(DelegateCondition<TenantState, Tenant>.FromContext(ActiveOwnerReason, context => OwnerGuard(context).HasActiveSystemOwner(context.Entity)));

        workflow.AddTransition(new("tenant-save", TenantState.Active, TenantState.Active)
        {
            Label = "Save",
            Description = "Save the tenant's name and description. It stays active",
            Icon = "save",
            RequiresPlatformOwner = true,
            SortOrder = 20,
        })
        .AddCondition(new RequiredFieldCondition<TenantState, Tenant>(e => e.Name))
        .AddCondition(new DelegateCondition<TenantState, Tenant>(PlatformTenantFixed, e => !e.IsRootTenant));

        workflow.AddTransition(new("tenant-suspend", TenantState.Active, TenantState.Suspended)
        {
            Label = "Suspend",
            Description = "Suspend the tenant: nobody signs in or is impersonated, and its data and integrations are held until it is reactivated",
            ConfirmationMessage = "Suspend this tenant? Its users are signed out and cannot sign in until it is reactivated.",
            RequiresPlatformOwner = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        })
        .AddCondition(new DelegateCondition<TenantState, Tenant>(NotPlatformTenant, e => !e.IsRootTenant))
        .AddAction(SignEveryoneOut());

        workflow.AddTransition(new("tenant-reactivate", TenantState.Suspended, TenantState.Active)
        {
            Label = "Reactivate",
            Description = "Reactivate the tenant: its users can sign in again and held integrations resume",
            RequiresPlatformOwner = true,
            SortOrder = 30,
        });

        workflow.AddTransition(new("tenant-schedule-deletion", TenantState.Active, TenantState.PendingDeletion)
        {
            Label = "Schedule deletion",
            Description = "Schedule the active tenant for removal: nobody signs in, and it is read-only for the platform owner",
            ConfirmationMessage = "Schedule this tenant for deletion? Its users lose access at once.",
            RequiresPlatformOwner = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        })
        .AddCondition(new DelegateCondition<TenantState, Tenant>(NotPlatformTenant, e => !e.IsRootTenant))
        .AddAction(SignEveryoneOut());

        workflow.AddTransition(new("tenant-schedule-deletion-suspended", TenantState.Suspended, TenantState.PendingDeletion)
        {
            Label = "Schedule deletion",
            Description = "Schedule the suspended tenant for removal. It is read-only for the platform owner",
            ConfirmationMessage = "Schedule this tenant for deletion?",
            RequiresPlatformOwner = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        })
        .AddCondition(new DelegateCondition<TenantState, Tenant>(NotPlatformTenant, e => !e.IsRootTenant));

        workflow.AddTransition(new("tenant-cancel-deletion", TenantState.PendingDeletion, TenantState.Suspended)
        {
            Label = "Cancel deletion",
            Description = "Take the tenant off the deletion schedule. It stays suspended until it is reactivated",
            RequiresPlatformOwner = true,
            SortOrder = 10,
            Placement = TransitionPlacement.Leading,
        });

        workflow.AddTransition(new("tenant-archive", TenantState.PendingDeletion, TenantState.Archived)
        {
            Label = "Archive",
            Description = "Archive the tenant: it is kept for the record and nobody has access",
            ConfirmationMessage = "Archive this tenant? This cannot be undone.",
            RequiresPlatformOwner = true,
            SortOrder = 30,
        });

        workflow.AddTransition(new(PurgedStep, TenantState.PendingDeletion, TenantState.Archived)
        {
            Label = "Purged",
            Description = "The retention pass removed the tenant's data once its grace period was over, and archived it",
            Icon = "delete_forever",
            IsSystemDriven = true,
            SortOrder = 40,
        });

        return workflow;
    }

    private static ITenantOwnerGuard OwnerGuard(WorkflowContext<TenantState, Tenant> context) =>
        context.ServiceProvider?.GetService(typeof(ITenantOwnerGuard)) as ITenantOwnerGuard
            ?? throw new InvalidOperationException("The tenant workflow's Activate step is judged only where its owner guard is registered.");

    private static DelegateAction<TenantState, Tenant> SignEveryoneOut() => new(context =>
    {
        var signOut = context.ServiceProvider?.GetService(typeof(ITenantSignOut)) as ITenantSignOut
            ?? throw new InvalidOperationException("A tenant is suspended or scheduled for deletion only where its users' sign-out is registered.");
        var tenantId = context.Entity.Id;
        context.BeforeSave.Add((store, cancellationToken) => signOut.SignOutEveryoneAsync(store, tenantId, cancellationToken));
    });
}
