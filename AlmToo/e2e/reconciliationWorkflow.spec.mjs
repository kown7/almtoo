import { expect, test } from '@playwright/test';

const repositoryUrl = 'https://github.com/octocat/Hello-World.git';
const sourceRevision = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const remoteBlobId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const remoteTreeId = 'cccccccccccccccccccccccccccccccccccccccc';
const reviewedPath = '/README.md';
const revisedSourceRevision = 'dddddddddddddddddddddddddddddddddddddddd';

const controlledBrowserGitModule = `
const sourceRevision = '${sourceRevision}';
const remoteBlobId = '${remoteBlobId}';
const remoteTreeId = '${remoteTreeId}';
const appliedWrites = [];
const ok = (operation, message, value) => ({ operation, succeeded: true, message, value });

export async function initialize() {
  return ok('initialize', 'Browser Git is ready.', null);
}

export async function cloneOrOpen(request) {
  return ok('cloneOrOpen', 'The controlled repository is open.', {
    repositoryUrl: request.repositoryUrl,
    workspaceName: request.workspaceName,
    rootPath: '/controlled-workspace',
    wasCloned: false
  });
}

export async function listFiles() {
  return ok('listFiles', 'The controlled files were listed.', [
    { path: '${reviewedPath}', name: 'README.md', kind: 'file', sizeBytes: 27, isEditableText: true }
  ]);
}

export async function getStatus() {
  return ok('getStatus', 'The controlled status was read.', [
    { path: '${reviewedPath}', changeKind: 'modified' }
  ]);
}

export async function getRemoteSnapshot(request) {
  return ok('git.remote.snapshot', 'The controlled remote snapshot was read.', {
    ref: { name: 'refs/remotes/origin/main', objectId: sourceRevision },
    commit: {
      commitId: sourceRevision,
      treeId: remoteTreeId,
      parentCommitId: null,
      message: 'Controlled remote reconciliation fixture',
      authorName: 'Controlled Fixture',
      authoredAt: '2026-10-30T12:00:00Z'
    },
    blobs: [{ path: '${reviewedPath}', objectId: remoteBlobId, sizeBytes: 27 }],
    boundedBlobCount: 1
  });
}

export async function applyReviewedReconciliation(request) {
  appliedWrites.push(request);
  globalThis.__reconciliationWrites = appliedWrites;
  return ok('git.reconciliation.apply', 'The controlled reconciliation was applied.', {
    reviewId: request.reviewId,
    sourceRevision: request.sourceRevision,
    appliedPaths: request.selections.map(selection => selection.path),
    boundedAppliedPathCount: request.selections.length
  });
}

export async function readTextFile() {
  return ok('readTextFile', 'The controlled file was read.', {
    path: '${reviewedPath}', content: 'Controlled remote content\\n', encoding: 'utf-8', sizeBytes: 27
  });
}
`;

function observeBrowserDiagnostics(page) {
  const diagnostics = [];
  page.on('pageerror', error => diagnostics.push(`pageerror:${error.message}`));
  page.on('console', message => {
    if (message.type() === 'error') diagnostics.push(`console:${message.text()}`);
  });
  return diagnostics;
}

