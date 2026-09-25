# Browser Git Resource Access Contract

## Status and approval

This document defines the target contract for browser-local Git Resource Access. It contains the operation facets, operation signatures, DTOs, and invariants owned by `IBrowserGitAccessor` and `BrowserGitAccessor`.

Manager workflows, Manager state, Client behavior, migration steps, test plans, and historical source layout do not belong in the Accessor contract itself. The implementation-state iDesign review at the end maps those adjacent layers only to verify the complete dependency graph.

| Field | Value |
|---|---|
| Design status | Approved |
| Implementation authorization | Authorized for S02 |
| Reviewer | Project owner (human reviewer) |
| Review date | 2026-09-24 |
| Source revision | Git `256f9ebf197b7817cfa4a907a63e87fe27b8c61c`; reviewed design SHA-256 `f10a33806a83f7747789483b38ff2036afe97e4a81760925fd2b9f1171433028` |
| Disposition | Approved as currently designed |

Implementation may begin only after a human reviewer changes the status to `Approved` and records the reviewer, review date, reviewed source revision, and an explicitly approved disposition. Automated checks cannot approve this contract.

## Contract ownership

| Item | Definition |
|---|---|
| Git interface | `AlmToo.Accessor.BrowserGitAccessor.Interface.IBrowserGitAccessor` |
| File interface | `AlmToo.Accessor.BrowserGitAccessor.Interface.IBrowserFileAccessor` |
| Service | `AlmToo.Accessor.BrowserGitAccessor.Service.BrowserGitAccessor` |
| DTO namespace | `AlmToo.Accessor.BrowserGitAccessor.Interface` |
| JavaScript service | Accessor-owned modules under `wwwroot/js` |
| Lifetime | One scoped Service implements both interfaces for one browser workspace and is asynchronously disposable |
| Allowed callers | Managers and Engines, as permitted by `docs/IDESIGN.md` |

The two interfaces protect the same browser and JavaScript platform boundary while separating distinct caller capabilities. `IBrowserGitAccessor` owns repository lifecycle and version-control operations. `IBrowserFileAccessor` owns filtering and updating files inside the active browser workspace. The split prevents file-oriented callers from depending on Git mutation operations; both interfaces are implemented by one cohesive Service because they coordinate the same active workspace and JavaScript module lifetime.

DTOs are data-transfer contracts adjacent to the Resource Access interfaces. They are not services and do not constitute a separate Resource layer or namespace.

## Operation facets

| Interface | Facet | Operations | Responsibility |
|---|---|---|---|
| `IBrowserGitAccessor` | Repository workspace | `CloneOrOpenAsync` | Establish the active browser-local checkout. |
| `IBrowserGitAccessor` | Working tree | `GetStatusAsync`, `CommitAsync` | Inspect working-tree changes and create local commits. |
| `IBrowserGitAccessor` | Reviewed push | `InspectPushAsync`, `PushAsync` | Produce immutable push coordinates and push only those reviewed coordinates. |
| `IBrowserGitAccessor` | Credential storage | `HasCredentialAsync`, `StoreCredentialAsync`, `ForgetCredentialAsync` | Manage credential presence in Accessor-owned tab-scoped storage without returning the secret. |
| `IBrowserFileAccessor` | File query | `FilterFilesAsync` | Retrieve directory entries or one supported text file through an explicit filter DTO. |
| `IBrowserFileAccessor` | File update | `UpdateFilesAsync` | Apply validated text updates through an explicit update DTO. |
| Service | Lifetime | private initialization, `DisposeAsync` | Import and initialize modules lazily, then dispose every successfully imported module shared by both interfaces. |

## Operations

Every public operation except disposal accepts an optional `CancellationToken`. Every operation returns a normalized, credential-free result envelope rather than leaking JavaScript or vendor-specific models.

`initialize` is not a public operation. `BrowserGitAccessor` privately and idempotently imports its modules and initializes browser storage before the first operation that needs them. `CloneOrOpenAsync` therefore appears to callers as one operation even though private initialization may run first.

