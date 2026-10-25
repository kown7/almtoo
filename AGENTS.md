# Project Agent Instructions

## Mandatory architecture policy

Before architecture research, milestone or slice planning, implementation, refactoring, or review, read [`docs/IDESIGN.md`](docs/IDESIGN.md).

Treat it as required project policy, not optional background material.

### Required behavior

- Assign every new or materially changed service to an iDesign layer: Client, Manager, Engine, Accessor, or Resource.
- Preserve the dependency direction and call constraints defined in `docs/IDESIGN.md`.
- Prefer cohesive concrete services over interface-per-class or method-by-method functional decomposition.
- Introduce an interface only when it represents a genuine architectural boundary, has a credible alternate implementation, isolates an external/platform dependency, or supplies a necessary testing seam that cannot be achieved more simply.
- Do not add pass-through interfaces whose only implementation and caller change together.
- Keep orchestration in Managers, deterministic domain policy in Engines, integration logic in Accessors, and transport or UI concerns in Clients or Resources as defined by the guideline.
- Record intentional exceptions as explicit architectural decisions with rationale.

### Planning and review gate

Every architecture-affecting plan and review must complete [`docs/IDESIGN-REVIEW.md`](docs/IDESIGN-REVIEW.md). A change is not complete while that review has an unexplained failure.

**Hard approval gate:** Before architecture research, slice planning, refactoring, implementation, or review work begins, the agent must create or update a **current, scoped** iDesign review for that unit of work. The review must identify affected layers, dependency direction, retained or proposed interfaces, and one-sentence justification for each interface. The agent must then stop and present the review to the user. No plan or source-code implementation may begin until the user explicitly approves the review. A historical PASS for another slice or feature never satisfies the current gate.

**Re-review stop:** At the start of every slice, and again whenever execution reveals a materially changed service, contract, Accessor, Resource, or retained interface, invalidate the prior approval, update the scoped review, and stop for approval again. This stop applies even when no new interface is added and before any commit or merge.

When adding or retaining an interface, include a one-sentence justification naming the boundary, substitution need, or platform dependency it protects.


## Credentials

The credentials are stored in `/root/.config/almtoo/live.env`. Whenever credentials are needed for, e.g., live-tests, copy them to AlmToo/.env path.

