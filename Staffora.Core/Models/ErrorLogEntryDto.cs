using Staffora.Core.Entities.Platform;

namespace Staffora.Core.Models;

public sealed record ErrorLogEntryDto
{
    public Guid Id { get; init; }
    public DateTime Occurred { get; init; }
    public ErrorLogLevel Level { get; init; }
    public string Category { get; init; } = string.Empty;
    public int EventId { get; init; }
    public string? EventName { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ExceptionType { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestPath { get; init; }
    public Guid? TenantId { get; init; }
    public string? TenantName { get; init; }
    public Guid? UserId { get; init; }
    public string? UserName { get; init; }
    public Guid? ImpersonatorId { get; init; }
    public string? ImpersonatorName { get; init; }
}
