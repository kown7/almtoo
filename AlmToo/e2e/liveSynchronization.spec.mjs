import { expect, test } from '@playwright/test';
import { prepareLiveFixture, readLiveConfiguration } from './liveSynchronizationFixture.mjs';

// Intentionally evaluates at discovery: a live command without CI-injected values fails,
// rather than being skipped or accidentally reading a local environment file.
const configuration = readLiveConfiguration();
test.describe.configure({ mode: 'serial' });

function fileName(path) {
  return path.slice(path.lastIndexOf('/') + 1);
}

function safeTransportLabel(rawUrl) {
  try {
    const host = new URL(rawUrl).hostname.toLowerCase();
    if (host === 'cors.isomorphic-git.org') return 'cors-proxy';
    if (host === 'github.com' || host.endsWith('.github.com')) return 'github-transport';
  } catch {
    // Test diagnostics are intentionally best-effort and never retain the URL.
  }
  return null;
}

function observeDiagnostics(page) {
  const errors = [];
  const consoleMessages = [];
  const transportOutcomes = new Set();
  page.on('pageerror', () => errors.push('pageerror'));
  page.on('console', message => {
    if (message.type() === 'error') errors.push('console-error');
    consoleMessages.push(message.text());
  });
  page.on('requestfailed', request => {
    const transport = safeTransportLabel(request.url());
    if (transport) transportOutcomes.add(`${transport}-network-failure`);
  });
  page.on('response', response => {
    const transport = safeTransportLabel(response.url());
    if (transport && response.status() >= 400) transportOutcomes.add(`${transport}-http-${response.status()}`);
  });
  return { errors, consoleMessages, transportOutcomes };
}

async function installSynchronizationStageObserver(page) {
  return page.evaluate(() => {
    const stateKey = '__almtooLiveSynchronizationStage';
    if (window[stateKey]) return true;
    const git = window.git;
    if (!git) return false;

    const state = { last: 'not-started', failed: null, walkResult: 'not-called', pullResult: 'not-called' };
    const observedMethods = new Set(['fetch', 'resolveRef', 'log', 'isDescendent', 'walk', 'pull']);
    window.git = new Proxy(git, {
      get(target, property, receiver) {
        const original = Reflect.get(target, property, receiver);
        if (typeof property !== 'string' || !observedMethods.has(property) || typeof original !== 'function') return original;
        return async function (...args) {
          state.last = property;
          try {
            const result = await original.apply(target, args);
            if (property === 'walk') {
              state.walkResult = Array.isArray(result) ? 'array' : result === null ? 'null' : typeof result;
            }
            if (property === 'pull') {
              state.pullResult = 'succeeded';
            }
            return result;
          } catch (error) {
            state.failed = property;
            if (property === 'pull') {
              const name = ['HttpError', 'MissingParameterError', 'FastForwardError'].includes(error?.name) ? error.name : 'other';
              const status = error?.statusCode ?? error?.status ?? error?.data?.statusCode ?? error?.data?.status;
              state.pullResult = Number.isInteger(status) ? `failed:${name}:http-${status}` : `failed:${name}:no-http-status`;
            }
            throw error;
          }
        };
      }
    });
    window[stateKey] = state;
    return true;
  });
}

async function observedSynchronizationStage(page) {
  return page.evaluate(() => {
    const state = window.__almtooLiveSynchronizationStage;
    if (!state) return 'unavailable';
    const stage = state.failed ? `failed-${state.failed}` : `after-${state.last}`;
    return `${stage};walk-result:${state.walkResult};pull-result:${state.pullResult}`;
  });
}

async function assertNoCredentialLeak(page, diagnostics) {
  // Keep the injected value in Node: the browser product opens public repositories only.
  const browserSurface = await page.evaluate(() => [
    document.documentElement.outerHTML,
    location.href,
    ...performance.getEntriesByType('resource').map(entry => entry.name),
    ...Object.values(localStorage),
    ...Object.values(sessionStorage)
  ].join('\n'));
  expect(browserSurface.includes(configuration.token), 'The injected GitHub credential must never reach browser DOM, URLs, storage, or resource URLs').toBe(false);
  expect(diagnostics.consoleMessages.some(message => message.includes(configuration.token)), 'The injected GitHub credential must not reach console capture').toBe(false);
  expect(/(?:github_pat_|ghp_|gho_|ghs_|ghu_)/i.test(browserSurface), 'Credential-shaped values must never reach browser surfaces').toBe(false);
}

async function openFixtureRepository(page) {
  await page.goto('/');
  await page.getByLabel('Repository URL').fill(configuration.repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });
  await expect(page.getByRole('button', { name: 'Cancel current operation' })).toHaveCount(0);
}

async function workspaceSnapshot(page) {
  const workspace = page.getByRole('region', { name: 'Browse workspace' });
  return {
    repository: await page.locator('.status-grid dd').first().textContent(),
    files: await workspace.getByRole('list', { name: 'Repository file list' }).textContent()
  };
}

