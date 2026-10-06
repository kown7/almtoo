# Git-Bug Browser Accessor Facet Design

## Status

**Proposed design — no production implementation yet.** This document is the architecture and verification contract that must be approved before a Git-Bug browser feature is coded.

| Field | Decision |
| --- | --- |
| Host application | AlmToo Blazor WebAssembly application |
| Owning concrete service | Existing `BrowserGitAccessor` |
| New integration facet | `IGitBugAccessor`, implemented by `BrowserGitAccessor` |
| First user value | Read-only browse and inspect Git-Bug issues in the already-open browser-local repository |
| Explicitly deferred | Creating, editing, commenting on, closing, pushing, pulling, bridge management, authentication, and GitHub issue synchronization |
| Data source | Git-Bug data stored with the active Git repository; no browser call to a local `git bug` CLI |
| Compatibility principle | Support only fixture-proven Git-Bug storage versions and surface an actionable unsupported-format result otherwise |

Git-Bug is Git-repository-native: it keeps issue data outside the working tree and synchronizes it through its Git workflow. Its CLI and GraphQL API are valid external interfaces, but a Blazor WebAssembly client cannot execute a user’s local CLI. The first AlmToo integration therefore reads the Git-Bug data associated with the browser-local repository through the existing browser Git platform boundary.

## Product boundary

### In scope for the first implementation milestone

1. Detect whether the active browser-local repository contains supported Git-Bug metadata.
2. List issues with stable identifier, title, state, labels, author display name when available, and last-updated time when available.
3. Open one issue to show its complete title, state, description, labels, participants, and ordered comments when represented by the supported data version.
4. Give users an honest empty state, unavailable-data state, or unsupported-format state without exposing raw Git objects, repository paths, or JavaScript diagnostics.
5. Preserve the existing repository workspace and never change Git refs, objects, index, worktree files, remote configuration, or credentials during Git-Bug reads.

### Out of scope

The first facet is deliberately **read-only**. It must not synthesize Git-Bug objects, invoke Git-Bug CLI commands, use a hosted GraphQL server, send credentials, write refs, stage files, commit, push, pull, or bridge issues to a third-party tracker. Those are separate product loops that need their own design, synchronization policy, and acceptance evidence.

## Architectural placement

| Component | iDesign layer | Responsibility | Allowed dependencies | Forbidden dependencies |
| --- | --- | --- | --- | --- |
| Git-Bug page and issue components | Client | Render Manager-owned list/detail state; submit user intent | `GitBugWorkspaceManager`, passive page state | `IGitBugAccessor`, `BrowserGitAccessor`, `IJSRuntime`, Git-Bug storage parsing |
| `GitBugWorkspaceManager` and state | Manager | Coordinate load/cancel/select transitions; translate typed Accessor results into display-safe state | `IGitBugAccessor`, passive Git-Bug contract records | Razor components, JS interop, raw refs/objects, Git-Bug parser rules |
| `IGitBugAccessor` and Git-Bug records | Accessor contract | Define the external Git-Bug read boundary and passive resources | Contract records only | Client/Manager types, UI formatting, JS interop implementation |
| `BrowserGitAccessor` Git-Bug facet | Accessor | Invoke the browser bridge, map bridge DTOs to typed resources, classify failures, preserve read-only operation | `IJSRuntime`, `browserGitEngine.js`, existing active repository context | Client/Manager state, Razor rendering, direct remote Git calls |
| `browserGitEngine.js` Git-Bug reader | Resource | Resolve Git-Bug metadata in the active browser filesystem and decode only supported storage formats | `isomorphic-git`, LightningFS, local repository objects and refs | .NET UI state, credentials, network writes, Git-Bug mutations |
| Fixture repository and decoder fixtures | Resource/test infrastructure | Represent versioned Git-Bug repository data and expected public read models | Test harness and browser bridge tests | Production UI behavior |

### Dependency flow

