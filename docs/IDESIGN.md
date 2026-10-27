# iDesign Method — Generic Architecture Guideline for Software Agents

**Purpose:**  
This document defines a **generic, enforceable architecture guideline** for agents (and developers) designing software systems using iDesign layering principles.

**Audience:**  
- AI coding/design agents
- Human developers and reviewers
- Architects enforcing consistent boundaries

**Scope:**  
Applies to new features, refactors, and service decomposition across application types (web, mobile, desktop, backend, distributed systems).

---

## 1) Core Intent

Design systems as a set of **well-defined layers** with strict dependency direction, explicit responsibilities, and minimal coupling.

### Primary goals
- High cohesion within a layer
- Low coupling between layers
- Predictable dependency flow
- Replaceable implementations
- Testable orchestration and logic
- Reviewable architecture decisions

---

## 2) Canonical Layer Model

Utilities are **lateral** (cross-cutting), not part of orchestration flow.

```
┌──────────────────────────────────────────────────────────┐
│  Presentation / Interface Layer                          │
│  (UI, API endpoints, controllers, handlers)              │
│  → depends only on Managers + Utilities                  │
└───────────────────────┬──────────────────────────────────┘
                        │
┌───────────────────────▼──────────────────────────────────┐
│  Managers (Application Orchestration)                    │
│  (use-case workflows, transaction scripts, coordination) │
│  → depends on Engines, Accessors, Utilities              │
└───────────────┬───────────────────────────────┬──────────┘
                │                               │
┌───────────────▼──────────────────┐            │
│  Engines (Domain / Stateless)    │            │
│  (pure logic, parsing, transforms│            │
│   policy calculations, rules)    │            │
│  → may depend on Accessors*      │            │
└───────────────┬──────────────────┘            │
                │                               │
┌───────────────▼───────────────────────────────▼──────────┐
│  Accessors (I/O and Integration)                         │
│  (DB, external APIs, filesystem, cache, queues, auth)    │
└───────────────────────────────────────────────────────────┘

Utilities (lateral): logging, guards, retry/backoff, shared pure helpers, constants.
```

\* Prefer pure Engines without I/O dependencies when possible.

---

## 3) Layer Responsibilities

## 3.1 Presentation / Interface Layer
**Examples:** UI components, controllers, route handlers, GraphQL resolvers, CLI commands.

**Must:**
- Map external input to application requests
- Trigger manager operations
- Return/format responses

**Must not:**
- Implement orchestration logic
- Call Accessors directly
- Embed domain rules that belong in Engines

---

## 3.2 Managers (Application Use-Case Coordination)
**Role:** Implement application-layer use-cases and coordinate their execution.

A **use-case** is a complete application intent with explicit inputs, flow-level preconditions, side effects, result, and application failure contract. A use-case is the architectural boundary exposed by a Manager operation.

A **workflow** is the internal sequence used to implement a use-case. It may contain validation, Engine calls, Accessor calls, retries, refreshes, and compensation. A workflow is not, by itself, a reason to create a Manager. Managers are organized around application use-cases, not around implementation workflows, pages, workspaces, domain nouns, DTOs, or individual integration operations.

**Must:**
- Expose one or more complete, intent-oriented use-case operations.
- Define and enforce flow-level preconditions before side effects occur.
- Orchestrate the required calls to Engines and Accessors.
- Translate technical results and failures into an application-level result contract.
- Handle application-level failures, cancellation, and compensation where the use-case requires them.
- Keep the use-case boundary independently understandable and testable.
- Retain scoped execution state only when it supports use-case continuity, concurrency safety, cancellation, or application-level result state.

**Must not:**
- Call other Managers; avoid a Manager-to-Manager dependency graph.
- Hold domain algorithm complexity that belongs in Engines.
- Perform external I/O or contain vendor/platform integration details that belong in Accessors.
- Perform direct transport or presentation formatting concerns such as UI/API shaping.
- Expose internal workflow steps as public operations unless each step is itself a complete use-case.
- Become an arbitrary UI state container unrelated to the use-cases it coordinates.

### 3.2.1 Manager class granularity

