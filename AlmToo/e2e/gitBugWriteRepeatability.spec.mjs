import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';
import { fixtureSnapshotDigest, snapshotGitBugRepository } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';

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

  setPhase('repeatability local commit');
  await page.getByRole('button', { name: 'Review changed files' }).click();
  await page.getByLabel('Commit message').fill(`Accept repeatability round ${round}`);
  await page.getByLabel('Author name').fill('Browser Repeatability');
  await page.getByLabel('Author email').fill('repeatability@example.invalid');
  await page.getByRole('button', { name: 'Prepare commit details' }).click();
  await page.getByRole('button', { name: 'Confirm and create local commit' }).click();
  await expect(page.getByText(/Local commit created:/)).toBeVisible({ timeout: 30_000 });
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
  test.setTimeout(240_000);
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
      await acceptancePage.getByRole('button', { name: 'Review push target' }).click();
      await expect(acceptancePage.locator('.gitbug-push-review')).toContainText(remote.url);
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm push' }).click();
      await acceptancePage.getByRole('button', { name: 'Push reviewed commit' }).click();
      await expect(acceptancePage.getByRole('alert').first()).toContainText(/credential|permission|rejected/i);
      await expect(acceptancePage.getByRole('button', { name: 'Review push target' })).toBeVisible();
      await assertCredentialAbsent(acceptancePage, credential);
      expect((await remote.snapshot()).refs).toEqual(initialRemote.refs);

      setPhase('repeatability authentication recovery retry');
      await remote.clearFailureMode();
      await acceptancePage.getByRole('button', { name: 'Review push target' }).click();
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: 'Replace credential' }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm push' }).click();
      await acceptancePage.getByRole('button', { name: 'Push reviewed commit' }).click();
      await expect(acceptancePage.getByText(/Pushed commit [0-9a-f]+ and refreshed local status\./).first()).toBeVisible({ timeout: 60_000 });
      await expect(acceptancePage.getByRole('alert')).toHaveCount(0);

      const snapshot = await remote.snapshot();
      expect(snapshot.refs.some(ref => ref.startsWith('heads/'))).toBeTruthy();
      expect(snapshot.requestCount).toBeGreaterThan(initialRemote.requestCount);
      assertNoCredential(diagnostics);
      assertNoCredential(await acceptancePage.evaluate(() => ({
        html: document.documentElement.innerHTML,
        local: JSON.stringify(localStorage),
        session: JSON.stringify(sessionStorage),
        url: location.href
      })));
      await assertCredentialAbsent(acceptancePage, credential);
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.requestFailures).toEqual([]);
      expect(diagnostics.httpErrors.filter(({ status }) => status !== 401)).toEqual([]);
      expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
      finalDigests.push(stableFixtureDigest(await snapshotGitBugRepository(acceptancePage)));
    });
  }

  expect(initialRemoteRefs).toHaveLength(2);
  expect(initialRemoteRefs[0]).toEqual(initialRemoteRefs[1]);
  expect(finalDigests).toHaveLength(2);
});
