using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Staffora.Core.Entities.Catalogue;
using Staffora.Core.Entities.Comments;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.Customers;
using Staffora.Core.Entities.Portal;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Core.Entities.EntityAttributes.OrganizationAttributes;
using Staffora.Core.Entities.EntityAttributes.UserAttributes;
using Staffora.Core.Entities.Identity;
using Staffora.Core.Entities.Integration;
using Staffora.Core.Entities.Messaging;
using Staffora.Core.Entities.Organization;
using Staffora.Core.Entities.Platform;
using Staffora.Core.Entities.Resources;
using Staffora.Core.Entities.Scheduling;
using Staffora.Core.Entities.Tenancy;
using Staffora.Core.Entities.Workflow;
using Staffora.Core.Execution;
using Staffora.Data.Configurations;
using Staffora.Data.Conventions;

namespace Staffora.Data;

public partial class ApplicationDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public IRuntimeExecutionContext? RuntimeContext { get; internal set; }

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<TenantRolePermission> TenantRolePermissions => Set<TenantRolePermission>();

    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();
    public DbSet<OrganizationUnit> OrganizationUnits => Set<OrganizationUnit>();
    public DbSet<OrganizationUnitUserRole> OrganizationUnitUserRoles => Set<OrganizationUnitUserRole>();
    public DbSet<OrganizationUnitAttributeDefinition> OrganizationUnitAttributeDefinitions => Set<OrganizationUnitAttributeDefinition>();
    public DbSet<OrganizationUnitAttribute> OrganizationUnitAttributes => Set<OrganizationUnitAttribute>();
    public DbSet<ApplicationUserAttributeDefinition> ApplicationUserAttributeDefinitions => Set<ApplicationUserAttributeDefinition>();
    public DbSet<ApplicationUserAttribute> ApplicationUserAttributes => Set<ApplicationUserAttribute>();

    public DbSet<JobAttributeDefinition> JobAttributeDefinitions => Set<JobAttributeDefinition>();

    public DbSet<JobAttribute> JobAttributes => Set<JobAttribute>();
    public DbSet<StateHistoryEntry> StateHistoryEntries => Set<StateHistoryEntry>();

    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<ResourceUnitMembership> ResourceUnitMemberships => Set<ResourceUnitMembership>();

    public DbSet<Qualification> Qualifications => Set<Qualification>();

    public DbSet<ResourceQualification> ResourceQualifications => Set<ResourceQualification>();

    public DbSet<WorkPattern> WorkPatterns => Set<WorkPattern>();

    public DbSet<WorkPatternSegment> WorkPatternSegments => Set<WorkPatternSegment>();

    public DbSet<ResourcePatternAssignment> ResourcePatternAssignments => Set<ResourcePatternAssignment>();

    public DbSet<ResourceAbsence> ResourceAbsences => Set<ResourceAbsence>();

    public DbSet<ClosedDay> ClosedDays => Set<ClosedDay>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerContact> CustomerContacts => Set<CustomerContact>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderStaff> OrderStaff => Set<OrderStaff>();

    public DbSet<OfferedService> OfferedServices => Set<OfferedService>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobTimeSlot> JobTimeSlots => Set<JobTimeSlot>();

    public DbSet<JobSlot> JobSlots => Set<JobSlot>();

    public DbSet<JobSlotQualification> JobSlotQualifications => Set<JobSlotQualification>();

    public DbSet<Service> Services => Set<Service>();

    public DbSet<ServiceQualification> ServiceQualifications => Set<ServiceQualification>();

    public DbSet<SlotAssignment> SlotAssignments => Set<SlotAssignment>();

    public DbSet<RequestRound> RequestRounds => Set<RequestRound>();

    public DbSet<CommentContainer> CommentContainers => Set<CommentContainer>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<OutgoingMessage> OutgoingMessages => Set<OutgoingMessage>();

    public DbSet<StoredMessage> StoredMessages => Set<StoredMessage>();

    public DbSet<MessageRecipient> MessageRecipients => Set<MessageRecipient>();

    public DbSet<IntegrationMessage> IntegrationMessages => Set<IntegrationMessage>();

    public DbSet<IntegrationReceipt> IntegrationReceipts => Set<IntegrationReceipt>();

    public DbSet<ImpersonationAudit> ImpersonationAudits => Set<ImpersonationAudit>();

    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    private void RenewUserConcurrencyStamps()
    {
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>().Where(entry => entry.State == EntityState.Modified))
        {
            var stamp = entry.Property(user => user.ConcurrencyStamp);
            if (!stamp.IsModified || Equals(stamp.CurrentValue, stamp.OriginalValue))
            {
                stamp.CurrentValue = Guid.NewGuid().ToString();
                stamp.IsModified = true;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        builder.ConfigureStateEntities();
        AddTenantFilters(builder);
    }
}