```text
Git-Bug Client components
          ↓
GitBugWorkspaceManager
          ↓
IGitBugAccessor  ← implemented by → BrowserGitAccessor
          ↓                            ↓
passive contracts                 browserGitEngine.js
                                       ↓
                          browser-local Git repository
```

The existing repository workflow remains independent. It may establish the active browser-local repository, but it does not own Git-Bug query semantics. A future Git-Bug page can use the same active-repository lifecycle without making `RepositoryWorkspaceManager` decode or present issue data.

## Interface and resource contract

### Why a separate facet interface is justified

`IGitBugAccessor` is a genuine external-format boundary, not a pass-through abstraction: Git-Bug’s storage/version semantics have credible alternate implementations (browser-local decoder today; a supported GraphQL or server-backed implementation later), and the interface isolates both that platform dependency and the required fixture seam. It is implemented by the existing cohesive `BrowserGitAccessor`; no extra service or interface-per-class is introduced.

The public contract is intentionally a **read model**, not raw Git-Bug objects. Raw refs, object IDs, commits, filesystem paths, and parser-specific fields are Resource details and must not escape the Accessor.

### Proposed passive records

```csharp
public interface IGitBugAccessor
{
    ValueTask<GitOperationResult<GitBugIssuePage>> ListIssuesAsync(
        GitBugIssueQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<GitOperationResult<GitBugIssueDetail>> GetIssueAsync(
        GitBugIssueId issueId,
        CancellationToken cancellationToken = default);
}

public readonly record struct GitBugIssueId(string Value);

public sealed record GitBugIssueQuery(
    GitBugIssueState? State = null,
    string? SearchText = null,
    string? Cursor = null,
    int PageSize = 50);

public enum GitBugIssueState { Open, Closed }

public sealed record GitBugIssuePage(
    IReadOnlyList<GitBugIssueSummary> Issues,
    string? NextCursor,
    int TotalCount);

public sealed record GitBugIssueSummary(
    GitBugIssueId Id,
    string Title,
    GitBugIssueState State,
    IReadOnlyList<string> Labels,
    string? AuthorDisplayName,
    DateTimeOffset? UpdatedAt);

public sealed record GitBugIssueDetail(
    GitBugIssueSummary Summary,
    string? Description,
    IReadOnlyList<GitBugComment> Comments,
    IReadOnlyList<string> Participants);

public sealed record GitBugComment(
    string AuthorDisplayName,
    string Body,
    DateTimeOffset? CreatedAt);
```

Final member names and required/optional fields are not approved until they are checked against a fixture created by a supported Git-Bug release. The contract must not claim values that the decoder cannot reliably obtain.

### Result and failure semantics

The facet reuses `GitOperationResult<T>` so all browser Git integration failures share a single safe result envelope. Before implementation, add only the Git-Bug-specific values needed to `GitOperationFailureKind`:

| Failure kind | User-safe message | Diagnostic policy | Manager behavior |
| --- | --- | --- | --- |
| `GitBugDataUnavailable` | “This repository has no supported Git-Bug issue data.” | Fixed category only | Empty/unavailable state, no retry loop |
| `GitBugFormatUnsupported` | “This Git-Bug data version is not supported yet.” | Supported-version identifier only | Explain limitation; preserve prior display until user dismisses/reloads |
| `GitBugDataMalformed` | “Git-Bug issue data could not be read safely.” | Fixed parser category; no object content | Error state with retry |
| existing `NetworkUnavailable` | Not expected for local reads; reserved for a future remote source | Existing safe diagnostic policy | Retry only when a remote source is deliberately added |
| `Unknown` | “Git-Bug issues could not be loaded.” | Existing safe diagnostic policy | Retry affordance |

Cancellation is not shown as an error. A later list/detail request supersedes an older request; the Manager ignores stale completions.

## Resource design

### Browser bridge responsibilities

`browserGitEngine.js` gains two read-only exports, named during implementation consistently with its existing bridge functions:

```text
listGitBugIssues(query)  -> BrowserGitResponse<GitBugIssuePageDto>
getGitBugIssue(issueId)  -> BrowserGitResponse<GitBugIssueDetailDto>
```

