import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const moduleUrl = new URL('../wwwroot/js/browserGitEngine.js', import.meta.url);
const serviceUrl = new URL('../Services/Git/BrowserGitService.cs', import.meta.url);
const serviceContractUrl = new URL('../Services/Git/IBrowserGitService.cs', import.meta.url);
const resultContractUrl = new URL('../Services/Git/GitOperationResult.cs', import.meta.url);
const vendorAssetUrls = [
  new URL('../wwwroot/js/vendor/buffer/buffer.min.js', import.meta.url),
  new URL('../wwwroot/js/vendor/isomorphic-git/index.umd.min.js', import.meta.url),
  new URL('../wwwroot/js/vendor/isomorphic-git/http-web.umd.js', import.meta.url),
  new URL('../wwwroot/js/vendor/lightning-fs/lightning-fs.min.js', import.meta.url)
];

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

function installFakeGitRuntime(gitOverrides = {}, fsOverrides = {}) {
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
        async writeFile() {},
        async flush() {},
        ...fsOverrides
      };
    }
  }

  globalThis.LightningFS = FakeLightningFS;
  globalThis.git = {
    async clone() {},
    async listServerRefs() {
      return [];
    },
    async fetch() {
      return { fetchHead: 'abc123' };
    },
    async writeRef() {},
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

async function openFakeRepository({ gitOverrides = {}, fsOverrides = {} } = {}) {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime(gitOverrides, fsOverrides);

  const engine = await importFreshModule();
  const opened = await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'coverage-demo'
  });
  assert.equal(opened.succeeded, true);
  return engine;
}

async function runPushFailure(error, token = 'github_pat_test-sentinel') {
  resetBrowserGlobals();
  installLoadingDocument();
  installFakeGitRuntime({
    async getConfig() { return 'https://github.com/octocat/Hello-World.git'; },
    async currentBranch() { return 'refs/heads/main'; },
    async resolveRef() { return 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; },
    async push() { throw error; }
  });

  const engine = await importFreshModule();
  await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'demo'
  });
  const review = (await engine.inspectPush()).value;
  return engine.push(review, token);
}

