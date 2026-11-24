# Roadmap

Planned functionality that does not exist in the code yet. Implemented features are described in [features.md](features.md) and the documents it lists. When an item ships, its description moves to the feature documentation and the item is removed here. The order of the lists is not a schedule.

## Product

- **Tenant vocabulary:** words a tenant chooses for its staff and jobs ("Cleaner", "Visit"), held as a tenant setting and read by every page, notice and portal text.
- **Material resources:** vehicles, equipment and rooms as resources without a user.
- **On-call schedules.**

## Authorization

- **Unit-scoped coordination:** a coordinator who sees only part of the company. Today roles are held on a unit and grant permissions tenant-wide ([ADR 0021](adr/0021-one-organization-tree-per-tenant.md)).

## Scheduling and staffing

- **Weekly capacity** per staff member.
- **Per-session staffing:** staffing individual occurrences of a recurring job separately.
- **A job-wide thread** to all of a job's staff members.
- **A customer blocking a staff member** from its jobs.

## Customer portal

- Mirroring jobs the staffing company registers itself into the customer's portal.
- Customer edits to an order after it is sent.
- Staff members the customer wishes for on an order.

## Notifications and identity

- An e-mail channel with per-channel preferences.
- Invitations and password reset by e-mail.
- SMS, a daily digest, per-type notification preferences and notice templates a tenant edits.

## Platform

- Session profiles that narrow a session to one of several roles.
- Role templates the platform owner edits.
- Entity icons with a selector, and section theming.
- Custom field columns and filters in lists, and archiving custom field definitions.

## Related

- [ADR 0027](adr/0027-generic-resource-staffing.md) (generic staffing), [ADR 0007](adr/0007-cross-tenant-ordering-customer-portal.md) (portal), [ADR 0010](adr/0010-comment-threads-per-audience.md) (comment threads), [ADR 0011](adr/0011-notifications-from-workflow-after-commit.md) (notifications)
- [Implementation plans](plans/README.md): how the product was built, step by step.