Each export must:

1. Call the existing dependency/filesystem initialization and require an active repository.
2. Resolve only Git-Bug metadata and Git objects from the active local repository.
3. Decode a pinned, fixture-proven storage schema into a JSON-safe DTO.
4. Validate required identifier, title, state, ordering, and bounded text fields before returning success.
5. Return a categorized bridge failure for absent, unsupported, or malformed data.
6. Perform no network request and no write-capable Git operation.

The managed `BrowserGitAccessor` owns JS module lifetime and maps DTOs exactly once to C# contract records. It must not duplicate Git-Bug format parsing or have a Client-visible `IJSRuntime` dependency.

### Format-discovery gate

The raw Git-Bug representation is an external protocol and must not be guessed from CLI output or inferred from a single hand-made ref. Before production decoding:

1. Install or obtain a pinned supported Git-Bug release in test infrastructure, not in browser production code.
2. Create a disposable repository fixture containing at minimum: two open issues, one closed issue, description, labels, comments, a non-ASCII title/body, and a missing optional field.
3. Capture the Git refs and objects required to read those records and document the supported Git-Bug version plus source hashes.
4. Prove the browser reader produces the proposed public DTOs from that fixture without a network request or mutation.
5. Add an incompatible-version fixture and prove `GitBugFormatUnsupported` is returned instead of a partial or invented issue.

If Git-Bug’s supported storage representation cannot be read safely through the browser Git library, stop the local-decoder path. Replan around a deliberately deployed GraphQL/server Accessor; do not put CLI execution or a storage-format workaround into Client code.

### Pinned fixture evidence

Task T01 pins Git-Bug `v0.11.0` (`d1819bd1dbd6`) as the first supported test-infrastructure release. The fixture and complete provenance record live under `AlmToo/tests/fixtures/gitBug/`; the release artifact, published checksum source, their SHA-256 values, scenario identifiers, and exact repository-state snapshots are sealed in `v0.11.0/fixture-manifest.json`.

The fixture proves these repository facts without adding production behavior:

- three issue refs represent two open issues and one closed issue;
- one identity ref supplies the fixture author;
- labels, descriptions, follow-up comments, Unicode text, an empty login, and a `null` labels collection are all present in the pinned CLI-observed expectations;
- 28 reachable Git-Bug objects coexist with the initial worktree commit's three objects;
- symbolic HEAD, every ref, index bytes, worktree bytes, expected JSON, and repository object bytes are hash-sealed and remain unchanged after validation.

The checked-in CLI JSON is scenario evidence, not the production read protocol. Compatibility remains limited to this pinned fixture and the exact format evidence below; no other release, packed-only representation, or unobserved operation is supported by implication.

The fixture validator emits only the release, aggregate scenario/ref/object counts, and an unchanged-state category. It emits no issue content, identity values, ref names, object IDs, hashes, or raw diagnostics.

### Browser-local format evidence

The T02 Resource test reads the fixture exclusively through the project-pinned `isomorphic-git` APIs `listRefs`, `resolveRef`, `readCommit`, `readTree`, and `readBlob`. For Git-Bug `v0.11.0`, it proves only this storage contract:

- issue metadata is rooted at `refs/bugs/*`; each observed issue is a linear commit history;
- every observed issue commit tree contains one empty `version-4` marker and an `ops` JSON blob with an `author.id` and `ops` array;
- the referenced actor is resolved through `refs/identities/*`, whose observed `version` JSON blob has identity schema version `2` and a string `name`;
- observed operations are creation (`type: 1`, title and message), comment (`type: 3`, message), closed status (`type: 4`, status `2`), and label changes (`type: 5`, added array and nullable removed array);
- replaying those operations in commit order reproduces fixture IDs, titles, open/closed state, nullable labels, author display names, descriptions, and ordered comment bodies, including Unicode and absent optional labels.

The proof deliberately does not approve participant derivation, update-time semantics, status values other than the observed open default and closed value, identity fields other than display name, or operation kinds absent from the fixture. Those proposed contract members remain nullable or subject to a later fixture-backed contract task.

