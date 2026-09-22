# Browser Git Architecture Design

## Purpose and scope

This document is the source-accurate architecture baseline and migration contract for AlmToo's browser-local Git workspace. It covers repository clone or reopen, file browsing and text editing, status, local commit, reviewed GitHub push, tab-scoped credential recovery, JavaScript module lifetime, browser storage, remote transport, and the tests and asset pipeline that protect those behaviors.

This document records the current implementation before refactoring and the approved iDesign direction from D010 through D013. It does **not** authorize runtime changes or change existing behavior. The detailed migration may proceed only after the approval gate below records a real human approval.

## Design status and approval gate

| Field | Value |
|---|---|
| Design status | Pending human approval |
| Implementation authorization | Blocked |
| Reviewer | Not recorded |
| Review date | Not recorded |
| Source revision | Not recorded |
| Disposition | Pending human approval |

**Gate rule:** S02 implementation is blocked while the design status is `Pending human approval` or `Rejected`. An `Approved` record is genuine only when reviewer, review date, source revision, and an approved disposition are recorded from an actual human decision. Passing automated contract tests proves document completeness only; it never grants implementation authorization.

## Current-state inventory

Every path in this table is tracked source or a tracked test/configuration surface. The JavaScript modules are implementation details of the **Accessor boundary**, not separate workflow services.

| Current source surface | Current responsibility and dependencies | Current iDesign condition | Intended destination |
|---|---|---|---|
| `AlmToo/Pages/Home.razor` | Blazor route and UI; holds workspace state and orchestrates open, browse, read, save, status, commit, review, credential, and push flows. Calls `IBrowserGitService` and credential `IJSRuntime` directly. | Client with legacy Manager and Accessor responsibilities mixed in. | **Client** calling concrete `GitWorkspaceManager` only for Git workflows. |
| `AlmToo/Components/RepositoryFileBrowser.razor` | Renders Resource values and raises directory/file selection callbacks. | Client; no platform I/O. | **Client**, preserving presentation-only responsibility. |
| `AlmToo/Components/RepositoryChangedFiles.razor` | Renders changed-file Resources and raises refresh callbacks. | Client; no platform I/O. | **Client**, preserving presentation-only responsibility. |
| `AlmToo/Components/RepositoryCommitForm.razor` | Captures and performs presentation validation of commit fields, then raises a commit request callback. | Client; input mapping and UI feedback. | **Client**, mapping input to a Manager request. |
| `AlmToo/Program.cs` | WebAssembly composition root; registers `IBrowserGitService` to `BrowserGitService`. | Composition Resource, currently wires Client directly to Accessor. | Composition **Resource** registering concrete Manager and Accessor interface/implementation. |
| `AlmToo/Services/Git/IBrowserGitService.cs` | Current C# contract for all browser Git operations. | Platform boundary is credible, but name/location obscure Accessor ownership. | Rename/move to the single **Accessor** interface `IBrowserGitAccessor`. |
| `AlmToo/Services/Git/BrowserGitService.cs` | Imports/caches the Git JavaScript module, initializes it once, invokes operations, translates envelopes/DTOs/failures, validates push identities, redacts push errors, and disposes the module. Depends on `IJSRuntime`. | **Accessor** currently misnamed and misplaced. | Rename/move to `BrowserGitAccessor` under `AlmToo/Accessors/Git`; retain platform translation and lifetime only. |
| `AlmToo/Services/Git/GitOperationResult.cs` | Defines operation results, failure kinds, requests, repository/file/status/commit/push records and enums. | Data contracts mixed into legacy service folder. | Data-only Git **Resource** records, co-located with the Accessor boundary contracts or explicitly owned Manager state. |
| `AlmToo/wwwroot/js/browserGitEngine.js` | Loads browser vendor assets, owns lightning-fs/isomorphic-git integration and active repository state, performs filesystem/Git/remote operations, validates paths and push coordinates, and returns structured envelopes. | JavaScript implementation detail of the **Accessor** boundary despite the legacy `Engine` filename. | Accessor-owned JavaScript implementation; it is not an iDesign Engine or workflow service. |
| `AlmToo/wwwroot/js/pushCredentialSession.js` | Encapsulates the one session-storage credential key and input-only store/presence/get/forget operations with fail-closed behavior. | JavaScript implementation detail currently invoked by the Client. | Accessor-owned credential-session integration reached only through `IBrowserGitAccessor`. |
| `AlmToo/tools/copy-git-browser-assets.mjs` | Bundles Buffer and copies pinned isomorphic-git, HTTP, and lightning-fs assets into the web root. | Build-time integration Resource. | Accessor asset pipeline **Resource**, preserving deterministic asset availability. |
| `AlmToo/package.json` | Pins browser Git packages and runs asset copy and Node behavioral suites. | Build/test Resource. | Build/test **Resource** with the design contract included. |
| `AlmToo/tests/browserGitEngineTests.mjs` | Exercises browser Git JavaScript behavior, C# source contracts, malformed input, storage/I/O failure, push classification, and redaction. | Accessor integration and source-contract verification. | **Accessor integration tests**, renamed/relocated only at a buildable migration checkpoint. |
| `AlmToo/tests/pushCredentialSessionTests.mjs` | Exercises tab credential presence, retrieval, forgetting, corruption, unavailable storage, and no logging/serialization sinks. | Accessor mechanism tests. | **Accessor integration tests** for credential-session behavior. |
| `AlmToo/tests/homePageContractTests.mjs` | Guards current Client markup, orchestration calls, reviewed push, recovery messaging, and credential-safe UI behavior. | Client contract tests that expose current direct dependencies. | Split into **Client contract** and concrete **Manager workflow** coverage during migration. |
| `AlmToo/tests/browserGitDesignContractTests.mjs` | Reads tracked architecture/source files and guards this design's sections, mappings, decisions, review, security, migration/testing, and honest approval state. | Architecture contract test. | Architecture verification **Resource** retained through S03. |
| `AlmToo/e2e/credentialSafety.spec.mjs` | Browser UAT for tab isolation, reload presence, forget, authentication rejection, empty input, and absence of credentials from rendered markup/errors. | End-to-end Client/Manager/Accessor proof. | Verification **Resource** retained as unchanged browser UAT. |
| `AlmToo/e2e/liveGitHubPush.spec.mjs` | Optional live GitHub UAT for reviewed SHA push, real rejection cleanup, non-fast-forward protection, no automatic retry, and secret-safe diagnostics. | End-to-end external-boundary proof. | Verification **Resource** retained as opt-in live browser UAT. |

