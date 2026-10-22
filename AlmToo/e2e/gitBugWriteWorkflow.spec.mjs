import { expect, test } from '@playwright/test';

import { withGitBugAcceptance } from './gitBugAcceptanceHarness.mjs';
import { fixtureSnapshotDigest, snapshotGitBugRepository } from './gitBugFixture.mjs';

const credential = 'fixture-push-secret';
const editedTitle = 'Unicode café regression fixed 🐛';
const editedDescription = 'Updated description with deterministic UTF-8 text.';
const comment = 'Browser acceptance comment with deterministic UTF-8 text.';

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
  test.setTimeout(180_000);
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
    await expect(acceptancePage.getByRole('button', { name: 'Confirm and create local commit' })).toHaveCount(0);
    await expect(acceptancePage.getByRole('button', { name: 'Push reviewed commit' })).toHaveCount(0);

    await acceptancePage.getByRole('textbox', { name: 'Comment' }).fill(comment);
    await acceptancePage.getByRole('button', { name: 'Add comment locally' }).click();
    await expect(acceptancePage.getByText(comment, { exact: true })).toBeVisible({ timeout: 30_000 });
    await expect(acceptancePage.getByRole('button', { name: 'Confirm and create local commit' })).toHaveCount(0);
    await expect(acceptancePage.getByRole('button', { name: 'Push reviewed commit' })).toHaveCount(0);

    setPhase('review and create local commit');
    await acceptancePage.getByRole('button', { name: 'Review changed files' }).click();
    await expect(acceptancePage.getByRole('heading', { name: 'Review local commit', level: 3 })).toBeVisible();
    await expect(acceptancePage.getByText(/changed file/i).first()).toBeVisible();
    await acceptancePage.getByLabel('Commit message').fill('Accept Git-Bug browser write');
    await acceptancePage.getByLabel('Author name').fill('Browser Acceptance');
    await acceptancePage.getByLabel('Author email').fill('browser@example.invalid');
    await acceptancePage.getByRole('button', { name: 'Prepare commit details' }).click();
    await expect(acceptancePage.getByRole('heading', { name: 'Confirm local commit', level: 4 })).toBeVisible();
    await acceptancePage.getByRole('button', { name: 'Confirm and create local commit' }).click();
    await expect(acceptancePage.getByText(/Local commit created:/)).toBeVisible({ timeout: 30_000 });
    const afterCommit = fixtureSnapshotDigest(await snapshotGitBugRepository(acceptancePage));
    expect(afterCommit).not.toBe(before);

    setPhase('review and push commit');
    await acceptancePage.getByRole('button', { name: 'Review push target' }).click();
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText(repositoryUrl);
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('Branch');
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('Destination');
    await expect(acceptancePage.locator('.gitbug-push-review')).toContainText('Commit');

    const credentialInput = acceptancePage.getByLabel('Tab-scoped credential');
    await credentialInput.fill(credential);
    await acceptancePage.getByRole('button', { name: /(?:Use|Replace) credential/ }).click();
    await expect(credentialInput).toHaveCount(0);
    await expect(acceptancePage.getByRole('heading', { name: 'Confirm remote push', level: 4 })).toBeVisible();
    await acceptancePage.getByRole('button', { name: 'Confirm push' }).click();
    await expect(acceptancePage.getByRole('button', { name: 'Push reviewed commit' })).toBeVisible();
    await acceptancePage.getByRole('button', { name: 'Push reviewed commit' }).click();
    await expect(acceptancePage.getByText(/Pushed commit [0-9a-f]+ and refreshed local status\./).first()).toBeVisible({ timeout: 60_000 });
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