Discovery fails closed with exactly four test categories: `supported`, `metadata-absent`, `malformed`, and `unsupported-format`. Missing issue refs produce metadata-absent; an unreadable referenced object produces malformed; a synthetic next commit carrying `version-5` produces unsupported-format; and neither negative outcome returns partial issue data. A complete SHA-256 snapshot of each test repository before and after discovery proves the read path changes no refs, objects, index, worktree, expected data, or fixture metadata.

### T01 iDesign review

Pass. The fixture, manifest, validator, and validator tests are **Resource/test infrastructure**. They depend only on the Node test harness and the existing pinned `isomorphic-git` package, introduce no production service or interface, and add no Client, Manager, Engine, or Accessor dependency edge. There is therefore no interface justification or architectural exception to record for this task.

### T02 iDesign review

Pass. `gitBugFixtureDiscoveryTests.mjs` is **Resource/test infrastructure** proving the future browser Resource boundary with the existing browser Git dependency. It adds no production class, service, interface, or dependency edge; orchestration, UI, managed Accessor behavior, and production browser exports remain unchanged. The test-local decoder is intentionally not promoted to a service, and the existing `IGitBugAccessor` justification remains the genuine external-format/platform boundary documented above. No architectural exception is required.

## Manager and Client behavior

### `GitBugWorkspaceManager`

The Manager is a concrete, cohesive workflow service; it does not need a second interface. It exposes passive state and these intent-level operations:

```text
LoadIssuesAsync(query, cancellationToken)
OpenIssueAsync(issueId, cancellationToken)
ReturnToIssueList()
ClearIssueState()
```

It owns one operation gate/cancellation lifecycle comparable to the repository workspace workflow. It validates page size and identifier shape before calling the Accessor, resets stale selection when repository identity changes, and notifies Client observers only after coherent state transitions.

Suggested state states are `Idle`, `LoadingList`, `ListReady`, `LoadingDetail`, `DetailReady`, `Unavailable`, `Unsupported`, and `Failed`. The state contains public read models and user-safe messages only; it never contains bridge diagnostics, repository paths, refs, object IDs, or credential material.

### Client

The Client renders three deliberate states:

- **Repository not open:** instruct the user to open a repository through the existing workflow.
- **Issue list/detail:** render typed Manager state, use accessible selection controls, and retain list context on return from detail.
- **Unavailable/unsupported/failed:** render the Manager’s safe message and a retry action only when retry is meaningful.

The Client must not call `IGitBugAccessor`, inspect ref names, marshal JS, parse issue bodies, or decide storage compatibility.

## Data integrity, security, and privacy

- Reads are restricted to the active browser-local repository. No ambient filesystem or cross-repository lookup is permitted.
- The first implementation has no credential input, remote request, token persistence, or Git mutation path.
- User-controlled title, description, comment, label, and actor text is rendered as text, never trusted HTML. Markdown rendering, if planned later, needs an explicit sanitization design.
- Query text and issue IDs are bounded and validated before bridge invocation. Page size is clamped in the Manager and bridge.
- The bridge returns aggregate/categorized diagnostics only. It never logs or returns raw object content, Git remote URLs, headers, credentials, or complete stack traces.
- Read operations must leave HEAD, refs, index, worktree files, and Git-Bug data byte-for-byte unchanged. Tests snapshot these boundaries before and after every failure and success path.
- Do not copy Git-Bug implementation code into AlmToo without a separate licensing review. A clean interoperability decoder must be independently implemented from documented/fixture-observed behavior.

## Observability

The Accessor emits the existing operation/result shape with fixed operation names such as `gitBug.listIssues` and `gitBug.getIssue`. Safe metrics/log fields are:

```text
operation, outcome, failureKind, issueCountBucket, resultState, supportedFormatVersion
```

Forbidden telemetry fields are issue body, title, comments, user names, IDs, refs, remote URLs, request query, object hashes, diagnostics, and credentials. The Manager exposes a user-safe state transition that lets the Client display a retry/error state without logging private repository content.

