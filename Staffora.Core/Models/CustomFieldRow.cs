using Staffora.Core.Entities.EntityAttributes;

namespace Staffora.Core.Models;

public sealed record CustomFieldRow(Guid Id, CustomFieldTarget Target, string Name, AttributeValueType ValueType, bool IsGlobal, bool CanEdit)
{
    public CustomFieldState State { get; init; } = CustomFieldState.Active;

    public IReadOnlyList<string> Options { get; init; } = [];

    public int RetiredOptions { get; init; }

    public string? TenantName { get; init; }

    public Guid? TenantId { get; init; }

    public string? Audience { get; init; }
}
