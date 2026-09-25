import { expect, test } from '@playwright/test';

const credentialStorageKey = 'almtoo.push-credential.v1';
const testCredentialStorageKey = 'almtoo.test-credential.v1';
const publicRepositoryUrl = 'https://github.com/octocat/Hello-World.git';

async function observeBrowserDiagnostics(page) {
  const errors = [];
  page.on('pageerror', () => errors.push('pageerror'));
  page.on('console', message => {
    if (message.type() === 'error') errors.push('console-error');
  });
  await page.addInitScript(({ testCredentialStorageKey }) => {
    globalThis.__almTooCredentialDiagnosticLeak = false;
    const containsCredential = value => {
      const credential = sessionStorage.getItem(testCredentialStorageKey);
      return Boolean(credential && String(value).includes(credential));
    };
    const originalConsoleError = console.error.bind(console);
    console.error = (...values) => {
      globalThis.__almTooCredentialDiagnosticLeak ||= values.some(containsCredential);
      originalConsoleError(...values);
    };
    addEventListener('error', event => {
      globalThis.__almTooCredentialDiagnosticLeak ||= containsCredential(event.message);
    });
    addEventListener('unhandledrejection', event => {
      globalThis.__almTooCredentialDiagnosticLeak ||= containsCredential(event.reason);
    });
  }, { testCredentialStorageKey });
  return errors;
}

async function enterEphemeralCredential(page, input) {
  await input.evaluate((element, { testCredentialStorageKey }) => {
    const credential = ['ghp', 'credential', 'safety', Date.now().toString(36), crypto.randomUUID()].join('_');
    sessionStorage.setItem(testCredentialStorageKey, credential);
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    setValue.call(element, credential);
    element.dispatchEvent(new Event('input', { bubbles: true }));
  }, { testCredentialStorageKey });
}

async function credentialIsStored(page) {
  return page.evaluate(key => sessionStorage.getItem(key) !== null, credentialStorageKey);
}

async function credentialIsAbsentFromBrowserSurfaces(page) {
  return page.evaluate(({ testCredentialStorageKey }) => {
    const credential = sessionStorage.getItem(testCredentialStorageKey);
    if (!credential) return false;
    const resourceUrls = performance.getEntriesByType('resource').map(entry => entry.name);
    return !document.documentElement.outerHTML.includes(credential)
      && !location.href.includes(credential)
      && !resourceUrls.some(url => url.includes(credential))
      && !globalThis.__almTooCredentialDiagnosticLeak;
  }, { testCredentialStorageKey });
}

async function openPublicRepository(page) {
  await page.getByLabel('Repository URL').fill(publicRepositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });
  await expect(page.getByRole('button', { name: 'Open repository' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Cancel current operation' })).toHaveCount(0);
}

async function installAuthenticationRejection(page) {
  await page.route(/service=git-receive-pack/, route => route.fulfill({
    status: 200,
    contentType: 'application/x-git-receive-pack-advertisement',
    body: '001e# service=git-receive-pack\n0000001eERR authentication failed\n0000'
  }));
}

test('retains credential presence across same-tab reload and Forget removes it', async ({ page, context }) => {
  test.setTimeout(180_000);
  const browserErrors = await observeBrowserDiagnostics(page);

  await page.goto('/');
  await openPublicRepository(page);
  const credentialInput = page.getByLabel('GitHub personal access token');
  await expect(credentialInput).toHaveAttribute('type', 'password');
  await enterEphemeralCredential(page, credentialInput);
  await expect(page.getByRole('button', { name: 'Use token in this tab' })).toBeEnabled();
  await page.getByRole('button', { name: 'Use token in this tab' }).click();

  await expect(page.getByText('Token saved for this tab.')).toBeVisible();
  await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  await expect(credentialInput).toHaveValue('');
  expect(await credentialIsStored(page), 'Credential storage should contain one opaque value').toBe(true);
  expect(await credentialIsAbsentFromBrowserSurfaces(page), 'Credential must stay out of DOM, URLs, console, and page errors').toBe(true);

  await page.reload();
  const credentialStatus = page.getByRole('region', { name: 'Push credential status' });
  await expect(credentialStatus.locator('p[data-credential-state="present"]:not([role])')).toHaveText(
    'A token is available for an explicit push in this tab.');
  await expect(page.getByLabel('GitHub personal access token')).toHaveCount(0);
  expect(await credentialIsStored(page), 'Same-tab reload should preserve credential presence').toBe(true);
  expect(await credentialIsAbsentFromBrowserSurfaces(page), 'Reloaded browser surfaces must exclude the credential value').toBe(true);

  const otherTab = await context.newPage();
  const otherTabErrors = await observeBrowserDiagnostics(otherTab);
  await otherTab.goto('/');
  await expect(otherTab.getByRole('region', { name: 'Push credential status' })).toHaveCount(0);
  expect(await credentialIsStored(otherTab), 'A separate tab must not inherit credential state').toBe(false);
  expect(otherTabErrors, 'The isolated-tab check must not emit browser errors').toEqual([]);
  await otherTab.close();

  await credentialStatus.getByRole('button', { name: 'Forget token' }).click();
  await expect(credentialStatus).toHaveCount(0);
  expect(await credentialIsStored(page), 'Forget must remove tab storage').toBe(false);
  expect(await credentialIsAbsentFromBrowserSurfaces(page), 'Forgotten credential must stay out of browser surfaces').toBe(true);

  await openPublicRepository(page);
  const reloadedCredentialInput = page.getByLabel('GitHub personal access token');
  await expect(reloadedCredentialInput).toHaveAttribute('type', 'password');
  await expect(reloadedCredentialInput).toHaveValue('');
  expect(browserErrors, 'Credential reload and Forget must not emit browser errors').toEqual([]);
});

test('authentication rejection clears the credential and requests a replacement', async ({ page }) => {
  test.setTimeout(180_000);
  const browserErrors = await observeBrowserDiagnostics(page);

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
  await enterEphemeralCredential(page, credentialInput);
  await expect(page.getByRole('button', { name: 'Use token in this tab' })).toBeEnabled();
  await page.getByRole('button', { name: 'Use token in this tab' }).click();
  await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  expect(await credentialIsStored(page), 'Credential should be present before the explicit push').toBe(true);

  await page.getByRole('button', { name: 'Review push' }).click();
  const reviewedSha = (await page.locator('[data-push-review="sha"]').textContent())?.trim();
  expect(reviewedSha, 'Push review should expose only a full commit identifier').toMatch(/^[0-9a-f]{40}$/i);
  await installAuthenticationRejection(page);
  await page.getByLabel(/I confirm this exact commit/).check();
  await page.getByRole('button', { name: 'Push reviewed commit' }).click();

  await expect(page.locator('[data-push-failure="credential-rejected"]')).toHaveText(
    'The reviewed commit could not be pushed.',
    { timeout: 90_000 });
  await expect(credentialInput).toBeEnabled();
  await expect(credentialInput).toHaveValue('');
  await expect(page.locator('[data-credential-state="present"]')).toHaveCount(0);
  expect(await credentialIsStored(page), 'Credential rejection must remove tab storage').toBe(false);
  expect(await credentialIsAbsentFromBrowserSurfaces(page), 'Rejected credential must stay out of DOM, URLs, console, and page errors').toBe(true);
  expect(browserErrors, 'Credential rejection must not emit browser errors').toEqual([]);
});
