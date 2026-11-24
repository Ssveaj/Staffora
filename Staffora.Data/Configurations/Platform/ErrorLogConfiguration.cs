using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Staffora.Core.Entities.Platform;

namespace Staffora.Data.Configurations.Platform;

public sealed class ErrorLogConfiguration : IEntityTypeConfiguration<ErrorLog>
{
    public const int ExceptionDetailMaxLength = 32_000;

    public const int CategoryMaxLength = 256;
    public const int EventNameMaxLength = 256;
    public const int MessageMaxLength = 4_000;
    public const int ExceptionTypeMaxLength = 512;
    public const int CorrelationIdMaxLength = 64;
    public const int RequestPathMaxLength = 512;

    public void Configure(EntityTypeBuilder<ErrorLog> builder)
    {
        builder.HasKey(log => log.Id);
        builder.Property(log => log.Category).HasMaxLength(CategoryMaxLength);
        builder.Property(log => log.EventName).HasMaxLength(EventNameMaxLength);
        builder.Property(log => log.Message).HasMaxLength(MessageMaxLength);
        builder.Property(log => log.ExceptionType).HasMaxLength(ExceptionTypeMaxLength);
        builder.Property(log => log.ExceptionDetail).HasMaxLength(ExceptionDetailMaxLength);
        builder.Property(log => log.CorrelationId).HasMaxLength(CorrelationIdMaxLength);
        builder.Property(log => log.RequestPath).HasMaxLength(RequestPathMaxLength);
        builder.HasIndex(log => log.Occurred);
        builder.HasIndex(log => log.CorrelationId);
    }
}
