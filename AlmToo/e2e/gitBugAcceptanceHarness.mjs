import { expect } from '@playwright/test';

import { startControlledGitBugRemote } from './controlledGitBugRemote.mjs';
import { seedGitBugFixture, waitForGitBugFixture } from './gitBugFixture.mjs';

const proxyPrefix = 'https://cors.isomorphic-git.org/';
const proxyPattern = `${proxyPrefix}**`;
const defaultTimeout = 30_000;
const maxDiagnostics = 50;

function boundedPush(values, value) {
  if (values.length < maxDiagnostics) values.push(value);
}

function redact(value, secrets) {
  let result = typeof value === 'string' ? value : JSON.stringify(value);
  for (const secret of secrets) {
    if (secret) result = result.split(secret).join('[REDACTED]');
  }
  return result;
}

function redactValue(value, secrets) {
  if (typeof value === 'string') return redact(value, secrets);
  return JSON.parse(redact(value, secrets));
}

export function collectGitBugDiagnostics(page, { scenario = 'unknown', credential = '' } = {}) {
  const secrets = new Set([credential]);
  const diagnostics = {
    scenario,
    phase: 'created',
    consoleErrors: [],
    pageErrors: [],
    requestFailures: [],
    httpErrors: [],
    proxyErrors: [],
    lifecycle: { remoteStarted: false, remoteReady: false, proxyInstalled: false, browserReady: false, cleaned: false }
  };

  const onPageError = error => boundedPush(diagnostics.pageErrors, redact(error.message, secrets));
  const onConsole = message => {
    if (message.type() === 'error') boundedPush(diagnostics.consoleErrors, redact(message.text(), secrets));
  };
  const onRequestFailed = request => boundedPush(diagnostics.requestFailures, {
    url: redact(request.url(), secrets),
    failure: redact(request.failure()?.errorText ?? 'unknown', secrets)
  });
  const onResponse = response => {
    if (response.status() >= 400) boundedPush(diagnostics.httpErrors, {
      url: redact(response.url(), secrets),
      status: response.status()
    });
  };

  page.on('pageerror', onPageError);
  page.on('console', onConsole);
  page.on('requestfailed', onRequestFailed);
  page.on('response', onResponse);

  return {
    diagnostics,
    setPhase(phase) { diagnostics.phase = phase; },
    addSecret(secret) { if (secret) secrets.add(secret); },
    recordProxyError(error) { boundedPush(diagnostics.proxyErrors, redact(error?.message ?? String(error), secrets)); },
    removeListeners() {
      page.off('pageerror', onPageError);
      page.off('console', onConsole);
      page.off('requestfailed', onRequestFailed);
      page.off('response', onResponse);
    },
    redact(value) { return redactValue(value, secrets); }
  };
}

async function installControlledCorsProxy(page, repositoryUrl, remoteUrl, diagnostics) {
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
      responseHeaders['access-control-allow-headers'] = '*';
      await route.fulfill({
        status: response.status,
        headers: responseHeaders,
        body: Buffer.from(await response.arrayBuffer())
      });
    } catch (error) {
      diagnostics.recordProxyError(error);
      await route.abort('failed');
    }
  });
}

async function openFixture(page, repositoryUrl, timeout = defaultTimeout) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Open a public repository' })).toBeVisible({ timeout });
  await seedGitBugFixture(page, 'supported', { repositoryUrl });
  await waitForGitBugFixture(page, { timeout });
  await page.getByLabel('Repository URL').fill(repositoryUrl);
  await page.getByRole('button', { name: 'Open repository' }).click();
  await expect(page.getByRole('heading', { name: 'Repository ready' })).toBeVisible({ timeout });
  await page.getByRole('link', { name: 'Git-Bug Issues' }).click();
  await expect(page).toHaveURL(/\/git-bug$/);
  await expect(page.getByRole('heading', { name: 'Git-Bug issues', level: 1 })).toBeVisible({ timeout });
}

function acceptanceError(error, scenario, phase) {
  const message = error instanceof Error ? error.message : String(error);
  const wrapped = new Error(`[git-bug acceptance] scenario=${scenario} phase=${phase}: ${message}`);
  if (error instanceof Error) wrapped.cause = error;
  return wrapped;
}

