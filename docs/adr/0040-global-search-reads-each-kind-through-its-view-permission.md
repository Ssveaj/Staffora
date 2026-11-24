# ADR 0040: Global search reads each kind through its view permission, by name, in SQL

## Status

Accepted

## Context

Users need one search that finds staff members, jobs and customers by name, and the platform owner needs to find tenants on the console. Results must be ranked and grouped by kind, each hit filtered by the tenant and the reader's permissions in SQL through one service, and a hit must open the record's existing dialog or page.

## Decision

- **One service, one query per kind.** `GlobalSearchService` (`Staffora.Services/Search/`) decides from one snapshot of the session which kinds the reader may search, then queries each of them on one context of the runtime, so the tenant filter applies, with the runtime tenant also named in the `Where` clause. Each kind is a `LIKE` on its name columns (`Contains`, case-insensitive under the database's collation), ordered by whether the name starts with the words typed, then by name, and limited to five rows (`PerKind`) in SQL. The words typed are trimmed, need at least two characters and are truncated to 100.
- **A kind is searched only by a reader who may view it**, with the permission its own list page gates on: staff members with `ResourcesView`, jobs with any of `JobService.Readers`, customers with `CustomerService.Readers`.
  - A staff member who reads no job but their own (`ViewOwnAssignments` alone) finds the jobs of their own assignments, each hit being their assignment, which their assignment dialog shows them.
  - The platform owner on the console (platform reach) searches tenants only, under the platform lease with its reason. An impersonating platform owner searches as the impersonated user.
  - A session that may search nothing gets no search button and no shortcut.
- **A hit names what it opens** (`SearchOpens`). The shell opens it through the dialog route registry with the dialog's own id-only opener, so the address carries `?dlg=` and a reload restores it, or, for a tenant, navigates to its console page. The opener's load checks the reader and the tenant again.
- **No index and no schema change.** The name columns are short and a tenant's rows are few; a `LIKE` with a leading wildcard scans each tenant's rows of one table under its `TenantId` index.
- **The dialog is a form dialog** (`GlobalSearchDialog`, opened through `OpenFormDialogAsync`) following the ARIA combobox pattern: the field keeps the focus (`data-dialog-autofocus`, which the form dialog host honours), the arrow keys move the active option (`aria-activedescendant`), Enter opens the active hit, and the dialog handles Escape itself (`CloseOnEsc` off), so a key press closes it only once.

## Rationale

Searching each kind with the same permission as its list page, inside the tenant filter, means the search can never show a reader something the rest of the application would hide. Opening hits through the existing dialogs and pages means no new view of a record is needed and every load is checked again. A plain `LIKE` is adequate for the current data sizes and needs no schema change.

## Consequences

- A two-character search in a large tenant reads every row of four tables' name columns. When that becomes too slow, the next step is a full-text index per kind behind the same service, not a cache.
- Words a tenant chooses for its records (planned, [roadmap](../roadmap.md)) change only the dialog's labels.
- A new kind adds its permission, its query and its `SearchOpens` member in the one service, and its dialog token in the shell's button.

## Related Decisions

- [ADR 0013](0013-tenant-isolation-in-the-data-context.md): the tenant filter and the platform lease.
- [ADR 0017](0017-dialogs-load-through-one-service-call.md): dialogs opened by id.
