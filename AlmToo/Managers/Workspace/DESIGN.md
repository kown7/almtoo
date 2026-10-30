# WorkspaceManager design

## Boundary and current source baseline

`WorkspaceManager` is the single application-layer coordinator for the active browser-local VCS workspace. It owns complete application use-cases, review-first sequencing, cancellation, and application-safe state. It does not own browser Git, Git-Bug decoding, browser JavaScript, or transport details; those remain below the Accessor boundary.

```text
Client -> WorkspaceManager -> IBrowserGitAccessor / IGitBugAccessor -> BrowserGitAccessor -> browser Git Resource
```

The canonical implementation is already normalized at:

- `AlmToo/Managers/Workspace/Service/WorkspaceManager.cs`
- `AlmToo/Managers/Workspace/Service/WorkspaceGitBugFacet.cs`
- `AlmToo/Managers/Workspace/Interface/WorkspaceState.cs`
- `AlmToo/Managers/Workspace/Interface/WorkspaceContracts.cs`

The plan's former `RepositoryWorkspaceManager`, `GitBugWorkspaceManager`, and duplicated state paths do not exist in this checkout. They are treated below as historical source names, not as live boundaries. No source file was moved or renamed by this inventory task.

There is no Git-Bug Engine in this boundary. Git-Bug DTOs and format/integration operations remain Accessor-owned. An Engine may be introduced only if a separate deterministic policy is identified and justified by a future review.

## Current public Manager operation inventory

Every public operation on the normalized `WorkspaceManager` is accounted for below. Overloads are grouped where they represent the same application intent. `State`, `StateChanged`, the constructor, and `Dispose` are lifecycle surface rather than use-cases and are accounted for in the state/lifecycle sections.

### Repository and workspace use-cases

| Current operation | Use-case | State transition and ownership | Accessor boundary |
|---|---|---|---|
| `InitializeCredentialStateAsync` | Initialize credential availability | Sets credential-safe availability state; never stores or publishes credential material | `IBrowserGitAccessor.HasCredentialAsync` |
| `OpenRepositoryAsync` | Open or restore the active repository workspace | Establishes the one shared repository context and resets dependent file, VCS, publication, and Git-Bug state before loading the workspace | `IBrowserGitAccessor.CloneOrOpenAsync` plus refresh/read operations |
| `LoadDirectoryAsync` | Browse a repository directory | Replaces `Entries` and `CurrentPath`; rejects the request when no repository is active | `IBrowserGitAccessor.FilterFilesAsync` |
| `SelectFileAsync` | Load a selected text file for viewing/editing | Sets `SelectedFilePath`, `SelectedFileContent`, and `EditableFileContent`; failure clears unsafe content | `IBrowserGitAccessor.FilterFilesAsync` |
| `EditSelectedFile` | Edit the selected file locally | Mutates only `EditableFileContent`; `HasUnsavedChanges` is derived by `WorkspaceState` | None; local Manager state transition |
| `SaveSelectedFileAsync` | Persist the selected file | Writes the reviewed editor content, then refreshes status and clears/reconciles edit state | `IBrowserGitAccessor.UpdateFilesAsync` and `GetStatusAsync` |
| `RefreshStatusAsync` | Refresh changed-file status | Replaces `ChangedFiles`, updates `LastRefreshSucceeded`, and invalidates stale review state as required | `IBrowserGitAccessor.GetStatusAsync` |
| `LoadBranchHistoryAsync` | Load current branch choices and recent history | Updates branch/history sections and bounded cancellation/failure state | `IBrowserGitAccessor.GetBranchesAsync` and history operation |
| `SwitchBranchAsync` | Switch the active branch | Validates current workspace safety, switches, then refreshes directory/status/branch state; failure leaves a safe categorized outcome | `IBrowserGitAccessor.CheckoutBranchAsync` plus refresh operations |

### Review-first write and synchronization use-cases