### Vendor and generated asset surfaces

`AlmToo/wwwroot/js/vendor/buffer/buffer.min.js`, `AlmToo/wwwroot/js/vendor/isomorphic-git/index.umd.min.js`, `AlmToo/wwwroot/js/vendor/isomorphic-git/http-web.umd.js`, and `AlmToo/wwwroot/js/vendor/lightning-fs/lightning-fs.min.js` are generated/copied Resource assets consumed only by the Accessor's JavaScript implementation. They are not application services and must not be imported by Clients or Managers.

## Current call and dependency graph

```text
Program.cs (composition)
  -> IBrowserGitService -> BrowserGitService -> IJSRuntime
                                             -> browserGitEngine.js
                                                -> vendor scripts
                                                -> lightning-fs / browser storage
                                                -> isomorphic-git / CORS proxy / remote Git

Home.razor (Client + current orchestration)
  -> IBrowserGitService
  -> IJSRuntime -> pushCredentialSession.js -> sessionStorage
  -> RepositoryFileBrowser.razor (Client child)
  -> RepositoryChangedFiles.razor (Client child)
  -> RepositoryCommitForm.razor (Client child)

copy-git-browser-assets.mjs -> node_modules -> wwwroot vendor assets
Node suites -> tracked JS/C#/Razor/design sources
Browser UAT -> rendered Client -> current C#/JS/storage/remote stack
```

The two direct edges from `Home.razor` to `IBrowserGitService` and Git-related `IJSRuntime` are current-state facts and target-state violations under D011. They remain only until the ordered migration rewires the Client through the Manager.

## Target layer map