test('browser Git vendor assets are present in the static web root', async () => {
  for (const assetUrl of vendorAssetUrls) {
    const content = await readFile(assetUrl);
    assert.ok(content.length > 0, `${assetUrl.pathname} must not be empty`);
  }
});

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
  const serverRefsOptions = [];
  const fetchOptions = [];
  const writtenRefs = [];
  const issueId = 'a'.repeat(64);
  const identityId = 'b'.repeat(64);
  installFakeGitRuntime({
    async clone(options) {
      cloneOptions = options;
    },
    async listServerRefs(options) {
      serverRefsOptions.push(options);
      return options.prefix === 'refs/bugs/'
        ? [
          { ref: `refs/bugs/${issueId}`, oid: 'bug-tip' },
          { ref: 'refs/bugs/not-an-issue-id', oid: 'ignored' }
        ]
        : [{ ref: `refs/identities/${identityId}`, oid: 'identity-tip' }];
    },
    async fetch(options) {
      fetchOptions.push(options);
      return { fetchHead: options.ref.endsWith(issueId) ? 'bug-tip' : 'identity-tip' };
    },
    async writeRef(options) {
      writtenRefs.push(options);
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
  assert.deepEqual(serverRefsOptions.map(({ prefix }) => prefix), ['refs/bugs/', 'refs/identities/']);
  assert.ok(serverRefsOptions.every(({ url }) => url === cloneOptions.url));
  assert.deepEqual(fetchOptions.map(({ ref }) => ref), [
    `refs/bugs/${issueId}`,
    `refs/identities/${identityId}`
  ]);
  assert.ok(fetchOptions.every(({ remoteRef, singleBranch }) => remoteRef && singleBranch));
  assert.deepEqual(writtenRefs.map(({ ref, value }) => ({ ref, value })), [
    { ref: `refs/bugs/${issueId}`, value: 'bug-tip' },
    { ref: `refs/identities/${identityId}`, value: 'identity-tip' }
  ]);
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
  assert.match(result.diagnostic, /normalized repository-relative paths|parent directory segments/);
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

  for (const result of [missingReview, missingToken]) {
    assert.equal(result.succeeded, false);
    assert.equal(result.failureKind, 'unknown');
    assert.equal(result.diagnostic, 'The push failed without exposing remote response details.');
  }
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
  assert.equal(staleHead.failureKind, 'unknown');
  assert.equal(staleHead.diagnostic, 'The push failed without exposing remote response details.');
  assert.equal(pushCount, 0);

  oid = review.outgoingCommitId;
  branch = 'refs/heads/other';
  const staleBranch = await engine.push(review, token);
  assert.equal(staleBranch.succeeded, false);
  assert.equal(staleBranch.failureKind, 'unknown');
  assert.equal(pushCount, 0);

  branch = 'refs/heads/main';
  origin = 'https://github.com/octocat/Another-Repository.git';
  const staleOrigin = await engine.push(review, token);
  assert.equal(staleOrigin.succeeded, false);
  assert.equal(staleOrigin.failureKind, 'unknown');
  assert.equal(pushCount, 0);

  origin = review.repositoryUrl;
  const rejected = await engine.push(review, token);
  assert.equal(rejected.succeeded, false);
  assert.equal(pushCount, 1);
  assert.equal(JSON.stringify(rejected).includes(token), false);
  assert.equal(rejected.failureKind, 'unknown');
  assert.equal(rejected.diagnostic, 'The push failed without exposing remote response details.');
});

test('push classifies authentication and permission rejection without exposing host details', async () => {
  const token = 'github_pat_test-sentinel';
  const scenarios = [
    Object.assign(new Error(`Authentication failed: Authorization: Bearer ${token}`), { statusCode: 401 }),
    { status: 403, message: `write access not granted; credential=${token}` },
    Object.assign(new Error(`bad credentials ${token}`), { code: 'EAUTH' })
  ];

  for (const error of scenarios) {
    const result = await runPushFailure(error, token);
    const serialized = JSON.stringify(result);

    assert.equal(result.succeeded, false);
    assert.equal(result.failureKind, 'credentialRejected');
    assert.equal(result.diagnostic, 'The remote rejected the supplied credentials or repository permission.');
    assert.equal(serialized.includes(token), false);
    assert.equal(serialized.includes('Authorization:'), false);
    assert.equal(serialized.includes('Bearer'), false);
    assert.equal(serialized.includes(error.message), false);
  }
});

test('push classifies remote-ahead and network or CORS failures with fixed redacted fields', async () => {
  const token = 'github_pat_test-sentinel';
  const scenarios = [
    {
      kind: 'remoteAhead',
      diagnostic: 'The remote branch contains commits that are not in the reviewed local history.',
      errors: [
        Object.assign(new Error(`Push rejected because it was not a simple fast-forward ${token}`), {
          code: 'PushRejectedError',
          data: { reason: 'not-fast-forward' }
        }),
        new Error(`non-fast-forward: fetch first ${token}`)
      ]
    },
    {
      kind: 'networkUnavailable',
      diagnostic: 'The remote could not be reached from this browser.',
      errors: [
        Object.assign(new Error(`network timeout ${token}`), { code: 'ETIMEDOUT' }),
        new TypeError(`Failed to fetch because of CORS ${token}`)
      ]
    }
  ];

  for (const scenario of scenarios) {
    for (const error of scenario.errors) {
      const result = await runPushFailure(error, token);
      const serialized = JSON.stringify(result);

      assert.equal(result.succeeded, false);
      assert.equal(result.failureKind, scenario.kind);
      assert.equal(result.diagnostic, scenario.diagnostic);
      assert.deepEqual(Object.keys(result).sort(), ['diagnostic', 'failureKind', 'message', 'operation', 'succeeded', 'value']);
      assert.equal(serialized.includes(token), false);
      assert.equal(serialized.includes(error.message), false);
    }
  }
});

test('push rejects unsupported refs before transport and preserves the local HEAD', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  const oid = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  let branch = 'refs/heads/main';
  let pushCount = 0;
  installFakeGitRuntime({
    async getConfig() { return 'https://github.com/octocat/Hello-World.git'; },
    async currentBranch() { return branch; },
    async resolveRef() { return oid; },
    async push() { pushCount += 1; }
  });

  const engine = await importFreshModule();
  await engine.cloneOrOpen({ repositoryUrl: 'https://github.com/octocat/Hello-World.git', workspaceName: 'demo' });
  const review = (await engine.inspectPush()).value;
  branch = 'refs/tags/v1';

  const result = await engine.push(review, 'github_pat_test-sentinel');

  assert.equal(result.failureKind, 'unsupportedRef');
  assert.equal(result.diagnostic, 'The checked-out ref is not a supported local branch.');
  assert.equal(pushCount, 0);
  branch = 'refs/heads/main';
  assert.equal((await engine.inspectPush()).value.outgoingCommitId, oid);
});