async function openControlledRepository(page, diagnostics) {
  await page.addInitScript(({ sourceRevision, remoteBlobId, remoteTreeId, reviewedPath }) => {
    const ok = (operation, message, value) => ({ operation, succeeded: true, message, value });
    const writes = [];
    globalThis.__reconciliationWrites = writes;
    globalThis.__almTooBrowserGitTestAdapter = {
      enabled: true,
      initialize: () => ok('initialize', 'Browser Git is ready.', { rootPath: '/controlled-workspace' }),
      cloneOrOpen: request => ok('cloneOrOpen', 'The controlled repository is open.', {
        repositoryUrl: request.repositoryUrl,
        workspaceName: request.workspaceName,
        rootPath: '/controlled-workspace',
        wasCloned: false
      }),
      listFiles: () => ok('listFiles', 'The controlled files were listed.', [
        { path: reviewedPath, name: 'README.md', kind: 'file', sizeBytes: 27, isEditableText: true }
      ]),
      getStatus: () => ok('getStatus', 'The controlled status was read.', [
        { path: reviewedPath, changeKind: 'modified' }
      ]),
      getRemoteSnapshot: request => ok('git.remote.snapshot', 'The controlled remote snapshot was read.', {
        ref: { name: 'refs/remotes/origin/main', objectId: sourceRevision },
        commit: {
          commitId: sourceRevision,
          treeId: remoteTreeId,
          parentCommitId: null,
          message: 'Controlled remote reconciliation fixture',
          authorName: 'Controlled Fixture',
          authoredAt: '2026-10-30T12:00:00Z'
        },
        blobs: [{ path: reviewedPath, objectId: remoteBlobId, sizeBytes: 27 }],
        boundedBlobCount: 1
      }),
      applyReviewedReconciliation: request => {
        writes.push(request);
        return ok('git.reconciliation.apply', 'The controlled reconciliation was applied.', {
          reviewId: request.reviewId,
          sourceRevision: request.sourceRevision,
          appliedPaths: request.selections.map(selection => selection.path),
          boundedAppliedPathCount: request.selections.length
        });
      },
      readTextFile: () => ok('readTextFile', 'The controlled file was read.', {
        path: reviewedPath, content: 'Controlled remote content\\n', encoding: 'utf-8', sizeBytes: 27
      })
    };
  }, { sourceRevision, remoteBlobId, remoteTreeId, reviewedPath });

  await page.goto('/');
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Review remote changes' })).toBeVisible();
}

test('inspects, reviews, and explicitly applies a bounded reconciliation', async ({ page }) => {
  test.setTimeout(60_000);
  const diagnostics = observeBrowserDiagnostics(page);
  await page.addInitScript(() => {
    globalThis.__reconciliationWrites = [];
  });

  await openControlledRepository(page, diagnostics);

  const writesBeforeReview = await page.evaluate(() => globalThis.__reconciliationWrites.length);
  expect(writesBeforeReview).toBe(0);

  await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  await expect(page.locator('[data-reconciliation-status="RequiresResolution"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-phase="None"]')).toBeVisible();
  await expect(page.locator(`[data-reconciliation-change="${reviewedPath}"]`)).toContainText('Divergent');
  await expect(page.locator('[data-reconciliation-change-count="true"]')).toHaveText('1');
  await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toHaveCount(0);
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);

  await page.getByLabel(`Resolution for ${reviewedPath}`).selectOption('AcceptRemote');
  await page.getByRole('button', { name: 'Review selected resolutions' }).click();
  await expect(page.getByText('no remote write occurs until confirmation.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toBeDisabled();
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);

  await page.getByLabel('I confirm these exact resolutions may be applied to this browser-local workspace.').check();
  await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toBeEnabled();
  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();

  await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-message="true"]')).toContainText('applied');
  const appliedRequest = await page.evaluate(() => globalThis.__reconciliationWrites[0]);
  expect(appliedRequest).toMatchObject({
    reviewId: expect.any(String),
    sourceRevision
  });
  expect(appliedRequest.selections).toEqual([{ path: reviewedPath, resolution: 'AcceptRemote' }]);
  expect(appliedRequest.remotePreconditions).toEqual([{ path: reviewedPath, expectedRemoteObjectId: null }]);
  expect(diagnostics).toEqual([]);
  await expect(page.locator('body')).not.toContainText('fixture-push-secret');
  await expect(page.locator('body')).not.toContainText('at Browser');
});

