import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const moduleUrl = new URL('../wwwroot/js/browserGitEngine.js', import.meta.url);
const serviceUrl = new URL('../Services/Git/BrowserGitService.cs', import.meta.url);
const serviceContractUrl = new URL('../Services/Git/IBrowserGitService.cs', import.meta.url);
const resultContractUrl = new URL('../Services/Git/GitOperationResult.cs', import.meta.url);

async function importFreshModule() {
  return import(`${moduleUrl.href}?case=${Date.now()}-${Math.random()}`);
}

function resetBrowserGlobals() {
  delete globalThis.document;
  delete globalThis.LightningFS;
  delete globalThis.git;
  delete globalThis.GitHttp;
}

function installLoadingDocument({ failFirstScript = false } = {}) {
  const scripts = new Map();

  globalThis.document = {
    head: {
      appendChild(script) {
        if (failFirstScript && scripts.size === 0) {
          scripts.set(script.dataset.browserGitVendor, script);
          script.listeners.error?.();
          return;
        }

        scripts.set(script.dataset.browserGitVendor, script);
        script.listeners.load?.();
      }
    },
    createElement(tagName) {
      assert.equal(tagName, 'script');
      return {
        async: true,
        dataset: {},
        listeners: {},
        addEventListener(name, callback) {
          this.listeners[name] = callback;
        }
      };
    },
    querySelector() {
      return null;
    }
  };

  return scripts;
}

function installFakeGitRuntime(gitOverrides = {}) {
  class FakeLightningFS {
    constructor() {
      this.promises = {
        async mkdir() {},
        async stat(path) {
          if (path.endsWith('/.git')) {
            const error = new Error('missing repository metadata');
            error.code = 'ENOENT';
            throw error;
          }

          return { isFile: () => true, size: 0 };
        },
        async readdir() {
          return [];
        },
        async readFile() {
          return new Uint8Array();
        },
        async writeFile() {}
      };
    }
  }

  globalThis.LightningFS = FakeLightningFS;
  globalThis.git = {
    async clone() {},
    async resolveRef() {
      return 'HEAD';
    },
    async statusMatrix() {
      return [];
    },
    async add() {},
    async remove() {},
    async commit() {
      return 'abc123';
    },
    ...gitOverrides
  };
  globalThis.GitHttp = { request() {} };
}

test('initialize returns a structured failure when a vendor dependency cannot load', async () => {
  resetBrowserGlobals();
  installLoadingDocument({ failFirstScript: true });

  const engine = await importFreshModule();
  const result = await engine.initialize();

  assert.equal(result.operation, 'initialize');
  assert.equal(result.succeeded, false);
  assert.match(result.message, /could not be initialized/i);
  assert.match(result.diagnostic, /Failed to load/);
});

test('cloneOrOpen validates malformed repository requests before invoking git clone', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime();

  const engine = await importFreshModule();
  const result = await engine.cloneOrOpen({ repositoryUrl: '', workspaceName: 'demo' });

  assert.equal(result.operation, 'cloneOrOpen');
  assert.equal(result.succeeded, false);
  assert.match(result.message, /could not be cloned or opened/i);
  assert.match(result.diagnostic, /repositoryUrl is required/);
});

test('cloneOrOpen routes public repository clones through the browser CORS proxy', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  let cloneOptions;
  installFakeGitRuntime({
    async clone(options) {
      cloneOptions = options;
    }
  });

  const engine = await importFreshModule();
  const result = await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'hello-world'
  });

  assert.equal(result.succeeded, true);
  assert.equal(cloneOptions.url, 'https://github.com/octocat/Hello-World.git');
  assert.equal(cloneOptions.corsProxy, 'https://cors.isomorphic-git.org');
  assert.equal(cloneOptions.singleBranch, true);
  assert.equal(cloneOptions.depth, 1);
});