test('rejected transport preserves local HEAD and is attempted exactly once', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  const oid = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  let pushCount = 0;
  installFakeGitRuntime({
    async getConfig() { return 'https://github.com/octocat/Hello-World.git'; },
    async currentBranch() { return 'refs/heads/main'; },
    async resolveRef() { return oid; },
    async push() {
      pushCount += 1;
      throw Object.assign(new Error('non-fast-forward'), { code: 'PushRejectedError' });
    }
  });

  const engine = await importFreshModule();
  await engine.cloneOrOpen({ repositoryUrl: 'https://github.com/octocat/Hello-World.git', workspaceName: 'demo' });
  const review = (await engine.inspectPush()).value;
  const result = await engine.push(review, 'github_pat_test-sentinel');
  const after = (await engine.inspectPush()).value;

  assert.equal(result.failureKind, 'remoteAhead');
  assert.equal(pushCount, 1);
  assert.equal(after.outgoingCommitId, oid);
  assert.deepEqual(after, review);
});

test('push maps unknown and malformed failures to an explicit redacted fallback', async () => {
  const token = 'github_pat_test-sentinel';
  const malformedError = {};
  Object.defineProperty(malformedError, 'statusCode', {
    get() { throw new Error(`throwing accessor leaked ${token}`); }
  });

  const nonStaleRejection = Object.assign(new Error(`Push rejected because tag exists ${token}`), {
    code: 'PushRejectedError',
    data: { reason: 'tag-exists' }
  });

  for (const error of [new Error(`opaque host failure ${token}`), nonStaleRejection, malformedError, null, 'bad error']) {
    const result = await runPushFailure(error, token);
    const serialized = JSON.stringify(result);

    assert.equal(result.succeeded, false);
    assert.equal(result.failureKind, 'unknown');
    assert.equal(result.diagnostic, 'The push failed without exposing remote response details.');
    assert.equal(serialized.includes(token), false);
    assert.equal(serialized.includes('opaque host failure'), false);
  }
});

test('push failure classification does not write host errors or credentials to console', async () => {
  const calls = [];
  const originalMethods = Object.fromEntries(
    ['log', 'info', 'warn', 'error'].map((method) => [method, console[method]]));

  for (const method of Object.keys(originalMethods)) {
    console[method] = (...args) => calls.push([method, ...args]);
  }

  try {
    await runPushFailure(new Error('Authentication failed for a hidden credential'));
  } finally {
    for (const [method, implementation] of Object.entries(originalMethods)) {
      console[method] = implementation;
    }
  }

  assert.deepEqual(calls, []);
});

