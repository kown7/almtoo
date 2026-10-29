# iDesign Architecture Review

Use this checklist for every architecture-affecting plan, implementation, refactor, and review. Copy the result into the relevant plan, summary, pull request, or review artifact.

## Current review gate

This file contains historical reviews below; those entries are evidence, not automatic approval for new work. Every new architecture-affecting unit must add or update a clearly scoped current section before planning or implementation:

```markdown
## <unit or slice> <scope>

**Status:** PENDING APPROVAL | APPROVED | BLOCKED
**Affected layers:** Client, Manager, Engine, Accessor, Resource
**Dependency direction:** <caller> -> <callee>
**Interfaces added or retained:** <list, or none>
**Interface justifications:** <one sentence per interface>
**Exceptions or risks:** <list, or none>
**User approval:** <pending, or date and explicit approval reference>
```

The agent must present the current section to the user and stop while it is `PENDING APPROVAL`. No architecture-affecting plan or source-code implementation may begin until the user explicitly approves it. If the scope or interface set changes materially, reset the status to `PENDING APPROVAL` and repeat the stop.

## Manager policy expansion

**Status:** APPROVED
**Affected layers:** Client, Manager, Engine, Accessor
**Dependency direction:** Client -> Manager -> Engine and/or Accessor
**Interfaces added or retained:** None
**Interface justifications:** None; this change expands written Manager rules only.
**Exceptions or risks:** Stateful Managers are an intentional application-specific adaptation for scoped use-case continuity and client-facing execution state; they must not become arbitrary UI state containers.
**User approval:** Explicitly approved by user request on 2026-10-25

**Approved policy direction:** Managers own application use-cases, not pages, workspaces, or implementation workflows. A use-case is a complete application intent with defined inputs, preconditions, side effects, result, and failure contract. A Manager class may represent one use-case or a tightly cohesive group of use-cases sharing an application boundary, dependencies, state, and lifecycle. Pass-through wrappers are rejected; Managers do not call other Managers; and an oversized Manager should first be reduced through Engines, Accessors, and cohesive private helpers before being split into additional Managers.

## Manager and Engine structure and redesign

**Status:** APPROVED
**Affected layers:** Client, Manager, Engine, Accessor
**Dependency direction:** Client -> Manager -> Engine and/or Accessor
**Interfaces added or retained:** No new Manager interface is approved by default; existing Accessor interfaces remain subject to boundary review.
**Interface justifications:** Any retained or new interface must justify a platform boundary, alternate implementation, or necessary testing seam.
**Exceptions or risks:** Existing Managers are stateful and broad; the legacy `Services/Git` area may overlap with Accessor responsibilities; structural moves must avoid creating pass-through interfaces or splitting Managers by page or technical noun.
**User approval:** Explicitly approved by user request on 2026-10-25

**Approved scope:** Normalize Manager and Engine packaging to the Accessor pattern where appropriate (`<Layer>/<ServiceName>/Interface` and `Service`), classify the legacy Git services, inventory current public Manager operations as use-cases, verify whether any deterministic policy actually justifies an Engine, and redesign Manager boundaries around cohesive use-case families before changing behavior. The consolidated public Manager is named `WorkspaceManager`. No Engine is presumed for Git-Bug: its Accessor owns the integration and DTO contracts, while `WorkspaceManager` coordinates the use-cases. Structural moves and behavioral redesign should be separable so each can be verified independently.

## M015 S01 Workspace use-case inventory and boundary review

**Status:** APPROVED
**Affected layers:** Client, Manager, Accessor, Resource
**Dependency direction:** Client -> WorkspaceManager -> `IBrowserGitAccessor` / `IGitBugAccessor` -> `BrowserGitAccessor` -> browser Git Resource
**Interfaces added or retained:** Retain `IBrowserGitAccessor` and `IGitBugAccessor`; no `IWorkspaceManager`, Git-Bug Engine, or new Accessor facet
**Interface justifications:** `IBrowserGitAccessor` protects the browser JavaScript/Git/transport and credential platform boundary; `IGitBugAccessor` protects the distinct Git-Bug format/storage boundary and fixture-backed substitution seam.
**Exceptions or risks:** The former `RepositoryWorkspaceManager`, `GitBugWorkspaceManager`, duplicate state paths, and `AlmToo/Services/Git` are absent in the current checkout; the design artifact records them as historical migration inputs and forbids recreating pass-through services.
**User approval:** Explicitly approved by user request on 2026-10-25

**Review result:** PASS. The current normalized `WorkspaceManager` owns complete application use-cases and safe state transitions, Clients remain above the Manager boundary, browser/Git integration remains below the Accessor boundary, and no deterministic policy or alternate implementation justifies a new Engine or Manager interface.

## M015 S02 WorkspaceManager consolidation

**Status:** APPROVED
**Affected layers:** Client, Manager, Accessor
**Dependency direction:** Client -> WorkspaceManager -> BrowserGitAccessor contracts -> BrowserGitAccessor
**Interfaces added or retained:** `IBrowserGitAccessor`, `IGitBugAccessor`, `IGitPublicationAccessor`, `IBrowserFileAccessor`; no new Manager interface
**Interface justifications:** Existing Accessor contracts protect the browser Git platform boundary and focused Git-Bug/publication/file seams; no `IWorkspaceManager` is introduced because no alternate implementation or required testing seam has been established.
**Exceptions or risks:** One concrete Accessor currently implements all retained facets; S03 must verify whether publication and file facets remain independently justified. WorkspaceManager is stateful by design for active workspace continuity, cancellation, and safe failure state.
**User approval:** Explicitly approved by user request to continue on 2026-10-25
**Review result:** PASS. The consolidation preserves Client -> Manager -> Accessor direction, removes duplicate workspace context ownership, and introduces no Engine or pass-through Manager interface.

## M015 S03 Accessor facet consolidation

