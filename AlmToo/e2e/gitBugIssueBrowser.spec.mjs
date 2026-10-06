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
    if (message.type() === 'error') errors.push('console-error');
  });
  return errors;
}

async function openFixtureRepository(page, variant) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible({ timeout: 30_000 });
  const { repositoryUrl } = await seedGitBugFixture(page, variant);

  const remoteRequests = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (!['127.0.0.1', 'localhost'].includes(url.hostname)) remoteRequests.push(`${request.method()} ${url.origin}`);
  });
  await page.route(/^https:\/\//, route => route.abort('blockedbyclient'));

  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('definition').filter({ hasText: 'Opened from browser storage' })).toBeVisible();

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
      expect(remoteRequests, 'Git-Bug browsing must not make an off-origin request').toEqual([]);
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
