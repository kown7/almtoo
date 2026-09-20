---
name: idesign
description: Enforce this project's iDesign layering, dependency direction, cohesive service decomposition, and interface justification during research, planning, implementation, refactoring, and review.
---

<objective>
Apply the architecture policy in `../../docs/IDESIGN.md` to every architecture-affecting unit of work. Prevent accidental layer inversion, orchestration leakage, pass-through abstractions, and interface-per-class decomposition.

This skill is mandatory project policy. Read `../../docs/IDESIGN.md` before making or approving architecture decisions. Use `../../docs/IDESIGN-REVIEW.md` as the completion gate.
</objective>

<quick_start>
1. Read `../../docs/IDESIGN.md`.
2. Identify each affected component's layer: Client, Manager, Engine, Accessor, or Resource.
3. Draw the relevant caller-to-callee dependency direction before changing types.
4. Prefer one cohesive concrete service until a real boundary requires abstraction.
5. For each interface added or retained, write one sentence naming:
   - the external or platform boundary it isolates;
   - the credible alternate implementation it supports; or
   - the necessary testing seam that cannot be achieved more simply.
6. Complete `../../docs/IDESIGN-REVIEW.md` during planning and again during final review.
7. Record any intentional exception as a decision; do not silently waive the policy.
</quick_start>

<interface_policy>
An interface is justified when at least one of these is concrete and current:

- It crosses a process, network, persistence, device, browser, framework, or third-party boundary.
- Two credible implementations exist or are part of accepted near-term scope.
- Runtime substitution is required by the product.
- It protects a stable contract from a volatile mechanism.
- It is the least-complex practical seam for deterministic testing of a side effect.

An interface is not justified merely because:

- dependency injection can register it;
- mocking might be convenient;
- every class should have an interface;
- it mirrors a single implementation method-for-method;
- it splits a cohesive workflow into thin pass-through functions;
- a hypothetical implementation might exist someday.

If no justification survives review, use a concrete type or combine responsibilities into the cohesive layer owner.
</interface_policy>

<layer_review>
For every changed service, report:

| Component | Layer | Responsibility | May call | Must not call |
|---|---|---|---|---|
| Name | Client, Manager, Engine, Accessor, or Resource | One cohesive reason to change | Allowed downstream dependencies | Forbidden dependencies |

Check especially:

- Clients do not contain business rules or resource-access details.
- Managers orchestrate a use case but do not call other Managers.
- Engines contain deterministic domain policy and avoid orchestration or transport concerns.
- Accessors isolate integration and persistence mechanisms and do not own workflow policy.
- Resources remain transport or presentation adapters rather than application coordinators.
- Dependencies follow the matrix in `docs/IDESIGN.md`.
</layer_review>

<planning_requirements>
Architecture-affecting plans must include:

- affected layers and dependency direction;
- responsibility movement between existing and proposed types;
- interface additions, removals, and explicit justifications;
- tests that prove behavior at the correct layer;
- an iDesign review step using `docs/IDESIGN-REVIEW.md`.

Push back on plans that create broad service catalogs, one-interface-per-class structures, Manager-to-Manager calls, or abstractions without a current boundary.
</planning_requirements>

<review_requirements>
During review:

- inspect implementation dependencies, not only type names;
- search for interfaces with one implementation and one caller;
- identify pass-through methods with no policy, translation, or boundary isolation;
- verify orchestration is not fragmented across many thin services;
- distinguish a real platform boundary from speculative abstraction;
- report violations with file and symbol references;
- propose the smallest cohesion-improving correction.

Do not claim compliance solely because interfaces and dependency injection are present.
</review_requirements>

<success_criteria>
The work is compliant when:

- every affected service has one defensible layer assignment and cohesive responsibility;
- dependencies follow `docs/IDESIGN.md`;
- each interface has a documented current justification;
- no unexplained pass-through abstraction or interface-per-class pattern remains;
- behavior is tested at the appropriate layer;
- `docs/IDESIGN-REVIEW.md` is completed with PASS or documented decisions for exceptions.
</success_criteria>