test('Git-Bug Resource exports normalize missing workspace and excessive-ref failures without object reads', async () => {
  resetBrowserGlobals();
  installLoadingDocument();
  let objectReadCount = 0;
  installFakeGitRuntime({
    async listRefs() { return Array.from({ length: 1001 }, (_, index) => index.toString(16).padStart(64, '0')); },
    async readCommit() { objectReadCount += 1; assert.fail('Excessive refs must fail before commit reads.'); },
    async readTree() { objectReadCount += 1; assert.fail('Excessive refs must fail before tree reads.'); },
    async readBlob() { objectReadCount += 1; assert.fail('Excessive refs must fail before blob reads.'); }
  });

  const engine = await importFreshModule();
  const missingWorkspace = await engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 50 });
  assert.equal(missingWorkspace.failureKind, 'unknown');
  assert.equal(missingWorkspace.diagnostic, null);

  assert.equal((await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'git-bug-limits'
  })).succeeded, true);
  const excessiveRefs = await engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 50 });
  assert.equal(excessiveRefs.failureKind, 'gitBugDataMalformed');
  assert.equal(excessiveRefs.value, null);
  assert.equal(excessiveRefs.diagnostic, null);
  assert.equal(objectReadCount, 0);
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
  assert.match(serviceSource, /SanitizePushFailure\(RedactCredential\(result, personalAccessToken\)\)/);
  for (const [wireKind, typedKind] of [
    ['credentialRejected', 'CredentialRejected'],
    ['remoteAhead', 'RemoteAhead'],
    ['networkUnavailable', 'NetworkUnavailable'],
    ['unsupportedRef', 'UnsupportedRef'],
    ['unknown', 'Unknown']
  ]) {
    assert.match(serviceSource, new RegExp(`"${wireKind}" => GitOperationFailureKind\\.${typedKind}`));
    assert.match(resultContractSource, new RegExp(`\\b${typedKind}\\b`));
  }
  assert.match(serviceSource, /\[JsonPropertyName\("failureKind"\)\][\s\S]*?string\? FailureKind/);
  assert.match(serviceSource, /SanitizedPushFailure\(result\.FailureKind \?\? GitOperationFailureKind\.Unknown\)/);
  assert.match(serviceSource, /diagnostic,[\s\S]*?failureKind\);/);
  assert.match(serviceSource, /The push failed without exposing remote response details/);
  assert.match(resultContractSource, /GitOperationFailureKind\? FailureKind = null/);
  assert.match(resultContractSource, /enum GitOperationFailureKind[\s\S]*?CredentialRejected/);
  assert.match(serviceSource, /The response value did not match the expected contract/);
});

test('listFiles classifies and sorts entries while sampling the editable-size boundary', async () => {
  const sizes = new Map([
    ['/almtoo-workspaces/coverage-demo/src', null],
    ['/almtoo-workspaces/coverage-demo/README.md', 1024 * 1024],
    ['/almtoo-workspaces/coverage-demo/too-large.txt', 1024 * 1024 + 1],
    ['/almtoo-workspaces/coverage-demo/image.png', 12]
  ]);
  const engine = await openFakeRepository({
    fsOverrides: {
      async readdir() { return ['too-large.txt', 'image.png', 'README.md', 'src']; },
      async stat(path) {
        if (path.endsWith('/.git')) {
          const error = new Error('missing repository metadata');
          error.code = 'ENOENT';
          throw error;
        }
        const size = sizes.get(path);
        return { isFile: () => size !== null, size: size ?? 0 };
      }
    }
  });

  const result = await engine.listFiles('/');

  assert.equal(result.succeeded, true);
  assert.deepEqual(result.value.map(({ name, kind, isEditableText }) => ({ name, kind, isEditableText })), [
    { name: 'src', kind: 'Directory', isEditableText: false },
    { name: 'image.png', kind: 'File', isEditableText: false },
    { name: 'README.md', kind: 'File', isEditableText: true },
    { name: 'too-large.txt', kind: 'File', isEditableText: false }
  ]);
});

test('readTextFile accepts allowlisted extensionless names as repository-relative paths', async () => {
  const engine = await openFakeRepository({
    fsOverrides: {
      async readFile(path) {
        assert.equal(path, '/almtoo-workspaces/coverage-demo/README');
        return new TextEncoder().encode('Hello World!\n');
      }
    }
  });

  const result = await engine.readTextFile('/README');

  assert.equal(result.succeeded, true);
  assert.deepEqual(result.value, {
    path: '/README', content: 'Hello World!\n', encoding: 'utf-8', sizeBytes: 13
  });
});

test('readTextFile and writeTextFile preserve UTF-8 content and normalized repository paths', async () => {
  const writes = [];
  const engine = await openFakeRepository({
    fsOverrides: {
      async readFile(path) {
        assert.equal(path, '/almtoo-workspaces/coverage-demo/docs/notes.txt');
        return new TextEncoder().encode('café');
      },
      async writeFile(path, content, encoding) {
        writes.push({ path, content, encoding });
      }
    }
  });

  const readResult = await engine.readTextFile('/docs/notes.txt');
  const writeResult = await engine.writeTextFile('/docs/notes.txt', 'updated café');

  assert.deepEqual(readResult.value, {
    path: '/docs/notes.txt', content: 'café', encoding: 'utf-8', sizeBytes: 5
  });
  assert.equal(writeResult.succeeded, true);
  assert.deepEqual(writes, [{
    path: '/almtoo-workspaces/coverage-demo/docs/notes.txt', content: 'updated café', encoding: 'utf8'
  }]);
});