**Status:** APPROVED
**Affected layers:** Manager, Accessor, Client tests
**Dependency direction:** WorkspaceManager -> `IBrowserGitAccessor` and `IGitBugAccessor` -> `BrowserGitAccessor`
**Interfaces added or retained:** Retain `IBrowserGitAccessor` and `IGitBugAccessor`; remove `IGitPublicationAccessor` and `IBrowserFileAccessor` as separate contracts.
**Interface justifications:** `IBrowserGitAccessor` protects the browser Git platform boundary; `IGitBugAccessor` protects the distinct Git-Bug format and fixture-backed substitution seam. The publication and file facets currently resolve to the same concrete Accessor and have no independent consumer or implementation.
**Exceptions or risks:** Removing the two redundant facets changes Accessor contracts and test seams; all Manager, DI, architecture-test, and fixture references must migrate together. The former `AlmToo/Services/Git` layer is absent, and the architecture gate forbids recreating its service names or redundant facet contracts.
**User approval:** Explicitly approved by user response on 2026-10-25
**Review result:** PASS. The approved reduction removes nominal Accessor facets without adding concrete services or weakening the browser Git or Git-Bug platform boundaries. Final verification passed: .NET build, 78 .NET tests, 124 Node tests, stale production-reference scan, and modified-file whitespace audit.

**Approved scope:** Reduce interface proliferation without creating new Accessor services: fold file and publication operations into `IBrowserGitAccessor`, retain `IGitBugAccessor`, remove the redundant facet registrations and files, classify or remove `AlmToo/Services/Git`, and verify the final Client -> WorkspaceManager -> Accessor boundary.

## Next milestone: Git-Bug comment presentation de-duplication

**Status:** APPROVED
**Affected layers:** Client (`GitBugIssueDetail.razor`), Manager contract state (`GitBugIssueDetail` data), Accessor (`BrowserGitAccessor` and `browserGitEngine.js`), test fixtures and browser acceptance
**Dependency direction:** Client -> Git-Bug Manager -> `IGitBugAccessor` -> `BrowserGitAccessor` -> browser Git integration
**Interfaces added or retained:** Retain existing `IGitBugAccessor` and passive issue-detail contracts; no new interface proposed.
**Interface justifications:** Existing Accessor interfaces isolate the browser Git and Git-Bug storage boundary and provide the required testing seam for deterministic fixture-backed reads and writes; this milestone does not add another abstraction.
**Proposed contract behavior:** A Git-Bug creation message remains the issue description but is not returned as a second follow-up comment. Type-3 Git-Bug comment operations remain comments, including after a local comment write and refresh.
**Proof required:** Fixture contract expectations, Accessor/Engine tests, and Chromium acceptance must prove the creation message renders once in the description, existing follow-up comments render once, and adding a new comment does not duplicate prior comments.
**Exceptions or risks:** Existing fixture expectations currently count the creation message in `comments`; they must be deliberately updated with the contract rationale, not silently changed.
**User approval:** Explicitly approved through the structured review gate in this session.

## S03 Git-Bug local commit proof

**Result:** PASS

- **Client:** `GitBug.razor` and `GitBugIssueDetail.razor` render passive `GitBugWorkspaceState` and delegate review/commit intent callbacks; they do not access browser Git APIs, credentials, or raw diagnostics.
- **Manager:** `GitBugWorkspaceManager` owns review loading, metadata validation, explicit confirmation, cancellation-safe transitions, status refresh, and bounded recoverable failures.
- **Accessor:** `BrowserGitAccessor` and `browserGitEngine.js` validate bounded commit metadata, expose changed-file review and safe commit metadata, keep browser diagnostics below the managed boundary, and provide no push or credential call from the commit path.
- **Proof:** focused Node tests cover changed-file preview, successful local commit, status refresh, malformed/empty metadata, categorized commit failure, and no-push behavior; .NET contract tests cover bounded credential-free DTO shape. The aggregate `npm test` suite is the final verification surface.

## 1. Layer assignments

| Component | Layer | Cohesive responsibility | Allowed dependencies | Forbidden dependencies |
|---|---|---|---|---|
| `Pages/Home.razor` and repository components | Client | Render credential-free Manager state and delegate user actions | `RepositoryWorkspaceManager`, `RepositoryWorkspaceState` | Accessor types, JS interop, Git policy, raw diagnostics |
| `RepositoryWorkspaceManager` and `RepositoryWorkspaceState` | Manager | Orchestrate review-first synchronization, preconditions, cancellation, and state transitions | Accessor interfaces, passive Accessor contract records | other Managers, JS interop, rendering |
| `browserGitEngine.js`, `IBrowserGitAccessor`, and `BrowserGitAccessor` | Accessor | Integrate browser-local Git metadata and remote transport, including fetch/review/apply execution, credential-free serialization, and failure translation | browser APIs, Git engine, passive Accessor contract records | Client, Manager workflow, UI state |
| `BrowserGitContracts.cs` beside `IBrowserGitAccessor` and `IBrowserFileAccessor` | Accessor contract | Carry passive requests, results, review metadata, decisions, and safe failure categories | Accessor interfaces and data types only | methods, validation, I/O, orchestration, credentials |

Synchronization remains a Browser Git capability, not a new VCS-neutral service: the Manager owns review and acceptance order; the Accessor owns browser/Git integration; Resource records carry only credential-free data.

**Result:** PASS

## 2. Dependency direction

- [x] Calls follow the dependency matrix in `docs/IDESIGN.md`: Client -> Manager -> Accessor -> browser/platform.
- [x] `RepositoryWorkspaceManager` does not call another Manager.
- [x] No Engine is introduced; synchronization sequencing is Manager orchestration, not Resource policy.
- [x] `BrowserGitAccessor` performs platform integration and safe translation but does not choose UI workflow order or acceptance.
- [x] Clients render Manager state only; Resources contain passive records/enums only.
- [x] Fetch/review is separate from explicit apply acceptance, and diagnostics remain below the Client boundary.

**Result:** PASS

## 3. Interface justification

