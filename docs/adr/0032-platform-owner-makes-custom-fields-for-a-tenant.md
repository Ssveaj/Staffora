# ADR 0032: Custom fields are the platform owner's, global or for one chosen tenant

## Status

Accepted. Amended by [ADR 0033](0033-custom-fields-have-a-lifecycle.md).

## Context

Custom fields could be made in two places: a tenant's administrators made the tenant's own fields in its Custom fields panel, and the platform owner made global fields, the same in every tenant. The platform settings never showed a tenant's fields, and every layer (service, data context, pages) had to tell a tenant's own fields from the platform's.

## Decision

- The platform owner alone makes and changes custom fields, from the platform settings.
- The field dialog's **Tenant** drop-down offers the staffing companies whose data is not held (Setup or Active). Left empty it makes a global field; a chosen company makes that company's field, which carries its `TenantId` and shows in its dialogs beside the global ones. The field's values are the company's data.
- A tenant reads its own fields and the global ones, read-only, in its Custom fields panel, and fills in their values on its records. No tenant permission, and no impersonating platform owner, writes a field.
- The platform settings list every field, global and per company, grouped by tenant.

## Rationale

One owner for every field keeps the rule simple in the service, the data context and the pages, and nothing in a tenant has to distinguish its own fields from the platform's.

## Consequences

- The data context writes a definition, global or a tenant's, only in system scope, and refuses every other write of one with one message (`ApplicationDbContext.TenantIsolation.cs`, `CheckAttributeDefinition`).
- The platform writes a tenant's field under the platform lease, stamped with the tenant's system account (`StampOnOtherTenantsBehalf`, which stamps a definition of another tenant as it stamps a tenant row), never with the platform owner, as the tenant promise in [PRODUCT.md](../../PRODUCT.md) requires of every row written on a tenant's behalf. It writes no field of a company whose data is held.
- A field is never deleted; it has a lifecycle (Draft, Active, Inactive) in a state-entity dialog of its own ([ADR 0033](0033-custom-fields-have-a-lifecycle.md)), and the values tenants hold are kept.
- `CustomFieldService` refuses every write from inside a tenant (`FieldsAreThePlatforms`). A tenant's Custom fields panel has no "New field" and opens every field read-only.
- The development demo and the browser journeys' Company A get their fields from the platform owner as it activates the tenant.
- [PRODUCT.md](../../PRODUCT.md): the platform owner sets up every custom field; a tenant administrator does not.

## Related Decisions

- Amends [ADR 0012](0012-platform-owner-and-tenant-onboarding.md) (an action of the platform owner in a tenant outside impersonation).
- [ADR 0033](0033-custom-fields-have-a-lifecycle.md): the field lifecycle.
- [ADR 0013](0013-tenant-isolation-in-the-data-context.md): the filter exceptions for custom field definitions and values.