| Target unit | Layer | Cohesive responsibility | Allowed dependencies | Forbidden dependencies |
|---|---|---|---|---|
| `Home.razor` and repository child components | Client | Render workspace state, collect user input, and trigger use cases. | Concrete `GitWorkspaceManager`, Git Resources, UI framework/utilities. | `IBrowserGitAccessor`, `BrowserGitAccessor`, Git-related `IJSRuntime`, storage, remote Git. |
| `GitWorkspaceManager` | Manager (concrete) | Own one browser Git workspace session and orchestrate open/browse/edit/status/commit/review/push/credential recovery flows. | `IBrowserGitAccessor`, Resources, utilities. | Other Managers, direct `IJSRuntime`, vendor JS, browser storage, remote Git. |
| `IBrowserGitAccessor` | Accessor interface | Express the replaceable JavaScript/browser platform boundary for Manager calls and Manager-test substitution. | Data-only Resources. | Managers, Clients, workflow policy. |
| `BrowserGitAccessor` | Accessor implementation | Translate typed calls/results to browser Git and credential-session JavaScript, own module initialization/lifetime, and sanitize platform failures. | `IJSRuntime`, Accessor-owned JS modules/assets, Resources, utilities. | Managers, Clients, application workflow sequencing. |
| Git requests/results/state records and enums | Resource | Carry immutable/data-only requests, values, status, review identity, and safe result signals. | Other data-only types only. | I/O, orchestration, UI behavior, mutable module lifetime. |
| `browserGitEngine.js` and `pushCredentialSession.js` | Accessor implementation detail | Integrate browser filesystem, Git transport, session storage, and asset runtime behind the C# Accessor. | Browser/platform APIs and vendor assets. | Client workflow ownership or independent service registration. |
| Asset-copy script, package scripts, vendor assets, composition root | Resource | Build/runtime composition and static asset availability. | Concrete registrations and build dependencies. | Use-case orchestration or domain policy. |

No new iDesign Engine is required by the baseline: the present deterministic checks are tightly coupled to integration contract validation. T02 must not invent an Engine merely to distribute methods. If independent domain policy emerges, that requires an explicit, justified design update.

## Responsibilities and dependency rules

1. Clients depend only on the concrete `GitWorkspaceManager`, data-only Resources, and UI utilities for Browser Git work.
2. The concrete Manager may call `IBrowserGitAccessor`; it never calls another Manager and never imports JavaScript or performs platform I/O.
3. The Accessor owns JavaScript interop, module import/disposal, browser filesystem/storage, Git vendor integration, remote transport, DTO translation, and platform failure sanitization. It does not choose multi-step UI workflow.
4. Resources contain data only. They do not call services or implement I/O or orchestration.
5. `browserGitEngine.js` is a legacy filename, not an iDesign Engine classification. Both JavaScript modules remain private implementation mechanisms of `BrowserGitAccessor`.
6. Credential values may cross only the explicit Client input -> Manager push request -> Accessor immediate-use -> credential/transport path. They must never be retained in Resource models or Manager state, returned in results, logged, rendered, serialized for diagnostics, or included in exception text.
7. The single Accessor interface protects the JavaScript/browser platform boundary and provides Manager-test substitution; the Manager stays concrete because an interface would be pass-through.

### Locked decisions D010-D013

| Decision | Constraint implemented by this design |
|---|---|
| D010 | Treat the current `BrowserGitService` as an **Accessor**, move it beneath `Accessors`, and rename the implementation and platform-bound interface to `BrowserGitAccessor` and `IBrowserGitAccessor`. |
| D011 | Insert one cohesive concrete `GitWorkspaceManager`; Clients do not call the Accessor or Git-related `IJSRuntime` directly. |
| D012 | Retain exactly one Accessor interface for the JavaScript/browser platform boundary and Manager-test substitution; keep the Manager concrete and reject a pass-through Manager interface. |
| D013 | Keep this design at `AlmToo/Accessors/Git/DESIGN.md` and require explicit human approval before S02 implementation. |

These decisions are locked for this design. A necessary exception must be recorded as a separate architectural decision, not silently introduced here.

## Contracts

The current public operation set is `initialize`, `cloneOrOpen`, `listFiles`, `readTextFile`, `writeTextFile`, `getStatus`, `commit`, `inspectPush`, and `push`. The refactor preserves the behavior and externally observable state of all operations.