| Interface | Implementations | Callers | Current boundary or substitution need | Keep, remove, or defer |
|---|---:|---:|---|---|
| `IBrowserGitAccessor` | 1: `BrowserGitAccessor` | 1: `RepositoryWorkspaceManager` | Protects the browser JavaScript, Git transport, and credential platform boundary and provides the necessary substitution seam for workflow tests, including synchronization failure/cancellation paths. | Keep |
| `IBrowserFileAccessor` | 1: `BrowserGitAccessor` | 1: `RepositoryWorkspaceManager` | Protects the browser filesystem/JavaScript platform boundary and provides the necessary substitution seam for workspace precondition and refresh tests. | Keep |

No VCS-neutral interface is introduced: synchronization is a capability of the existing Browser Git platform boundary, not a speculative alternate implementation.

**Result:** PASS

## 4. Cohesion and decomposition

- [x] The Manager remains one cohesive repository-workspace orchestrator; synchronization is an extension of its existing operation state machine.
- [x] The Accessor remains a genuine platform boundary with validation, translation, credential handling, and browser module lifetime.
- [x] Fetch/review/apply contracts are passive Resource data and do not create a service or method-per-record decomposition.
- [x] Existing operation serialization is reused; no parallel Manager or speculative Engine is added.
- [x] Related synchronization state (`Review`, `Decision`, and safe failure category) changes together in Manager state.

**Result:** PASS

## 5. Verification placement

| Behavior | Owning layer | Verification type | Evidence |
|---|---|---|---|
| Client delegates synchronization and does not render diagnostics | Client | source contract | `AlmToo/tests/homePageContractTests.mjs` and architecture gate |
| Review-first sequencing, editor/tree preconditions, cancellation, and safe state | Manager | workflow unit | `AlmToo.Tests/Managers/Repositories/RepositoryWorkspaceManagerTests.cs` |
| Browser Git synchronization request/result translation | Accessor | contract/integration | `AlmToo/tests/browserGitAccessorContractTests.mjs`, managed tests |
| Passive sync records and stable enums | Resource | architecture/compile contract | `AlmToo.Tests/Resource/BrowserGitContractsTests.cs`, architecture gate |
| Layer ownership and interface rationale | Cross-layer | architecture contract | `AlmToo/tests/browserGitArchitectureGateTests.mjs` |

- [x] No Engine is present; deterministic synchronization policy is not split from Manager orchestration.
- [x] Accessor behavior is checked at the browser/platform boundary.
- [x] Manager behavior is checked as a complete use-case workflow.
- [x] Client and Resource boundaries are checked without exposing raw Git/HTTP/storage diagnostics or PAT values.

**Result:** PASS

## 6. Exceptions

None. Synchronization uses the established Browser Git Accessor boundary and concrete repository Manager. No VCS-neutral abstraction, Resource behavior, Client integration, or cross-layer diagnostic exposure is intentionally retained. Revisit only if a second platform implementation or an independently substantial deterministic synchronization Engine is required.

**Result:** NOT APPLICABLE

## Compliance statement

> This M004 synchronization change follows the project iDesign policy. Layer assignments and dependency direction were reviewed. Every retained interface protects a browser/platform boundary or necessary substitution seam, and no unexplained interface-per-class or pass-through decomposition remains. Review-first acceptance, non-destructive preconditions, cancellation, and safe diagnostic handling remain owned by the correct layers.

**Overall result:** PASS

## M004 S02 completion review

- [x] `browserGitEngine.js` and `BrowserGitAccessor` remain Accessors: the engine owns browser Git, storage, and remote transport integration; the concrete Accessor owns managed interop validation and safe DTO translation.
- [x] `RepositoryWorkspaceManager` remains the sole Manager for review and apply orchestration; no Engine or second synchronization service was introduced.
- [x] Resource records remain passive and credential-free. `IBrowserGitAccessor` is retained as the single justified interface because it protects the browser JavaScript and Git platform dependency for Manager tests.
- [x] Contract gates verify Client sources reference neither `BrowserGitAccessor` nor `browserGitEngine`, while synchronization failures return a stable failure kind with null diagnostics above the Accessor.

**Result:** PASS

## M004 S03 completion review

- [x] `Pages/Home.razor` remains a **Client**: it subscribes to `RepositoryWorkspaceManager` state, renders only credential-free review summaries and stable categories, and delegates review, apply, and cancellation to that Manager.
- [x] `RepositoryWorkspaceManager` remains the **Manager**: it owns operation serialization, exact-review validation, working-copy preconditions, cancellation, and safe outcome state; Home adds no synchronization policy or Accessor call.
- [x] `IBrowserGitAccessor` and `BrowserGitAccessor` remain the **Accessor** boundary for browser Git, transport, credentials, and diagnostic translation. The Client names neither the Accessor nor browser JavaScript and renders no diagnostic or credential value.
- [x] `SynchronizationReview`, `IncomingCommitMetadata`, changed-file summaries, decisions, and failure categories remain passive **Resource** data crossing the existing Manager boundary.
- [x] No new interface is needed because this change introduces no new substitution boundary: the existing concrete Manager is the cohesive Client-facing orchestration surface, and `IBrowserGitAccessor` already protects the browser/platform seam.

**Result:** PASS

## M004 S04 live-proof review

- [x] `e2e/liveSynchronizationFixture.mjs` and `e2e/liveSynchronization.spec.mjs` are Playwright test infrastructure, not application services: they execute only in Node during the dedicated live acceptance command and are absent from production Client, Manager, Accessor, and Resource dependencies.
- [x] The live specification drives `Pages/Home.razor` exclusively through its existing Client controls, which delegate review and apply to `RepositoryWorkspaceManager`; it introduces no Client-to-Accessor or Client-to-engine path.
- [x] `RepositoryWorkspaceManager` remains the sole workflow owner, and `IBrowserGitAccessor` remains the sole justified browser-platform seam protecting Git, browser storage, and transport integration. No interface, Engine, or production exception is introduced.
- [x] Fixture configuration is injected from the process environment, while browser, console, storage, DOM, URL, and resource-URL assertions preserve the existing credential-free Client boundary.
- [x] Redacted Playwright transport and browser-Git stage classification remain test-only infrastructure: they reduce request events and wrapped Git calls to fixed labels, retain no URL, arguments, header, body, Git response, SHA, error text, or credential value, and add no production dependency or application boundary.

