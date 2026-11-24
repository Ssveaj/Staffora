using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;

namespace Staffora.Services.Customers;

public sealed class CustomerContactService
{
    public const string NotFound = "Contact not found.";

    public const string NameMissing = "A contact needs a first and a last name.";

    public static readonly string TextTooLong =
        $"A contact's first and last name have at most {CustomerContact.NameMaxLength} characters each, the e-mail address {CustomerContact.EmailMaxLength}, the phone {CustomerContact.PhoneMaxLength} and the title {CustomerContact.TitleMaxLength}.";

    public const string CreateBySaveOrActivate = "A contact is added by saving it or by activating it.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly ILogger<CustomerContactService> log;

    public CustomerContactService(
        IApplicationDbContextFactory<ApplicationDbContext> dbFactory,
        IRuntimeExecutionContext runtime,
        PermissionService permissionService,
        WorkflowService workflows,
        StateHistoryService history,
        ILogger<CustomerContactService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.log = log;
    }

    public async Task<Result<DetachedEntityUpdater<CustomerContact>>> LoadCustomerContactUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(CustomerService.Readers, cancellationToken))
            return Result<DetachedEntityUpdater<CustomerContact>>.Failure(CustomerService.NoView);

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var contact = await context.CustomerContacts
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return contact is null
                ? Result<DetachedEntityUpdater<CustomerContact>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<CustomerContact>>.Success(new DetachedEntityUpdater<CustomerContact>(contact, context));
        }, this.log, $"the customer contact {id}", CustomerService.ReadFailed, cancellationToken);
    }

    public async Task<Result<CustomerContact>> NewCustomerContactAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken))
            return Result<CustomerContact>.Failure(CustomerService.NoManage);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<CustomerContact>.Failure(CustomerService.ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<CustomerContact>.Failure(IRuntimeExecutionContext.NoTenant);

        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        var customer = await context.Customers.AsNoTracking().FirstOrDefaultAsync(row => row.Id == customerId, cancellationToken);
        return customer is null
            ? Result<CustomerContact>.Failure(CustomerService.NotFound)
            : Result<CustomerContact>.Success(new CustomerContact { TenantId = tenantId, State = CustomerContactState.Create, CustomerId = customer.Id, Customer = customer });
    }

    public async Task<Result<Guid>> CreateCustomerContactAsync(CustomerContact contact, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken))
            return Result<Guid>.Failure(CustomerService.NoManage);

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
            return Result<Guid>.Failure(CustomerService.ChangeInOwnTenantOnly);

        if (session.TenantId is not { } tenantId)
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);

        if (transitionKey is not (CustomerContactWorkflowAttribute.SaveDraftStep or CustomerContactWorkflowAttribute.ActivateStep))
            return Result<Guid>.Failure(CreateBySaveOrActivate);

        var row = new CustomerContact
        {
            TenantId = tenantId,
            State = CustomerContactState.Create,
            CustomerId = contact.CustomerId,
            FirstName = contact.FirstName?.Trim() ?? string.Empty,
            LastName = contact.LastName?.Trim() ?? string.Empty,
            Email = Optional(contact.Email),
            Phone = Optional(contact.Phone),
            Title = Optional(contact.Title),
        };
        if (DescribeInvalidText(row) is { } textReason)
            return Result<Guid>.Failure(textReason);

        try
        {
            return await this.workflows.CreateAndTransitionAsync<CustomerContactState, CustomerContact>(row, transitionKey, CustomerContactWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Adding a contact to customer {CustomerId} failed.", contact.CustomerId);
            return Result<Guid>.Failure("An error occurred while adding the contact.");
        }
    }

    public async Task<Result> UpdateCustomerContactAndTransitionAsync(DetachedEntityUpdater<CustomerContact> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken))
            return Result.Failure(CustomerService.NoManage);

        if (this.runtime.Current.HasPlatformReach())
            return Result.Failure(CustomerService.ChangeInOwnTenantOnly);

        var id = (Guid)updater.OriginalValue(nameof(CustomerContact.Id))!;
        try
        {
            if (WorkflowRegistry.Default.Get<CustomerContactState, CustomerContact>().IsDeleteStep(transitionKey))
            {
                return await this.workflows.TransitionEntityAsync<CustomerContactState, CustomerContact>(id, transitionKey, CustomerContactWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }

            var contact = updater.Entity;
            Tidy(contact, updater);
            if (updater.HasChanges() && DescribeInvalidText(contact) is { } textReason)
                return Result.Failure(textReason);

            return await this.workflows.SaveAndTransitionAsync<CustomerContactState, CustomerContact>(updater, transitionKey, CustomerContactWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the contact {ContactId} failed.", id);
            return Result.Failure("An error occurred while saving the contact.");
        }
    }

    public async Task<Result<StateHistoryPage>> GetCustomerContactHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(CustomerService.Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<CustomerContact>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(CustomerService.NoView);

    public async Task<Result<CustomerContactEditorOptions>> GetCustomerContactEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var manages = await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken) && !this.runtime.Current.HasPlatformReach();
        if (id is null)
            return manages ? Result<CustomerContactEditorOptions>.Success(new CustomerContactEditorOptions(FieldsLocked: false)) : Result<CustomerContactEditorOptions>.Failure(CustomerService.NoManage);

        return manages || await this.permissionService.HasAnyPermissionAsync(CustomerService.Readers, cancellationToken)
            ? Result<CustomerContactEditorOptions>.Success(new CustomerContactEditorOptions(FieldsLocked: !manages))
            : Result<CustomerContactEditorOptions>.Failure(CustomerService.NoView);
    }

    private static void Tidy(CustomerContact contact, DetachedEntityUpdater<CustomerContact> updater)
    {
        if (!string.Equals(contact.FirstName, updater.OriginalValue(nameof(CustomerContact.FirstName)) as string, StringComparison.Ordinal))
        {
            contact.FirstName = contact.FirstName?.Trim() ?? string.Empty;
        }

        if (!string.Equals(contact.LastName, updater.OriginalValue(nameof(CustomerContact.LastName)) as string, StringComparison.Ordinal))
        {
            contact.LastName = contact.LastName?.Trim() ?? string.Empty;
        }

        if (!string.Equals(contact.Email, updater.OriginalValue(nameof(CustomerContact.Email)) as string, StringComparison.Ordinal))
        {
            contact.Email = Optional(contact.Email);
        }

        if (!string.Equals(contact.Phone, updater.OriginalValue(nameof(CustomerContact.Phone)) as string, StringComparison.Ordinal))
        {
            contact.Phone = Optional(contact.Phone);
        }

        if (!string.Equals(contact.Title, updater.OriginalValue(nameof(CustomerContact.Title)) as string, StringComparison.Ordinal))
        {
            contact.Title = Optional(contact.Title);
        }
    }

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? DescribeInvalidText(CustomerContact contact)
    {
        if (string.IsNullOrWhiteSpace(contact.FirstName) || string.IsNullOrWhiteSpace(contact.LastName))
            return NameMissing;

        return contact.FirstName.Length > CustomerContact.NameMaxLength
            || contact.LastName.Length > CustomerContact.NameMaxLength
            || contact.Email?.Length > CustomerContact.EmailMaxLength
            || contact.Phone?.Length > CustomerContact.PhoneMaxLength
            || contact.Title?.Length > CustomerContact.TitleMaxLength
            ? TextTooLong
            : null;
    }
}

public sealed record CustomerContactEditorOptions(bool FieldsLocked);
