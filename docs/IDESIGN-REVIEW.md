# iDesign Architecture Review

Use this checklist for every architecture-affecting plan, implementation, refactor, and review. Copy the result into the relevant plan, summary, pull request, or review artifact.

## 1. Layer assignments

| Component | Layer | Cohesive responsibility | Allowed dependencies | Forbidden dependencies |
|---|---|---|---|---|
| `<type or module>` | Client, Manager, Engine, Accessor, or Resource | `<one reason to change>` | `<dependencies>` | `<dependencies>` |

**Result:** PASS / FAIL / NOT APPLICABLE

## 2. Dependency direction

- [ ] Calls follow the dependency matrix in `docs/IDESIGN.md`.
- [ ] Managers do not call other Managers.
- [ ] Engines do not depend on orchestration, transport, or UI concerns.
- [ ] Accessors do not own workflow or domain policy.
- [ ] Clients and Resources do not contain domain algorithms.

**Result:** PASS / FAIL / NOT APPLICABLE

## 3. Interface justification

Complete one row for every interface added, changed, or materially relied upon.

| Interface | Implementations | Callers | Current boundary or substitution need | Keep, remove, or defer |
|---|---:|---:|---|---|
| `<interface>` | `<count/names>` | `<count/names>` | `<platform, process, persistence, transport, volatile mechanism, or necessary test seam>` | `<decision>` |

Reject an interface whose only rationale is dependency injection, mocking convenience, convention, or a hypothetical future implementation.

**Result:** PASS / FAIL / NOT APPLICABLE

## 4. Cohesion and decomposition

- [ ] Each service has one cohesive responsibility rather than one method or function.
- [ ] No thin pass-through service exists without policy, translation, lifecycle ownership, or boundary isolation.
- [ ] Orchestration is not fragmented across multiple Managers.
- [ ] Related behavior that changes together remains together.
- [ ] Functional decomposition has not been disguised as a service/interface hierarchy.

**Result:** PASS / FAIL / NOT APPLICABLE

## 5. Verification placement

| Behavior | Owning layer | Verification type | Evidence |
|---|---|---|---|
| `<behavior>` | `<layer>` | unit, contract, integration, operational, or UAT | `<command/artifact>` |

- [ ] Engine policy is covered by deterministic unit tests.
- [ ] Accessor behavior is covered at the integration boundary.
- [ ] Manager behavior is covered as a use-case workflow.
- [ ] Client or Resource behavior is covered through contract or UI verification.

**Result:** PASS / FAIL / NOT APPLICABLE

## 6. Exceptions

List each intentional exception with its decision ID, rationale, scope, and revisit trigger. Write `None` when there are no exceptions.

## Compliance statement

> This change follows the project iDesign policy. Layer assignments and dependency direction were reviewed. Every interface has a current architectural justification, and no unexplained interface-per-class or pass-through decomposition remains.

**Overall result:** PASS / FAIL
