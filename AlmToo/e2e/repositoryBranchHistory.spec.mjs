import { expect, test } from '@playwright/test';

import {
  branchHistoryFixture,
  branchHistorySnapshotDigest,
  seedBranchHistoryFixture,
  snapshotBranchHistoryRemote,
  snapshotBranchHistoryRepository,
  startBranchHistoryFixture
} from './gitBranchHistoryFixture.mjs';

const proxyPrefix = 'https://cors.isomorphic-git.org/';
const proxyPattern = `${proxyPrefix}**`;
const workspacePath = '/almtoo-workspaces/git-bug-fixture';
const fixtureUsername = 'branch-history-user';
const fixturePassword = 'branch-history-secret';

function observeDiagnostics(page, secrets) {
  const diagnostics = {
    pageErrors: [],
    consoleErrors: [],
    requestFailures: [],
    httpErrors: [],
    dialogs: [],
    proxyErrors: []
  };
  const redact = value => {
    let result = String(value ?? '');
    for (const secret of secrets) result = result.split(secret).join('[REDACTED]');
    return result;
  };
  page.on('pageerror', error => diagnostics.pageErrors.push(redact(error.message)));
  page.on('console', message => {
    if (message.type() === 'error') diagnostics.consoleErrors.push(redact(message.text()));
  });
  page.on('requestfailed', request => diagnostics.requestFailures.push(redact(`${request.url()} ${request.failure()?.errorText ?? ''}`)));
  page.on('response', response => {
    if (response.status() >= 400) diagnostics.httpErrors.push(redact(`${response.status()} ${response.url()}`));
  });
  page.on('dialog', async dialog => {
    diagnostics.dialogs.push(redact(`${dialog.type()}: ${dialog.message()}`));
    await dialog.dismiss();
  });
  return { diagnostics, redact };
}

