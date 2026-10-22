import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';

const credential = 'fixture-push-secret';

async function openIssueAndPrepareCommit(page, remote, title, setPhase) {
  setPhase('review Git-Bug refs');
  await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
  await page.getByLabel('Title').fill(title);
  await page.getByRole('textbox', { name: 'Description' }).fill('Recovery-path edit with deterministic controlled remote failure.');
  await page.getByRole('button', { name: 'Save issue locally' }).click();
  await expect(page.getByRole('heading', { name: title, level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'Review changed files' }).click();
  await expect(page.getByRole('heading', { name: 'Changed files and refs', level: 2 })).toBeVisible({ timeout: 15_000 });
  const changedRefList = page.locator('[aria-label="Changed ref list"]');
  await expect(changedRefList).toContainText('refs/bugs/');
  await expect(changedRefList).toContainText('refs/identities/');
  await expect(page.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible({ timeout: 15_000 });
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
  await expect(page.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible();
  await page.getByRole('button', { name: 'Confirm publication' }).click();
  await expect(page.getByRole('button', { name: 'Publish reviewed refs' })).toBeVisible();
  await page.getByRole('button', { name: 'Publish reviewed refs' }).click();
}

test.describe('Git-Bug controlled push recovery', () => {
  test('recovers from authentication failure only after explicit credential replacement', async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    await withGitBugAcceptance(page, testInfo, {
      scenario: 'authentication-failure-recovery',
      credential,
      remote: { username: credential, password: 'x-oauth-basic', failureMode: 'auth' }
    }, async ({ page: acceptancePage, remote, diagnostics, setPhase }) => {
      const initialRemote = await remote.snapshot();
      await openIssueAndPrepareCommit(acceptancePage, remote, 'Authentication recovery issue', setPhase);
      setPhase('authentication failure');
      await pushWithExplicitCredential(acceptancePage);
      await expect(acceptancePage.getByRole('alert').first()).toContainText(/credential|permission|rejected/i);
      await assertCredentialCleared(acceptancePage, { durable: false });
      const rejectedRemote = await remote.snapshot();
      expect(rejectedRemote.refs).toEqual(initialRemote.refs);
      await expect(acceptancePage.getByRole('button', { name: 'Review Git-Bug refs' })).toBeVisible();

      setPhase('authentication recovery retry');
      await remote.clearFailureMode();
      await acceptancePage.getByRole('button', { name: 'Review Git-Bug refs' }).click();
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: 'Replace credential' }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm publication' }).click();
      await acceptancePage.getByRole('button', { name: 'Publish reviewed refs' }).click();
      await expect(acceptancePage.getByText('Git-Bug refs published successfully.')).toBeVisible({ timeout: 15_000 });
      expect((await remote.snapshot()).requestCount).toBeGreaterThan(initialRemote.requestCount);
      await assertCredentialCleared(acceptancePage);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
    });
  });

  test('recovers from a network failure only after an explicit review retry', async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    await withGitBugAcceptance(page, testInfo, {
      scenario: 'network-failure-recovery',
      credential,
      remote: { username: credential, password: 'x-oauth-basic', failureMode: 'network' }
    }, async ({ page: acceptancePage, remote, diagnostics, setPhase }) => {
      const initialRemote = await remote.snapshot();
      await openIssueAndPrepareCommit(acceptancePage, remote, 'Network recovery issue', setPhase);
      setPhase('network failure');
      await pushWithExplicitCredential(acceptancePage);
      await expect(acceptancePage.getByRole('alert').first()).toContainText(/network|reached|failed/i);
      await assertCredentialCleared(acceptancePage, { durable: false });
      expect((await remote.snapshot()).refs).toEqual(initialRemote.refs);
      await expect(acceptancePage.getByRole('button', { name: 'Review and retry' })).toBeVisible();

      setPhase('network recovery retry');
      await remote.clearFailureMode();
      await acceptancePage.getByRole('button', { name: 'Review and retry' }).click();
      await expect(acceptancePage.locator('.gitbug-push-review')).toContainText(remote.url);
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm publication' }).click();
      await acceptancePage.getByRole('button', { name: 'Publish reviewed refs' }).click();
      await expect(acceptancePage.getByText('Git-Bug refs published successfully.')).toBeVisible({ timeout: 15_000 });
      expect((await remote.snapshot()).requestCount).toBeGreaterThan(initialRemote.requestCount);
      await assertCredentialCleared(acceptancePage);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.consoleErrors
        .filter(message => !message.includes('status of 401') && !message.includes('ERR_FAILED'))).toEqual([]);
    });
  });
});
