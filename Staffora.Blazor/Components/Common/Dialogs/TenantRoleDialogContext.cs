using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Role)]
public sealed class TenantRoleDialogContext
    : StateEntityDialogContextBase<TenantRoleState, TenantRole>
{
    public const string InfoView = "info";

    public const string PermissionsView = "permissions";

    public const string HoldersView = "holders";

    private readonly TenantRoleService service;

    public TenantRoleDialogContext(TenantRoleService service, Guid? roleId)
        : base(roleId, TenantRoleWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    private bool fieldsLocked = true;
    private RoleHoldingChoices choices = RoleHoldingChoices.None;

    public IReadOnlySet<RolePermission> Grantable { get; private set; } = new HashSet<RolePermission>();

    public IReadOnlySet<RolePermission> Offered { get; private set; } = new HashSet<RolePermission>();

    public string? LockedReason { get; private set; }

    public bool CanManageHolders { get; private set; }

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "badge" },
        new StateEntityDialogView(PermissionsView, "Permissions") { Order = 1, Icon = "key", Area = TenantRoleWorkflowAttribute.AreaPermissions },
        new StateEntityDialogListView<UserRoleHoldingRow>(HoldersView, "Holders", RoleHolderList.Definition, (query, cancellationToken) => this.service.Holdings.QueryTenantRoleHoldersAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 2,
            Icon = "group",
            Permission = RolePermission.RolesView,
            Area = TenantRoleWorkflowAttribute.AreaHolders,
            AddLabel = "Add holder",
            Add = AddHolderAsync,
            CanAdd = () => CanManageHolders,
        },
    ];

    private Task<bool> AddHolderAsync(DialogService dialogs, CancellationToken cancellationToken) =>
        AddRoleHoldingDialog.OpenForRoleAsync(dialogs, EntityId!.Value, Entity!.TenantId, DisplayName, this.choices);

    protected override Task<Result<DetachedEntityUpdater<TenantRole>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadTenantRoleUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<TenantRole> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateTenantRoleAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<TenantRole> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateTenantRoleAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetTenantRoleHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetTenantRoleEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        var read = options.Value;

        this.users ??= Services?.GetService(typeof(UserService)) as UserService;
        (this.fieldsLocked, LockedReason, Grantable, CanManageHolders, this.choices, Offered) = (read.FieldsLocked, read.LockedReason, read.Grantable, read.CanManageHolders, read.Choices, read.Offered);
        return Result.Success();
    }

    protected override Task<Result<TenantRole>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewTenantRoleAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(TenantRole entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateTenantRoleAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(TenantRole entity) => this.fieldsLocked;

    private UserService? users;

    public async Task<StateEntityDialogResult> OpenHolderAsync(DialogService dialogs, UserRoleHoldingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (this.users is not { } users)
        {
            return await RoleHoldingDialog.OpenAsync(dialogs, row, CanManageHolders);
        }

        await UserDialog.OpenAsync(dialogs, users, row.UserId);
        return new StateEntityDialogResult(true, false, null);
    }
}