**Result:** PASS

## M007 S02 T01 Git-Bug Accessor facet review

- [x] `IGitBugAccessor`, its passive records, and `BrowserGitAccessor` are assigned to the **Accessor** boundary; private bridge DTOs and validation remain implementation details below that boundary.
- [x] The dependency direction remains Manager → `IGitBugAccessor` → `BrowserGitAccessor` → browser Resource. This task adds no Client or Manager dependency and exposes no `IJSRuntime`, Git ref, path, URL, credential, or raw diagnostic.
- [x] `IGitBugAccessor` is justified because it protects the external Git-Bug storage/version and browser-platform boundary, has a credible future GraphQL/server substitution, and supplies the required fixture-backed test seam.
- [x] The existing concrete `BrowserGitAccessor` implements the facet directly and is registered under it; no pass-through wrapper, method-per-service decomposition, or new Engine is introduced.
- [x] Contract tests own managed DTO mapping, fixed failure classification, bounds, and telemetry sanitization. Resource decoding and repository immutability remain in the following browser Resource task.
- [x] No architectural exception is required.

**Result:** PASS

## M007 S02 T02 Git-Bug browser Resource decoder review

- [x] The new `browserGitEngine.js` Git-Bug reader is assigned to the **Resource** boundary: it resolves browser-local refs and objects, applies the fixture-proven format decoder, and emits passive bridge DTOs.
- [x] Dependency direction remains `BrowserGitAccessor` → browser Resource → `isomorphic-git` and LightningFS. No Client, Manager, or managed contract type is imported by the Resource.
- [x] No new service or interface is introduced. The existing `IGitBugAccessor` remains the justified external-format/browser-platform boundary; the decoder is a cohesive capability of the existing browser Git module rather than a pass-through abstraction.
- [x] The Resource performs no workflow orchestration, remote request, credential handling, or Git mutation. It maps only deterministic version-4 replay semantics and fixed absent/unsupported/malformed outcomes.
- [x] Bounds on refs, history, trees, operations, blobs, text, comments, labels, and aggregate detail protect the browser integration boundary; tests prove supported and negative reads preserve full repository snapshots.
- [x] No architectural exception is required.

**Result:** PASS

## M007 S02 T03 Git-Bug boundary lock review

- [x] Every Client source under `Pages` and `Components` is source-gated against Browser Git and Git-Bug Accessor contracts, Accessor namespaces, browser engine references, and JS interop; future Clients remain limited to a concrete Manager and passive Manager state.
- [x] `IGitBugAccessor` and its passive contracts remain at the **Accessor** boundary, directly implemented by the cohesive `BrowserGitAccessor`; all three Browser Git facets resolve to the same scoped concrete instance.
- [x] `browserGitEngine.js` remains the Git-Bug **Resource** beneath the Accessor. Its list and detail exports are architecture-gated and fixture-tested for no network, no write-capable Git operation, and unchanged repository snapshots on success and every negative category.
- [x] Managed negative tests prove unavailable, unsupported, malformed, unknown, interop-exception, cancellation, oversized, and invalid-input paths fail closed with fixed operations/categories and null diagnostics.
- [x] `IGitBugAccessor` remains justified by the external-format/browser-platform boundary, credible local/server substitution, and fixture seam. No wrapper service, Manager interface, speculative Engine, or pass-through interface was added.
- [x] No architectural exception is required.

**Result:** PASS

## M007 S03 T01 Git-Bug workspace Manager review

- [x] `GitBugWorkspaceManager` and `GitBugWorkspaceState` are assigned to the **Manager** layer: the concrete service owns list/detail intent, flow-level validation, cancellation, stale-result rejection, repository lifecycle reset, and user-safe state transitions.
- [x] Dependency direction remains Client → concrete Manager → `IGitBugAccessor`; the Manager references no Razor component, JavaScript interop, browser Resource, or other Manager.
- [x] Repository lifecycle synchronization uses the passive `RepositoryInfo` contract through `SetRepository` and retains only a private comparison identity. Repository paths, URLs, query values, bridge diagnostics, and exception text are absent from passive Client state.
- [x] `IGitBugAccessor` is retained because it protects the external Git-Bug storage/version and browser-platform boundary, has credible local-decoder and server-backed substitutions, and supplies the necessary fixture seam. No interface was added for the concrete Manager.
- [x] One cohesive generation-based operation lifecycle cancels superseded work and prevents stale completion publication. Deterministic format parsing remains in the Resource; integration remains in the Accessor.
- [x] Workflow tests own happy list/detail/return behavior, input bounds, typed failures, sanitization, cancellation, stale completion rejection, result identity validation, and repository-change reset.
- [x] No architectural exception is required.

**Result:** PASS

## M007 S03 T02 Git-Bug issue Client review

- [x] `Pages/GitBug.razor`, `GitBugIssueList.razor`, and `GitBugIssueDetail.razor` are assigned to the **Client** layer: they render only passive `GitBugWorkspaceState`, expose semantic list/detail controls, and delegate all issue intent to the concrete Manager.
- [x] Dependency direction remains Client → `GitBugWorkspaceManager` → `IGitBugAccessor` → browser Resource. The page observes passive repository lifecycle state only to submit `SetRepository`; neither Manager calls the other.
- [x] Client-safe default-list and string-ID overloads remain cohesive operations on the concrete Manager. No Client constructs Accessor request DTOs, and no pass-through interface or additional service is introduced.
- [x] `IGitBugAccessor` remains the sole justified interface because it protects the external-format/browser-platform boundary, credible alternate implementation, and fixture seam. `GitBugWorkspaceManager` remains interface-free.
- [x] All external issue text uses Razor text rendering rather than raw markup. Loading and failure states use live-region semantics, retry is Manager-controlled, and return from detail restores the retained Manager list context.
- [x] Source tests own navigation, complete state rendering, passive children, text safety, accessible controls, responsive/focus styling, and forbidden Client dependencies. Browser fixture acceptance remains in T03.
- [x] No architectural exception is required.

**Result:** PASS