| Operation | Input | Output | Contract |
|---|---|---|---|
| `CloneOrOpenAsync` | `RepositoryOpenRequest` | `GitOperationResult<RepositoryInfo>` | Validates the request, ensures private initialization, reopens an existing browser-local checkout or clones the remote repository, and selects it as active. |
| `FilterFilesAsync` | `FilterFilesRequest` | `GitOperationResult<FilterFilesResult>` | Filters the active checkout by normalized repository-relative path. A directory filter returns its immediate entries; a file filter returns one supported UTF-8 text file. Invalid, binary, or oversized content fails without replacement-corrupted text. |
| `UpdateFilesAsync` | `UpdateFilesRequest` | `GitOperationResult<UpdateFilesResult>` | Validates every requested UTF-8 text update before mutation, applies the updates to the active checkout, and returns the normalized paths changed. |
| `GetStatusAsync` | None | `GitOperationResult<IReadOnlyList<ChangedFile>>` | Returns working-tree changes for the active checkout. |
| `CommitAsync` | `CommitRequest` | `GitOperationResult<CommitInfo>` | Creates a local Git commit using validated message and author metadata. |
| `InspectPushAsync` | None | `GitOperationResult<PushReview>` | Resolves the credential-free repository URL, branch, outgoing commit, and exact destination ref without pushing. |
| `HasCredentialAsync` | None | `GitOperationResult<bool>` | Reports only whether a credential is present in tab-scoped storage; never returns its value. |
| `StoreCredentialAsync` | Credential text | `GitOperationResult<bool>` | Stores the input in Accessor-owned tab storage. The secret must not appear in results, fields, logs, diagnostics, URLs, or exceptions. |
| `ForgetCredentialAsync` | None | `GitOperationResult<bool>` | Removes the tab-scoped credential and fails closed if storage is unavailable. |
| `PushAsync` | `PushRequest` | `GitOperationResult<PushResult>` | Retrieves the credential immediately before transport, verifies the supplied reviewed coordinates, pushes with force-with-lease semantics, and clears local secret references in `finally`. |
| `DisposeAsync` | None | `ValueTask` | Disposes every successfully imported JavaScript module. Partial initialization is safe to dispose. |

### Interface signatures

```csharp
namespace AlmToo.Accessor.BrowserGitAccessor.Interface;

public interface IBrowserGitAccessor : IAsyncDisposable
{
    ValueTask<GitOperationResult<RepositoryInfo>> CloneOrOpenAsync(
        RepositoryOpenRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<IReadOnlyList<ChangedFile>>> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<CommitInfo>> CommitAsync(
        CommitRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<PushReview>> InspectPushAsync(
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<bool>> HasCredentialAsync(
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<bool>> StoreCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<bool>> ForgetCredentialAsync(
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<PushResult>> PushAsync(
        PushRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBrowserFileAccessor
{
    ValueTask<GitOperationResult<FilterFilesResult>> FilterFilesAsync(
        FilterFilesRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<UpdateFilesResult>> UpdateFilesAsync(
        UpdateFilesRequest request,
        CancellationToken cancellationToken = default);
}
```

## DTO catalogue

All DTOs below use the generic `AlmToo.Accessor.BrowserGitAccessor.Interface` namespace and live adjacent to `IBrowserGitAccessor` and `IBrowserFileAccessor`. No Context segment is present because the application currently has only the generic UI context. They contain data only.

### Operation results

#### `GitOperationResult`

| Field | Type | Meaning |
|---|---|---|
| `Operation` | `string` | Stable operation identifier used to localize failures. |
| `Succeeded` | `bool` | Whether the operation completed successfully. |
| `Message` | `string` | Credential-free message safe to render to a user. |
| `Diagnostic` | `string?` | Credential-free technical detail for tests or troubleshooting. Presentation must never render it. |
| `FailureKind` | `GitOperationFailureKind?` | Structured recovery category when a known Git or transport failure occurred. |

`GitOperationResult<T>` adds `T? Value`. `Value` is meaningful only when `Succeeded` is true. Failure translation must not copy raw untrusted exception text into `Message`.

#### `GitOperationFailureKind`

| Value | Meaning |
|---|---|
| `CredentialRejected` | Authentication or authorization failed; the stored credential must be forgotten before another explicit attempt. |
| `RemoteAhead` | The remote ref no longer matches the reviewed lease. |
| `NetworkUnavailable` | Remote transport could not be reached or completed. |
| `UnsupportedRef` | The requested branch or destination ref cannot be pushed safely. |
| `Unknown` | Safe fallback when no narrower recovery category applies. |

### Repository and file DTOs

