import { expect, test } from '@playwright/test';
import fs from 'node:fs';

import git from 'isomorphic-git';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';
import { snapshotGitBugRepository } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';
const rawTransportDiagnostic = 'ECONNRESET';
const rawTransportCommand = 'git-receive-pack';

async function seedUnrelatedRef(repository) {
  const branches = (await git.listRefs({ fs, gitdir: repository, filepath: 'refs/heads' })).sort();
  const sourceBranch = branches[0];
  if (!sourceBranch) throw new Error('Controlled fixture has no branch to seed an unrelated ref.');

  const sourceOid = await git.resolveRef({ fs, gitdir: repository, ref: `refs/heads/${sourceBranch}` });
  const ref = 'refs/heads/unrelated-publication-ref';
  await git.writeRef({ fs, gitdir: repository, ref, value: sourceOid, force: true });
  return { ref: ref.slice('refs/'.length), oid: sourceOid };
}

async function prepareReviewedIssue(page, title, description) {
  await page.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
  await page.getByLabel('Title').fill(title);
  await page.getByRole('textbox', { name: 'Description' }).fill(description);
  await page.getByRole('button', { name: 'Save issue locally' }).click();
  await expect(page.getByRole('heading', { name: title, level: 2 })).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'Review changed files' }).click();
  await expect(page.getByRole('heading', { name: 'Changed files and refs', level: 2 })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible({ timeout: 15_000 });

  const review = page.locator('[aria-label="Reviewed Git-Bug refs"]');
  await expect(review).toContainText('refs/bugs/');
  await expect(review).toContainText('refs/identities/');
  await expect(page.getByLabel('Tab-scoped credential')).toBeVisible();

  const entries = await review.locator('li').evaluateAll(items => items.map(item => ({
    ref: item.querySelector(':scope > div > code')?.textContent?.trim() ?? '',
    localObject: item.querySelector(':scope > small code')?.textContent?.trim() ?? '',
    remotePrecondition: item.querySelectorAll(':scope > small code')[1]?.textContent?.trim() ?? ''
  })));
  expect(entries.length).toBeGreaterThan(0);
  expect(entries.every(entry => entry.ref.startsWith('refs/'))).toBeTruthy();
  return entries;
}

