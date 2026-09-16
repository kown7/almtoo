# M001: New milestone M001

This draft was captured automatically from structured question responses.
Use it so `/gsd` can resume the in-flight milestone discussion.

## Captured Question Round — 2026-09-13T18:52:36.328Z

### Git access

How should M001 handle browser CORS for cloning public repositories?

- **Public proxy initially (Recommended)** — Use a configurable public CORS proxy for your personal starter tool, accepting availability and privacy tradeoffs.
- **Own small proxy** — Add a minimal controlled proxy service, improving reliability and control but expanding M001 beyond a browser-only app.
- **Direct only** — Attempt direct access and support only hosts that permit browser Git HTTP requests, which may make common repositories fail.

**Selected:** None of the above
**Notes:** Use github's REST API to clone a git repository for now

## Captured Question Round — 2026-09-14T20:25:18.126Z

### Failures

We can go deep on error handling and failure modes, or I can apply sensible defaults for the source workspace, offline commits, GitHub synchronization, and browser storage. Which do you prefer?

- **Apply defaults (Recommended)** — Use conservative failure and recovery behavior suitable for this personal starter tool.
- **Go deep** — Work through individual failures, retries, recovery, and user-visible states in detail.

**Selected:** Apply defaults (Recommended)

## Captured Question Round — 2026-09-14T20:26:48.277Z

### Verification

What verification level should M001 require?

- **Automated browser flow (Recommended)** — Add unit/integration tests plus a browser-executable end-to-end flow for download, edit, offline commit, reload, and synchronization.
- **Build and unit tests** — Require compilation and focused logic tests, with browser behavior checked manually.
- **Manual verification only** — Keep setup minimal and verify the workflow interactively in the browser.

**Selected:** Build and unit tests

### Browsers

Which browser target should define done for this personal first release?

- **Current Chromium (Recommended)** — Prove the workflow in current Chrome or Edge and defer broader compatibility.
- **Chromium and Firefox** — Require both browser families, increasing storage and interop verification work.
- **Major browsers** — Include Chromium, Firefox, and Safari from the first milestone.

**Selected:** Current Chromium (Recommended)
