using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Execution;
using Staffora.Core.Models;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;
using Staffora.Services.Authorization;
using Staffora.Services.Tenancy;

namespace Staffora.Services.Services;

public sealed record TenantOnboarding(string? TenantName, string? TenantDescription, string? OwnerFirstName, string? OwnerLastName, string? OwnerEmail);

public sealed record OnboardedTenant(Guid TenantId, Guid OwnerId, string OwnerEmail, string InitialPassword)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"{nameof(TenantId)} = {TenantId}, {nameof(OwnerId)} = {OwnerId}, {nameof(OwnerEmail)} = {OwnerEmail}, {nameof(InitialPassword)} = (not shown)");
        return true;
    }
}

public sealed class TenantOnboardingService
{
    public const string ConsoleOnly = TenantService.ConsoleOnlyCreates;

    public const string OwnerRequired = "A new tenant needs its first system owner: a first name, a last name and an email address.";

    public const int TenantNameMaxLength = 200;

    public const int TenantDescriptionMaxLength = 500;

    public const int NameMaxLength = 100;

    public const int EmailMaxLength = 256;

    public static readonly string OwnerNameTooLong = $"A first or last name has at most {NameMaxLength} characters.";

    public static readonly string TenantNameTooLong = $"A tenant's name has at most {TenantNameMaxLength} characters.";

    public static readonly string TenantDescriptionTooLong = $"A tenant's description has at most {TenantDescriptionMaxLength} characters.";

    public static readonly string OwnerEmailTooLong = $"An email address has at most {EmailMaxLength} characters.";

    private readonly IRuntimeExecutionContext runtime;
    private readonly TenantService tenants;
    private readonly UserService users;

    public TenantOnboardingService(IRuntimeExecutionContext runtime, TenantService tenants, UserService users)
    {
        this.runtime = runtime;
        this.tenants = tenants;
        this.users = users;
    }

    public async Task<Result<OnboardedTenant>> OnboardTenantAsync(TenantOnboarding request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!this.runtime.HasPlatformReach())
            return Result<OnboardedTenant>.Failure(ConsoleOnly);

        var firstName = request.OwnerFirstName?.Trim() ?? string.Empty;
        var lastName = request.OwnerLastName?.Trim() ?? string.Empty;
        var email = request.OwnerEmail?.Trim() ?? string.Empty;
        if (firstName.Length == 0 || lastName.Length == 0 || email.Length == 0)
            return Result<OnboardedTenant>.Failure(OwnerRequired);

        if (firstName.Length > NameMaxLength || lastName.Length > NameMaxLength)
            return Result<OnboardedTenant>.Failure(OwnerNameTooLong);

        if (email.Length > EmailMaxLength)
            return Result<OnboardedTenant>.Failure(OwnerEmailTooLong);

        if ((request.TenantName?.Trim().Length ?? 0) > TenantNameMaxLength)
            return Result<OnboardedTenant>.Failure(TenantNameTooLong);

        if ((request.TenantDescription?.Trim().Length ?? 0) > TenantDescriptionMaxLength)
            return Result<OnboardedTenant>.Failure(TenantDescriptionTooLong);

        var password = UserPasswords.Generate();
        var built = await this.users.NewUserRowAsync(new ApplicationUser { FirstName = firstName, LastName = lastName, Email = email, UserName = email }, Guid.Empty, password);
        if (!built.IsSuccess || built.Value is not { } owner)
            return Result<OnboardedTenant>.Failure(built.Error);

        var tenant = new Tenant { Name = request.TenantName ?? string.Empty, Description = request.TenantDescription };
        var created = await this.tenants.CreateTenantAsync(tenant, TenantWorkflowAttribute.SaveSetupStep, cancellationToken, onboard: (context, tenantId, ct) => TenantOnboardingWriter.WriteAsync(context, tenantId, owner, ct));
        if (!created.IsSuccess)
        {
            return !string.Equals(created.Error, TenantService.NameTaken, StringComparison.Ordinal) && await this.users.SignInNameHeldByAnotherAsync(owner)
                ? Result<OnboardedTenant>.Failure(UserService.SignInNameUnavailable)
                : Result<OnboardedTenant>.Failure(created.Error);
        }

        return Result<OnboardedTenant>.Success(
            new OnboardedTenant(created.Value, owner.Id, email, password),
            "The tenant is created in Setup with its first system owner, who signs in with the initial password shown now, once.");
    }

    internal async Task OnboardManagedTenantAsync(ApplicationDbContext context, ManagedTenantOnboarding portal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portal);
        var session = this.runtime.Current;
        if (session.HasPlatformReach() || session.TenantId != portal.StaffingCompanyId)
        {
            throw new InvalidOperationException("A customer's portal is enabled only inside its staffing company's own step.");
        }

        Tenant tenant;
        string staffingCompany;
        using (context.BeginSystemScope("enable a customer's portal: create its managed tenant, Active, on its behalf"))
        {
            var parent = await context.Tenants.AsNoTracking().Where(row => row.Id == portal.StaffingCompanyId).Select(row => new { row.Name, row.State }).SingleAsync(cancellationToken);
            if (parent.State != TenantState.Active)
            {
                throw new InvalidOperationException("A customer's portal is enabled only by a staffing company in use.");
            }

            staffingCompany = parent.Name;
            tenant = new Tenant
            {
                Id = portal.TenantId,
                Name = await FreeTenantNameAsync(context, OwnNameOf(portal.Name), cancellationToken),
                State = TenantState.Active,
                ParentTenantId = portal.StaffingCompanyId,
                CreatedById = SystemAccount.IdFor(portal.TenantId),
            };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync(cancellationToken);
        }

        await this.tenants.GiveNewTenantItsOwnRowsAsync(context, tenant, cancellationToken, OwnNameOf(portal.Name));
        await TenantOnboardingWriter.WriteManagedAsync(context, tenant.Id, portal.Administrator, cancellationToken);

        using (context.BeginSystemScope("enable a customer's portal: its supplier party and its history, on its behalf"))
        {
            var supplier = new Supplier
            {
                TenantId = tenant.Id,
                Name = staffingCompany,
                LinkedTenantId = portal.StaffingCompanyId,
                State = SupplierState.Active,
                CreatedById = SystemAccount.IdFor(tenant.Id),
            };
            context.Suppliers.Add(supplier);

            StateHistoryWriter.AddOnBehalfOf(context, tenant.Id,
            [
                new TransitionStep(tenant, TenantState.Setup, TenantState.Active, TenantWorkflowAttribute.ActivateStep),
                new TransitionStep(supplier, SupplierState.Create, SupplierState.Active, SupplierWorkflowAttribute.OpenStep),
            ]);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private static string OwnNameOf(string customerName)
    {
        var name = customerName.Trim();
        return name.Length == 0 ? "Customer portal" : name[..Math.Min(name.Length, TenantNameMaxLength)];
    }

    private static async Task<string> FreeTenantNameAsync(ApplicationDbContext context, string name, CancellationToken cancellationToken)
    {
        if (!await context.Tenants.AsNoTracking().AnyAsync(row => row.Name == name, cancellationToken))
        {
            return name;
        }

        for (var number = 2; ; number++)
        {
            var suffix = $" ({number})";
            var candidate = name[..Math.Min(name.Length, TenantNameMaxLength - suffix.Length)].TrimEnd() + suffix;
            if (!await context.Tenants.AsNoTracking().AnyAsync(row => row.Name == candidate, cancellationToken))
            {
                return candidate;
            }
        }
    }
}

internal sealed record ManagedTenantOnboarding(Guid TenantId, string Name, Guid StaffingCompanyId, ApplicationUser Administrator);
