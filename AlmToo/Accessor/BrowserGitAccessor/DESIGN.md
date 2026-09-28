# Browser Git Architecture Design

## Status and scope

This document is the durable architecture baseline for Browser Git. It describes the implemented browser-local repository workflow and its D018 Resource boundary. It is intentionally broader than an Accessor API catalogue: a contributor should be able to trace ownership, dependencies, composition, every supported workflow, failures, verification, risks, and extension rules from this document.

| Field | Value |
|---|---|
| Architecture status | Implemented architecture |
| Current implementation baseline | Browser Git workflow through Client, Manager, Accessor, and passive Resource contracts is implemented |
| D018 Resource relocation | Implemented by S04 T02 and enforced by source-path-specific architecture contracts |
| Runtime behavior | Payload shapes, scoped composition, cancellation, credentials, and workflow behavior are unchanged |
| Review date | 2026-09-25 |
| Governing policy | `docs/IDESIGN.md` and `docs/IDESIGN-REVIEW.md` |

The operation signatures and behavioral invariants below are authoritative. “Current” describes the final source tree after T02. “Target” documents the same required topology so future changes can be compared against it. Compile, architecture, Manager, and browser verification jointly prove the implementation rather than relying on documentation claims.

## Architecture overview

### Current-state topology

The implemented call path is:

```text
Pages/Home.razor + Repository*.razor                         Client
                 |
                 v
RepositoryWorkspaceManager + RepositoryWorkspaceState       Manager
                 |
                 +------ IBrowserGitAccessor ----------------+
                 +------ IBrowserFileAccessor ---------------+  Accessor interfaces
                                                              |
                                                              v
                                                   BrowserGitAccessor
                                                    /               \
                                      browserGitEngine.js   pushCredentialSession.js
                                      LightningFS/isomorphic-git   sessionStorage/GitHub
```

`Program.cs` is the Client composition root. It creates one scoped `BrowserGitAccessor` and exposes that same instance through both Accessor capability interfaces. The scoped `RepositoryWorkspaceManager` receives both facets. The Manager owns use-case order and observable UI state; the Accessor owns browser/platform integration, trust-boundary validation, response translation, credential redaction, and JavaScript module lifetime.

The current source keeps shared request, result, value, and failure records in `Resource/BrowserGitResource/Data/BrowserGitContracts.cs` under `AlmToo.Resource.BrowserGitResource.Data`. The Accessor Interface directory now contains only the two capability interfaces; the former Accessor-owned DTO file no longer exists.

### Target topology

D018 separates behavior-bearing Accessor interfaces from shared passive data:

```text
Client -> concrete RepositoryWorkspaceManager -> Accessor interfaces -> BrowserGitAccessor -> browser/platform
                   |                                  |          |
                   +------------------------------> Resource <---+

Accessor interfaces:
  AlmToo/Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs
  AlmToo/Accessor/BrowserGitAccessor/Interface/IBrowserFileAccessor.cs
  namespace AlmToo.Accessor.BrowserGitAccessor.Interface

Passive shared contracts:
  AlmToo/Resource/BrowserGitResource/Data/BrowserGitContracts.cs
  namespace AlmToo.Resource.BrowserGitResource.Data
```

The singular `Resource` and `BrowserGitResource` nomenclature is exact. The target is not `Resources`, a global `Models` folder, an Accessor `Interface` DTO file, or a behavior-bearing Resource service. `BrowserGitContracts.cs` contains records and enums only. It owns no factory methods, validation, workflow, integration, JavaScript calls, mutation algorithms, or executable policy.

`RepositoryWorkspaceState` does **not** move to the Resource boundary. It is mutable Manager-owned orchestration state with busy/result/error, selection, credential-presence, and reviewed-push lifecycle signals. Resource records are passive values shared across layer boundaries; Manager state is the observable state machine of one use case.

### Component-to-layer assignments

