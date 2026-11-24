using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Workflow;
using Staffora.Core.Workflow.WorkflowAttributes;
using Staffora.Data;

namespace Staffora.Services.Portal;

internal static class CustomerContactPairing
{
    public static async Task<PairedContact> FindOrAddAsync(ApplicationDbContext context, Guid tenantId, Guid customerId, Guid userId, string firstName, string lastName, string? email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var found = await context.CustomerContacts.AsNoTracking()
            .Where(contact => contact.TenantId == tenantId && contact.CustomerId == customerId && contact.LinkedUserId == userId)
            .Select(contact => new { contact.Id, contact.FirstName, contact.LastName })
            .FirstOrDefaultAsync(cancellationToken);
        if (found is not null)
        {
            return new PairedContact(found.Id, $"{found.FirstName} {found.LastName}".Trim(), Added: false);
        }

        var contact = new CustomerContact
        {
            TenantId = tenantId,
            CustomerId = customerId,
            FirstName = Cut(firstName, CustomerContact.NameMaxLength),
            LastName = Cut(lastName, CustomerContact.NameMaxLength),
            Email = email is { Length: > 0 } address ? Cut(address, CustomerContact.EmailMaxLength) : null,
            LinkedUserId = userId,
            State = CustomerContactState.Active,
            CreatedById = SystemAccount.IdFor(tenantId),
        };
        context.CustomerContacts.Add(contact);
        StateHistoryWriter.AddOnBehalfOf(context, tenantId, [new TransitionStep(contact, CustomerContactState.Create, CustomerContactState.Active, CustomerContactWorkflowAttribute.ActivateStep)]);
        return new PairedContact(contact.Id, $"{contact.FirstName} {contact.LastName}".Trim(), Added: true);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}

internal sealed record PairedContact(Guid Id, string Name, bool Added);
