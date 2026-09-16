import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const moduleUrl = new URL('../wwwroot/js/browserGitEngine.js', import.meta.url);
const serviceUrl = new URL('../Services/Git/BrowserGitService.cs', import.meta.url);

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
    'commit'
  ]) {
    assert.match(moduleSource, new RegExp(`export async function ${operation}\\b`));
    assert.match(serviceSource, new RegExp(`\\"${operation}\\"`));
  }

  assert.match(serviceSource, /catch \(JSException exception\)/);
  assert.match(serviceSource, /GitOperationResult<RepositoryInfo>\.Failure/);
  assert.match(serviceSource, /GitOperationResult<CommitInfo>\.Failure/);
});
