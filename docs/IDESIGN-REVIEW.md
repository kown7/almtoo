# iDesign Architecture Review

Use this checklist for every architecture-affecting plan, implementation, refactor, and review. Copy the result into the relevant plan, summary, pull request, or review artifact.

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
