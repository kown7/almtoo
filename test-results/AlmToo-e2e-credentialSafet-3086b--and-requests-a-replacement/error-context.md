# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: AlmToo/e2e/credentialSafety.spec.mjs >> authentication rejection clears the credential and requests a replacement
- Location: AlmToo/e2e/credentialSafety.spec.mjs:88:1

# Error details

```
Error: page.goto: Protocol error (Page.navigate): Cannot navigate to invalid URL
Call log:
  - navigating to "/", waiting until "load"

```

# Test source

```ts
  1   | import { expect, test } from '@playwright/test';
  2   | 
  3   | const credentialStorageKey = 'almtoo.push-credential.v1';
  4   | const publicRepositoryUrl = 'https://github.com/octocat/Hello-World.git';
  5   | 
  6   | function observeBrowserErrors(page) {
  7   |   const errors = [];
  8   |   page.on('pageerror', () => errors.push('pageerror'));
  9   |   page.on('console', message => {
  10  |     if (message.type() === 'error') errors.push('console-error');
  11  |   });
  12  |   return errors;
  13  | }
  14  | 
  15  | function createEphemeralCredential() {
  16  |   return ['ghp', 'credential', 'safety', Date.now().toString(36)].join('_');
  17  | }
  18  | 
  19  | async function credentialIsStored(page) {
  20  |   return page.evaluate(key => sessionStorage.getItem(key) !== null, credentialStorageKey);
  21  | }
  22  | 
  23  | async function renderedDocumentExcludes(page, credential) {
  24  |   return page.evaluate(
  25  |     value => !document.documentElement.outerHTML.includes(value),
  26  |     credential);
  27  | }
  28  | 
  29  | async function openPublicRepository(page) {
  30  |   await page.getByLabel('Repository URL').fill(publicRepositoryUrl);
  31  |   await page.getByRole('button', { name: 'Open repository' }).click();
  32  |   await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });
  33  | }
  34  | 
  35  | async function installAuthenticationRejection(page) {
  36  |   await page.route(/service=git-receive-pack/, route => route.fulfill({
  37  |     status: 200,
  38  |     contentType: 'application/x-git-receive-pack-advertisement',
  39  |     body: '001f# service=git-receive-pack\n0000001eERR authentication failed\n0000'
  40  |   }));
  41  | }
  42  | 
  43  | test('retains credential presence across same-tab reload and Forget removes it', async ({ page, context }) => {
  44  |   test.setTimeout(180_000);
  45  |   const browserErrors = observeBrowserErrors(page);
  46  |   const credential = createEphemeralCredential();
  47  | 
  48  |   await page.goto('/');
  49  |   await openPublicRepository(page);
  50  |   const credentialInput = page.getByLabel('GitHub personal access token');
  51  |   await expect(credentialInput).toHaveAttribute('type', 'password');
  52  |   await credentialInput.fill(credential);
  53  |   await page.getByRole('button', { name: 'Use token in this tab' }).click();
  54  | 
  55  |   await expect(page.getByText('Token saved for this tab.')).toBeVisible();
  56  |   await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  57  |   await expect(credentialInput).toHaveValue('');
  58  |   expect(await credentialIsStored(page), 'Credential storage should contain one opaque value').toBe(true);
  59  |   expect(await renderedDocumentExcludes(page, credential), 'Rendered markup must exclude the credential value').toBe(true);
  60  | 
  61  |   await page.reload();
  62  |   await expect(page.locator('[data-credential-state="present"]')).toBeVisible();
  63  |   await expect(page.getByLabel('GitHub personal access token')).toHaveCount(0);
  64  |   expect(await credentialIsStored(page), 'Same-tab reload should preserve credential presence').toBe(true);
  65  |   expect(await renderedDocumentExcludes(page, credential), 'Reloaded markup must exclude the credential value').toBe(true);
  66  | 
  67  |   const otherTab = await context.newPage();
  68  |   const otherTabErrors = observeBrowserErrors(otherTab);
  69  |   await otherTab.goto('/');
  70  |   await expect(otherTab.locator('[data-credential-state="present"]')).toHaveCount(0);
  71  |   expect(await credentialIsStored(otherTab), 'A separate tab must not inherit credential state').toBe(false);
  72  |   expect(otherTabErrors, 'The isolated-tab check must not emit browser errors').toEqual([]);
  73  |   await otherTab.close();
  74  | 
  75  |   await openPublicRepository(page);
  76  |   const reloadedCredentialInput = page.getByLabel('GitHub personal access token');
  77  |   await expect(reloadedCredentialInput).toHaveAttribute('type', 'password');
  78  |   await expect(reloadedCredentialInput).toHaveValue('');
  79  |   await page.getByRole('button', { name: 'Forget token' }).click();
  80  |   await expect(page.getByText('Token forgotten for this tab.')).toBeVisible();
  81  |   await expect(page.locator('[data-credential-state="present"]')).toHaveCount(0);
  82  |   await expect(reloadedCredentialInput).toHaveValue('');
  83  |   expect(await credentialIsStored(page), 'Forget must remove tab storage').toBe(false);
  84  |   expect(await renderedDocumentExcludes(page, credential), 'Forgotten credential must not appear in rendered markup').toBe(true);
  85  |   expect(browserErrors, 'Credential reload and Forget must not emit browser errors').toEqual([]);
  86  | });
  87  | 
  88  | test('authentication rejection clears the credential and requests a replacement', async ({ page }) => {
  89  |   test.setTimeout(180_000);
  90  |   const browserErrors = observeBrowserErrors(page);
  91  |   const credential = createEphemeralCredential();
  92  |   await installAuthenticationRejection(page);
  93  | 
> 94  |   await page.goto('/');
      |              ^ Error: page.goto: Protocol error (Page.navigate): Cannot navigate to invalid URL
  95  |   await openPublicRepository(page);
  96  |   const browser = page.getByRole('region', { name: 'Browse workspace' });
  97  |   await browser.getByRole('button', { name: /^README(?:\s|$)/ }).click();
  98  |   const editor = page.getByLabel('Plain text editor');
  99  |   await expect(editor).toBeVisible();
  100 |   await editor.fill(`${await editor.inputValue()}\nAlmToo credential rejection proof ${Date.now()}\n`);
  101 |   await page.getByRole('button', { name: 'Save changes' }).click();
  102 |   await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  103 |   await page.getByLabel('Commit message').fill(`AlmToo credential rejection proof ${Date.now()}`);
  104 |   await page.getByRole('button', { name: 'Create local commit' }).click();
  105 |   await expect(page.getByText('Created a browser-local commit.')).toBeVisible();
  106 | 
  107 |   const credentialInput = page.getByLabel('GitHub personal access token');
  108 |   await credentialInput.fill(credential);
  109 |   await page.getByRole('button', { name: 'Use token in this tab' }).click();
  110 |   await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  111 |   expect(await credentialIsStored(page), 'Credential should be present before the explicit push').toBe(true);
  112 | 
  113 |   await page.getByRole('button', { name: 'Review push' }).click();
  114 |   const reviewedSha = (await page.locator('[data-push-review="sha"]').textContent())?.trim();
  115 |   expect(reviewedSha, 'Push review should expose only a full commit identifier').toMatch(/^[0-9a-f]{40}$/i);
  116 |   await page.getByLabel(/I confirm this exact commit/).check();
  117 |   await page.getByRole('button', { name: 'Push reviewed commit' }).click();
  118 | 
  119 |   await expect(page.locator('[data-credential-state="replacement-required"]')).toHaveText(
  120 |     'The remote rejected the credential or repository permission. Enter a replacement token.',
  121 |     { timeout: 90_000 });
  122 |   await expect(credentialInput).toBeEnabled();
  123 |   await expect(credentialInput).toHaveValue('');
  124 |   await expect(page.locator('[data-credential-state="present"]')).toHaveCount(0);
  125 |   expect(await credentialIsStored(page), 'Credential rejection must remove tab storage').toBe(false);
  126 |   expect(await renderedDocumentExcludes(page, credential), 'Rejected credential must not appear in rendered markup').toBe(true);
  127 |   expect(browserErrors, 'Credential rejection must not emit browser errors').toEqual([]);
  128 | });
  129 | 
```