`GitOperationResult` and `GitOperationResult<T>` expose inspectable `Operation`, `Succeeded`, safe user-facing `Message`, optional technical `Diagnostic`, and optional allowlisted `FailureKind`; generic results additionally carry `Value`. JavaScript responses use the corresponding lower-camel fields. C# rejects null/malformed envelopes, mismatched operation names, missing messages, invalid push coordinates, and malformed commit SHAs as structured failures.

The result signals are the current observability surface: operation and success localize the failed operation, message is UI-safe, diagnostic is technical but must be credential-safe, and push failure classification is limited to credential rejected, remote ahead, network unavailable, unsupported ref, or unknown. UI workflow state (`isOpening`, loading/saving/committing/reviewing/pushing flags, messages, `data-push-failure`, and credential presence/replacement state) remains inspectable without exposing a credential value.

Initialization and the Git module import are task-cached and idempotent per scoped Accessor instance. The active JavaScript repository and browser filesystem live for that module/session lifetime. A completed module reference is disposed asynchronously; incomplete/failed imports are not awaited during disposal. Cancellation is accepted at every C# operation boundary and becomes a structured canceled result rather than escaping. Current Home calls do not supply a cancellation token; changing that behavior is outside this baseline.

Push contracts preserve an immutable reviewed `RepositoryUrl`, `Branch`, `OutgoingCommitId`, and `DestinationRef`. The Accessor and JavaScript revalidate those coordinates against current state immediately before one non-force push attempt. The personal access token is a separate input-only argument and never a Resource field.

## Current workflow and data flows

### Clone or open and initialization

1. `Home.razor` trims the repository URL, derives a sanitized workspace name, resets UI workspace state, and invokes `CloneOrOpenAsync`.
2. `BrowserGitService` lazily imports `browserGitEngine.js`, performs task-cached `initialize`, then invokes `cloneOrOpen`.
3. JavaScript loads static vendor assets, initializes persistent lightning-fs storage, opens an existing `.git` worktree when valid, or shallow-clones one branch through the configured CORS proxy.
4. A successful `RepositoryInfo` becomes current UI state; Home then lists `/` and refreshes status. A failed or thrown boundary yields a safe status message.

### Browse and read

1. Selecting a directory clears selected-file state, normalizes the path, calls `ListFilesAsync`, and then refreshes status.
2. JavaScript resolves only repository-relative paths, rejects parent traversal, reads filesystem entries, classifies editable text by extension/size, and sorts directories before files.
3. Selecting an editable listed file calls `ReadTextFileAsync`; the returned UTF-8 text initializes both saved and editable Client state. Missing, stale, unsupported, binary, or oversized selections remain recoverable UI failures.

### Edit and write

1. The Client tracks dirty state by comparing editable text to the last loaded/saved content.
2. Save calls `WriteTextFileAsync` for the selected safe path and current text.
3. On success only, the saved snapshot is updated and changed-file status is refreshed. Failures preserve editable Client content and expose a safe message.

### Status refresh

Home invokes `GetStatusAsync` after open/list/save and explicit refresh. JavaScript maps the isomorphic-git status matrix to changed-file Resources. Failure clears displayed changed files and retains an operation-specific safe message.

### Commit

1. `RepositoryCommitForm.razor` trims and validates non-empty message/name and email shape, then raises `CommitRequest`.
2. Home calls `CommitAsync`; JavaScript validates fields, computes changed files, stages adds/removals, rejects no-change commits, and creates a browser-local commit.
3. Success stores `CommitInfo`, invalidates any prior push review/confirmation, and refreshes status. Failure preserves the worktree and shows fixed recovery text.

### Push review and confirmation

1. Review calls `InspectPushAsync`; JavaScript reads and canonicalizes a credential-free GitHub HTTPS origin, a supported local `refs/heads/*` branch, and full local HEAD SHA.
2. Home displays the immutable origin, branch, and SHA, clears prior confirmation, and requires explicit checkbox confirmation plus credential presence.
3. Push copies that reviewed identity into `PushRequest`, retrieves the token immediately before the attempt, and calls `PushAsync` once.
4. JavaScript re-reads current origin/branch/HEAD, rejects stale review state, and issues one matching non-force transport attempt. There is no automatic retry.
5. Success reports the pushed SHA and refreshes status. Failure preserves local HEAD and reviewed state and provides classified manual recovery guidance.

