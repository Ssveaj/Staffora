# Comment threads and notifications

People talk in comment threads on the records they share, and hear about what happened through in-app notices. Who may read and write each thread, and who receives a notice, is set by permissions ([authorization.md](../authorization.md#comment-threads-the-permission-is-passed-in), [Notices](../authorization.md#notices)).

## Comment threads

[ADR 0010](../adr/0010-comment-threads-per-audience.md). A record people write on implements `ICommentedEntity` and gets one thread per audience (`CommentAudience`: Internal, Staff, Customer). Each thread is a `CommentContainer`, created with its first comment, holding `Comment` rows that are never edited or deleted ([database.md](../database.md#commentcontainer)). The thread a comment is in decides who reads it; no comment carries a visibility flag.

### CommentService

`CommentService` (`Staffora.Services/Comments/`):

- `GetThreadsAsync<TEntity>(id, audiences, ct)` answers the threads the reader may read, in the order given. Each thread carries its newest 200 comments oldest first (`CommentService.MaxThreadComments`), its total, whether the reader may post, and its times on the organization's wall clock (`TenantClock`).
- `AddCommentAsync<TEntity>(id, access, text, ct)` posts at once and answers the thread as it is now. The text is trimmed, must not be blank and holds at most 2000 characters.
- A post holds the record's row from its read to its commit (repeatable read) and writes under the record's thread lock (`CommentThreadWriter.LockAsync`, an application lock owned by the transaction). A step's note and the delete blocker take the same lock. So two first comments at once (two posts, or a post and a note) create one thread, and a delete never leaves a thread without its record.
- Only `CommentThreadWriter` constructs a comment or a thread.
- The entity's own service passes each thread's permissions with every call ([authorization.md](../authorization.md#comment-threads-the-permission-is-passed-in)) and exposes them to its dialog as `Get{Entity}CommentsAsync(id, ct)` and `Add{Entity}CommentAsync(id, audience, text, ct)`.

### The comment prompt

A workflow step may ask for words through `TransitionCommentPrompt`:

- When the prompt is `Required` (the default), the server refuses the step without an answer. Otherwise the answer is optional ([ADR 0035](../adr/0035-absence-questions-are-workflow-steps.md): an absence's Answer runs without words and then writes nothing).
- An answer longer than a comment holds is refused either way.
- `WorkflowService` writes the answer into the entity's thread (the prompt's `Audience`, Internal by default) as a `TransitionNote` naming its step, inside the step's transaction, so it commits or rolls back with the step ([TRANSITION-ORCHESTRATION.md](../../.claude/skills/workflow-state-entities/TRANSITION-ORCHESTRATION.md), step 9).
- Registration refuses a prompt on a workflow whose entity has no threads, and on a step into a delete state.
- The prompt dialog takes no more than a comment holds (`PromptAsync(..., maxLength)`): the text area's `maxlength`, a count under it named to the box, and a longer answer refused before it is sent. The transition button passes the prompt's own `Required` and `RequiredMessage`; the dialog shows the message under the box only after an empty send.

### Closed threads

The entity's service may say a thread is closed to the reader (`CommentThreadAccess.ClosedReason`, decided from the record as the call reads it). The thread is still read, its `CanPost` is false, the side column shows no composer, and a post is refused with the reason. The staff thread of an assignment while its request is open is one ([Assignments](jobs-and-staffing.md#assignments)).

### The app's commented records

- **Absence.** One thread, the staff thread, read by the absence's staff member and the approvers. It is written only through the steps' prompts (the questions, the answers, a rejection's reason) and is closed to free comments. Any other reader gets no column, and so does everyone while it is empty.
- **Assignment.** The staff thread ([Staff conversations](#staff-conversations)). Closed to free comments while its request is open ([ADR 0037](../adr/0037-request-questions-are-workflow-steps.md)); its questions and answers are written by the request's question steps.
- **Job.** The internal thread, the coordinators' notes: read with `ViewCoordination`, written with `PerformCoordination` (`JobService.InternalThread`, `GetJobCommentsAsync`, `AddJobCommentAsync`). Anyone without a permission of the Jobs or Coordination area, a staff member first, is refused before anything is read. Notes are never notified and never published on the change channel. They show as note cards under "Notes for the coordinators only". The job's cancel steps write their reason into this thread.
- **Customer thread.** A job a portal sent, and its request, share the customer thread ([Customer portal](customers-and-portal.md#customer-portal), "The customer thread"): the job's Customer thread and the request's "Messages with supplier". `CommentThread.Title` and `Readers` give a thread a name of its own where its audience's name does not fit the reader. Each comment is copied into the other tenant, its author named as that tenant knows them (`CommentAuthorType.Contact` and `Supplier`, `Comment.AuthorContactId` and `AuthorSupplierId`).

### Delete

A record whose threads hold a comment is not hard deleted ([ADR 0016](../adr/0016-deletion-cascades-through-child-workflows.md)). `CommentThreadsBlockDelete` refuses the delete with its reason, and the footer draws the step greyed with it. Every commented entity is a state entity, deleted only through a step that asks the blocker.

### CommentBubbleList

`CommentBubbleList` (`Components/Common/Comments/`) is the one comment block; every thread of the app renders through it (the side column and the job's Staff tab). It is an ordered list named by its label.

- **Conversations** (the staff and customer threads) read as a chat. The reader's own bubbles sit on the right, in a shade of the dialog's state colour, tail at the top right. Everybody else's sit on the left on the sunken grey (white on the side column's tint), tail at the top left. A bubble takes up to 85 % of the column (520 px).
- The bubble's top line holds the author and the time of day, the time as a `<time>` element with its full instant in its title. A line with the day goes where the day changes ("Today", "Yesterday", "Tuesday 10 November", with the year when it is another), on the organization's calendar and the injected clock (`CommentDays`).
- **Notes** (an internal thread, `AsNotes`) are flat cards across the column, the reader's own marked by a bar on its left, each with its day and time ("today 08:12"). A note is never mistaken for a message a staff member or customer reads.
- Each comment says "(you)" for the reader's own to a screen reader. A comment written with a step reads as any other (author, time, text), never the step's name.
- A line in the warning tone goes before the first comment the reader has not seen or answered (`MarkedCommentId`, "New", "Unanswered").
- A note says when only the newest are shown ("Showing the newest 200 of 250 comments."). An empty thread shows a round icon and its words.
- The list scrolls in a box of its own that starts at the newest comment and is in the tab order, so the composer stays in reach under a long thread.

The composer, shown when the reader may post:

- One rounded field that grows with the text, its round send button ("Post") inside. Enter posts and Shift+Enter starts a new line ([comment-composer.js](../../Staffora.Blazor/wwwroot/scripts/comment-composer.js) keeps Enter from breaking the line); a hint under it says so on a device with a keyboard.
- The text area is named by a label. It keeps the text on a refusal (an alert). Once posted it empties, keeps the focus, and a status region says "Comment posted.". The same region announces a comment someone else wrote that a new read brought ("New comment from Ada Example.").
- The draft belongs to the caller when it keeps one (`Draft`, `DraftChanged`).

### The side column

`StateEntityDialogSide` and `DialogSideTabs` ([DIALOGS.md](../../.claude/skills/workflow-state-entities/DIALOGS.md), "Comment side column") draw the threads beside a state-entity dialog's body, on the light tint of the dialog's state colour.

- Folder tabs on a baseline, one per thread the reader may read: its audience's icon, named in words with its count and attention ("Internal, 3 comments, awaits your reply"), its count as a round badge over its corner, in the warning tone when the thread needs the reader.
- A heading line under the tabs: the audience's icon, the open tab's name and its count (in the state pill's colours), what needs the reader ("Awaits your reply", "New reply") over a hairline of the state's colour, and who reads it in a quiet line ("Notes for the coordinators only", "Between the staff member and the coordinators"). The shown thread follows through `CommentBubbleList`.
- A context may add a conversations tab (`LoadConversationsAsync`: the job's Staff tab).
- Each thread's draft is kept on the context, in the circuit only, until it is posted or the dialog closes. A text typed in one thread survives a switch to another tab and back and never carries over into another thread.
- The context reads the threads beside the entity on every load of a saved row. A reader with no thread gets no column, a new record has none, and a failed read shows in the column, never as "could not open".
- A dialog opened at its default width (`StateEntityDialogOptions.Width` left null) opens at 720 pixels and widens to 1180 (`WithSideColumnWidth`, app.css) only while its reader has the column.
- Beside the body, the thread fills the column's height with the composer pinned under it. Under 768 pixels the column sits under the body, the thread's box stops at half the screen's height, and the composer sits right under it.

### Attention

- A thread whose entity keeps whose-turn timestamps ([Staff conversations](#staff-conversations)) answers its attention from them (`CommentThread.Turn`, `CommentTurn`: `RequiresAttention`, `AttentionText`, `FirstMarkedCommentId`).
- Any other thread keeps the generic badge (`CommentThread.AwaitsReply`): its last comment was written in the thread by another user, or crossed from the other side of the customer thread. A step's note or the reader's own last word needs no reply.
- The customer thread is judged by side instead (`CommentThread.JudgedBySide`, set by its two services): only the other organization's last word awaits the reader.
- There is no per-user read table.

### Effects of a post

The comment service is the one source of notices besides the workflow ([ADR 0011](../adr/0011-notifications-from-workflow-after-commit.md)). An entity's service may pass what a post in one of its threads does besides the comment (`CommentThreadAccess.Effects`, `CommentPostEffects`):

- **The claim** is the first statement of the post's transaction: one `UPDATE` of the record's row that stamps whose turn it is and holds the row until the commit. Two posts at once are written in one order, and neither waits on a lock the other holds.
- While holding it, the post reads no other row: what its notices and change name is read before the transaction. A step that holds a parent's row before the record's (a job's cancel cascading into its assignments) never waits on it in a ring.
- **Its notices** are outbox rows of the same transaction linked to the comment (`OutgoingMessageWriter.AddForComment`), delivered only after the commit.
- **Its change** is published on the tenant change channel after the commit.

A thread without effects (an internal one) stamps, tells and publishes nothing.

### Staff conversations

An assignment's thread, between that staff member and the coordinators, is kept by `StaffConversationService` (`Staffora.Services/Jobs/`): its read and post, whose turn it is, the seen marks and the notices. Each method first checks that the reader may open the assignment, as the assignment service judges it. The assignment dialog reaches it through `SlotAssignmentService.Conversations`, the job dialog through `JobService.Conversations`.

**Whose turn.** Whose turn it is lives on the assignment as UTC timestamps on the injected clock ([database.md](../database.md#slotassignment)), judged by `StaffConversation`:

- A staff member's comment stamps their side; a coordinator's reply, or "handled", stamps the coordinators' side. A step's note stamps nothing.
- Each stamp is a millisecond after the other side's when the clock has not passed it, so the side that wrote last always has the later stamp. A coordinator's stamp is also after the staff member's last read, so a reply is never taken for seen whatever the clock did.

**For a coordinator** the thread needs attention while the staff member wrote last ("Awaits your reply"). The line "Unanswered" goes before the staff member's first comment stored after the coordinators' last one, judged by the comments themselves, since a post stamps a moment before its comment is stored. The attention clears with their reply, or, when the message needs no words, with "Mark as handled" on the yes or the assignment ([ADR 0038](../adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md)):

- The steps `assignment-accepted-message-handled` and `assignment-assigned-message-handled` are self-steps with `PerformCoordination`, never on one's own assignment, offered only while the message waits (`AwaitsReplyReason`) and while the job is being staffed.
- Marking handled stamps the coordinators' side as a reply would, writes nothing in the thread and tells nobody. It moves the staff member's seen mark with it unless a real reply is still unseen.

**For the staff member** the thread needs attention while a coordinator wrote after they last read it ("New reply", marked "New"). Reading it is seeing it: their read stamps what they have seen before the thread is read, and never while a platform owner impersonates them, as for notices.

**Notices.** A staff member's comment tells the job's coordinator and whoever made the assignment (`assignment-staff-wrote`), each only while they hold `ViewCoordination` as it is delivered, never the poster. A coordinator's comment tells the staff member (`assignment-coordinator-wrote`) while they hold `ViewOwnAssignments`. A notice names who wrote about which job and slot, never the words; the thread shows them on a read that checks the reader again. Each post publishes the job and the staff record on the tenant change channel, so the workspace and the staff member's own pages read again.

**Where conversations show:**

- The assignment's dialog: the side column's Staff tab, with its marks.
- The job dialog's **Staff** tab (`StaffConversationsPanel`, `StaffConversationService.GetJobConversationsAsync`, `ViewCoordination`). One row per staff member asked, who said yes or was assigned, or whose thread holds a comment, with the slot and state, the newest comment's time and start (`StaffConversationRow`) and an unread mark while it waits. Waiting rows come first and the tab's badge counts them in the warning tone.
  A row opens the conversation in the tab, with "All staff members" to go back, and posts there. The tab is read again whenever a dialog stacked on the job closes, since a request or a reply made there changes nothing of the job itself (`StateEntityDialogContext.RefreshConversationsAsync`).
- "My assignments" ([Staff pages](coordination-and-homes.md#staff-pages)).

Verified by: `StaffConversationTests`, `StaffConversationDialogsTests`, `StaffConversationRulesTests`, `CommentBubbleListTests`, `CommentDaysTests`, `CommentConstructionScanTests`, `StateEntityDialogTests`, and the journey `tests/e2e/conversations.spec.ts`.

## Notifications

[ADR 0011](../adr/0011-notifications-from-workflow-after-commit.md). A notice comes from a workflow step, after its commit, and reaches people in-app through the bell in the header bar of both layouts. E-mail, SMS, digests and per-type preferences are planned behind the same router and catalogue ([roadmap](../roadmap.md)).

### Message types and the catalogue

In `Staffora.Core/Messaging/`:

- A `MessageType` is one kind of notice: a durable key (stored with every notice, never renamed), a `MessageCategory` (Account, Staffing, Absence, Ordering, Conversation; the bell shows its icon) and the placeholders its template may use.
- The app's types are `public static readonly` fields of `MessageTypes` (`MessageTypes.All`). Each has exactly one `MessageTemplate` (subject, text, link) in `MessageCatalogue.Templates`, in code.
- A template names only its own type's placeholders as `{{Name}}`, has a subject and a text, and links only inside the app: a page (`/me/overview`) or a dialog over the current page, written as the dialog address (`?dlg=user-{{UserId}}`).
- `MessageCatalogueScanTests` finds every declared type by reflection over the product's assemblies and fails on a type without a template, a template of an undeclared type, a foreign or misspelt placeholder, a blank text or a link out of the app.

### Filled in memory, once

The workflow action `NotifyAction<TState, TEntity>(type, values, recipients, recipientPermission, when)`:

- Its optional `when` is judged on the step's context as the action runs and queues the notice only for the runs it holds for. A planned leave's request and a sick leave's report are two kinds of notice of one step, and a cancellation tells the staff member only when someone else cancels.
- It reads the placeholders' values from the entity in memory when the step runs. With `withComment` ([ADR 0035](../adr/0035-absence-questions-are-workflow-steps.md)) the step's prompt words are appended to the text as a paragraph of their own (`MessageCatalogue.WithWords`), which the bell shows with its line breaks.
- It fills the template (`MessageCatalogue.Render`): a value as plain text, escaped as one part of the address in a link. A placeholder or value that is not the type's own is refused, and so is a link that leaves the app once filled. Subject and text are cut to 200 and 2000 characters.
- It queues the filled notice on the step's context.

What a recipient reads is what was true when the step ran; no notice is filled again later.

### The pipeline

In `Staffora.Services/Messaging/` ([TRANSITION-ORCHESTRATION.md](../../.claude/skills/workflow-state-entities/TRANSITION-ORCHESTRATION.md), steps 10 and 15):

1. `WorkflowService` writes the queued notices as `OutgoingMessage` outbox rows in the step's own transaction, linked to the step's history entry (`OutgoingMessageWriter`), so a rolled-back step leaves none.
2. After the commit it hands them to `IOutgoingMessageDispatcher` (`OutgoingMessageDispatcher`), which sends each on the channels `MessageChannelRouter` picks (in-app for every type).
3. `InAppChannelSender` delivers a row once: one `StoredMessage` and one `MessageRecipient` per person, the row marked delivered in the same save, then each person's open circuits are told through `NoticeBus`. A row already delivered is left as it is, and the unique key of `StoredMessage` on its outgoing message refuses a second copy.

Failures and retries:

- A failed delivery is logged as an error and the row marked not delivered; the step stands. A row left pending (the process stopped between the commit and the dispatch) stays pending.
- Both are delivered again by the integration outbox's hosted dispatcher ([Integration outbox](operations.md#integration-outbox)): a failed row after its back-off (`OutgoingMessage.Attempts`, `NextAttempt`; 30 seconds, doubling, at most an hour, `IntegrationRetryRules`), a pending one once it is a minute old (`IntegrationRetryRules.NoticeGrace`, so it never races its own step's delivery). Each is claimed first (its attempt count is a concurrency token), delivered as the tenant's system account, once (the stored message's unique key), and given up, not delivered, after eight attempts.
- A notice of a tenant whose data is held (Suspended, Pending deletion or Archived, `TenantStates.HoldsData`) is sent on no channel and stays pending, the hold logged as a warning. The hosted dispatcher delivers it only once the tenant is Active again (an archived tenant's never), without spending an attempt. No step of such a tenant's records runs anyway ([authorization.md](../authorization.md#tenant-states-and-access)), so only a step that committed just before the tenant was suspended meets the hold.

### Recipients

`InAppChannelSender.ResolveRecipientsAsync` delivers to:

- the people the action names (only those who hold its `namedRecipientsHold` permission as it is delivered, when it names one);
- every holder of the permission it names;
- when the named people resolve to nobody as it is delivered, every holder of its `fallbackPermission` (`OutgoingMessage.FallbackPermission`). A portal's cancellation and a customer's message go to the job's coordinator while they coordinate, to every holder of `PerformCoordination` otherwise; the staffing company's message goes to the orderer, or else to the portal's customer administrators.

Recipients are only the active users of the entity's tenant, never a system account. A holding counts as it does for its holder's permissions (on a unit in use, of an active role). So a notice never crosses tenants, and a platform owner's step on the console notifies the tenant's people only, delivered on the tenant's behalf ([authorization.md](../authorization.md#notices)).

### Written on another tenant's behalf

A customer portal's send writes the job it becomes into the staffing company, and with it that job's notice to the coordinators there (`job-requested`, category Ordering):

- The step's follow-up fills it from the catalogue and adds it with `OutgoingMessageWriter.AddOnBehalfOf`, pending, linked to the job's first history entry (`job-receive`) and created by the staffing company's system account, in the send's transaction.
- After the commit, the send's after-commit work hands it to the dispatcher in a service scope of its own acting as that system account (`OrderSender.DeliverAsync`), so it is read, delivered and marked in the staffing company's tenant.
- One that cannot be delivered then stays pending, and the hosted dispatcher delivers it once it is a minute old. The job's history lists it as any step's notice.

### History

Each step of an entity's history lists the notices it sent, by type, with channel and delivery ("Account reactivated: delivered in-app to 1 person", "nobody to deliver to in-app" when no recipient was an active user, "pending", "not delivered"), naming no recipient. `StateHistoryNotice` is shown by `StateHistoryStepView` in the state pill's card and in the full history as a quiet line ("Notice: Account reactivated · delivered to 1 person", its `Outcome`).

### The bell

`NotificationBell` (`Components/Account/NotificationBell.razor`, hosted by `AccountMenu`):

- **Shown** in a circuit only (from its first render; a page rendered on the server alone has none), for a session with a user. Not shown to a platform owner on the platform console, to whom no notice is ever delivered (`NoticeService.HasNotices`), so the console's bar keeps to one line on a phone.
- **The button** is named with the count ("Notifications, 2 unread") and carries a badge (99+ above 99). A polite live region says "New notice. 2 unread." on every notice that arrives, whether or not the count rose (another page may have marked notices read meanwhile), and nothing else. When a coordinator's step settles notices for everyone ([Settled for everyone](#settled-for-everyone)) the bell reads its count again and says nothing.
- **Live.** The circuit subscribes to the per-user bus through `NoticeService.Subscribe`, so a notice delivered to the person raises the badge without a reload. The bus is in-process: one app instance only (KI-69).
- **The panel** is a disclosure (`aria-expanded`, `aria-controls`), not a modal: a drawer at the window's right under the top bar, 430 px wide with the drawer shadow, the whole width below the bar on a phone. The page beside it stays in reach.
- **Focus.** The panel follows the button in the tab order. Escape (on the button or in the panel) and its close button ("Close notifications") close it and return the focus to the button. Mark all read moves the focus to the panel's heading. A notice that opens nothing returns the focus to the bell.
- **Content.** The head says "Notifications" over the count ("All read", "2 unread"). The panel shows the newest 50 notices, Unread or All (a segmented pair of pressed-state buttons), and Mark all read. An empty view is a compact empty state, a load the inline loader.
- **A notice** is a button: it marks itself read and opens its link, a page through the router or a dialog over the current page through `DialogRouteRegistry` (the same id-only opener a list row and a pasted link use; the dialog's own load refuses a row the reader may not see). Subject and text are text, never markup.

### NoticeService

`NoticeService` reads and marks the session's own notices only (the effective user's, in the runtime tenant): `GetUnreadCountAsync`, `GetNoticesAsync(filter)`, `MarkReadAsync(id)`, `MarkAllReadAsync()`, `MayMarkRead`, `HasNotices`.

- Marking is one `UPDATE` over the reader's own unread copies in the tenant (allow-listed in `DataContextScanTests`; `BulkUpdateGuard` lets it set only the read flag, the read time and the editor stamp), so Mark all read loads nothing however many are unread.
- A notice's rows are kept as written: the data context refuses rewording an outbox row, any change of a delivered notice, and any change of a copy but its read flag and read time ([database.md](../database.md#storedmessage)).
- While a platform owner impersonates, the bell shows the impersonated person's notices and marks none read ([authorization.md](../authorization.md#notices)).

### Settled for everyone

[ADR 0038](../adr/0038-a-question-from-staff-is-marked-handled-for-every-coordinator.md). A coordinator's step that settles a staff member's question or message marks read the notices it raised, for every recipient:

- The request's and the absence's "Answer the staff member" and "Mark as handled", and the conversation's "Mark as handled", through the workflow action `MarkNoticesReadAction`. The kinds each entity settles: `SlotAssignmentWorkflowAttribute.StaffNoticeTypes` (`assignment-question-from-staff`, `assignment-staff-wrote`) and `ResourceAbsenceWorkflowAttribute.StaffNoticeTypes` (`absence-question-from-staff`).
- A coordinator's reply in an assignment's conversation, through the same service inside the comment's transaction.

`IHandledNotices` (`HandledNotices`) marks, on the step's own context and in its transaction, the unread copies of those kinds about that record in its tenant. It is one `UPDATE`, allow-listed in `DataContextScanTests`, setting the read flag, the read time and the editor stamp (the step's actor). After the commit it tells each reader it marked on the notice bus (`NoticeArrived.Read`), so an open bell reads its count again and announces nothing. A rolled-back step marks nothing.

### Adding a notice

1. Declare the type in `MessageTypes`: a new key, its category, its placeholders.
2. Add its template to `MessageCatalogue.Templates`.
3. Put a `NotifyAction` on the step that sends it, naming the recipients by id or by permission.

The scan holds the template to the type; the step's history shows the delivery.

### The notices of the app

| Category | Type | To | Links |
|---|---|---|---|
| Account | `user-reactivated` | | |
| Absence | `absence-leave-requested`, `absence-sick-reported` | the approvers | the absence's dialog |
| Absence | `absence-registered`, `absence-approved`, `absence-rejected` (says its reason is written on the absence), `absence-canceled` | the staff member | the absence's dialog |
| Absence | `absence-question-to-staff` (with the prompt's words) | the staff member | the absence's dialog |
| Absence | `absence-question-from-staff`, `absence-answer-from-staff` (with the prompt's words), `absence-canceled-by-staff` | the approvers | the absence's dialog |
| Staffing | `assignment-requested`, `assignment-assigned`, `assignment-not-selected`, `assignment-canceled` | the staff member | `?dlg=assignment-{id}` |
| Staffing | `assignment-accepted`, `assignment-declined`, `assignment-withdrawn` | the job's coordinator and whoever made the assignment | `?dlg=assignment-{id}` |
| Staffing | `assignment-taken-off` (names the absence it was taken off for) | the staff member | `?dlg=assignment-{id}` |
| Staffing | `assignment-question-from-staff` (with the prompt's words) | the job's coordinator and whoever asked; every holder of `PerformCoordination` when neither still coordinates | `?dlg=assignment-{id}` |
| Staffing | `assignment-answer-to-staff` (with the prompt's words) | the staff member | `?dlg=assignment-{id}` |
| Conversation | `assignment-staff-wrote` | the job's coordinator and whoever made the assignment | `?dlg=assignment-{id}` |
| Conversation | `assignment-coordinator-wrote` | the staff member | `?dlg=assignment-{id}` |
| Conversation | `job-customer-wrote` | the job's coordinator; every holder of `PerformCoordination` while the job has none who could read it | the job's dialog |
| Conversation | `order-supplier-wrote` | the request's orderer; the portal's customer administrators while the orderer is inactive or cannot read requests | the request's dialog |
| Ordering | `job-requested` | the staffing company's coordinators ([Written on another tenant's behalf](#written-on-another-tenants-behalf)) | |

See also [Absence](schedules-and-absence.md#absence) and [Assignments](jobs-and-staffing.md#assignments), "Notices".

- The conversation notices name no comment's words. Their outbox rows are linked to the comment that sent them rather than to a step's history entry.
- The customer thread's two are written by the copying handler inside the message's delivery, linked to the copy, and delivered after the delivery's commit.

Verified by: `MessageCatalogueScanTests`, `NotificationBellTests`, `NoticeRetryTests`, `WorkflowServiceTests`, `DataContextScanTests`.