async function installControlledCorsProxy(page, repositoryUrl, remoteUrl, diagnostics, redact) {
  await page.route(proxyPattern, async route => {
    const request = route.request();
    const proxiedUrl = request.url().slice(proxyPrefix.length);
    const repositoryTarget = proxiedUrl.startsWith(repositoryUrl)
      ? repositoryUrl
      : repositoryUrl.replace(/^https?:\/\//u, '');
    if (!proxiedUrl.startsWith(repositoryTarget)) {
      await route.continue();
      return;
    }

    const target = `${remoteUrl}${proxiedUrl.slice(repositoryTarget.length)}`;
    const headers = { ...request.headers() };
    delete headers.host;
    delete headers.origin;
    try {
      const response = await fetch(target, {
        method: request.method(),
        headers,
        body: ['GET', 'HEAD'].includes(request.method()) ? undefined : request.postDataBuffer()
      });
      const responseHeaders = Object.fromEntries(response.headers.entries());
      responseHeaders['access-control-allow-origin'] = '*';
      await route.fulfill({
        status: response.status,
        headers: responseHeaders,
        body: Buffer.from(await response.arrayBuffer())
      });
    } catch (error) {
      diagnostics.proxyErrors.push(redact(error?.message ?? error));
      await route.abort();
    }
  });
}

async function seedFeatureBranchIntoBrowserFixture(page) {
  await page.evaluate(async ({ workspacePath: dir, fixture }) => {
    const fixtureFs = globalThis.__almTooGitBugFixtureFs;
    const browserGit = globalThis.git;
    if (!fixtureFs || !browserGit) throw new Error('The deterministic browser Git fixture is not ready.');

    const gitdir = `${dir}/.git`;
    const baseCommit = await browserGit.resolveRef({ fs: fixtureFs, gitdir, ref: 'refs/heads/main' });
    const author = { name: 'AlmToo branch-history fixture', email: 'branch-history@almtoo.invalid' };
    const createBranch = async (branch, contents, messages) => {
      await browserGit.branch({ fs: fixtureFs, dir, ref: branch, object: baseCommit, checkout: false, force: true });
      await browserGit.checkout({ fs: fixtureFs, dir, ref: branch, force: true, noUpdateHead: false });
      for (const [index, message] of messages.entries()) {
        await fixtureFs.promises.writeFile(`${dir}/${fixture.trackedFile}`, `${contents}${index + 1}\\n`);
        await browserGit.add({ fs: fixtureFs, dir, filepath: fixture.trackedFile });
        await browserGit.commit({ fs: fixtureFs, dir, message, author, committer: author });
      }
    };

    await createBranch(fixture.mainBranch, fixture.mainContents, ['Main branch checkpoint', 'Main branch follow-up']);
    await createBranch(fixture.featureBranch, fixture.featureContents, ['Feature branch checkpoint', 'Feature branch follow-up']);
    await browserGit.checkout({ fs: fixtureFs, dir, ref: fixture.mainBranch, force: true, noUpdateHead: false });
    await fixtureFs.promises.writeFile(`${gitdir}/HEAD`, `ref: refs/heads/${fixture.mainBranch}\n`);
  }, { workspacePath, fixture: branchHistoryFixture });
}

async function openBranchHistoryRepository(page, remote) {
  await page.goto('/');
  await seedBranchHistoryFixture(page, remote);
  await seedFeatureBranchIntoBrowserFixture(page, remote);
  await page.locator('#repository-url').fill(remote.url);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'Branches' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Recent commits' })).toBeVisible();
}

function branchSelect(page) {
  return page.locator('#repository-branch');
}

test('discovers branch state, inspects history, switches clean worktrees, and preserves dirty worktrees', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  const secrets = [fixtureUsername, fixturePassword];
  const { diagnostics, redact } = observeDiagnostics(page, secrets);
  const remote = await startBranchHistoryFixture({ username: fixtureUsername, password: fixturePassword });
  await installControlledCorsProxy(page, remote.url, remote.url, diagnostics, redact);

  try {
    await openBranchHistoryRepository(page, remote);
    const remoteBefore = await snapshotBranchHistoryRemote(remote);
    const initial = await snapshotBranchHistoryRepository(page, workspacePath);
    expect(initial.head).toBe(initial.refs['heads/main']);
    expect(initial.refs['heads/main']).toBeTruthy();
    expect(initial.status.every(entry => entry.head === entry.workdir && entry.workdir === entry.stage)).toBe(true);

    const branchSection = page.locator('.repository-branch-selector');
    await expect(branchSection.getByText('Current branch')).toBeVisible();
    await expect(branchSection.locator('.repository-branch-selector__current strong')).toHaveText(branchHistoryFixture.mainBranch);
    const branchPicker = branchSelect(page);
    await expect(branchPicker).toBeVisible();
    await expect(branchPicker.locator('option', { hasText: branchHistoryFixture.featureBranch })).toHaveCount(1);

    const historySection = page.locator('.repository-commit-history');
    await expect(historySection.getByText('Recent commits')).toBeVisible();
    await expect(historySection.getByLabel('Recent commit count')).toContainText(`${branchHistoryFixture.historyDepth} commits`);
    await expect(historySection.locator('li')).toHaveCount(branchHistoryFixture.historyDepth);

    await branchPicker.selectOption(branchHistoryFixture.featureBranch);
    await expect(branchSection.locator('.repository-branch-selector__current strong')).toHaveText(branchHistoryFixture.featureBranch, { timeout: 30_000 });
    await expect(historySection.getByText('Feature branch follow-up')).toBeVisible();
    await expect(historySection.getByText('Feature branch checkpoint')).toBeVisible();

    const cleanFeature = await snapshotBranchHistoryRepository(page, workspacePath);
    expect(cleanFeature.head).toBe(cleanFeature.refs['heads/feature']);
    expect(cleanFeature.trackedFiles[branchHistoryFixture.trackedFile]).toBeTruthy();
    expect(cleanFeature.status.every(entry => entry.head === entry.workdir && entry.workdir === entry.stage)).toBe(true);

    await branchPicker.selectOption(branchHistoryFixture.mainBranch);
    await expect(branchSection.locator('.repository-branch-selector__current strong')).toHaveText(branchHistoryFixture.mainBranch, { timeout: 30_000 });
    const cleanMain = await snapshotBranchHistoryRepository(page, workspacePath);
    expect(cleanMain.head).toBe(cleanMain.refs['heads/main']);
    expect(cleanMain.trackedFiles[branchHistoryFixture.trackedFile]).toBeTruthy();
    expect(cleanMain.trackedFiles[branchHistoryFixture.trackedFile]).not.toBe(
      cleanFeature.trackedFiles[branchHistoryFixture.trackedFile]
    );
    expect(cleanMain.status.every(entry => entry.head === entry.workdir && entry.workdir === entry.stage)).toBe(true);

    const markerFile = page.getByRole('button', { name: branchHistoryFixture.trackedFile });
    await markerFile.click();
    const editor = page.locator('.file-editor');
    await expect(editor).toBeVisible();
    const editorInput = editor.locator('textarea');
    await editorInput.fill('dirty branch-history edit\n');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect(page.getByText('The browser-local files were updated.', { exact: true })).toBeVisible({ timeout: 30_000 });

    const dirtyBefore = await snapshotBranchHistoryRepository(page, workspacePath);
    expect(dirtyBefore.head).toBe(dirtyBefore.refs['heads/main']);
    expect(dirtyBefore.trackedFiles[branchHistoryFixture.trackedFile]).toBeTruthy();
    expect(dirtyBefore.trackedFiles[branchHistoryFixture.trackedFile]).not.toBe(
      cleanMain.trackedFiles[branchHistoryFixture.trackedFile]
    );
    expect(dirtyBefore.status.some(entry => entry.filepath === branchHistoryFixture.trackedFile && entry.head !== entry.workdir)).toBe(true);

    await branchPicker.selectOption(branchHistoryFixture.featureBranch);
    await expect(page.getByRole('alert')).toContainText('Commit or discard the changed files before switching branches.');
    await expect(branchSection.locator('.repository-branch-selector__current strong')).toHaveText(branchHistoryFixture.mainBranch);

    const dirtyAfter = await snapshotBranchHistoryRepository(page, workspacePath);
    expect(branchHistorySnapshotDigest(dirtyAfter)).toBe(branchHistorySnapshotDigest(dirtyBefore));
    expect(dirtyAfter.head).toBe(dirtyAfter.refs['heads/main']);
    expect(dirtyAfter.trackedFiles[branchHistoryFixture.trackedFile]).toBe(
      dirtyBefore.trackedFiles[branchHistoryFixture.trackedFile]
    );

    expect(branchHistorySnapshotDigest(await snapshotBranchHistoryRemote(remote))).toBe(branchHistorySnapshotDigest(remoteBefore));
    expect(remote.requestCount).toBeGreaterThan(0);
    expect(JSON.stringify(diagnostics)).not.toContain(fixtureUsername);
    expect(JSON.stringify(diagnostics)).not.toContain(fixturePassword);
    expect(diagnostics.pageErrors).toEqual([]);
    expect(diagnostics.consoleErrors).toEqual([]);
    expect(diagnostics.requestFailures).toEqual([]);
    expect(diagnostics.httpErrors).toEqual([]);
    expect(diagnostics.dialogs).toEqual([]);
    expect(diagnostics.proxyErrors).toEqual([]);

    await testInfo.attach('branch-history-snapshots.json', {
      body: JSON.stringify({ initial, cleanFeature, cleanMain, dirtyBefore, dirtyAfter, remoteBefore }, null, 2),
      contentType: 'application/json'
    });
    await testInfo.attach('browser-diagnostics.json', {
      body: JSON.stringify(diagnostics, null, 2),
      contentType: 'application/json'
    });
  } finally {
    await page.unroute(proxyPattern);
    await remote.close();
  }
});