#### `RepositoryOpenRequest`

| Field | Type | Meaning |
|---|---|---|
| `RepositoryUrl` | `string` | Absolute supported remote URL. It is untrusted input and must not contain an embedded credential. |
| `WorkspaceName` | `string` | Validated browser-local storage key derived from the repository, not an arbitrary filesystem path. |

#### `RepositoryInfo`

| Field | Type | Meaning |
|---|---|---|
| `RepositoryUrl` | `string` | Canonical credential-free repository URL. |
| `WorkspaceName` | `string` | Browser-local workspace identifier. |
| `RootPath` | `string` | Accessor-owned browser filesystem root used to scope operations; callers must not manipulate it. |
| `WasCloned` | `bool` | `true` when this operation cloned the remote; `false` when it reopened local storage. |

#### `FilterFilesRequest`

| Field | Type | Meaning |
|---|---|---|
| `Path` | `string` | Normalized repository-relative path to filter; `/` addresses the checkout root. |
| `Kind` | `FileFilterKind` | `DirectoryEntries` returns immediate children; `TextContent` returns one supported text file. |

#### `FileFilterKind`

| Value | Meaning |
|---|---|
| `DirectoryEntries` | Return immediate children of a directory path. |
| `TextContent` | Return one supported UTF-8 text file. |

#### `FilterFilesResult`

| Field | Type | Meaning |
|---|---|---|
| `Path` | `string` | Normalized path that was filtered. |
| `Entries` | `IReadOnlyList<RepositoryFileEntry>` | Immediate children for `DirectoryEntries`; otherwise empty. |
| `TextFile` | `TextFileContent?` | File content for `TextContent`; otherwise `null`. |

Exactly one result shape is populated according to the requested `Kind`.

#### `UpdateFilesRequest`

| Field | Type | Meaning |
|---|---|---|
| `Updates` | `IReadOnlyList<TextFileUpdate>` | One or more text updates validated as a unit before mutation. Duplicate normalized paths are rejected. |

#### `TextFileUpdate`

| Field | Type | Meaning |
|---|---|---|
| `Path` | `string` | Normalized repository-relative file path. |
| `Content` | `string` | Complete UTF-8 text content that replaces the file. |

#### `UpdateFilesResult`

| Field | Type | Meaning |
|---|---|---|
| `UpdatedPaths` | `IReadOnlyList<string>` | Stable normalized paths successfully updated. |

#### `RepositoryFileEntry`

| Field | Type | Meaning |
|---|---|---|
| `Path` | `string` | Normalized repository-relative path beginning with `/`. |
| `Name` | `string` | Display name of the entry. |
| `Kind` | `RepositoryFileKind` | `File` or `Directory`. |
| `SizeBytes` | `long?` | File size when known; `null` for directories or unknown size. |
| `IsEditableText` | `bool` | Whether the file satisfies the editable-text name and size policy. It does not promise arbitrary byte access. |

#### `TextFileContent`

| Field | Type | Meaning |
|---|---|---|
| `Path` | `string` | Normalized repository-relative path actually read. |
| `Content` | `string` | Decoded editable text. Invalid UTF-8 fails rather than silently replacing bytes. |
| `Encoding` | `string?` | Encoding used to decode the content; currently `utf-8`. |
| `SizeBytes` | `long?` | Original encoded byte count. |

The initial editable-text policy is UTF-8 text no larger than 1 MiB and restricted to known text-oriented names or extensions. General binary access requires separate byte-oriented operations and DTOs.

### Change and commit DTOs

#### `ChangedFile`

| Field | Type | Meaning |
|---|---|---|
| `Path` | `string` | Normalized repository-relative changed path. |
| `ChangeKind` | `GitChangeKind` | `Added`, `Modified`, `Deleted`, `Renamed`, `Untracked`, or `Unknown`. |

#### `CommitRequest`

| Field | Type | Meaning |
|---|---|---|
| `Message` | `string` | Non-empty commit message. |
| `AuthorName` | `string` | Non-empty author display name. |
| `AuthorEmail` | `string` | Non-empty author email accepted by the Git engine. |

#### `CommitInfo`

| Field | Type | Meaning |
|---|---|---|
| `CommitId` | `string` | Identifier of the local commit created by the operation. |
| `Message` | `string` | Commit message recorded for display. |

### Push DTOs

