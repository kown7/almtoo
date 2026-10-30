# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: reconciliationWorkflow.spec.mjs >> rejects stale remote apply without writing and requires a fresh review
- Location: AlmToo/e2e/reconciliationWorkflow.spec.mjs:373:1

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
  235 | 
  236 |         return ok('getStatus', 'The controlled status was read.', [{ path: reviewedPath, changeKind: 'modified' }]);
  237 |       },
  238 |       getRemoteSnapshot: () => {
  239 |         calls.snapshots += 1;
  240 |         if (scenario === 'safe-failure') {
  241 |           return failure(
  242 |             'git.remote.snapshot',
  243 |             'token fixture-push-secret leaked from Error: raw browser diagnostic at BrowserGitResource',
  244 |             'remoteSnapshotDataUnavailable',
  245 |             'Error: raw browser diagnostic at BrowserGitResource\nAuthorization: Bearer fixture-push-secret');
  246 |         }
  247 | 
  248 |         const revision = scenario === 'stale-remote' && calls.snapshots > 1
  249 |           ? revisedSourceRevision
  250 |           : sourceRevision;
  251 |         return ok('git.remote.snapshot', 'The controlled remote snapshot was read.', snapshot(revision));
  252 |       },
  253 |       applyReviewedReconciliation: request => {
  254 |         calls.applies += 1;
  255 |         if (scenario === 'cancel-apply' && calls.applies === 1) {
  256 |           return delayed(ok('git.reconciliation.apply', 'The delayed apply completed.', {
  257 |             reviewId: request.reviewId,
  258 |             sourceRevision: request.sourceRevision,
  259 |             appliedPaths: [reviewedPath],
  260 |             boundedAppliedPathCount: 1
  261 |           }));
  262 |         }
  263 |         if (scenario === 'write-failure' && calls.applies === 1) {
  264 |           return failure(
  265 |             'git.reconciliation.apply',
  266 |             'token fixture-push-secret leaked from Error: raw browser write diagnostic at BrowserGitResource',
  267 |             'reconciliationWriteFailed',
  268 |             'Error: raw browser write diagnostic at BrowserGitResource\nAuthorization: Bearer fixture-push-secret');
  269 |         }
  270 |         if (scenario === 'stale-remote' && calls.applies === 1) {
  271 |           return failure(
  272 |             'git.reconciliation.apply',
  273 |             'raw stale remote transport detail: fixture-push-secret',
  274 |             'reconciliationStaleRemote',
  275 |             'Error: stale remote at BrowserGitResource');
  276 |         }
  277 | 
  278 |         writes.push(request);
  279 |         return ok('git.reconciliation.apply', 'The controlled reconciliation was applied.', {
  280 |           reviewId: request.reviewId,
  281 |           sourceRevision: request.sourceRevision,
  282 |           appliedPaths: [reviewedPath],
  283 |           boundedAppliedPathCount: 1
  284 |         });
  285 |       }
  286 |     };
  287 |   }, { scenario, sourceRevision, revisedSourceRevision, reviewedPath });
  288 | 
  289 |   await page.goto('/');
