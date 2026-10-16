import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';
import { fixtureSnapshotDigest, snapshotGitBugRepository } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';

test('repeats the deterministic Git-Bug write lifecycle without state leakage', async ({ page }, testInfo) => {
  test.setTimeout(240_000);
  const digests = [];

  for (const round of [1, 2]) {
    await withGitBugAcceptance(page, testInfo, {
      scenario: `write-repeatability-round-${round}`,
      credential,
      remote: { username: credential, password: 'x-oauth-basic' }
    }, async ({ page: acceptancePage, remote, diagnostics, setPhase, assertNoCredential }) => {
      setPhase('repeatability local edit');
      const before = fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage));
      await acceptancePage.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
      await acceptancePage.getByLabel('Title').fill(`Repeatability round ${round}`);
      await acceptancePage.getByRole('textbox', { name: 'Description' }).fill(`Deterministic repeatability round ${round}.`);
      await acceptancePage.getByRole('button', { name: 'Save issue locally' }).click();
      await expect(acceptancePage.getByRole('heading', { name: `Repeatability round ${round}`, level: 2 })).toBeVisible({ timeout: 30_000 });

      setPhase('repeatability local commit');
      await acceptancePage.getByRole('button', { name: 'Review changed files' }).click();
      await acceptancePage.getByLabel('Commit message').fill(`Accept repeatability round ${round}`);
      await acceptancePage.getByLabel('Author name').fill('Browser Repeatability');
      await acceptancePage.getByLabel('Author email').fill('repeatability@example.invalid');
      await acceptancePage.getByRole('button', { name: 'Prepare commit details' }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm and create local commit' }).click();
      await expect(acceptancePage.getByText(/Local commit created:/)).toBeVisible({ timeout: 30_000 });
      expect(fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage))).not.toBe(before);

      setPhase('repeatability remote push');
      await acceptancePage.getByRole('button', { name: 'Review push target' }).click();
      await expect(acceptancePage.locator('.gitbug-push-review')).toContainText(remote.url);
      await acceptancePage.getByLabel('Tab-scoped credential').fill(credential);
      await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
      await acceptancePage.getByRole('button', { name: 'Confirm push' }).click();
      await acceptancePage.getByRole('button', { name: 'Push reviewed commit' }).click();
      await expect(acceptancePage.getByText(/Pushed commit [0-9a-f]+ and refreshed local status\./).first()).toBeVisible({ timeout: 60_000 });

      const snapshot = await remote.snapshot();
      expect(snapshot.refs.some(ref => ref.startsWith('heads/'))).toBeTruthy();
      expect(snapshot.requestCount).toBeGreaterThan(0);
      assertNoCredential(diagnostics);
      assertNoCredential(await acceptancePage.evaluate(() => ({
        html: document.documentElement.innerHTML,
        local: JSON.stringify(localStorage),
        session: JSON.stringify(sessionStorage),
        url: location.href
      })));
      expect(diagnostics.pageErrors).toEqual([]);
      expect(diagnostics.requestFailures).toEqual([]);
      expect(diagnostics.httpErrors.filter(({ status }) => status !== 401)).toEqual([]);
      digests.push(fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage)));
    });
  }

  expect(digests[0]).not.toBe(digests[1]);
});