### Session credential acquisition, forget, and retry

1. First render imports `pushCredentialSession.js` and restores presence only; it never restores the value into Client state.
2. Submission clears the password input before storing the trimmed value in tab-scoped `sessionStorage`. Only presence is rendered.
3. Explicit Forget removes the one scoped key. Authentication/permission rejection clears the in-memory attempt value and invokes Forget, then requests a replacement.
4. Missing/corrupt/unavailable storage fails closed. A retry is always a new explicit review/confirmation and push action; network and non-fast-forward failures are never automatically retried.

### Cancellation, module lifetime, disposal, and result propagation

The C# Accessor forwards cancellation to import, initialization, and JS invocation and translates cancellation, `JSException`, unavailable module, malformed payload, and unknown exception paths into structured results. Both C# module caches are scoped to their current owners; `BrowserGitService` and Home dispose successfully imported modules in `DisposeAsync`. Results propagate safe message/value/failure-kind signals upward; Home catches any escaped exception and substitutes fixed UI text rather than rendering diagnostics.

## Failure Modes

| Dependency or boundary | Failure path | Current owner and observable behavior | Recovery invariant |
|---|---|---|---|
| JavaScript module or vendor asset load | Missing asset, script error, import failure, malformed/null envelope | C# Accessor returns `initialize` or operation failure with safe message and technical non-secret diagnostic. | Reopen/reload after assets are restored; no partial success is claimed. |
| Browser storage/filesystem | unavailable IndexedDB/sessionStorage, quota, missing file/worktree, denied read/write | Git JS returns operation-specific failure; credential JS returns false/null and never throws. UI clears only result views, preserving editable text where possible. | Reopen workspace or restore storage; credential path fails closed. |
| Network, CORS proxy, or remote Git | connection loss/timeout/CORS failure, malformed host response | Push is classified `NetworkUnavailable` or safe `Unknown`; clone/open returns safe failure. | Local work/review remain; only manual retry after network repair. |
| Authentication or permission | 401/403/auth rejection | Fixed `CredentialRejected` result; Home forgets session token and requests replacement. | No credential remains in input/result/diagnostic/DOM; explicit replacement and retry required. |
| Non-fast-forward remote | remote advances after review | `RemoteAhead`, one attempt maximum, local HEAD and review preserved. | Reconcile remote manually, review again, confirm again; never force or auto-retry. |
| Cancellation or disposal | canceled interop, navigation/disposal before/after import | Accessor returns structured cancellation; only successfully completed module tasks are disposed. | No exception details reach UI; a later owner may create a fresh scoped instance. |
| Malformed interop payload | null envelope, wrong operation, absent message/value, invalid DTO/SHA/ref | Accessor rejects it as an unexpected response and supplies stable operation/message/error signals. | Fix boundary/runtime and retry; do not accept malformed state. |
| Unsafe/malformed input | unsupported URL, credential-bearing GitHub origin, parent path, missing path/worktree, invalid commit/review | Validation fails before unsafe filesystem/transport use where implemented. | Correct input or reopen; no transport for invalid push review. |
| Secret-bearing external failure | host/error contains PAT or auth headers | JavaScript push classification returns fixed allowlisted diagnostics; C# redacts then sanitizes; UI never consumes raw diagnostics. | Credential persistence, logging, rendering, serialization, and error leakage are prohibited. |

The design introduces no new external dependency. Future failure localization belongs to the Accessor for platform/interop faults, the Manager for workflow/recovery state, and the Client for accessible presentation of safe signals.

## Security and trust boundaries