> 290 |   await page.getByLabel('Repository URL').fill(repositoryUrl);
      |                                           ^ Error: locator.fill: Test timeout of 60000ms exceeded.
  291 |   await page.getByRole('button', { name: 'Open repository' }).click();
  292 |   await expect(page.getByRole('heading', { name: 'Review remote changes' })).toBeVisible();
  293 | }
  294 | 
  295 | async function reviewAndConfirmReconciliation(page, resolution = 'AcceptRemote') {
  296 |   await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  297 |   await expect(page.locator('[data-reconciliation-status="RequiresResolution"]')).toBeVisible();
  298 |   await page.getByLabel(`Resolution for ${reviewedPath}`).selectOption(resolution);
  299 |   await page.getByRole('button', { name: 'Review selected resolutions' }).click();
  300 |   await expect(page.getByRole('checkbox', { name: /confirm these exact resolutions/i })).toBeVisible();
  301 |   await page.getByRole('checkbox', { name: /confirm these exact resolutions/i }).check();
  302 | }
  303 | 
  304 | async function expectNoUnsafeBrowserDiagnostics(page, diagnostics) {
  305 |   expect(diagnostics).toEqual([]);
  306 |   const body = page.locator('body');
  307 |   await expect(body).not.toContainText('fixture-push-secret');
  308 |   await expect(body).not.toContainText('raw browser');
  309 |   await expect(body).not.toContainText('BrowserGitResource');
  310 |   await expect(body).not.toContainText('at Browser');
  311 | }
  312 | 
  313 | test('cancels inspection without allowing a delayed result to overwrite cancelled state', async ({ page }) => {
  314 |   test.setTimeout(60_000);
  315 |   const diagnostics = observeBrowserDiagnostics(page);
  316 |   await openRecoveryRepository(page, 'cancel-inspect');
  317 | 
  318 |   await page.evaluate(() => { globalThis.__delayNextStatus = true; });
  319 |   await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  320 |   await expect(page.locator('[data-reconciliation-progress="ReviewReconciliation"]')).toBeVisible();
  321 |   await expect.poll(() => page.evaluate(() => typeof globalThis.__releaseCancelledInspection)).toBe('function');
  322 |   await page.getByRole('button', { name: 'Cancel current operation' }).click();
  323 |   await expect(page.locator('[data-reconciliation-phase="None"]')).toBeVisible();
  324 |   await expect(page.locator('[data-reconciliation-status="Inspecting"]')).toBeVisible();
  325 |   await page.evaluate(() => globalThis.__releaseCancelledInspection());
  326 |   await page.waitForTimeout(1_500);
  327 | 
  328 |   expect(await page.evaluate(() => globalThis.__reconciliationCalls.snapshots)).toBe(0);
  329 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  330 |   await expect(page.locator('[data-reconciliation-status="RequiresResolution"]')).toHaveCount(0);
  331 |   await expect(page.locator('[data-reconciliation-change-count="true"]')).toHaveCount(0);
  332 |   await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
  333 | });
  334 | 
  335 | test('cancels apply, retains the valid review, and retries the bounded write', async ({ page }) => {
  336 |   test.setTimeout(60_000);
  337 |   const diagnostics = observeBrowserDiagnostics(page);
  338 |   await openRecoveryRepository(page, 'cancel-apply');
  339 |   await reviewAndConfirmReconciliation(page);
  340 | 
  341 |   await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  342 |   await expect(page.locator('[data-reconciliation-progress="ApplyReconciliation"]')).toBeVisible();
  343 |   await page.getByRole('button', { name: 'Cancel current operation' }).click();
  344 |   await expect(page.locator('[data-reconciliation-status="Cancelled"]')).toBeVisible();
  345 |   await expect(page.locator('[data-reconciliation-retryable="true"]')).toBeVisible();
  346 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  347 | 
  348 |   await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  349 |   await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  350 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(1);
  351 |   await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
  352 | });
  353 | 
  354 | test('retries a recoverable write failure while keeping local state and safe error text', async ({ page }) => {
  355 |   test.setTimeout(60_000);
  356 |   const diagnostics = observeBrowserDiagnostics(page);
  357 |   await openRecoveryRepository(page, 'write-failure');
  358 |   await reviewAndConfirmReconciliation(page);
  359 | 
  360 |   await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  361 |   await expect(page.locator('[data-reconciliation-status="Failed"]')).toBeVisible();
  362 |   await expect(page.locator('[data-reconciliation-failure="WriteFailed"]')).toBeVisible();
  363 |   await expect(page.locator('[data-reconciliation-retryable="true"]')).toBeVisible();
  364 |   await expect(page.locator('[data-reconciliation-message="true"]')).toContainText('write failed');
  365 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  366 | 
  367 |   await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  368 |   await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  369 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(1);
  370 |   await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
  371 | });
  372 | 
  373 | test('rejects stale remote apply without writing and requires a fresh review', async ({ page }) => {
  374 |   test.setTimeout(60_000);
  375 |   const diagnostics = observeBrowserDiagnostics(page);
  376 |   await openRecoveryRepository(page, 'stale-remote');
  377 |   await reviewAndConfirmReconciliation(page);
  378 | 
  379 |   await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  380 |   await expect(page.locator('[data-reconciliation-status="Failed"]')).toBeVisible();
  381 |   await expect(page.locator('[data-reconciliation-failure="StaleRemote"]')).toBeVisible();
  382 |   await expect(page.locator('[data-reconciliation-retryable="true"]')).toHaveCount(0);
  383 |   expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  384 |   await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toHaveCount(0);
  385 | 
  386 |   await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  387 |   await expect(page.locator('[data-reconciliation-source-revision="true"]')).toHaveText(revisedSourceRevision);
  388 |   await expect(page.getByLabel(`Resolution for ${reviewedPath}`)).toBeVisible();
  389 |   await page.getByLabel(`Resolution for ${reviewedPath}`).selectOption('AcceptRemote');
  390 |   await page.getByRole('button', { name: 'Review selected resolutions' }).click();
```