# Implementation plans

The documents in this folder are the implementation plans the product was built by. They are kept as a record of how the code reached its present form: the order of the work, what each step had to prove before it was ticked, and the tests that prove it.

They are history, not the authoritative description of the product. A plan describes the code as it stood when its step was done; later steps and changes may have replaced it. When a plan and a permanent document disagree, the permanent document is right.

| Plan | Covers |
|---|---|
| [Build plan](build-plan.md) | The product, phase by phase: the workflow engine, persistence, the common foundation, the platform level, job staffing, the customer portal, generic resource staffing and product depth |
| [UI plan](ui-plan.md) | The visual language, the app shell, the shared patterns and the views |
| [Code simplification and refactoring](simplification-plan.md) | The inventory and the refactoring of the services after the build plan |

Each step has a short description, a **Done when** line (what had to be proven), and where the step was built, a short **Built** line and a **Verified by** line naming the test classes and browser journeys that hold it.

## The permanent documentation

- What the product is: [PRODUCT.md](../../PRODUCT.md)
- How it is built: [docs/ARCHITECTURE.md](../ARCHITECTURE.md)
- Features and shared components: [docs/features.md](../features.md)
- Entities, relationships and indexes: [docs/database.md](../database.md)
- Tenant isolation: [docs/tenant-isolation.md](../tenant-isolation.md)
- Signing in and sessions: [docs/authentication.md](../authentication.md)
- Roles, permissions and impersonation: [docs/authorization.md](../authorization.md)
- Architectural decisions: [docs/adr/](../adr/README.md)
- Planned, not yet built: [docs/roadmap.md](../roadmap.md)
- Known gaps between the code and its rules: [docs/known-issues.md](../known-issues.md)
- Rules for code and agents: [.github/copilot-instructions.md](../../.github/copilot-instructions.md)
