using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Scheduling;
using Staffora.Services.Services;
using Staffora.Services.Tenancy;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.User)]
public sealed class UserDialogContext
    : StateEntityDialogContextBase<UserState, ApplicationUser>
{
    public const string InfoView = "info";

    public const string RolesView = "roles";

    public const string StaffView = "staff";

    public const string QualificationsView = "qualifications";

    public const string ScheduleView = "schedule";

    public const string AbsenceView = "absence";

    private readonly UserService service;

    public UserDialogContext(UserService service, Guid? userId)
        : base(userId, ApplicationUserWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
    }

    private UserEditorOptions? options;

    private UserEditorOptions Options => this.options ?? UserEditorOptions.ReadOnly;

    public bool CanManageRoles { get; private set; }

    public StaffSummary? Staff { get; private set; }

    public bool CanManageQualifications { get; private set; }

    public StaffSchedule? Schedule { get; private set; }

    public string? ScheduleProblem { get; private set; }

    public bool CanAssignPatterns { get; private set; }

    public bool CanAddAbsence { get; private set; }

    public string? StaffStatus { get; private set; }

    public string? StaffStatusMeaning { get; private set; }

    public bool MayRemove(UserRoleHoldingRow row) => CanManageRoles && !this.rolesBeyondCeiling.Contains(row.RoleId);

    private IReadOnlySet<Guid> rolesBeyondCeiling = new HashSet<Guid>();

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "badge" },
        new StateEntityDialogListView<UserRoleHoldingRow>(RolesView, "Roles", UserRoleHoldingList.Definition, (query, cancellationToken) => this.service.Holdings.QueryApplicationUserRoleHoldingsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "admin_panel_settings",
            Permission = RolePermission.RolesView,
            Area = ApplicationUserWorkflowAttribute.AreaRoles,
            AddLabel = "Add role",
            Add = AddRoleAsync,
            CanAdd = () => CanManageRoles,
        },
        new StateEntityDialogListView<StaffUnitRow>(StaffView, "Staff", StaffUnitList.Definition, (query, cancellationToken) => this.service.QueryApplicationUserStaffUnitsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 2,
            Icon = "badge",
            Permission = RolePermission.ResourcesView,
            When = () => Staff is not null,
        },
        new StateEntityDialogListView<StaffQualificationRow>(QualificationsView, "Qualifications", StaffQualificationList.Definition, (query, cancellationToken) => this.service.QueryApplicationUserQualificationsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 3,
            Icon = "workspace_premium",
            Permission = RolePermission.ResourcesView,
            When = () => Staff is not null,
            AddLabel = "Add qualification",
            Add = AddQualificationAsync,
            CanAdd = () => CanManageQualifications,
        },
        new StateEntityDialogListView<PatternAssignmentRow>(ScheduleView, "Schedule", StaffScheduleList.Definition, (query, cancellationToken) => this.service.PatternAssignments.QueryAssignmentsOfUserAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 4,
            Icon = "calendar_month",
            Permission = RolePermission.ResourcesView,
            When = () => Staff is not null,
            AddLabel = "Assign work pattern",
            Add = AssignPatternAsync,
            CanAdd = () => CanAssignPatterns,
        },
        new StateEntityDialogListView<AbsenceRow>(AbsenceView, "Absence", StaffAbsenceList.Definition, (query, cancellationToken) => this.service.Absences.QueryAbsencesOfUserAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 5,
            Icon = "event_busy",
            Permission = RolePermission.ViewResourceAbsence,
            When = () => Staff is not null,
            AddLabel = "Register absence",
            Add = AddAbsenceAsync,
            CanAdd = () => CanAddAbsence,
        },
    ];

    private async Task<bool> AddAbsenceAsync(DialogService dialogs, CancellationToken cancellationToken)
    {
        if (EntityId is not { } userId)
        {
            return false;
        }

        var result = await AbsenceDialog.OpenCreateAsync(dialogs, Absences, userId);
        return result.Changed;
    }

    public Task<StateEntityDialogResult> OpenAbsenceAsync(DialogService dialogs, AbsenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return AbsenceDialog.OpenAsync(dialogs, Absences, row.Id);
    }

    private ResourceAbsenceService Absences => this.absences!;

    private ResourceAbsenceService? absences;
    private PatternAssignmentService? patternAssignments;
    private TenantRoleService? roles;

    private async Task<bool> AssignPatternAsync(DialogService dialogs, CancellationToken cancellationToken)
    {
        if (EntityId is not { } userId)
        {
            return false;
        }

        var result = await PatternAssignmentDialog.OpenCreateAsync(dialogs, PatternAssignments, userId);
        return result.Changed;
    }

    public Task<StateEntityDialogResult> OpenAssignmentAsync(DialogService dialogs, PatternAssignmentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return PatternAssignmentDialog.OpenAsync(dialogs, PatternAssignments, row.Id);
    }

    private PatternAssignmentService PatternAssignments => this.patternAssignments!;

    private Task<bool> AddQualificationAsync(DialogService dialogs, CancellationToken cancellationToken) =>
        AddQualificationDialog.OpenAsync(dialogs, EntityId!.Value, DisplayName);

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    private Task<bool> AddRoleAsync(DialogService dialogs, CancellationToken cancellationToken) =>
        AddRoleHoldingDialog.OpenAsync(dialogs, EntityId!.Value, Entity!.TenantId, DisplayName, Options.Choices);

    protected override Task<Result<DetachedEntityUpdater<ApplicationUser>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadApplicationUserUpdaterAsync(entityId, cancellationToken);

    protected override async Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<ApplicationUser> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        var saved = await this.service.UpdateApplicationUserAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

        if (saved.IsSuccess && !string.IsNullOrWhiteSpace(saved.Message))
        {
            ShowOnce(saved.Message);
        }

        return saved;
    }

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetApplicationUserHistoryAsync(entityId, query, cancellationToken);

    protected override Task<Result<IReadOnlyList<IEntityAttributeDefinition>>> LoadCustomFieldsAsync(CancellationToken cancellationToken) =>
        this.service.GetApplicationUserCustomFieldsAsync(EntityId, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetApplicationUserEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        this.options = options.Value;
        this.absences ??= this.service.Absences;
        this.patternAssignments ??= this.service.PatternAssignments;
        this.roles ??= this.service.Roles;

        TransitionFilter = options.Value.OnConsole ? transition => ConsoleSteps.Contains(transition.Key) : null;
        CanManageRoles = options.Value.CanManageRoles;
        Staff = options.Value.Staff;
        CanManageQualifications = options.Value.CanManageQualifications;
        (Schedule, ScheduleProblem, CanAssignPatterns) = (options.Value.Schedule, options.Value.ScheduleProblem, options.Value.CanAssignPatterns);
        CanAddAbsence = options.Value.CanAddAbsence;
        StaffStatus = Staff is { } staff ? EnumDisplayHelper.GetDisplayName(staff.State) : null;
        StaffStatusMeaning = Staff is { } described ? EnumDisplayHelper.GetDescription(described.State) : null;
        this.rolesBeyondCeiling = options.Value.Choices.RolesBeyondCeiling;
        return Result.Success();
    }

    protected override Task<Result<ApplicationUser>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewApplicationUserAsync(cancellationToken);

    protected override async Task<Result<Guid>> CreateEntityAsync(ApplicationUser entity, string transitionKey, CancellationToken cancellationToken)
    {
        var created = await this.service.CreateApplicationUserAsync(entity, transitionKey, cancellationToken);

        if (created.IsSuccess && !string.IsNullOrWhiteSpace(created.Message))
        {
            ShowOnce(created.Message);
        }

        return created;
    }

    protected override bool IsLockedRecord(ApplicationUser entity) => Options.FieldsLocked;

    protected override bool IsLockedMember(string fieldOrArea) => Options.OnConsole && !UserService.ConsoleEditableColumns.Contains(fieldOrArea);

    private static readonly HashSet<string> ConsoleSteps = [ApplicationUserWorkflowAttribute.UnlockStep, ApplicationUserWorkflowAttribute.SaveStep, ApplicationUserWorkflowAttribute.NewPasswordStep];

    public Task<StateEntityDialogResult> OpenHoldingAsync(DialogService dialogs, UserRoleHoldingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return MayRemove(row) ? RoleHoldingDialog.OpenAsync(dialogs, row, true) : TenantRoleDialog.OpenAsync(dialogs, Roles, row.RoleId);
    }

    private TenantRoleService Roles => this.roles!;
}
