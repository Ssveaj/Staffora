using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Services;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Unit)]
public sealed class OrganizationUnitDialogContext
    : StateEntityDialogContextBase<OrganizationUnitState, OrganizationUnit>
{
    public const string InfoView = "info";

    public const string UnitsView = "units";

    public const string HoldingsView = "holdings";

    private readonly OrganizationService service;

    public OrganizationUnitDialogContext(OrganizationService service, Guid? unitId)
        : base(unitId, OrganizationUnitWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    private bool fieldsLocked = true;
    private RoleHoldingChoices choices = RoleHoldingChoices.None;

    public Guid? ParentId { get; init; }

    public IReadOnlyList<OrganizationUnit> Parents { get; private set; } = [];

    public IReadOnlyList<OrganizationUnitTypeOption> Types { get; private set; } = [];

    public bool CanManageRoles { get; private set; }

    public bool MayRemove(UserRoleHoldingRow row) => CanManageRoles && this.choices.WithinCeiling(row.RoleId);

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "badge" },
        new StateEntityDialogListView<OrganizationUnitListItem>(UnitsView, "Units below", OrganizationUnitChildList.Definition, (query, cancellationToken) => this.service.QueryOrganizationUnitChildrenAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "account_tree",
            Permission = RolePermission.OrganizationUnitsView,
            Area = OrganizationUnitWorkflowAttribute.AreaUnits,
        },
        new StateEntityDialogListView<UserRoleHoldingRow>(HoldingsView, "Role holdings", UnitRoleHoldingList.Definition, (query, cancellationToken) => this.service.Holdings.QueryOrganizationUnitRoleHoldingsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 2,
            Icon = "admin_panel_settings",
            Permission = RolePermission.RolesView,
            Area = OrganizationUnitWorkflowAttribute.AreaHoldings,
            AddLabel = "Add role holding",
            Add = AddHoldingAsync,
            CanAdd = () => CanManageRoles,
        },
    ];

    private Task<bool> AddHoldingAsync(DialogService dialogs, CancellationToken cancellationToken) =>
        AddRoleHoldingDialog.OpenForUnitAsync(dialogs, EntityId!.Value, Entity!.TenantId, DisplayName, this.choices);

    protected override Task<Result<DetachedEntityUpdater<OrganizationUnit>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadOrganizationUnitUpdaterAsync(entityId, cancellationToken);

    protected override Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<OrganizationUnit> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateOrganizationUnitAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<OrganizationUnit> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateOrganizationUnitAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetOrganizationUnitHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> LoadCustomFieldsAsync(CancellationToken cancellationToken) =>
        this.service.GetOrganizationUnitCustomFieldsAsync(EntityId, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetOrganizationUnitEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        var read = options.Value;
        (this.fieldsLocked, Parents, Types, CanManageRoles, this.choices) = (read.FieldsLocked, read.Parents, read.Types, read.CanManageRoles, read.Choices);
        return Result.Success();
    }

    protected override Task<Result<OrganizationUnit>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewOrganizationUnitAsync(ParentId, cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(OrganizationUnit entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateOrganizationUnitAsync(entity, transitionKey, cancellationToken);

    protected override bool IsLockedRecord(OrganizationUnit entity) => this.fieldsLocked;
}