- Repository URL, repository paths, repository content, filenames, branch/ref values, interop payloads, and commit message/author metadata are **untrusted inputs**.
- Personal access tokens and any equivalent session credentials are **secrets**. They may be held transiently only for explicit storage or one push attempt.
- Browser storage (lightning-fs/IndexedDB and `sessionStorage`), loaded JavaScript/vendor assets, the CORS proxy, and remote Git/GitHub are **external trust boundaries** owned by the Accessor.
- Repository paths must remain beneath the active workspace; parent traversal is rejected. Push origins must be credential-free GitHub HTTPS URLs and exact reviewed branch/SHA/ref coordinates must be revalidated.
- Credential persistence beyond tab-scoped session storage is prohibited. Credentials must not enter local storage, Resource records, Manager state, logs, console output, rendered markup, result messages, diagnostics, serialized errors, or source fixtures.
- External failures are converted to allowlisted push categories and fixed redacted text. Unknown or malformed errors fail closed without reflecting host details.
- The Client can observe only credential presence/replacement state; value retrieval is immediate-use only and must move behind the Accessor boundary.

## Load Profile

Expected use is one browser-local repository and interactive text-file/status operations. No supported numeric repository or concurrency limits have been measured, so this design does not invent them.

The likely first saturation points at a practical 10x workload are browser memory and IndexedDB quota from repository size/file count, followed by JavaScript interop payload size for large listings/text, status/diff CPU for many changed files, and user-perceived wait under remote latency. Current protections include a shallow single-branch clone, a bounded editable-text size, binary/unsupported file exclusion, one active workspace, task-cached module/filesystem initialization, busy-state re-entry guards, a single explicit non-force push attempt, and no automatic retry.

S02/S03 measurement points are operation duration and safe operation/failure-kind counts at the Accessor, Manager busy/re-entry state, listing/status result counts, text payload bytes, storage/quota failures, and remote latency. Measurements must remain credential-free. Potential protections to evaluate from evidence—not assume—are pagination/incremental listing, cancellation, serialized workspace mutations, reduced interop payloads, and documented repository limits.

## Negative Tests

| Negative or boundary scenario | Existing proof or required contract |
|---|---|
| Malformed/unsupported repository request and URL | `AlmToo/tests/browserGitEngineTests.mjs` clone validation; `AlmToo/tests/homePageContractTests.mjs` empty URL and safe open failure. |
| Unsafe/missing repository path and traversal | `browserGitEngineTests.mjs` parent traversal, absent active workspace, and operation-specific file I/O failures. |
| Absent repository/worktree or origin; detached/unsupported ref | `browserGitEngineTests.mjs` inspect-without-open, missing origin, credential-bearing origin, detached and unsupported refs. |
| Invalid commit input and no changes | `browserGitEngineTests.mjs` author/no-change cases; `homePageContractTests.mjs` commit form validation. |
| Absent/invalid/corrupt credential and unavailable session storage | `AlmToo/tests/pushCredentialSessionTests.mjs` empty, corrupt, unavailable, and failing storage cases; `homePageContractTests.mjs` missing-token fail-closed path. |
| Malformed/stale push review and changed origin/branch/SHA | `browserGitEngineTests.mjs` missing credential/review and changed review-state cases. |
| Authentication rejection and credential cleanup | `browserGitEngineTests.mjs`, `AlmToo/e2e/credentialSafety.spec.mjs`, and opt-in `AlmToo/e2e/liveGitHubPush.spec.mjs`. |
| Rejected/non-fast-forward push, one attempt, preserved local work | `browserGitEngineTests.mjs` remote-ahead and no-retry cases; opt-in live stale-push UAT. |
| Secret-bearing/throwing/malformed external failures | `browserGitEngineTests.mjs` token-bearing errors, malformed throwing accessor, fixed fallback, and no-console checks. |
| Module/asset, filesystem, and storage failure | `browserGitEngineTests.mjs` vendor load and file/status I/O failures; `pushCredentialSessionTests.mjs` storage failures. |
| Cancellation, disposal, and async re-entry | C# source contract confirms cancellation translation and completed-module disposal; behavioral concurrency/cancellation coverage is an explicit S02/S03 gap. |

`AlmToo/tests/browserGitDesignContractTests.mjs` additionally fails on missing architecture sections, unmapped tracked source surfaces, absent target assignments, changed D010-D013 constraints, missing credential/redaction/migration/testing coverage, incomplete review results, or malformed approval metadata.

## Migration sequence

The implementation sequence is intentionally high level for T01; T02 must turn each item into buildable and rollback-safe checkpoints without changing behavior:

