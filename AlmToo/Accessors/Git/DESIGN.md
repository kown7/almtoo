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
6. Credential values may cross only the explicit Client input -> Manager credential method -> Accessor immediate-use storage path; push retrieval remains entirely inside the Accessor immediately before transport. They must never be retained in Resource models or Manager state, returned in results, logged, rendered, serialized for diagnostics, or included in exception text.
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

### Target Resource shapes and placement

`AlmToo/Services/Accessors/BrowserGit/Interface/BrowserGitResources.cs` owns the data-only boundary Resources: `GitOperationResult`, `GitOperationResult<T>`, `GitOperationFailureKind`, `RepositoryOpenRequest`, `RepositoryInfo`, `RepositoryFileEntry`, `RepositoryFileKind`, `TextFileContent`, `ChangedFile`, `GitChangeKind`, `CommitRequest`, `CommitInfo`, `PushReview`, `PushRequest`, and `PushResult`. Their fields retain the current shapes in `Services/Git/GitOperationResult.cs`; no record contains a credential, service reference, callback, mutable module handle, or behavior. `AlmToo/Services/Managers/GitWorkspace/Service/GitWorkspaceState.cs` is a separate immutable, data-only Manager snapshot containing the current repository, listing/path/selection/edit buffer, changed files, commit and push-review values, busy flags, safe messages/success flags, push failure kind, and credential presence/replacement flags. It contains no credential value or technical diagnostic.

`GitOperationResult` and `GitOperationResult<T>` expose inspectable `Operation`, `Succeeded`, safe user-facing `Message`, optional technical `Diagnostic`, and optional allowlisted `FailureKind`; generic results additionally carry `Value`. JavaScript responses use the corresponding lower-camel fields. C# rejects null/malformed envelopes, mismatched operation names, missing messages, invalid push coordinates, and malformed commit SHAs as structured failures.

### Single Accessor contract

The only retained service interface is `AlmToo.Services.Accessors.BrowserGit.Interface.IBrowserGitAccessor` at `AlmToo/Services/Accessors/BrowserGit/Interface/IBrowserGitAccessor.cs`. Its implementation is `AlmToo.Services.Accessors.BrowserGit.Service.BrowserGitAccessor` at `AlmToo/Services/Accessors/BrowserGit/Service/BrowserGitAccessor.cs`; namespaces mirror the required iDesign folder structure. The contract-level shape is:

```csharp
public interface IBrowserGitAccessor : IAsyncDisposable
{
    ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(RepositoryOpenRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<IReadOnlyList<RepositoryFileEntry>>> ListFilesAsync(string path = "/", CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<TextFileContent>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<CommitInfo>> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushReview>> InspectPushAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> StoreCredentialAsync(string credential, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushResult>> PushAsync(PushRequest request, CancellationToken cancellationToken = default);
}
```

`PushAsync` retrieves the credential from the Accessor-owned credential module immediately before transport and clears its local variable in `finally`; it never returns or accepts a credential Resource. `StoreCredentialAsync` accepts the secret as an input-only argument and must not retain it in C# fields. This is an intentional target signature change from the legacy service and removes credential retrieval from the Client while preserving tab-scoped storage behavior.

### Concrete Manager contract and state ownership

`AlmToo.Services.Managers.GitWorkspace.Service.GitWorkspaceManager` at `AlmToo/Services/Managers/GitWorkspace/Service/GitWorkspaceManager.cs` is a scoped concrete service, with no Manager interface. It owns one `GitWorkspaceState` snapshot and the full workspace use-case sequencing:

```csharp
public sealed class GitWorkspaceManager : IAsyncDisposable
{
    public GitWorkspaceState State { get; }
    public event Action? StateChanged;
    public Task OpenRepositoryAsync(string repositoryUrl, CancellationToken cancellationToken = default);
    public Task BrowseAsync(string path, CancellationToken cancellationToken = default);
    public Task SelectFileAsync(string path, CancellationToken cancellationToken = default);
    public void UpdateEditableContent(string content);
    public Task SaveSelectedFileAsync(CancellationToken cancellationToken = default);
    public Task RefreshStatusAsync(CancellationToken cancellationToken = default);
    public Task CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);
    public Task ReviewPushAsync(CancellationToken cancellationToken = default);
    public Task StoreCredentialAsync(string credential, CancellationToken cancellationToken = default);
    public Task ForgetCredentialAsync(CancellationToken cancellationToken = default);
    public Task PushReviewedCommitAsync(CancellationToken cancellationToken = default);
}
```

