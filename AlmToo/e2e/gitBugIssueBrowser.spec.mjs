import { expect, test } from '@playwright/test';

import {
  fixtureSnapshotDigest,
  seedGitBugFixture,
  snapshotGitBugRepository
} from './gitBugFixture.mjs';

function observeBrowserErrors(page) {
  const errors = [];
  page.on('pageerror', () => errors.push('pageerror'));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const text = message.text();
    if (text.includes('cors.isomorphic-git.org') || text.includes('ERR_BLOCKED_BY_CLIENT')) return;
    errors.push('console-error');
  });
  return errors;
}

function observeRemoteDiagnostics(page) {
  const diagnostics = { consoleErrors: [], pageErrors: [], requestFailures: [], httpErrors: [] };
  page.on('pageerror', error => diagnostics.pageErrors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error') diagnostics.consoleErrors.push(message.text());
  });
  page.on('requestfailed', request => diagnostics.requestFailures.push({
    url: request.url(),
    failure: request.failure()?.errorText ?? 'unknown'
  }));
  page.on('response', response => {
    if (response.status() >= 400) diagnostics.httpErrors.push({ url: response.url(), status: response.status() });
  });
  return diagnostics;
}

async function openFixtureRepository(page, variant) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible({ timeout: 30_000 });
  const { repositoryUrl } = await seedGitBugFixture(page, variant);

  const blockedRemoteRequests = [];
  await page.route(/^https:\/\//, route => {
    blockedRemoteRequests.push(`${route.request().method()} ${route.request().url()}`);
    return route.abort('blockedbyclient');
  });

  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('definition').filter({ hasText: 'Opened from browser storage' })).toBeVisible();

  const remoteRequests = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (!['127.0.0.1', 'localhost'].includes(url.hostname)) remoteRequests.push(`${request.method()} ${request.url()}`);
  });
  const before = fixtureSnapshotDigest(await snapshotGitBugRepository(page));
  await page.getByRole('link', { name: 'Git-Bug Issues' }).click();
  await expect(page).toHaveURL(/\/git-bug$/);
  await expect(page.getByRole('heading', { name: 'Git-Bug issues', level: 1 })).toBeVisible();
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await expect(page.getByLabel(/personal access token|credential/i)).toHaveCount(0);

  return {
    before,
    remoteRequests,
    async expectReadOnly() {
      expect(fixtureSnapshotDigest(await snapshotGitBugRepository(page)), 'Git-Bug browsing must preserve every repository file byte').toBe(before);
      expect(remoteRequests.filter(request => !blockedRemoteRequests.includes(request)), 'Git-Bug browsing must not make an unblocked off-origin request').toEqual([]);
      await expect(page.locator('input[type="password"]')).toHaveCount(0);
      await expect(page.getByLabel(/personal access token|credential/i)).toHaveCount(0);
    }
  };
}

