import { expect, test } from '@playwright/test';

const repositoryUrl = 'https://github.com/octocat/Hello-World.git';
const sourceRevision = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const remoteBlobId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const remoteTreeId = 'cccccccccccccccccccccccccccccccccccccccc';
const reviewedPath = '/README.md';

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
