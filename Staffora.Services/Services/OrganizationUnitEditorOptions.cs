using Staffora.Core.Entities.Organization;
using Staffora.Core.Models;

namespace Staffora.Services.Services;

public sealed record OrganizationUnitEditorOptions(bool FieldsLocked, IReadOnlyList<OrganizationUnit> Parents, IReadOnlyList<OrganizationUnitTypeOption> Types, bool CanManageRoles, RoleHoldingChoices Choices);
