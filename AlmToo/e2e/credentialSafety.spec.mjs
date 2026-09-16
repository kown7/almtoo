import { expect, test } from '@playwright/test';

const credentialStorageKey = 'almtoo.push-credential.v1';
const publicRepositoryUrl = 'https://github.com/octocat/Hello-World.git';

function observeBrowserErrors(page) {
  const errors = [];
  page.on('pageerror', () => errors.push('pageerror'));
  page.on('console', message => {
    if (message.type() === 'error') errors.push('console-error');
  });
  return errors;
}

function createEphemeralCredential() {
  return ['ghp', 'credential', 'safety', Date.now().toString(36)].join('_');
}

async function credentialIsStored(page) {
  return page.evaluate(key => sessionStorage.getItem(key) !== null, credentialStorageKey);
}

async function renderedDocumentExcludes(page, credential) {
  return page.evaluate(
    value => !document.documentElement.outerHTML.includes(value),
    credential);
}

async function openPublicRepository(page) {
  await page.getByLabel('Repository URL').fill(publicRepositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });
}

async function installAuthenticationRejection(page) {
  await page.route(/service=git-receive-pack/, route => route.fulfill({
    status: 200,
    contentType: 'application/x-git-receive-pack-advertisement',
    body: '001f# service=git-receive-pack\n0000001eERR authentication failed\n0000'
  }));
}

test('retains credential presence across same-tab reload and Forget removes it', async ({ page, context }) => {
  test.setTimeout(180_000);
  const browserErrors = observeBrowserErrors(page);
  const credential = createEphemeralCredential();

  await page.goto('/');
  await openPublicRepository(page);
  const credentialInput = page.getByLabel('GitHub personal access token');
  await expect(credentialInput).toHaveAttribute('type', 'password');
  await credentialInput.fill(credential);
  await page.getByRole('button', { name: 'Use token in this tab' }).click();

  await expect(page.getByText('Token saved for this tab.')).toBeVisible();
  await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  await expect(credentialInput).toHaveValue('');
  expect(await credentialIsStored(page), 'Credential storage should contain one opaque value').toBe(true);
  expect(await renderedDocumentExcludes(page, credential), 'Rendered markup must exclude the credential value').toBe(true);

  await page.reload();
  await expect(page.locator('[data-credential-state="present"]')).toBeVisible();
  await expect(page.getByLabel('GitHub personal access token')).toHaveCount(0);
  expect(await credentialIsStored(page), 'Same-tab reload should preserve credential presence').toBe(true);
  expect(await renderedDocumentExcludes(page, credential), 'Reloaded markup must exclude the credential value').toBe(true);

  const otherTab = await context.newPage();
  const otherTabErrors = observeBrowserErrors(otherTab);
  await otherTab.goto('/');
  await expect(otherTab.locator('[data-credential-state="present"]')).toHaveCount(0);
  expect(await credentialIsStored(otherTab), 'A separate tab must not inherit credential state').toBe(false);
  expect(otherTabErrors, 'The isolated-tab check must not emit browser errors').toEqual([]);
  await otherTab.close();

  await openPublicRepository(page);
  const reloadedCredentialInput = page.getByLabel('GitHub personal access token');
  await expect(reloadedCredentialInput).toHaveAttribute('type', 'password');
  await expect(reloadedCredentialInput).toHaveValue('');
  await page.getByRole('button', { name: 'Forget token' }).click();
  await expect(page.getByText('Token forgotten for this tab.')).toBeVisible();
  await expect(page.locator('[data-credential-state="present"]')).toHaveCount(0);
  await expect(reloadedCredentialInput).toHaveValue('');
  expect(await credentialIsStored(page), 'Forget must remove tab storage').toBe(false);
  expect(await renderedDocumentExcludes(page, credential), 'Forgotten credential must not appear in rendered markup').toBe(true);
  expect(browserErrors, 'Credential reload and Forget must not emit browser errors').toEqual([]);
});

test('authentication rejection clears the credential and requests a replacement', async ({ page }) => {
  test.setTimeout(180_000);
  const browserErrors = observeBrowserErrors(page);
  const credential = createEphemeralCredential();
  await installAuthenticationRejection(page);

  await page.goto('/');
  await openPublicRepository(page);
  const browser = page.getByRole('region', { name: 'Browse workspace' });
  await browser.getByRole('button', { name: /^README(?:\s|$)/ }).click();
  const editor = page.getByLabel('Plain text editor');
  await expect(editor).toBeVisible();
  await editor.fill(`${await editor.inputValue()}\nAlmToo credential rejection proof ${Date.now()}\n`);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  await page.getByLabel('Commit message').fill(`AlmToo credential rejection proof ${Date.now()}`);
  await page.getByRole('button', { name: 'Create local commit' }).click();
  await expect(page.getByText('Created a browser-local commit.')).toBeVisible();

  const credentialInput = page.getByLabel('GitHub personal access token');
  await credentialInput.fill(credential);
  await page.getByRole('button', { name: 'Use token in this tab' }).click();
  await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  expect(await credentialIsStored(page), 'Credential should be present before the explicit push').toBe(true);

  await page.getByRole('button', { name: 'Review push' }).click();
  const reviewedSha = (await page.locator('[data-push-review="sha"]').textContent())?.trim();
  expect(reviewedSha, 'Push review should expose only a full commit identifier').toMatch(/^[0-9a-f]{40}$/i);
  await page.getByLabel(/I confirm this exact commit/).check();
  await page.getByRole('button', { name: 'Push reviewed commit' }).click();

  await expect(page.locator('[data-credential-state="replacement-required"]')).toHaveText(
    'The remote rejected the credential or repository permission. Enter a replacement token.',
    { timeout: 90_000 });
  await expect(credentialInput).toBeEnabled();
  await expect(credentialInput).toHaveValue('');
  await expect(page.locator('[data-credential-state="present"]')).toHaveCount(0);
  expect(await credentialIsStored(page), 'Credential rejection must remove tab storage').toBe(false);
  expect(await renderedDocumentExcludes(page, credential), 'Rejected credential must not appear in rendered markup').toBe(true);
  expect(browserErrors, 'Credential rejection must not emit browser errors').toEqual([]);
});
