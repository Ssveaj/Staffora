# ADR 0033: Custom fields have a lifecycle: Draft, Active, Inactive, never deleted

## Status

Accepted. Amends [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md). Amended by [ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md).

## Context

Under [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md) the platform owner makes every custom field, global or for one staffing company. A field without a lifecycle is live in every record's dialog the moment it is saved, cannot be prepared before tenants see it, and can only be taken out of use by deleting it, which is possible only once no tenant holds a value of it. Custom fields must support Save, Edit, Activate and Deactivate through the workflow, and must never be deleted.

## Decision

- A custom field's definition is a state entity with a workflow, like every other record with a lifecycle, shown in a StateEntityDialog of its own ([ADR 0009](0009-state-entity-dialog-only-and-workflow-governed-attributes.md)).
- Its states are **Draft**, **Active** and **Inactive**, with a dedicated **Create** entry state. A new field chooses what it applies to (its definition table, chosen before the dialog opens), its tenant (none for a global field) and its type, and is saved as a draft, which no tenant sees.
- **Activate** puts the field in use. **Deactivate**, after a confirmation, takes it out of use. **Activate** brings it back.
- Its name, its options (relabelled, retired or added; a saved option is never removed), whether it is required and a job field's audience can be saved in every state. What it applies to, its type and its tenant never change after creation.
- A field is never deleted.
- Only an active field reaches the tenants. A draft or inactive field is hidden from every record's dialog and required nowhere, and the values that records hold of an inactive field are kept and return when it is activated again.

## Rationale

A lifecycle lets a field be prepared before anyone sees it and taken out of use without losing a single value, and its steps get what every other record's steps have (history, the access gate, the dialog's footer) instead of a form of their own. This is preferred over a delete guarded by a count of values.

## Consequences

- `CustomFieldState` has Create -1, Draft 0, Active 1 and Inactive 2; Draft is the default, which grants nothing. `CustomFieldWorkflowAttribute<TDefinition>` has one sealed attribute per definition table.
- The steps are `field-create`, `field-save-draft`, `field-activate`, `field-save-active`, `field-deactivate`, `field-save-inactive` and `field-reactivate`, each marked `RequiresPlatformOwner` with no tenant permission. Registration refuses a platform owner's step as a designated save (`IsSave`), so each state's one self-transition save is its edit authority, and the field's dialog opens locked until Edit like every other ([ADR 0031](0031-dialogs-open-locked-until-edit.md)).
- `EntityAttributeDefinitionBase<TEntity>` derives from `StateEntityBase<CustomFieldState>`; the definition tables have `State` and `IsInTransition` beside the `RowVersion` they keep. The transition lock moves the row version, so `WorkflowService` reads it again under the lock for every state entity that has one. `BulkUpdateGuard` lets a definition's lock through (`IsInTransition` alone) and refuses every other bulk statement on a definition.
- A tenant's field is held while the tenant's data is held (`WorkflowService` knows a definition's tenant). Its history is the tenant's, stamped with the tenant's system account; a global field's history is the platform tenant's. `WorkflowService` stamps a create's insert on another tenant's behalf as it stamps a step's save. Only system owners and the platform owner read the history ([ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md)).
- Only active fields are read for a tenant: the lookup cache's catalogue (`GetDefinitionsAsync`), a tenant's Custom fields panel, the assigned staff member's job fields, the fields that `RequiredAttributesCondition` judges, and the data context's required rule. A value of a field that is not active is accepted only as the stored value it is, unchanged (in the services through `CustomFieldValues`, in the data context on every save); a merge never deletes it.
- `CustomFieldService` offers a field's dialog its calls per table (load, step with its edits, new, create, history, editor options). There is no create, update, count or delete outside the workflow.
- The platform settings' "New field" asks what the field applies to, then opens that table's dialog in create mode. The list shows each field's state and filters by it. A tenant's console Settings tab lists the tenant's fields in every state and opens them. A tenant opens its active fields read-only, with no step.
- The development demo and the browser journeys' Company A create their fields through the workflow and activate them.

## Related Decisions

- Amends [ADR 0032](0032-platform-owner-makes-custom-fields-for-a-tenant.md): fields are no longer deleted.
- [ADR 0031](0031-dialogs-open-locked-until-edit.md): the edit authority and the locked dialog.
- [ADR 0036](0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md): the workflow inspector is removed and history is read by owners only.