A Manager class may represent one use-case or group several closely related use-cases. Grouping is a packaging decision, not a relaxation of the use-case boundary: every public operation must still represent a complete use-case.

Group use-cases in one Manager only when they share a cohesive application boundary, meaningful dependencies, state, and lifecycle. Do not introduce a new Manager solely for a page, component, domain noun, DTO, Accessor, or individual operation. Before adding a Manager, first consider extracting deterministic policy to an Engine, integration details to an Accessor, or a cohesive private helper within the existing Manager.

### 3.2.2 Manager evaluation questions

Use these questions when reviewing an existing or proposed Manager:

1. Does every public operation represent a complete application use-case rather than an internal workflow step?
2. Are the use-case inputs, preconditions, side effects, result, and failure behavior explicit?
3. Is the Manager coordinating Engines and Accessors rather than owning domain algorithms or external I/O?
4. If the Manager is stateful, does each retained state value support use-case continuity, concurrency safety, cancellation, or application-level result state?
5. If several use-cases share a class, do they share a cohesive application boundary, dependencies, state, and lifecycle?
6. Could the proposed new Manager instead be an Engine, an Accessor, or a cohesive private helper?
7. Does the design preserve the prohibition on Manager-to-Manager calls?

---

## 3.3 Engines (Stateless Domain Logic)
**Role:** Deterministic logic units.

**Must:**
- Encapsulate business/domain rules
- Remain stateless and side-effect-minimized
- Be easy to unit test with in-memory inputs

**Must not:**
- Depend on Managers
- Depend on transport/UI concerns
- Hide orchestration logic

---

## 3.4 Accessors (Data/Integration)
**Role:** All external I/O and integration boundaries.

**Must:**
- Encapsulate integration protocols
- Expose interface contracts (DTOs near interface)
- Translate external failures to meaningful technical errors

**Must not:**
- Implement orchestration flow
- Depend on Managers or Engines
- Leak vendor-specific models beyond contract boundary

---

## 3.5 Utilities (Cross-Cutting)
**Role:** Shared helpers with no business orchestration role.

**May include:**
- Guard clauses
- Logging wrappers
- Retry/backoff policies
- Formatting helpers
- Reusable pure functions

**Must not:**
- Depend on Managers, Engines, or Accessors
- Become a dumping ground for business logic

---

## 4) Dependency Rules (Hard Constraints)

| Caller Layer | Allowed Dependencies | Forbidden Dependencies |
|---|---|---|
| Presentation | Managers, Utilities | Engines, Accessors |
| Managers | Engines, Accessors, Utilities | Other Managers |
| Engines | Accessors\*, Utilities | Managers, other Engines\*\* |
| Accessors | Utilities | Managers, Engines, other Accessors |
| Utilities | (none required) | Presentation, Managers, Engines, Accessors |

\* Only when justified; prefer pure Engines.  
\*\* Engine-to-Engine is discouraged unless explicitly approved and documented.

---

## 5) Packaging & Naming Conventions

Organize by **service unit** per capability:

- `Interface/` → `I<ServiceName>.cs` (+ service-specific DTOs)
- `Service/` → `<ServiceName>.cs` implementation

Recommended structure:

```text
Services/
├── Managers/
│   └── <ManagerName>/
│       ├── Interface/
│       │   ├── I<ManagerName>.cs
│       │   └── <ManagerName>Dtos.cs
│       └── Service/
│           └── <ManagerName>.cs
├── Engines/
│   └── <EngineName>/
│       ├── Interface/
│       │   ├── I<EngineName>.cs
│       │   └── <EngineName>Dtos.cs
│       └── Service/
│           └── <EngineName>.cs
├── Accessors/
│   └── <AccessorName>/
│       ├── Interface/
│       │   ├── I<AccessorName>.cs
│       │   └── <AccessorName>Dtos.cs
│       └── Service/
│           └── <AccessorName>.cs
└── Utilities/
    └── <shared helper files>
```

### Namespace guidelines

Use namespaces that mirror folder and layer ownership.

**Root format:**
- `<Company>.<Product>` as the stable namespace root (or equivalent org-wide standard)