## M007 S03 T03 local reopen review

- [x] `browserGitEngine.js` remains the existing **Resource** beneath `BrowserGitAccessor`; reopening an already-local workspace refreshes optional remote Git-Bug references only through the existing browser Git transport boundary.
- [x] The change preserves Client → Manager → Accessor → Resource dependency direction, adds no service or interface, and keeps Git-Bug remote discovery best-effort and separate from ordinary branch restoration.
- [x] Browser acceptance proves fixture-backed list and detail reads remain local after refs are persisted, while unavailable or malformed optional metadata does not fail ordinary repository open.
- [x] No architectural exception is required.

**Result:** PASS

## M008 S01 remote Git-Bug ref acquisition review

- [x] `browserGitEngine.js` remains the existing **Resource** beneath `BrowserGitAccessor`; optional `refs/bugs/*` and `refs/identities/*` discovery stays at the browser Git transport boundary for both initial clone and an existing local workspace whose remote is available.
- [x] `GitBugWorkspaceManager`, Razor Clients, and Accessor contracts do not gain remote-ref or JS interop responsibilities; dependency direction remains Client → Manager → Accessor → Resource.
- [x] The existing `IGitBugAccessor` interface is retained as the typed read-only boundary between the Manager and Resource-backed Git-Bug data. No new interface is introduced because no alternate implementation or additional substitution boundary is required.
- [x] Optional metadata discovery remains best-effort and bounded; ordinary repository open behavior and user-safe failure handling remain unchanged when the remote is unavailable.

**Result:** PASS

## M009 S02 T04 Git-Bug local mutation proof review

- [x] `browserGitEngine.js` is assigned to the **Accessor** integration boundary with Resource-level Git object/ref mechanics below it; `editGitBugIssue` and `createGitBugComment` perform bounded v0.11.0 writes only through the active browser-local repository and approved `refs/bugs/*` ref.

- [x] `GitBugWorkspaceManager` remains the **Manager** for input validation, sequencing, cancellation/generation protection, refresh, and safe user-facing state. `GitBug.razor` and `GitBugIssueDetail.razor` remain **Client** components that submit intent only and never construct Accessor contracts or call JavaScript.
- [x] `IGitBugAccessor` remains justified because it protects the browser Git/Git-Bug platform boundary and supplies the necessary substitution seam for deterministic Manager tests; no new interface, wrapper, Engine, or pass-through service was introduced.
- [x] The local Git-Bug history commit required to advance an issue ref is distinct from an ordinary worktree commit: proof asserts `commitCreated: false`, zero push/add/remove calls, and writes confined to the approved bug ref; no push or credential path is reachable from these operations.
- [x] Engine, fixture, Accessor, Manager, and architecture tests cover Unicode writes, malformed/unsupported/conflict failures, bounded redaction, bridge short-circuiting, stale completion protection, deterministic fixture hashes, and unchanged repository state.
- [x] No architectural exception is required.

**Result:** PASS

## M009 S03 T04 Git-Bug preview-to-local-commit proof review

- [x] Focused fixture-backed tests prove the Resource returns bounded changed-file previews, validates commit metadata before mutation, reports safe local commit metadata, refreshes status inputs, and maps commit failures without leaking raw browser diagnostics.
- [x] Tests assert that the local commit path does not call `push` or require credentials; the Client remains dependent only on passive Manager state and explicit confirmation intent.
- [x] `BrowserGitContracts.cs` and `IBrowserGitAccessor` remain the typed **Accessor** boundary, while `browserGitEngine.js` remains the browser-platform **Resource**. No Client-to-Resource dependency or pass-through interface is introduced.
- [x] `gitBugClientArchitectureTests.mjs`, `browserGitArchitectureGateTests.mjs`, and `gitBugBrowserResourceTests.mjs` continue to enforce Client and Resource boundaries, fixture-only proof, and forbidden push/credential behavior.
- [x] No architectural exception is required.

**Result:** PASS

## M009 S04 T04 credential-safe push composition review

- [x] `GitBug.razor` and `GitBugIssueDetail.razor` remain **Client** components: they render passive Manager push-review, credential-presence, busy, success, and recoverable-failure state, and submit only explicit review, credential-intent, confirm, retry, and replacement callbacks. They do not reference `IBrowserGitAccessor`, browser Git JavaScript, raw diagnostics, or credential values.
- [x] `GitBugWorkspaceManager` remains the **Manager** for review-first gating, explicit confirmation, cancellation fencing, credential replacement, retry/review invalidation, failure mapping, and post-push status refresh. The credential is input-only and never retained in `GitBugWorkspaceState`, logs, or diagnostics; no implicit retry or forced push is permitted.
- [x] `BrowserGitContracts.cs`, `IBrowserGitAccessor`, and `BrowserGitAccessor` remain the typed **Accessor** boundary. `browserGitEngine.js` and `pushCredentialSession.js` remain the browser/platform **Resource** surface beneath it, with one non-forced transport attempt, reviewed-coordinate revalidation, and fixed redacted failure categories for authentication, authorization, network, non-fast-forward, unsupported-ref, malformed, and stale inputs.
- [x] Composed proof covers review-to-push success, credential rejection and replacement, authorization/network/remote-ahead recovery, stale review, cancellation, status refresh, redaction, and absence of unintended commit/push retries. Architecture gates additionally require the push Resource exports and preserve Client → Manager → Accessor → Resource direction.
- [x] Focused Node and .NET tests and the aggregate `npm --prefix AlmToo test` suite pass. Remaining live-browser credential transport proof is intentionally deferred to S05; no architectural exception is required.

**Result:** PASS

## M009 S05 T04 browser write and recovery acceptance review