async function review(page) {
  await installSynchronizationStageObserver(page);
  await page.getByRole('button', { name: 'Review synchronization' }).click();
  return page.locator('.synchronization-panel');
}

async function awaitReadyReview(page, panel, diagnostics) {
  const ready = panel.locator('[data-synchronization-decision="ready-to-apply"]');
  try {
    await expect(ready).toBeVisible({ timeout: 90_000 });
    return ready;
  } catch {
    const failure = panel.locator('[data-synchronization-failure]');
    const failureCategory = await failure.getAttribute('data-synchronization-failure', { timeout: 250 }).catch(() => 'none');
    const stage = await observedSynchronizationStage(page);
    throw new Error(`Synchronization review did not become ready. Safe failure: ${failureCategory}; engine stage: ${stage}; safe transport outcomes: ${[...diagnostics.transportOutcomes].sort().join(',') || 'none'}.`);
  }
}

async function prepareAndOpen(page) {
  const fixture = await prepareLiveFixture(configuration);
  try {
    await openFixtureRepository(page);
    return fixture;
  } catch (error) {
    await fixture.cleanup();
    throw error;
  }
}

test('reviews, explicitly fast-forwards, and reopens one controlled GitHub advancement', async ({ page }) => {
  test.setTimeout(240_000);
  const diagnostics = observeDiagnostics(page);
  const fixture = await prepareAndOpen(page);
  try {
    await fixture.advance();
    const panel = await review(page);
    const ready = await awaitReadyReview(page, panel, diagnostics);
    await expect(ready.locator('.synchronization-review__sha')).toHaveText(/^[0-9a-f]{40}$/i);
    await expect(ready.getByRole('list', { name: 'Incoming changed-file summary' })).toContainText(fixture.addedPath);
    await expect(ready.getByRole('list', { name: 'Incoming changed-file summary' })).toContainText(fixture.modifiedPath);
    await expect(ready.getByRole('list', { name: 'Incoming changed-file summary' })).toContainText(fixture.deletedPath);
    await expect(ready.getByText('Added', { exact: true })).toBeVisible();
    await expect(ready.getByText('Modified', { exact: true })).toBeVisible();
    await expect(ready.getByText('Deleted', { exact: true })).toBeVisible();

    const confirmation = ready.getByLabel('I reviewed this exact incoming update and accept applying it to this browser-local workspace.');
    const apply = ready.getByRole('button', { name: 'Apply reviewed synchronization' });
    await expect(confirmation).not.toBeChecked();
    await expect(apply).toBeDisabled();
    await confirmation.check();
    await expect(apply).toBeEnabled();
    await apply.click();
    const applied = panel.locator('[data-synchronization-decision="Applied"]');
    try {
      await expect(applied).toContainText('Repository synchronization was applied', { timeout: 90_000 });
    } catch {
      const decision = await panel.locator('[data-synchronization-decision]').getAttribute('data-synchronization-decision').catch(() => 'none');
      const failure = await panel.locator('[data-synchronization-failure]').getAttribute('data-synchronization-failure').catch(() => 'none');
      const stage = await observedSynchronizationStage(page);
      throw new Error(`Synchronization apply did not reach Applied; decision=${decision ?? 'none'}; failure=${failure ?? 'none'}; stage=${stage}.`);
    }

    await page.reload();
    await page.getByLabel('Repository URL').fill(configuration.repositoryUrl);
    await page.getByRole('button', { name: 'Open repository' }).click();
    await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout: 90_000 });
    await expect(page.getByRole('definition').filter({ hasText: 'Opened from browser storage' })).toBeVisible();
    const reopenedBrowser = page.getByRole('region', { name: 'Browse workspace' });
    const reopenedFiles = reopenedBrowser.getByRole('list', { name: 'Repository file list' });
    await expect(reopenedFiles).toContainText(fileName(fixture.addedPath));
    await expect(reopenedFiles).not.toContainText(fileName(fixture.deletedPath));
    await reopenedBrowser.getByRole('button', { name: new RegExp(`^${fileName(fixture.addedPath)}(?:\\s|$)`) }).click();
    await expect(page.getByLabel('Plain text editor')).toHaveValue(`AlmToo live fixture added ${fixture.runId}\n`);
    await reopenedBrowser.getByRole('button', { name: new RegExp(`^${configuration.fixtureFile}(?:\\s|$)`) }).click();
    await expect(page.getByLabel('Plain text editor')).toHaveValue(`AlmToo live fixture modified ${fixture.runId}\n`);
    const reopenedPanel = await review(page);
    await expect(reopenedPanel.locator('[data-synchronization-decision="Current"]')).toContainText('already current', { timeout: 90_000 });
    expect(await observedSynchronizationStage(page), 'The reopened local repository must resolve matching refs without retaining their values').toBe('after-resolveRef;walk-result:not-called;pull-result:not-called');
    await assertNoCredentialLeak(page, diagnostics);
    expect(diagnostics.errors, 'A successful live synchronization must not emit page or console errors').toEqual([]);
  } finally {
    await fixture.cleanup();
  }
});

