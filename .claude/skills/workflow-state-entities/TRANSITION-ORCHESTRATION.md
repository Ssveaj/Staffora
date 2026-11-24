# Transition orchestration

How `WorkflowService` runs one transition ([ADR 0002](../../../docs/adr/0002-workflowservice-orchestration.md)), with deletes per [ADR 0016](../../../docs/adr/0016-deletion-cascades-through-child-workflows.md), notifications per [ADR 0011](../../../docs/adr/0011-notifications-from-workflow-after-commit.md) and live refresh per [ADR 0019](../../../docs/adr/0019-coordination-workspace.md). This is the contract every transition action must fit.

`WorkflowService` is one class in partial files in `Staffora.Services/Workflow/`:

| File | Holds |
|---|---|
| `WorkflowService.cs` | Constructor, entry points, `RunAsync`, the system step, the after-commit work |
| `WorkflowService.Judgement.cs` | Access gate, held tenant, edit-authority rule, conditions and what they read, the footer's answers, delete blockers |
| `WorkflowService.NewRecords.cs` | A create's copy, its references and its refusals |
| `WorkflowService.Locks.cs` | Step locks, the lock statement and its change-tracker sync, cascades, removal of deleted records |
| `WorkflowService.Refusals.cs` | Refusal texts and the mapping of an exception to a refusal |
| `WorkflowService.Batch.cs` | Several creates as one change |

`AddWorkflowServices()` (`WorkflowServiceRegistration.cs`) registers the service, the dispatcher, the step locks, preparations, follow-ups, delete blockers and cleanups, for the app and the tests alike.

## Entry points

- **`TransitionEntityAsync<TState, TEntity>(entityId, transitionKey, queryConfigurator?, comment?, ct)`** runs the transition by its durable key. `TransitionEntityToStateAsync<TState, TEntity>(entityId, targetState, …)` runs the one transition from the current state to `targetState`, refused when there is none or more than one. Both return a `Result`; a refusal carries its reasons and leaves the database as it was.
- **`GetAvailableTransitionsAsync<TState, TEntity>(entityId, queryConfigurator?, ct)`** answers in one call every transition from the current state: the access gate's answer over the whole plan and the blocking reasons (`TransitionAvailability<TState, TEntity>`). The entity and the permissions are read once. A condition that throws blocks its own step with "This step cannot be checked yet." (`WorkflowService.StepCannotBeChecked`) and is logged; the other steps keep their answers.
- **`SaveAndTransitionAsync<TState, TEntity>(updater, transitionKey, queryConfigurator?, comment?, ct)`** is a dialog's step in edit mode: the updater's edits and the step in one transaction.
  1. Cheap refusals first: signed in; the step exists; it leaves the state the updater's snapshot was taken in; it is not a step into a delete state; a `TakesNoEdits` step is not sent with edits (`TransitionRefusals.PendingEdits`); the access gate; the edit-authority rule (`TransitionRefusals.EditsRequireSaveAccess`, `TransitionRefusals.NoEditsInState`; [SKILL.md](SKILL.md#who-may-store-edits-the-edit-authority-rule)).
  2. On the step's context, inside the transaction it opens and under the step's locks: the edit-authority rule asked again of what the updater holds, then the updater's diff saved with the field enforcement of any save (`DetachedEntityUpdater.SaveInTransactionAsync`, outside the transition save scope), then the change tracker cleared. A `TakesNoEdits` step saves nothing and is refused with `PendingEdits` when the updater holds an edit by then.
  3. Steps 1 to 14 of the sequence run on the row as the database now holds it, the edits merged with whatever others changed. A `TakesNoEdits` step is refused at step 7 when the row, read under its lock, differs from the updater's snapshot (`TransitionRefusals.ChangedSinceOpened`).
  4. Any refusal or exception rolls the edits back with the step; after the commit the updater accepts its changes.