| Component | Layer | Responsibility | State |
|---|---|---|---|
| `Pages/Home.razor`, `Components/Repository*.razor` | Client | Render Manager state, collect input, and delegate actions | Implemented |
| `Program.cs` | Client composition root | Select scoped concrete services and interface facets | Implemented |
| `RepositoryWorkspaceManager` | Manager | Orchestrate the complete repository workspace use case | Implemented; intentionally concrete |
| `RepositoryWorkspaceState` and Manager input/error/result records | Manager | Expose credential-free mutable workflow state | Implemented; remains Manager-owned |
| `IBrowserGitAccessor` | Accessor interface | Define repository, Git, push, and credential platform capabilities | Implemented; retained |
| `IBrowserFileAccessor` | Accessor interface | Define browser-workspace file capabilities | Implemented; retained |
| `BrowserGitAccessor` | Accessor service | Integrate .NET with browser filesystem, Git, remote transport, and credential storage | Implemented |
| `browserGitEngine.js`, `pushCredentialSession.js` | Accessor implementation | Execute browser Git/filesystem and tab credential operations | Implemented |
| `Resource/BrowserGitResource/Data/BrowserGitContracts.cs` | Resource | Carry shared passive records and enums with no behavior | Implemented and enforced |

No Engine service is required. Workflow sequencing belongs to the Manager, while integration and trust-boundary checks belong to the Accessor. There is no independent deterministic domain algorithm substantial enough to justify an Engine.

## Dependency rules

### Allowed edges

- Client repository components call the concrete `RepositoryWorkspaceManager` and read `RepositoryWorkspaceState`.
- The Manager calls `IBrowserGitAccessor` and `IBrowserFileAccessor`.
- Client-facing Manager state, the Manager, Accessor interfaces, and Accessor service may reference passive types in `AlmToo.Resource.BrowserGitResource.Data`.
- `BrowserGitAccessor` calls browser JavaScript modules and platform/vendor APIs.
- `Program.cs` references Client, Manager, and Accessor registration types solely to compose the scoped object graph.
- Tests may substitute the two Accessor interfaces to verify Manager workflows.

### Forbidden edges

- Clients must not call an Accessor, JavaScript interop, Git library, or credential module directly.
- Managers must not call other Managers or own JavaScript interop.
- Accessors must not depend on Clients, Managers, `RepositoryWorkspaceState`, or presentation behavior.
- Accessors must not choose workflow order, automatic retry, push confirmation, or UI recovery guidance.
- Resources must not call any service or contain algorithms, methods, validation, I/O, mutable workflow state, or executable bodies.
- The passive contracts must not be declared in an Accessor interface file and must not move to plural `Resources`, global `Models`, or generic `Common` placement.
- Composition must not create separate concrete Accessor instances for the Git and file facets.

The runtime call direction is `Client -> Manager -> Accessor -> platform`. Resource references carry data laterally and do not reverse the runtime call graph.

## Contract ownership

| Item | Target definition |
|---|---|
| Git interface | `AlmToo.Accessor.BrowserGitAccessor.Interface.IBrowserGitAccessor` |
| File interface | `AlmToo.Accessor.BrowserGitAccessor.Interface.IBrowserFileAccessor` |
| Service | `AlmToo.Accessor.BrowserGitAccessor.Service.BrowserGitAccessor` |
| Passive Resource source | `AlmToo/Resource/BrowserGitResource/Data/BrowserGitContracts.cs` |
| Passive Resource namespace | `AlmToo.Resource.BrowserGitResource.Data` |
| Manager state | `AlmToo.Managers.Repositories.RepositoryWorkspaceState` |
| JavaScript integration | Accessor-owned modules under `wwwroot/js` |
| Lifetime | One scoped service implements both Accessor interfaces for one browser workspace and is asynchronously disposable |

`IBrowserGitAccessor` is retained because it protects the browser JavaScript, remote Git, and tab-credential platform boundary and supplies a necessary substitution seam for Manager workflow verification.

`IBrowserFileAccessor` is retained because it protects the browser filesystem and JavaScript platform boundary while limiting file-oriented callers to the file capability and supplying its necessary substitution seam.

The concrete `RepositoryWorkspaceManager` needs no interface: it has no external or platform boundary, no credible alternate implementation, and no testing seam that cannot be achieved by substituting its Accessor dependencies.

## Composition and lifetime

`Program.cs` registers `BrowserGitAccessor` as scoped, maps both Accessor interfaces to that same concrete scoped instance, and registers the concrete Manager as scoped. In Blazor WebAssembly, this gives one coordinated browser workspace service for the application scope.

