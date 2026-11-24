using Staffora.Core.Entities.EntityAttributes;
using Staffora.Core.Entities.EntityAttributes.JobAttributes;
using Staffora.Services.Caching;

namespace Staffora.Services.CustomFields;

public sealed record CustomFieldDefinitionRow(Guid Id, Guid? TenantId, string Name, AttributeValueType ValueType, IReadOnlyList<AttributeChoiceOption> Options, bool IsRequired = false, JobFieldAudience? Audience = null) : ILookupRow;
