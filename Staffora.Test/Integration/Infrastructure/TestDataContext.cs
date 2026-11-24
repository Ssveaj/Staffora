using Microsoft.EntityFrameworkCore;
using Staffora.Data;
using Staffora.Data.Configurations;
using Staffora.Data.Configurations.EntityAttributes;

namespace Staffora.Test.Integration.Infrastructure;

public sealed class TestDataContext : ApplicationDbContext
{
    public TestDataContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    public DbSet<TestParent> TestParents => Set<TestParent>();
    public DbSet<TestChild> TestChildren => Set<TestChild>();
    public DbSet<TestGrandchild> TestGrandchildren => Set<TestGrandchild>();
    public DbSet<TestParentLine> TestParentLines => Set<TestParentLine>();
    public DbSet<TestFolder> TestFolders => Set<TestFolder>();
    public DbSet<TestDocument> TestDocuments => Set<TestDocument>();
    public DbSet<TestParentAttributeDefinition> TestParentAttributeDefinitions => Set<TestParentAttributeDefinition>();
    public DbSet<TestParentAttribute> TestParentAttributes => Set<TestParentAttribute>();
    public DbSet<TestFolderAttributeDefinition> TestFolderAttributeDefinitions => Set<TestFolderAttributeDefinition>();
    public DbSet<TestFolderAttribute> TestFolderAttributes => Set<TestFolderAttribute>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<TestParent>(parent =>
        {
            parent.ConfigureTenantEntity();
            parent.Property(e => e.Name).HasMaxLength(200).IsRequired();
            parent.Property(e => e.Notes).HasMaxLength(500);
            parent.HasMany(e => e.Children)
                .WithOne(e => e.Parent)
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.NoAction);
            parent.HasMany(e => e.Lines)
                .WithOne()
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<TestParentLine>(line =>
        {
            line.HasKey(e => new { e.ParentId, e.Code });
            line.ConfigureTenantEntity();
            line.Property(e => e.Code).HasMaxLength(20);
            line.Property(e => e.Text).HasMaxLength(200).IsRequired();
        });

        builder.Entity<TestChild>(child =>
        {
            child.ConfigureTenantEntity();
            child.Property(e => e.Name).HasMaxLength(200).IsRequired();
            child.HasMany(e => e.Grandchildren)
                .WithOne(e => e.Child)
                .HasForeignKey(e => e.ChildId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<TestGrandchild>(grandchild =>
        {
            grandchild.ConfigureTenantEntity();
            grandchild.Property(e => e.Name).HasMaxLength(200).IsRequired();
        });

        builder.Entity<TestFolder>(folder =>
        {
            folder.ConfigureTenantEntity();
            folder.Property(e => e.Name).HasMaxLength(200).IsRequired();
            folder.HasMany(e => e.Documents)
                .WithOne(e => e.Folder)
                .HasForeignKey(e => e.FolderId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<TestDocument>(document =>
        {
            document.ConfigureTenantEntity();
            document.Property(e => e.Name).HasMaxLength(200).IsRequired();
        });

        builder.Entity<TestParentAttributeDefinition>(definition => definition.ConfigureAttributeDefinition<TestParent, TestParentAttributeDefinition>());
        builder.Entity<TestParentAttribute>(value =>
        {
            value.ConfigureEntityAttribute<TestParent, TestParentAttributeDefinition, TestParentAttribute>();
            value.HasOne(a => a.Entity).WithMany(e => e.Attributes).HasForeignKey(a => a.EntityId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<TestFolderAttributeDefinition>(definition => definition.ConfigureAttributeDefinition<TestFolder, TestFolderAttributeDefinition>());
        builder.Entity<TestFolderAttribute>(value =>
        {
            value.ConfigureEntityAttribute<TestFolder, TestFolderAttributeDefinition, TestFolderAttribute>();
            value.HasOne(a => a.Entity).WithMany(e => e.Attributes).HasForeignKey(a => a.EntityId).OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(builder);
    }
}