The shared instance matters because Git state, browser filesystem state, lazy initialization, credential module access, and disposal belong to one workspace lifetime. Creating one service per interface would split initialization and active repository state. The service lazily imports modules, caches initialization, and disposes every successfully imported module. Partial initialization remains safe to dispose.

The Manager owns an operation gate and linked cancellation source. It prevents overlapping workspace operations, publishes busy state before awaiting the Accessor, and clears busy state in `finally`. Neither Resource contracts nor Clients own service lifetime.

## Accessor operation contract

Every public operation except disposal accepts an optional `CancellationToken`. Every operation returns a normalized, credential-free result envelope rather than leaking JavaScript or vendor models. Initialization is private, lazy, and idempotent.

| Operation | Input | Output | Boundary contract |
|---|---|---|---|
| `CloneOrOpenAsync` | `RepositoryOpenRequest` | `GitOperationResult<RepositoryInfo>` | Validate credential-free HTTPS URL and workspace key; open or clone and select the checkout |
| `FilterFilesAsync` | `FilterFilesRequest` | `GitOperationResult<FilterFilesResult>` | List one directory or read one allowlisted UTF-8 text file beneath the checkout |
| `UpdateFilesAsync` | `UpdateFilesRequest` | `GitOperationResult<UpdateFilesResult>` | Validate all normalized unique text updates before writing |
| `GetStatusAsync` | None | `GitOperationResult<IReadOnlyList<ChangedFile>>` | Return normalized working-tree changes |
| `CommitAsync` | `CommitRequest` | `GitOperationResult<CommitInfo>` | Validate author/message and create a browser-local commit |
| `InspectPushAsync` | None | `GitOperationResult<PushReview>` | Return immutable credential-free push coordinates |
| `HasCredentialAsync` | None | `GitOperationResult<bool>` | Return presence only, never credential text |
| `StoreCredentialAsync` | Credential text | `GitOperationResult<bool>` | Store input in Accessor-owned tab scope without echoing it |
| `ForgetCredentialAsync` | None | `GitOperationResult<bool>` | Clear tab-scoped credential and fail closed if unavailable |
| `PushAsync` | `PushRequest` | `GitOperationResult<PushResult>` | Revalidate reviewed coordinates, retrieve the credential at transport time, push with lease protection, and sanitize output |
| `DisposeAsync` | None | `ValueTask` | Dispose imported JavaScript modules safely |

### Interface signatures

The interfaces import `AlmToo.Resource.BrowserGitResource.Data`; operation shapes remain unchanged.

```csharp
using AlmToo.Resource.BrowserGitResource.Data;

namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

public interface IBrowserGitAccessor : IAsyncDisposable
{
    ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(RepositoryOpenRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<CommitInfo>> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushReview>> InspectPushAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> HasCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> StoreCredentialAsync(string credential, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<PushResult>> PushAsync(PushRequest request, CancellationToken cancellationToken = default);
}

public interface IBrowserFileAccessor
{
    ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(FilterFilesRequest request, CancellationToken cancellationToken = default);
    ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(UpdateFilesRequest request, CancellationToken cancellationToken = default);
}
```

## Resource contract catalogue

The canonical contract file is `AlmToo/Resource/BrowserGitResource/Data/BrowserGitContracts.cs` in namespace `AlmToo.Resource.BrowserGitResource.Data`. It contains only the passive records and enums listed below. Public payload field order, names, nullability, and JSON behavior remained unchanged during relocation.

### Result and failure contracts

#### `GitOperationResult` and `GitOperationResult<T>`

Fields are `Operation`, `Succeeded`, `Message`, optional `Value`, optional `Diagnostic`, and optional `FailureKind`. `Message` is always credential-free and safe to render. `Diagnostic` is credential-free technical detail that the presentation must not render. `Value` is meaningful only when `Succeeded` is true.

The former static `Success` and `Failure` factory methods were removed in T02. Callers construct records explicitly so the Resource has no methods or executable bodies.

#### `GitOperationFailureKind`