function assertRemoteOnlyContainsReviewedUpdates(before, after, localAfter, reviewedEntries) {
  const reviewedRefs = new Set(reviewedEntries.map(entry => entry.ref.replace(/^refs\//u, '')));
  expect(after.refs).toEqual(before.refs);
  for (const ref of before.refs) {
    if (reviewedRefs.has(ref)) {
      expect(after.refOids[ref], `reviewed remote ref ${ref}`).toBe(localAfter.refOids[ref]);
    } else {
      expect(after.refOids[ref], `unreviewed remote ref ${ref}`).toBe(before.refOids[ref]);
    }
  }
  for (const entry of reviewedEntries) {
    const ref = entry.ref.replace(/^refs\//u, '');
    expect(localAfter.refOids[ref], `local reviewed object ${entry.ref}`).toBe(entry.localObject);
    expect(after.refOids[ref], `published reviewed object ${entry.ref}`).toBe(entry.localObject);
    if (entry.remotePrecondition !== 'absent') {
      expect(before.refOids[ref], `review precondition ${entry.ref}`).toBe(entry.remotePrecondition);
    }
  }
}

async function assertNoSecrets(page, diagnostics) {
  const browserState = await page.evaluate(() => ({
    body: document.body.innerText,
    url: location.href,
    html: document.documentElement.innerHTML,
    local: JSON.stringify(localStorage)
  }));
  const serialized = JSON.stringify({ browserState, diagnostics });
  expect(serialized).not.toContain(credential);
  expect(serialized).not.toContain(rawTransportDiagnostic);
  expect(serialized).not.toContain(rawTransportCommand);
  expect(serialized).not.toMatch(/Authorization:\s*Basic/i);
  expect(diagnostics.pageErrors).toEqual([]);
  expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
}

async function publishReviewedRefs(page) {
  await page.getByLabel('Tab-scoped credential').fill(credential);
  await page.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
  await expect(page.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible();
  await page.getByRole('button', { name: 'Confirm publication' }).click();
  await expect(page.getByRole('button', { name: 'Publish reviewed refs' })).toBeVisible();
  await page.getByRole('button', { name: 'Publish reviewed refs' }).click();
  await expect(page.getByText('Git-Bug refs published successfully.')).toBeVisible({ timeout: 15_000 });
}

test.describe('Git-Bug controlled remote publication', () => {
  test('publishes exactly the reviewed issue and identity refs', async ({ page }, testInfo) => {
    test.setTimeout(90_000);
    await withGitBugAcceptance(page, testInfo, {
      scenario: 'controlled-remote-exact-publication',
      credential,
      remote: {
        username: 'x-access-token',
        password: credential,
        seedRepository: seedUnrelatedRef
      }
    }, async ({ page: acceptancePage, remote, diagnostics, setPhase }) => {
      setPhase('capture pre-review snapshots');
      const beforeRemote = await remote.snapshot();
      const beforeLocal = await snapshotGitBugRepository(acceptancePage);
      const unrelatedRef = 'heads/unrelated-publication-ref';
      expect(beforeRemote.refs).toContain(unrelatedRef);

      setPhase('inspect exact reviewed refs');
      const reviewedEntries = await prepareReviewedIssue(
        acceptancePage,
        'Controlled remote publication issue',
        'Exact reviewed ref publication coverage.'
      );
      const reviewedIssueRefs = reviewedEntries.filter(entry => entry.ref.startsWith('refs/bugs/'));
      const reviewedIdentityRefs = reviewedEntries.filter(entry => entry.ref.startsWith('refs/identities/'));
      expect(reviewedIssueRefs.length).toBeGreaterThan(0);
      expect(reviewedIdentityRefs.length).toBeGreaterThan(0);
      expect(reviewedEntries.map(entry => entry.ref)).toEqual([...new Set(reviewedEntries.map(entry => entry.ref))]);

      const afterLocalReview = await snapshotGitBugRepository(acceptancePage);
      expect(afterLocalReview.refs).toEqual(beforeLocal.refs);
      for (const entry of reviewedEntries) {
        const localRef = entry.ref.replace(/^refs\//u, '');
        expect(afterLocalReview.refOids[localRef]).toBe(entry.localObject);
      }
      const reviewCounts = Object.fromEntries(await acceptancePage.locator('.gitbug-publication-review dl > div').evaluateAll(rows => rows.map(row => [
        row.querySelector('dt')?.textContent?.trim() ?? '',
        row.querySelector('dd')?.textContent?.trim() ?? ''
      ])));
      expect(Number(reviewCounts['Reviewed refs'])).toBeGreaterThanOrEqual(reviewedEntries.length);
      expect(Number(reviewCounts['Required issue refs'])).toBeGreaterThanOrEqual(reviewedIssueRefs.length);
      expect(Number(reviewCounts['Required identity refs'])).toBeGreaterThanOrEqual(reviewedIdentityRefs.length);

      setPhase('publish exact reviewed refs');
      await publishReviewedRefs(acceptancePage);
      const afterRemote = await remote.snapshot();
      assertRemoteOnlyContainsReviewedUpdates(beforeRemote, afterRemote, afterLocalReview, reviewedEntries);
      expect(afterRemote.refOids[unrelatedRef]).toBe(beforeRemote.refOids[unrelatedRef]);
      expect(afterRemote.requestCount).toBeGreaterThan(beforeRemote.requestCount);

      await assertNoSecrets(acceptancePage, diagnostics);
      expect(diagnostics.requestFailures).toEqual([]);
      expect(diagnostics.httpErrors.filter(({ status }) => status !== 401)).toEqual([]);
    });
  });

  test('cancelling after review leaves the controlled remote unchanged', async ({ page }, testInfo) => {
    test.setTimeout(90_000);
    await withGitBugAcceptance(page, testInfo, {
      scenario: 'controlled-remote-publication-cancellation',
      credential,
      remote: {
        username: 'x-access-token',
        password: credential,
        seedRepository: seedUnrelatedRef
      }
    }, async ({ page: acceptancePage, remote, diagnostics, setPhase }) => {
      setPhase('capture cancellation baseline');
      const beforeRemote = await remote.snapshot();
      const beforeRequestCount = beforeRemote.requestCount;
      expect(beforeRemote.refs).toContain('heads/unrelated-publication-ref');

      setPhase('review then cancel publication');
      await prepareReviewedIssue(
        acceptancePage,
        'Cancelled controlled publication issue',
        'This reviewed publication is intentionally cancelled.'
      );
      const reviewedRequestCount = remote.requestCount;
      await acceptancePage.getByRole('button', { name: 'Back to issues' }).click();
      await expect(acceptancePage.getByRole('heading', { name: 'Git-Bug issues', level: 1 })).toBeVisible({ timeout: 15_000 });

      const afterCancel = await remote.snapshot();
      expect(afterCancel.refs).toEqual(beforeRemote.refs);
      expect(afterCancel.refOids).toEqual(beforeRemote.refOids);
      expect(afterCancel.snapshotDigest).toBe(beforeRemote.snapshotDigest);
      expect(afterCancel.requestCount).toBe(reviewedRequestCount);
      expect(afterCancel.requestCount).toBeGreaterThanOrEqual(beforeRequestCount);

      setPhase('assert cancellation diagnostics');
      await assertNoSecrets(acceptancePage, diagnostics);
      expect(diagnostics.requestFailures).toEqual([]);
      expect(diagnostics.httpErrors.filter(({ status }) => status !== 401)).toEqual([]);
    });
  });
});
