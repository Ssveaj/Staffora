using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Messaging;
using Staffora.Core.Workflow.Actions;
using Staffora.Core.Workflow.Conditions;
using Staffora.Core.Workflow.FieldAccess;

namespace Staffora.Core.Workflow.WorkflowAttributes;

public sealed class ApplicationUserWorkflowAttribute : WorkflowAttribute
{
    public const string AreaAttributes = WorkflowAreas.Attributes;

    public const string AreaRoles = "Roles";

    public const string AreaAccount = "Account";

    public const string CreateStep = "user-create";

    public const string SaveStep = "user-save";

    public const string UnlockStep = "user-unlock";

    public const string NewPasswordStep = "user-new-password";

    public const string DeactivateStep = "user-deactivate";

    public const string ReactivateStep = "user-reactivate";

    public const string PortalCreateOrdererStep = "user-portal-create-orderer";

    public const string PortalCreateAdministratorStep = "user-portal-create-administrator";

    public const string PortalNewPasswordStep = "user-portal-new-password";

    public const string PortalDeactivateStep = "user-portal-deactivate";

    public const string PortalReactivateStep = "user-portal-reactivate";

    public static readonly IReadOnlyList<string> PortalSteps = [PortalCreateOrdererStep, PortalCreateAdministratorStep, PortalNewPasswordStep, PortalDeactivateStep, PortalReactivateStep];

    public static RolePreset? PortalRoleOf(string transitionKey) => transitionKey switch
    {
        PortalCreateOrdererStep => RolePresets.Orderer,
        PortalCreateAdministratorStep => RolePresets.CustomerAdmin,
        _ => null,
    };

    public const string NotSelf = "You cannot deactivate your own account";

    public const string NotOwnPassword = "Only a system owner gives themselves a new password. Ask a system owner or the platform owner for one";

    public const string NotOwnDetails = "Only a system owner changes their own details. Ask a system owner or the platform owner";

    private const string OwnTenant = "Only a user of this organization changes its users";
    private const string OwnTenantOrConsole = "Only a user of this organization or the platform owner changes this";
    private const string NotSystemAccount = "A tenant's system account is never changed";
    private const string LockedOut = "The user is locked out of signing in after too many failed attempts";

    public const string NotAPlatformOwnerReason = "A platform owner's password, lockout and access are managed only with the operations commands";

    public const string WithinCeilingReason = "The user holds permissions you do not hold";

    public const string SignInNameWithinCeilingReason = "Only someone who holds every permission the user holds changes their user name or email address";

    public const string OwnerByOwnerReason = "Only a system owner changes a system owner's password, lockout or access";

    public const string LastOwnerReason = "The user is the organization's last active system owner, and it always keeps one";

    public const string LastCustomerAdministratorReason = "The user is the organization's last active customer administrator, and it always keeps one";

    public const string OrdinaryNewAccountReason = "A new user is an ordinary account: no system account, no two-factor sign-in, no lockout and no failed sign-ins set ahead";

    private const int Back = 10;
    private const int Neutral = 20;
    private const int Forward = 30;