| Value | Recovery meaning |
|---|---|
| `CredentialRejected` | Authentication or authorization failed; forget the credential and require explicit replacement |
| `RemoteAhead` | Remote history changed after review; reconcile and review again |
| `NetworkUnavailable` | Browser transport could not reach or complete the remote operation |
| `UnsupportedRef` | Checked-out branch or destination cannot be pushed safely |
| `Unknown` | Credential-free fallback for all other safe failures |

### Repository and file contracts

- `RepositoryOpenRequest(RepositoryUrl, WorkspaceName)` carries untrusted credential-free HTTPS input and a normalized browser workspace key.
- `RepositoryInfo(RepositoryUrl, WorkspaceName, RootPath, WasCloned)` identifies the active browser-local checkout.
- `FilterFilesRequest(Path, Kind)` selects directory entries or one text file.
- `FileFilterKind` contains `DirectoryEntries` and `TextContent`.
- `FilterFilesResult(Path, Entries, TextFile)` populates the shape selected by `Kind`.
- `UpdateFilesRequest(Updates)` contains one or more updates validated as a unit.
- `TextFileUpdate(Path, Content)` replaces one complete UTF-8 text file.
- `UpdateFilesResult(UpdatedPaths)` returns normalized paths written.
- `RepositoryFileEntry(Path, Name, Kind, SizeBytes, IsEditableText)` describes one immediate entry.
- `RepositoryFileKind` contains `File` and `Directory`.
- `TextFileContent(Path, Content, Encoding, SizeBytes)` returns allowlisted text without lossy decoding.

The initial editable-text limit is 1 MiB. Unsupported, binary, invalid UTF-8, oversized, traversing, encoded, or duplicate paths fail before unsafe mutation. General binary access requires a separate reviewed capability and byte-oriented contracts.

### Change, commit, and push contracts

- `ChangedFile(Path, ChangeKind)` and `GitChangeKind` describe normalized working-tree changes.
- `CommitRequest(Message, AuthorName, AuthorEmail)` carries validated commit input.
- `CommitInfo(CommitId, Message)` identifies the browser-local commit.
- `PushReview(RepositoryUrl, Branch, OutgoingCommitId, DestinationRef)` is the immutable review snapshot.
- `PushRequest` repeats exactly those four reviewed coordinates.
- `PushResult(RepositoryUrl, Branch, DestinationRef, PushedCommitId)` must match the request on success.

No credential field exists in any Resource contract.

## End-to-end workflows

### Clone or open

1. The Client sends the repository URL to `RepositoryWorkspaceManager.OpenRepositoryAsync`.
2. The Manager trims input, resets stale workspace state, derives a normalized workspace key, and calls `CloneOrOpenAsync`.
3. The Accessor validates a credential-free HTTPS URL, initializes browser storage, and asks the JavaScript Git module to reopen or clone.
4. The Accessor maps the response into `RepositoryInfo` and a safe result envelope.
5. On success the Manager loads `/`, refreshes status, and publishes repository, entries, changes, and result state. Any failed step leaves a safe error and does not fabricate an open workspace.

### Browse

1. The Client delegates a directory or file selection to the Manager.
2. For a directory, the Manager clears stale entries and selection before calling `FilterFilesAsync` with `DirectoryEntries`.
3. For a file, the Manager verifies that the visible entry is an editable file, clears stale content, and calls `FilterFilesAsync` with `TextContent`.
4. The Accessor normalizes and contains the path, enforces allowlisted UTF-8 and size policy, then invokes the browser filesystem.
5. The Manager publishes only the returned normalized path, entries, or text.

### Edit and save

1. Editing changes only Manager-owned `EditableFileContent`; it performs no I/O.
2. Save requires an active repository and selected loaded text file.
3. The Manager creates one `UpdateFilesRequest`, and the Accessor validates all updates, uniqueness, containment, encoding, and size before writing.
4. On success the Manager updates the selected baseline, refreshes Git status, and publishes the result. Failure retains safe state and does not report the file as saved.

### Status

The Manager calls `GetStatusAsync` only for an active repository. The Accessor maps browser Git status into `ChangedFile` values. On failure the Manager clears stale changed-file data and publishes safe recovery guidance.

### Commit

1. The Manager requires an active repository and non-empty message, author name, and author email.
2. The Accessor rejects control characters and invokes the local Git commit operation.
3. On success the Manager stores `CommitInfo`, invalidates any older push review, refreshes status, and publishes the result.
4. Commit never pushes and never requests a credential.