test('Git-Bug browses fixture issues, opens detail, and returns to the retained list without mutation', async ({ page }) => {
  test.setTimeout(90_000);
  const browserErrors = observeBrowserErrors(page);
  const fixture = await openFixtureRepository(page, 'supported');

  const issueList = page.getByRole('list', { name: 'Git-Bug issue list' });
  await expect(issueList).toBeVisible();
  await expect(issueList.getByRole('listitem')).toHaveCount(3);
  await expect(page.getByLabel('Issue count')).toHaveText('3 total');

  await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
  await expect(page.getByRole('heading', { name: 'Unicode café regression 🐛', level: 2 })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Description', level: 3 })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Comments (2)', level: 3 })).toBeVisible();
  await expect(page.getByText('priority:high', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Back to issues' }).click();
  await expect(issueList).toBeVisible();
  await expect(issueList.getByRole('listitem')).toHaveCount(3);
  await expect(page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' })).toBeVisible();

  await fixture.expectReadOnly();
  expect(browserErrors, 'The Git-Bug list/detail user loop must not emit browser errors').toEqual([]);
});

test('Git-Bug opens the real remote issue, shows empty detail states, and returns without mutation', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  const diagnostics = observeRemoteDiagnostics(page);
  const repositoryUrl = process.env.ALMTOO_GITBUG_REMOTE_URL ?? 'https://github.com/kown7/gitbugtest.git';
  const workspaceName = new URL(repositoryUrl).pathname.split('/').filter(Boolean).pop()?.replace(/\.git$/, '') ?? 'gitbugtest';
  const workspacePath = `/almtoo-workspaces/${workspaceName}`;

  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible({ timeout: 30_000 });
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 120_000 });

  const before = fixtureSnapshotDigest(await snapshotGitBugRepository(page, workspacePath));
  await page.getByRole('link', { name: 'Git-Bug Issues' }).click();
  await expect(page).toHaveURL(/\/git-bug$/);
  await expect(page.getByRole('heading', { name: 'Git-Bug issues', level: 1 })).toBeVisible({ timeout: 60_000 });

  const issueList = page.getByRole('list', { name: 'Git-Bug issue list' });
  await expect(issueList).toBeVisible();
  await expect(issueList.getByRole('listitem')).toHaveCount(1);
  await expect(page.getByLabel('Issue count')).toHaveText('1 total');

  await issueList.getByRole('button').first().click();
  await expect(page.getByRole('heading', { name: 'Description', level: 3 })).toBeVisible();
  await expect(page.getByText('No description was provided.', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Comments (0)', level: 3 })).toBeVisible();
  await expect(page.getByText('No comments yet.', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Back to issues' }).click();
  await expect(issueList).toBeVisible();
  await expect(issueList.getByRole('listitem')).toHaveCount(1);

  const after = fixtureSnapshotDigest(await snapshotGitBugRepository(page, workspacePath));
  await testInfo.attach('git-bug-remote-diagnostics', {
    body: Buffer.from(JSON.stringify({ repositoryUrl, before, after, diagnostics }, null, 2)),
    contentType: 'application/json'
  });
  expect(after, 'Git-Bug read-only browsing must preserve the repository snapshot').toBe(before);
  expect(diagnostics.consoleErrors, 'The remote flow must not emit console errors').toEqual([]);
  expect(diagnostics.pageErrors, 'The remote flow must not emit page errors').toEqual([]);
  expect(diagnostics.requestFailures, 'The remote flow must not fail network requests').toEqual([]);
  expect(diagnostics.httpErrors, 'The remote flow must not receive HTTP errors').toEqual([]);
});

test('Git-Bug reports unavailable metadata safely without credentials, network, or mutation', async ({ page }) => {
  test.setTimeout(90_000);
  const browserErrors = observeBrowserErrors(page);
  const fixture = await openFixtureRepository(page, 'unavailable');

  const state = page.locator('[data-gitbug-state="unavailable"]');
  await expect(state.getByRole('heading', { name: 'Git-Bug data unavailable' })).toBeVisible();
  await expect(state).toContainText('This repository has no supported Git-Bug issue data.');
  await expect(state.getByRole('button', { name: 'Try again' })).toHaveCount(0);

  await fixture.expectReadOnly();
  expect(browserErrors, 'The unavailable-data path must not emit browser errors').toEqual([]);
});

test('Git-Bug reports an unsupported fixture version safely without credentials, network, or mutation', async ({ page }) => {
  test.setTimeout(90_000);
  const browserErrors = observeBrowserErrors(page);
  const fixture = await openFixtureRepository(page, 'unsupported');

  const state = page.locator('[data-gitbug-state="unsupported"]');
  await expect(state.getByRole('heading', { name: 'Git-Bug version unsupported' })).toBeVisible();
  await expect(state).toContainText('This Git-Bug data version is not supported yet.');
  await expect(state.getByRole('button', { name: 'Try again' })).toHaveCount(0);

  await fixture.expectReadOnly();
  expect(browserErrors, 'The unsupported-format path must not emit browser errors').toEqual([]);
});
