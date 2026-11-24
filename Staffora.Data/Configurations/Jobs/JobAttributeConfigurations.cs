using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Jobs;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Data.Configurations.EntityAttributes;

namespace Staffora.Data.Configurations.Jobs;

public class JobAttributeDefinitionConfiguration : IEntityTypeConfiguration<JobAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<JobAttributeDefinition> builder)
    {
        builder.ConfigureAttributeDefinition<Job, JobAttributeDefinition>();
        builder.Property(definition => definition.Audience).IsRequired().HasDefaultValue(JobFieldAudience.Coordinators);
    }
}

public class JobAttributeConfiguration : IEntityTypeConfiguration<JobAttribute>
{
    public void Configure(EntityTypeBuilder<JobAttribute> builder)
    {
        builder.ConfigureEntityAttribute<Job, JobAttributeDefinition, JobAttribute>();
        builder.HasOne(a => a.Entity)
            .WithMany(e => e.Attributes)
            .HasForeignKey(a => a.EntityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
