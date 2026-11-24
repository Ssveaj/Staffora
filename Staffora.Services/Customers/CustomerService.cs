using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Staffora.Core.Entities;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Lists;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Workflow;
using Staffora.Services.Diagnostics;
using Staffora.Services.Services;
using Staffora.Services.Settings;

namespace Staffora.Services.Customers;

public sealed class CustomerService
{
    public static readonly IReadOnlyList<RolePermission> Readers = [RolePermission.CustomersView, RolePermission.CustomersManage];

    public const string NoView = "You do not have permission to view customers.";

    public const string NoManage = "You do not have permission to manage customers.";

    public const string NotFound = "Customer not found.";

    public const string NameTaken = "Another customer already has this name.";

    public static readonly NameAndDescriptionRule TextRule = new("customer", Customer.NameMaxLength, Customer.DescriptionMaxLength);

    public const string CreateBySaveOrActivate = "A customer is created by saving it as a draft or by activating it.";

    public const string ChangeInOwnTenantOnly = "The platform console keeps no tenant's customers: impersonate one of the tenant's users to change them.";

    public const string ReadFailed = "The customers could not be read. Try again.";

    public const string NoPortalEnable = "You do not have permission to enable a customer's portal.";

    public const string PortalAdministratorRequired = "A portal needs its first customer administrator: a first name, a last name and an email address.";

    public const string PortalUnitRequired = "Choose the unit in use that the jobs ordered through the portal go to.";

    public const string PortalNotEnabled = "The customer's portal could not be enabled, so nothing was changed.";

    public const string PortalBeingEnabled = "The customer's portal is being enabled already. Wait for it to finish.";

    public const string PortalRequiresActiveCompany = "Your organization is still being set up: a customer's portal is enabled once it is in use.";

    private readonly IApplicationDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly IRuntimeExecutionContext runtime;
    private readonly PermissionService permissionService;
    private readonly WorkflowService workflows;
    private readonly StateHistoryService history;
    private readonly CustomerContactService contacts;
    private readonly UserService users;
    private readonly CustomerPortalProvisioner portals;
    private readonly TenantSettingsEnsureService settingsEnsure;
    private readonly ILogger<CustomerService> log;

    public CustomerService(IApplicationDbContextFactory<ApplicationDbContext> dbFactory, IRuntimeExecutionContext runtime, PermissionService permissionService, WorkflowService workflows, StateHistoryService history, CustomerContactService contacts, UserService users, CustomerPortalProvisioner portals, TenantSettingsEnsureService settingsEnsure, ILogger<CustomerService> log)
    {
        this.dbFactory = dbFactory;
        this.runtime = runtime;
        this.permissionService = permissionService;
        this.workflows = workflows;
        this.history = history;
        this.contacts = contacts;
        this.users = users;
        this.portals = portals;
        this.settingsEnsure = settingsEnsure;
        this.log = log;
    }

    public CustomerContactService Contacts => this.contacts;

    public async Task<Result<ListPage<CustomerListItem>>> QueryCustomersAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
        {
            return Result<ListPage<CustomerListItem>>.Failure(NoView);
        }

        if (this.runtime.TenantId is not { } tenantId)
        {
            return Result<ListPage<CustomerListItem>>.Failure(IRuntimeExecutionContext.NoTenant);
        }

        var list = CustomerList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var customers = context.Customers.AsNoTracking().Where(customer => customer.TenantId == tenantId);
            if (list.Search is { Length: > 0 } search)
            {
                customers = customers.Where(customer => customer.Name.Contains(search) || (customer.Description != null && customer.Description.Contains(search)));
            }

            if (list.Filter(CustomerList.Status) is { } status && ListOption.TryParseEnum<CustomerState>(status, out var state))
            {
                customers = customers.Where(customer => customer.State == state);
            }