1. Establish data-only Git Resources and keep compatibility with current C# names.
2. Rename/move `BrowserGitService` and its single interface to the Accessor location; keep JavaScript and static assets Accessor-owned and preserve wire operation names.
3. Move credential-session interop behind the Accessor while preserving tab-only presence/value rules.
4. Introduce the concrete `GitWorkspaceManager` and move workflow state/sequencing from Home without changing messages, review confirmation, retry, or failure behavior.
5. Update DI composition and rewire Clients to the Manager; remove Client-to-Accessor and Git-related Client-to-`IJSRuntime` edges.
6. Relocate/update tests and asset naming only after each compatibility checkpoint passes the existing suite.
7. Remove legacy service names only after source-contract, build, Node, and focused browser checks prove no callers remain.

At every checkpoint, preserve a compiling rollback point and run the relevant existing suite. No checkpoint may weaken credential redaction, exact-reviewed push, local-work preservation, or manual-retry behavior.

## Verification strategy

- **Architecture contract:** `node --test AlmToo/tests/browserGitDesignContractTests.mjs` validates the tracked design/source baseline and approval gate without reading `.gsd`, `.planning`, or `.audits`.
- **Accessor behavior:** `AlmToo/tests/browserGitEngineTests.mjs` and `AlmToo/tests/pushCredentialSessionTests.mjs` protect JS/browser integration, envelopes, failures, push invariants, and credential safety.
- **Current Client contract:** `AlmToo/tests/homePageContractTests.mjs` protects the existing user-visible flow until Manager workflow tests assume orchestration proof.
- **Full Node regression:** `npm test --prefix AlmToo` must retain all behavioral suites while adding the architecture contract.
- **Build:** S02/S03 must run a clean Blazor build after each structural checkpoint.
- **Browser UAT:** `AlmToo/e2e/credentialSafety.spec.mjs` is focused credential/recovery proof; `AlmToo/e2e/liveGitHubPush.spec.mjs` is opt-in real-remote proof when its fixture is explicitly available.
- **S03 source enforcement:** add non-brittle checks forbidding Client-to-Accessor, Git-related Client-to-`IJSRuntime`, Manager-to-Manager, Accessor-to-Manager/Engine, platform I/O outside the Accessor, and behavior in Resources.

Focused assertion messages are required so a missing section, mapping, decision constraint, review result, or approval field can be repaired without interpreting one aggregate failure.

## Risks and extension points

| Risk | Control / extension point |
|---|---|
| Refactor changes behavior hidden in the 966-line Home Client. | Move one workflow at a time behind the concrete Manager and preserve source/behavior/browser proofs at each checkpoint. |
| Legacy JavaScript filename implies an iDesign Engine. | Treat it explicitly as Accessor implementation; rename only with asset/import/test compatibility. |
| Credential movement increases exposure. | Preserve immediate-use retrieval, tab scope, fixed redaction, no Resource storage, and rejection-driven forget tests. |
| Browser quota/memory/interop pressure is unmeasured. | Add credential-free metrics and evidence-based limits before introducing pagination or batching. |
| Remote/CORS/vendor behavior varies outside the app. | Keep translations and classifications in the Accessor and retain opt-in live UAT. |
| A Manager interface or method-per-service decomposition appears during migration. | D012 keeps the Manager concrete; any additional interface requires a current boundary/substitution justification and review. |
| Future providers, auth methods, branches, pull/merge, or multi-workspace use expand scope. | Extend the Accessor contract and cohesive Manager only after separate requirements/decisions; do not weaken reviewed-state or secret rules. |

## Embedded iDesign review

This is the required review for the T01 source baseline and target direction. It reviews the proposed architecture, while recording present Client-to-Accessor and Client-to-JS edges as migration gaps rather than approving them as target dependencies.

### 1. Layer assignments