### Reviewed push

1. `InspectPushAsync` obtains the exact credential-free repository URL, local branch, outgoing full commit ID, and destination ref.
2. The Manager stores that `PushReview`; the Client renders it and collects explicit confirmation.
3. Credential presence is required separately. Before push, the Manager re-runs inspection and compares all coordinates to the reviewed value.
4. Any stale or changed coordinate invalidates confirmation and requires a new review.
5. The Manager constructs `PushRequest` only from the confirmed review.
6. The Accessor validates GitHub HTTPS coordinates and full commit IDs, retrieves the credential immediately before transport, and pushes with force-with-lease semantics.
7. Returned coordinates must match the request. Success clears review and refreshes status; failure never retries automatically.

### Credential recovery and forget

At startup the Manager asks only whether a credential exists. Store accepts credential text as ephemeral input, passes it to the Accessor, and clears the local attempt variable in `finally`. The Accessor stores it in tab-scoped storage and never returns it. Forget clears Manager presence state before calling storage and requires explicit recovery if storage cannot confirm removal.

A `CredentialRejected` push causes the Accessor to forget the stored credential and causes the Manager to set `HasCredential = false` and `CredentialReplacementRequired = true`. The Client then requests a replacement without receiving or rendering the rejected secret. Closing the tab remains the safe fallback when storage clearing cannot be confirmed.

### Cancellation and concurrency

The Manager permits one active repository operation. A concurrent attempt fails immediately with “Another repository operation is already in progress” and does not enter the Accessor. External cancellation and `CancelCurrentOperation` flow through a linked token to imports, initialization, JavaScript invocation, credential storage, filesystem, and transport where supported.

Cancellation publishes `WasCancelled`, a failed result, and credential-free text; `finally` clears busy state and releases the operation gate. Cancellation never fabricates success, auto-retries a mutation, preserves push confirmation, or exposes a credential. Resource relocation adds no runtime call and no payload field, so it must not alter concurrency or cancellation behavior.

## Failure taxonomy and diagnostics

| Dependency or boundary | Representative failures | Safe path and diagnostic surface |
|---|---|---|
| JavaScript module import | unavailable module, invalid runtime state, cancellation | Accessor returns normalized initialization/invocation failure without raw exception text |
| Browser filesystem / LightningFS | unavailable storage, malformed path, invalid UTF-8, oversized content, write failure | Accessor rejects before mutation where possible; Manager publishes safe operation error |
| isomorphic-git / response envelope | null or malformed response, unsupported ref, unexpected value | Accessor validates operation/message/value and maps to safe `Diagnostic` and `FailureKind` |
| Public GitHub clone transport | CORS/network loss, unavailable repository, unsupported URL | `NetworkUnavailable` or safe unknown failure; user may retry explicitly after resolving availability |
| Authenticated GitHub push | credential rejection, permission denial, remote ahead, connection loss | typed failure, credential clearing for rejection, review invalidation/recovery guidance, never automatic replay |
| `sessionStorage` credential module | storage unavailable/corrupt, false result, cancellation | fail closed, presence false, replacement required; credential value is never returned |
| Manager operation gate | overlapping user actions | second action fails visibly before integration work starts |
| Cancellation | token before/during import, storage, filesystem, or transport | safe cancelled result and `finally` cleanup |
| Resource deserialization/shape | missing value or malformed platform response | Accessor rejects the response; Resource itself performs no validation |

`GitOperationResult.Operation`, `Message`, optional `Diagnostic`, and optional `FailureKind` localize failures without exposing secrets. Manager state additionally exposes the active operation, busy state, result, error, cancellation marker, credential-presence booleans, and push failure category. Node architecture tests report rule and source path; xUnit reports the workflow test; Playwright retains failure-only trace/screenshots and identifies the public step. Raw credential values must never appear in any diagnostic artifact.

## Contract invariants

### Initialization and lifetime

- Initialization is private, lazy, idempotent, and shared by concurrent callers.
- Failed initialization cannot leave a partially usable Accessor.
- Every successfully imported module is disposed, including after partial initialization.
- Both capability interfaces resolve to one scoped concrete Accessor.

