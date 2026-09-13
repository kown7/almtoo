# Requirements — Browser-Based Git Text Editor

## 1. Product Summary

Build a Blazor-based tool that can check out a Git repository, keep a local working copy in the browser, allow editing text files, create commits, and eventually push those commits back to the remote repository.

The first version should focus on a safe local browser workflow for public repositories only: clone/open repository content, inspect files, edit text files, view changes, and create local commits. The implementation should use a browser-compatible Git engine exposed to Blazor through a C# abstraction. A pure .NET Git library is preferred only if it works reliably in Blazor WebAssembly; otherwise, a JavaScript browser Git library accessed through JS interop is acceptable. Private repositories, authenticated clone, and remote push support are eventual capabilities and should be designed for, but not required in the first release.

## 2. Goals

- Let a user work with a public Git repository from the Blazor app.
- Use a browser-compatible Git library for browser-local Git operations where practical.
- Prefer a C# service abstraction over the Git engine so the underlying implementation can be replaced if needed.
- Keep repository data locally in the browser where possible.
- Enable browsing and editing text files from the repository.
- Track file modifications relative to the checked-out state.
- Allow committing local changes with a commit message.
- Prepare the design so authenticated push can be added later.

## 3. Non-Goals for Initial Version

- Full Git feature parity.
- Binary file editing.
- Merge conflict resolution UI.
- Multi-user collaboration.
- Private repository checkout in the first release.
- Authenticated clone in the first release.
- Remote push in the first release.
- Support for every Git hosting provider-specific workflow.

## 4. Functional Requirements

### R001 — Repository checkout

The app shall allow the user to provide a public Git repository URL and create a local browser-held working copy.

Acceptance criteria:
- User can enter a public repository URL.
- App attempts to fetch repository content without requiring credentials.
- App shows progress or status while loading.
- App reports checkout errors clearly.
- App fails quietly for private/authentication-required repositories in the initial release.
- App does not expose credentials in logs or UI messages.

### R002 — Browser-local repository storage

The app shall store the checked-out repository locally in browser storage so the user can continue working without immediately re-fetching everything. The initial storage backend shall use `lightning-fs`, backed by browser storage such as IndexedDB.

Acceptance criteria:
- Repository files can be reloaded after page refresh.
- Local edits survive page refresh before commit.
- Storage failures are reported with actionable messages.
- The storage design accounts for browser quota limits.
- `lightning-fs` is used as the initial browser filesystem overlay unless implementation evidence shows it is unsuitable.

### R003 — File browser

The app shall display a navigable tree or list of repository files and folders.

Acceptance criteria:
- User can browse folders.
- User can select a file to view.
- Directories and files are visually distinguishable.
- Unsupported files are clearly marked or blocked from editing.

### R004 — Text file viewing

The app shall display supported text files from the local working copy.

Acceptance criteria:
- Common text files can be opened and read.
- File path and modification status are visible.
- Large files are handled safely with a size limit or warning.
- Binary files are not rendered as editable text.

### R005 — Text file editing

The app shall allow editing supported text files and saving those edits to the local browser working copy.

Acceptance criteria:
- User can modify file content.
- User can save edits locally.
- Unsaved changes are indicated.
- User receives clear feedback if saving fails.

### R006 — Change detection

The app shall detect and display modified files relative to the current checked-out baseline or last local commit.

Acceptance criteria:
- Modified files are listed.
- Added, modified, and deleted states are distinguishable when supported.
- User can inspect changed file content or a simple diff before committing.

### R007 — Local commits

The app shall allow the user to create a local Git commit from selected or all pending changes.

Acceptance criteria:
- User can enter a commit message.
- Empty commit messages are rejected.
- App creates a commit in the local repository model.
- After commit, the working tree status updates.
- Commit failures are shown clearly.

### R008 — Commit history

The app shall show recent local commit history for the checked-out repository.

Acceptance criteria:
- User can see commit hash, message, author, and date where available.
- Newly created local commits appear in the history.
- Errors reading history are visible and non-destructive.

