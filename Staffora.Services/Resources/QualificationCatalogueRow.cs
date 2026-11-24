using Staffora.Core.Entities.Resources;
using Staffora.Services.Caching;

namespace Staffora.Services.Resources;

public sealed record QualificationCatalogueRow(Guid Id, Guid? TenantId, string Name, string? Description, QualificationState State) : ILookupRow;

public sealed record QualificationEditorOptions(bool FieldsLocked);