test('preserves dirty editor and uncommitted browser-local work when apply is rejected', async ({ page }) => {
  test.setTimeout(240_000);
  const diagnostics = observeDiagnostics(page);
  const fixture = await prepareAndOpen(page);
  try {
    await fixture.advance();
    const browser = page.getByRole('region', { name: 'Browse workspace' });
    await browser.getByRole('button', { name: new RegExp(`^${configuration.fixtureFile}(?:\\s|$)`) }).click();
    const editor = page.getByLabel('Plain text editor');
    await editor.fill(`${await editor.inputValue()}\nunsaved live synchronization proof\n`);
    const beforeUnsavedApply = await workspaceSnapshot(page);
    const ready = await awaitReadyReview(page, await review(page), diagnostics);
    await ready.getByLabel('I reviewed this exact incoming update and accept applying it to this browser-local workspace.').check();
    await ready.getByRole('button', { name: 'Apply reviewed synchronization' }).click();
    await expect(page.locator('[data-synchronization-failure="unsaved-editor-changes"]')).toContainText('Save or discard', { timeout: 90_000 });
    expect(await workspaceSnapshot(page)).toEqual(beforeUnsavedApply);

    await page.getByRole('button', { name: 'Save changes' }).click();
    const beforeTreeApply = await workspaceSnapshot(page);
    const readyAfterSave = await awaitReadyReview(page, await review(page), diagnostics);
    await readyAfterSave.getByLabel('I reviewed this exact incoming update and accept applying it to this browser-local workspace.').check();
    await readyAfterSave.getByRole('button', { name: 'Apply reviewed synchronization' }).click();
    await expect(page.locator('[data-synchronization-failure="uncommitted-working-tree"]')).toContainText('Commit or discard', { timeout: 90_000 });
    expect(await workspaceSnapshot(page)).toEqual(beforeTreeApply);
    await assertNoCredentialLeak(page, diagnostics);
    expect(diagnostics.errors, 'Rejected dirty applies must not emit browser errors').toEqual([]);
  } finally {
    await fixture.cleanup();
  }
});

test('preserves browser-local state for inaccessible and divergent controlled remotes', async ({ page }) => {
  test.setTimeout(300_000);
  const diagnostics = observeDiagnostics(page);
  const fixture = await prepareAndOpen(page);
  try {
    const inaccessibleBefore = await workspaceSnapshot(page);
    await page.route('**/*', async route => {
      if (route.request().url().includes('info/refs')) {
        await route.fulfill({
          status: 200,
          contentType: 'application/x-git-upload-pack-advertisement',
          headers: { 'access-control-allow-origin': '*' },
          body: '001e# service=git-upload-pack\n0000001eERR authentication failed\n0000'
        });
      } else {
        await route.continue();
      }
    });
    const inaccessiblePanel = await review(page);
    const inaccessible = inaccessiblePanel.locator('[data-synchronization-failure]');
    await expect(inaccessible).toBeVisible({ timeout: 90_000 });
    await expect(inaccessible).toHaveAttribute('data-synchronization-failure', 'credential-rejected');
    await expect(inaccessible).toContainText(/remote|connect|safely/i);
    await expect(inaccessible).not.toContainText(/stack trace|exception at|browserGitEngine|token=/i);
    expect(await workspaceSnapshot(page)).toEqual(inaccessibleBefore);
    await page.unrouteAll({ behavior: 'wait' });

    const browser = page.getByRole('region', { name: 'Browse workspace' });
    await browser.getByRole('button', { name: new RegExp(`^${configuration.fixtureFile}(?:\\s|$)`) }).click();
    const editor = page.getByLabel('Plain text editor');
    await editor.fill(`${await editor.inputValue()}\ndivergent local proof\n`);
    await page.getByRole('button', { name: 'Save changes' }).click();
    await page.getByLabel('Author name').fill('AlmToo Live Proof');
    await page.getByLabel('Author email').fill('live-proof@almtoo.local');
    await page.getByLabel('Commit message').fill(`divergent live proof ${fixture.runId}`);
    await page.getByRole('button', { name: 'Create local commit' }).click();
    await expect(page.getByText('Created a browser-local commit.')).toBeVisible();

    await fixture.advance();
    const divergentBefore = await workspaceSnapshot(page);
    const divergentPanel = await review(page);
    await expect(divergentPanel.locator('[data-synchronization-decision="Divergent"]')).toContainText('diverged', { timeout: 90_000 });
    expect(await workspaceSnapshot(page)).toEqual(divergentBefore);
    await assertNoCredentialLeak(page, diagnostics);
    expect(diagnostics.errors, 'Inaccessible and divergent reviews must not emit browser errors').toEqual([]);
  } finally {
    await fixture.cleanup();
  }
});
