# ADR 0028: The product is called Staffora

## Status

Accepted

## Context

The product serves any service business that staffs jobs with its own people ([ADR 0027](0027-generic-resource-staffing.md)). A name tied to one industry would tell companies in other industries that the product is not for them.

## Decision

The product, its solution and its projects are called **Staffora**: `Staffora.slnx`, `Staffora.Blazor`, `Staffora.Core`, `Staffora.Data`, `Staffora.Services`, `Staffora.E2ESetup` and `Staffora.Test`, with matching namespaces. The application's title, the shell's brand, the documentation, the local databases, the test databases and the environment variables the tests read carry the same name.

## Rationale

A name of its own, not tied to any industry, fits a product for any service business.

## Consequences

- The development database is `stafforadb`, and the test environment variables are `STAFFORA_INTEGRATION_CONNECTION`, `STAFFORA_E2E_CONNECTION` and `STAFFORA_E2E_WORKERS`.
- The authentication and antiforgery cookies are `Staffora.Auth` and `Staffora.Antiforgery`.
- The web project's user-secrets id is Staffora's own; no setting the application needs comes from user secrets.

## Related Decisions

- [ADR 0027](0027-generic-resource-staffing.md): the generic staffing domain.