    public override IWorkflow CreateWorkflow()
    {
        var workflow = new Workflow<UserState, ApplicationUser>();

        workflow.AddState(UserState.Create)
            .ConfigureFields(f => Person(f, FieldAccessFlags.Required, FieldAccessFlags.ReadWrite)
                .Area(AreaRoles, FieldAccessFlags.None));

        workflow.AddState(UserState.Active)
            .ConfigureFields(f => Person(f, FieldAccessFlags.Required, FieldAccessFlags.ReadWrite)
                .Area(AreaRoles, FieldAccessFlags.ReadWrite));

        workflow.AddState(UserState.Inactive)
            .ConfigureFields(f => Person(f, FieldAccessFlags.ReadOnly, FieldAccessFlags.ReadOnly)
                .Area(AreaRoles, FieldAccessFlags.ReadOnly));

        workflow.AddTransition(new(CreateStep, UserState.Create, UserState.Active)
        {
            Label = "Create",
            Description = "Create the user, who signs in with the initial password shown once",
            Icon = "person_add",
            RequiredPermission = RolePermission.UsersCreate,
            SortOrder = Forward,
        })
        .AddCondition(Required(e => e.FirstName))
        .AddCondition(Required(e => e.LastName))
        .AddCondition(Required(e => e.Email))
        .AddCondition(Required(e => e.UserName, "User name is required"))
        .AddCondition(OrdinaryNewAccount());

        foreach (var (key, label, description, sortOrder) in new[]
        {
            (PortalCreateOrdererStep, "Create orderer", "Create an orderer of your organization: they write, send and follow orders, and sign in with the initial password shown once", Forward),
            (PortalCreateAdministratorStep, "Create administrator", "Create another customer administrator: they manage your organization's users too, and sign in with the initial password shown once", Neutral),
        })
        {
            workflow.AddTransition(new(key, UserState.Create, UserState.Active)
            {
                Label = label,
                Description = description,
                Icon = "person_add",
                RequiredPermission = RolePermission.CustomerUsersManage,
                SortOrder = sortOrder,
            })
            .AddCondition(Required(e => e.FirstName))
            .AddCondition(Required(e => e.LastName))
            .AddCondition(Required(e => e.Email))
            .AddCondition(Required(e => e.UserName, "User name is required"))
            .AddCondition(OrdinaryNewAccount());
        }

        workflow.AddTransition(new(SaveStep, UserState.Active, UserState.Active)
        {
            Label = "Save",
            Description = "Save the user's details. They stay active",
            Icon = "save",
            RequiredPermission = RolePermission.UsersEdit,
            SortOrder = Neutral,
            IsSave = true,
        })
        .AddCondition(Required(e => e.FirstName))
        .AddCondition(Required(e => e.LastName))
        .AddCondition(Required(e => e.Email))
        .AddCondition(Required(e => e.UserName, "User name is required"))
        .AddCondition(InOwnTenantOrOnTheConsole())
        .AddCondition(NotTheirOwnUnlessOwner(NotOwnDetails))
        .AddCondition(SignInNameWithinCeiling());

        workflow.AddTransition(new(UnlockStep, UserState.Active, UserState.Active)
        {
            Label = "Unlock",
            Description = "Lift the user's sign-in lockout and clear their failed attempts",
            Icon = "lock_open",
            RequiredPermission = RolePermission.UsersEdit,
            SortOrder = Neutral,
            Placement = TransitionPlacement.Leading,

            TakesNoEdits = true,
        })
        .AddCondition(InOwnTenantOrOnTheConsole())
        .AddCondition(NotASystemAccount())
        .AddCondition(NotAPlatformOwner())
        .AddCondition(WithinCeiling())
        .AddCondition(OwnerByOwner())
        .AddCondition(DelegateCondition<UserState, ApplicationUser>.FromContext(LockedOut, context => context.Entity.LockoutEnd is { } end && end > context.TimeProvider.GetUtcNow()))
        .AddAction(new DelegateAction<UserState, ApplicationUser>(context =>
        {
            context.Entity.LockoutEnd = null;
            context.Entity.AccessFailedCount = 0;
        }));

        foreach (var (key, permission, portal) in new[] { (NewPasswordStep, RolePermission.UsersEdit, false), (PortalNewPasswordStep, RolePermission.CustomerUsersManage, true) })
        {
            workflow.AddTransition(new(key, UserState.Active, UserState.Active)
            {
                Label = "New password",
                Description = "Reset the user's sign-in: a new password shown to you once. It signs them out everywhere and lifts a lockout",
                Icon = "password",
                ConfirmationMessage = "Give this user a new password? Their current password stops working, they are signed out everywhere, and the new password is shown to you once.",
                RequiredPermission = permission,
                ShowWhenBlocked = true,
                SortOrder = Neutral,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = portal,
            })
            .AddCondition(portal ? InOwnTenant() : InOwnTenantOrOnTheConsole())
            .AddCondition(NotTheirOwnUnlessOwner(NotOwnPassword))
            .AddCondition(NotASystemAccount())
            .AddCondition(NotAPlatformOwner())
            .AddCondition(WithinCeiling())
            .AddCondition(OwnerByOwner())
            .AddAction(new DelegateAction<UserState, ApplicationUser>(context =>
            {
                var issuer = PasswordIssuer(context);
                issuer.IssueNewPassword(context.Entity);

                var userId = context.Entity.Id;
                context.BeforeSave.Add((store, cancellationToken) => issuer.RemoveOtherSignInMeansAsync(store, userId, cancellationToken));
            }));
        }

        foreach (var (key, permission, portal) in new[] { (DeactivateStep, RolePermission.UsersEdit, false), (PortalDeactivateStep, RolePermission.CustomerUsersManage, true) })
        {
            workflow.AddTransition(new(key, UserState.Active, UserState.Inactive)
            {
                Label = "Deactivate",
                Description = "Deactivate the user: they are signed out, cannot sign in or be impersonated, and their roles grant nothing until they are reactivated",
                Icon = "person_off",
                ConfirmationMessage = "Deactivate this user? They are signed out and cannot sign in until they are reactivated.",
                RequiredPermission = permission,
                ShowWhenBlocked = true,
                SortOrder = Back,
                Placement = TransitionPlacement.Leading,
                TakesNoEdits = portal,
            })
            .AddCondition(InOwnTenant())
            .AddCondition(NotTheirOwn(NotSelf))
            .AddCondition(NotASystemAccount())
            .AddCondition(NotAPlatformOwner())
            .AddCondition(WithinCeiling())
            .AddCondition(OwnerByOwner())
            .AddCondition(NotTheLastOwner())
            .AddCondition(NotTheLastCustomerAdministrator())
            .AddAction(new DelegateAction<UserState, ApplicationUser>(context => context.Entity.SecurityStamp = NewStamp()));
        }

        foreach (var (key, permission, portal) in new[] { (ReactivateStep, RolePermission.UsersEdit, false), (PortalReactivateStep, RolePermission.CustomerUsersManage, true) })
        {
            workflow.AddTransition(new(key, UserState.Inactive, UserState.Active)
            {
                Label = "Reactivate",
                Description = "Reactivate the user: they can sign in again and their roles grant their permissions",
                Icon = "how_to_reg",
                RequiredPermission = permission,
                ShowWhenBlocked = true,
                SortOrder = Forward,
                TakesNoEdits = portal,
            })
            .AddCondition(InOwnTenant())
            .AddCondition(NotASystemAccount())
            .AddCondition(NotAPlatformOwner())
            .AddCondition(WithinCeiling())
            .AddCondition(OwnerByOwner())
            .AddAction(new NotifyAction<UserState, ApplicationUser>(
                MessageTypes.UserReactivated,
                user => new Dictionary<string, string?> { ["FirstName"] = user.FirstName, ["LastName"] = user.LastName },
                recipients: user => [user.Id]));
        }

        return workflow;
    }

