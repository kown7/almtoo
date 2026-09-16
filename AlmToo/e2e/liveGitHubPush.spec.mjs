import { expect, test } from '@playwright/test';

const liveFixture = {
  owner: process.env.ALMTOO_LIVE_GITHUB_OWNER,
  repository: process.env.ALMTOO_LIVE_GITHUB_REPOSITORY,
  branch: process.env.ALMTOO_LIVE_GITHUB_BRANCH,
  file: process.env.ALMTOO_LIVE_GITHUB_FILE,
  token: process.env.ALMTOO_LIVE_GITHUB_PAT
};

const requiredLiveVariables = {
  ALMTOO_LIVE_GITHUB_OWNER: liveFixture.owner,
  ALMTOO_LIVE_GITHUB_REPOSITORY: liveFixture.repository,
  ALMTOO_LIVE_GITHUB_BRANCH: liveFixture.branch,
  ALMTOO_LIVE_GITHUB_FILE: liveFixture.file,
  ALMTOO_LIVE_GITHUB_PAT: liveFixture.token
};
const missingLiveVariables = Object.entries(requiredLiveVariables)
  .filter(([, value]) => !value)
  .map(([name]) => name);

function observeBrowserErrors(page, secret) {
  const errors = [];
  let exposedSecret = false;
  page.on('pageerror', error => {
    exposedSecret ||= Boolean(secret && error.message.includes(secret));
    errors.push('pageerror');
  });
  page.on('console', message => {
    exposedSecret ||= Boolean(secret && message.text().includes(secret));
    if (message.type() === 'error') errors.push('console-error');
  });
  return {
    errors,
    secretWasExposed: () => exposedSecret
  };
}

function createRejectedCredential() {
  return ['ghp', 'rejected', 'credential', Date.now().toString(36)].join('_');
}

async function credentialIsStored(page) {
  return page.evaluate(() => sessionStorage.getItem('almtoo.push-credential.v1') !== null);
}