test('repository paths reject parent directory traversal as structured operation failures', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime();

  const engine = await importFreshModule();
  const openResult = await engine.cloneOrOpen({
    repositoryUrl: 'https://example.test/repository.git',
    workspaceName: 'demo'
  });
  assert.equal(openResult.succeeded, true);

  const result = await engine.listFiles('../outside');

  assert.equal(result.operation, 'listFiles');
  assert.equal(result.succeeded, false);
  assert.match(result.message, /could not be listed/i);
  assert.match(result.diagnostic, /parent directory segments/);
});

test('getStatus returns a structured failure when git status cannot be read', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime({
    async statusMatrix() {
      throw new Error('IndexedDB quota exceeded while reading git status');
    }
  });

  const engine = await importFreshModule();
  const openResult = await engine.cloneOrOpen({
    repositoryUrl: 'https://example.test/repository.git',
    workspaceName: 'demo'
  });
  assert.equal(openResult.succeeded, true);

  const result = await engine.getStatus();

  assert.equal(result.operation, 'getStatus');
  assert.equal(result.succeeded, false);
  assert.match(result.message, /status could not be read/i);
  assert.match(result.diagnostic, /quota exceeded/);
});

test('commit validates author input and no-change repositories as structured failures', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime();

  const engine = await importFreshModule();
  const openResult = await engine.cloneOrOpen({
    repositoryUrl: 'https://example.test/repository.git',
    workspaceName: 'demo'
  });
  assert.equal(openResult.succeeded, true);

  const missingAuthorResult = await engine.commit({
    message: 'Update docs',
    authorName: '',
    authorEmail: 'dev@example.test'
  });
  assert.equal(missingAuthorResult.operation, 'commit');
  assert.equal(missingAuthorResult.succeeded, false);
  assert.match(missingAuthorResult.message, /commit could not be created/i);
  assert.match(missingAuthorResult.diagnostic, /authorName is required/);

  const noChangesResult = await engine.commit({
    message: 'Update docs',
    authorName: 'Developer',
    authorEmail: 'dev@example.test'
  });
  assert.equal(noChangesResult.operation, 'commit');
  assert.equal(noChangesResult.succeeded, false);
  assert.match(noChangesResult.message, /commit could not be created/i);
  assert.match(noChangesResult.diagnostic, /no browser-local changes/);
});

test('inspectPush returns the canonical origin, current branch, outgoing SHA, and exact destination ref', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime({
    async getConfig({ path }) {
      assert.equal(path, 'remote.origin.url');
      return 'https://github.com/octocat/Hello-World.git';
    },
    async currentBranch({ fullname }) {
      assert.equal(fullname, true);
      return 'refs/heads/main';
    },
    async resolveRef({ ref }) {
      assert.equal(ref, 'HEAD');
      return '0123456789abcdef0123456789abcdef01234567';
    }
  });

  const engine = await importFreshModule();
  assert.equal((await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'hello-world'
  })).succeeded, true);

  const result = await engine.inspectPush();

  assert.equal(result.succeeded, true);
  assert.deepEqual(result.value, {
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    branch: 'main',
    outgoingCommitId: '0123456789abcdef0123456789abcdef01234567',
    destinationRef: 'refs/heads/main'
  });
});

test('push revalidates the review and performs one exact-branch non-force transport attempt', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  const token = 'github_pat_secret-value';
  const oid = 'fedcba9876543210fedcba9876543210fedcba98';
  const pushCalls = [];
  installFakeGitRuntime({
    async getConfig() {
      return 'https://github.com/octocat/Hello-World.git';
    },
    async currentBranch() {
      return 'refs/heads/topic/exact-push';
    },
    async resolveRef() {
      return oid;
    },
    async push(options) {
      pushCalls.push(options);
      const auth = options.onAuth();
      assert.equal(auth.username, token);
      assert.equal(auth.password, 'x-oauth-basic');
      return { ok: true };
    }
  });

  const engine = await importFreshModule();
  await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'hello-world'
  });
  const review = (await engine.inspectPush()).value;
  const result = await engine.push(review, token);

  assert.equal(result.succeeded, true);
  assert.equal(result.value.pushedCommitId, oid);
  assert.equal(pushCalls.length, 1);
  assert.equal(pushCalls[0].url, review.repositoryUrl);
  assert.equal(pushCalls[0].ref, review.branch);
  assert.equal(pushCalls[0].remoteRef, review.branch);
  assert.equal(pushCalls[0].force, false);
  assert.equal(pushCalls[0].corsProxy, 'https://cors.isomorphic-git.org');
  assert.equal(JSON.stringify(result).includes(token), false);
});