The Client supplies transient input, invokes one use case, renders `State`, and subscribes/unsubscribes to `StateChanged`; it does not interpret diagnostics or orchestrate follow-up Accessor calls. The Manager maps safe results to the existing UI messages and state transitions. `DisposeAsync` cancels its lifetime token, unsubscribes/releases state notifications, and disposes the scoped Accessor once; the Client disposes only its Manager subscription, not JavaScript modules.

### Initialization, concurrency, cancellation, and error invariants

- Module import, vendor loading, filesystem creation, and initialization remain task-cached and idempotent per scoped Accessor. A failed initialization is stable for that scope; reload/new scope is the retry boundary. Only successfully imported modules are asynchronously disposed.
- The Manager admits at most one workspace-mutating use case at a time (`open`, save, commit, push, credential store/forget). Existing busy flags make duplicate UI submissions no-ops. Status/list/read may run only against the current workspace generation; a late result from an older open/selection generation is discarded rather than overwriting newer state.
- Every public async method accepts cancellation. The Manager links caller cancellation with its lifetime token. The Accessor forwards it to import and interop and translates `OperationCanceledException`, `JSException`, unavailable modules, malformed envelopes, and unknown exceptions into safe `GitOperationResult` failures. Cancellation never causes an automatic retry or partial-success state.
- Retry ownership is explicit: module/storage repair requires a fresh scope/reload; open/read/save/status/commit may be manually retried by the user; authentication rejection forgets the token and requires replacement; network, unknown, unsupported-ref, and remote-ahead push failures preserve review/local work and require a new explicit review/confirmation. Neither Manager nor Accessor automatically retries transport.
- Error translation is Accessor-owned for platform/interop failures, Manager-owned for workflow-safe recovery state, and Client-owned only for accessible rendering. `Diagnostic` is inspectable by credential-free tests/telemetry but is never rendered by the Client.

The result signals are the observability surface: operation and success localize the failed operation, message is UI-safe, diagnostic is technical but credential-safe, and push failure classification is limited to credential rejected, remote ahead, network unavailable, unsupported ref, or unknown. Manager workflow state (opening/loading/saving/committing/reviewing/pushing flags, messages, `data-push-failure`, and credential presence/replacement state) remains inspectable without exposing a credential value.

Push contracts preserve an immutable reviewed `RepositoryUrl`, `Branch`, `OutgoingCommitId`, and `DestinationRef`. The Accessor and JavaScript revalidate those coordinates against current state immediately before one non-force push attempt. A personal access token is never a Resource field, Manager field, result, diagnostic, event argument, or log value.

## Target workflow sequences

All arrows below are target calls; platform details after `BrowserGitAccessor` remain private Accessor implementation.

### Clone or open

1. Client calls `GitWorkspaceManager.OpenRepositoryAsync(url, ct)`; Manager trims/validates input, creates the same sanitized workspace name, increments the workspace generation, clears stale workspace views, and marks opening.
2. Manager calls `IBrowserGitAccessor.CloneOrOpenAsync(RepositoryOpenRequest, ct)`.
3. Accessor performs idempotent import/initialize and calls `browserGitEngine.js`; JS opens persistent lightning-fs state or shallow-clones through the remote/CORS boundary.
4. On success Manager stores `RepositoryInfo`, then invokes Accessor list `/` and status in the current generation. On failure it publishes the existing safe open message. A stale/canceled generation cannot publish results.

### Browse and read

1. Client calls `BrowseAsync(path)` or `SelectFileAsync(path)`; Manager validates current repository/list membership and updates busy/selection state.
2. Manager calls Accessor `ListFilesAsync` or `ReadTextFileAsync`; Accessor normalizes envelopes while JS enforces repository-relative paths and editable-file rules.
3. Manager accepts only the current generation/selection result, stores Resource values and safe messages, and refreshes status after browse as today. Unsupported/stale selections are recoverable and never trigger platform reads.