- [x] `Pages/GitBug.razor` and `Components/GitBugIssueDetail.razor` remain **Client** components: they render passive Manager state and submit explicit edit, comment, review, commit, credential, push, retry, and recovery intents; they do not access browser Git APIs, transport details, or raw diagnostics.
- [x] `GitBugWorkspaceManager` remains the **Manager** for review-first sequencing, local commit confirmation, scoped credential handling, explicit push/retry transitions, failure classification, and post-push refresh. No second Manager, Engine, or pass-through service was introduced.
- [x] `BrowserGitAccessor` and `IBrowserGitAccessor` remain the typed **Accessor** boundary for browser Git and Git-Bug integration. The browser Git module and `controlledGitBugRemote.mjs` are test/platform **Resource** infrastructure; the controlled remote is disposable and is never an application dependency.
- [x] The `test:git-bug-write` package command runs both Chromium S05 specs through the existing localhost Playwright web-server configuration. With no live credential environment, the specs use only the deterministic localhost controlled remote; the live GitHub command remains separate and opt-in.
- [x] Browser acceptance proves changed-file and commit review, explicit push, authentication/network recovery, no implicit retry, bounded alerts, repository/remote state, cleanup in `finally`, and absence of credentials from DOM, URL, storage, logs, diagnostics, and durable fixture state.
- [x] No live secret is present in source, fixtures, URLs, logs, or repository state, and no architectural exception is required.

**Result:** PASS

## M009 S03 T01 Git-Bug commit contract review

- [x] `BrowserGitContracts.cs` and `BrowserGitAccessor` remain the typed **Accessor contract** boundary, carrying bounded changed-file and commit metadata and translating malformed bridge output to stable failure categories without exposing browser diagnostics.
- [x] `browserGitEngine.js` remains the browser Git **Resource** beneath that boundary for status inspection and one local commit. Commit validation occurs before mutation, and the operation has no push or credential dependency.
- [x] The existing `IBrowserGitAccessor` is retained because it protects the browser Git platform boundary and provides the established substitution seam for Manager workflow tests; no new interface or pass-through service is introduced.
- [x] Changed-file count, commit message, author metadata, and commit ID are bounded at the Resource/Accessor boundary. Typed results retain only safe commit metadata and do not carry raw JavaScript errors or secrets.
- [x] No architectural exception is required.

**Result:** PASS

## M011 S02 T02 empty-description browser acceptance review

- [x] `gitBugWriteWorkflow.spec.mjs` exercises the Client through accessible controls only; it does not introduce Client-to-Accessor, browser-engine, or transport dependencies.
- [x] `GitBugWorkspaceManager` remains the **Manager** for review-first edit, local commit, explicit credential, push, recovery, and refresh sequencing. Empty descriptions are verified through the existing workflow rather than by bypassing Manager state transitions.
- [x] `BrowserGitAccessor` and `browserGitEngine.js` remain the Accessor/Resource integration boundary. The controlled remote is test-only infrastructure and no new application service, Engine, or pass-through interface was introduced.
- [x] Final Chromium acceptance passed the empty-description edit, complete write workflow, authentication recovery, and network recovery. The tests retain page-error, console-error, request-failure, HTTP-error, alert, remote-state, and credential-redaction assertions appropriate to each scenario.
- [x] The combined run was repeated after one isolated transient recovery assertion failure; the isolated test and final four-test run both passed. No architectural exception is required.

**Result:** PASS

## M012 Branch and history workflow planning review

- [x] **Client:** branch selection and history views will be implemented as Razor components that render passive `RepositoryWorkspaceState` and emit explicit branch-switch and history intents; they will not call browser Git APIs or inspect raw diagnostics.
- [x] **Manager:** `RepositoryWorkspaceManager` remains the cohesive orchestration boundary for branch discovery, clean-worktree gating, cancellation-safe switching, state transitions, and refresh after a successful switch.
- [x] **Accessor:** `IBrowserGitAccessor` and `BrowserGitAccessor` will expose typed branch and history operations at the existing browser-platform boundary; no new pass-through interface is planned.
- [x] **Resource:** `browserGitEngine.js` will own isomorphic-git ref enumeration, checkout, bounded log reads, and safe failure mapping. The Resource will not leak raw Git errors to the Client.
- [x] **Engine policy:** branch-switch eligibility and dirty-worktree rules belong in the Manager; deterministic branch/history bounds and Git/ref parsing remain in the Accessor/Resource boundary.
- [x] **Interface justification:** any new `IBrowserGitAccessor` methods protect the existing JS/browser platform substitution boundary and keep Clients dependent on Managers rather than IJSRuntime; no interface-per-feature decomposition is permitted.
- [x] No architectural exception is required.

**Result:** PASS

## M012 S02 T02 safe branch switching implementation review

- [x] `RepositoryWorkspaceManager` remains the **Manager**: it validates local branch intent, performs a fresh status read, rejects dirty or unavailable worktrees before checkout, fences stale cancellation before checkout and refresh, and publishes safe passive outcomes.
- [x] `IBrowserGitAccessor.CheckoutBranchAsync` remains the existing **Accessor** platform seam; the Manager does not call JavaScript, browser storage, Git commands, or another Manager. The typed method is justified by the browser Git substitution boundary and test seam.
- [x] Directory, status, branch, and history refreshes remain Accessor calls coordinated by the Manager; `RepositoryWorkspaceState` carries only passive repository metadata, stable failure kinds, cancellation, and loading/switching flags.
- [x] Negative workflow tests prove dirty rejection, unavailable status, cancellation, stale clean-read cancellation, and safe diagnostic suppression; the focused .NET Manager suite passes 23/23.
- [x] No architectural exception is required.

**Result:** PASS

## M012 S03 T01 branch and history Client implementation review

- [x] `RepositoryBranchSelector.razor`, `RepositoryCommitHistory.razor`, and the branch/history section of `Pages/Home.razor` remain **Client** components: they render passive `RepositoryWorkspaceState`, expose accessible controls, and emit explicit refresh and branch-switch callbacks only.
- [x] The new Razor components reference no `IBrowserGitAccessor`, `IJSRuntime`, browser Resource, raw diagnostic, or Accessor namespace; all branch eligibility, dirty-worktree rejection, refresh sequencing, cancellation, and safe failure mapping remain owned by `RepositoryWorkspaceManager`.
- [x] Existing file, edit, commit, push, and synchronization Client boundaries remain unchanged. Home adds only concrete Manager callback adapters and does not introduce a new service, Engine, or interface.
- [x] Loading, empty, dirty-worktree guidance, retry, bounded history count, keyboard-operable select/buttons, disabled busy states, and polite/assertive live regions are rendered at the Client boundary without exposing transport or Git diagnostics.
- [x] No architectural exception is required.