test('push rejects missing credentials and malformed review without transport', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  let pushCount = 0;
  installFakeGitRuntime({
    async push() {
      pushCount += 1;
    }
  });

  const engine = await importFreshModule();
  await engine.cloneOrOpen({ repositoryUrl: 'https://github.com/octocat/Hello-World.git', workspaceName: 'demo' });

  const missingReview = await engine.push(null, 'token');
  const missingToken = await engine.push({ repositoryUrl: 'https://github.com/octocat/Hello-World.git' }, '');

  assert.equal(missingReview.succeeded, false);
  assert.match(missingReview.diagnostic, /review is required/i);
  assert.equal(missingToken.succeeded, false);
  assert.match(missingToken.diagnostic, /personalAccessToken is required/i);
  assert.equal(pushCount, 0);
});

test('inspectPush rejects an absent active repository without reading Git state', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime({
    async getConfig() {
      assert.fail('Git state must not be read without an active repository');
    }
  });

  const engine = await importFreshModule();
  const result = await engine.inspectPush();

  assert.equal(result.succeeded, false);
  assert.match(result.diagnostic, /requires cloneOrOpen/i);
});

test('inspectPush rejects missing origins and detached or unsupported refs', async () => {
  for (const scenario of [
    { origin: undefined, branch: 'refs/heads/main', expected: /origin/i },
    { origin: 'https://token@github.com/octocat/Hello-World.git', branch: 'refs/heads/main', expected: /credential-free/i },
    { origin: 'https://github.com/octocat/Hello-World.git', branch: undefined, expected: /checked-out branch/i },
    { origin: 'https://github.com/octocat/Hello-World.git', branch: 'refs/tags/v1', expected: /refs\/heads/i }
  ]) {
    resetBrowserGlobals();
    installLoadingDocument();
    installFakeGitRuntime({
      async getConfig() { return scenario.origin; },
      async currentBranch() { return scenario.branch; },
      async resolveRef() { return '0123456789abcdef0123456789abcdef01234567'; }
    });

    const engine = await importFreshModule();
    await engine.cloneOrOpen({ repositoryUrl: 'https://github.com/octocat/Hello-World.git', workspaceName: 'demo' });
    const result = await engine.inspectPush();

    assert.equal(result.succeeded, false);
    assert.match(result.diagnostic, scenario.expected);
  }
});

test('push rejects changed review state and transport errors without retrying or exposing the token', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  const token = 'github_pat_do-not-serialize';
  let origin = 'https://github.com/octocat/Hello-World.git';
  let branch = 'refs/heads/main';
  let oid = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  let pushCount = 0;
  installFakeGitRuntime({
    async getConfig() { return origin; },
    async currentBranch() { return branch; },
    async resolveRef() { return oid; },
    async push() {
      pushCount += 1;
      throw new Error(`remote rejected the update for ${token}`);
    }
  });

  const engine = await importFreshModule();
  await engine.cloneOrOpen({ repositoryUrl: 'https://github.com/octocat/Hello-World.git', workspaceName: 'demo' });
  const review = (await engine.inspectPush()).value;

  oid = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  const staleHead = await engine.push(review, token);
  assert.equal(staleHead.succeeded, false);
  assert.match(staleHead.diagnostic, /changed since review/i);
  assert.equal(pushCount, 0);

  oid = review.outgoingCommitId;
  branch = 'refs/heads/other';
  const staleBranch = await engine.push(review, token);
  assert.equal(staleBranch.succeeded, false);
  assert.match(staleBranch.diagnostic, /changed since review/i);
  assert.equal(pushCount, 0);

  branch = 'refs/heads/main';
  origin = 'https://github.com/octocat/Another-Repository.git';
  const staleOrigin = await engine.push(review, token);
  assert.equal(staleOrigin.succeeded, false);
  assert.match(staleOrigin.diagnostic, /changed since review/i);
  assert.equal(pushCount, 0);

  origin = review.repositoryUrl;
  const rejected = await engine.push(review, token);
  assert.equal(rejected.succeeded, false);
  assert.equal(pushCount, 1);
  assert.equal(JSON.stringify(rejected).includes(token), false);
  assert.match(rejected.diagnostic, /remote rejected/i);
  assert.match(rejected.diagnostic, /\[REDACTED\]/);
});

