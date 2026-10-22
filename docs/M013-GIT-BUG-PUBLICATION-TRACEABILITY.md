# M013 Git-Bug Central Publication Traceability

## Purpose and boundary

M013 S04 proves that Git-Bug issue edits and comments remain local browser-repository mutations until an explicit central publication action. `GitBugWorkspaceManager` owns issue edits, comments, and refresh. `RepositoryWorkspaceManager` owns changed-ref review, credential state, confirmation, publication, failure recovery, credential clearing, and post-publication refresh. `IGitPublicationAccessor` is the existing Browser Git Accessor's reviewed-publication facet.

The accepted publication set is ref-aware: approved Git-Bug issue refs under `refs/bugs/*`, the required identity ref under `refs/identities/*`, and their reachable objects. The Client must show the changed refs, require explicit review and confirmation, collect a tab-scoped credential, and invoke publication only after confirmation. No credential-bearing value is placed in an Accessor contract, repository configuration, snapshot, log, diagnostic, URL, or browser-visible state.

## Proof matrix

| Requirement | Implementation boundary | Acceptance proof |
|---|---|---|
| Local Git-Bug edit/comment is explicit | `GitBugWorkspaceManager` and `browserGitEngine.js` | Workflow edits an issue or creates a comment, then observes changed Git-Bug refs before publication |
| Central publication is explicit and reviewed | `RepositoryWorkspaceManager` and `IGitPublicationAccessor` | Browser clicks changed-ref review, enters credential, confirms, and publishes; no implicit push occurs after local mutation |
| Correct refs and objects arrive remotely | controlled smart-HTTP remote and fixture snapshot helpers | `remoteBefore`/`remoteAfter` compare refs, Git-Bug partitions, required identity refs, and reachable-object evidence using stable `snapshotDigest` values |
| Credentials remain secret-safe | Manager credential lifecycle and acceptance diagnostics | Credential is entered through the UI, cleared after success/failure, and absent from DOM, URL, storage, logs, diagnostics, and durable snapshots |
| Credential rejection is recoverable | Accessor failure mapping and Manager recovery state | Rejected credential clears confirmation and credential presence, preserves a safe retry message, and allows explicit replacement and retry |
| Remote-ahead failure is recoverable | reviewed remote preconditions and Manager recovery state | Remote-ahead response invalidates the stale review, avoids automatic replay, and permits fresh review after reconciliation |
| Duplicate publication is prevented | publication receipt and refreshed state | Repeating the same accepted browser action does not create a second remote outcome; stable before/after digests and request assertions remain consistent |
| Browser diagnostics are clean | Client boundary and Playwright harness | Chromium scenarios assert no page errors, console errors, failed requests, or secret-bearing failure artifacts |

## Scenarios

- `gitBugWriteWorkflow.spec.mjs`: edits the Unicode fixture issue, reviews the changed Git-Bug refs, commits the local mutation, publishes through the controlled remote, and compares safe remote snapshots.
- `gitBugWriteRecovery.spec.mjs`: covers credential rejection, replacement/retry, remote-ahead recovery, and safe failure-state reset without persisting credentials.
- `gitBugWriteRepeatability.spec.mjs`: covers repeatable publication outcomes and duplicate prevention using transport-independent fixture digests.
- `controlledGitBugRemote.mjs`, `gitBugAcceptanceHarness.mjs`, and `gitBugFixture.mjs`: provide disposable smart-HTTP transport, ref/object reachability snapshots, deterministic digests, and redacted diagnostics.

## Verification commands

Run from `/app`:

```text
node --test AlmToo/tests/liveSynchronizationFixtureTests.mjs AlmToo/tests/gitBugFixtureValidationTests.mjs AlmToo/tests/gitBugFixtureDiscoveryTests.mjs
npm test --prefix AlmToo
dotnet test AlmToo.Tests/AlmToo.Tests.csproj --no-restore
cd AlmToo && npx playwright test e2e/gitBugWriteWorkflow.spec.mjs e2e/gitBugWriteRecovery.spec.mjs e2e/gitBugWriteRepeatability.spec.mjs --project=chromium
```

The final T03 verification additionally runs the focused .NET project, the complete JavaScript suite, and the three Chromium publication specs as one required closeout sequence. Command results and durations are recorded in the task summary and slice UAT evidence; this document records the durable mapping rather than copying credential-bearing runtime output.

## Layer and interface review

- **Client:** renders passive Manager state and emits explicit review, credential, confirmation, publish, retry, and recovery intents.
- **Manager:** coordinates the issue and repository publication workflows without Manager-to-Manager calls.
- **Accessor:** validates reviewed refs and performs central smart-HTTP publication through `IGitPublicationAccessor`.
- **Resource:** browser Git/isomorphic-git performs local Git-Bug object/ref mechanics; controlled remote and snapshots are test infrastructure.
- **Interface justification:** `IGitPublicationAccessor` protects the reviewed-ref validation and browser/remote substitution boundary while retaining the existing branch-only push facet. No pass-through interface or interface-per-method service was introduced.

## Limitations

This proof uses a disposable controlled smart-HTTP remote and fixture-proven Git-Bug `v0.11.0` storage. It does not claim support for arbitrary Git-Bug storage versions, issue closing, pull/reconciliation, bridge management, hosted issue synchronization, or user-owned live credentials. Those capabilities require separate contract and acceptance evidence.