            var total = await customers.CountAsync(cancellationToken);
            var page = ListPage<CustomerListItem>.PageWithin(list, total);
            var rows = await new ListOrder<Customer>(customers)
                .By(customer => customer.Name, list.Descending)
                .By(customer => customer.Id)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(customer => new { customer.Id, customer.Name, customer.Description, customer.State })
                .ToListAsync(cancellationToken);
            var ids = rows.Select(row => row.Id).ToList();
            var counts = await context.CustomerContacts.AsNoTracking()
                .Where(contact => contact.TenantId == tenantId && ids.Contains(contact.CustomerId))
                .GroupBy(contact => contact.CustomerId)
                .Select(group => new { CustomerId = group.Key, Count = group.Count(), Active = group.Count(contact => contact.State == CustomerContactState.Active) })
                .ToDictionaryAsync(group => group.CustomerId, cancellationToken);
            List<CustomerListItem> items = [.. rows.Select(row => new CustomerListItem(row.Id, row.Name, row.Description, row.State, counts.GetValueOrDefault(row.Id)?.Count ?? 0, counts.GetValueOrDefault(row.Id)?.Active ?? 0))];
            return Result<ListPage<CustomerListItem>>.Success(ListPage<CustomerListItem>.Ungrouped(items, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the customers of tenant {TenantId} failed.", tenantId);
            return Result<ListPage<CustomerListItem>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<ListPage<CustomerContactRow>>> QueryCustomerContactsAsync(Guid customerId, ListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
        {
            return Result<ListPage<CustomerContactRow>>.Failure(NoView);
        }

        var list = CustomerContactList.Definition.Normalize(query);
        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var rows = context.CustomerContacts.AsNoTracking().Where(contact => contact.CustomerId == customerId);
            if (list.Search is { Length: > 0 } search)
            {
                rows = rows.Where(contact => contact.FirstName.Contains(search) || contact.LastName.Contains(search)
                    || (contact.Email != null && contact.Email.Contains(search)) || (contact.Title != null && contact.Title.Contains(search)));
            }

            if (list.Filter(CustomerContactList.Status) is { } status && ListOption.TryParseEnum<CustomerContactState>(status, out var state))
            {
                rows = rows.Where(contact => contact.State == state);
            }

            var total = await rows.CountAsync(cancellationToken);
            var page = ListPage<CustomerContactRow>.PageWithin(list, total);
            var items = await new ListOrder<CustomerContact>(rows)
                .By(contact => contact.LastName, list.Descending)
                .By(contact => contact.FirstName, list.Descending)
                .By(contact => contact.Id)
                .Query
                .Skip((page - 1) * list.PageSize)
                .Take(list.PageSize)
                .Select(contact => new CustomerContactRow(contact.Id, contact.CustomerId, contact.FirstName, contact.LastName, contact.Title, contact.Email, contact.Phone, contact.State))
                .ToListAsync(cancellationToken);
            return Result<ListPage<CustomerContactRow>>.Success(ListPage<CustomerContactRow>.Ungrouped(items, total, page, list.PageSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the contacts of customer {CustomerId} failed.", customerId);
            return Result<ListPage<CustomerContactRow>>.Failure(ReadFailed);
        }
    }

    public async Task<Result<DetachedEntityUpdater<Customer>>> LoadCustomerUpdaterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
        {
            return Result<DetachedEntityUpdater<Customer>>.Failure(NoView);
        }

        return await DeadlockRetry.ReadAsync(async token =>
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var customer = await context.Customers
                .WithWorkflowGraph()
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == id, token);
            return customer is null
                ? Result<DetachedEntityUpdater<Customer>>.Failure(NotFound)
                : Result<DetachedEntityUpdater<Customer>>.Success(new DetachedEntityUpdater<Customer>(customer, context));
        }, this.log, $"the customer {id}", ReadFailed, cancellationToken);
    }