### Paths and text

- Every path is normalized, repository-relative, and contained beneath the active checkout.
- Empty segments, `.`, `..`, backslashes, percent-encoded traversal, filesystem absolute paths, and paths outside the checkout are rejected.
- Unsupported, binary, invalid UTF-8, or oversized content fails without lossy rewrite.
- All update paths are validated before the first write and duplicate normalized paths are rejected.

### Push and credentials

- Push executes only after exact review, explicit confirmation, credential presence, and a fresh matching inspection.
- Push uses lease protection and rejects missing or mismatched coordinates.
- Credentials never enter Resource records, Manager state, instance fields, markup, URLs, logs, diagnostics, exceptions, persisted repository files, traces, screenshots, or test output.
- Authentication rejection forgets stored credentials before returning.
- Missing or unavailable credential storage fails closed.
- Failed pushes are never replayed automatically.

### Resource purity

- `AlmToo.Resource.BrowserGitResource.Data` contains passive records and enums only.
- Resource contracts contain no methods, factory helpers, validation, computed workflow properties, mutable orchestration state, I/O, or executable bodies.
- `RepositoryWorkspaceState`, `RepositoryWorkspaceError`, `RepositoryWorkspaceResult`, and `RepositoryCommitInput` remain Manager-owned.

## Migration sequence and implementation status

| Step | Change | Status |
|---:|---|---|
| 1 | Establish Client -> Manager -> Accessor workflow, split Git/file capability interfaces, and scoped shared composition | Implemented |
| 2 | Document complete current/target architecture and D018 ownership | Implemented by S04 T01 |
| 3 | Create `AlmToo/Resource/BrowserGitResource/Data/BrowserGitContracts.cs` with namespace `AlmToo.Resource.BrowserGitResource.Data` | Implemented by S04 T02 |
| 4 | Move only passive records/enums; remove `GitOperationResult` factory methods and use explicit construction | Implemented by S04 T02 |
| 5 | Import Resource contracts from Accessor interfaces/service, Manager/state, and tests; delete old Accessor DTO declarations | Implemented by S04 T02 |
| 6 | Enforce singular placement, Resource purity, legal dependencies, and unchanged composition with negative fixtures | Implemented by S04 T02 |
| 7 | Run Node, xUnit, warning-as-error build, and autonomous browser workflows | Node, xUnit, and build complete in S04 T02; autonomous browser re-proof remains S04 T03 |

The migration was a source ownership change, not a payload redesign. It preserved record shapes, serialization, operation names, cancellation, credential handling, scoped composition, and browser behavior. Compiler errors and source-path architecture rules detect stale namespace consumers.

## Testing and observability strategy

### Contract and architecture tests

- `tests/browserGitDesignContractTests.mjs` verifies durable headings, meaningful coverage, target Resource path/namespace, Manager-state distinction, interface rationales, migration truthfulness, failure/test/risk coverage, and the full iDesign review. Inline negative documents prove missing topics, old target ownership, and missing interface justification fail without reading `.gsd`.
- `tests/browserGitArchitectureGateTests.mjs` enforces dependency direction, singular Resource placement, passive Resource declarations, composition, interface ownership, and source-path-specific negative fixtures after T02.
- `tests/browserGitAccessorContractTests.mjs` checks interface/service/Resource source contracts and credential-safe boundary behavior.
- JavaScript engine and credential-session tests verify malformed input, containment, storage clearing, transport classification, and secret absence.

### Manager and browser verification

- `AlmToo.Tests/Managers/Repositories/RepositoryWorkspaceManagerTests.cs` verifies complete operation sequencing, concurrency, cancellation, stale review, recovery, safe errors, and credential state.
- `dotnet build AlmToo/AlmToo.csproj --no-restore --warnaserror` catches stale namespaces, signatures, nullability, and registration drift.
- Playwright exercises the real localhost Blazor entrypoint for clone/open, browse, edit/save, status, commit, reviewed push, credential persistence/forget, authentication rejection, storage clearing, and redaction.
- Credential-gated live remote mutation remains outside autonomous verification because it requires disposable credentials and remote mutation.

### Operational signals