### R009 — Push preparation

The app shall preserve enough repository metadata and commit structure to support future push back to a remote repository.

Acceptance criteria:
- Local commits are represented in a way compatible with later remote synchronization.
- Remote URL is retained without storing secrets unsafely.
- The UI may show push as unavailable or planned, but must not pretend push is complete.

### R010 — Future authenticated remote operations

A future version shall allow authenticated clone of private repositories and authenticated push of local commits back to the remote repository. The expected future authentication methods are username/password where supported and personal access tokens where required by the Git host.

Acceptance criteria:
- User can authenticate securely with username/password or a personal access token.
- App can push commits to a configured branch.
- Push errors, authorization failures, and rejected updates are reported clearly.
- Secrets are not persisted insecurely or displayed.

### R011 — Browser-compatible Git implementation

The app shall use `isomorphic-git` as the initial browser-compatible Git implementation for checkout, working tree inspection, change detection, local commit creation, and future push support. The Git engine shall be hidden behind a C# service abstraction used by the Blazor application.

Acceptance criteria:
- Git operations can run in the browser without a required application backend for the initial local workflow.
- A pure .NET Git implementation may be used only if it works reliably in Blazor WebAssembly.
- The app uses `isomorphic-git` through Blazor JS interop for the starter implementation.
- The selected library supports or can reasonably support clone/fetch, file access, status, commit history, commit creation, and future push.
- Library limitations are documented before implementation begins.
- Any fallback to server-assisted Git operations is treated as an explicit architecture decision, not an accidental dependency.

## 5. Quality Requirements

### Q001 — Security

The app shall avoid exposing repository credentials, access tokens, or private URLs in logs, browser storage messages, exceptions, or UI output.

### Q002 — Browser storage safety

The app shall handle browser quota limits, storage unavailability, and corrupted local repository state gracefully.

### Q003 — Offline tolerance

After a repository is checked out and stored locally, the app should allow browsing and editing local files without network access.

### Q004 — Error visibility

Important storage and file-editing failures shall be surfaced to the user with contextual, actionable messages. For the initial public-repository-only release, private/authentication-required repository checkout failures may fail quietly without detailed user-facing diagnostics.

### Q005 — Text-file safety

The app shall avoid corrupting line endings, encodings, or binary files. Initial editing support may be limited to UTF-8 text files.

### Q006 — Performance boundaries

The app shall enforce reasonable first-version limits for repository size, file size, and number of files in the browser-local implementation. Exact limits may be tuned during implementation based on the selected Git/storage library, but the app should protect the browser from repositories or files that are too large for responsive local editing.

## 6. User Flows

### Flow 1 — Check out a repository

1. User opens the app.
2. User enters a repository URL.
3. App fetches repository content.
4. App stores the repository locally in browser storage.
5. App displays the file browser.

### Flow 2 — Edit a text file

1. User selects a text file.
2. App displays file content.
3. User edits content.
4. User saves changes.
5. App marks the file as modified.

### Flow 3 — Commit changes

1. User opens the changes view.
2. App lists modified files.
3. User reviews the changes.
4. User enters a commit message.
5. User creates a local commit.
6. App updates status and commit history.

### Flow 4 — Resume local work

1. User refreshes or reopens the app.
2. App detects a locally stored repository.
3. App restores file state, pending edits, and local commits.
4. User continues working.

## 7. Initial Release Scope

Recommended first release:
- Public repository URL input.
- `isomorphic-git` integration behind a C# abstraction.
- Browser-local repository persistence using `lightning-fs`.
- File tree browsing.
- Plain UTF-8 text file viewing and editing.
- Change list.
- Local commit creation.
- Recent commit history.
- Quiet failure behavior for private repositories and authenticated clone.
- Clear unsupported-state messaging for push.

## 8. Open Questions

No open requirements questions remain from the initial capture. Implementation should validate library limitations, browser storage behavior, and practical size limits during the first build slice.