    public async Task<Result<Customer>> NewCustomerAsync(CancellationToken cancellationToken)
    {
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken))
        {
            return Result<Customer>.Failure(NoManage);
        }

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
        {
            return Result<Customer>.Failure(ChangeInOwnTenantOnly);
        }

        return session.TenantId is { } tenantId
            ? Result<Customer>.Success(new Customer { TenantId = tenantId, State = CustomerState.Create })
            : Result<Customer>.Failure(IRuntimeExecutionContext.NoTenant);
    }

    public async Task<Result<Guid>> CreateCustomerAsync(Customer customer, string transitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customer);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken))
        {
            return Result<Guid>.Failure(NoManage);
        }

        var session = this.runtime.Current;
        if (session.HasPlatformReach())
        {
            return Result<Guid>.Failure(ChangeInOwnTenantOnly);
        }

        if (session.TenantId is not { } tenantId)
        {
            return Result<Guid>.Failure(IRuntimeExecutionContext.NoTenant);
        }

        if (transitionKey is not (CustomerWorkflowAttribute.SaveDraftStep or CustomerWorkflowAttribute.ActivateStep))
        {
            return Result<Guid>.Failure(CreateBySaveOrActivate);
        }

        var name = customer.Name?.Trim() ?? string.Empty;
        var description = string.IsNullOrWhiteSpace(customer.Description) ? null : customer.Description.Trim();
        if (TextRule.Problem(name, description) is { } textReason)
        {
            return Result<Guid>.Failure(textReason);
        }

        try
        {
            if (await NameTakenAsync(name, null, cancellationToken))
            {
                return Result<Guid>.Failure(NameTaken);
            }

            var row = new Customer { TenantId = tenantId, Name = name, Description = description, State = CustomerState.Create };
            var created = await this.workflows.CreateAndTransitionAsync<CustomerState, Customer>(row, transitionKey, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);

            return !created.IsSuccess && await NameTakenAsync(name, null, cancellationToken) ? Result<Guid>.Failure(NameTaken) : created;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Creating a customer in tenant {TenantId} failed.", tenantId);
            return Result<Guid>.Failure("An error occurred while creating the customer.");
        }
    }

    public async Task<Result> UpdateCustomerAndTransitionAsync(DetachedEntityUpdater<Customer> updater, string transitionKey, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken))
        {
            return Result.Failure(NoManage);
        }

        if (this.runtime.Current.HasPlatformReach())
        {
            return Result.Failure(ChangeInOwnTenantOnly);
        }

        var id = (Guid)updater.OriginalValue(nameof(Customer.Id))!;
        try
        {
            if (WorkflowRegistry.Default.Get<CustomerState, Customer>().IsDeleteStep(transitionKey))
            {
                return await this.workflows.TransitionEntityAsync<CustomerState, Customer>(id, transitionKey, CustomerWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            }

            var customer = updater.Entity;
            if (customer.Name is { } typed && typed != typed.Trim())
            {
                customer.Name = typed.Trim();
            }

            var renamed = !string.Equals(customer.Name, updater.OriginalValue(nameof(Customer.Name)) as string, StringComparison.Ordinal);
            if (updater.HasChanges())
            {
                if (TextRule.Problem(customer.Name ?? string.Empty, customer.Description) is { } textReason)
                {
                    return Result.Failure(textReason);
                }

                if (renamed && await NameTakenAsync(customer.Name!, id, cancellationToken))
                {
                    return Result.Failure(NameTaken);
                }
            }

            var saved = await this.workflows.SaveAndTransitionAsync<CustomerState, Customer>(updater, transitionKey, CustomerWorkflowQueries.WithWorkflowGraph, comment, cancellationToken);
            return !saved.IsSuccess && renamed && await NameTakenAsync(customer.Name ?? string.Empty, id, cancellationToken) ? Result.Failure(NameTaken) : saved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Saving the customer {CustomerId} failed.", id);
            return Result.Failure("An error occurred while saving the customer.");
        }
    }

    public async Task<Result<EnabledCustomerPortal>> EnableCustomerPortalAsync(DetachedEntityUpdater<Customer> updater, CustomerPortalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(request);
        if (!await this.permissionService.HasPermissionAsync(RolePermission.CustomerPortalEnable, cancellationToken))
        {
            return Result<EnabledCustomerPortal>.Failure(NoPortalEnable);
        }

        if (this.runtime.Current.HasPlatformReach())
        {
            return Result<EnabledCustomerPortal>.Failure(ChangeInOwnTenantOnly);
        }

        var firstName = request.FirstName?.Trim() ?? string.Empty;
        var lastName = request.LastName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        if (firstName.Length == 0 || lastName.Length == 0 || email.Length == 0)
        {
            return Result<EnabledCustomerPortal>.Failure(PortalAdministratorRequired);
        }

        if (firstName.Length > TenantOnboardingService.NameMaxLength || lastName.Length > TenantOnboardingService.NameMaxLength)
        {
            return Result<EnabledCustomerPortal>.Failure(TenantOnboardingService.OwnerNameTooLong);
        }

        if (email.Length > TenantOnboardingService.EmailMaxLength)
        {
            return Result<EnabledCustomerPortal>.Failure(TenantOnboardingService.OwnerEmailTooLong);
        }

        var id = (Guid)updater.OriginalValue(nameof(Customer.Id))!;
        try
        {
            if (request.UnitId is not { } unitId || !(await ReadPortalUnitsAsync(cancellationToken)).Any(unit => unit.Id == unitId))
            {
                return Result<EnabledCustomerPortal>.Failure(PortalUnitRequired);
            }

            if (await HasPortalAsync(id, cancellationToken))
            {
                return Result<EnabledCustomerPortal>.Failure(CustomerWorkflowAttribute.PortalNotEnabledReason);
            }

            if (await OwnTenantStateAsync(cancellationToken) == TenantState.Setup)
            {
                return Result<EnabledCustomerPortal>.Failure(PortalRequiresActiveCompany);
            }

            var password = UserPasswords.Generate();
            var built = await this.users.NewUserRowAsync(new ApplicationUser { FirstName = firstName, LastName = lastName, Email = email, UserName = email }, Guid.Empty, password);
            if (!built.IsSuccess || built.Value is not { } administrator)
            {
                return Result<EnabledCustomerPortal>.Failure(built.Error);
            }

            if (!this.portals.Prepare(id, unitId, administrator))
            {
                return Result<EnabledCustomerPortal>.Failure(PortalBeingEnabled);
            }

            Result enabled;
            Guid? portalTenant;
            try
            {
                enabled = await this.workflows.SaveAndTransitionAsync<CustomerState, Customer>(updater, CustomerWorkflowAttribute.EnablePortalStep, CustomerWorkflowQueries.WithWorkflowGraph, cancellationToken: cancellationToken);
                portalTenant = this.portals.LinkedTenantOf(id);
            }
            finally
            {
                this.portals.Forget(id);
            }

            if (!enabled.IsSuccess)
            {
                return !await HasPortalAsync(id, cancellationToken) && await this.users.SignInNameHeldByAnotherAsync(administrator)
                    ? Result<EnabledCustomerPortal>.Failure(UserService.SignInNameUnavailable)
                    : Result<EnabledCustomerPortal>.Failure(enabled.Error ?? PortalNotEnabled);
            }

            if (portalTenant is { } tenant)
            {
                this.settingsEnsure.ForgetCachedSettings(tenant);
            }

            return Result<EnabledCustomerPortal>.Success(
                new EnabledCustomerPortal(email, password),
                $"The portal is enabled. {firstName} {lastName} signs in as its customer administrator with {email} and the initial password {password} - give it to them now: it is shown only this once.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Enabling the portal of customer {CustomerId} failed.", id);
            return Result<EnabledCustomerPortal>.Failure(PortalNotEnabled);
        }
    }

    private async Task<bool> HasPortalAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Customers.AsNoTracking().AnyAsync(customer => customer.Id == id && customer.LinkedTenantId != null, cancellationToken);
    }

    private async Task<TenantState?> OwnTenantStateAsync(CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Tenants.AsNoTracking().Where(tenant => tenant.Id == this.runtime.Current.TenantId).Select(tenant => (TenantState?)tenant.State).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<CustomerPortalUnit>> ReadPortalUnitsAsync(CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.OrganizationUnits.AsNoTracking()
            .Where(unit => unit.State == OrganizationUnitState.Active || unit.UnitType == OrganizationUnitType.Company)
            .OrderBy(unit => unit.UnitType == OrganizationUnitType.Company ? 0 : 1).ThenBy(unit => unit.Name)
            .Select(unit => new CustomerPortalUnit(unit.Id, unit.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<StateHistoryPage>> GetCustomerHistoryAsync(Guid id, StateHistoryQuery query, CancellationToken cancellationToken) =>
        await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken)
            ? await this.history.GetHistoryAsync<Customer>(id, query, cancellationToken)
            : Result<StateHistoryPage>.Failure(NoView);

    public async Task<Result<CustomerEditorOptions>> GetCustomerEditorOptionsAsync(Guid? id, CancellationToken cancellationToken)
    {
        var console = this.runtime.Current.HasPlatformReach();
        var manages = await this.permissionService.HasPermissionAsync(RolePermission.CustomersManage, cancellationToken) && !console;
        if (id is null)
        {
            return manages ? Result<CustomerEditorOptions>.Success(new CustomerEditorOptions(FieldsLocked: false)) : Result<CustomerEditorOptions>.Failure(NoManage);
        }

        if (!manages && !await this.permissionService.HasAnyPermissionAsync(Readers, cancellationToken))
        {
            return Result<CustomerEditorOptions>.Failure(NoView);
        }

        try
        {
            await using var context = this.dbFactory.CreateDbContext(this.runtime);
            var portal = await context.Customers.AsNoTracking()
                .Where(customer => customer.Id == id)
                .Select(customer => new { Enabled = customer.LinkedTenantId != null, Unit = customer.DefaultUnit == null ? null : customer.DefaultUnit.Name })
                .FirstOrDefaultAsync(cancellationToken);
            var enables = !console && await this.permissionService.HasPermissionAsync(RolePermission.CustomerPortalEnable, cancellationToken);
            var units = enables && portal is { Enabled: false } ? await ReadPortalUnitsAsync(cancellationToken) : [];
            return Result<CustomerEditorOptions>.Success(new CustomerEditorOptions(FieldsLocked: !manages)
            {
                Portal = new CustomerPortalOptions(Shown: portal is not null, portal?.Enabled ?? false, portal?.Unit, units),
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            this.log.LogError(exception, "Reading the editor options of customer {CustomerId} failed.", id);
            return Result<CustomerEditorOptions>.Failure(ReadFailed);
        }
    }

    private async Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        await using var context = this.dbFactory.CreateDbContext(this.runtime);
        return await context.Customers.AsNoTracking().AnyAsync(row => row.Name == name && row.Id != exceptId, cancellationToken);
    }
}

public sealed record CustomerEditorOptions(bool FieldsLocked)
{
    public CustomerPortalOptions Portal { get; init; } = CustomerPortalOptions.None;
}

public sealed record CustomerPortalOptions(bool Shown, bool Enabled, string? UnitName, IReadOnlyList<CustomerPortalUnit> Units)
{
    public static CustomerPortalOptions None { get; } = new(false, false, null, []);
}

public sealed record CustomerPortalUnit(Guid Id, string Name);

public sealed record CustomerPortalRequest(Guid? UnitId, string? FirstName, string? LastName, string? Email);

public sealed record EnabledCustomerPortal(string AdministratorEmail, string InitialPassword)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"{nameof(AdministratorEmail)} = {AdministratorEmail}, {nameof(InitialPassword)} = (not shown)");
        return true;
    }
}