async function openFixtureFile(page, repositoryPath) {
  const segments = repositoryPath.replace(/^\/+/, '').split('/').filter(Boolean);
  expect(segments.length, 'The fixture file must identify a repository file').toBeGreaterThan(0);

  const browser = page.getByRole('region', { name: 'Browse workspace' });
  for (const directory of segments.slice(0, -1)) {
    await browser.getByRole('button', { name: new RegExp(`^${escapeRegExp(directory)}(?:\\s|$)`) }).click();
  }
  await browser.getByRole('button', { name: new RegExp(`^${escapeRegExp(segments.at(-1))}(?:\\s|$)`) }).click();
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function githubRepositoryEndpoint(owner, repository, suffix) {
  return `https://api.github.com/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repository)}${suffix}`;
}

async function githubRequest(owner, repository, suffix, token, options = {}) {
  const response = await fetch(githubRepositoryEndpoint(owner, repository, suffix), {
    ...options,
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${token}`,
      'X-GitHub-Api-Version': '2022-11-28',
      ...options.headers
    }
  });

  if (!response.ok) {
    throw new Error(`GitHub fixture request failed with HTTP ${response.status}.`);
  }

  return response.json();
}

async function readRemoteBranchSha(owner, repository, branch, token) {
  const encodedBranch = branch.split('/').map(encodeURIComponent).join('/');
  const payload = await githubRequest(owner, repository, `/git/ref/heads/${encodedBranch}`, token);
  if (!payload?.object?.sha || !/^[0-9a-f]{40}$/i.test(payload.object.sha)) {
    throw new Error('GitHub branch verification returned a malformed commit SHA.');
  }
  return payload.object.sha;
}

async function advanceFixtureFile(owner, repository, branch, file, token) {
  const encodedFile = file.replace(/^\/+/, '').split('/').map(encodeURIComponent).join('/');
  const encodedBranch = encodeURIComponent(branch);
  const current = await githubRequest(owner, repository, `/contents/${encodedFile}?ref=${encodedBranch}`, token);
  if (!current?.sha || typeof current.content !== 'string' || current.encoding !== 'base64') {
    throw new Error('GitHub fixture file response was malformed.');
  }

  const existingContent = Buffer.from(current.content.replace(/\s/g, ''), 'base64').toString('utf8');
  const marker = `\nIndependent remote advance ${Date.now()}-${Math.random().toString(16).slice(2)}\n`;
  const updated = await githubRequest(owner, repository, `/contents/${encodedFile}`, token, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      message: `Advance remote after AlmToo review ${Date.now()}`,
      content: Buffer.from(`${existingContent}${marker}`, 'utf8').toString('base64'),
      sha: current.sha,
      branch
    })
  });

  if (!updated?.commit?.sha || !/^[0-9a-f]{40}$/i.test(updated.commit.sha)) {
    throw new Error('GitHub fixture update returned a malformed commit SHA.');
  }
  return updated.commit.sha;
}

async function readLocalPushState(page) {
  return page.evaluate(async () => {
    const engine = await import('/js/browserGitEngine.js');
    return engine.inspectPush();
  });
}

test.describe.configure({ mode: 'serial' });

test('loads the browser-local repository form without browser errors', async ({ page }) => {
  const browserDiagnostics = observeBrowserErrors(page);

  await page.goto('/');
  await expect(page).toHaveTitle('Repository Browser');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible();
  await expect(page.getByLabel('Repository URL')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open repository' })).toBeEnabled();

  expect(browserDiagnostics.errors, 'The smoke case must not emit browser errors').toEqual([]);
});

test('pushes the exact reviewed SHA to the matching disposable GitHub branch', async ({ page }) => {
  test.skip(
    missingLiveVariables.length > 0,
    `Live disposable GitHub proof skipped; set: ${missingLiveVariables.join(', ')}.`);
  test.setTimeout(180_000);

  const browserDiagnostics = observeBrowserErrors(page, liveFixture.token);
  const { owner, repository, branch, file, token } = liveFixture;
  const repositoryUrl = `https://github.com/${owner}/${repository}.git`;
  const uniqueMarker = `\nAlmToo live push proof ${Date.now()}-${Math.random().toString(16).slice(2)}\n`;

  await page.goto('/');
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });

  await openFixtureFile(page, file);
  const editor = page.getByLabel('Plain text editor');
  await expect(editor).toBeVisible();
  await editor.fill(`${await editor.inputValue()}${uniqueMarker}`);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled();

  await page.getByLabel('Commit message').fill(`AlmToo live push proof ${Date.now()}`);
  await page.getByRole('button', { name: 'Create local commit' }).click();
  await expect(page.getByText('Created a browser-local commit.')).toBeVisible();

  await page.getByRole('button', { name: 'Review push' }).click();
  const reviewedSha = (await page.locator('[data-push-review="sha"]').textContent())?.trim();
  expect(reviewedSha).toMatch(/^[0-9a-f]{40}$/i);
  await expect(page.locator('[data-push-review="origin"]')).toHaveText(`https://github.com/${owner}/${repository}`);
  await expect(page.locator('[data-push-review="branch"]')).toHaveText(branch);

  const patInput = page.getByLabel('GitHub personal access token');
  await expect(patInput).toHaveAttribute('type', 'password');
  await patInput.fill(token);
  await page.getByRole('button', { name: 'Use token in this tab' }).click();
  await expect(page.locator('.credential-presence[data-credential-state="present"]')).toBeVisible();
  const pushButton = page.getByRole('button', { name: 'Push reviewed commit' });
  await expect(pushButton).toBeDisabled();
  await page.getByLabel(/I confirm this exact commit/).check();
  await expect(pushButton).toBeEnabled();

  await pushButton.click();
  await expect(page.getByRole('button', { name: 'Pushing...' })).toBeDisabled();
  await expect(page.getByText(`Pushed commit ${reviewedSha} to the reviewed GitHub branch.`)).toBeVisible({ timeout: 90_000 });
  await expect(patInput).toHaveValue('');

  const remoteSha = await readRemoteBranchSha(owner, repository, branch, token);
  expect(remoteSha).toBe(reviewedSha);
  expect(browserDiagnostics.errors, 'The successful live push must not emit browser errors').toEqual([]);
  expect(browserDiagnostics.secretWasExposed(), 'The credential must not enter browser diagnostics').toBe(false);
});

test('real GitHub authentication rejection forgets the tab credential', async ({ page }) => {
  test.skip(
    missingLiveVariables.length > 0,
    `Live disposable GitHub rejection proof skipped; set: ${missingLiveVariables.join(', ')}.`);
  test.setTimeout(180_000);

  const rejectedCredential = createRejectedCredential();
  const browserDiagnostics = observeBrowserErrors(page, rejectedCredential);
  const { owner, repository, branch, file } = liveFixture;
  const repositoryUrl = `https://github.com/${owner}/${repository}.git`;
  const uniqueMarker = `\nAlmToo live rejection proof ${Date.now()}-${Math.random().toString(16).slice(2)}\n`;

  await page.goto('/');
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });

  await openFixtureFile(page, file);
  const editor = page.getByLabel('Plain text editor');
  await expect(editor).toBeVisible();
  await editor.fill(`${await editor.inputValue()}${uniqueMarker}`);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled();

  await page.getByLabel('Commit message').fill(`AlmToo live rejection proof ${Date.now()}`);
  await page.getByRole('button', { name: 'Create local commit' }).click();
  await expect(page.getByText('Created a browser-local commit.')).toBeVisible();

  await page.getByRole('button', { name: 'Review push' }).click();
  await expect(page.locator('[data-push-review="origin"]')).toHaveText(`https://github.com/${owner}/${repository}`);
  await expect(page.locator('[data-push-review="branch"]')).toHaveText(branch);

  const patInput = page.getByLabel('GitHub personal access token');
  await patInput.fill(rejectedCredential);
  await page.getByRole('button', { name: 'Use token in this tab' }).click();
  expect(await credentialIsStored(page), 'Credential should exist before the explicit rejection attempt').toBe(true);

  await page.getByLabel(/I confirm this exact commit/).check();
  await page.getByRole('button', { name: 'Push reviewed commit' }).click();

  await expect(page.locator('[data-credential-state="replacement-required"]')).toHaveText(
    'The remote rejected the credential or repository permission. Enter a replacement token.',
    { timeout: 90_000 });
  await expect(patInput).toBeEnabled();
  await expect(patInput).toHaveValue('');
  expect(await credentialIsStored(page), 'Live credential rejection must remove tab storage').toBe(false);
  expect(await page.locator('body').innerText()).not.toContain(rejectedCredential);
  expect(browserDiagnostics.errors, 'The live rejection case must not emit browser errors').toEqual([]);
  expect(browserDiagnostics.secretWasExposed(), 'The rejected credential must not enter browser diagnostics').toBe(false);
});

