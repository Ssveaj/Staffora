using Staffora.Core.Entities.Customers;

namespace Staffora.Core.Models;

public sealed record CustomerListItem(Guid Id, string Name, string? Description, CustomerState State, int Contacts, int ActiveContacts)
{
    public string ContactsLabel => Contacts == 0 ? "None" : $"{Contacts} ({ActiveContacts} active)";
}

public sealed record CustomerContactRow(Guid Id, Guid CustomerId, string FirstName, string LastName, string? Title, string? Email, string? Phone, CustomerContactState State)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}