| Current operation | Use-case | State transition and ownership | Accessor boundary |
|---|---|---|---|
| `ReviewSynchronizationAsync` | Review remote synchronization before applying it | Loads `SynchronizationReview`, clears prior decision/failure, and does not apply changes | `IBrowserGitAccessor.SynchronizeAsync` in review mode |
| `ApplySynchronizationAsync` | Apply an explicitly reviewed synchronization | Requires a current compatible review and accepted decision, then refreshes workspace state | `IBrowserGitAccessor.SynchronizeAsync` in apply mode |
| `ReviewCommitAsync` | Review local changes for commit | Loads changed-file review and sets `CommitReviewLoaded`; no commit occurs | `IBrowserGitAccessor.GetStatusAsync` |
| `SetProposedCommit` | Set or clear proposed commit metadata | Stores bounded application input in `ProposedCommit`; does not invoke integration | None; local Manager state transition |
| `ConfirmLocalCommitAsync` | Commit the currently reviewed/proposed changes | Requires review and valid metadata, then records `CreatedCommit`, write outcome, and refreshed status | `IBrowserGitAccessor.CommitAsync` and `GetStatusAsync` |
| `CommitAsync(WorkspaceCommitInput)` / `CommitAsync(CommitRequest)` | Commit supplied application input | Compatibility overloads converge on the same validated commit use-case; no separate service or workflow is created | `IBrowserGitAccessor.CommitAsync` |
| `ReviewPublicationAsync` / `InspectPublicationAsync` | Review the refs/changes that may be published | Loads `PublicationReview`, clears confirmation, and exposes only passive reviewed metadata | `IBrowserGitAccessor.InspectPublicationAsync` |
| `ConfirmPublicationReview` | Confirm or revoke reviewed publication | Changes only `PublicationConfirmed`; confirmation is invalidated when the review changes | None; local Manager state transition |
| `PublishReviewedPublicationAsync` / `PublishPublicationAsync(GitPublicationRequest)` / `PublishReviewedAsync()` / `PublishReviewedAsync(GitPublicationRequest)` | Publish explicitly reviewed changes | Requires a current review and explicit confirmation, then records receipt or safe categorized failure and refreshes status | `IBrowserGitAccessor.PublishAsync` |
| `InspectPushAsync` | Review a branch push before execution | Loads `PushReview` and clears prior confirmation/failure | `IBrowserGitAccessor.InspectPushAsync` |
| `ConfirmPushReview` | Confirm or revoke reviewed push | Changes only `PushConfirmed`; does not push | None; local Manager state transition |
| `PushReviewedCommitAsync(PushReview)` | Push an explicitly reviewed commit | Revalidates the reviewed coordinate, uses input-only credential material, records safe outcome, and refreshes status | `IBrowserGitAccessor.PushAsync` |
| `StoreCredentialAsync` | Store a user-supplied credential for the active browser session | Updates only `HasCredential` and replacement-safe flags; credential value is not retained in `WorkspaceState` | `IBrowserGitAccessor.StoreCredentialAsync` |
| `ForgetCredentialAsync` | Remove the active browser credential | Clears credential availability and invalidates push state as needed | `IBrowserGitAccessor.ForgetCredentialAsync` |

### Git-Bug use-cases

`WorkspaceGitBugFacet.cs` is a cohesive partial of `WorkspaceManager`, not a second Manager. Its public operations are:

| Current operation | Use-case | State transition and ownership | Accessor boundary |
|---|---|---|---|
| `SetRepository(RepositoryInfo?)` | Synchronize Git-Bug availability with the active workspace | Updates `State.GitBug.RepositoryAvailable`; the root `State.Repository` remains the repository authority | None; lifecycle state transition |
| `LoadIssuesAsync()` / `LoadIssuesAsync(GitBugIssueQuery)` | List Git-Bug issues | Enters `LoadingList`, retains bounded list query/cursor state, then publishes list or safe failure | `IGitBugAccessor.ListIssuesAsync` |
| `OpenIssueAsync(string)` / `OpenIssueAsync(GitBugIssueId)` | Load a Git-Bug issue detail | Enters `LoadingDetail`, validates the issue identity, and publishes selected issue/detail or safe failure | `IGitBugAccessor.GetIssueAsync` |
| `EditSelectedIssueAsync(string?, string?)` | Edit the selected Git-Bug issue | Validates bounded title/description and selected repository/issue context, then refreshes detail/list state after a successful write | `IGitBugAccessor.EditIssueAsync` |
| `AddCommentToSelectedIssueAsync(string?)` | Add a comment to the selected Git-Bug issue | Validates bounded comment input and selected context, then refreshes detail/list state after a successful write | `IGitBugAccessor.CreateCommentAsync` |
| `ReturnToIssueList` | Return from detail to retained list context | Clears selected detail while retaining the last safe list query/result | None; local state transition |
| `ClearIssueState` | Clear Git-Bug selection and list context | Resets the Git-Bug section to its idle/empty state | None; local state transition |
| `CancelCurrentOperation` | Cancel active repository or Git-Bug work | Cancels both shared and Git-Bug operation sources; stale completions cannot publish state | None; Manager lifecycle |

All public overloads above converge on use-cases, not on new service types. `Dispose` releases operation cancellation and the Accessor lifetime; it is not a workspace use-case.

## State ownership and transitions

