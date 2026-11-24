using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Customers;

namespace Staffora.Data.Configurations.Customers;

public class CustomerContactConfiguration : IEntityTypeConfiguration<CustomerContact>
{
    public void Configure(EntityTypeBuilder<CustomerContact> builder)
    {
        builder.ConfigureTenantEntity();
        builder.Property(contact => contact.CustomerId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne(contact => contact.Customer)
            .WithMany(customer => customer.Contacts)
            .HasForeignKey(contact => contact.CustomerId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.Property(contact => contact.FirstName).HasMaxLength(CustomerContact.NameMaxLength).IsRequired();
        builder.Property(contact => contact.LastName).HasMaxLength(CustomerContact.NameMaxLength).IsRequired();
        builder.Property(contact => contact.Email).HasMaxLength(CustomerContact.EmailMaxLength);
        builder.Property(contact => contact.Phone).HasMaxLength(CustomerContact.PhoneMaxLength);
        builder.Property(contact => contact.Title).HasMaxLength(CustomerContact.TitleMaxLength);
        builder.HasIndex(contact => new { contact.CustomerId, contact.State });
        builder.HasIndex(contact => contact.LinkedUserId)
            .IsUnique()
            .HasFilter($"[{nameof(CustomerContact.LinkedUserId)}] IS NOT NULL");
    }
}
