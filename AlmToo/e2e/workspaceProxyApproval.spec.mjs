import { expect, test } from '@playwright/test';

const publicRepositoryUrl = 'https://github.com/octocat/Hello-World.git';

test.describe('workspace route and approved proxy policy', () => {
  test('redirects an unavailable direct workspace request and rejects malformed proxy input', async ({ page }) => {
    await page.goto('/workspace');

    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible();
    await expect(page.getByLabel('Approved connection')).toBeVisible();

    await page.getByLabel('Approved connection').selectOption('isomorphic-git');
    await expect(page.getByRole('button', { name: 'Approve connection' })).toBeVisible();

    await page.getByRole('button', { name: 'Approve connection' }).click();
    await expect(page.getByRole('button', { name: 'Approve connection' })).toHaveCount(0);

    const malformedProxyResult = await page.evaluate(async () => {
      const browserGit = await import('/js/browserGitEngine.js');
      const result = await browserGit.cloneOrOpen({
        repositoryUrl: 'https://github.com/octocat/Hello-World.git',
        proxyId: 'https://proxy.example.invalid'
      });
      return { succeeded: result.succeeded, failureKind: result.failureKind };
    });

    expect(malformedProxyResult).toEqual({
      succeeded: false,
      failureKind: 'proxySelectionInvalid'
    });
  });

  test('opens through an explicitly approved proxy and recovers the workspace after reload', async ({ page }) => {
    await page.goto('/');
    await page.getByLabel('Approved connection').selectOption('isomorphic-git');
    await page.getByRole('button', { name: 'Approve connection' }).click();

    await page.getByLabel('Repository URL').fill(publicRepositoryUrl);
    await page.getByRole('button', { name: 'Open repository' }).click();
    await expect(page.getByRole('button', { name: 'Open workspace' })).toBeVisible({ timeout: 120000 });
    await page.getByRole('button', { name: 'Open workspace' }).click();

    await expect(page).toHaveURL(/\/workspace$/);
    await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible();

    await page.reload();
    await expect(page).toHaveURL(/\/workspace$/);
    await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible();

    await page.goto('/');
    await page.getByLabel('Approved connection').selectOption('isomorphic-git');
    await expect(page.getByRole('button', { name: 'Approve connection' })).toBeVisible();
    await page.getByLabel('Approved connection').selectOption('direct');
    await page.getByLabel('Approved connection').selectOption('isomorphic-git');
    await expect(page.getByRole('button', { name: 'Approve connection' })).toBeVisible();
  });
});