#### `PushReview`

| Field | Type | Meaning |
|---|---|---|
| `RepositoryUrl` | `string` | Credential-free remote URL inspected for the push. |
| `Branch` | `string` | Local branch being reviewed. |
| `OutgoingCommitId` | `string` | Exact local commit reviewed for push. |
| `DestinationRef` | `string` | Exact remote ref reviewed for update. |

#### `PushRequest`

`PushRequest` contains the same four fields as `PushReview`. A caller creates it from a review and must not alter those coordinates between inspection and push.

#### `PushResult`

| Field | Type | Meaning |
|---|---|---|
| `RepositoryUrl` | `string` | Credential-free remote URL used by the completed push. |
| `Branch` | `string` | Local branch pushed. |
| `DestinationRef` | `string` | Remote ref updated. |
| `PushedCommitId` | `string` | Commit identifier reported as pushed. |

A successful result must match the request coordinates.

## Contract invariants

### Initialization and lifetime

- Initialization is private, lazy, idempotent, and shared by concurrent callers.
- A failed initialization does not leave a partially usable Accessor.
- All successfully imported JavaScript modules are disposed, including after partial initialization.

### Cancellation

- Cancellation is forwarded to module import, initialization, JavaScript invocation, browser storage, and remote transport where supported.
- Cancellation returns control without fabricating success or leaving a secret in a field or diagnostic.

### Paths and editable text

- Every path is repository-relative, normalized, and contained beneath the active checkout root.
- Empty segments, `.` and `..`, backslash traversal, encoded traversal, absolute filesystem paths, and paths outside the active checkout are rejected.
- Text operations never claim arbitrary-file support. Unsupported, binary, invalid UTF-8, or oversized content fails safely and is never rewritten from lossy decoding.

### Push coordinates

- `InspectPushAsync` returns the exact repository URL, branch, outgoing commit ID, and destination ref required by `PushAsync`.
- `PushAsync` rejects missing or mismatched coordinates.
- Push uses force-with-lease semantics rather than unconditional force.
- Remote-ahead and unsupported-ref failures receive explicit `GitOperationFailureKind` values.

### Credentials and safe failures

- Credential text is input-only and may exist only in the immediate call stack and Accessor-owned tab storage.
- Credentials never enter DTOs, instance fields, rendered markup, URLs, logs, diagnostics, exception text, persisted repository storage, or test output.
- Authentication or permission rejection forgets the stored credential before returning `CredentialRejected`.
- Missing, corrupt, or unavailable credential storage fails closed.
- Failed pushes are never replayed automatically.
- `Message` is always safe to render. `Diagnostic` is credential-free and never intended for rendering.

## iDesign review

This implementation-state review applies every section of `docs/IDESIGN-REVIEW.md` to the production tree after the S02 refactor.

### 1. Layer assignments

| Component | Layer | Cohesive responsibility | Allowed dependencies | Forbidden dependencies |
|---|---|---|---|---|
| `Pages/Home.razor` and `Components/Repository*.razor` | Client | Render repository state, collect user input, and delegate user actions. | `RepositoryWorkspaceManager`, its state contract, Blazor presentation utilities. | Accessors, JavaScript interop, Git workflow policy. |
| `RepositoryWorkspaceManager` and `RepositoryWorkspaceState` | Manager | Coordinate one repository use case and expose its credential-free observable state as one cohesive Manager contract. | Browser Git and file Accessor contracts, framework utilities. | Other Managers, direct JavaScript interop, presentation rendering. |
| `IBrowserGitAccessor`, `IBrowserFileAccessor`, `BrowserGitAccessor`, `browserGitEngine.js`, and `pushCredentialSession.js` | Accessor | Own browser filesystem, Git transport, tab credential storage, interop lifetime, and platform-failure translation. | Browser and JavaScript platform APIs, vendor libraries, adjacent Accessor DTOs. | Managers, Clients, UI state, workflow ownership. |
| `Program.cs` | Client composition root | Select scoped implementations and start the Blazor Client. | Client, Manager, and Accessor registration types. | Repository workflow or integration behavior. |
| Static host/configuration assets under `wwwroot` and configuration files | Resource | Supply immutable hosting, style, script, and configuration inputs. | None beyond host/tool processing. | Orchestration, policy, integration services. |
| Request, result, and value DTOs in `BrowserGitContracts.cs` | Accessor contract data | Carry typed data across the browser integration boundary. | Data types only. | Service behavior or a speculative Resource service/namespace. |

