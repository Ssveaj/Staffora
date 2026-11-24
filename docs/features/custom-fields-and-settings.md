# Custom fields and tenant settings

This document describes the tenant-level configuration the application reads at runtime: custom fields (entity attributes) that the platform owner defines and tenants fill in, the in-process lookup cache that serves tenant catalogues, and the typed tenant settings with their settings shell. Permissions are defined in [authorization.md](../authorization.md); tenant isolation rules are in [tenant-isolation.md](../tenant-isolation.md).

## Entity attributes (dynamic custom fields)

Custom fields are added to an entity at runtime, without schema changes. The platform owner makes every field, global or for one tenant ([ADR 0032](../adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)); a tenant reads its fields and fills in their values.

Every field is a state entity with a lifecycle (Draft, Active, Inactive), shown in a state-entity dialog of its own and never deleted ([ADR 0033](../adr/0033-custom-fields-have-a-lifecycle.md)). Only an active field reaches a tenant.

Custom fields are implemented for `ApplicationUser`, `OrganizationUnit` and `Job`. Job fields additionally have an audience ([Job fields](#job-fields)).

### Building blocks

```text
Staffora.Core/Entities/EntityAttributes/
├── IEntityAttributeDefinition, EntityAttributeDefinitionBase<TEntity>  a field, a state entity (StateEntityBase): Name, ValueType, TenantId, Options, audit columns
├── CustomFieldState                   Create (-1), Draft (0, the default), Active, Inactive
├── IEntityAttributeValue, EntityAttributeBase<TEntity, TDefinition>   one value of one entity: the typed columns
├── AttributeValueType                 Integer, Number, Date, Text, Boolean, Choice (5, the free JSON type, is retired)
├── AttributeChoiceOption              a choice's option: a stable Key, its Label, IsRetired; the key rules
├── AttributeDefinitionRules           the rules a field is saved by
├── AttributeValueRules, AttributeValues  the rules a value is saved by; copying, comparing, emptiness
├── CustomFieldTarget                  the entities fields apply to (User, OrganizationUnit, Job), not stored
├── UserAttributes/                    ApplicationUserAttributeDefinition, ApplicationUserAttribute
├── OrganizationAttributes/            OrganizationUnitAttributeDefinition, OrganizationUnitAttribute
└── JobAttributes/                     JobAttributeDefinition (with its Audience), JobAttribute, JobFieldAudience

Staffora.Core/Workflow/WorkflowAttributes/CustomFieldWorkflowAttribute  the lifecycle every field has; one sealed attribute per table (User, Unit, Job)
Staffora.Core/Lists/CustomFieldList      the Custom fields panel's list definitions
Staffora.Core/Models/CustomFieldRow      one row of that list

Staffora.Data/Configurations/EntityAttributes/
├── ConfigureAttributeDefinition<TEntity, TDefinition>()          tenant FK, unique (TenantId, Name), audit FKs, Options as JSON, rowversion
├── ConfigureEntityAttribute<TEntity, TDefinition, TAttribute>()  composite key, StringValue (4000), DateValue (date), NoAction to the field
└── ChoiceOptionsConversion                                       the Options column's converter and comparer
Staffora.Data/ApplicationDbContext.CustomFields.cs                  stamping, and the field and value rules in SaveChanges

Staffora.Services/CustomFields/
├── CustomFieldService                 the list; a field's dialog (CustomFieldService.Dialogs.cs); a record dialog's fields
├── CustomFieldWorkflowQueries         one workflow graph per definition table (the field's row alone)
├── CustomFieldDefinitionRow           one field as the lookup cache keeps it
├── CustomFieldTargets                 a target's definition table and name; whether its fields can be required
└── CustomFieldValues                  the value check and merge the user, unit and job services run when they save

Staffora.Blazor/Components/EntityAttributes/
├── AttributeValueField                the one input of a value, for every type
├── CustomFieldsFieldset               a dialog's "Custom fields" fieldset: the own fields, then the global ones
└── CustomFieldsPanel                  the list of fields with "New field", hosted by a page

Staffora.Blazor/Components/Common/Dialogs/
├── CustomFieldDialog<TDefinition, TContext>, CustomFieldDialogContext<TDefinition>  a field's state-entity dialog, shared
├── UserFieldDialog, UnitFieldDialog, JobFieldDialog (+ contexts)  one closed body and context per table, tokens userfield, unitfield, jobfield
└── ChooseCustomFieldTargetDialog      "New Field"'s question: what the field applies to
Staffora.Blazor/Components/Common/WorkflowFields/StateChoiceOptions  a choice's options, edited one by one as the field's workflow allows
```

### Fields

**Scope.** A field (definition) is either global or one tenant's own:

- **Global** (`TenantId` null): the platform's, the same in every tenant.
- **Tenant** (its `TenantId`): its values are the tenant's data.

Every field is the platform's to make and change ([ADR 0032](../adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)). Fields are written under the platform lease, and a tenant's field is stamped with that tenant's system account. The data context refuses any write of a field outside system scope. A tenant reads its active fields read-only in its panel and fills them in its dialogs.

**Lifecycle** ([ADR 0033](../adr/0033-custom-fields-have-a-lifecycle.md)). A field is a state entity (`IEntityAttributeDefinition : IStateEntity<CustomFieldState>`, `EntityAttributeDefinitionBase<TEntity> : StateEntityBase<CustomFieldState>`). Its workflow (`CustomFieldWorkflowAttribute<TDefinition>`, one sealed attribute per table) has four states:

| State | Meaning |
|---|---|
| Create (-1) | The dialog's entry state, never stored |
| Draft (0, default) | Saved, still being prepared, seen by no tenant |
| Active | In use |
| Inactive | Out of use (category Archived) |

Every step is a platform owner's (`RequiresPlatformOwner`, no tenant permission):

- `field-create` (Create to Draft), `field-save-draft`
- `field-activate` (Draft to Active), `field-save-active`
- `field-deactivate` (Active to Inactive, after a confirmation that the values are kept), `field-save-inactive`
- `field-reactivate` (Inactive to Active)

There is no delete: a field nobody should fill any more is deactivated.

- While new, a field chooses its tenant and its type; once saved both are read-only.
- Its name (required), its options, its required mark (a job field's only, where the table's workflow shows it) and a job field's audience change in every state.
- Every step out of a state judges a name, a choice's options (at least one not retired, a label on each) and no required yes-or-no field, so the dialog offers a step as the form is filled in.
- A platform owner's step cannot be a designated save (registration refuses one), so the dialog opens with its fields open; each state's one save is its edit authority.

**Only an active field reaches a tenant.** The lookup cache's catalogue, `GetDefinitionsAsync`, a tenant's list, the assigned staff member's job fields, and the fields `RequiredAttributesCondition` and the data context's required rule read are the active ones. A draft or an inactive field is in no record's dialog and required nowhere. The values records hold of an inactive field are kept untouched and return when it is activated again. A value of a field that is not active is accepted only as the stored value, unchanged (`AttributeValueRules.FieldNotInUse`, by the services and by `SaveChanges`).

**Unique names.** A name is unique among the tenant's own fields of one entity and among the global fields of that entity (a unique index on `(TenantId, Name)`, which SQL Server holds for the null tenant too). A tenant's field never takes a global field's name, nor a global field a name a tenant's field of that entity has, since both show side by side in the tenant's dialogs. The service judges that rule across the two scopes with the database's collation (case-insensitive); a race between two saves in one scope ends at the index with the same kind of answer.

**Required** (`IsRequired`). A field marked required must have a value before its entity takes a step its workflow guards with `RequiredAttributesCondition` (a forward step, such as an activation). The step is greyed or hidden with "{Field} is required" until the field is filled, judged in memory in the dialog and again by the server in the step's transaction. Its meaning depends on the state (the workflow skill's [ATTRIBUTES.md](../../.claude/skills/workflow-state-entities/ATTRIBUTES.md), "Required fields"):

- In a state such a step leaves, a save may leave the field empty (a draft may be incomplete), and the dialog says "Needed before {step}" under it rather than marking it required.
- In a state such a step leads to, it is required: marked and `aria-required`, and the data context refuses a save that empties it, naming it.
- A choice counts as filled with a key its field has (a retired option included), never with an unknown one.
- A yes-or-no field is never required (an unticked box reads as no; `AttributeDefinitionRules.RequiredYesOrNo`).
- Users and units have no workflow step that needs it, so they do not read it and their editor offers no Required box. A job's does: handing it over, starting and confirming it need the job's required fields, so a field is needed before those steps in registration and required while the job waits for coordination, is coordinated or confirmed. In progress (reached by the job clock's step) nothing is said of it.

**Type** (`AttributeValueType`) decides the one value column its values use. A saved field keeps its type.

| Type | Column | Notes |
|---|---|---|
| `Integer` | `Int32Value` | A whole number |
| `Number` | `DoubleValue` | Finite |
| `Date` | `DateValue` | A calendar day ([ADR 0020](../adr/0020-dates-and-times.md)) |
| `Text` | `StringValue` | At most 4000 characters |
| `Boolean` | `BooleanValue` | Yes or no |
| `Choice` | `StringValue` | Holds an option's key |

The free JSON type (5) is retired: nobody can type valid JSON into a form field and nothing read it, so there is no JSON input anywhere and the number stays unused.

**Choice options.** A choice has its options in order (`Options`, one JSON column written only by `ChoiceOptionsConversion`; the editor edits the options one by one).

- Each option has a stable key, made once by the service from the label the option was first saved with and never changed. A value stores the key, not the label.
- The key spells the label in lowercase ASCII (`Year 7 to 9` becomes `year-7-to-9`): accents are dropped (`Café`, `cafe`), a ligature is written out by its compatibility decomposition (`Ǆ`, `dz`), letters no decomposition takes apart are spelled out (`Straße`, `strasse`; `æ`, `ae`), a label with nothing left (`!!!`, or a script without an ASCII spelling) becomes `option`, and `-2`, `-3` are appended while the field already holds that key.
- Only the service gives keys: an option sent with a key the stored field does not hold (any key on a new field) gets a new one from its label, so a client never chooses a key.
- A saved option is relabelled or retired, never removed. A retired option is no longer offered for a new choice; a value that holds it keeps it and shows its label marked "(retired)"; it can be restored. A stored key therefore always means the option it was chosen as, and no key is reused for another option.
- A choice needs at least one option that is not retired and holds at most 50, each label (at most 100 characters) unique ignoring case.

**Rules.** `AttributeDefinitionRules` (a name of 1 to 100 characters, a type the field may take and keeps, options only on a choice, every saved option kept with its key) runs in the service before it writes and again in `SaveChanges` for every added or changed field, judged against the stored row, so a field written past the service is held to them too (`CustomFieldException`).

**Audit columns.** A field carries `Created`, `CreatedById`, `Modified`, `ModifiedById` and `ImpersonatedById`, stamped in `SaveChanges` as a tenant row's are. In system scope, where every field is written, a caller-set creator or a named editor is kept and must be a user of the field's tenant (its system account, `StampOnOtherTenantsBehalf`), or of the platform tenant for a global field.

**Concurrency.** A field's `RowVersion` (SQL Server `rowversion`) is its concurrency token beside `State` and `IsInTransition`. The dialog's updater carries the version the field was opened with, so a step with edits over a field someone else saved since is refused ("Someone else changed this field after you opened it. …", `CustomFieldService.ChangedMeanwhile`) instead of last-save-wins; opened again, the dialog saves. The step's own transition lock moves the row version too, so `WorkflowService` reads it again under the lock before the step's save (`WorkflowService.Locks.cs`, for every state entity with a row version).

### Values

- **Storage.** A value is one row per entity and field, keyed by `(EntityId, EntityAttributeDefinitionId)`. The non-generic `IEntityAttributeValue` lets one input component and one set of rules serve every entity.
- **Empty values.** A text of white space alone is no value (`AttributeValues.Normalize`). A value with every column empty clears its field, and a cleared field keeps no row: the services store a new value only when it holds something and delete the row of a held value sent empty (`CustomFieldValues.Merge`). `SaveChanges` refuses a row that holds nothing ("A custom field value that holds nothing is not stored; …", `AttributeValueRules.EmptyRow`), so the services are the one place that clears a value and the database never holds an empty one.
- **Validation on every save path.** `AttributeValueRules` judges every value: only the column of its field's type holds something, text within its length, a finite number, a choice among its field's option keys (never a label, never in another case), and a retired option only where the value held it already.
  - `UserService.CreateUserAsync` and `UpdateUserAsync` and `OrganizationService.CreateUnitAsync` and `UpdateUnitAsync` run it through `CustomFieldValues` against the fields that apply to the entity's tenant (the global ones and the tenant's own, of the entity's own definition table). A value of another tenant's field or of another entity's field is refused as "a field that does not apply here"; a user is refused before Identity creates them.
  - `SaveChanges` runs the same rules on every added or changed value row (`ApplicationDbContext.CustomFields.cs`), the retired option included: an added row never takes one, and a changed row keeps one only when the row the database holds is that option (read for such a row alone, whatever originals the caller attached).
- **Deleting.** A value is owned by the row it describes, not by its field. Deleting the owning record cascades to its values, while the field's foreign key is `NoAction`, so the database never deletes values with their field and no cascade crosses the tenant boundary. A field is never deleted ([ADR 0033](../adr/0033-custom-fields-have-a-lifecycle.md)): its workflow has no delete state, and the data context refuses the delete of a state entity outside its workflow in every scope, so tenants' values always keep their field.
- **Tenant isolation.** Fields and values are listed exceptions to the tenant filter ([tenant-isolation.md](../tenant-isolation.md)): a tenant reads the global fields and its own and writes none; values are read and written only through their owner. `ExecuteUpdate` and `ExecuteDelete` of a field or a value are refused in every scope (`BulkUpdateGuard`), since a tenant's filter shows the global fields too and only `SaveChanges` judges them. The one exception is a step's transition lock, an update of definitions that sets `IsInTransition` alone.

### CustomFieldService

Every method returns a `Result` and checks its permission first ([authorization.md](../authorization.md)):

- `SettingsView` to read the list and open a field's dialog.
- `SettingsEdit` with platform reach to create a field or take a step.
- For a record dialog's fields, `SettingsView` or any permission of the entity's area.

Every write belongs to the platform console ([ADR 0032](../adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)): the platform owner with platform reach makes and steps the global fields and the fields of a chosen staffing company, under the platform lease. A company's field is stamped with its system account and never written while the company's data is held. Inside a tenant, an impersonating platform owner included, every write is refused ("Custom fields are set up by the platform, so they cannot be added or changed here.", `FieldsAreThePlatforms`).

| Method | Returns |
|---|---|
| `QueryDefinitionsAsync(ListQuery)` | `Result<ListPage<CustomFieldRow>>`. In a tenant: the active global fields and the tenant's own active ones of every entity, read-only. On the console: every field in every state, the global ones and every tenant's with its tenant's name, under the platform lease (`CustomFieldList.PlatformDefinition`). Searched by name (on the console by tenant name too), filtered by entity, by scope (own or global) and on the console by state, grouped by entity (on the console by tenant by default, global fields first), sorted by name or type, cut in SQL over the tables at once (`UNION ALL`). Each row carries its state and whether the caller may change it |
| `QueryTenantDefinitionsAsync(tenantId, ListQuery)` | The same for a tenant's console Custom fields tab (`CustomFieldList.ConsoleDefinition`): the tenant's fields beside the global ones in every state (platform reach) |
| `Load{Table}UpdaterAsync(id)` | `Result<DetachedEntityUpdater<{Table}>>`: the field for its dialog ([ADR 0015](../adr/0015-dialogs-save-through-a-detached-entity-updater.md), [ADR 0017](../adr/0017-dialogs-load-through-one-service-call.md)), read through the table's workflow graph. The console reads any field; anyone else with `SettingsView` an active field of their tenant or an active global one |
| `Update{Table}AndTransitionAsync(updater, key, comment)` | `Result`: the dialog's edits judged against the stored field (the name trimmed and free in its scope, each new option given its key, the rules, a tenant whose data is not held), then the edits and the step in one transaction (`WorkflowService.SaveAndTransitionAsync`). The cache entries the field is in are dropped once it returns |
| `New{Table}Async()`, `Create{Table}Async(field, key)` | A new text field in the entry state for the dialog's create mode; the create: a row of its own from the dialog's name, type, tenant (none: global; else a staffing company whose data is not held), options (keys given here), required mark (only where it can be required) and a job field's audience, with its first step (`WorkflowService.CreateAndTransitionAsync`) |
| `Get{Table}HistoryAsync(id, query)` | The field's history, for those who may open its dialog and read histories (an active system owner, or a platform owner on the console, [ADR 0036](../adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md)); a global field's history only on the console (`GlobalHistoryIsThePlatforms`) |
| `Get{Table}EditorOptionsAsync(id)` | `Result<CustomFieldEditorOptions>`: the tenants the Tenant drop-down offers (on the console the staffing companies in Setup or Active, and a saved field's own tenant; elsewhere the reader's own) and whether every field is read-only (`FieldsLocked`, for anyone but the console) |
| `ReadAssignedStaffJobFieldsAsync(assignmentId)` (internal) | `Result<IReadOnlyList<AssignedStaffJobField>>`: behind `ViewOwnAssignments`, and only for the session's own assignment while it is Assigned (anything else reads nothing), the active job fields whose audience includes the assigned staff member with their values. Read in one query that judges the audience and the state in the database, never from the lookup cache. The assignment service's editor options are its one caller |
| `GetDefinitionsAsync<TDefinition>(tenantId)` | `Result<List<TDefinition>>`: the fields a record's dialog renders, the active global ones and the tenant's active ones, ordered by name, read through the [lookup cache](#lookup-cache) and handed out as definitions of the call's own. Another tenant's only with platform reach (the console, which opens a tenant's user read-only); refused for anyone else ("Custom fields are read only in your own tenant.", `OtherTenantsFields`) before the cache is asked |

`{Table}` is the definition table's entity name (`ApplicationUserAttributeDefinition`, `OrganizationUnitAttributeDefinition`, `JobAttributeDefinition`), as the dialog scans name every dialog's calls; each hands the work to one shared method with its table's graph.

A refusal comes back as a failed `Result`: a broken rule with its reason, a name taken with the rule's words, a tenant check with "This field cannot be changed here.", and a field opened under one id and sent with another as not found.

### UI

**Value inputs.** `AttributeValueField` (`Definition`, `Value`, `NamePrefix`, `Disabled`, `Changed`) renders the input of its field's type:

- a text box; a numeric box (whole number or number); a date picker over `DateOnly`; a check box beside its label;
- for a choice, a drop-down of its options that are not retired, by label, storing the key, with a retired or unknown option a value holds shown marked;
- for a type that takes no values, a line saying so and no input.

Each input is named by its field: the form field's `Component` matches the input's `Name` (`<NamePrefix>-<field id>`); the drop-down's hidden input through `FieldLabel.Of`; the check box with an `aria-label` and `FieldLabel.CheckBoxInput`. A drop-down's combobox is named by the field (`aria-label`) as well as its hidden input.

**Record dialogs.** `UserDialogComponent` and `OrganizationUnitDialog` load the fields that apply through `GetDefinitionsAsync`, from the lookup cache. The user and the unit are loaded with their values but without their fields, so opening a dialog again reads no definitions. The dialog adds an empty value row for each field the entity has none for and renders each field through `AttributeValueField` alone, inside `CustomFieldsFieldset`:

- The fields are cells of one `.field-row` grid after the dialog's own fields (two columns once the dialog's body is 40 rem wide, one under that).
- There is no "Custom fields" legend and no "Global fields" heading, since the reader need not know who set a field up. The tenant's own fields come first and the global ones after them.
- A dialog may hand in one field of its own as the grid's first cell (`Leading`, the user dialog's Phone), so it shares a row with the first custom field.
- A read-only dialog passes `Disabled`.

**In a state-entity dialog** the field is placed by `StateCustomFields` and follows the workflow's `Attributes` area in the persisted state, read from the dialog's context:

- not rendered while the area is hidden;
- read-only while it is locked: the input's `readonly`, still focusable; a drop-down's combobox says `aria-readonly`; a yes-or-no field reads as "Yes" or "No" in a read-only box, since Radzen's read-only check box leaves the tab order;
- while editable, a required field (`IsRequired`) is marked required (a mark after its label, `aria-required`) in a state a step needing it leads to, and says "Needed before {step}" under it (the line describes the input) in a state such a step leaves (`StateEntityDialogContext.RequirementOf`).

A read-only field takes no edit, whatever event reaches it.

**The Custom fields panel.** `CustomFieldsPanel` is the list of fields on the list surface ([List view](lists-and-dialogs.md#list-view)): name, type and the entity a field applies to (the entity column is left out while the list is grouped by it, its default, through `ListColumn.HiddenWhenGroupedBy`).

- **In a tenant:** its scope ("Own" or "Global") in the state strip, every field listed active and read-only, and no "New field".
- **On the console:** its state (Draft in the info tone, Active in the success tone, Inactive in the neutral tone) in the strip, with a Tenant column, or a Scope column on a tenant's tab.
- A row opens the field's state-entity dialog through its table's id-only opener (`UserFieldDialog.OpenAsync`, `UnitFieldDialog.OpenAsync`, `JobFieldDialog.OpenAsync`), which puts `userfield-<id>`, `unitfield-<id>` or `jobfield-<id>` on the URL's dialog stack, so F5 and a link reopen it. The list reads its page again when the dialog closes.
- "New field" asks what the field applies to (`ChooseCustomFieldTargetDialog`, a form dialog) and opens that table's dialog in create mode.

Hosts of the panel:

- The tenant's settings shell (`Pages/TenantAdmin/Settings.razor`), its Custom fields panel at `/tenant/management/settings/custom-fields` ([Tenant settings](#tenant-settings)).
- The platform console's settings (`Pages/SuperAdmin/PlatformSettings.razor`, `/superadmin/settings`, "Platform settings" under Platform). The page offers "New Field" in its header, and the panel is its list alone: every field, grouped by tenant, with a Tenant column when grouped by entity (`CustomFieldList.PlatformDefinition`); every row opens to change ([ADR 0032](../adr/0032-platform-owner-makes-custom-fields-for-a-tenant.md)).
- A tenant's console Custom fields tab, with the tenant's id (`TenantId`): the tenant's own fields beside the global ones in every state, through `CustomFieldService.QueryTenantDefinitionsAsync` (platform reach, then the platform lease), each row opening the field's dialog.

The shell passes `AddressPrefix` "fields", so the list's keys are `fields.q`, `fields.entity` and so on, and never meet those of another panel's list in the address.

**The field's dialog** ([ADR 0033](../adr/0033-custom-fields-have-a-lifecycle.md)) is a StateEntityDialog built by the recipe in the workflow skill's [DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md): one shared body (`CustomFieldDialog<TDefinition, TContext>`) and context base (`CustomFieldDialogContext<TDefinition>`), and per table a closed body (`UserFieldDialog`, `UnitFieldDialog`, `JobFieldDialog`) with its id-only opener and create opener, and a context with its `[DialogRoute]` token and one call per part to `CustomFieldService`.

- **Fields:** the **Tenant** (a drop-down of the editor options' tenants; none chosen means global), the name, the **Type**, a choice's **Options**, a job field's **Read by** and, where the table's workflow shows it, **Required field**. Each is shown, locked and marked by the field's workflow for the persisted state through the `StateField` components.
- **Options** (`StateChoiceOptions`): a label each, "Add option", a saved option retired or restored by its icon button ("Retire option 2"), an option not saved yet removed again.
- **Footer:** the lifecycle (Create, Save, Activate, Deactivate with its confirmation, Activate again). The header holds the state, the history and the workflow.
- For anyone but the console the dialog is read-only (`IsLockedRecord` from the options' `FieldsLocked`), with a note that the platform sets custom fields up, and its footer offers no step.

Verified by: `AttributeFieldScanTests` (no other markup reads or writes a value's columns, and no dialog switches on a field's type), `CustomFieldDialogsTests`, `CustomFieldWorkflowTests`, `CustomFieldLifecycleTests`, `tests/e2e/custom-fields.spec.ts`.

### Job fields

A job field (`JobAttributeDefinition`, in the Custom fields panel under "Jobs", its dialog's own "Read by" field) has an **audience** (`JobFieldAudience`):

- **Coordinators only** (the default): the job staff, every reader of jobs.
- **Also the assigned staff member:** a staff member whose assignment to one of the job's slots is Assigned.

The audience is a disclosure rule, so it waits for the coordinator's pick: a staff member who is only requested or who has said yes reads none, while coverage counts a yes as held for planning. Once the assignment is no longer Assigned (canceled, or the job is over), the staff member reads none again.

- **Job dialog.** Shows every field (`StateCustomFields`, the workflow's custom fields area): editable until the job is completed or canceled, read-only after.
- **Assignment dialog.** Shows the job's fields the reader may read, read-only, as "Job details" (`SlotAssignmentService.GetSlotAssignmentEditorOptionsAsync`, its editor options, read with every load): every field with its audience for the job staff, and for the assignment's own staff member only those of their audience, given in words (`AttributeValues.DisplayText`).
- **One decision point.** The staff member's fields and their values are read by one query that judges both the audience and the assignment being the reader's own and Assigned in the database (`CustomFieldService.ReadAssignedStaffJobFieldsAsync`, internal). It never reads from the lookup cache, so a field narrowed to the coordinators on another instance of the app is not read again on the next read, whatever this instance's cache still holds for its ten minutes. No value of a coordinators-only field is read into memory, and a staff member merely asked, or another staff member, reads nothing through it.
- **No other path.** A coordinators-only field never reaches a staff member: the assignment's workflow graph holds no custom field, the job's reads and the field lists refuse a staff member (`SettingsView` or a permission of the Jobs or Coordination area reads job fields), and no notice or history names one.

Verified by: `JobCustomFieldTests` (including the audience narrowed by another host with a lookup cache of its own).

### Adding attributes to an entity type

1. **Core.** Add `{Entity}AttributeDefinition : EntityAttributeDefinitionBase<{Entity}>` and `{Entity}Attribute : EntityAttributeBase<{Entity}, {Entity}AttributeDefinition>` under `Staffora.Core/Entities/EntityAttributes/{Area}Attributes/`, an `ICollection<{Entity}Attribute> Attributes` navigation on the entity, and a `CustomFieldTarget` member.
2. **Data.** Add both configurations in the entity's domain folder under `Staffora.Data/Configurations/`: call `ConfigureAttributeDefinition` and `ConfigureEntityAttribute`, then map `HasOne(a => a.Entity).WithMany(e => e.Attributes)` with cascade delete (the field's key stays `NoAction`). Register both `DbSet`s in `ApplicationDbContext` and add a migration.
3. **Service.** Add the target to `CustomFieldService` (the list's `UNION ALL`, and the field dialog's calls with the table's workflow graph in `CustomFieldWorkflowQueries`) and to `CustomFieldTargets`. Give the definition its sealed `CustomFieldWorkflowAttribute<TDefinition>` (with the required mark where the entity's steps need required fields), and add its closed dialog body and context. Include `Attributes` when the entity's service loads it, normalize the sent values (`CustomFieldValues.Normalized`), run `CustomFieldValues.DescribeInvalidAsync` before it saves the collection, and merge them with `CustomFieldValues.Merge`.
4. **UI.** Render each field in the entity's dialog through `AttributeValueField`, and add the dialog to `AttributeFieldScanTests`. For a state entity: its dialog renders them through `StateCustomFields` and its context loads them (`LoadCustomFieldsAsync`), its updater declares the collection (`WithCollection(e => e.Attributes)`), its workflow graph includes it, every state of its workflow puts it in the `Attributes` area, and the forward steps that need the required fields carry `RequiredAttributesCondition`.

## Lookup cache

`TenantLookupCache` (`Staffora.Services/Caching/`) keeps tenant catalogues in memory. A catalogue is read once per tenant and served to every circuit of that tenant until the service that owns it saves a change.

| Catalogue | Owner | Rows | Invalidated |
|---|---|---|---|
| Custom field definitions (one per definition table) | `CustomFieldService.DefinitionCatalogue<TDefinition>.Instance` | `CustomFieldDefinitionRow` | After every create, change and step of a field |
| Tenant settings | `TenantSettingsService.Catalogue` | `TenantSettingRow` | After every save of a setting |
| Qualification catalogue | `QualificationService.Catalogue` | `QualificationCatalogueRow` | After every step of a qualification, its create included |
| Service catalogue | `ServiceCatalogueService.Catalogue` | `ServiceCatalogueRow` (with the qualifications each names at their levels) | After every create and step |
| A portal's copies of its staffing company's services | `OrderService.OfferedCatalogue` | | By the integration handlers once a delivery has committed |
| Closed days | `ClosedDayService.Catalogue` | `ClosedDayRow` (with the unit's name) | After every create, change and delete ([Closed days](schedules-and-absence.md#closed-days)) |

- **One cache per process.** A singleton over a `MemoryCache` of its own, shared by every circuit, registered by `AddTenantSettings` in `Program.cs`. It is in-process: a second instance of the app does not hear about another's saves and sees them within the time-to-live.
- **Catalogues.** A `LookupCatalogue<TRow>` is declared once, as a static field of its owning service, with a name and a load that reads one tenant's rows. The catalogue object is the key, so two catalogues never share entries. Its rows implement `ILookupRow` (they name their tenant, or null for a global row) and are immutable records with read-only lists.
- **Keyed by tenant, filled by key.** An entry is one catalogue of one tenant, global rows included, so a dialog's fields cost one query (one entry per tenant and table, not a global entry beside a tenant one). The cache reads each entry on a system context under a lease of its own, with a query that names the tenant, so what an entry holds depends on its key alone, never on the circuit that filled it: the platform console naming a tenant, an impersonating platform owner and the tenant's own user all fill it with the same rows. A load that read a row of another tenant fails and caches nothing.
- **Access is the service's.** The owning service checks the caller's permission through `PermissionService` and the tenant the caller may read on every call, before it asks the cache, so a warm entry never answers a call the service would refuse.
  - `GetDefinitionsAsync` reads the runtime's tenant, or another tenant only with platform reach, and then only one that exists. An id that names no tenant is refused with `UnknownTenant` (the same for every such id), so the console cannot fill the cache with entries for ids that name nothing. Anyone else naming another tenant is refused (`OtherTenantsFields`) and makes the cache load nothing. Each call gets definitions of its own, built from the cached rows.
  - The settings' reader of a named tenant (`TenantSettingsService.GetOfTenantAsync`) is held the same way: the runtime's own tenant or, with platform reach, any tenant, refused to anyone else before the cache is asked.
  - `QualificationService.GetCatalogueAsync` reads the runtime's own tenant only, behind its readers' permissions (`ResourcesView` or `QualificationsManage`), and serves the "Add qualification" form's choices (`GetChoicesForAsync`). Its list page and a staff member's qualifications are list queries in SQL, and giving a qualification reads it from the database.
- **Invalidated after the commit, by generation.** The owning service calls `Invalidate(catalogue, tenantId)` once a save of that tenant's rows has returned, whether it committed or threw (an unneeded reload costs one query; a missed one would keep a stale entry), and `InvalidateEveryTenant(catalogue)` after a save of a global row, which every tenant's entry holds.
  - Each tenant and catalogue has a generation that an invalidation moves on. An entry is stored and found under the generations it was read at, so a read that began before the invalidation (and may have read the rows before the commit) stores its rows where nobody looks again. It can never put old rows back after the invalidation, and a reader arriving after the invalidation never joins it.
  - The only window is between the commit and the invalidation, a few statements, where a reader still gets the old entry.
  - Writes, and the checks of the values a user or unit service saves (`CustomFieldValues`), always read the database.
- **One load at a time.** Readers of an entry that is not cached share one load, which runs without their cancellation (each stops waiting on its own token). A failed load is not cached, and its readers get its exception, which the service turns into a failed `Result`.
  - The first reader starts the load, and a reader is a Blazor circuit, so the load first leaves that circuit's synchronization context (a forced yield to the thread pool, `ConfigureAwaitOptions.ForceYielding`), and every await on its path, the catalogues' loads included, is `ConfigureAwait(false)`. The shared load never waits for, or runs on, the circuit that started it, whose renderer may be busy or gone while other circuits wait.
- **Bounded.** At most `EntryLimit` (4,096) entries, each kept at most `TimeToLive` (10 minutes) on the injected `TimeProvider` ([ADR 0020](../adr/0020-dates-and-times.md)); a full cache compacts its least used entries. A change written past the owning service (startup seeding before the app serves, a second app instance) therefore shows within ten minutes. The generations are one number per invalidated tenant and catalogue.

Verified by: `TenantLookupCacheTests`, `LookupCacheTests`, `LookupCacheDialogTests`.

## Tenant settings

A tenant's settings are typed definitions in code and one row per setting and tenant. The time zone default is pinned when the tenant is set up ([ADR 0020](../adr/0020-dates-and-times.md): "defaulting to the platform's time zone at onboarding"), so a later change of the platform's configuration moves no tenant.

### Definitions

`TenantSettingDefinitions` (`Staffora.Core/Settings/`) declares every setting once as a `TenantSettingDefinition<T>`: its key (never renamed), category (the shell's panel, `TenantSettingCategory.General`), kind (the editor), name, description, default (from `TenantSettingDefaults`, the platform's values), and how its text is parsed, formatted and judged.

| Key | Kind | Value | Default |
|---|---|---|---|
| `General.TimeZone` | `TimeZone` | An IANA id ([ADR 0020](../adr/0020-dates-and-times.md)); pinned for every tenant (`IsPinned`) | The platform's time zone |
| `General.RespondWithin` | `Hours` | The time to answer a request: whole hours from 1 to 720 as `ResponseWindow`; empty for no deadline (`AllowsEmpty`) | 48 |
| `General.PublicHolidays` | `HolidayCalendar` | The public holiday calendar; empty for none ([Public holidays](schedules-and-absence.md#public-holidays)) | `SE` (Sweden) |
| `General.ClosedOnPublicHolidays` | `Switch` | `true` or `false` | Off |

A setting that is not pinned is missing for most tenants, so its fallback is not logged.

### Time zones

- **Offered zones.** `IanaTimeZones` lists the zones a tenant can choose: the zones of the CLDR mapping between Windows and IANA ids under their current names (`Asia/Kolkata`, `Europe/Kyiv`), plus `Etc/UTC`, without the fixed-offset `Etc/GMT±n` and POSIX-style zones. Only ids the host resolves through `TimeZoneInfo.FindSystemTimeZoneById` are offered, and an id is accepted only exactly as listed.
- **Host time zone data.** A stored id names the same IANA zone on every host that knows it, but each host applies its own time zone data (ICU on Windows, tzdata on Linux). Hosts with data of different ages can disagree about a zone whose rules changed recently, and a recently added or renamed id resolves only on a host with recent data.
- **Unknown stored id.** A stored id this host does not resolve reads as the platform's zone, with a warning in the log, and the General panel says so ("The company's saved time zone, <id>, is not known on this server; choose another.").
- **Refused input.** A Windows id, another spelling, an old alias or an unknown name is refused: "\"Mars/Olympus_Mons\" is not a time zone this system offers. Choose one from the list, such as Europe/Stockholm."
- **Labels.** The picker names each zone by its current offset and its id with spaces for underscores ("(UTC+01:00) Europe/Stockholm"), ordered by offset.
- **The platform's time zone.** `Platform:TimeZone` in configuration (`PlatformOptions`; `appsettings.json` sets `Europe/Stockholm`, the code's default is `Etc/UTC`), refused at startup when it is not an offered id. It is what every tenant's own zone is pinned to when the tenant is set up, the platform tenant's zone (which holds no row and follows the configuration), and the fallback of a tenant whose row is missing or not accepted.

### Pinning at startup and onboarding

`TenantSettingsEnsureService` (singleton, registered by `AddTenantSettings`) writes the `General.TimeZone` row of every tenant without one, holding the platform's zone as configured then. It writes on a system context under a lease of its own, stamped with the tenant's system account.

- `EnsureTimeZoneForEveryTenantAsync` runs at every start of the app (`Program.cs`, after the system accounts; the development demo, built after it, pins its tenants' zones through onboarding) and in the end-to-end preparation.
- `EnsureTimeZoneAsync(tenantId)` runs at onboarding.
- It never changes a row a tenant holds, including one this server does not accept. Two starts at once write one row (the unique key refuses the second insert, which accepts the first one's row). A tenant that fails is logged and the rest are still pinned.
- A missing row is a defensive fallback only: the platform's zone, a warning logged once per cache entry, and a notice in the General panel.

### Rows

`TenantSetting` ([database.md](../database.md#tenantsetting)): the key and the value as text, one per tenant and key, with a row version, never deleted. Read through the [lookup cache](#lookup-cache) (one entry per tenant).

### TenantSettingsService

`TenantSettingsService` (`Staffora.Services/Settings/`) is the only writer of settings.

- **`LoadAsync()`** (`SettingsView`) gives the settings shell every setting with the value in force, the text the tenant's row holds, the platform's default, where the value in force comes from (`TenantSettingSource`: `Stored`, `Missing`, `NotAccepted`) and the version of the tenant's row (`TenantSettingsSnapshot`).
- **`SaveAsync(key, value, loadedVersion)`** (`SettingsEdit`) sets one setting.
  - An empty value is refused ("Time zone needs a value."), as nothing returns a tenant to having no zone, except where the definition allows it (the time to answer, where empty means no deadline).
  - A save is judged by its definition.
  - It is refused with `ChangedMeanwhile` when the row changed or appeared since it was loaded: by the version read, by the row version when it changes between the read and the save, and by the unique key for a row two saves add at once.
  - It invalidates the tenant's entry on every way out once it has read the row, a refusal included, so the panel opened again after a refusal reads what the database holds even when this process's entry was stale (a save by another instance of the app).
- **Own tenant only.** Both work on the runtime's own tenant and take no tenant from the caller. The platform console is refused ("Settings belong to a tenant: change them by impersonating one of the tenant's users."), so a platform owner changes a tenant's settings by impersonating one of its users, and the row records the impersonator. The platform console shows no tenant's settings.
- **`GetAsync(definition)`** gives the value in force for the code that acts on it and needs no permission: every user of a tenant works in its time zone. It reads only the runtime's own tenant; the platform tenant reads the platform's values without the cache. A missing row, or a stored value this server does not accept, reads as the platform's default, logged as a warning once per cache entry.
- **`GetOfTenantAsync(definition, tenantId)`** gives the value in force of a named tenant, for code that acts on that tenant's record from outside its runtime tenant or with no circuit: the platform console's state history and the job clock, which acts as each tenant's system account. It answers only the runtime's own tenant or, with platform reach, any tenant, checked from one snapshot before the cache is asked, and refuses anyone else with `NotYourTenant`, which names no tenant. It reads the same cache entry the tenant's own users read; the platform tenant reads the platform's values.

### Local time

`TenantClock` (scoped) converts to the runtime tenant's wall clock:

- `GetTimeZoneAsync`; `GetTimeZoneOfAsync(tenantId)`, a named tenant's zone through `GetOfTenantAsync` (a state history read on the platform console shows its times in the entity's own tenant's zone, and the job clock and the job workflow's clock steps judge a period on the job's tenant's calendar);
- `NowAsync` (from the injected `TimeProvider`) and the static `ToLocal(instant, zone)` for code that converts many instants with one zone; a `DateTime` that is not UTC is refused;
- `TodayAsync`, today on the tenant's wall clock;
- the static `ToUtc(date, time, zone)`, which converts a calendar day and a time of day to an instant by handing on to the pure `WallClock` in `Staffora.Core/Scheduling/`, by the daylight-saving rules of [Schedules and availability](schedules-and-absence.md#schedules-and-availability);
- `GetHolidaysAsync` and `GetHolidaysOfAsync(tenantId)` ([Public holidays](schedules-and-absence.md#public-holidays)).

The General panel shows the time now in the chosen zone through it. The platform console's error log shows UTC.

### The settings shell

`Pages/TenantAdmin/Settings.razor` ("Settings" under Organization, gated on `SettingsView`, as the services are) holds the panels' navigation and one panel at a time, each at its own address so F5 and a link keep it.

| Group | Panel | Address |
|---|---|---|
| Company | **General** (`GeneralSettingsPanel`, `Components/Settings/`) | `/tenant/management/settings` |
| Data | **Custom fields** (`CustomFieldsPanel`, [Entity attributes](#entity-attributes-dynamic-custom-fields)) | `/tenant/management/settings/custom-fields` |
| Staffing | **Services** ([Service catalogue](jobs-and-staffing.md#service-catalogue)) | `/tenant/management/settings/services` |
| Staffing | **Closed days** ([Closed days](schedules-and-absence.md#closed-days)) | `/tenant/management/settings/closed-days` |

A new setting kind adds its editor to its category's panel; a new category adds a panel and its address.

**The General panel.**

- **Time zone.** A searchable Radzen drop-down: its input named "Time zone" through `FieldLabel`, its search box "Search time zones", matching any part of a label in any case. The keyboard opens it with Enter and types into the search, which filters; ArrowDown and Enter choose the match, as the arrows start from the top of a filtered list (`ResetSelectedIndexOnFilter`). A choice clears the search, and the picker is a new one after every load, so a search left without a choice never hides the saved zone's label.
- Beside it: the time now in the chosen zone, and the platform's time zone ("(UTC+01:00) Europe/Stockholm").
- **Save** is enabled whenever the chosen zone differs from the stored one and says what it saved in a polite status. "Use the platform's time zone (Europe/Stockholm)" shows whenever the stored zone is not the platform's and stores the platform's zone as the company's own row. A refused save shows the service's reason.
- A stored zone that is missing or not known on this server is said so in a warning above the picker, with the platform's zone chosen, so one Save (or the platform-zone button) replaces it, or another zone is picked instead.
- **Time to answer a request.** A number field named "Hours to answer" (1 to 720, empty for no deadline, its placeholder "No deadline"), what a request sent now would do ("2 days after it is sent", or "Never, there is no deadline"), the default without a saved value, and "Save time to answer", enabled while the hours differ from the value in force.
- **Public holidays** section ([Public holidays](schedules-and-absence.md#public-holidays)).
- Someone with `SettingsView` alone sees the panel with the picker disabled and no buttons ("You can view the settings but not change them.").

**Layout and keyboard.**

- The navigation is a `nav` named "Settings" (15 rem wide) beside the open panel on a wide window, with no quick-help column (the page's description is in the header's help popover). The panels sit under uppercase group headings, each group's list named by its heading, each panel a link with its icon; the open one is marked `aria-current="page"` and drawn on the accent's soft tint in the accent and semibold.
- The arrow keys, Home and End move the focus between the links, wrapping round (`wwwroot/scripts/settings-nav.js`, attached once the navigation is on screen); Enter follows one and the focus stays on it.
- The panel is a bordered card (radius 12, 1.5 rem padding) running down the window, its title 1.25 rem bold over one subtle sentence, the General panel's fields at most 32 rem wide.
- At 768 px and below, the navigation stands above the panel as a select-like button named "Settings section: General" (`aria-expanded`, `aria-controls`) that shows the links under it. Escape hides them and gives the focus back to the button, and a link followed closes them with the focus back on the button.

Verified by: `TenantSettingDefinitionTests`, `PlatformOptionsTests`, `TenantSettingsTests`, `TenantSettingsEnsureTests`, `ServicePermissionTests.Tenant_settings_require_their_permission`, `SettingsPageTests`, `tests/e2e/settings.spec.ts` (with the accessibility sweep covering both panels and the open picker).