    private static StateFieldBuilder<ApplicationUser> Person(StateFieldBuilder<ApplicationUser> fields, FieldAccessFlags identifying, FieldAccessFlags other) => fields
        .Default(FieldAccessFlags.ReadOnly)
        .Field(e => e.FirstName, identifying)
        .Field(e => e.LastName, identifying)
        .Field(e => e.Email, identifying)
        .Field(e => e.UserName, identifying)
        .Field(e => e.PhoneNumber, other)
        .Area(AreaAttributes, other, a => a.Field(e => e.Attributes))
        .Area(AreaAccount, FieldAccessFlags.ReadWrite, a => a
            .Field(e => e.NormalizedUserName)
            .Field(e => e.NormalizedEmail)
            .Field(e => e.EmailConfirmed)
            .Field(e => e.PasswordHash)
            .Field(e => e.SecurityStamp)
            .Field(e => e.ConcurrencyStamp)
            .Field(e => e.PhoneNumberConfirmed)
            .Field(e => e.TwoFactorEnabled)
            .Field(e => e.LockoutEnd)
            .Field(e => e.LockoutEnabled)
            .Field(e => e.AccessFailedCount)
            .Field(e => e.LastSignedIn));

    private static RequiredFieldCondition<UserState, ApplicationUser> Required(System.Linq.Expressions.Expression<Func<ApplicationUser, object?>> field, string? description = null) => new(field, description);