test('file operation I/O failures remain structured and operation-specific', async () => {
  const engine = await openFakeRepository({
    fsOverrides: {
      async readdir() { throw new Error('storage unavailable'); },
      async readFile() { throw new Error('read denied'); },
      async writeFile() { throw new Error('quota exceeded'); }
    }
  });

  for (const [operation, result] of [
    ['listFiles', await engine.listFiles('/')],
    ['readTextFile', await engine.readTextFile('/README.md')],
    ['writeTextFile', await engine.writeTextFile('/README.md', 'content')]
  ]) {
    assert.equal(result.operation, operation);
    assert.equal(result.succeeded, false);
    assert.equal(result.value, null);
    assert.ok(result.diagnostic);
  }
});

const syncOids = Object.freeze({
  base: '1111111111111111111111111111111111111111',
  local: '2222222222222222222222222222222222222222',
  remote: '3333333333333333333333333333333333333333',
  divergent: '4444444444444444444444444444444444444444'
});

async function openSynchronizationFixture({ scenario = 'ready', failure = null } = {}) {
  resetBrowserGlobals();
  installLoadingDocument();

  const remoteByScenario = {
    current: syncOids.local,
    ahead: syncOids.base,
    ready: syncOids.remote,
    divergent: syncOids.divergent
  };
  const state = {
    origin: 'https://github.com/octocat/Hello-World.git',
    branch: 'refs/heads/main',
    refs: {
      HEAD: syncOids.local,
      'refs/heads/main': syncOids.local,
      'refs/remotes/origin/main': syncOids.base
    },
    remote: remoteByScenario[scenario],
    files: { '/README.md': 'local readme', '/obsolete.txt': 'obsolete', '/src/app.js': 'old app' },
    fetchCalls: [],
    pullCalls: [],
    flushCalls: 0,
    remoteRefReads: 0
  };
  const parents = {
    [syncOids.local]: syncOids.base,
    [syncOids.remote]: syncOids.local,
    [syncOids.divergent]: syncOids.base,
    [syncOids.base]: null
  };
  const trees = {
    [syncOids.local]: { 'README.md': 'local readme', 'obsolete.txt': 'obsolete', 'src/app.js': 'old app' },
    [syncOids.remote]: { 'README.md': 'remote readme', 'new.txt': 'new', 'src/app.js': 'new app' },
    [syncOids.base]: { 'README.md': 'base-readme' },
    [syncOids.divergent]: { 'README.md': 'divergent-readme', 'other.txt': 'other' }
  };
  const snapshot = () => structuredClone({ refs: state.refs, files: state.files });
  const isDescendent = (oid, ancestor) => {
    for (let current = oid; current; current = parents[current]) {
      if (current === ancestor) return true;
    }
    return false;
  };
  const treeEntry = (tree, filepath) => tree?.[filepath]
    ? { async type() { return 'blob'; }, async oid() { return tree[filepath]; } }
    : undefined;

  installFakeGitRuntime({
    async getConfig() { return state.origin; },
    async currentBranch() { return state.branch; },
    async resolveRef({ ref }) {
      if (ref === 'refs/remotes/origin/main') {
        state.remoteRefReads += 1;
        if (failure === 'remoteRef' && state.remoteRefReads > 1) {
          throw new Error('remote tracking ref could not be resolved');
        }
      }
      if (!Object.hasOwn(state.refs, ref)) throw new Error(`missing ref ${ref}`);
      return state.refs[ref];
    },
    async fetch(options) {
      state.fetchCalls.push(options);
      if (failure === 'fetch') throw new TypeError('Failed to fetch github_pat_test-sentinel');
      state.refs['refs/remotes/origin/main'] = state.remote;
    },
    async writeRef({ ref, value }) { state.refs[ref] = value; },
    async deleteRef({ ref }) { delete state.refs[ref]; },
    async checkout() {
      if (failure === 'checkout') throw new Error('local checkout failed');
      state.refs.HEAD = state.refs['refs/heads/main'];
      state.files = Object.fromEntries(Object.entries(trees[state.refs.HEAD]).map(([path, content]) => [`/${path}`, content]));
    },
    async isDescendent({ oid, ancestor }) { return isDescendent(oid, ancestor); },
    async log({ ref }) {
      const oid = state.refs[ref];
      return [{
        oid,
        commit: { parent: parents[oid] ? [parents[oid]] : [], message: 'Remote changes', author: { name: 'Remote Author', timestamp: 1_700_000_000 } }
      }];
    },
    TREE({ ref }) { return { ref }; },
    async walk({ trees: walkTrees, map }) {
      const [local, remote] = walkTrees.map(({ ref }) => trees[state.refs[ref]]);
      for (const path of ['src/app.js', 'obsolete.txt', 'README.md', 'new.txt']) {
        map(path, [treeEntry(local, path), treeEntry(remote, path)]);
      }
    },
    async pull(options) {
      state.pullCalls.push(options);
      if (failure === 'pull') throw new Error('browser storage failed after download');
      state.refs.HEAD = state.remote;
      state.refs['refs/heads/main'] = state.remote;
      state.files = { '/README.md': 'remote readme', '/new.txt': 'new', '/src/app.js': 'new app' };
    }
  }, {
    async stat(path) {
      if (path.endsWith('/.git')) {
        const error = new Error('missing repository metadata');
        error.code = 'ENOENT';
        throw error;
      }
      return { isFile: () => true, size: 0 };
    },
    async flush() {
      state.flushCalls += 1;
    }
  });

  const engine = await importFreshModule();
  assert.equal((await engine.cloneOrOpen({ repositoryUrl: state.origin, workspaceName: 'synchronization' })).succeeded, true);
  return { engine, state, snapshot };
}

