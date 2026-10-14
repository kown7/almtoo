import { expect, test } from '@playwright/test';

import { startControlledGitBugRemote } from './controlledGitBugRemote.mjs';
import {
  fixtureSnapshotDigest,
  seedGitBugFixture,
  snapshotGitBugRepository
} from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';
const editedTitle = 'Unicode café regression fixed 🐛';
const editedDescription = 'Updated description with deterministic UTF-8 text.';
const comment = 'Browser acceptance comment with deterministic UTF-8 text.';

function collectDiagnostics(page) {
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

function assertNoCredential(value) {
  const serialized = JSON.stringify(value);
  expect(serialized, 'credential must not cross a browser-visible diagnostic surface').not.toContain(credential);
}

async function routeControlledCorsProxy(page, repositoryUrl, remoteUrl) {
  const proxyPrefix = 'https://cors.isomorphic-git.org/';
  await page.route(`${proxyPrefix}**`, async route => {
    const request = route.request();
    const proxiedUrl = request.url().slice(proxyPrefix.length);
    const repositoryTarget = proxiedUrl.startsWith(repositoryUrl)
      ? repositoryUrl
      : repositoryUrl.replace(/^https?:\/\//u, '');
    if (!proxiedUrl.startsWith(repositoryTarget)) {
      await route.continue();
      return;
    }
    const remoteTarget = `${remoteUrl}${proxiedUrl.slice(repositoryTarget.length)}`;
    const headers = { ...request.headers() };
    delete headers.host;
    delete headers.origin;
    const method = request.method();
    const response = await fetch(remoteTarget, {
      method,
      headers,
      body: method === 'GET' || method === 'HEAD' ? undefined : request.postDataBuffer()
    });
    const responseHeaders = Object.fromEntries(response.headers.entries());
    responseHeaders['access-control-allow-origin'] = '*';
    responseHeaders['access-control-allow-headers'] = '*';
    await route.fulfill({
      status: response.status,
      headers: responseHeaders,
      body: Buffer.from(await response.arrayBuffer())
    });
  });
}

async function openFixture(page, repositoryUrl) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible({ timeout: 30_000 });
  await seedGitBugFixture(page, 'supported', { repositoryUrl });
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 30_000 });
  await page.getByRole('link', { name: 'Git-Bug Issues' }).click();
  await expect(page).toHaveURL(/\/git-bug$/);
  await expect(page.getByRole('heading', { name: 'Git-Bug issues', level: 1 })).toBeVisible();
}

test('edits, reviews, commits, and pushes a Git-Bug issue through the browser', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  const diagnostics = collectDiagnostics(page);
  const remote = await startControlledGitBugRemote({ username: credential, password: 'x-oauth-basic' });

  try {
    const repositoryUrl = remote.url;
    await routeControlledCorsProxy(page, repositoryUrl, remote.url);
    await openFixture(page, repositoryUrl);
    const before = fixtureSnapshotDigest(await snapshotGitBugRepository(page));

    await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
    await expect(page.getByRole('heading', { name: 'Unicode café regression 🐛', level: 2 })).toBeVisible();

    await page.getByLabel('Title').fill(editedTitle);
    await page.getByRole('textbox', { name: 'Description' }).fill(editedDescription);
    await page.getByRole('button', { name: 'Save issue locally' }).click();
    await expect(page.getByRole('heading', { name: editedTitle, level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('textbox', { name: 'Description' })).toHaveValue(editedDescription, { timeout: 30_000 });

    await page.getByRole('textbox', { name: 'Comment' }).fill(comment);
    await page.getByRole('button', { name: 'Add comment locally' }).click();
    await expect(page.getByText(comment, { exact: true })).toBeVisible({ timeout: 30_000 });

    await page.getByRole('button', { name: 'Review changed files' }).click();
    await expect(page.getByRole('heading', { name: 'Review local commit', level: 3 })).toBeVisible();
    await expect(page.getByText(/changed file/i).first()).toBeVisible();
    await page.getByLabel('Commit message').fill('Accept Git-Bug browser write');
    await page.getByLabel('Author name').fill('Browser Acceptance');
    await page.getByLabel('Author email').fill('browser@example.invalid');
    await page.getByRole('button', { name: 'Prepare commit details' }).click();
    await expect(page.getByRole('heading', { name: 'Confirm local commit', level: 4 })).toBeVisible();
    await page.getByRole('button', { name: 'Confirm and create local commit' }).click();
    await expect(page.getByText(/Local commit created:/)).toBeVisible();
    const afterCommit = fixtureSnapshotDigest(await snapshotGitBugRepository(page));
    expect(afterCommit).not.toBe(before);

    await page.getByRole('button', { name: 'Review push target' }).click();
    await expect(page.locator('.gitbug-push-review')).toContainText(repositoryUrl);
    await expect(page.locator('.gitbug-push-review')).toContainText('Branch');
    await expect(page.locator('.gitbug-push-review')).toContainText('Destination');
    await expect(page.locator('.gitbug-push-review')).toContainText('Commit');

    const credentialInput = page.getByLabel('Tab-scoped credential');
    await credentialInput.fill(credential);
    await page.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
    await expect(credentialInput).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Confirm remote push', level: 4 })).toBeVisible();

    await page.getByRole('button', { name: 'Confirm push' }).click();
    await expect(page.getByRole('button', { name: 'Push reviewed commit' })).toBeVisible();
    await page.getByRole('button', { name: 'Push reviewed commit' }).click();
    await expect(page.getByText(/Pushed commit [0-9a-f]+ and refreshed local status\./).first()).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('alert')).toHaveCount(0);

    const snapshot = await remote.snapshot();
    expect(snapshot.refs.some(ref => ref.startsWith('heads/'))).toBeTruthy();
    expect(snapshot.requestCount).toBeGreaterThan(0);
    expect(fixtureSnapshotDigest(await snapshotGitBugRepository(page))).not.toBe(before);

    const storage = await page.evaluate(() => ({
      localStorage: Object.fromEntries(Object.entries(localStorage)),
      sessionStorage: Object.fromEntries(Object.entries(sessionStorage)),
      body: document.body.innerText
    }));
    assertNoCredential(storage);
    assertNoCredential(diagnostics);
    assertNoCredential(await page.evaluate(() => location.href));
    await testInfo.attach('git-bug-write-diagnostics', {
      body: Buffer.from(JSON.stringify({ before, afterCommit, snapshot, diagnostics }, null, 2)),
      contentType: 'application/json'
    });
    expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
    expect(diagnostics.pageErrors).toEqual([]);
    expect(diagnostics.requestFailures).toEqual([]);
    expect(diagnostics.httpErrors.filter(({ status }) => status !== 401)).toEqual([]);
  } finally {
    await remote.close();
  }
});