    private static DelegateCondition<UserState, ApplicationUser> InOwnTenant() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(OwnTenant, context => RuntimeOf(context) is { TenantId: { } tenant } && tenant == context.Entity.TenantId);

    private static DelegateCondition<UserState, ApplicationUser> InOwnTenantOrOnTheConsole() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(OwnTenantOrConsole, context => RuntimeOf(context)?.Current is { } session
            && (session.TenantId == context.Entity.TenantId || (session.IsSuperAdmin && session.IsInPlatformTenant && !session.IsImpersonating)));

    private static DelegateCondition<UserState, ApplicationUser> NotTheirOwn(string description) =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(description, context => RuntimeOf(context) is { EffectiveUserId: { } self } && self != context.Entity.Id);

    private static DelegateCondition<UserState, ApplicationUser> NotTheirOwnUnlessOwner(string description) =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(description, context => RuntimeOf(context) is { EffectiveUserId: { } self }
            && (self != context.Entity.Id || Guard(context).SessionIsOwnerOf(context.Entity)));

    private static DelegateCondition<UserState, ApplicationUser> NotASystemAccount() => new(NotSystemAccount, e => !e.IsSystemAccount);

    private static DelegateCondition<UserState, ApplicationUser> OrdinaryNewAccount() =>
        new(OrdinaryNewAccountReason, e => !e.IsSystemAccount && !e.TwoFactorEnabled && e.LockoutEnd is null && e.AccessFailedCount == 0);

    private static DelegateCondition<UserState, ApplicationUser> NotAPlatformOwner() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(NotAPlatformOwnerReason, context => !Guard(context).IsPlatformOwner(context.Entity));

    private static DelegateCondition<UserState, ApplicationUser> WithinCeiling() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(WithinCeilingReason, context => !Guard(context).HoldsMoreThanTheSession(context.Entity));

    private static DelegateCondition<UserState, ApplicationUser> SignInNameWithinCeiling() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(SignInNameWithinCeilingReason, context => Guard(context) is var guard
            && (!guard.SignInNameChanged(context.Entity) || (!guard.HoldsMoreThanTheSession(context.Entity) && !guard.IsOwnerBeyondTheSession(context.Entity))));

    private static DelegateCondition<UserState, ApplicationUser> OwnerByOwner() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(OwnerByOwnerReason, context => !Guard(context).IsOwnerBeyondTheSession(context.Entity));

    private static DelegateCondition<UserState, ApplicationUser> NotTheLastOwner() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(LastOwnerReason, context => !Guard(context).IsLastActiveOwner(context.Entity));

    private static DelegateCondition<UserState, ApplicationUser> NotTheLastCustomerAdministrator() =>
        DelegateCondition<UserState, ApplicationUser>.FromContext(LastCustomerAdministratorReason, context => !Guard(context).IsLastActiveCustomerAdministrator(context.Entity));

    private static IUserAccountGuard Guard(WorkflowContext<UserState, ApplicationUser> context) =>
        context.ServiceProvider?.GetService(typeof(IUserAccountGuard)) as IUserAccountGuard
            ?? throw new InvalidOperationException("The user workflow's steps are judged only where its account guard is registered.");

    private static IRuntimeExecutionContext? RuntimeOf(WorkflowContext<UserState, ApplicationUser> context) =>
        context.ServiceProvider?.GetService(typeof(IRuntimeExecutionContext)) as IRuntimeExecutionContext;

    private static IUserPasswordIssuer PasswordIssuer(WorkflowContext<UserState, ApplicationUser> context) =>
        context.ServiceProvider?.GetService(typeof(IUserPasswordIssuer)) as IUserPasswordIssuer
            ?? throw new InvalidOperationException("The new-password step runs only where a password issuer is registered.");

    private static string NewStamp() => Guid.NewGuid().ToString("N").ToUpperInvariant();
}