function assertSafeSynchronizationResult(result) {
  const serialized = JSON.stringify(result);
  assert.equal(result.operation, 'synchronize');
  assert.equal(result.diagnostic, null);
  assert.equal(serialized.includes('github_pat_test-sentinel'), false);
  assert.equal(serialized.includes('Failed to fetch'), false);
}

test('synchronize fetch and review classify history without changing the local workspace', async () => {
  for (const [scenario, expectedDecision] of [['current', 'Current'], ['ahead', 'Ahead'], ['ready', 'ReadyToApply'], ['divergent', 'Divergent']]) {
    const { engine, state, snapshot } = await openSynchronizationFixture({ scenario });
    const before = snapshot();
    const result = await engine.synchronize({ repositoryUrl: state.origin, branch: 'main', intent: 'Review' });

    assertSafeSynchronizationResult(result);
    assert.equal(result.succeeded, true);
    assert.equal(result.value.decision, expectedDecision);
    assert.equal(state.fetchCalls.length, 1);
    assert.equal(state.fetchCalls[0].ref, 'main');
    assert.equal(state.fetchCalls[0].singleBranch, true);
    assert.equal(state.fetchCalls[0].depth, 50);
    assert.deepEqual({ HEAD: state.refs.HEAD, local: state.refs['refs/heads/main'], files: state.files }, { HEAD: before.refs.HEAD, local: before.refs['refs/heads/main'], files: before.files });
  }

  const { engine, state, snapshot } = await openSynchronizationFixture({ scenario: 'ready' });
  const before = snapshot();
  const fetched = await engine.synchronize({ repositoryUrl: state.origin, branch: 'main', intent: 'Fetch' });
  assertSafeSynchronizationResult(fetched);
  assert.equal(fetched.value.decision, 'ReadyToApply');
  assert.deepEqual({ HEAD: state.refs.HEAD, local: state.refs['refs/heads/main'], files: state.files }, { HEAD: before.refs.HEAD, local: before.refs['refs/heads/main'], files: before.files });
  assert.equal(state.refs['refs/remotes/origin/main'], syncOids.remote);
});