test('Blazor Git service invokes operations exported by the browser Git module', async () => {
  const [serviceSource, moduleSource] = await Promise.all([
    readFile(serviceUrl, 'utf8'),
    readFile(moduleUrl, 'utf8')
  ]);

  for (const operation of [
    'initialize',
    'cloneOrOpen',
    'listFiles',
    'readTextFile',
    'writeTextFile',
    'getStatus',
    'commit',
    'inspectPush',
    'push'
  ]) {
    assert.match(moduleSource, new RegExp(`export async function ${operation}\\b`));
    assert.match(serviceSource, new RegExp(`\\"${operation}\\"`));
  }

  assert.match(serviceSource, /catch \(OperationCanceledException\)/);
  assert.match(serviceSource, /catch \(JSException\)/);
  assert.doesNotMatch(serviceSource, /exception\.ToString\(\)/);
  assert.match(serviceSource, /GitOperationResult<RepositoryInfo>\.Failure/);
  assert.match(serviceSource, /GitOperationResult<CommitInfo>\.Failure/);
});

test('Blazor push boundary keeps credentials input-only and preserves reviewed and pushed identity', async () => {
  const [serviceSource, interfaceSource, resultContractSource] = await Promise.all([
    readFile(serviceUrl, 'utf8'),
    readFile(serviceContractUrl, 'utf8'),
    readFile(resultContractUrl, 'utf8')
  ]);

  assert.match(interfaceSource, /GitOperationResult<PushReview>> InspectPushAsync/);
  assert.match(interfaceSource, /GitOperationResult<PushResult>> PushAsync\([\s\S]*?PushRequest request,[\s\S]*?string personalAccessToken/);

  assert.match(resultContractSource, /public record PushReview\([\s\S]*?string RepositoryUrl,[\s\S]*?string Branch,[\s\S]*?string OutgoingCommitId,[\s\S]*?string DestinationRef\);/);
  assert.match(resultContractSource, /public record PushRequest\([\s\S]*?string RepositoryUrl,[\s\S]*?string Branch,[\s\S]*?string OutgoingCommitId,[\s\S]*?string DestinationRef\);/);
  assert.match(resultContractSource, /public record PushResult\([\s\S]*?string RepositoryUrl,[\s\S]*?string Branch,[\s\S]*?string DestinationRef,[\s\S]*?string PushedCommitId\);/);

  const recordParameters = [...resultContractSource.matchAll(/public record\s+\w+(?:<[^>]+>)?\s*\(([\s\S]*?)\);/g)]
    .map((match) => match[1])
    .join('\n');
  assert.doesNotMatch(recordParameters, /\b(?:PersonalAccessToken|Token|Pat)\b/i);

  assert.match(serviceSource, /InvokeWithValueAsync<PushReviewDto, PushReview>\([\s\S]*?\"inspectPush\"/);
  assert.match(serviceSource, /InvokeWithValueAsync<PushResultDto, PushResult>\([\s\S]*?request,[\s\S]*?personalAccessToken\);/);
  assert.match(serviceSource, /return new\(RepositoryUrl, Branch, DestinationRef, PushedCommitId\);/);
  assert.match(serviceSource, /RedactCredential\(result, personalAccessToken\)/);
  assert.match(serviceSource, /The response value did not match the expected contract/);
});