`WorkspaceState` is the single public root owned by `WorkspaceManager`. Its passive sections are:

- **Shared operation outcome:** `Operation`, `IsBusy`, `Error`, and `Result`; `WorkspaceOperation` provides stable application operation identifiers.
- **Repository/navigation:** `Repository`, `Entries`, `CurrentPath`, selected file path/content, editable content, and the derived unsaved-change flag.
- **VCS and review:** changed files, branch/history state, commit review/proposed commit/created commit, and bounded cancellation/failure categories.
- **Publication and synchronization:** publication review/confirmation/receipt, push review/confirmation/failure, synchronization review/decision/failure.
- **Credential-safe status:** `HasCredential` and replacement/write outcomes only; credential strings and browser diagnostics never enter state.
- **Git-Bug:** the nested `GitBugState` owns list/detail selection, bounded query/cursor, write status, loading status, and safe `GitOperationFailureKind` values.

The root repository is authoritative. The Git-Bug facet's private `RepositoryIdentity`, workspace-name comparison, list-context flag, and operation generation exist only to fence stale asynchronous completions and reset Git-Bug state when the active repository changes; they are not a second public workspace context. `SetRepository` is a compatibility/lifecycle synchronization operation and is a consolidation candidate if all Clients can derive Git-Bug availability directly from root workspace state.

The Manager serializes repository operations through `operationGate` and uses cancellation sources for active work. Git-Bug operations use their own generation/cancellation fence because issue list/detail requests can be superseded independently. Failures are translated to `WorkspaceError`, `WorkspaceResult`, or safe Accessor failure categories; raw browser, HTTP, filesystem, Git, and credential diagnostics are not exposed through Client state.

## Manager-owned contract and state shape

`WorkspaceContracts.cs` and `WorkspaceState.cs` are Manager-owned boundary contracts in the folder-mirroring namespace `AlmToo.Managers.Workspace.Interface`. They define the stable `WorkspaceOperation` use-case identifiers, Client-safe `WorkspaceError` and `WorkspaceResult` records, bounded `WorkspaceCommitInput`, the passive `WorkspaceState` root, and the passive nested `GitBugState` feature section. Existing Accessor request/result records remain owned by `AlmToo.Accessor.BrowserGitAccessor.Interface`; the Manager contracts do not duplicate those DTOs.

The reconciliation portion of the boundary is intentionally typed and bounded: `WorkspaceFileComparison` and `WorkspaceReconciliationInput` carry only safe paths/object identifiers, `WorkspaceReconciliationChange` exposes deterministic conflict categories with at most two `WorkspaceReconciliationResolution` choices, and `WorkspaceReconciliationResult` carries a bounded `WorkspaceReconciliationFailureKind` rather than transport diagnostics. `WorkspaceReconciliationApplyMetadata` fences an explicit apply to a reviewed revision and bounded change count; `WorkspaceState.ReconciliationApply` is passive Manager state and never contains credential material. `WorkspaceReconciliationContract` is a pure guard for malformed, duplicate, unsafe, oversized, or credential-shaped values and does not perform I/O.

`WorkspaceError` intentionally carries only a safe message, operation identifier, stable failure category, and cancellation flag. Accessor diagnostics remain below the Manager boundary, including exception type names and `GitOperationResult.Diagnostic` values. The concrete `WorkspaceManager` remains the only Client-facing orchestration surface; no `IWorkspaceManager`, Git-Bug Engine, or Client-to-Accessor dependency was added.

## Accessor contract audit

The current production boundary is:

- `IBrowserGitAccessor` in `AlmToo/Accessor/BrowserGitAccessor/Interface/IBrowserGitAccessor.cs`: the cohesive browser Git platform contract for repository open, status, branches/history, files, local commit, publication, push, credentials, and synchronization. Current methods include `CloneOrOpenAsync`, `GetStatusAsync`, `GetBranchesAsync`, `CheckoutBranchAsync`, `GetHistoryAsync`, `CommitAsync`, `InspectPublicationAsync`, `PublishAsync`, `InspectPushAsync`, `PushAsync`, `FilterFilesAsync`, `UpdateFilesAsync`, `HasCredentialAsync`, `StoreCredentialAsync`, `ForgetCredentialAsync`, and `SynchronizeAsync`.
- `IGitBugAccessor` in `AlmToo/Accessor/BrowserGitAccessor/Interface/IGitBugAccessor.cs`: the distinct Git-Bug storage/format boundary for `ListIssuesAsync`, `GetIssueAsync`, `EditIssueAsync`, and `CreateCommentAsync`.
- `BrowserGitAccessor` is the concrete implementation and remains responsible for browser integration, validation, translation, credential handling, and safe failure mapping. Accessor contracts and records remain passive; no Client references them.

