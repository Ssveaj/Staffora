# ADR 0036: A record's history is for owners, and the workflow inspector is gone

## Status

Accepted. Amends [ADR 0033](0033-custom-fields-have-a-lifecycle.md).

## Context

A state-entity dialog's state pill opened a card with the state's description, the record's last steps, a link to the full history and a link to a workflow inspector: a structured list of the record's whole workflow with each step's permission and the outcome of each of its conditions. Whoever could open the record could read both. The inspector is not needed: the history shows who did what and when, and the footer shows what a reader can do. A record's history must be readable only by the tenant's system owners and the platform owner; ordinary users see only the steps. The full history dialog must also show states with the same colours and icons as the dialogs.

## Decision

- **No workflow inspector.** There is no "Workflow" link, inspector dialog or component, no `Inspect{Entity}Async` in any service, and no `WorkflowService.InspectTransitionsAsync`. The footer is the one place where a reader learns what a record can do next: the steps they may take, disabled with their reasons when blocked, and the next-step hint.
- **History for owners only.** A record's history (who took which step, and when) is read only by an active system owner of the record's organization (`SystemOwners.IsActiveOwnerAsync`) or by a platform owner on the platform console (platform reach, not impersonating). A platform owner who impersonates reads what the impersonated user reads. A customer portal's tenant has no system owner, so only the platform owner reads a portal record's history.
- **Enforced in one place.** `StateHistoryService.GetHistoryAsync` refuses everyone else with `StateHistoryService.OwnersOnly`, before anything of the record is read, whatever the entity's own service allowed. `StateHistoryService.MayReadHistoryAsync` asks the same question without a record: a dialog asks it once as it first loads (`StateEntityDialogContext.MayReadHistory`) and offers the history only when the answer is yes.
- **What everyone else sees.** The state pill, its card's state and description, and the footer's steps. The card shows no last steps and no link, and the header no history button, so nothing asks for a history the reader may not read.
- **States look the same everywhere.** In the history, each step's From and To are small chips in the state's own `[StateStyling]` colour and `[Icon]`, with the text chosen by `ColorContrast.OnFill`, as the pill takes them. The full history dialog is framed in the record's current state colour, as the record's dialog is (`FormDialogOptions.StateColor`, `DialogStateColors`), instead of the accent of a dialog without a lifecycle.

## Rationale

The history answers who did what and when, and the footer answers what can be done next, so the inspector is not needed. Enforcing the owner rule in one service means no entity service can let a history through.

## Consequences

- [ADR 0033](0033-custom-fields-have-a-lifecycle.md)'s statement that a field's steps get the inspector no longer holds.
- A coordinator, an administrator who is not a system owner, a staff member and a customer portal's users do not see who changed a record or when. A system owner reads it for them when needed.
- The footer's answers (`GetAvailableTransitionsAsync`) have their own tests (`StepAnswerTests`).
- Tests that read a history as an ordinary user expect the refusal; those that need a history read it as a system owner (`TwoTenantScenario.MakeSystemOwnerAsync`) or as a platform owner.
- A service may still read history rows for its own derivations (for example "open again" in [ADR 0044](0044-taking-off-for-absence-and-open-again.md)); that is not the history view.

## Related Decisions

- Amends [ADR 0033](0033-custom-fields-have-a-lifecycle.md).
- [ADR 0012](0012-platform-owner-and-tenant-onboarding.md): system owners and platform reach.
- [ADR 0024](0024-visual-language-tokens-and-state-colour.md): state colours and contrast.
