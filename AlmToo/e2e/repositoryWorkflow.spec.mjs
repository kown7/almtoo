import { expect, test } from '@playwright/test';

const publicRepositoryUrl = 'https://github.com/octocat/Hello-World.git';
const reviewedRepositoryUrl = publicRepositoryUrl;

function observeBrowserErrors(page) {
  const errors = [];
  page.on('pageerror', () => errors.push('pageerror'));
  page.on('console', message => {
    if (message.type() === 'error') errors.push('console-error');
  });
  return errors;
}

async function expectManagerSettled(page) {
  await expect(page.getByRole('button', { name: 'Cancel current operation' })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
}

test('completes the browser-local repository workflow through push review', async ({ page }) => {
  test.setTimeout(180_000);
  const browserErrors = observeBrowserErrors(page);
  const uniqueSuffix = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const commitMessage = `AlmToo autonomous workflow ${uniqueSuffix}`;

  await page.goto('/');
  await expect(page).toHaveTitle('Repository Browser');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible();
  await expectManagerSettled(page);

  await page.getByLabel('Repository URL').fill(publicRepositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });
  await expect(page.getByRole('button', { name: 'Open repository' })).toBeEnabled();
  await expect(page.getByRole('definition').filter({ hasText: /Cloned in this session|Opened from browser storage/ })).toBeVisible();
  await expectManagerSettled(page);

  const browser = page.getByRole('region', { name: 'Browse workspace' });
  await expect(browser.getByLabel('Current repository path')).toHaveText('/');
  const repositoryFileList = browser.getByRole('list', { name: 'Repository file list' });
  await expect(repositoryFileList).toBeVisible();
  await repositoryFileList.getByRole('button', { name: /^README(?:\s|$)/ }).click();

  const fileEditor = page.getByRole('region', { name: '/README' });
  const editor = fileEditor.getByLabel('Plain text editor');
  await expect(editor).toBeVisible();
  await expect(fileEditor.getByRole('heading', { name: '/README' })).toBeVisible();
  await expect(fileEditor.getByText('The file was read from browser-local storage.')).toBeVisible();
  await expectManagerSettled(page);

  const originalContent = await editor.inputValue();
  await editor.fill(`${originalContent}\nAlmToo browser workflow proof ${uniqueSuffix}\n`);
  await expect(fileEditor.getByText('Unsaved edits', { exact: true })).toBeVisible();
  await fileEditor.getByRole('button', { name: 'Save changes' }).click();
  await expect(fileEditor.getByText('The browser-local files were updated.')).toBeVisible();
  await expect(fileEditor.getByText('No unsaved edits', { exact: true })).toBeVisible();
  await expectManagerSettled(page);

  const changedFiles = page.getByRole('region', { name: 'Changed files' });
  const changedReadme = changedFiles.getByRole('listitem').filter({ hasText: '/README' });
  await expect(changedReadme).toContainText('Modified');
  await expect(changedReadme).toContainText('/README');

  await page.getByLabel('Author name').fill('AlmToo Browser UAT');
  await page.getByLabel('Author email').fill('browser-uat@almtoo.local');
  await page.getByLabel('Commit message').fill(commitMessage);
  await page.getByRole('button', { name: 'Create local commit' }).click();
  await expect(page.getByText('Created a browser-local commit.')).toBeVisible();
  await expectManagerSettled(page);

  const commitConfirmation = page.locator('.commit-form__confirmation');
  const commitId = (await commitConfirmation.locator('div').filter({ hasText: /^Commit[0-9a-f]{40}$/i }).locator('dd').textContent())?.trim();
  expect(commitId, 'The completed commit should expose its full identifier').toMatch(/^[0-9a-f]{40}$/i);
  await expect(commitConfirmation.locator('div').filter({ hasText: `Message${commitMessage}` }).locator('dd')).toHaveText(commitMessage);
  await expect(changedFiles.getByRole('list', { name: 'Changed file list' })).toHaveCount(0);
  await expect(changedFiles.getByText('No changed files detected.')).toBeVisible();

  await page.getByRole('button', { name: 'Review push' }).click();
  await expect(page.getByText('Review the origin, branch, destination ref, and outgoing commit before confirming the push.')).toBeVisible();
  await expectManagerSettled(page);

  const pushReview = page.locator('dl[aria-label="Push destination review"]');
  await expect(pushReview.locator('[data-push-review="origin"]')).toHaveText(reviewedRepositoryUrl);
  const reviewedBranch = (await pushReview.locator('[data-push-review="branch"]').textContent())?.trim();
  expect(reviewedBranch, 'Push review should identify the checked-out branch').toBeTruthy();
  await expect(pushReview.locator('[data-push-review="destination"]')).toHaveText(`refs/heads/${reviewedBranch}`);
  await expect(pushReview.locator('[data-push-review="sha"]')).toHaveText(commitId);
  await expect(page.getByRole('button', { name: 'Push reviewed commit' })).toBeDisabled();
  await expect(page.getByLabel(/I confirm this exact commit/)).not.toBeChecked();

  expect(browserErrors, 'The autonomous repository workflow must not emit browser errors').toEqual([]);
});