This browser-only subsystem has no server status endpoint. Its safe observability surfaces are Manager-owned `IsBusy`, `Result`, `Error`, `WasCancelled`, `HasCredential`, `CredentialReplacementRequired`, and `PushFailureCategory`; stable result operation names and failure kinds; Node rule/path output; xUnit case names; and Playwright failure-only artifacts. Diagnostics may describe category and boundary but must never contain a token.

## Load and performance profile

The expected interactive load is one active workspace and one user operation at a time. The first saturation point at 10x rapid user actions is deliberately the Manager’s single-operation `SemaphoreSlim`, not browser filesystem or remote transport. Nine overlapping actions fail fast with visible busy guidance instead of queuing duplicate mutations.

Repository size stresses browser memory, LightningFS, status traversal, and clone transport before Resource records; editable text is capped at 1 MiB and directory browsing is scoped to immediate entries. Resource relocation adds no calls, payload fields, serialization step, cache, or allocation-heavy algorithm. Large-repository pagination and streaming are future extensions and must be designed explicitly rather than hidden in the Resource layer.

## Known risks

- Browser storage quotas or private-mode restrictions can prevent clone, file, or credential operations; all must fail closed with safe guidance.
- Public Git hosting CORS and network availability are external and may make otherwise valid clone flows unavailable.
- Large repositories can exhaust browser memory or make status traversal slow; the current product is intentionally bounded to interactive repositories.
- Browser tab loss removes tab-scoped credentials and may interrupt in-flight operations.
- Remote history can change between review and push; fresh inspection plus lease protection is mandatory but cannot eliminate all network races.
- Resource records are shared broadly, so adding behavior would silently erode layer ownership unless architecture tests reject it.
- Relocation can leave stale imports or duplicate DTO declarations; compiler and source-path gates must detect both.
- Failure artifacts can become a credential leak if raw platform errors are propagated; sanitization and secret-absence assertions are release-blocking.

## Extension points

- Additional Git operations belong on a capability interface only when they expose browser/platform integration; their sequence remains Manager-owned.
- A new platform implementation may implement the existing Accessor interfaces if it preserves result and security contracts.
- Deterministic policy substantial enough to stand alone may be extracted to a stateless Engine; it must not be placed in Resource records.
- Binary file support requires a separate capability, explicit byte contracts, limits, and browser verification.
- Large-directory pagination requires new passive page/cursor records and Manager orchestration; Resource remains data-only.
- Alternate credential stores require a reviewed Accessor platform seam and must preserve presence-only Manager state and fail-closed clearing.
- Additional remote hosts require explicit URL/ref capability rules and safe failure mapping; do not generalize by weakening GitHub validation silently.

Every extension must update layer assignment, dependency rules, interface justification, negative architecture fixtures, and the implementation-state iDesign review.

## iDesign review

This implementation-state review copies every section of `docs/IDESIGN-REVIEW.md`. It evaluates the final production tree after the S04 T02 D018 Resource relocation.

### 1. Layer assignments

| Component | Layer | Cohesive responsibility | Allowed dependencies | Forbidden dependencies |
|---|---|---|---|---|
| `Pages/Home.razor`, `Components/Repository*.razor` | Client | Render state, map input, delegate actions | concrete Manager, Manager state, Blazor utilities | Accessors, Resource behavior, JS interop, Git policy |
| `RepositoryWorkspaceManager` | Manager | Coordinate one complete repository workspace use case | Accessor interfaces, passive Resource records, framework utilities | other Managers, direct JS interop, rendering |
| `RepositoryWorkspaceState` | Manager | Hold credential-free observable workflow state | passive Resource values | Accessor implementation, platform I/O |
| `IBrowserGitAccessor`, `IBrowserFileAccessor` | Accessor | Define browser/platform capability boundaries | passive Resource records, framework cancellation/lifetime | Manager, Client, workflow state |
| `BrowserGitAccessor` and JS modules | Accessor | Integrate browser filesystem, Git transport, credential storage, and translate failures | browser/vendor APIs, passive Resource records | Manager, Client, workflow sequencing |
| `AlmToo/Resource/BrowserGitResource/Data/BrowserGitContracts.cs` | Resource | Passive cross-boundary records and enums | data types only | methods, policy, validation, I/O, mutable Manager state |
| `Program.cs` | Client composition root | Compose one scoped Accessor instance and concrete Manager | registration types | repository workflow or integration behavior |

