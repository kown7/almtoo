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

When adding or retaining an interface, include a one-sentence justification naming the boundary, substitution need, or platform dependency it protects.