async function openRecoveryRepository(page, scenario) {
  await page.addInitScript(({ scenario, sourceRevision, revisedSourceRevision, reviewedPath }) => {
    const writes = [];
    const calls = { snapshots: 0, applies: 0 };
    const ok = (operation, message, value) => ({ operation, succeeded: true, message, value });
    const failure = (operation, message, failureKind, diagnostic) => ({
      operation,
      succeeded: false,
      message,
      diagnostic,
      failureKind
    });
    const delayed = value => new Promise(resolve => setTimeout(() => resolve(value), 1_200));
    const snapshot = revision => ({
      ref: { name: 'refs/remotes/origin/main', objectId: revision },
      commit: {
        commitId: revision,
        treeId: 'cccccccccccccccccccccccccccccccccccccccc',
        parentCommitId: null,
        message: 'Controlled recovery fixture',
        authorName: 'Controlled Fixture',
        authoredAt: '2026-10-30T12:00:00Z'
      },
      blobs: [{ path: reviewedPath, objectId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', sizeBytes: 27 }],
      boundedBlobCount: 1
    });

    globalThis.__reconciliationWrites = writes;
    globalThis.__reconciliationCalls = calls;
    globalThis.__almTooBrowserGitTestAdapter = {
      enabled: true,
      initialize: () => ok('initialize', 'Browser Git is ready.', { rootPath: '/controlled-workspace' }),
      cloneOrOpen: request => ok('cloneOrOpen', 'The controlled repository is open.', {
        repositoryUrl: request.repositoryUrl,
        workspaceName: request.workspaceName,
        rootPath: '/controlled-workspace',
        wasCloned: false
      }),
      listFiles: () => ok('listFiles', 'The controlled file list was read.', [
        { path: reviewedPath, name: 'README.md', kind: 'file', sizeBytes: 27, isEditableText: true }
      ]),
      getStatus: () => scenario === 'cancel-inspect'
        ? delayed(ok('getStatus', 'The controlled status was read.', [{ path: reviewedPath, changeKind: 'modified' }]))
        : ok('getStatus', 'The controlled status was read.', [{ path: reviewedPath, changeKind: 'modified' }]),
      getRemoteSnapshot: () => {
        calls.snapshots += 1;
        if (scenario === 'safe-failure') {
          return failure(
            'git.remote.snapshot',
            'token fixture-push-secret leaked from Error: raw browser diagnostic at BrowserGitResource',
            'remoteSnapshotDataUnavailable',
            'Error: raw browser diagnostic at BrowserGitResource\nAuthorization: Bearer fixture-push-secret');
        }

        const revision = scenario === 'stale-remote' && calls.snapshots > 1
          ? revisedSourceRevision
          : sourceRevision;
        return ok('git.remote.snapshot', 'The controlled remote snapshot was read.', snapshot(revision));
      },
      applyReviewedReconciliation: request => {
        calls.applies += 1;
        if (scenario === 'cancel-apply' && calls.applies === 1) {
          return delayed(ok('git.reconciliation.apply', 'The delayed apply completed.', {
            reviewId: request.reviewId,
            sourceRevision: request.sourceRevision,
            appliedPaths: [reviewedPath],
            boundedAppliedPathCount: 1
          }));
        }
        if (scenario === 'write-failure' && calls.applies === 1) {
          return failure(
            'git.reconciliation.apply',
            'token fixture-push-secret leaked from Error: raw browser write diagnostic at BrowserGitResource',
            'reconciliationWriteFailed',
            'Error: raw browser write diagnostic at BrowserGitResource\nAuthorization: Bearer fixture-push-secret');
        }
        if (scenario === 'stale-remote' && calls.applies === 1) {
          return failure(
            'git.reconciliation.apply',
            'raw stale remote transport detail: fixture-push-secret',
            'reconciliationStaleRemote',
            'Error: stale remote at BrowserGitResource');
        }

        writes.push(request);
        return ok('git.reconciliation.apply', 'The controlled reconciliation was applied.', {
          reviewId: request.reviewId,
          sourceRevision: request.sourceRevision,
          appliedPaths: [reviewedPath],
          boundedAppliedPathCount: 1
        });
      }
    };
  }, { scenario, sourceRevision, revisedSourceRevision, reviewedPath });

  await page.goto('/');
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Review remote changes' })).toBeVisible();
}

async function reviewAndConfirmReconciliation(page, resolution = 'AcceptRemote') {
  await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  await expect(page.locator('[data-reconciliation-status="RequiresResolution"]')).toBeVisible();
  await page.getByLabel(`Resolution for ${reviewedPath}`).selectOption(resolution);
  await page.getByRole('button', { name: 'Review selected resolutions' }).click();
  await expect(page.getByRole('checkbox', { name: /confirm these exact resolutions/i })).toBeVisible();
  await page.getByRole('checkbox', { name: /confirm these exact resolutions/i }).check();
}

async function expectNoUnsafeBrowserDiagnostics(page, diagnostics) {
  expect(diagnostics).toEqual([]);
  const body = page.locator('body');
  await expect(body).not.toContainText('fixture-push-secret');
  await expect(body).not.toContainText('raw browser');
  await expect(body).not.toContainText('BrowserGitResource');
  await expect(body).not.toContainText('at Browser');
}

test('cancels inspection without allowing a delayed result to overwrite cancelled state', async ({ page }) => {
  test.setTimeout(60_000);
  const diagnostics = observeBrowserDiagnostics(page);
  await openRecoveryRepository(page, 'cancel-inspect');

  await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  await expect(page.locator('[data-reconciliation-progress="ReviewReconciliation"]')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel current operation' }).click();
  await expect(page.locator('[data-reconciliation-phase="None"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-status="Inspecting"]')).toBeVisible();
  await page.waitForTimeout(1_500);

  expect(await page.evaluate(() => globalThis.__reconciliationCalls.snapshots)).toBe(0);
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  await expect(page.locator('[data-reconciliation-status="RequiresResolution"]')).toHaveCount(0);
  await expect(page.locator('[data-reconciliation-change-count="true"]')).toHaveCount(0);
  await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
});

test('cancels apply, retains the valid review, and retries the bounded write', async ({ page }) => {
  test.setTimeout(60_000);
  const diagnostics = observeBrowserDiagnostics(page);
  await openRecoveryRepository(page, 'cancel-apply');
  await reviewAndConfirmReconciliation(page);

  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  await expect(page.locator('[data-reconciliation-progress="ApplyReconciliation"]')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel current operation' }).click();
  await expect(page.locator('[data-reconciliation-status="Cancelled"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-retryable="true"]')).toBeVisible();
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);

  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(1);
  await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
});

test('retries a recoverable write failure while keeping local state and safe error text', async ({ page }) => {
  test.setTimeout(60_000);
  const diagnostics = observeBrowserDiagnostics(page);
  await openRecoveryRepository(page, 'write-failure');
  await reviewAndConfirmReconciliation(page);

  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  await expect(page.locator('[data-reconciliation-status="Failed"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-failure="WriteFailed"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-retryable="true"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-message="true"]')).toContainText('write failed');
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);

  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(1);
  await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
});

test('rejects stale remote apply without writing and requires a fresh review', async ({ page }) => {
  test.setTimeout(60_000);
  const diagnostics = observeBrowserDiagnostics(page);
  await openRecoveryRepository(page, 'stale-remote');
  await reviewAndConfirmReconciliation(page);

  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  await expect(page.locator('[data-reconciliation-status="Failed"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-failure="StaleRemote"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-retryable="true"]')).toHaveCount(0);
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  await expect(page.getByRole('button', { name: 'Apply reviewed reconciliation' })).toHaveCount(0);

  await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  await expect(page.locator('[data-reconciliation-source-revision="true"]')).toHaveText(revisedSourceRevision);
  await expect(page.getByLabel(`Resolution for ${reviewedPath}`)).toBeVisible();
  await page.getByLabel(`Resolution for ${reviewedPath}`).selectOption('AcceptRemote');
  await page.getByRole('button', { name: 'Review selected resolutions' }).click();
  await page.getByRole('checkbox', { name: /confirm these exact resolutions/i }).check();
  await page.getByRole('button', { name: 'Apply reviewed reconciliation' }).click();
  await expect(page.locator('[data-reconciliation-status="Applied"]')).toBeVisible();
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(1);
  await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
});

test('normalizes credential and raw browser failures into a fixed safe category', async ({ page }) => {
  test.setTimeout(60_000);
  const diagnostics = observeBrowserDiagnostics(page);
  await openRecoveryRepository(page, 'safe-failure');

  await page.getByRole('button', { name: 'Inspect remote changes' }).click();
  await expect(page.locator('[data-reconciliation-status="Unavailable"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-failure="RemoteUnavailable"]')).toBeVisible();
  await expect(page.locator('[data-reconciliation-retryable="true"]')).toHaveCount(0);
  await expect(page.locator('[data-reconciliation-message="true"]')).toContainText('remote comparison snapshot');
  expect(await page.evaluate(() => globalThis.__reconciliationWrites.length)).toBe(0);
  await expectNoUnsafeBrowserDiagnostics(page, diagnostics);
});