No Engine is present because there is no independent deterministic domain algorithm in this integration-focused subsystem.

**Result:** PASS

### 2. Dependency direction

- [x] Runtime calls follow `Client -> Manager -> Accessor`; `Program.cs` is composition-only.
- [x] The concrete Manager calls no other Manager.
- [x] Engine constraints are NOT APPLICABLE because no Engine participates.
- [x] Accessors own integration, boundary validation, translation, and lifetime but not workflow or presentation.
- [x] Clients contain no integration behavior, and the implemented Resource contains no domain algorithm.
- [x] Passive Resource references do not reverse the runtime call graph.
- [x] The exact D018 source path and namespace are implemented, and no Accessor Interface DTO declaration remains.

**Result:** PASS

### 3. Interface justification

| Interface | Implementations | Callers | Current boundary or substitution need | Keep, remove, or defer |
|---|---:|---:|---|---|
| `IBrowserGitAccessor` | 1: `BrowserGitAccessor` | 1: `RepositoryWorkspaceManager` | Protects the browser JavaScript, remote Git, and credential platform boundary and supplies the necessary substitution seam for Manager workflow tests. | Keep |
| `IBrowserFileAccessor` | 1: `BrowserGitAccessor` | 1: `RepositoryWorkspaceManager` | Protects the browser filesystem and JavaScript platform boundary and supplies the necessary substitution seam for file workflow tests. | Keep |

These are capability facets of one genuine external platform boundary, not interface-per-class wrappers. The concrete `RepositoryWorkspaceManager` remains interface-free because it has no external boundary, credible alternate implementation, or otherwise unavailable testing seam.

**Result:** PASS

### 4. Cohesion and decomposition

- [x] The Manager owns the complete open, browse, edit/save, status, commit, review, credential, push, cancellation, and recovery sequence.
- [x] The Accessor is not a pass-through: it validates trust-boundary input, maps contracts, normalizes failures, redacts secrets, and owns module lifetime.
- [x] Orchestration is not fragmented across Managers or presentation components.
- [x] Both capability interfaces remain on one concrete service because workspace, module, Git, credential, and disposal state change together.
- [x] Resource records are passive shared data rather than method services or disguised functional decomposition.
- [x] Manager-owned mutable state remains separate from Resource transfer values.

**Result:** PASS

### 5. Verification placement

| Behavior | Owning layer | Verification type | Evidence |
|---|---|---|---|
| Client delegation and safe rendering | Client | contract and UAT | `tests/homePageContractTests.mjs`; Playwright workflow |
| Complete workspace sequencing, cancellation, stale review, and recovery | Manager | workflow unit | `AlmToo.Tests/Managers/Repositories/RepositoryWorkspaceManagerTests.cs` |
| Browser Git, file, credential, and safe failure translation | Accessor | contract and integration | Accessor, engine, and credential-session Node suites |
| Passive contract ownership and purity | Resource | architecture contract and compile | architecture negative fixtures; warning-as-error build |
| Layer graph, composition, and design coverage | Cross-layer | architecture contract | design and architecture Node suites |
| Public end-to-end behavior | Client through Accessor | UAT | required Playwright specs against localhost Blazor |

- [x] Engine unit verification is NOT APPLICABLE because no Engine is present.
- [x] Accessor behavior is checked at .NET/JavaScript/platform boundaries.
- [x] Manager behavior is checked as complete use-case workflows.
- [x] Client and Resource behavior is checked through contracts, compile, and browser verification.

**Result:** PASS

### 6. Exceptions

None. `Program.cs` references registration types solely as the Client composition root and owns no runtime repository workflow. The Resource contract uses the required singular placement and contains only passive data.

**Result:** NOT APPLICABLE — there are no intentional exceptions.

## Compliance statement

> This change follows the project iDesign policy. Layer assignments and dependency direction were reviewed. Every interface has a current architectural justification, and no unexplained interface-per-class or pass-through decomposition remains.

**Overall result:** PASS

Repeat this review when an operation, payload, interface, dependency edge, composition lifetime, Resource ownership rule, or workflow responsibility changes.