async function clearBrowserState(page) {
  if (page.isClosed()) return;
  await page.evaluate(() => {
    localStorage.clear();
    sessionStorage.clear();
  });
}

async function attachDiagnostics(testInfo, diagnostics) {
  if (!testInfo?.attach) return;
  await testInfo.attach('git-bug-acceptance-diagnostics', {
    body: Buffer.from(JSON.stringify(diagnostics, null, 2)),
    contentType: 'application/json'
  });
}

/**
 * Owns one complete deterministic Git-Bug browser acceptance lifecycle.
 * The helper is test infrastructure at the Resource boundary; production
 * Clients, Managers, Accessors, and Resources are not involved here.
 */
export async function withGitBugAcceptance(page, testInfo, options = {}, action) {
  if (typeof action !== 'function') throw new TypeError('Git-Bug acceptance action is required.');
  const scenario = options.scenario ?? testInfo?.title ?? 'git-bug acceptance';
  const diagnosticsContext = collectGitBugDiagnostics(page, { scenario, credential: options.credential });
  const { diagnostics } = diagnosticsContext;
  let remote;
  let operationError = null;
  let phase = 'remote startup';
  let routeInstalled = false;

  try {
    remote = await startControlledGitBugRemote(options.remote ?? {});
    diagnosticsContext.addSecret(remote.credentials?.username);
    diagnosticsContext.addSecret(remote.credentials?.password);
    diagnostics.lifecycle.remoteStarted = true;

    phase = 'remote readiness';
    diagnosticsContext.setPhase(phase);
    await remote.waitUntilReady();
    diagnostics.remoteBefore = await remote.snapshot();
    diagnostics.lifecycle.remoteReady = true;

    phase = 'proxy installation';
    diagnosticsContext.setPhase(phase);
    await installControlledCorsProxy(page, remote.url, remote.url, diagnosticsContext);
    routeInstalled = true;
    diagnostics.lifecycle.proxyInstalled = true;

    phase = 'browser fixture readiness';
    diagnosticsContext.setPhase(phase);
    await openFixture(page, remote.url, options.timeout ?? defaultTimeout);
    diagnostics.lifecycle.browserReady = true;

    phase = 'scenario';
    diagnosticsContext.setPhase(phase);
    return await action({
      page,
      remote,
      diagnostics,
      setPhase(nextPhase) {
        phase = nextPhase;
        diagnosticsContext.setPhase(nextPhase);
      },
      openFixture: () => openFixture(page, remote.url, options.timeout ?? defaultTimeout),
      assertNoCredential(value) {
        const serialized = JSON.stringify(value);
        expect(serialized, 'credential must not cross a browser-visible diagnostic surface')
          .not.toContain(options.credential ?? remote.credentials?.username ?? '');
      }
    });
  } catch (error) {
    operationError = acceptanceError(error, scenario, phase);
    throw operationError;
  } finally {
    const cleanupErrors = [];
    diagnosticsContext.setPhase('cleanup');
    try {
      await clearBrowserState(page);
    } catch (error) {
      cleanupErrors.push(acceptanceError(error, scenario, 'cleanup browser state'));
    }
    if (routeInstalled) {
      try {
        await page.unroute(proxyPattern);
      } catch (error) {
        cleanupErrors.push(acceptanceError(error, scenario, 'cleanup proxy route'));
      }
    }
    if (remote) {
      try {
        diagnostics.remoteAfter = await remote.snapshot();
        diagnostics.remote = diagnostics.remoteAfter;
      } catch (error) {
        cleanupErrors.push(acceptanceError(error, scenario, 'capture remote diagnostics'));
      }
      try {
        await remote.close();
      } catch (error) {
        cleanupErrors.push(acceptanceError(error, scenario, 'cleanup controlled remote'));
      }
    }
    diagnostics.cleanupErrors = cleanupErrors.map(error => error.message);
    diagnostics.lifecycle.cleaned = cleanupErrors.length === 0;
    diagnosticsContext.removeListeners();
    try {
      await attachDiagnostics(testInfo, diagnosticsContext.redact(diagnostics));
    } catch (error) {
      cleanupErrors.push(acceptanceError(error, scenario, 'attach diagnostics'));
    }
    if (!operationError && cleanupErrors.length > 0) throw cleanupErrors[0];
  }
}

export { openFixture };
