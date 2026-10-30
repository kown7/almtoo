# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: reconciliationWorkflow.spec.mjs >> inspects, reviews, and explicitly applies a bounded reconciliation
- Location: AlmToo/e2e/reconciliationWorkflow.spec.mjs:139:1

# Error details

```
Test timeout of 60000ms exceeded.
```

```
Error: locator.fill: Test timeout of 60000ms exceeded.
Call log:
  - waiting for getByLabel('Repository URL')

```

# Page snapshot

```yaml
- generic [active]: Loading
```

# Test source

```ts
  34  | }
  35  | 
  36  | export async function getStatus() {
  37  |   return ok('getStatus', 'The controlled status was read.', [
  38  |     { path: '${reviewedPath}', changeKind: 'modified' }
  39  |   ]);
  40  | }
  41  | 
  42  | export async function getRemoteSnapshot(request) {
  43  |   return ok('git.remote.snapshot', 'The controlled remote snapshot was read.', {
  44  |     ref: { name: 'refs/remotes/origin/main', objectId: sourceRevision },
  45  |     commit: {
  46  |       commitId: sourceRevision,
  47  |       treeId: remoteTreeId,
  48  |       parentCommitId: null,
  49  |       message: 'Controlled remote reconciliation fixture',
  50  |       authorName: 'Controlled Fixture',
  51  |       authoredAt: '2026-10-30T12:00:00Z'
  52  |     },
  53  |     blobs: [{ path: '${reviewedPath}', objectId: remoteBlobId, sizeBytes: 27 }],
  54  |     boundedBlobCount: 1
  55  |   });
  56  | }
  57  | 
  58  | export async function applyReviewedReconciliation(request) {
  59  |   appliedWrites.push(request);
  60  |   globalThis.__reconciliationWrites = appliedWrites;
  61  |   return ok('git.reconciliation.apply', 'The controlled reconciliation was applied.', {
  62  |     reviewId: request.reviewId,
  63  |     sourceRevision: request.sourceRevision,
  64  |     appliedPaths: request.selections.map(selection => selection.path),
  65  |     boundedAppliedPathCount: request.selections.length
  66  |   });
  67  | }
  68  | 
  69  | export async function readTextFile() {
  70  |   return ok('readTextFile', 'The controlled file was read.', {
  71  |     path: '${reviewedPath}', content: 'Controlled remote content\\n', encoding: 'utf-8', sizeBytes: 27
  72  |   });
  73  | }
  74  | `;
  75  | 
  76  | function observeBrowserDiagnostics(page) {
  77  |   const diagnostics = [];
  78  |   page.on('pageerror', error => diagnostics.push(`pageerror:${error.message}`));
  79  |   page.on('console', message => {
  80  |     if (message.type() === 'error') diagnostics.push(`console:${message.text()}`);
  81  |   });
  82  |   return diagnostics;
  83  | }
  84  | 
  85  | async function openControlledRepository(page, diagnostics) {
  86  |   await page.addInitScript(({ sourceRevision, remoteBlobId, remoteTreeId, reviewedPath }) => {
  87  |     const ok = (operation, message, value) => ({ operation, succeeded: true, message, value });
  88  |     const writes = [];
  89  |     globalThis.__reconciliationWrites = writes;
  90  |     globalThis.__almTooBrowserGitTestAdapter = {
  91  |       enabled: true,
  92  |       initialize: () => ok('initialize', 'Browser Git is ready.', { rootPath: '/controlled-workspace' }),
  93  |       cloneOrOpen: request => ok('cloneOrOpen', 'The controlled repository is open.', {
  94  |         repositoryUrl: request.repositoryUrl,
  95  |         workspaceName: request.workspaceName,
  96  |         rootPath: '/controlled-workspace',
  97  |         wasCloned: false
  98  |       }),
  99  |       listFiles: () => ok('listFiles', 'The controlled files were listed.', [
  100 |         { path: reviewedPath, name: 'README.md', kind: 'file', sizeBytes: 27, isEditableText: true }
  101 |       ]),
  102 |       getStatus: () => ok('getStatus', 'The controlled status was read.', [
  103 |         { path: reviewedPath, changeKind: 'modified' }
  104 |       ]),
  105 |       getRemoteSnapshot: request => ok('git.remote.snapshot', 'The controlled remote snapshot was read.', {
  106 |         ref: { name: 'refs/remotes/origin/main', objectId: sourceRevision },
  107 |         commit: {
  108 |           commitId: sourceRevision,
  109 |           treeId: remoteTreeId,
  110 |           parentCommitId: null,
  111 |           message: 'Controlled remote reconciliation fixture',
  112 |           authorName: 'Controlled Fixture',
  113 |           authoredAt: '2026-10-30T12:00:00Z'
  114 |         },
  115 |         blobs: [{ path: reviewedPath, objectId: remoteBlobId, sizeBytes: 27 }],
  116 |         boundedBlobCount: 1
  117 |       }),
  118 |       applyReviewedReconciliation: request => {
  119 |         writes.push(request);
  120 |         return ok('git.reconciliation.apply', 'The controlled reconciliation was applied.', {
  121 |           reviewId: request.reviewId,
  122 |           sourceRevision: request.sourceRevision,
  123 |           appliedPaths: request.selections.map(selection => selection.path),
  124 |           boundedAppliedPathCount: request.selections.length
  125 |         });
  126 |       },
  127 |       readTextFile: () => ok('readTextFile', 'The controlled file was read.', {
  128 |         path: reviewedPath, content: 'Controlled remote content\\n', encoding: 'utf-8', sizeBytes: 27
  129 |       })
  130 |     };
  131 |   }, { sourceRevision, remoteBlobId, remoteTreeId, reviewedPath });
  132 | 
  133 |   await page.goto('/');
> 134 |   await page.getByLabel('Repository URL').fill(repositoryUrl);
      |                                           ^ Error: locator.fill: Test timeout of 60000ms exceeded.
  135 |   await page.getByRole('button', { name: 'Open repository' }).click();
  136 |   await expect(page.getByRole('heading', { name: 'Review remote changes' })).toBeVisible();
  137 | }
  138 | 
  139 | test('inspects, reviews, and explicitly applies a bounded reconciliation', async ({ page }) => {
  140 |   test.setTimeout(60_000);
  141 |   const diagnostics = observeBrowserDiagnostics(page);
  142 |   await page.addInitScript(() => {
  143 |     globalThis.__reconciliationWrites = [];
  144 |   });
  145 | 
  146 |   await openControlledRepository(page, diagnostics);
  147 | 
  148 |   const writesBeforeReview = await page.evaluate(() => globalThis.__reconciliationWrites.length);
  149 |   expect(writesBeforeReview).toBe(0);
  150 | 
  151 |   await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  152 |   await expect(page.locator('[data-reconciliation-status="RequiresResolution"]')).toBeVisible();
  153 |   await expect(page.locator('[data-reconciliation-phase="None"]')).toBeVisible();
  154 |   await expect(page.locator(`[data-reconciliation-change="${reviewedPath}"]`)).toContainText('Divergent');
  155 |   await expect(page.locator('[data-reconciliation-change-count="true"]')).toHaveText('1');
  156 |   await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toHaveCount(0);
  157 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  158 | 
  159 |   await page.getByLabel(`Resolution for ${reviewedPath}`).selectOption('AcceptRemote');
  160 |   await page.getByRole('button', { name: 'Review selected resolutions' }).click();
  161 |   await expect(page.getByText('no remote write occurs until confirmation.')).toBeVisible();
  162 |   await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toBeDisabled();
  163 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  164 | 
  165 |   await page.getByLabel('I confirm these exact resolutions may be applied to this browser-local workspace.').check();
  166 |   await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toBeEnabled();
  167 |   await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  168 | 
  169 |   await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  170 |   await expect(page.locator('[data-reconciliation-message="true"]')).toContainText('applied');
  171 |   const appliedRequest = await page.evaluate(() => globalThis.__reconciliationWrites[0]);
  172 |   expect(appliedRequest).toMatchObject({
  173 |     reviewId: expect.any(String),
  174 |     sourceRevision
  175 |   });
  176 |   expect(appliedRequest.selections).toEqual([{ path: reviewedPath, resolution: 'AcceptRemote' }]);
  177 |   expect(appliedRequest.remotePreconditions).toEqual([{ path: reviewedPath, expectedRemoteObjectId: null }]);
  178 |   expect(diagnostics).toEqual([]);
  179 |   await expect(page.locator('body')).not.toContainText('fixture-push-secret');
  180 |   await expect(page.locator('body')).not.toContainText('at Browser');
  181 | });
  182 | 
  183 | async function openRecoveryRepository(page, scenario) {
  184 |   await page.addInitScript(({ scenario, sourceRevision, revisedSourceRevision, reviewedPath }) => {
  185 |     const writes = [];
  186 |     const calls = { snapshots: 0, applies: 0 };
  187 |     const ok = (operation, message, value) => ({ operation, succeeded: true, message, value });
  188 |     const failure = (operation, message, failureKind, diagnostic) => ({
  189 |       operation,
  190 |       succeeded: false,
  191 |       message,
  192 |       diagnostic,
  193 |       failureKind
  194 |     });
  195 |     const delayed = value => new Promise(resolve => setTimeout(() => resolve(value), 1_200));
  196 |     const snapshot = revision => ({
  197 |       ref: { name: 'refs/remotes/origin/main', objectId: revision },
  198 |       commit: {
  199 |         commitId: revision,
  200 |         treeId: 'cccccccccccccccccccccccccccccccccccccccc',
  201 |         parentCommitId: null,
  202 |         message: 'Controlled recovery fixture',
  203 |         authorName: 'Controlled Fixture',
  204 |         authoredAt: '2026-10-30T12:00:00Z'
  205 |       },
  206 |       blobs: [{ path: reviewedPath, objectId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', sizeBytes: 27 }],
  207 |       boundedBlobCount: 1
  208 |     });
  209 | 
  210 |     globalThis.__reconciliationWrites = writes;
  211 |     globalThis.__reconciliationCalls = calls;
  212 |     globalThis.__almTooBrowserGitTestAdapter = {
  213 |       enabled: true,
  214 |       initialize: () => ok('initialize', 'Browser Git is ready.', { rootPath: '/controlled-workspace' }),
  215 |       cloneOrOpen: request => ok('cloneOrOpen', 'The controlled repository is open.', {
  216 |         repositoryUrl: request.repositoryUrl,
  217 |         workspaceName: request.workspaceName,
  218 |         rootPath: '/controlled-workspace',
  219 |         wasCloned: false
  220 |       }),
  221 |       listFiles: () => ok('listFiles', 'The controlled file list was read.', [
  222 |         { path: reviewedPath, name: 'README.md', kind: 'file', sizeBytes: 27, isEditableText: true }
  223 |       ]),
  224 |       getStatus: () => {
  225 |         if (scenario === 'cancel-inspect' && globalThis.__delayNextStatus) {
  226 |           globalThis.__delayNextStatus = false;
  227 |           return new Promise(resolve => {
  228 |             globalThis.__releaseCancelledInspection = () => resolve(ok(
  229 |               'getStatus',
  230 |               'The controlled status was read.',
  231 |               [{ path: reviewedPath, changeKind: 'modified' }]
  232 |             ));
  233 |           });
  234 |         }
```