test('synchronize presents credential-free, sorted incoming metadata before a clean fast-forward apply', async () => {
  const { engine, state, snapshot } = await openSynchronizationFixture({ scenario: 'ready' });
  const beforeReview = snapshot();
  const review = await engine.synchronize({ repositoryUrl: state.origin, branch: 'main', intent: 'Review' });

  assertSafeSynchronizationResult(review);
  assert.equal(review.value.incomingCommit.commitId, syncOids.remote);
  assert.equal(review.value.incomingCommit.parentCommitId, syncOids.local);
  assert.equal(review.value.incomingCommit.authorName, 'Remote Author');
  assert.deepEqual(review.value.changedFiles, [
    { path: '/new.txt', changeKind: 'Added' },
    { path: '/obsolete.txt', changeKind: 'Deleted' },
    { path: '/README.md', changeKind: 'Modified' },
    { path: '/src/app.js', changeKind: 'Modified' }
  ]);
  assert.deepEqual({ HEAD: state.refs.HEAD, local: state.refs['refs/heads/main'], files: state.files }, { HEAD: beforeReview.refs.HEAD, local: beforeReview.refs['refs/heads/main'], files: beforeReview.files });

  const applied = await engine.synchronize({ repositoryUrl: state.origin, branch: 'main', intent: 'Apply' });
  assertSafeSynchronizationResult(applied);
  assert.equal(applied.succeeded, true);
  assert.equal(applied.value.decision, 'Applied');
  assert.equal(state.refs.HEAD, syncOids.remote);
  assert.equal(state.refs['refs/heads/main'], syncOids.remote);
  assert.deepEqual(state.files, { '/README.md': 'remote readme', '/new.txt': 'new', '/src/app.js': 'new app' });
  assert.equal(state.fetchCalls.length, 1, 'Apply must reuse the review fetch rather than perform a second network fetch.');
  assert.equal(state.flushCalls, 1, 'Apply must durably flush browser-local metadata before reporting success.');
});

test('synchronize rejects malformed, mismatched, failed, and non-fast-forward paths without local workspace mutation', async () => {
  const rejectedRequests = [
    { request: { repositoryUrl: '', branch: 'main', intent: 'Review' } },
    { request: { repositoryUrl: 'https://github.com/octocat/Elsewhere.git', branch: 'main', intent: 'Review' } },
    { request: { repositoryUrl: 'https://github.com/octocat/Hello-World.git', branch: 'other', intent: 'Review' } }
  ];
  for (const { request } of rejectedRequests) {
    const { engine, snapshot } = await openSynchronizationFixture();
    const before = snapshot();
    const result = await engine.synchronize(request);
    assertSafeSynchronizationResult(result);
    assert.equal(result.succeeded, false);
    assert.deepEqual(snapshot(), before);
  }

  for (const failure of ['fetch', 'checkout']) {
    const { engine, state, snapshot } = await openSynchronizationFixture({ failure });
    const before = snapshot();
    const result = await engine.synchronize({ repositoryUrl: state.origin, branch: 'main', intent: failure === 'checkout' ? 'Apply' : 'Review' });
    assertSafeSynchronizationResult(result);
    assert.equal(result.succeeded, false);
    assert.deepEqual(snapshot(), before);
  }

  const { engine, state, snapshot } = await openSynchronizationFixture({ failure: 'remoteRef' });
  const before = snapshot();
  const unresolved = await engine.synchronize({ repositoryUrl: state.origin, branch: 'main', intent: 'Review' });
  assertSafeSynchronizationResult(unresolved);
  assert.equal(unresolved.succeeded, false);
  assert.deepEqual(snapshot(), before);

  for (const scenario of ['current', 'ahead', 'divergent']) {
    const fixture = await openSynchronizationFixture({ scenario });
    const before = fixture.snapshot();
    const result = await fixture.engine.synchronize({ repositoryUrl: fixture.state.origin, branch: 'main', intent: 'Apply' });
    assertSafeSynchronizationResult(result);
    assert.equal(result.succeeded, false);
    assert.equal(result.failureKind, 'remoteAhead');
    assert.deepEqual({ HEAD: fixture.state.refs.HEAD, local: fixture.state.refs['refs/heads/main'], files: fixture.state.files }, { HEAD: before.refs.HEAD, local: before.refs['refs/heads/main'], files: before.files });
  }
});