No Engine participates in this integration-focused change; deterministic repository workflow remains simple enough to reside in the Manager and boundary validation in the Accessor.

**Result:** PASS

### 2. Dependency direction

- [x] Calls follow `Client -> Manager -> Accessor`; `Program.cs` is the composition-only exception to runtime call flow, not a service dependency.
- [x] The concrete `RepositoryWorkspaceManager` calls no other Manager.
- [x] Engine dependency rules are **NOT APPLICABLE** because this subsystem introduces no Engine.
- [x] The Accessor owns I/O, validation at its trust boundary, safe failure translation, and lifetime only; it owns no UI state or use-case sequence.
- [x] Clients render and delegate, while static Resources contain no domain algorithm.

**Result:** PASS

### 3. Interface justification

| Interface | Implementations | Callers | Current boundary or substitution need | Keep, remove, or defer |
|---|---:|---:|---|---|
| `IBrowserGitAccessor` | 1: `BrowserGitAccessor` | 1: `RepositoryWorkspaceManager` | Protects the browser/JavaScript Git and credential platform boundary and provides the focused substitution seam used to verify Manager workflows without browser transport. | Keep |
| `IBrowserFileAccessor` | 1: `BrowserGitAccessor` | 1: `RepositoryWorkspaceManager` | Protects the browser filesystem/JavaScript platform boundary and provides the focused substitution seam used to verify file workflow behavior without browser storage. | Keep |

The interfaces represent capability facets of one genuine external platform boundary; they are not interface-per-class wrappers. The cohesive concrete `RepositoryWorkspaceManager` remains interface-free because it has no external boundary, credible alternate implementation, or otherwise unavailable testing seam.

**Result:** PASS

### 4. Cohesion and decomposition

- [x] The Manager owns the complete open, browse, edit, commit, review, credential, push, cancellation, and recovery workflow for one workspace.
- [x] The Accessor does more than pass through: it validates trust-boundary inputs, maps contracts, normalizes failures, redacts credentials, and owns shared module lifetime.
- [x] Orchestration is not fragmented across Managers; child components are presentation-only.
- [x] Both Accessor facets remain on one concrete service because browser filesystem, Git state, credential module, and disposal lifetime change together.
- [x] DTOs stay adjacent to the interfaces as Accessor contracts rather than becoming method services or a speculative Resource layer.

**Result:** PASS

### 5. Verification placement

| Behavior | Owning layer | Verification type | Evidence |
|---|---|---|---|
| Client delegation and state rendering | Client | contract and UAT | `tests/homePageContractTests.mjs`; required browser workflow suite |
| Repository workflow, cancellation, stale-review, and recovery | Manager | workflow unit | `AlmToo.Tests/Managers/Repositories/RepositoryWorkspaceManagerTests.cs` |
| Browser Git, file, credential, and failure contracts | Accessor | contract and integration | `tests/browserGitAccessorContractTests.mjs`, `tests/browserGitEngineTests.mjs`, `tests/pushCredentialSessionTests.mjs` |
| Layer graph, naming, composition, and review coverage | Cross-layer | architecture contract | `tests/browserGitArchitectureGateTests.mjs` |
| Static assets and application composition | Resource and Client composition | build and UAT | warning-free `dotnet build`; required browser workflow suite |

- [x] Engine unit verification is **NOT APPLICABLE** because no Engine is present.
- [x] Accessor behavior is checked at its JavaScript and .NET contract boundaries.
- [x] Manager behavior is checked as complete use-case workflows.
- [x] Client and Resource behavior is checked by source contracts, build, and browser UAT.

**Result:** PASS

### 6. Exceptions

None. `Program.cs` references each layer solely as the Client composition root; it performs no runtime repository workflow and therefore requires no architectural exception.

**Result:** NOT APPLICABLE — there are no intentional exceptions.

## Compliance statement

> This change follows the project iDesign policy. Layer assignments and dependency direction were reviewed. Every interface has a current architectural justification, and no unexplained interface-per-class or pass-through decomposition remains.

**Overall result:** PASS

This review must be repeated if implementation changes an operation facet, signature, DTO, dependency direction, interface, composition lifetime, or layer ownership.