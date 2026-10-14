import { expect, test } from '@playwright/test';

import { startControlledGitBugRemote } from './controlledGitBugRemote.mjs';
import { seedGitBugFixture } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';

function collectDiagnostics(page) {
  const diagnostics = { consoleErrors: [], pageErrors: [], requestFailures: [], httpErrors: [] };
  page.on('pageerror', error => diagnostics.pageErrors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error') diagnostics.consoleErrors.push(message.text());
  });
  page.on('requestfailed', request => diagnostics.requestFailures.push(request.failure()?.errorText ?? 'unknown'));
  page.on('response', response => {
    if (response.status() >= 400) diagnostics.httpErrors.push(response.status());
  });
  return diagnostics;
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
    const target = `${remoteUrl}${proxiedUrl.slice(repositoryTarget.length)}`;
    const headers = { ...request.headers() };
    delete headers.host;
    delete headers.origin;
    try {
      const response = await fetch(target, {
        method: request.method(),
        headers,
        body: ['GET', 'HEAD'].includes(request.method()) ? undefined : request.postDataBuffer()
      });
      const responseHeaders = Object.fromEntries(response.headers.entries());
      responseHeaders['access-control-allow-origin'] = '*';
      responseHeaders['access-control-allow-headers'] = '*';
      await route.fulfill({ status: response.status, headers: responseHeaders, body: Buffer.from(await response.arrayBuffer()) });
    } catch {
      await route.abort('failed');
    }
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

async function openIssueAndPrepareCommit(page, remote, title) {
  await routeControlledCorsProxy(page, remote.url, remote.url);
  await openFixture(page, remote.url);
  await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
  await page.getByLabel('Title').fill(title);
  await page.getByRole('textbox', { name: 'Description' }).fill('Recovery-path edit with deterministic controlled remote failure.');
  await page.getByRole('button', { name: 'Save issue locally' }).click();
  await expect(page.getByRole('heading', { name: title, level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'Review changed files' }).click();
  await page.getByLabel('Commit message').fill('Accept controlled recovery write');
  await page.getByLabel('Author name').fill('Browser Recovery');
  await page.getByLabel('Author email').fill('recovery@example.invalid');
  await page.getByRole('button', { name: 'Prepare commit details' }).click();
  await expect(page.getByRole('heading', { name: 'Confirm local commit', level: 4 })).toBeVisible();
  await page.getByRole('button', { name: 'Confirm and create local commit' }).click();
  await expect(page.getByText(/Local commit created:/)).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'Review push target' }).click();
  await expect(page.locator('.gitbug-push-review')).toContainText(remote.url);
  await expect(page.locator('.gitbug-push-review')).toContainText('Branch');
}

async function assertCredentialCleared(page, { durable = true } = {}) {
  const credentialInput = page.getByLabel('Tab-scoped credential');
  if (await credentialInput.count()) await expect(credentialInput).toHaveValue('');
  const browserState = await page.evaluate(() => ({
    html: document.documentElement.innerHTML,
    url: location.href,
    local: JSON.stringify(localStorage),
    session: JSON.stringify(sessionStorage)
  }));
  expect(browserState.html).not.toContain(credential);
  expect(browserState.url).not.toContain(credential);
  if (durable) {
    expect(browserState.local).not.toContain(credential);
    expect(browserState.session).not.toContain(credential);
  }
}

async function pushWithExplicitCredential(page) {
  await page.getByLabel('Tab-scoped credential').fill(credential);
  await page.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
  await expect(page.getByRole('heading', { name: 'Confirm remote push', level: 4 })).toBeVisible();
  await page.getByRole('button', { name: 'Confirm push' }).click();
  await expect(page.getByRole('button', { name: 'Push reviewed commit' })).toBeVisible();
  await page.getByRole('button', { name: 'Push reviewed commit' }).click();
}

test.describe('Git-Bug controlled push recovery', () => {
  test('recovers from authentication failure only after explicit credential replacement', async ({ page }) => {
    test.setTimeout(180_000);
    const remote = await startControlledGitBugRemote({ username: credential, password: 'x-oauth-basic', failureMode: 'auth' });
    const diagnostics = collectDiagnostics(page);
    const initialRemote = await remote.snapshot();
    try {
      await openIssueAndPrepareCommit(page, remote, 'Authentication recovery issue');
      await pushWithExplicitCredential(page);
      await expect(page.getByRole('alert').first()).toContainText(/credential|permission|rejected/i);
      await assertCredentialCleared(page, { durable: false });
      const rejectedRemote = await remote.snapshot();
      expect(rejectedRemote.refs).toEqual(initialRemote.refs);
      expect(page.getByRole('button', { name: 'Push reviewed commit' })).toBeVisible();

      await remote.clearFailureMode();
      await page.getByRole('button', { name: 'Review push target' }).click();
      await page.getByLabel('Tab-scoped credential').fill(credential);
      await page.getByRole('button', { name: 'Replace credential' }).click();
      await page.getByRole('button', { name: 'Confirm push' }).click();
      await page.getByRole('button', { name: 'Push reviewed commit' }).click();
      await expect(page.getByText(/Pushed commit [0-9a-f]+ and refreshed local status\./).first()).toBeVisible({ timeout: 60_000 });
      expect((await remote.snapshot()).requestCount).toBeGreaterThan(initialRemote.requestCount);
      await assertCredentialCleared(page);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
    } finally {
      await remote.close();
    }
  });

  test('recovers from a network failure only after an explicit review retry', async ({ page }) => {
    test.setTimeout(180_000);
    const remote = await startControlledGitBugRemote({ username: credential, password: 'x-oauth-basic', failureMode: 'network' });
    const diagnostics = collectDiagnostics(page);
    const initialRemote = await remote.snapshot();
    try {
      await openIssueAndPrepareCommit(page, remote, 'Network recovery issue');
      await pushWithExplicitCredential(page);
      await expect(page.getByRole('alert').first()).toContainText(/network|reached|failed/i);
      await assertCredentialCleared(page, { durable: false });
      expect((await remote.snapshot()).refs).toEqual(initialRemote.refs);
      await expect(page.getByRole('button', { name: 'Review and retry' })).toBeVisible();

      await remote.clearFailureMode();
      await page.getByRole('button', { name: 'Review and retry' }).click();
      await expect(page.locator('.gitbug-push-review')).toContainText(remote.url);
      await page.getByRole('button', { name: 'Confirm push' }).click();
      await page.getByRole('button', { name: 'Push reviewed commit' }).click();
      await expect(page.getByText(/Pushed commit [0-9a-f]+ and refreshed local status\./).first()).toBeVisible({ timeout: 60_000 });
      expect((await remote.snapshot()).requestCount).toBeGreaterThan(initialRemote.requestCount);
      await assertCredentialCleared(page);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.consoleErrors
        .filter(message => !message.includes('status of 401') && !message.includes('ERR_FAILED'))).toEqual([]);
    } finally {
      await remote.close();
    }
  });
});