test('rejects a stale reviewed push without rewriting the independently advanced branch', async ({ page }) => {
  test.skip(
    missingLiveVariables.length > 0,
    `Live disposable GitHub stale-push proof skipped; set: ${missingLiveVariables.join(', ')}.`);
  test.setTimeout(180_000);

  const { owner, repository, branch, file, token } = liveFixture;
  const browserDiagnostics = observeBrowserErrors(page, token);
  const repositoryUrl = `https://github.com/${owner}/${repository}.git`;
  const uniqueMarker = `\nAlmToo stale push proof ${Date.now()}-${Math.random().toString(16).slice(2)}\n`;
  let receivePackAttempts = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('git-receive-pack')) {
      receivePackAttempts += 1;
    }
  });

  await page.goto('/');
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });

  await openFixtureFile(page, file);
  const editor = page.getByLabel('Plain text editor');
  await editor.fill(`${await editor.inputValue()}${uniqueMarker}`);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('button', { name: 'Save changes' })).toBeDisabled();

  await page.getByLabel('Commit message').fill(`AlmToo stale push proof ${Date.now()}`);
  await page.getByRole('button', { name: 'Create local commit' }).click();
  await expect(page.getByText('Created a browser-local commit.')).toBeVisible();
  await page.getByRole('button', { name: 'Review push' }).click();

  const reviewedSha = (await page.locator('[data-push-review="sha"]').textContent())?.trim();
  expect(reviewedSha).toMatch(/^[0-9a-f]{40}$/i);
  const localStateBeforeAdvance = await readLocalPushState(page);
  expect(localStateBeforeAdvance.succeeded).toBe(true);
  expect(localStateBeforeAdvance.value.outgoingCommitId).toBe(reviewedSha);

  const remoteShaBeforeAdvance = await readRemoteBranchSha(owner, repository, branch, token);
  expect(remoteShaBeforeAdvance).not.toBe(reviewedSha);
  const independentlyAdvancedSha = await advanceFixtureFile(owner, repository, branch, file, token);
  expect(independentlyAdvancedSha).not.toBe(remoteShaBeforeAdvance);
  expect(await readRemoteBranchSha(owner, repository, branch, token)).toBe(independentlyAdvancedSha);

  const patInput = page.getByLabel('GitHub personal access token');
  await patInput.fill(token);
  await page.getByRole('button', { name: 'Use token in this tab' }).click();
  await page.getByLabel(/I confirm this exact commit/).check();
  const pushButton = page.getByRole('button', { name: 'Push reviewed commit' });
  await expect(pushButton).toBeEnabled();

  await pushButton.click();
  const rejection = page.locator('[data-push-failure="remote-ahead"]');
  await expect(rejection).toHaveText(
    'The remote branch is ahead. Refresh the repository, reconcile the remote commits with the preserved local commit, then review and retry manually.',
    { timeout: 90_000 });
  await expect(rejection).toHaveAttribute('role', 'alert');
  expect(receivePackAttempts, 'One explicit confirmation must make one receive-pack attempt').toBe(1);

  await page.waitForTimeout(1_500);
  expect(receivePackAttempts, 'A stale rejection must not schedule an automatic retry').toBe(1);
  expect(await readRemoteBranchSha(owner, repository, branch, token), 'The rejected push must not rewrite the advanced remote').toBe(independentlyAdvancedSha);

  const localStateAfterRejection = await readLocalPushState(page);
  expect(localStateAfterRejection.succeeded).toBe(true);
  expect(localStateAfterRejection.value.outgoingCommitId, 'The outgoing local commit must remain at HEAD').toBe(reviewedSha);
  await expect(page.locator('[data-push-review="sha"]')).toHaveText(reviewedSha);
  expect(await page.locator('body').innerText()).not.toContain(token);
  expect(browserDiagnostics.errors, 'The stale rejection must not emit browser errors').toEqual([]);
  expect(browserDiagnostics.secretWasExposed(), 'The credential must not enter browser diagnostics').toBe(false);
});