## Verification strategy

| Level | Proof | Location |
| --- | --- | --- |
| Contract | C# tests enforce passive contracts, result mapping, failure categories, and interface ownership. | `AlmToo.Tests/Accessor/BrowserGitAccessor/` |
| Resource contract | Node tests exercise browser bridge exports using fixture repositories, including unsupported/malformed fixtures. | `AlmToo/tests/` |
| Manager | Unit tests prove cancellation, stale-result rejection, repository-change reset, and no Accessor calls from Client code. | `AlmToo.Tests/Managers/` |
| Architecture | Source-path contract test confirms Client → Manager → `IGitBugAccessor` → Browser resource direction and permits no forbidden JS interop. | `AlmToo/tests/` |
| Browser integration | Playwright opens a fixture-backed browser-local repository, lists issues, opens one detail, and verifies no writes or credential requests. | `AlmToo/e2e/` |
| Negative paths | Missing Git-Bug data, unsupported version, malformed objects, cancellation, invalid identifier, oversized query, and bridge exception all produce safe outcomes. | Unit, Node, and Playwright tests |

The first coding slice cannot complete merely because list UI renders. It requires a fixture-backed proof that a read never changes repository state and that unsupported/malformed external data does not leak into Client state.

## Delivery sequence

1. **Discovery and contract:** pin a Git-Bug version, build fixtures, write public contract and architecture tests, and prove the local decoder can identify supported metadata without writes.
2. **Accessor facet:** implement/mapping-test the bridge and managed facet for list/detail reads with categorized failures.
3. **Manager workflow:** implement state, cancellation, selection, and repository-change reset with Manager tests.
4. **Client vertical slice:** add the accessible list/detail UI and run fixture-backed browser acceptance.

If step 1 finds the local representation unsuitable for isomorphic-git, stop before steps 2–4 and replan the facet as a GraphQL-backed `IGitBugAccessor`. That replacement remains at the same Accessor boundary; no Client or Manager needs to learn the transport.

## iDesign review

### 1. Layer assignments

The layer table above assigns all new or materially changed components. The feature is Client → Manager → Accessor → Resource; the browser bridge is a Resource, not a presentation service.

### 2. Dependency direction

Pass. Clients depend only on the concrete Manager and passive Manager state. The Manager depends only on `IGitBugAccessor` and passive contract resources. The Accessor owns JS interop. No lower layer depends on Client or Manager types.

### 3. Interface justification

Pass. `IGitBugAccessor` protects a real external Git-Bug data-format/platform boundary with credible local-decoder and GraphQL/server implementations and provides the required fixture seam. `GitBugWorkspaceManager` remains concrete because the current application has one orchestration policy and no credible alternative implementation.

### 4. Cohesion and decomposition

Pass. `BrowserGitAccessor` remains the single browser-local Git platform service; the Git-Bug facet adds a cohesive read capability rather than a parallel wrapper. The Manager owns workflow state, while the Resource owns deterministic data decoding. No method-by-method functional service layer is introduced.

### 5. Verification placement

Pass subject to the format-discovery gate. Decoder semantics are verified at the Resource boundary, state/cancellation in Manager tests, architecture direction in source-path tests, and the user loop in browser tests.

### 6. Exceptions

None. A future GraphQL-backed implementation must revisit this review because it adds a remote integration/resource and authentication/operational concerns.

## Approval checklist

- [x] Git-Bug `v0.11.0` is pinned with a provenance- and hash-sealed fixture repository.
- [x] Fixture analysis proves the browser Git stack can locate and decode the required metadata without writes.
- [x] Fixture-proven fields are reconciled: ID, title, open/closed state, nullable labels, author display name, description, and ordered comments; participants and other unobserved semantics remain unapproved.
- [ ] License/interoperability review approves the independently implemented decoder approach.
- [x] The iDesign review above remains passing after concrete file names and tests are selected.
- [ ] The milestone plan maps each delivery-sequence item to a slice with a verifiable exit criterion.