| Component | Layer | Cohesive responsibility | Allowed dependencies | Forbidden dependencies |
|---|---|---|---|---|
| Home and repository components | Client | UI input/output | concrete Manager, Resources, UI utilities | Accessor, Git JS/platform I/O |
| `GitWorkspaceManager` | Manager | workspace use-case orchestration and state | Accessor interface, Resources, utilities | other Managers, JS/platform APIs |
| `IBrowserGitAccessor` / `BrowserGitAccessor` | Accessor | browser Git/credential integration and translation | JS/browser/vendor mechanisms, Resources, utilities | Clients, Managers, workflow policy |
| Git records/results/enums | Resource | immutable/data-only contracts | data types | I/O, orchestration, UI |
| JS modules and vendor assets | Accessor detail / Resource asset | platform mechanism and static runtime | browser/vendor APIs | independent workflow service role |

**Result:** PASS — every current and target surface has one explicit assignment; current violations are named migration work.

### 2. Dependency direction

- [x] Target calls follow the dependency matrix in `docs/IDESIGN.md`.
- [x] The concrete Manager does not call other Managers.
- [x] No Engine is invented; no Engine depends on orchestration, transport, or UI.
- [x] The Accessor owns integration but not workflow policy.
- [x] Clients and Resources contain no target domain algorithms.

**Result:** PASS — target direction is Client -> concrete Manager -> Accessor interface -> platform; current illegal edges are explicitly forbidden and scheduled for removal.

### 3. Interface justification

| Interface | Implementations | Callers | Current boundary or substitution need | Keep, remove, or defer |
|---|---:|---:|---|---|
| `IBrowserGitAccessor` (renamed from `IBrowserGitService`) | 1: `BrowserGitAccessor` | 1 production Manager plus Manager tests | JavaScript/browser platform boundary and necessary Manager-test substitution | Keep exactly one Accessor interface. |
| Proposed Manager interface | 0 | 0 | No alternate implementation or external boundary; it would pass through with its caller | Do not add; keep Manager concrete. |

**Result:** PASS — the single Accessor interface protects the JavaScript/browser platform boundary and provides Manager-test substitution, while the Manager stays concrete because an interface would be pass-through.

### 4. Cohesion and decomposition

- [x] Each target service has one cohesive responsibility rather than one method or function.
- [x] The Manager owns sequencing/state; the Accessor owns translation/lifecycle/boundary isolation, so neither is a thin pass-through.
- [x] Orchestration remains in one cohesive Manager.
- [x] Browser Git and credential mechanisms that change with platform integration remain together behind one Accessor boundary.
- [x] No functional decomposition is disguised as a service/interface hierarchy.

**Result:** PASS — the design rejects interface-per-class and method-per-service decomposition.

### 5. Verification placement

| Behavior | Owning layer | Verification type | Evidence |
|---|---|---|---|
| Browser Git, filesystem, remote, result translation | Accessor | integration/contract | `browserGitEngineTests.mjs`; future C# Accessor tests |
| Credential session and redaction | Accessor | integration/security contract | `pushCredentialSessionTests.mjs`, engine redaction cases |
| Workspace sequencing and recovery state | Manager | workflow | extracted Manager tests required in S02 |
| Markup, accessible states, explicit confirmation | Client | contract/UAT | `homePageContractTests.mjs`, `credentialSafety.spec.mjs` |
| Real reviewed/rejected/non-fast-forward push | Cross-layer | opt-in UAT | `liveGitHubPush.spec.mjs` |
| Architecture shape and approval honesty | Architecture Resource | contract | `browserGitDesignContractTests.mjs` |

- [x] No independent Engine policy currently requires a deterministic unit suite.
- [x] Accessor behavior has integration-boundary coverage.
- [x] Manager behavior has an explicit S02 workflow-test destination.
- [x] Client and Resource behavior is covered by source contracts and browser UAT.

**Result:** PASS — verification is assigned to owning boundaries; the missing Manager suite is planned with the extraction, not silently claimed as current evidence.

### 6. Exceptions

None. The current direct Client dependencies are legacy migration gaps, not intentional target exceptions. If they cannot be removed, a separate architectural decision with rationale and revisit trigger is required.

### Compliance statement

> This design follows the project iDesign policy. Layer assignments and dependency direction were reviewed. Every retained interface has a current architectural justification, and no unexplained interface-per-class or pass-through decomposition remains.

**Overall result:** PASS — architecture baseline/design only. Implementation remains blocked by the independent human approval gate.
