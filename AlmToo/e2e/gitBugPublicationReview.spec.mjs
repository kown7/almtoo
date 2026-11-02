import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';

const credential = 'fixture-push-secret';
const rawTransportDiagnostic = 'ECONNRESET';

async function prepareReviewedIssue(page) {
  await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
  await page.getByLabel('Title').fill('Reviewed publication UI issue');
  await page.getByRole('textbox', { name: 'Description' }).fill('Deterministic publication review coverage.');
  await page.getByRole('button', { name: 'Save issue locally' }).click();
  await expect(page.getByRole('heading', { name: 'Reviewed publication UI issue', level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'Review changed files' }).click();
  await expect(page.getByRole('heading', { name: 'Changed files and refs', level: 2 })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible({ timeout: 15_000 });
}

async function assertSafePublicationSurface(page, secret) {
  const html = await page.locator('body').innerHTML();
  expect(html).not.toContain(secret);
  expect(html).not.toContain(rawTransportDiagnostic);
  expect(html).not.toContain('Authorization');
  expect(html).not.toContain('server payload');
  await expect(page.locator('[aria-label="Reviewed Git-Bug refs"]')).toBeVisible();
  await expect(page.getByText('Required issue refs', { exact: true })).toBeVisible();
  await expect(page.getByText('Required identity refs', { exact: true })).toBeVisible();
  await expect(page.getByText('Remote preconditions', { exact: true })).toBeVisible();
}

test.describe('Git-Bug reviewed publication Client', () => {
  test('inspects exact refs, gates publication on explicit confirmation, and redacts failure surfaces', async ({ page }, testInfo) => {
    test.setTimeout(60_000);
    await withGitBugAcceptance(page, testInfo, {
      scenario: 'reviewed-publication-ui',
      credential,
      remote: { username: 'x-access-token', password: credential, failureMode: 'auth' }
    }, async ({ page: acceptancePage, remote, diagnostics }) => {
      await prepareReviewedIssue(acceptancePage);
      await assertSafePublicationSurface(acceptancePage, credential);

      const beforeCredential = await remote.snapshot();
      await expect(acceptancePage.getByLabel('Tab-scoped credential')).toBeVisible();
      await expect(acceptancePage.getByRole('button', { name: 'Confirm publication' })).toHaveCount(0);
      await expect(acceptancePage.getByRole('button', { name: 'Publish reviewed refs' })).toHaveCount(0);
      expect((await remote.snapshot()).refs).toEqual(beforeCredential.refs);

      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: 'Use credential' }).click();
      await expect(acceptancePage.getByRole('button', { name: 'Confirm publication' })).toBeVisible();
      await expect(acceptancePage.getByRole('button', { name: 'Publish reviewed refs' })).toHaveCount(0);

      await acceptancePage.getByRole('button', { name: 'Confirm publication' }).click();
      await expect(acceptancePage.getByRole('button', { name: 'Publish reviewed refs' })).toBeVisible();
      await acceptancePage.getByRole('button', { name: 'Publish reviewed refs' }).click();
      await expect(acceptancePage.getByRole('alert')).toContainText(/credential|permission|rejected|unavailable|retry/i);
      await expect(acceptancePage.getByRole('button', { name: 'Review and retry' })).toBeVisible();
      await assertSafePublicationSurface(acceptancePage, credential);
      expect((await remote.snapshot()).refs).toEqual(beforeCredential.refs);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
    });
  });
});