### Edit and save

1. Client sends text changes to `UpdateEditableContent`; Manager stores the non-secret edit buffer and computes dirty state against the saved snapshot.
2. `SaveSelectedFileAsync` rejects missing selection or re-entry, snapshots path/content, and calls Accessor `WriteTextFileAsync`.
3. Success advances the saved snapshot and calls `GetStatusAsync`; failure preserves editable content and publishes the current safe save message. Cancellation follows the same preservation rule.

### Status

1. Client or an owning workflow calls `RefreshStatusAsync`; duplicate refresh for the same generation is coalesced/no-op while busy.
2. Manager calls Accessor `GetStatusAsync` and accepts only the current workspace generation.
3. Success replaces changed-file Resources; failure clears only that result view and publishes the operation-specific safe message.

### Commit

1. Client presentation validation creates `CommitRequest` and calls `CommitAsync`; Manager checks repository and mutation gate, clears prior commit result, and calls Accessor.
2. Accessor/JS validates again, stages additions/removals, rejects no changes, and creates one local commit.
3. Success stores `CommitInfo`, invalidates push review/confirmation, and refreshes status. Failure/cancellation preserves worktree and existing edit state and exposes fixed recovery text.

### Reviewed push

1. Client calls `ReviewPushAsync`; Manager clears old confirmation/failure state and calls Accessor `InspectPushAsync`.
2. Accessor returns credential-free immutable `PushReview`; Manager stores it for rendering. Client explicitly confirms that exact identity; confirmation is Manager state, not a platform call.
3. `PushReviewedCommitAsync` requires review, confirmation, credential presence, and a free mutation gate; Manager copies the review into token-free `PushRequest` and calls Accessor once.
4. Accessor obtains the token from `pushCredentialSession.js` immediately before transport, revalidates origin/branch/HEAD, performs at most one non-force push, clears the local token variable, and returns a sanitized result.
5. Success stores pushed SHA and refreshes status. Any failure preserves local HEAD and review, clears confirmation, and maps to manual recovery. No layer automatically retries.

### Credential recovery

1. On first Manager initialization, `HasCredentialAsync` restores presence only. Client never receives the value.
2. Client clears its password input before calling `StoreCredentialAsync`; Manager forwards the transient string without storing it; Accessor writes only the scoped session key and publishes presence.
3. Explicit forget calls Accessor `ForgetCredentialAsync`. Credential rejection from push triggers Manager-directed forget and replacement-required state; storage failure fails closed.
4. Replacement and retry require new user input plus a new review/confirmation. Values never enter Manager state, Resources, events, markup, messages, diagnostics, logs, or serialized errors.

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

