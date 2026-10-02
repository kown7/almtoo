# M004 Synchronization Traceability

## Purpose and evidence boundary

This map records the testable synchronization promises delivered in M004 and their objective proof. It is intentionally safe to inspect: it contains no secrets, PAT values, raw Git or HTTP diagnostics, or browser-storage content. The listed tests assert behavior through stable categories, contract surfaces, and controlled fixtures.

## Testable M004 promises

1. **Review before mutation.** A synchronization review fetches and classifies the incoming history, commit summary, and changed-file summary before any browser-local workspace mutation. Applying requires the exact reviewed update and an explicit user confirmation.
2. **Clean fast-forward with browser-local refresh.** For a reviewed fast-forward on a clean workspace, explicit apply advances the browser-local repository, flushes durable local metadata, and the refreshed browser session reopens the updated workspace from local storage.
3. **No-loss dirty-workspace blocking.** Apply is blocked without changing browser-local files when unsaved editor changes or uncommitted working-tree changes exist. The user must save, discard, or commit before a later review and apply.
4. **Stable safe outcomes.** Current, remote-ahead, divergent, inaccessible or credential-rejected, malformed request, browser-storage failure, and network-unavailable paths yield stable safe outcomes. Rejected review or apply leaves the local workspace unchanged and does not expose raw integration diagnostics.
5. **Tab-scoped input-only PAT handling.** A personal access token is accepted only as tab-scoped, input-only credential material for an authenticated operation; UI, Resource data, storage, URLs, and observable diagnostics retain no credential value.

## iDesign ownership

The established layering remains unchanged:

- **Client** renders credential-free state and collects user intent.
- **Manager** orchestrates safe review, confirmation, precondition checks, and result mapping.
- **Accessor** owns browser Git, browser storage, and remote transport integration.
- **Resources** are data-only credential-free contracts.

`docs/IDESIGN-REVIEW.md` records the layer, dependency, interface, and verification review. No new service, interface, Engine, or cross-layer dependency is introduced by this documentation map.

## Evidence matrix

| M004 promise | Primary objective proof | Supporting proof | What the evidence establishes |
|---|---|---|---|
| Review before mutation | `AlmToo/tests/browserGitEngineTests.mjs` | `AlmToo/tests/homePageContractTests.mjs`; `AlmToo/e2e/liveSynchronization.spec.mjs` | Engine review keeps the workspace unchanged; the Client delegates review and gates apply on confirmation; the controlled browser flow exercises that sequence. |
| Clean fast-forward and browser-local refresh | `AlmToo/tests/browserGitEngineTests.mjs` | `AlmToo/e2e/liveSynchronization.spec.mjs`; `dotnet test AlmToo/AlmToo.csproj --no-restore` | Apply follows a reviewed ready state, persists browser-local metadata, and the controlled browser proof reloads the updated workspace. |
| No-loss dirty-workspace blocking | `AlmToo/tests/homePageContractTests.mjs` | `AlmToo/e2e/liveSynchronization.spec.mjs`; `dotnet test AlmToo/AlmToo.csproj --no-restore` | Stable unsafe-precondition categories are rendered without mutation; live proof confirms editor and uncommitted work are retained. |
| Current, divergent, inaccessible, malformed, storage, and network outcomes remain safe | `AlmToo/tests/browserGitEngineTests.mjs` | `AlmToo/tests/homePageContractTests.mjs`; `AlmToo/e2e/liveSynchronization.spec.mjs` | Contract fixtures cover malformed input, failed fetch and storage/apply paths, current/ahead/divergent history, and safe redaction; live proof covers controlled inaccessible and divergent remotes. |
| Tab-scoped input-only PAT handling | `AlmToo/tests/homePageContractTests.mjs` | `AlmToo/tests/browserGitEngineTests.mjs`; `AlmToo/e2e/liveSynchronization.spec.mjs` | Client and contract checks retain credential presence only; controlled browser proof checks DOM, URLs, storage, resources, and console surfaces for credential absence. |
| Layer ownership and permitted dependencies | `AlmToo/tests/browserGitArchitectureGateTests.mjs` | `docs/IDESIGN-REVIEW.md`; `dotnet build AlmToo/AlmToo.csproj --no-restore` | Architecture gate enforces Client, Manager, Accessor, and Resource separation; managed build confirms the project compiles. |

## Verification entrypoints

Run the evidence surfaces from the repository root:

```text
node --test AlmToo/tests/browserGitEngineTests.mjs
node --test AlmToo/tests/homePageContractTests.mjs
node --test AlmToo/tests/browserGitArchitectureGateTests.mjs
dotnet test AlmToo/AlmToo.csproj --no-restore
dotnet build AlmToo/AlmToo.csproj --no-restore
npm --prefix AlmToo run test:browser:live
```

`npm --prefix AlmToo run test:browser:live` is controlled-remote browser proof, not a credential-free unit check. It requires its dedicated controlled fixture configuration and validates the live browser flow without turning remote access into a normal unit-test dependency.

## Inspection and drift protection

The follow-on repository guard at `AlmToo/tests/m004SynchronizationTraceabilityTests.mjs` verifies that this map retains every promise category, required command, evidence reference, and valid repository path. It is a static documentation-integrity check: it does not load credentials, invoke remote network calls, read browser storage, or expose credential material.
