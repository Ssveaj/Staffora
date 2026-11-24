---
name: workflow-state-entities
description: Workflow-driven state entities (StateEntityBase, workflow attributes, transitions, per-state field access, StateEntityDialog, transition buttons, custom-field attributes). Use when adding or changing an entity with lifecycle states, defining transitions, conditions or transition actions, or building the dialog, buttons or custom fields of a state entity.
---

# Workflow state entities

A **state entity** has a lifecycle. Its **workflow** is the single source of truth for which states exist, which transitions are allowed, who may run them, and which fields (custom fields included) are visible, editable and mandatory in each state. Services and components ask the workflow; they never decide these things themselves. Its UI is always a **StateEntityDialog** whose footer renders the allowed transitions as buttons ([ADR 0001](../../../docs/adr/0001-workflow-engine-for-state-entities.md), [ADR 0009](../../../docs/adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md)). The end-to-end flow of one step is described in [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md#a-workflow-step-end-to-end).

Find existing state entities by searching for `StateEntityBase<`.

| Read | Before |
|---|---|
| [TRANSITION-ORCHESTRATION.md](TRANSITION-ORCHESTRATION.md) | Changing `WorkflowService`, or writing a transition action that locks, cascades into or writes other entities |
| [DIALOGS.md](DIALOGS.md) | Writing or changing a state-entity dialog, its opener, its views or transition buttons |
| [ATTRIBUTES.md](ATTRIBUTES.md) | Adding custom fields to a state entity, or adding or reading a declarative C# attribute |

## Where the code lives

- **Engine:** `Staffora.Core/Workflow/` (`Workflow<TState, TEntity>`, `WorkflowRegistry`, `Conditions/`, `Actions/`, `FieldAccess/`). It evaluates and executes in memory and never saves. Its unit tests in `Staffora.Test/Unit/Workflow/` show every piece on test-only sample entities.
- **Orchestration:** `WorkflowService` (`Staffora.Services/Workflow/WorkflowService*.cs`) runs every transition: it locks, executes, writes history and saves in one transaction and dispatches after commit ([ADR 0002](../../../docs/adr/0002-workflowservice-orchestration.md)). Its integration tests (`Staffora.Test/Integration/Services/WorkflowServiceTests*.cs`) run on the test-only `TestParent`. Running transitions ad hoc from a component or service creates the race conditions ADR 0002 exists to prevent.

When this skill and the code disagree, trust in this order: the build, the nearest finished workflow, the ADRs, then this skill. Current gaps are in [docs/known-issues.md](../../../docs/known-issues.md).

## Adding a state entity

1. **State enum.** Declare `{Entity}State` next to the entity. Every member carries `[Display(Name)]`, `[Description]`, `[Icon]`, `[StateStyling]` and `[EntityStateCategory]` (`Entry` before use, such as Create, Draft or Setup; `Open` in use; `Done` finished as intended; `Canceled` ended unfinished, delete states included; `Archived` out of use and kept for the record). Mark the entry state `[EntryState]` (or name it `Create`); outside system scope `SaveChanges` inserts the entity only in it. Mark hard-delete states `[DeleteState]`. Done when `StateAnnotationScanTests` finds every member annotated.
2. **Entity.** Inherit `TenantStateEntityBase<{Entity}State>` (tenant-owned) or `StateEntityBase<{Entity}State>` (tenant-free: `Tenant`, and a custom field definition, whose nullable `TenantId` names its tenant or none). Both give `Guid Id`, `State` and `IsInTransition`. Decorate it with `[{Entity}Workflow]`.
3. **Workflow attribute.** Create `{Entity}WorkflowAttribute : WorkflowAttribute` in `Staffora.Core/Workflow/WorkflowAttributes/` and build a `Workflow<{Entity}State, {Entity}>` in `CreateWorkflow()`. Declare area names as `public const string Area{Name}`. Done when `entity.GetWorkflow<TState, TEntity>()` returns it; `WorkflowRegistry` checks the declaration on first use and throws `WorkflowDefinitionException` for a mistake.
4. **States.** `AddState` for **every** enum member (registration refuses a missing one), each with its own `ConfigureFields(...)`. An entity with custom fields configures `AreaAttributes` in every state ([ATTRIBUTES.md](ATTRIBUTES.md)).
5. **Transitions.** For each allowed move, `workflow.AddTransition(new("{entity}-{verb}", From, To) { ... })` with a durable `Key`, a `Description`, `RequiredPermission`, `SortOrder` (back 10, neutral 20, forward 30, as private constants), `Placement` (Leading for hand-back steps, Trailing otherwise), described conditions and actions. Destructive steps get a `ConfirmationMessage`. Every state where the user edits data has a "Save" self-transition (`Icon = "save"`) marked `IsSave = true` ([TRANSITION-ORCHESTRATION.md](TRANSITION-ORCHESTRATION.md#the-save-step)); with a `Create` entry state, `Create → {first state}` is that state's save. Delete is a transition into a `[DeleteState]`, gated by a described condition. Done when every edge of the lifecycle and every save loop is registered and the description test passes.
6. **Dialog.** Build the StateEntityDialog (context, body, opener) by the recipe in [DIALOGS.md](DIALOGS.md). Done when field visibility, editability and required markers all come from the field config through the `StateField` components, `StateAreaFieldset` and `StateCustomFields` (`StateFieldScanTests` finds no hand-written check in any body or context), and the footer shows only transitions the current user may run.

## Rules

1. The only UI for a state entity is a StateEntityDialog: no form dialog, no buttons of its own in the body. When an entity becomes stateful, convert its dialog in the same change.
2. Saving, creating, editing and deleting are transitions. `State` and `IsInTransition` change only through `WorkflowService`: an action that writes either itself, on any entity, fails the transition, which rolls back.
3. A new entity is created together with its first step, in one call and one transaction (`WorkflowService.CreateAndTransitionAsync`). Several records made as one change go through one batch (`CreateManyAndTransitionAsync`, or the internal `RunBatchAsync`): each runs its own first step with its gate, conditions and history in one transaction, their history shares the first step's correlation id, and a refused one rolls back all. A record the batch created may take a further step there (`WorkflowBatch.TransitionAsync`); no other record may.
4. A new row whose child rows hold a state entity is refused: each state entity is created by its own first step. A column only the system sets is refused by the create step's own condition (the job's `NothingSetAheadReason`, the user's `OrdinaryNewAccountReason`), since the engine stores a new row's columns as given.
5. The access gate and the step's conditions are judged on the new entity before anything is written (a reference named only by its foreign key is read first, and so is every reference along a dotted path the first step's conditions read, such as `Resource.User`); a refusal after the insert rolls the insert back. `GetTransitionAccessAsync` answers the gate for a state's steps without a row, for a dialog's create mode.
6. To hide a button, add a condition. Buttons render only when the transition can run; `ShowWhenBlocked` draws it greyed with the condition descriptions.
7. Field access comes from `workflow.GetFieldConfig(entity)`. Visibility follows state, never `Id == Guid.Empty` or an `IsCreate` flag; `IsCreate` only chooses between the create and the update service call.
8. Lists find, dialogs act: a list row opens the entity's dialog and carries no transition buttons. A child list in a parent's dialog is a view tab whose row opens the child's own StateEntityDialog on top.
9. A transition action that modifies another state entity adds it in `AddLockCandidates`, so `workflow.GetAllCandidatesForTransition` returns it and it is locked with the primary entity.
10. Notifications are workflow actions (`NotifyAction`), written with the step and dispatched after commit ([ADR 0011](../../../docs/adr/0011-notifications-from-workflow-after-commit.md); [TRANSITION-ORCHESTRATION.md](TRANSITION-ORCHESTRATION.md#notices)). Services never call the notifier directly; the comment service is the one other source.
11. Actions and conditions resolve services through `WorkflowContext.ServiceProvider`, read time from `WorkflowContext.TimeProvider`, and stay free of `HttpContext` and UI types.
12. A condition names every navigation it reads (`Reads`, dotted for nested paths; `DelegateCondition` takes them as `nameof(...)` arguments; only references and collections of entities, so a column, a primitive collection or a `[ComplexType]` fails registration). A navigation that is not loaded refuses the transition; it never passes by default. The caller's `WorkflowEnvironment` says what is loaded (`NavigationLoadState.From(dbContext)`). So every navigation a condition, action or ownership rule reads belongs in `WithWorkflowGraph()`, which both the dialog's `QueryConfigurator` and the service's load use.
13. Transitions are resolved by their durable `Key`, also when one `From`/`To` pair has several.

## Entry state

- Use a dedicated `Create = -1` entry state when the entity owns child rows that need its `Id` before they can be added, or has many fields and benefits from saving before activation. The explicit `-1` leaves existing state values unchanged. Its dialog offers the steps out of it as its create, so the record is created straight into its first state.
- When the entry state is the only safe default (a user: a row in Create signs in nowhere and holds nothing), give it the value 0 and number the others after it (`Create = 0, Active = 1, Inactive = 2`): a row written without naming a state then fails closed. Never make a state that grants access the default.
- Start in a business state instead when one already fits (`Tenant` starts in `Setup`), the entity is created and activated in one step, it is system-managed, or it starts at a request (`Requested`).
- A record that only follows other records has no dialog of its own, and every step is system-driven. A user's `Resource` follows the resource role ([ADR 0008](../../../docs/adr/0008-users-become-resources-through-a-role.md)): `ResourceLinker` creates it in its entry state and moves it with system-driven steps inside the transaction of the write that called for it (a role holding's service, or a step's follow-up, `IStepFollowUp`).
- `State` defaults to the member with value `0`, so the dialog context sets the entry state explicitly for a new entity.

## Deleting

Per [ADR 0016](../../../docs/adr/0016-deletion-cascades-through-child-workflows.md):

- **Hard delete or terminal state.** Offer a transition into a `[DeleteState]` only from entry or draft states, behind a described condition or a registered delete blocker, and with a `ConfirmationMessage`. Once anything depends on the entity, offer Archived, Inactive or Canceled instead.
- **Downward cascade.** Declare it on the delete (or cancel) state: `.Cascade(e => e.Children, ChildState.Deleted, leaveAlone...)`. Registration refuses a target the child workflow does not register, has no transition into, or reaches by two transitions from one state (a step marked `NeverCascaded` is not counted, and the engine's plan and the access gate leave it out). At run time a child in neither the target nor a left-alone state needs a transition to the target, or the step is refused. Each child runs its own transition (conditions, lock candidates, actions, access gate, history), recursively.
- **Cascade into a delete state** leaves no state alone (registration refuses `leaveAlone` there; block the delete with a condition instead). A child already in its delete state is locked and removed with the parent, without a history row of its own, together with everything its delete state cascades to. `WorkflowService` loads every cascaded navigation itself and removes every entity left in a delete state in the same save, grandchildren first, with one history row per entity that moved, correlated with the parent's.
- **Cascade through a reference.** A cascade's navigation may be a path through references to a collection (`.Cascade(e => e.Slot.Assignments, …)`). Each record moves through its own transition, a system-driven one included (the access gate asks it no permission; the footer never offers it). The path reaches the entity itself too, so the engine (`CascadeAction`) and `WorkflowService` leave it out, by reference and by id. Registration refuses such a cascade into a delete state. A create may carry one; `WorkflowService` loads the path before the insert, under the step's lock.
- **A child step taken with its parent.** `WorkflowContext.IsTakenWith(parent)` tells a condition that the child's step is part of the parent's step, so a rule the child keeps when taken alone may give way (a job's cancellation cancels its slots whatever their state). The access gate still asks the child step's own permission as part of the whole plan.
- **Database cascade only for owned rows without a lifecycle** (segments, timetable rows, permissions, attribute values). A child that is a state entity is `NoAction` in the database and cascades through its workflow.
- **Other dependents** (second foreign-key paths, join edges) are removed by a registered `IDeleteCleanup`, or block the delete through a condition that reads their navigation, or through a registered `IDeleteBlocker` ([TRANSITION-ORCHESTRATION.md](TRANSITION-ORCHESTRATION.md#delete-blockers)). A cleanup names its entity type (or a base type or interface) and dependent types, is registered in `AddWorkflowServices`, and runs on the transition's own context before the entity is removed.
- **No other way out.** `SaveChanges` refuses a delete of a state entity outside a transition save scope, in system scope as well, so tests and services delete through the delete transition too.
- Done when the delete-coverage model test (`Staffora.Test/Unit/Workflow/DeleteCoverageTests.cs`) passes for the entity.

## Field access

Each state owns a `StateFieldConfig<TEntity>`, configured through `StateFieldBuilder` inside `ConfigureFields(...)`. Configs are not inherited between states.

`FieldAccessFlags`: `Visible`, `Editable`, `Mandatory`, combined as `ReadOnly` (Visible), `ReadWrite` (Visible | Editable) and `Required` (all three). `None` hides the field.

Resolution, first match wins:

1. Field override: `.Field(e => e.Name, flags)`
2. Area flags, when the field belongs to an area: `.Area("Name", flags, a => a.Field(e => e.X))`
3. State default: `.Default(flags)`, which is `ReadWrite` when unset.

An **area** groups fields that show, hide or lock together: a child collection, a view tab, the custom fields. Fields that always follow the state default need no area. Configure a foreign key by its key property (`e => e.CustomerId`): field access is judged per property name.

Typical shape across a lifecycle:

```text
Create:    Default(None), identity fields Required
Draft:     Default(ReadWrite), child areas ReadWrite
Active:    identity fields ReadOnly, data ReadWrite
Archived:  Default(ReadOnly)
```

**`Required`** means the field has a value **while the entity is in that state**: a save in the state may not empty it (the server refuses), and the state's save step carries a `RequiredFieldCondition` for it, so the dialog's save stays blocked until the value is there. A row stored without the value still saves its other edits; the edit that supplies the value repairs it. Leaving a state that needs a value later (activation needs a name) is a condition on that transition. Required custom fields work differently ([ATTRIBUTES.md](ATTRIBUTES.md#custom-fields-on-a-state-entity), "Required fields").

### Server enforcement

The server enforces field access; the dialog's flags are a convenience, not the guard. `SaveChanges` (`Staffora.Data/ApplicationDbContext.WorkflowFieldEnforcement.cs`) refuses with a `WorkflowFieldEditException`:

- **Columns:** an update that changes a column the stored state does not make editable, or `State`/`IsInTransition` outside a transition save scope. Only changed values count.
- **Held rows:** an update or delete of a state entity whose stored `IsInTransition` is true, outside a transition save scope (`WorkflowFieldEditException.HeldByTransition`).
- **Deletes:** any delete of a state entity outside a transition save scope, in system scope as well.
- **Child collections:** a collection is a field of the parent, named by its navigation. When the parent's workflow **declares** it (`.Field(e => e.Lines, …)` or an area holding it, in at least one state), every insert, update and delete of a child row is judged against the parent's stored state, however it is written. A write is refused while the parent's state does not make the collection editable or a transition holds the parent; a moved child is judged against both parents. Not judged: a child in a transition save scope or whose parent is in one, an update of a child that changes no value, a child of a parent inserted or deleted in the same save, and (by the parent's original state instead) a child of a parent updated in the same save. A collection left to the state default is never judged, so declare every collection whose rows the parent's state should lock.
- **Inserts:** outside system scope and outside a transition save scope, a state entity is inserted only in an entry state; an enum that marks none refuses every such insert. Seeding, imports and fixtures that need a later state insert it in system scope. Outside a transition save scope it is never inserted with `IsInTransition` true, in system scope as well.
- **Required fields:** an update outside a transition save scope that empties a column its stored state makes `Required` (null, a blank string, the empty id) is refused, naming it in `EmptiedRequiredFields` ("{Display(Name)} is required while the {Entity} is {state}."). A declared `Required` collection must keep a row. Required custom fields are held the same way in a state a step carrying `RequiredAttributesCondition` leads to (`EmptiedCustomFields`). A transition's save is not judged (its actions may clear what the target state no longer needs), and inserts are judged by the create step's conditions.
- **Custom-field values:** the value collection is a declared collection in every state, so every value write is judged against the owner's stored state ([ATTRIBUTES.md](ATTRIBUTES.md)).
- **State history** (`StateHistoryEntry`) is never updated and deleted only in system scope; bulk statements on it are refused in every scope (`BulkUpdateGuard`), and only `StateHistoryWriter` constructs an entry (`DataContextScanTests`).
- **Bulk statements:** `ExecuteDelete` of a state entity is refused in every scope. `ExecuteUpdate` is allowed for the transition lock (for a custom field definition, the lock alone: `IsInTransition` and nothing else). A state entity with a row version has it read again after the lock (`WorkflowService.Locks.cs`).
- The scans read names only: reaching `BeginTransitionSaveScope`, the context's set of save-scoped entities, or `ExecuteUpdate`/`ExecuteDelete` through reflection or a new `ApplicationDbContext` partial class is forbidden.

## Transitions

### Conditions

Derive from `Condition<TState, TEntity>` or use `DelegateCondition`, `AllCondition`, `AnyCondition`, `RequiredFieldCondition` or `RequiredAttributesCondition`. Each carries a `Description` the footer shows when the step is blocked (`workflow.GetBlockingReasons`). Single-use conditions live as private nested classes in the workflow attribute. `RequiredAttributesCondition` reads the fields from `WorkflowEnvironment.CustomFields` ([ATTRIBUTES.md](ATTRIBUTES.md)); without them it refuses as a condition that cannot be checked, never passes.

### Permissions

The access gate ([TRANSITION-ORCHESTRATION.md](TRANSITION-ORCHESTRATION.md#transition-access-gate); [docs/authorization.md](../../../docs/authorization.md)) checks one of these per step, the same for running a step and for `GetAvailableTransitionsAsync`:

- **`RequiredPermission`** for role-based access.
- **Owner rule:** a step only its owner takes (an assignment's accept, decline, take back a yes) names the owner's own permission and puts the owner rule in a condition read from the runtime (`RespondToAssignment`, `OwnReason`).
- **Owner or another permission:** the gate takes one permission, so such a step names none and puts the rule in a condition, read from the runtime and from a guard its `IStepPreparation` fills with the session's permissions (an absence's save and cancellations: the staff member with `ReportOwnAbsence`, or a holder of `ManageResourceAbsence`; `IResourceAbsenceGuard`). The entity's service checks one of the permissions first.
- **`RequiresPlatformOwner`** (with no `RequiredPermission`) for steps only a platform owner runs, such as every tenant lifecycle move, since tenant roles must never grant them. It passes only with platform reach; what such a step writes into another tenant is stamped with that tenant's system account.

The gate covers the whole plan: a step whose cascades run a child step the user may not take is refused as a whole before any lock, and the engine refuses any step outside the judged plan.

### Actions

Implement `IAction<TState, TEntity>` (or use `DelegateAction`): `ExecuteAsync` does the side effect; `AddLockCandidates` names every other state entity it will touch, which must already be saved (a new one has no row to lock and is refused). Actions run leave (From-state) → transition → enter (To-state); `State` changes after all of them, so an action reads the destination from `context.TargetState`.

### A step that needs more than the entity

- **Input from a form, or a secret the step makes** (a customer's "Enable portal", a user's "New password"): a scoped service the entity's service prepares before it runs the step, which the action resolves from `WorkflowContext.ServiceProvider` (`ICustomerPortalProvisioner`, `IUserPasswordIssuer`; declared in Staffora.Core, implemented in Staffora.Services). A step run past the service finds nothing prepared and fails. The service refuses a second preparation for the same entity while the first one's step still runs, and only the caller that prepared forgets.
- **Rows outside its own entity** (a managed tenant with its rows) are written by an `IStepFollowUp` in the step's transaction once the entity is saved, never by the action, whose saves would carry the entity's changes outside the transition save scope.
- A follow-up that must run for a step decides from the step's own outcome (`TransitionOutcome.Steps`), never from something kept on the circuit's scoped service, which another step of the same record may clear meanwhile.
- A follow-up that finds, under the step's locks, that what the step calls for cannot be written refuses the whole step with a reason its user may read (`StepFollowUpRefusedException`; never a word about another tenant), and the step's save rolls back with it.
- A record written on another tenant's behalf by a follow-up (the job a portal sends) starts its history with a system-driven step of its own workflow (`job-receive`), recorded by `StateHistoryWriter.AddOnBehalfOf`, never run through the engine.
- A step that asks nothing of a form (an order's Send) needs no preparation: its action links the request through a scoped sender (`IOrderSender.Link`) and adds the notice's delivery as after-commit work (`WorkflowContext.AfterCommit`).

### Telling another tenant

A step whose record crosses the tenant boundary queues an integration message in its own transaction through a scoped service the workflow resolves (`IJobStateSync` for a job, `IOrderSender.QueueCancelAsync` for a request), from an action that adds a `BeforeSave` write. Never use an enter action on a state that has a save step (a save step that runs actions is refused at registration); put the action on every step into the state from another state. A record of the tenant's own queues nothing; one that crosses where no such service is registered fails the step, so it never moves untold.

### System-driven transitions

`IsSystemDriven` steps are run by services or cross-tenant sync and never shown as buttons.

- They run only inside the write they follow from, through `WorkflowService.RunSystemStepInTransactionAsync`; every public path of `WorkflowService` refuses one, whoever asks ([TRANSITION-ORCHESTRATION.md](TRANSITION-ORCHESTRATION.md#entry-points)).
- Callers: `ResourceLinker`, `JobClock` (a confirmed job goes in progress and is completed with its period; a request or request round whose answer-by time passed expires, [ADR 0041](../../../docs/adr/0041-a-request-expires-by-a-system-step.md)), the integration handlers `JobStateSyncHandler` and `OrderCancelHandler` (inside the message's delivery as the receiving tenant's system account, passing the delivery's `AfterCommit`), and `SlotGroupFollowUp`. `StaffRecordScanTests` holds the list; `IntegrationHandlerScanTests` keeps a handler inside its delivery.
- A system-driven step may be the target of a cascade, which runs it inside the cascading step; the cascade's access gate asks it no permission.
- Such a step names no permission when no user ever takes it, and its conditions still judge whether it is due, so a caller that misjudged runs nothing early.
- A workflow that needs the system to take a step adds a service that calls the runner inside the write it follows from, never the public API.

### Steps that take no edits

A dialog sends its edits with whatever step is pressed, so a rule only the save step carries would be bypassed by pressing another step of the same state. Mark every step that decides, cancels or acts on the record without changing its fields `TakesNoEdits = true`:

- `WorkflowService.SaveAndTransitionAsync` refuses it while the updater has edits (`TransitionRefusals.PendingEdits`, before anything is read, and again under the lock), and, once the row is locked, when a column the updater's snapshot holds differs from the stored row (`TransitionRefusals.ChangedSinceOpened`; compared by `DetachedEntityUpdater.ColumnsChangedSinceSnapshot`, which skips the key, concurrency tokens, columns another owner writes and audit stamps). It never saves the updater.
- The version check needs the snapshot, so it holds only for callers that pass an updater; `TransitionEntityAsync`, which takes an id, checks none.
- A service that tidies the updater before the step (trims, normalizes) leaves it alone for such a step, or it reads as edits nobody made.
- The dialog draws it greyed with the first reason while it holds edits ([DIALOGS.md](DIALOGS.md)). A create's first step is not judged by it, and registration refuses it on a save step.
- A new workflow asks of each step that leaves a state with editable fields: does it carry the save's rules itself, lead into a delete state, or have the data context or its service judge the same rule on any write? If none, mark it `TakesNoEdits`.
- A rule that distinguishes what an edit chooses anew from what the stored row keeps (a job's customer, contact or unit) asks a guard prepared before the step's edits are saved (`IJobGuard`, an `IStepPreparation`), never the navigation the edit set.

### Who may store edits: the edit-authority rule

A step's gate says who may take it, not who may change the fields its edits carry. The engine holds every workflow to one rule: **a dialog's edits go with a step other than its state's save only for a user who passes the access gate of the step that stores edits in that state, and the step is judged by that step's conditions as well as its own.**

- That step is `Workflow.GetEditAuthority(state)`: the designated save (`IsSave`), or, in a state without one, its one self-transition a user takes with edits (not system-driven, not `TakesNoEdits`).
- A state with neither takes edits with its steps only when every step out of it that may carry edits asks the same gate (`Workflow.EditsGoWithItsSteps`); otherwise the state stores no edits and each step out of it runs without them.
- `WorkflowService.SaveAndTransitionAsync` refuses a step with edits for a user without that authority before anything is read (`TransitionRefusals.EditsRequireSaveAccess`), asks again under the lock, and refuses a state that stores no edits (`TransitionRefusals.NoEditsInState`). Once the edits are saved it judges the save's conditions beside the step's.
- A create's first step is not judged by it, and a delete takes no edits.
- The dialog draws such a step greyed with `TransitionRefusals.EditsNotYours` (or `NoEditsHere`) while it holds edits, and the service keeps the fields read-only for such a user.
- `EditAuthorityTests` fails on a new step whose gate differs from its state's save, and on a state that would refuse edits a step carries; extend its expectations deliberately.

### Confirmation or comment

- A destructive step gets a `ConfirmationMessage`.
- A step that needs the user's words (decline, reject, cancel) gets a `CommentPrompt` (`TransitionCommentPrompt`: title, message, placeholder, the thread its answer goes into, `Audience`, Internal by default; `Required`, true by default, with its `RequiredMessage`). `WorkflowService` requires a required answer, hands it to the actions as the context's `Comment` and writes it into the entity's thread inside the step. An optional prompt's step may run without words and then writes nothing.
- A prompt is the step's confirmation too: registration refuses a transition with both, a prompt on an entity that does not implement `ICommentedEntity`, and a prompt on a step into a delete state.
- A conversation written only through steps (absence questions, [ADR 0035](../../../docs/adr/0035-absence-questions-are-workflow-steps.md); request questions, [ADR 0037](../../../docs/adr/0037-request-questions-are-workflow-steps.md)) is a state saying whose turn it is, steps with prompts each way, and a thread whose access carries a `ClosedReason` (with `ShowsClosedReason` off) while the steps own it. A state added beside an existing one as "the same, with someone's turn attached" gets every step out of the existing state again, each with its own key and the same conditions and actions, and every reader of the existing state decides about it (a helper such as `SlotAssignmentWorkflowAttribute.IsAsked` names both). A `NotifyAction` with `withComment` carries the prompt's words in its notice.

### Steps per edition

Where a staffing company and a customer portal must take the same step on the same entity and no permission is held in both editions, the workflow declares the step twice, each gated on its edition's permission, with the same conditions and actions (the user workflow's `PortalSteps`). A state whose user steps then ask two gates takes edits with none of them (the edit-authority rule).

## References

- Decisions: [ADR 0001](../../../docs/adr/0001-workflow-engine-for-state-entities.md), [0002](../../../docs/adr/0002-workflowservice-orchestration.md), [0008](../../../docs/adr/0008-users-become-resources-through-a-role.md), [0009](../../../docs/adr/0009-state-entity-dialog-only-and-workflow-governed-attributes.md), [0010](../../../docs/adr/0010-comment-threads-per-audience.md), [0011](../../../docs/adr/0011-notifications-from-workflow-after-commit.md), [0015](../../../docs/adr/0015-dialogs-save-through-a-detached-entity-updater.md), [0016](../../../docs/adr/0016-deletion-cascades-through-child-workflows.md), [0036](../../../docs/adr/0036-history-is-for-owners-and-the-workflow-inspector-is-gone.md).
- Per-entity lifecycles and their steps: [docs/database.md](../../../docs/database.md#entities) and [docs/features.md](../../../docs/features.md).
