import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';
import { fixtureSnapshotDigest, snapshotGitBugRepository } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';
const existingType3Comments = [
  'Confirmed on the browser fixture.\nSecond line with Ελληνικά.'
];

async function assertIssueContentExactlyOnce(page, description) {
  const descriptionSurface = page.locator('section.gitbug-detail__description');
  await expect(descriptionSurface.getByText(description, { exact: true })).toHaveCount(1);

  const commentsSurface = page.locator('section.gitbug-comments');
  for (const body of existingType3Comments) {
    await expect(commentsSurface.getByText(body, { exact: true })).toHaveCount(1);
  }
}

function stableFixtureDigest(snapshot) {
  const transportIndependentSnapshot = Object.fromEntries(
    Object.entries(snapshot).filter(([file]) => (
      file !== '.git/config'
      && file !== '.git/FETCH_HEAD'
      && !file.startsWith('.git/logs/')
    ))
  );
  return fixtureSnapshotDigest(transportIndependentSnapshot);
}

async function prepareRepeatabilityCommit(page, round, setPhase) {
  setPhase('repeatability local edit');
  await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
  await page.getByLabel('Title').fill(`Repeatability round ${round}`);
  await page.getByRole('textbox', { name: 'Description' }).fill(`Deterministic repeatability round ${round}.`);
  await page.getByRole('button', { name: 'Save issue locally' }).click();
  await expect(page.getByRole('heading', { name: `Repeatability round ${round}`, level: 2 })).toBeVisible({ timeout: 30_000 });
  await assertIssueContentExactlyOnce(page, `Deterministic repeatability round ${round}.`);

  setPhase('review Git-Bug refs');
  await page.getByRole('button', { name: 'Review changed files' }).click();
  await expect(page.getByRole('heading', { name: 'Changed files and refs', level: 2 })).toBeVisible({ timeout: 15_000 });
  const changedRefList = page.locator('[aria-label="Changed ref list"]');
  await expect(changedRefList).toContainText('refs/bugs/');
  await expect(changedRefList).toContainText('refs/identities/');
  await expect(page.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible({ timeout: 15_000 });
  await assertIssueContentExactlyOnce(page, `Deterministic repeatability round ${round}.`);
}

async function assertCredentialAbsent(page, credential) {
  const browserState = await page.evaluate(() => ({
    html: document.documentElement.innerHTML,
    local: JSON.stringify(localStorage),
    session: JSON.stringify(sessionStorage),
    url: location.href
  }));
  expect(JSON.stringify(browserState)).not.toContain(credential);
}

test('repeats deterministic Git-Bug write and recovery without state leakage', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  const initialRemoteRefs = [];
  const finalDigests = [];

  for (const round of [1, 2]) {
    await withGitBugAcceptance(page, testInfo, {
      scenario: `write-recovery-repeatability-round-${round}`,
      credential,
      remote: { username: credential, password: 'x-oauth-basic', failureMode: 'auth' }
    }, async ({ page: acceptancePage, remote, diagnostics, setPhase, assertNoCredential }) => {
      await expect(acceptancePage.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' })).toBeVisible();
      await expect(acceptancePage.getByRole('heading', { name: /Repeatability round \d+/, level: 2 })).toHaveCount(0);
      const before = stableFixtureDigest(await snapshotGitBugRepository(acceptancePage));
      const initialRemote = await remote.snapshot();
      initialRemoteRefs.push(initialRemote.refs);

      await prepareRepeatabilityCommit(acceptancePage, round, setPhase);
      expect(stableFixtureDigest(await snapshotGitBugRepository(acceptancePage))).not.toBe(before);

      setPhase('repeatability authentication failure');
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm publication' }).click();
      await acceptancePage.getByRole('button', { name: 'Publish reviewed refs' }).click();
      await expect(acceptancePage.getByRole('alert').first()).toContainText(/credential|permission|rejected/i);
      await assertCredentialAbsent(acceptancePage, credential);
      expect((await remote.snapshot()).refs).toEqual(initialRemote.refs);

      setPhase('repeatability recovery retry');
      await remote.clearFailureMode();
      await acceptancePage.getByRole('button', { name: 'Review and retry' }).click();
      await expect(acceptancePage.locator('.gitbug-push-review')).toContainText(remote.url);
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm publication' }).click();
      await acceptancePage.getByRole('button', { name: 'Publish reviewed refs' }).click();
      await expect(acceptancePage.getByText('Git-Bug refs published successfully.')).toBeVisible({ timeout: 15_000 });
      expect((await remote.snapshot()).requestCount).toBeGreaterThan(initialRemote.requestCount);
      assertNoCredential(await acceptancePage.evaluate(() => ({
        localStorage: Object.fromEntries(Object.entries(localStorage)),
        sessionStorage: Object.fromEntries(Object.entries(sessionStorage)),
        body: document.body.innerText
      })));
      assertNoCredential(diagnostics);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
      finalDigests.push(stableFixtureDigest(await snapshotGitBugRepository(acceptancePage)));
    });
  }

  expect(initialRemoteRefs).toHaveLength(2);
  expect(initialRemoteRefs[0]).toEqual(initialRemoteRefs[1]);
  expect(finalDigests).toHaveLength(2);
});