| Dependency or boundary | Failure path | Target owner and stable observable outcome | Retry or recovery boundary | S02 rollback checkpoint |
|---|---|---|---|---|
| JavaScript module or vendor asset load | missing asset, script error, import failure, malformed/null envelope | Accessor returns `initialize` or operation failure with safe message plus non-secret diagnostic; Manager remains unopened. | Repair assets and create a fresh scope/reload; cached failed initialization is not retried in-place. | Keep legacy module path/import alias through checkpoints 2-6. |
| Browser Git storage/filesystem | unavailable IndexedDB, quota, missing file/worktree, denied read/write | Accessor returns operation-specific failure; Manager clears only the failed result view and preserves editable text/local state where possible. | User reopens/restores storage and manually retries; no hidden retry. | Revert Accessor move while Resources remain source-compatible. |
| Credential session storage | unavailable/corrupt `sessionStorage`, set/get/remove failure | Accessor returns false/failure without a value; Manager publishes absent/replacement-required state and fails closed. | Restore storage/new tab; explicit store/forget action. If removal cannot be proved, user must close the tab. | Restore Home-owned module calls until Manager/Client cutover checkpoint. |
| Network, CORS proxy, or remote Git | timeout, connection loss, CORS rejection, malformed host response | Accessor maps push to `NetworkUnavailable` or safe `Unknown`; clone/open is an operation failure. Manager preserves local work/review and fixed guidance. | Repair network/CORS and explicitly review/confirm/retry; never automatic. | Restore old names/imports; wire operation names remain unchanged. |
| Authentication or permission | 401/403/auth rejection or malformed auth response | Accessor returns fixed `CredentialRejected`; Manager invokes forget and replacement-required state. | New credential input, new review, confirmation, and one explicit push. | Compatibility adapter preserves current Home rejection flow until Client cutover. |
| Non-fast-forward remote | remote advances after review or rejects non-force update | Accessor returns `RemoteAhead`; Manager preserves local HEAD/review, clears confirmation, and renders fixed guidance. | Reconcile remote manually, review again, confirm again; never force or auto-retry. | Roll back Manager/Client wiring without changing JS push implementation. |
| Cancellation, disposal, or re-entry | canceled interop, navigation during operation, duplicate click, stale completion | Manager serializes mutations and discards stale generations; Accessor returns structured cancellation and disposes only completed module imports. | Caller initiates a new action or a fresh scoped instance after disposal. | Revert the Manager checkpoint as one unit; legacy busy flags remain until cutover. |
| Malformed interop payload | null envelope, wrong operation, absent message/value, invalid DTO/SHA/ref | Accessor rejects it as an unexpected response with stable `Operation`, `Succeeded=false`, safe `Message`, and credential-free `Diagnostic`; Manager does not mutate success state. | Fix boundary/runtime then manually retry; malformed state is never accepted. | Revert Accessor implementation while retaining compatible Resources/interface. |
| Unsafe/malformed input | unsupported URL, credential-bearing GitHub origin, parent path, missing path/worktree, invalid commit/review | Client provides presentation validation; Manager checks sequence; Accessor validates trust-boundary input before filesystem/transport and returns operation-specific failure. | Correct input or reopen; no transport for invalid review. | Each layer can revert to the previous compatible caller at its checkpoint. |
| Secret-bearing external failure | host/error includes PAT, auth header, throwing accessor, or hostile serialization | Accessor allowlists classifications and fixed diagnostics after redaction; Manager/Client never receive or render raw host error. | Forget/replace if auth-related; otherwise repair boundary and manually retry. | Stop migration and revert current checkpoint if any credential-safety suite fails. |

The design introduces no new external dependency. Failure localization belongs to the Accessor for platform/interop faults, the Manager for workflow/recovery state, and the Client for accessible presentation of safe signals. Each checkpoint must fail closed and pass the existing redaction suite before it can become a rollback baseline.

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

The explicit test gaps are implementation-owned rather than silently accepted: **S02** adds C# Accessor cases for module import, malformed envelope, cancellation, and disposal plus Manager workflow cases for duplicate/re-entry, stale completion, cancellation, storage/auth/network mapping, empty state, preserved edits, and rejection-driven credential cleanup. **S03** adds source-contract negative fixtures for every forbidden dependency edge, Resource behavior/credential fields, duplicate interfaces, and surviving legacy names; it also runs the clean build and focused credential UAT. Existing Node/browser cases remain the behavior oracle throughout migration.

`AlmToo/tests/browserGitDesignContractTests.mjs` additionally fails on missing architecture sections, unmapped tracked source surfaces, absent target assignments, changed D010-D013 constraints, missing credential/redaction/migration/testing coverage, incomplete review results, or malformed approval metadata.

## Migration sequence

S02 executes these ordered, buildable, rollback-safe checkpoints. Before advancing, commit a clean build plus full Node-suite baseline; if a checkpoint fails, revert only that checkpoint because the prior contract remains compiling. Every checkpoint preserves wire operation names, messages, review identity, one-attempt push, local-work preservation, tab-scoped credentials, redaction, and manual retry.