**Layered namespace format:**
- `<Root>.Services.Managers.<ManagerName>.Interface`
- `<Root>.Services.Managers.<ManagerName>.Service`
- `<Root>.Services.Engines.<EngineName>.Interface`
- `<Root>.Services.Engines.<EngineName>.Service`
- `<Root>.Services.Accessors.<AccessorName>.Interface`
- `<Root>.Services.Accessors.<AccessorName>.Service`
- `<Root>.Services.Utilities`

**Rules:**
1. Namespace must map 1:1 with folder structure.
2. Interface contracts and their DTOs belong in the same `.Interface` namespace.
3. Implementations belong in the matching `.Service` namespace.
4. Do not place layer-specific services into generic buckets like `.Common` or `.Helpers`.
5. `using`/imports must respect the dependency matrix (namespace visibility does not justify illegal dependencies).
6. Prefer file-scoped namespaces where language/version supports them for consistency and reduced nesting.

### DTO placement rule
DTOs are owned by the interface boundary that uses them.  
Do **not** centralize all DTOs in a global `Models/` folder without ownership context.

---

## 6) Agent Design Protocol (Required)

When an agent designs or modifies architecture, it must:

1. **Classify each class/service** into exactly one layer.
2. **Justify each dependency** against the allowed matrix.
3. **Reject illegal edges** (e.g., Presentation → Accessor).
4. **Prefer extraction**:
   - orchestration → Manager
   - business logic → Engine
   - integration/I/O → Accessor
5. **Co-locate contracts** with owning interface.
6. **Document exceptions** explicitly (with rationale and expiry if temporary).

---

## 7) Review Checklist (Enforceable)

Use this in PR/design review:

- [ ] Every new class has an explicit layer assignment.
- [ ] Presentation depends only on Managers/Utilities.
- [ ] Every public Manager operation maps to a complete application use-case.
- [ ] Manager class grouping is justified by a cohesive use-case boundary, dependencies, state, and lifecycle.
- [ ] No Manager was introduced only for a page, workspace, domain noun, DTO, Accessor, or internal operation.
- [ ] Stateful Manager data is limited to use-case continuity, concurrency safety, cancellation, or application-level result state.
- [ ] No Manager calls another Manager.
- [ ] Engines are stateless and testable in isolation.
- [ ] Accessors contain all external I/O details.
- [ ] DTOs are co-located with owning service interface.
- [ ] No layer-violating imports/references.
- [ ] Utilities contain no hidden orchestration logic.
- [ ] Any exception is documented with rationale.

---

## 8) Common Anti-Patterns to Reject

- “God service” combining orchestration + I/O + logic
- Direct controller/UI access to DB/API client
- Manager chaining (`ManagerA -> ManagerB`)
- Manager created for a page, workspace, DTO, Accessor, or internal workflow step without a use-case boundary
- Public Manager methods that expose internal workflow steps rather than complete use-cases
- Stateful Manager used as an unrelated UI state container
- Accessor containing business policy decisions
- Utility namespace used as a business-logic landfill
- Shared global model folder with unclear ownership

---

## 9) Migration Guidance (From Non-Compliant Designs)

When refactoring legacy code:

1. Identify mixed-responsibility classes.
2. Slice methods by concern:
   - orchestration → Manager
   - pure rules/transforms → Engine
   - integration calls → Accessor
3. Define interfaces per new service unit.
4. Move DTOs to interface folders.
5. Update dependency injection registrations.
6. Add tests per layer (unit for Engines, integration for Accessors, workflow for Managers).

---

## 10) Decision Policy

If unsure where code belongs:

- If it **coordinates steps** across components → **Manager**
- If it **computes/transforms/parses/decides rules** → **Engine**
- If it **talks to external systems** → **Accessor**
- If it is **generic cross-cutting support** → **Utility**

When in doubt, choose the option that keeps dependencies one-way and improves testability.

---

## 11) Compliance Statement Template

Use in design docs/PRs:

> This change follows iDesign layering:  
> - Added/updated services: `<names>`  
> - Layer assignments: `<service -> layer>`  
> - New dependencies: `<edge list>`  
> - Verified against dependency matrix: `PASS/EXCEPTIONS`  
> - Exceptions (if any): `<rationale + mitigation + follow-up>`