### Facet decisions

| Facet | Decision | Justification |
|---|---|---|
| `IBrowserGitAccessor` | Retain | Protects the browser JavaScript/Git/transport platform boundary and provides the required Manager test seam. Its cohesive capability set is broader than one method but still one platform integration boundary. |
| `IGitBugAccessor` | Retain | Protects the distinct Git-Bug format/storage boundary and fixture-backed substitution seam. It is not a pass-through Manager interface. |
| Former publication/file Accessor facets | Consolidate into `IBrowserGitAccessor` | `IGitPublicationAccessor` and `IBrowserFileAccessor` are absent from the current source; their operations have one concrete implementation, one platform dependency, and no independent consumer. |
| New `IWorkspaceManager` | Do not add | The concrete Manager is the cohesive Client-facing orchestration surface; no alternate implementation or necessary abstraction seam has been demonstrated. |
| Git-Bug Engine | Do not add | Current Git-Bug behavior is Accessor integration plus Manager sequencing; no separate deterministic domain policy requires an Engine. |

## Legacy `Services/Git` classification

`AlmToo/Services/Git` does not exist in the current checkout, and a production/test source scan finds no live `Services/Git` type references. The legacy inventory is nevertheless preserved for migration traceability:

| Legacy type/category | Classification | Destination or action |
|---|---|---|
| `BrowserGitService` | Unused concrete browser Git service | Removed; browser I/O belongs only to `BrowserGitAccessor`. |
| `IBrowserGitService` | Pass-through/legacy service interface | Removed; it did not protect a boundary distinct from the browser Accessor. |
| Duplicate Git result/request contracts under `Services/Git` | Legacy contract copies | Removed; passive contracts live beside the Accessor interfaces and are consumed through the retained Accessor boundary. |
| Any remaining `Services/Git` file or reference | Stale-path failure, not a new service candidate | Must not be recreated during WorkspaceManager migration; verify with a repository source scan. |

The legacy service classification does not justify a Manager, Engine, or Accessor wrapper. JavaScript/browser integration remains below `BrowserGitAccessor`; application orchestration remains in `WorkspaceManager`.

## Architecture review result for this inventory

- **Client:** consumes concrete `WorkspaceManager` state and callbacks only.
- **Manager:** owns complete workspace, VCS, publication, synchronization, and Git-Bug use-cases and safe transitions.
- **Accessor:** owns browser Git, transport, credentials, Git-Bug storage/format integration, and diagnostic translation.
- **Resource:** owns browser Git engine mechanics and passive contract data; it does not call Clients or Managers.
- **Interfaces:** only `IBrowserGitAccessor` and `IGitBugAccessor` remain justified by external/platform boundaries and test substitution seams.
- **Result:** PASS for the inventory and boundary. No new Manager interface, Engine, Client-to-Accessor dependency, or source move is introduced by this task.

## Explicit non-goals

- No `IWorkspaceManager` by default.
- No Git-Bug Engine.
- No Manager per file, branch, commit, push, publication, synchronization, or Git-Bug issue.
- No Manager-to-Manager calls.
- No raw Accessor or browser integration dependency in Clients.
- No recreation of the removed `AlmToo/Services/Git` service family.

## M016 reconciliation policy boundary

Conflict reconciliation extends the existing review-first synchronization boundary rather than creating a second workspace coordinator:

```text
Client -> WorkspaceManager -> ConflictReconciliationEngine
                         -> IBrowserGitAccessor -> BrowserGitAccessor -> browser Git Resource
```

- `WorkspaceManager` owns inspect, review, explicit apply, cancellation, stale-result protection, and Client-safe state.
- `ConflictReconciliationEngine` owns deterministic path classification and bounded resolution policy; it does not call browser APIs or render UI. It maps absent comparisons to `Unavailable`, malformed values to `Failed` with `MalformedInput`, and unsupported formats to `Unsupported` with `UnsupportedFormat`; only divergent supported paths expose both bounded resolution choices.
- `ConflictReconciliationEngine` rejects oversized, duplicate, null, and unsafe path inputs without throwing, and returns only contract-valid, sanitized reconciliation state.
- `IBrowserGitAccessor` remains the platform seam for remote refs, snapshots, tree/blob reads, and later mutation integration.
- Reconciliation inputs and outputs must contain safe identifiers and paths only; credentials, raw provider diagnostics, and exception type names stay below the Manager boundary.
- Any new interface requires a genuine platform, substitution, or testing-seam justification and a fresh iDesign review at the affected slice.