**Result:** PASS

## M013 S01 T01 central Git-Bug publication architecture review

**Current review status:** PASS before T02 implementation. This is the authoritative pre-implementation review of the central write-ownership boundary; it explicitly separates the changed ownership from the retained platform interfaces and does not claim that the refactor itself is already implemented.

**Ownership decision:** issue listing, detail, edit, comment, and issue refresh remain in `GitBugWorkspaceManager`; commit review, proposed commit, commit execution, credential state, push review, confirmation, retry, and cancellation move to `RepositoryWorkspaceManager`. The existing Browser Git Accessor boundary is retained; no Manager-to-Manager call is introduced. No intentional exception exists, so no exception decision is required.

- [x] `Pages/GitBug.razor` and `Components/GitBugIssueDetail.razor` remain **Client** components. They compose passive issue state with passive repository write state and delegate issue, commit, credential, review, push, retry, and cancellation intents; they do not call Accessors, `IJSRuntime`, or browser Git Resources.
- [x] `GitBugWorkspaceManager` remains the **Manager** for issue listing, detail loading, issue edits, comment creation, and issue refresh. It does not retain a second commit, credential, push, or remote-recovery lifecycle.
- [x] `RepositoryWorkspaceManager` remains the single **Manager** for repository write orchestration: changed-file and changed-ref review, local commit, credential session, push confirmation, cancellation, retry, and safe failure mapping. The Git-Bug Client may invoke both Managers from the presentation composition point, but no Manager calls another Manager.
- [x] `BrowserGitAccessor` remains the cohesive **Accessor** implementation for browser Git integration. A focused publication facet may be introduced only to protect the publication contract boundary; its justification is the distinct substitution and validation boundary for reviewed ref publication, not interface-per-method decomposition.
- [x] `browserGitEngine.js` remains the browser Git **Resource** boundary. It will enumerate and validate only the allowed explicit publication namespaces, including `refs/bugs/` and required Git-Bug identity refs, and will not expose credentials or raw Git protocol diagnostics.
- [x] `GitRefUpdate`, publication review, and push request records remain passive Accessor contract data. Ref allowlists, bounds, remote preconditions, and failure classification stay below the Client boundary.
- [x] Existing file editing, branch/history, synchronization, local commit, and generic push flows remain compatible; Git-Bug refs become additional reviewed publication targets rather than an implicit side effect.
- [x] Verification is layered: source-contract and architecture tests for boundaries, .NET tests for Manager state transitions, JavaScript tests for ref validation, and Chromium plus controlled-remote proof for actual remote ref advancement.
- [x] No architectural exception is required. This is a current design review PASS for the boundary above; implementation remains subject to the slice verification gates and must preserve this ownership split.

**Result:** PASS

## M013 S02 T01 focused Git publication contract review

- [x] `BrowserGitAccessor` remains the cohesive **Accessor** implementation and `browserGitEngine.js` remains its browser Git integration Resource; the new `IGitPublicationAccessor` is a focused facet of the existing platform boundary, not a wrapper or pass-through service.
- [x] `IGitPublicationAccessor` is retained because reviewed publication has a distinct validation and substitution boundary while preserving the existing `PushReview` and `PushRequest` workflow contracts for branch-only callers.
- [x] `GitRefUpdate`, `GitPublicationRequest`, `GitPublicationReview`, and `GitPublicationReceipt` are passive **Accessor contract** records. Namespace allowlists, bounded update/precondition counts, object-id validation, and safe update-kind checks remain below the Client boundary.
- [x] The concrete Accessor is registered under both `IBrowserGitAccessor` and `IGitPublicationAccessor` from the same scoped instance; no Manager, Client, Engine, or second implementation is introduced.
- [x] Existing file, branch/history, synchronization, commit, and branch-only push workflows remain compatible. No architectural exception is required.

**Result:** PASS

## M015 cross-slice integration review

**Status:** APPROVED
**Affected layers:** Client, Manager, Accessor, Resource (browser Git integration)
**Dependency direction:** Client -> WorkspaceManager -> Browser Git Accessor contracts -> BrowserGitAccessor -> browser Git integration
**Interfaces added or retained:** `IBrowserGitAccessor` and `IGitBugAccessor` retained as Accessor seams; no Engine interface or Git-Bug Engine proposed.
**Interface justifications:** `IBrowserGitAccessor` isolates the browser Git platform dependency and supplies a Manager testing seam; `IGitBugAccessor` preserves the distinct Git-Bug integration contract within the same Accessor boundary.
**Declared review scope:** Evaluate only the M015 boundary map and the supplied S01, S02, and S03 evidence for cross-slice integration. Do not require redundant direct dependencies or invent boundaries; transitive dependency chains are valid.
**Ownership assertions:** WorkspaceManager owns application use-cases, workspace state, cancellation, and safe failures; BrowserGitAccessor owns Git and Git-Bug integration and DTO contracts; no Git-Bug Engine exists without separate deterministic policy justification.

- [x] **Client:** Home, Git-Bug, Program, imports, and repository child components consume Manager-owned, Client-safe contracts and do not depend directly on Accessor types or coordinate overlapping Managers.
- [x] **Manager:** one sealed partial WorkspaceManager owns repository, VCS, synchronization, and Git-Bug use-cases, shared WorkspaceState, cancellation, and bounded safe failures.
- [x] **Accessor:** IBrowserGitAccessor and IGitBugAccessor remain the justified integration seams; BrowserGitAccessor owns Git/Git-Bug integration and DTO contracts, with no unjustified Engine or legacy Services/Git wrapper.
- [x] **Dependency direction:** Client -> WorkspaceManager -> Browser Git Accessor contracts -> BrowserGitAccessor -> browser Git integration is enforced by the focused architecture and design tests.
- [x] **Verification:** `npm test --prefix AlmToo` (124 passed), `dotnet test AlmToo.sln --no-restore` (78 passed), repository browser acceptance (4 passed), and Git-Bug write/recovery/repeatability acceptance (5 passed).
- [x] No architectural exception is required.

