# M012 Branch and History Traceability

## Scope

M012/S04 proves the browser-local branch and history workflow through the real Client surface. The deterministic fixture creates bounded divergent `main` and `feature` histories, exposes a tracked marker difference, and keeps the controlled smart-HTTP remote disposable and credential-isolated.

## Acceptance mapping

| Contract | Browser proof |
|---|---|
| Branch discovery | `repositoryBranchHistory.spec.mjs` opens the seeded repository, asserts the current branch, and verifies the native `#repository-branch` selector exposes `feature` and `main`. |
| Recent history | The flow asserts the bounded recent-commit count and both deterministic feature commit messages. |
| Clean switching | The flow selects `feature` and `main`, asserts the current branch, HEAD-to-ref agreement, divergent tracked marker digests, and clean status matrices after each switch. |
| Dirty rejection | The flow edits and saves the tracked marker through the file editor, asserts the accessible alert `Commit or discard the changed files before switching branches.`, and proves the complete browser repository snapshot is unchanged. |
| Remote immutability | Remote HEAD, refs, tracked file, and status snapshot digest are equal before and after the browser workflow. |
| Diagnostics and safety | Page errors, console errors, failed requests, HTTP failures, dialogs, proxy failures, and fixture credential redaction are asserted; browser and remote cleanup runs in `finally`. |

## Architecture review

**Result: PASS**

- **Client:** Playwright drives repository URL entry, native branch selection, file editing, and accessible status/alert surfaces only.
- **Manager:** Existing `RepositoryWorkspaceManager` owns branch-switch validation and the safe dirty-worktree rejection; the acceptance test does not bypass it.
- **Accessor/Resource:** Existing `BrowserGitAccessor` and `browserGitEngine.js` remain the application boundary. The explicit `noUpdateHead: false` checkout option preserves symbolic branch state in the browser Git Resource. The controlled remote and in-memory fixture setup are test-only Resource-boundary infrastructure.
- **Interfaces:** No new production interface or pass-through service was introduced. The retained `IBrowserGitAccessor` boundary remains justified by browser-platform integration and the deterministic test seam.

## Verification evidence

| Check | Command | Result |
|---|---|---|
| Browser contract | `cd AlmToo && node --test tests/repositoryBranchHistoryBrowserContractTests.mjs` | PASS |
| Chromium acceptance | `cd AlmToo && npx playwright test e2e/repositoryBranchHistory.spec.mjs --project=chromium` | PASS |
| Fixture regression | `cd AlmToo && node --test tests/liveSynchronizationFixtureTests.mjs tests/gitBugFixtureValidationTests.mjs` | PASS (prior slice fixture verification) |
| Full JavaScript suite | `cd AlmToo && npm test` | PASS — 116 tests |
| .NET suite | `cd AlmToo && dotnet test ../AlmToo.Tests/AlmToo.Tests.csproj --no-restore` | PASS — 69 tests |