1. **Resources first.** Move current requests/results/enums unchanged into `Services/Accessors/BrowserGit/Interface/BrowserGitResources.cs`; add `Services/Managers/GitWorkspace/Service/GitWorkspaceState.cs`. Temporarily retain namespace forwarding/global aliases only where needed so every existing Client and service still compiles. Prove records are data-only and run build/full Node. Rollback: restore the original Resource file and aliases without behavior changes.
2. **Accessor rename and move.** Add `IBrowserGitAccessor` and `BrowserGitAccessor` under `Services/Accessors/BrowserGit/Interface` and `Services/Accessors/BrowserGit/Service`, preserving all operation strings, DTO validation, cancellation, task-cached initialization, redaction, and module disposal. Keep a temporary `IBrowserGitService`/`BrowserGitService` compatibility adapter for old callers. Rename `browserGitEngine.js` only if the import path, asset copier, and tests change atomically; otherwise document the legacy filename as Accessor-owned. Run Accessor tests/build. Rollback: rebind the adapter to legacy implementation.
3. **Credential integration behind the Accessor.** Import/cache/dispose `pushCredentialSession.js` in `BrowserGitAccessor`; add presence/store/forget methods and make target `PushAsync` retrieve the token internally immediately before the existing JS push. Keep Home on the compatibility surface until all credential tests pass. Rollback: restore Home-owned credential module path and the legacy push-token adapter; never duplicate token storage.
4. **Concrete Manager extraction.** Add scoped `GitWorkspaceManager` under `Services/Managers/GitWorkspace/Service` and workflow tests. Move one use case and its state at a time in the order open, browse/read, edit/save, status, commit, review/push, credential recovery. During this checkpoint Home may still use the legacy implementation, but the new Manager tests must prove state transitions, mutation serialization, stale-generation rejection, cancellation, result mapping, disposal, and exact credential recovery. Rollback: remove the unreferenced Manager; legacy behavior is untouched.
5. **DI composition.** Register `IBrowserGitAccessor -> BrowserGitAccessor` and concrete scoped `GitWorkspaceManager` in `Program.cs`; keep the compatibility service registration only while a legacy Client caller exists. Prove one scoped Manager owns one scoped Accessor and no duplicate module owner is created. Rollback: restore prior registrations.
6. **Client rewiring.** Rewire `Home.razor` atomically to inject only `GitWorkspaceManager`, render `GitWorkspaceState`, dispatch use cases, and subscribe/unsubscribe to state notifications. Child components remain presentation Clients consuming Resources/callbacks. Remove Git-related `IJSRuntime`, Accessor, token retrieval, and orchestration from Home. Run build, Manager/Client contracts, full Node, and focused credential browser UAT. Rollback: restore the legacy Home file and compatibility registrations as a unit.
7. **Test relocation and asset naming.** Split legacy `homePageContractTests.mjs` expectations into presentation/source rules and Manager workflow tests; update Accessor source paths after the production cutover. Relocate/rename JS or assets only in one atomic patch with `copy-git-browser-assets.mjs`, import constants, package scripts, and tests. Run the entire suite after each move. Rollback: restore prior tracked paths/imports; do not leave dual generated assets.
8. **Legacy-name removal.** Delete `Services/Git/IBrowserGitService.cs`, `BrowserGitService.cs`, compatibility adapters/aliases, old namespaces, and obsolete Home orchestration only after repository-wide source-contract checks prove zero callers. Run clean Blazor build, full Node suite, architecture checks, and focused browser UAT. Rollback: restore the compatibility adapter, not duplicated logic.

A checkpoint cannot be accepted merely because it builds. Its relevant behavior and negative tests must pass, and credential/redaction failure blocks advancement. Temporary compatibility code may delegate across old/new names but may not own workflow, fork implementations, introduce a second service interface beyond the temporary rename bridge, or survive checkpoint 8.

## Verification strategy