**Result:** PASS

## M013 S04 T03 central publication acceptance review

- [x] **Client:** the Git-Bug and repository write components render passive Manager state and submit explicit issue edit/comment, changed-ref review, credential, confirmation, publish, retry, and recovery intents. They do not call `IGitPublicationAccessor`, JavaScript, or controlled-remote infrastructure directly.
- [x] **Manager:** `GitBugWorkspaceManager` owns issue edits/comments and refresh; `RepositoryWorkspaceManager` owns changed-ref review, credential presence, explicit confirmation, publication, credential clearing, refresh, and safe failure recovery. No Manager-to-Manager call or duplicate publication lifecycle was introduced.
- [x] **Accessor:** `IGitPublicationAccessor` remains a focused facet of the existing `BrowserGitAccessor`, protecting the reviewed-ref validation and browser/remote publication boundary. It accepts only credential-free reviewed requests and preserves the existing branch-only push facet.
- [x] **Resource and test infrastructure:** `browserGitEngine.js` owns local Git-Bug object/ref mutation and browser Git transport mechanics; the disposable controlled smart-HTTP remote and fixture snapshot helpers are test infrastructure, not application services or dependencies.
- [x] **Verification placement:** contract and architecture tests enforce layer direction and passive records; Node tests prove refs/object reachability and safe failure snapshots; Chromium tests prove explicit review, credential entry, confirmation, publication, authentication rejection, remote-ahead recovery, duplicate prevention, and browser diagnostics.
- [x] **Interface justification:** `IGitPublicationAccessor` protects the distinct reviewed-publication validation/substitution boundary at the existing browser Git platform seam. No interface-per-method or pass-through service was added.
- [x] No architectural exception is required.

**Result:** PASS

## M016 S01-S04 conflict reconciliation planning review

**Status:** APPROVED
**Affected layers:** Client, Manager, Engine, Accessor, Resource
**Dependency direction:** Client -> WorkspaceManager -> ConflictReconciliationEngine and BrowserGitAccessor -> browser Git Resource
**Interfaces added or retained:** Retain `IBrowserGitAccessor`; add only typed Manager-owned reconciliation contracts if implementation requires a new boundary; no pass-through interface is planned.
**Interface justifications:** `IBrowserGitAccessor` isolates browser Git transport and remote snapshot/ref access as the existing platform boundary; any new interface must protect a genuine substitution or platform dependency and must be justified again at the affected slice review stop.
**Ownership decisions:** WorkspaceManager owns review/apply orchestration, lifecycle, cancellation, and safe Client state. A cohesive concrete Engine owns deterministic local-versus-remote comparison, conflict classification, and resolution policy. BrowserGitAccessor owns remote refs, snapshots, tree/blob access, and mutation integration. Clients render review and explicit confirmation only.
**Safety constraints:** Remote inspection is non-mutating; apply requires explicit reviewed intent; cancellation, auth failure, stale remote state, malformed data, and partial write failures preserve or restore local workspace safety; raw Accessor diagnostics and credentials do not cross the Manager boundary.
**Verification plan:** Contract and architecture tests enforce dependency direction and typed outcomes; Engine tests cover deterministic classifications and resolution policy; Manager tests cover state transitions, cancellation, stale-result protection, and failure recovery; controlled browser tests cover inspect -> review -> apply, conflict handling, retry, and no secret leakage.
**Exceptions or risks:** A new Engine is justified because conflict classification is deterministic domain policy rather than browser integration. No other architectural exception is approved. Each slice must re-open this review before implementation if the service, contract, Accessor, Resource, or retained interface boundary materially changes.
**Result:** PASS

## M016 S02 T01 remote snapshot Accessor review

**Status:** APPROVED
**Affected layers:** Accessor, Resource (browser Git integration)
**Dependency direction:** WorkspaceManager -> `IBrowserGitAccessor` -> `BrowserGitAccessor` -> `browserGitEngine.js` Resource
**Interfaces added or retained:** Retain `IBrowserGitAccessor`; extend its existing typed contract with credential-free remote snapshot acquisition. No new pass-through interface.
**Interface justifications:** `IBrowserGitAccessor` is the existing platform substitution seam for browser Git and remote transport; the snapshot operation belongs on that cohesive boundary because it must own browser cancellation, response validation, bounded failures, and redaction.
**Ownership decisions:** The Resource performs only non-mutating remote ref/commit/tree/blob reads through isomorphic-git. The Accessor normalizes and validates bounded DTOs and exposes only safe typed snapshot metadata; browser errors, credentials, tokens, URLs beyond the repository identity, and raw transport diagnostics remain below the boundary. WorkspaceManager and the reconciliation Engine consume the typed result without depending on browser or transport details.
**Interface review:** PASS. No checkout, file write, commit, push, publication, or apply path is reachable from remote inspection. Existing `IGitBugAccessor` and publication/synchronization facets remain unchanged.

## M016 S02 T02 WorkspaceManager remote review implementation review

**Status:** APPROVED
**Affected layers:** Client, Manager, Engine, Accessor, Resource
**Dependency direction:** Client -> WorkspaceManager -> ConflictReconciliationEngine and retained IBrowserGitAccessor -> BrowserGitAccessor -> browser Git Resource
**Interfaces added or retained:** Retain `IBrowserGitAccessor`; no new interface.
**Interface justifications:** `IBrowserGitAccessor` remains the genuine browser/platform substitution seam, while the Manager facet is a cohesive partial implementation rather than a pass-through interface.
**Ownership decisions:** WorkspaceManager owns repository-context validation, operation gating, cancellation, generation fencing, safe state publication, and mapping typed snapshot data to Engine input. ConflictReconciliationEngine remains deterministic and I/O-free; Accessor and Resource details remain hidden.
**Interface review:** PASS. Review is read-only and cannot reach checkout, write, commit, push, publication, or apply paths.