- **`CreateAndTransitionAsync<TState, TEntity>(entity, transitionKey, …)`** is a dialog's create. A copy of the entity (columns, the keys of referenced rows, its child rows) is judged: entry state; a tenant's record only inside its tenant; the access gate; no cascade over the new row's own children (a cascade through a reference is loaded and judged before the insert and run after it); no state entity among its child rows at any depth; every referenced row found (else "not found", the same for another tenant's); the references along every dotted path the step's conditions read loaded, through the new row's child rows too, and a stored row's collection on such a path. Then it is inserted in the step's transaction and moved by steps 1 to 14. The caller's instance is never changed.
  - An `internal` overload takes an `afterInsert` hook that writes what the inserted row needs before its step is stored (a new tenant's system account, Company root and time zone: `TenantService.CreateTenantAsync`), on the step's context, inside its transaction and under its platform lease. It commits with the step and rolls back with any failure; a create refused before its insert never runs it (`WorkflowServiceTests.Create.cs`, `TenantCreateTests`). The hook names the creator of every row it writes for another tenant (that tenant's system account), opens a system-scope lease with a reason for those rows, never writes the new row's `State` or `IsInTransition`, and never catches a failed save to carry on (see [Failures](#failures)).
- **`CreateManyAndTransitionAsync<TState, TEntity>(entities, transitionKey, …)`** and the internal **`RunBatchAsync(what, work, ct)`** (whose `WorkflowBatch.CreateAsync` mixes entity types) run several creates as one change: one context, platform lease and read-committed transaction. Each create runs `RunAsync` exactly as `CreateAndTransitionAsync` would (its step locks, gate, conditions, history and follow-ups; the change tracker cleared between creates), its history correlated with the batch's first entry. Nothing commits until every create succeeded; a refusal or exception rolls back all (no savepoints). After the one commit, notices, after-commit work and follow-up work run in order and the tenant change is published once. A record the batch created may take a further step (`WorkflowBatch.TransitionAsync`, step locks taken through `BeginLockedAsync(..., inBatch: true)`); a record the batch did not create is refused as not found, so a batch never moves stored records past their dialogs.
- **`GetTransitionAccessAsync<TState, TEntity>(state, ct)`** is the access gate alone, for a record without a row (a create dialog's footer).
- **`RunSystemStepInTransactionAsync<TState, TEntity>(context, entity, transitionKey, ct)`** (internal to Staffora.Services) runs a **system-driven** step inside a write that is not itself a step, on that write's context and open transaction, and leaves the commit to the caller.
  - It takes the type's step locks again (the caller's transaction owns them, so they are granted at once), judges the held tenant, runs the preparations and conditions, locks the candidates, runs the engine, writes the history (the runtime's user as actor) and saves in a transition save scope of its own: steps 1 to 13 without the access gate.
  - Only a system-driven step with no delete state runs this way. Without an `AfterCommitWork` (its optional last argument) it runs no cascade, no notice and no after-commit work (`WorkflowService.SystemStepProblem` names what breaks the rules; `SystemStepProblemTests`). Anything else throws, and a refusal throws `SystemStepRefusedException`, so the caller's write rolls back whole (`SystemStepTests`).
  - With an `AfterCommitWork` (an integration handler passes its delivery's `IntegrationDelivery.AfterCommit`, a follow-up its `StepFollowUpContext`'s) the step runs its cascades as its own plan, writes its notices as outbox rows in the caller's transaction, and leaves their dispatch and its after-commit work to the caller, which runs them once it has committed. A delivery rolled back after its step tells nobody, and its retry moves the record once (`OrderSyncTests`).
  - Its callers are `ResourceLinker`, `JobClock`, `JobStateSyncHandler`, `OrderCancelHandler` and `SlotGroupFollowUp` (`StaffRecordScanTests`). The job clock runs it in a transaction of its own, at read committed, that takes the job's step locks, reads the job under them and judges it again, as the tenant's system account.
  - It is the **only** runner of a system-driven step. The public paths (`TransitionEntityAsync`, `TransitionEntityToStateAsync`, `CreateAndTransitionAsync`, `SaveAndTransitionAsync`) refuse one before anything else is judged, whoever asks (`WorkflowService.SystemDrivenRefusal`), and the access answers (`GetAvailableTransitionsAsync`, `GetTransitionAccessAsync`) refuse it with the same reason (`JobClockTests`, `StepAnswerTests`).
- **`SaveEntityAsync(updater, ct)`** saves a `DetachedEntityUpdater<T>` on a fresh context and returns its `Result`; the entity's service checks the permission first.

## Sequence

```text
TransitionEntityAsync(entityId, transitionKey, queryConfigurator?, comment?, ct)
│
├─ 0. When an IStepLock is registered for the entity's type, open the step's transaction at read
│     committed and take the locks first, before anything of the step is read (a create: before its
│     references are read; a dialog's step: before its edits are saved). Each lock holds the
│     tenant's row (shared) before it is taken, so the order is the tenant's row, the locks, the
│     rows. Step 5 takes this transaction over.
├─ 1. Load the entity through the QueryConfigurator (WithWorkflowGraph), tracked (an untracked
│     entity, as a projection returns it, is refused). Load declared cascade navigations,
│     recursively, and the delete-state cascades of a child already in its delete state; a path
│     through references loads each reference, then the collection, and leaves the entity out.
│     Then every IStepPreparation registered for the type reads, on this context, what its
│     conditions need beyond the graph. With a dialog's edits it runs before they are saved, and
│     once they are saved each preparation's PrepareEditedAsync reads the row as edited;
│     ForgetEdited releases that once the step has run, committed or refused. A create calls
│     PrepareNewAsync once its references are loaded, under the step's locks. A condition that
│     finds nothing read fails closed.
├─ 2. Refuse unless State == transition.From
├─ 3. Access gate over the whole plan (see "Transition access gate"); the judged plan becomes the
│     environment's StepAccess. Then the held tenant (WorkflowService.HeldTenant), judged again
│     inside the transaction at 5, which decides
├─ 3a. A step with a required CommentPrompt is refused without a comment, or with one longer than
│      Comment.MaxTextLength, before anything is locked
├─ 4. workflow.GetBlockingReasons(entity, transitionKey, environment), else fail with them
│     (cascaded children's reasons included; a cascade cycle is a reason too). Edits that went
│     with a step other than the state's save are judged by that save's conditions too
│     (environment = injected TimeProvider, NavigationLoadState.From(db), service provider)
├─ 5. BEGIN TRANSACTION (or take over step 0's). The held tenant judged again inside it: the
│     record's tenant read under a shared key lock on its row kept to the end
│     (ApplicationDbContext.HoldTenantAsync), before anything is locked. A step moving the tenant
│     and this step are put in one order
│
├─ 6. workflow.GetAllCandidatesForTransition(entity, transitionKey, environment)
│     → LockCandidates, grouped by entity type (the primary entity, every entity a leave,
│     transition, enter or cascade action touches, recursively, and every child a delete cascade
│     finds already in its delete state with what that state cascades to). A candidate the
│     context does not track, or tracks only as added, is refused before the lock
│
├─ 7. For each entity type:
│     ├─ one ExecuteUpdateAsync: IsInTransition = true WHERE Id IN (...) AND IsInTransition = false
│     │    (each candidate's tenant named too under the platform lease); fewer affected rows than
│     │    entities means another transition holds a lock → fail
│     └─ sync the change tracker (see below) for every entity in the group
│     A TakesNoEdits step from a dialog: the stored row, read under the lock, compared with the
│     updater's snapshot (TransitionRefusals.ChangedSinceOpened)
├─ 7a. Delete blockers: every IDeleteBlocker registered for the type of each entity the step would
│      move into a [DeleteState] is asked on this context; a reason refuses the whole step
│
├─ 8. workflow.ExecuteTransitionAsync(entity, transitionKey, environment, comment, ct):
│     leave actions → transition actions → enter actions (incl. cascades, each child running its own
│     transition) → State = To, IsInTransition = false. Before each step, cascaded ones included,
│     the engine asks environment.StepAccess; a step outside the judged plan refuses the
│     transition. Returns a TransitionOutcome: OutgoingMessages, AfterCommit, BeforeSave, and one
│     Step per entity whose state changed (primary first). The BeforeSave writes run next on this
│     context. Then every tracked state entity must be in its loaded state or have moved from
│     exactly that state, and no action may have written IsInTransition; otherwise the run fails
├─ 9. A comment given to a prompted step: CommentThreadWriter adds it, trimmed, to the entity's
│     thread (the prompt's Audience) as a TransitionNote naming the step, under the record's thread
│     lock, saved and rolled back with the step. An optional prompt left empty writes nothing
├─ 10. StateHistoryWriter.Add(context, outcome): one entry per entity whose state changed,
│      correlated with the primary's, actor from the runtime. Then OutgoingMessageWriter.Add: every
│      queued notice as a pending outbox row linked to the primary's entry
├─ 11. Clear IsInTransition on every other locked entity
├─ 12. For each entity now in a [DeleteState], in reverse order of reach (grandchildren first): run
│      the IDeleteCleanups registered for its type (or a base type or interface), then remove it
├─ 13. SaveChangesAsync inside context.BeginTransitionSaveScope(candidates.All, transition.Key),
│      which lets exactly these entities change State, IsInTransition and fields their state locks,
│      and lets the rows of their declared collections be written. Only allow-listed files name
│      BeginTransitionSaveScope (DataContextScanTests)
│      UPDATE ... WHERE State = X AND IsInTransition = 1 (matches because of the sync)
├─ 13a. Every IStepFollowUp registered for the type, on this context in this transaction, under the
│      step's locks, handed a StepFollowUpContext (the outcome, the history correlation id, and an
│      AfterCommitWork run after step 15): what the step calls for in records it does not own.
│      An exception fails the step (StepFollowUpRefusedException carries a reason the user reads)
├─ 13b. TenantChangeCollector.NameAffectedAsync: ids the change names beyond the rows saved (a
│      user's staff record, a work pattern's staff members, a customer's jobs, the staff members of
│      every named job's live assignments), read in this transaction; a failed read is logged and
│      never fails the step
├─ 14. COMMIT, with no cancellation token
│
└─ 15. After commit only: hand the outbox rows to the IOutgoingMessageDispatcher; run AfterCommit
       callbacks; publish the tenant change (the tenant and the ids every save of the step touched)
       on the TenantChangeBus, each subscriber told on the thread pool. A rollback, a refusal or a
       failed save does none of this.
```

The engine only names the candidates; `WorkflowService` locks them. Every state entity implements `IStateEntity` with a `Guid Id`, so one generic lock statement per entity type serves every workflow. Candidates are grouped by the model's entity type, so a proxy or an unmapped subclass locks with its entity type.

## Notices

A step notifies through an action, never a service ([ADR 0011](../../../docs/adr/0011-notifications-from-workflow-after-commit.md); [docs/features/notifications-and-comments.md](../../../docs/features/notifications-and-comments.md#notifications)).

- `NotifyAction<TState, TEntity>(type, values, recipients, recipientPermission, when)` (`Staffora.Core/Messaging/`) fills its `MessageType`'s catalogue template from the entity in memory (`MessageCatalogue.Render`) and adds the `OutgoingMessage` to the context's `OutgoingMessages`. Its optional `when`, judged on the step's context, queues the notice only for the runs it holds for. It names no lock candidate.
- At step 10 `OutgoingMessageWriter` adds every queued notice as a pending outbox row linked to the primary's history entry, so a rollback drops it (`MessagingPipelineTests`).
- At step 15 the dispatcher (`OutgoingMessageDispatcher`) sends each row on the channels `MessageChannelRouter` picks (`InAppChannelSender`: the active users of the entity's tenant among the named people and the permission's holders, once each). A failure there is logged and the row marked not delivered with its retry due, never reported as the step's. The hosted dispatcher retries after back-off, only while the tenant's data is not held.
- On the platform console the step's rows and the delivery's are stamped with the tenant's system account.
- `MarkNoticesReadAction<TState, TEntity>(types)` marks what earlier notices asked for as read for every recipient ([ADR 0038](../../../docs/adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)). It resolves `IHandledNotices` from the step's scope (a step run where none is registered fails), adds a `BeforeSave` write that marks the unread copies of those kinds about the entity, and pushes `NoticeArrived.Read` after commit. It names no lock candidate.

## Delete blockers

An `IDeleteBlocker` (`Staffora.Services/Workflow/IDeleteBlocker.cs`) is a dependent that vetoes a state entity's delete with a reason the user reads, rather than being removed with it (`IDeleteCleanup`) or moved by a cascade. Examples: role holdings on a unit (`RoleHoldingsBlockUnitDelete`), a role's holders (`RoleHoldersBlockRoleDelete`), the comments of a commented record (`CommentThreadsBlockDelete`, registered for `ICommentedEntity`: comments are never deleted).

- A later entity that refers to an existing one adds a blocker of its own; the existing entity's workflow does not change.
- Each names its entity type (a base type or interface covers every assignable type) and the dependent types it counts (`Dependents`, which the delete-coverage model test counts as covered), and answers `FindUseAsync(context, entity, ct)` with a sentence naming what uses the entity and what to do instead, or null. It only reads, in the runtime's tenant or the step's platform lease, and tells nothing of another tenant's rows.
- `WorkflowService` asks every registered blocker when it runs the step (step 7a: inside the transaction, after the lock, before anything changes) and when the footer reads the steps, so a blocked delete is drawn greyed with the reasons. A dialog never judges a delete in memory.
- A writer that adds a row a blocker counts takes the same lock as the step, so the answer holds until the commit.
- Register each in `AddWorkflowServices`. Prove the veto with a blocker of its own (`OrganizationUnitLifecycleTests`).

## Step locks

An `IStepLock` (`Staffora.Services/Workflow/IStepLock.cs`) is a lock every step of one entity type takes before anything of the step is read: the lock the other writers its conditions depend on take too, so the step is judged, run and saved on rows nobody can change meanwhile. A tenant entity's step locks are registered once in `AddWorkflowServices` with `services.AddStepLock<TEntity>(AppLock...)` (a `TenantStepLock<TEntity>`, which takes them in rank order through `AppLock.TakeInOrderAsync`); a tenant's create takes `AppLock.TenantCreate` through `TenantCreateStepLock`. Which types take which lock, and the rank order, is in the `tenant-isolation` skill, "Lock order".

- `WorkflowService` opens the step's transaction at read committed, takes every lock registered for the type, and only then loads the entity, runs the preparations (so a guard reads its facts under the lock), loads the cascades and judges the conditions (step 0).
- Each `AppLock` holds the tenant's row first, in every scope.
- A lock not granted in time throws `AppLockBusyException` (the structure lock an `OrganizationStructureException`), and the step is refused with the lock's `BusyReason`.
- Code that runs a step's work outside `WorkflowService` (the job clock, an integration handler) takes the same registered `IStepLock`s for the type before it reads the record.
- The footer's reads take no lock: they only show, and the step judges again under the lock.

## Transition access gate

The gate decides who may run a transition, the same for running it and for `GetAvailableTransitionsAsync`, so a footer never offers what the server refuses ([docs/authorization.md](../../../docs/authorization.md)).

- It covers the whole plan: the transition and every transition its cascades run on children and grandchildren, each judged as if the user ran it alone. A step whose cascade runs a child step the user may not take is refused with a reason naming it ("The step 'x' also takes the step 'y' on a Child. …"), and `GetAvailableTransitionsAsync` answers `MayRun = false` with the same reason.
- `RequiresPlatformOwner` runs only with platform reach (`PlatformScope.HasPlatformReach`: the `Admin` role and the platform tenant, not impersonating), so neither a holder of every tenant permission nor the `Admin` role outside the platform tenant runs one. `RequiredPermission` needs that permission through `PermissionService`. A step with neither is open to every signed-in user of the entity's tenant.
- The gate runs before any lock. `PermissionService` caches the user's permissions for the scope.
- The engine is held to the judged plan through `WorkflowEnvironment.StepAccess`: a step outside it (one an action caused by changing a child before its cascade ran) refuses and rolls back the whole transition. After the engine has run, the service refuses the run when an action wrote `State` or `IsInTransition` itself, so a history row's From is always the state the engine read.

**Held tenant.** After the gate, a record of a tenant whose data is held (`TenantStates.HoldsData`; a tenant the context cannot see counts as held) takes no step, whoever asks: `WorkflowService.HeldTenant`. The footer and a create's footer answer with the same reason, and a create is refused with its insert rolled back. A `Tenant` belongs to no tenant, so its own lifecycle steps stay open (reactivate, cancel deletion). Setup is not held. The decisive read is inside the step's transaction (step 5), under the shared lock on the tenant's row that a step moving the tenant waits for; on the platform console, whose writes run in system scope, this read is the one that holds the record. Writes outside a step are held by the data context itself (the `tenant-isolation` skill, "Held tenant"). Suspend and Schedule deletion (from Active) also sign every user of the tenant out: an action queues `ITenantSignOut` as a `BeforeSave` write that rotates every user's security and concurrency stamps, stamped by the tenant's system account; a step run where no sign-out is registered fails.

**Platform reach.** For a runtime with platform reach the service works under the platform lease (`BeginPlatformScope`), so a platform owner moves a tenant and an entity of any tenant from the console. What such a step writes into another tenant is written on that tenant's behalf: `StateHistoryWriter` stamps its history with that tenant's system account (`ActorId` keeps the owner), and the service stamps every other row it adds or changes there with the same account before the save.

## The save step

A state where the user edits data marks one self-transition `IsSave = true`: the only step a dialog runs without a click, when it must settle the user's edits before it shows something else (the leave rule, [DIALOGS.md](DIALOGS.md#views-and-sessions)). `Workflow.GetSaveStep(state)` (and `IWorkflow.GetSaveStep`) names it, or null; a state without one makes the dialog ask whether to discard the edits.

- Registration refuses a designation that could do more than store the edits: two saves from one state, a save that leaves its state, asks a confirmation or a comment, runs an action (its own, or its state's leave or enter actions), is system-driven, or is reserved to the platform owner (`WorkflowRegistrationTests`). Such steps (the tenant's saves) stay undesignated, and their dialog asks.
- The save is also the step whose access decides who may store edits in its state (`Workflow.GetEditAuthority`).
- When the dialog runs the save step it goes through `SaveAndTransitionAsync` like any step: gate, conditions, field enforcement and history all apply.

## Failures

**No savepoints.** Every connection string enables MARS, under which SQL Server allows no savepoints, so a failed save inside the step's transaction cannot be rolled back on its own. Nothing inside a transaction catches a failed `SaveChanges` to continue writing: a failure ends the step and the transaction rolls back whole. Ensures called inside a caller's transaction check before they insert instead of catching a duplicate (`SystemAccountService`, `CompanyRoot`, `TenantSettingsEnsureService` keep their catch only outside a caller's transaction, at startup). EF's savepoint warning (30004) is configured down to Debug in `ApplicationDbContextOptions` for that reason.

**Every failure is a failed `Result`.** A refusal carries its reasons. An unexpected exception anywhere (the load, the graph, the cascades, a condition, the permission query, the lock, an action, the save) is logged and answered with a message that reveals nothing about it: "The step 'x' could not be completed, so nothing was changed.", or "The steps of the X could not be read. Try again." from `GetAvailableTransitionsAsync`. `SaveEntityAsync` answers "The X could not be saved, so nothing was changed. Reload it and try again."

**Cancellation.** Only the caller's own cancellation is rethrown, while its token is cancelled; an `OperationCanceledException` the caller did not ask for (a SqlClient command timeout) is a failure like any other. The commit runs with no token, and the dispatch and `AfterCommit` work run with `CancellationToken.None`: a committed step stands whatever the caller does, and a failure after it is logged, never reported as the step's.

## Change tracker sync after the lock

`State` and `IsInTransition` are concurrency tokens on every state entity, configured by `ConfigureStateEntities()` in `Staffora.Data/Configurations/StateEntityConfiguration.cs`, so EF puts their **original** values in the `WHERE` clause of every `UPDATE`. The lock in step 7 writes `IsInTransition = true` with `ExecuteUpdateAsync`, which bypasses the change tracker; EF still believes the original value is `false`.

Without the sync, the save matches no rows:

```sql
UPDATE ... SET State = @new, IsInTransition = 0
WHERE Id = @id AND State = @old AND IsInTransition = 0
```

The database holds `IsInTransition = 1`, so EF throws `DbUpdateConcurrencyException`. After locking, `LockAsync` in `WorkflowService.Locks.cs` sets the original value on every locked entity's entry:

```csharp
foreach (var entity in entities)
{
    db.Entry(entity).Property(nameof(IStateEntity.IsInTransition)).OriginalValue = true;
}
```

The string-based `Property(...)` is required: the candidates are `IStateEntity` values, which yield a non-generic `EntityEntry`. This applies to every locked entity, not only the primary one, and to any future raw update of a concurrency-token column on a tracked entity.
