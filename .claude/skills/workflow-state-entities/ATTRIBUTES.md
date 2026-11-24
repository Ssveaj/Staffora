# Attributes

Two kinds of attribute drive Staffora's behaviour: **custom fields** defined on entities at runtime, and **declarative C# attributes** that annotate code. Both follow the workflow.

## Custom fields on a state entity

Custom fields are added at runtime without schema changes; their values are governed by workflow field access ([ADR 0009](../../../docs/adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)). The model, the service and the UI are described in [docs/features/custom-fields-and-settings.md](../../../docs/features/custom-fields-and-settings.md#entity-attributes-dynamic-custom-fields) and the tables in [docs/database.md](../../../docs/database.md#entity-attributes). This section holds the rules an implementation must keep.

**Model.**

- Each attribute-enabled entity has a definition / value pair: `{Entity}AttributeDefinition : EntityAttributeDefinitionBase<{Entity}>` and `{Entity}Attribute : EntityAttributeBase<{Entity}, {Entity}AttributeDefinition>` (composite key entity + definition, typed nullable value columns). The non-generic `IEntityAttributeDefinition` and `IEntityAttributeValue` let one field component (`AttributeValueField`) and one set of rules (`AttributeDefinitionRules`, `AttributeValueRules`) serve every entity.
- `AttributeValueType` is stored as an int: append, never renumber or reuse (5, the free JSON type, is retired). A saved field keeps its type, and a choice keeps every saved option with its key (relabel or retire, never remove).

**Scoping and writes.**

- `TenantId == null` is a global definition; a set `TenantId` is the tenant's own. Both are made and changed only by the platform owner on the platform console ([ADR 0032](../../../docs/adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)); a tenant reads them read-only in its Custom fields settings panel (`CustomFieldsPanel`).
- `CustomFieldService` refuses every write of a definition from inside a tenant (`FieldsAreThePlatforms`); the data context writes one only in system scope.
- The fields a dialog renders come from the lookup cache (`CustomFieldService.GetDefinitionsAsync`), which every save and step of a field invalidates.
- Every save path that writes values runs `AttributeValueRules` (the services through `CustomFieldValues`, the data context again, a retired option judged against the stored row), so a choice outside its options never reaches the database.
- A cleared field keeps no row (the services delete it, the data context refuses an empty one), and a value goes with its owner, never with its field.

**A field's own lifecycle** ([ADR 0033](../../../docs/adr/0033-custom-fields-have-a-lifecycle.md)).

- A definition is itself a state entity (`IEntityAttributeDefinition : IStateEntity<CustomFieldState>`, `EntityAttributeDefinitionBase<TEntity> : StateEntityBase<CustomFieldState>`) with one workflow for every table (`CustomFieldWorkflowAttribute<TDefinition>`, a sealed attribute per table: `UserFieldWorkflowAttribute`, `UnitFieldWorkflowAttribute`, `JobFieldWorkflowAttribute`, and the test tables' own): Create (-1) to Draft (0, the safe default), Draft to Active, Active to Inactive and back, a save in Draft, Active and Inactive. Every step is a platform owner's and none leads into a delete state, so a field is never deleted. It is shown in a StateEntityDialog of its own ([DIALOGS.md](DIALOGS.md)).
- **Only an Active field applies to a record**: the catalogue the dialogs read, the fields `RequiredAttributesCondition` judges, the data context's required rule and the assigned staff member's job fields are the active ones.
- A value of a draft or inactive field is never added or changed (`AttributeValueRules.FieldNotInUse`, judged by `CustomFieldValues` and by `SaveChanges`). The values an inactive field holds are kept untouched and return when it is activated again.
- A new definition table gives its definition the sealed workflow attribute too (with the required mark, `RequiredMarkAccess`, where its entity's steps need required fields).

**Workflow governance.**

1. The workflow attribute declares `public const string AreaAttributes = WorkflowAreas.Attributes` and configures it in **every** state with the value collection in it (`.Area(AreaAttributes, flags, a => a.Field(e => e.Attributes))`): typically `ReadWrite` while the entity is being prepared, `ReadOnly` once it is confirmed or closed, `None` where the fields make no sense yet. Registration refuses a state that leaves the collection out, gives it an access of its own, or makes the area `Mandatory` (`CustomFieldWorkflowTests`).
2. The dialog renders values only through `StateCustomFields`: one `AttributeValueField` per field that applies, inside `CustomFieldsFieldset` (the tenant's own fields, then the global ones), every field following the `Attributes` area in the persisted state. The fields come with the dialog's first load through the context's `LoadCustomFieldsAsync` → the service's `Get{Entity}CustomFieldsAsync` → `CustomFieldService.GetDefinitionsAsync`; a context of an entity with custom fields that does not override it cannot open. The entity's collection holds exactly the values that have something: a field's row joins the collection once filled and leaves it once emptied, so the updater (declared with `WithCollection(e => e.Attributes)`) inserts, changes or deletes the stored rows as the user edits.
3. The server refuses an added, changed or removed value while the persisted state's `Attributes` area is not editable, whoever writes it: the updater first ("Custom fields cannot be changed while the {Entity} is {state}."), then `SaveChanges` for every write (the collection is a declared collection). A value inserted with its owner is judged by the state the owner is inserted in. An unchanged value passes.
4. Whether a field is **required** lives on the field: `IEntityAttributeDefinition.IsRequired`, set in the field's dialog (never on a yes-or-no field, which always has an answer; offered only for an entity whose workflow has a step that needs it, `CustomFieldTargets.CanBeRequired`), and it counts only while the field is Active. It is not field access: the workflow puts `RequiredAttributesCondition<TState, TEntity, TValue>(e => e.Attributes)` on the steps that need the fields, which blocks them while a required field has no value, naming the missing fields ("Grade and Room are required"). The dialog judges it in memory with the fields it loaded and the server again in the step's transaction, on the stored fields and values (`WorkflowService` reads both when the caller's graph left the values out). A value counts when it fills its field (`AttributeValues.Fills`): a choice whose key the field no longer has does not, one holding a retired option still does.

**Required fields: two meanings, by state.** The steps carrying `RequiredAttributesCondition` decide what `IsRequired` means in each state (`Workflow.StepsRequiringCustomFieldsFrom`, `IWorkflow.RequiresCustomFieldsIn`):

| State | Meaning | Dialog | Server |
|---|---|---|---|
| A state such a step leaves (a draft) | Needed before the step: the step stays blocked until the field is filled; a draft may be incomplete | Not marked required (no mark, no `aria-required`); a line "Needed before {step labels}" under the field describes its input (`aria-describedby`) | A save may leave it empty; the step's condition refuses the step |
| A state such a step leads to (active, confirmed) | Required: the record has passed the requirement and keeps it | Marked required (a mark after the label, `aria-required`) while the area is editable | A save that empties it is refused on every path, naming the field (`EmptiedCustomFields`); filling a missing one repairs the record |
| Any other state | Nothing | An ordinary field | Nothing |

A state that is both is the second. While the area is locked or hidden nothing is said: the greyed step's card names what it needs.

**Audience.** Job attribute definitions carry an audience (`JobAttributeDefinition.Audience`: the coordinators only, or also the assigned staff member). The job's own dialog shows every field. The staff member reads, in their own assignment's dialog only and only while assigned, the fields of their audience, read-only, through `SlotAssignmentService.GetSlotAssignmentEditorOptionsAsync`, which asks the internal `CustomFieldService.ReadAssignedStaffJobFieldsAsync`: one query reading fields and values together, with the audience, the reader's own assignment and its Assigned state judged in the database, never from the lookup cache. No graph a staff member loads holds a job's values. A new audience-bound read follows the same rule: judge the audience in the query that reads the values, so no value of another field is read and a narrowed audience takes effect at once on every app instance.

**Adding custom fields to an entity.**

1. The core pair under `Staffora.Core/Entities/EntityAttributes/{Area}Attributes/` and an `Attributes` navigation.
2. Both EF configurations, the DbSets and a migration.
3. `AreaAttributes` in every state with the collection in it.
4. The definition table's sealed workflow attribute and its field dialog.
5. `StateCustomFields` in the dialog and `LoadCustomFieldsAsync` on its context.
6. `WithCollection(e => e.Attributes)` on the updater and `Attributes` in the workflow graph.
7. Validation in the save path.
8. `RequiredAttributesCondition` on the forward steps that need required fields.

Done when a platform owner adds a field for a tenant and its editability follows the entity's state.

## Declarative C# attributes

| Attribute | Target | Read by | Purpose | Priority |
|---|---|---|---|---|
| `{Entity}WorkflowAttribute` | entity class | `GetWorkflow`, save-time field enforcement | States, transitions, field access | must |
| `[Display]`, `[Description]` | enum member | `EnumDisplayHelper`, header, footer | Labels and help text | must |
| `[Icon]` | enum member | `GetIcon` → footer fallback icon, header, history | Icon | must |
| `[StateStyling]` | enum member | `GetStateColor` → dialog accent, footer, state strip | State colour | must |
| `[EntityStateCategory]` | enum member | `GetStateCategory` → grouping, open-work lists, filters | Generic state classification: `Entry`, `Open`, `Done`, `Canceled`, `Archived` | must |
| `[EntryState]` | enum member | `IsEntryState` (or a member named `Create`) → footer, dialog context | Transitions offered before the first save | must |
| `[DeleteState]` | enum member | `IsDeleteState` → `WorkflowService` hard delete, dialog closes | Hard delete | must |
| `[PermissionArea]` | `RolePermission` member | `PermissionService`, role editor | Groups permissions | must |
| `[CustomerPortalPage]` | page class | portal page allow-list | Pages a portal user may open | must |
| `[ButtonStyle]` | enum member | `GetButtonStyle` → style of a button into that state | Button style | should |
| `[DialogRoute]` | dialog context | `DialogRouteRegistry` | Deep links to dialogs | must |
| `[DialogViewOnPage]` | page class | `StateEntityDialogRestoreHost` | The view a page's dialogs reopen on | should |

**Adding one.**

- Domain-neutral annotations go in `Staffora.Core/Annotations/` with an explicit `[AttributeUsage]`.
- One reader method in `Staffora.Core/CoreExtensions.cs`, built on its private `GetFieldAttribute<T>` helper, with a fallback default declared as a `Fallback…` constant (`GetIcon` falls back to a generic icon, `GetStateCategory` to `Open`).
- Page and context attributes live next to their reader.
- Every attribute read by scanning the assembly gets a scan test (`CustomerPortalPageScanTests` for `[CustomerPortalPage]`; `StateAnnotationScanTests` checks that each state enum member of every declared workflow carries the five required annotations and that every transition has a `Description`).
- Add a row to the table above saying what reads the attribute and whether it is descriptive or enforced (source files carry no comments).
