import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';
import { fixtureSnapshotDigest, snapshotGitBugRepository } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';
const editedTitle = 'Unicode café regression fixed 🐛';
const editedDescription = 'Updated description with deterministic UTF-8 text.';
const comment = 'Browser acceptance comment with deterministic UTF-8 text.';
const existingType3Comments = [
  'Confirmed on the browser fixture.\nSecond line with Ελληνικά.'
];

async function assertIssueContentExactlyOnce(page, description, comments) {
  const descriptionSurface = page.locator('section.gitbug-detail__description');
  await expect(descriptionSurface.getByText(description, { exact: true })).toHaveCount(1);

  const commentsSurface = page.locator('section.gitbug-comments');
  for (const body of comments) {
    await expect(commentsSurface.getByText(body, { exact: true })).toHaveCount(1);
  }
}

test('edits an existing Git-Bug issue with an empty description', async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await withGitBugAcceptance(page, testInfo, {
    scenario: 'empty-description-edit',
    credential,
    remote: { username: credential, password: 'x-oauth-basic' }
  }, async ({ page: acceptancePage, diagnostics, setPhase }) => {
    setPhase('edit empty description');
    await acceptancePage.getByRole('button', { name: 'Open issue: Minimal issue without optional metadata' }).click();
    await expect(acceptancePage.getByRole('heading', { name: 'Minimal issue without optional metadata', level: 2 })).toBeVisible();
    await acceptancePage.getByRole('textbox', { name: 'Description' }).fill('');
    await acceptancePage.getByLabel('Title').fill('Minimal issue edited safely');
    await acceptancePage.getByRole('button', { name: 'Save issue locally' }).click();

    await expect(acceptancePage.getByRole('heading', { name: 'Minimal issue edited safely', level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(acceptancePage.getByRole('textbox', { name: 'Description' })).toHaveValue('', { timeout: 30_000 });
    expect(diagnostics.pageErrors).toEqual([]);
    expect(diagnostics.requestFailures).toEqual([]);
    expect(diagnostics.httpErrors).toEqual([]);
  });
});

test('edits, reviews, commits, and pushes a Git-Bug issue through the browser', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  await withGitBugAcceptance(page, testInfo, {
    scenario: 'successful-git-bug-write',
    credential,
    remote: { username: credential, password: 'x-oauth-basic' }
  }, async ({ page: acceptancePage, remote, diagnostics, setPhase, assertNoCredential }) => {
    const repositoryUrl = remote.url;
    setPhase('capture initial fixture');
    const before = fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage));

    setPhase('edit issue');
    await acceptancePage.getByRole('button', { name: 'Open issue: Unicode café regression 🐛' }).click();
    await expect(acceptancePage.getByRole('heading', { name: 'Unicode café regression 🐛', level: 2 })).toBeVisible();
    await acceptancePage.getByLabel('Title').fill(editedTitle);
    await acceptancePage.getByRole('textbox', { name: 'Description' }).fill(editedDescription);
    await acceptancePage.getByRole('button', { name: 'Save issue locally' }).click();
    await expect(acceptancePage.getByRole('heading', { name: editedTitle, level: 2 })).toBeVisible({ timeout: 30_000 });
    await expect(acceptancePage.getByRole('textbox', { name: 'Description' })).toHaveValue(editedDescription, { timeout: 30_000 });
    await assertIssueContentExactlyOnce(acceptancePage, editedDescription, existingType3Comments);
    await expect(acceptancePage.getByRole('button', { name: 'Confirm and create local commit' })).toHaveCount(0);
    await expect(acceptancePage.getByRole('button', { name: 'Publish reviewed refs' })).toHaveCount(0);

    await acceptancePage.getByRole('textbox', { name: 'Comment' }).fill(comment);
    await acceptancePage.getByRole('button', { name: 'Add comment locally' }).click();
    await expect(acceptancePage.getByText(comment, { exact: true })).toBeVisible({ timeout: 30_000 });
    await assertIssueContentExactlyOnce(acceptancePage, editedDescription, [...existingType3Comments, comment]);
    await expect(acceptancePage.getByRole('button', { name: 'Confirm and create local commit' })).toHaveCount(0);
    await expect(acceptancePage.getByRole('button', { name: 'Publish reviewed refs' })).toHaveCount(0);

    setPhase('review Git-Bug refs');
    const reviewChangedFiles = acceptancePage.getByRole('button', { name: 'Review changed files' });
    await expect(reviewChangedFiles).toBeEnabled({ timeout: 15_000 });
    await reviewChangedFiles.click({ timeout: 15_000 });
    await expect(acceptancePage.getByRole('heading', { name: 'Changed files and refs', level: 2 })).toBeVisible({ timeout: 15_000 });
    const changedRefList = acceptancePage.locator('[aria-label="Changed ref list"]');
    await expect(changedRefList).toContainText('refs/bugs/');
    await expect(changedRefList).toContainText('refs/identities/');
    await expect(acceptancePage.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible({ timeout: 15_000 });
    await assertIssueContentExactlyOnce(acceptancePage, editedDescription, [...existingType3Comments, comment]);
    const afterLocalWrite = fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage));
    expect(afterLocalWrite).not.toBe(before);

    setPhase('publish reviewed Git-Bug refs');
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText(repositoryUrl);
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('Branch');
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('Reviewed refs');
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('refs/bugs/');
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('refs/identities/');

    const credentialInput = acceptancePage.getByLabel('Tab-scoped credential');
    await credentialInput.fill(credential);
    await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
    await expect(credentialInput).toHaveCount(0);
    await expect(acceptancePage.getByRole('heading', { name: 'Confirm Git-Bug publication', level: 4 })).toBeVisible();
    await acceptancePage.getByRole('button', { name: 'Confirm publication' }).click();
    await expect(acceptancePage.getByRole('button', { name: 'Publish reviewed refs' })).toBeVisible();
    await acceptancePage.getByRole('button', { name: 'Publish reviewed refs' }).click();
    await expect(acceptancePage.getByRole('alert')).toHaveCount(0, { timeout: 15_000 });
    await expect(acceptancePage.getByText('Git-Bug refs published successfully.')).toBeVisible({ timeout: 15_000 });
    await expect(acceptancePage.getByRole('alert')).toHaveCount(0);

    const snapshot = await remote.snapshot();
    expect(snapshot.refs.some(ref => ref.startsWith('heads/'))).toBeTruthy();
    expect(snapshot.requestCount).toBeGreaterThan(0);
    expect(fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage))).not.toBe(before);

    const storage = await acceptancePage.evaluate(() => ({
      localStorage: Object.fromEntries(Object.entries(localStorage)),
      sessionStorage: Object.fromEntries(Object.entries(sessionStorage)),
      body: document.body.innerText
    }));
    assertNoCredential(storage);
    assertNoCredential(diagnostics);
    assertNoCredential(await acceptancePage.evaluate(() => location.href));
    expect(diagnostics.consoleErrors.filter(message => !message.includes('status of 401'))).toEqual([]);
    expect(diagnostics.pageErrors).toEqual([]);
    expect(diagnostics.requestFailures).toEqual([]);
    expect(diagnostics.httpErrors.filter(({ status }) => status !== 401)).toEqual([]);
  });
});