- **Architecture contract:** `node --test AlmToo/tests/browserGitDesignContractTests.mjs` validates the tracked design/source baseline, complete target contract, review, and approval gate without reading `.gsd`, `.planning`, or `.audits`.
- **Accessor behavior:** `AlmToo/tests/browserGitEngineTests.mjs` and `AlmToo/tests/pushCredentialSessionTests.mjs` protect JS/browser integration, envelopes, module/storage/I/O failures, push invariants, and credential safety. S02 adds C# Accessor contract tests around both modules and malformed interop responses.
- **Manager workflow:** S02 introduces deterministic tests with one substitute `IBrowserGitAccessor` for all state transitions, ordering, duplicate/re-entry, stale completion, cancellation, disposal, error mapping, retry boundaries, and credential-rejection cleanup. This is owned by S02 and must pass before Client cutover.
- **Client contract:** `AlmToo/tests/homePageContractTests.mjs` protects the current flow; at cutover it must instead assert Manager-only injection, presentation mapping, explicit confirmation, accessibility, fixed recovery messages, and absence of Git `IJSRuntime`/Accessor calls.
- **Full Node regression:** `npm test --prefix AlmToo` must retain all behavioral suites and the architecture contract at every checkpoint.
- **Clean build:** S02 and S03 run `dotnet clean AlmToo/AlmToo.csproj && dotnet build AlmToo/AlmToo.csproj --no-restore` (restore first only when required by the environment). Warnings/errors from old namespaces or duplicate registrations block legacy removal.
- **Browser UAT:** `AlmToo/e2e/credentialSafety.spec.mjs` is required focused credential/recovery proof after Client cutover; `AlmToo/e2e/liveGitHubPush.spec.mjs` remains opt-in real-remote proof when its disposable fixture is explicitly available.

### S03 architecture enforcement and final verification

S03 converts the target rules into tracked source-contract checks. Checks inspect production C#/Razor/JS source (not ignored planning artifacts) and fail with the offending path and edge:

1. Client files under `Pages` and repository components must have no `IBrowserGitAccessor`, `BrowserGitAccessor`, legacy `IBrowserGitService`, or Git-related `IJSRuntime` injection/import/invocation; `Home.razor` must call concrete `GitWorkspaceManager` only.
2. `GitWorkspaceManager` must not import/call another Manager, `IJSRuntime`, browser storage, vendor modules, remote clients, or JS module paths. This is the explicit Manager-to-Manager prohibition; its one integration dependency is `IBrowserGitAccessor`.
3. The Accessor may depend on `IJSRuntime`, browser Git/credential modules/assets, and Resources, but cannot reference Clients, Managers, or any iDesign Engine. This enforces Accessor ownership of all platform I/O: browser storage/filesystem, credential-session, asset-module, and remote Git I/O is reachable only beneath this Accessor boundary.
4. Resource source files may contain records/enums/data validation only: no service injection, `IJSRuntime`, module handles, filesystem/network calls, callbacks/events, orchestration methods, or credential fields.
5. Repository search must find exactly one non-temporary production service interface for Browser Git and no legacy service names after checkpoint 8. JavaScript legacy filename text is allowed only as an Accessor-owned implementation path.
6. Final executable proof is clean Blazor build, full `npm test --prefix AlmToo`, focused credential browser UAT, and opt-in live push UAT when fixtures exist. Source-contract success alone cannot substitute for build or behavior proof.

Focused assertion messages are required so a missing section, mapping, forbidden dependency, incomplete review, malformed approval field, or violating source path can be repaired without interpreting one aggregate failure.

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

This is the completed T02 review of the implementation-ready target and migration proof. It copies every applicable section from `docs/IDESIGN-REVIEW.md`; present Client-to-Accessor and Client-to-JS edges are time-bounded S02 migration gaps, not approved target dependencies.

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

**Result:** PASS — verification is assigned to owning boundaries. Manager workflow coverage is explicitly deferred to owner **S02 implementation**, required before Client cutover; S03 owns final source enforcement and cross-layer proof. No future evidence is claimed as current.

### 6. Exceptions

None. The current direct Client dependencies are legacy migration gaps, not intentional target exceptions. If they cannot be removed, a separate architectural decision with rationale and revisit trigger is required.

### Compliance statement

> This design follows the project iDesign policy. Layer assignments and dependency direction were reviewed. Every retained interface has a current architectural justification, and no unexplained interface-per-class or pass-through decomposition remains.

**Overall result:** PASS — architecture baseline/design only. Implementation remains blocked by the independent human approval gate.
