using Radzen;
using Staffora.Blazor.Components.Common.Dialogs.StateEntity;
using Staffora.Blazor.Helper.UrlState;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Customers;
using Staffora.Services.Workflow;

namespace Staffora.Blazor.Components.Common.Dialogs;

[DialogRoute(DialogTokens.Customer)]
public sealed class CustomerDialogContext : StateEntityDialogContextBase<CustomerState, Customer>
{
    public const string InfoView = "info";

    public const string ContactsView = "contacts";

    private bool fieldsLocked = true;
    private bool portalShown;
    private bool portalEnabled;
    private string? portalUnitName;
    private IReadOnlyList<CustomerPortalUnit> portalUnits = [];
    private readonly CustomerService service;

    public CustomerDialogContext(CustomerService service, Guid? customerId) : base(customerId, CustomerWorkflowQueries.WithWorkflowGraph)
    {
        this.service = service;
        this.contacts = service.Contacts;
    }

    public string? PortalStatus => !this.portalShown ? null
        : this.portalEnabled ? $"Customer portal: enabled. Jobs ordered through it go to {this.portalUnitName ?? "a unit no longer in use"}."
        : "Customer portal: not enabled.";

    public string PortalState => this.portalEnabled ? "enabled" : "none";

    public IReadOnlyList<CustomerPortalUnit> PortalUnits => this.portalUnits;

    public CustomerPortalRequest? PortalRequest { get; set; }

    private readonly CustomerContactService contacts;

    public bool CanManage => !this.fieldsLocked;

    protected override IEnumerable<StateEntityDialogView> DeclareViews() =>
    [
        new StateEntityDialogView(InfoView, "Info") { Icon = "storefront" },
        new StateEntityDialogListView<CustomerContactRow>(ContactsView, "Contacts", CustomerContactList.Definition, (query, cancellationToken) => this.service.QueryCustomerContactsAsync(EntityId!.Value, query, cancellationToken))
        {
            Order = 1,
            Icon = "contacts",
            Area = CustomerWorkflowAttribute.AreaContacts,
            AddLabel = "Add contact",
            Add = AddContactAsync,
            CanAdd = () => CanManage,
        },
    ];

    private async Task<bool> AddContactAsync(DialogService dialogs, CancellationToken cancellationToken)
    {
        if (EntityId is not { } id)
        {
            return false;
        }

        var result = await CustomerContactDialog.OpenCreateAsync(dialogs, this.contacts, id);
        return result.Changed;
    }

    public Task<StateEntityDialogResult> OpenContactAsync(DialogService dialogs, CustomerContactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return CustomerContactDialog.OpenAsync(dialogs, this.contacts, row.Id);
    }

    protected override Task<Result<DetachedEntityUpdater<Customer>>> LoadUpdaterAsync(Guid entityId, CancellationToken cancellationToken) =>
        this.service.LoadCustomerUpdaterAsync(entityId, cancellationToken);

    protected override async Task<Result> SaveAndTransitionAsync(DetachedEntityUpdater<Customer> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        if (transitionKey != CustomerWorkflowAttribute.EnablePortalStep)
        {
            return await this.service.UpdateCustomerAndTransitionAsync(updater, transitionKey, comment, cancellationToken);
        }

        var request = PortalRequest;
        PortalRequest = null;
        if (request is null)
        {
            return Result.Failure("Fill in the portal's unit and its first customer administrator first.");
        }

        var enabled = await this.service.EnableCustomerPortalAsync(updater, request, cancellationToken);

        if (enabled.IsSuccess && !string.IsNullOrWhiteSpace(enabled.Message))
        {
            ShowOnce(enabled.Message);
        }

        return enabled.IsSuccess ? Result.Success() : Result.Failure(enabled.Error);
    }

    protected override Task<Result> DeleteAsync(DetachedEntityUpdater<Customer> updater, string transitionKey, string? comment, CancellationToken cancellationToken) =>
        this.service.UpdateCustomerAndTransitionAsync(updater, transitionKey, comment, cancellationToken);

    protected override Task<Result<StateHistoryPage>> LoadHistoryAsync(Guid entityId, StateHistoryQuery query, CancellationToken cancellationToken) =>
        this.service.GetCustomerHistoryAsync(entityId, query, cancellationToken);

    protected override async Task<Result> LoadEditorOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await this.service.GetCustomerEditorOptionsAsync(EntityId, cancellationToken);
        if (!options.IsSuccess || options.Value is null)
        {
            return Result.Failure(options.Error);
        }

        this.fieldsLocked = options.Value.FieldsLocked;
        (this.portalShown, this.portalEnabled, this.portalUnitName, this.portalUnits) = (options.Value.Portal.Shown, options.Value.Portal.Enabled, options.Value.Portal.UnitName, options.Value.Portal.Units);
        return Result.Success();
    }

    protected override Task<Result<Customer>> NewEntityAsync(CancellationToken cancellationToken) =>
        this.service.NewCustomerAsync(cancellationToken);

    protected override Task<Result<Guid>> CreateEntityAsync(Customer entity, string transitionKey, CancellationToken cancellationToken) =>
        this.service.CreateCustomerAsync(entity, transitionKey, cancellationToken);

    protected override bool ReadsEditorOptionsWithEveryLoad => true;

    protected override bool IsLockedRecord(Customer entity) => this.fieldsLocked;
}
