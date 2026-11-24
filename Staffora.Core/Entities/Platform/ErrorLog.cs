namespace Staffora.Core.Entities.Platform;

public class ErrorLog
{
    public Guid Id { get; set; }

    public DateTime Occurred { get; set; }

    public ErrorLogLevel Level { get; set; }

    public string Category { get; set; } = string.Empty;

    public int EventId { get; set; }

    public string? EventName { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ExceptionType { get; set; }

    public string? ExceptionDetail { get; set; }

    public string? CorrelationId { get; set; }

    public string? RequestPath { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? ImpersonatorId { get; set; }
}

public enum ErrorLogLevel
{
    Warning = 3,
    Error = 4,
    Critical = 5,
}
