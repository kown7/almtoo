import assert from 'node:assert/strict';
import { test } from 'node:test';

const moduleUrl = new URL('../wwwroot/js/browserGitEngine.js', import.meta.url);

async function importFreshModule() {
  return import(`${moduleUrl.href}?publication=${Date.now()}-${Math.random()}`);
}

function installLoadingDocument() {
  globalThis.document = {
    head: {
      appendChild(script) {
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
}

function installFakeRuntime(gitOverrides = {}) {
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
        async readdir() { return []; },
        async readFile() { return new Uint8Array(); },
        async writeFile() {},
        async flush() {}
      };
    }
  }

  globalThis.LightningFS = FakeLightningFS;
  globalThis.GitHttp = { request() {} };
  globalThis.git = {
    async clone() {},
    async fetch() { return { fetchHead: 'a'.repeat(40) }; },
    async writeRef() {},
    async resolveRef() { return 'a'.repeat(40); },
    async listServerRefs() { return []; },
    async listRefs() { return []; },
    async getConfig() { return 'https://github.com/octocat/Hello-World.git'; },
    ...gitOverrides
  };
}

async function openRepository(gitOverrides = {}) {
  delete globalThis.document;
  delete globalThis.LightningFS;
  delete globalThis.git;
  delete globalThis.GitHttp;
  installLoadingDocument();
  installFakeRuntime(gitOverrides);
  const engine = await importFreshModule();
  const opened = await engine.cloneOrOpen({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    workspaceName: 'publication-resource'
  });
  assert.equal(opened.succeeded, true);
  return engine;
}

function publicationRequest(overrides = {}) {
  const refName = `refs/bugs/${'b'.repeat(64)}`;
  const identityRefName = `refs/identities/${'e'.repeat(64)}`;
  const oldObjectId = 'c'.repeat(64);
  const identityOldObjectId = 'f'.repeat(64);
  return {
    reviewId: 'review-17',
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    branch: 'main',
    sourceRevision: 'a'.repeat(40),
    updates: [{
      refName,
      namespace: 'GitBug',
      kind: 'Update',
      newObjectId: 'd'.repeat(64),
      expectedOldObjectId: oldObjectId
    }, {
      refName: identityRefName,
      namespace: 'GitBug',
      kind: 'Update',
      newObjectId: 'a'.repeat(64),
      expectedOldObjectId: identityOldObjectId
    }],
    remotePreconditions: [
      { refName, expectedObjectId: oldObjectId },
      { refName: identityRefName, expectedObjectId: identityOldObjectId }
    ],
    ...overrides
  };
}

test('inspectGitBugRemote passively lists only supported Git-Bug refs', async () => {
  const issueId = 'b'.repeat(64);
  const identityId = 'e'.repeat(64);
  let publicationPhase = false;
  const calls = [];
  const engine = await openRepository({
    async listServerRefs(options) {
      calls.push(options);
      if (!publicationPhase) return [];
      return options.prefix === 'refs/bugs/'
        ? [{ ref: `refs/bugs/${issueId}`, oid: 'd'.repeat(64) }]
        : [{ ref: `refs/identities/${identityId}`, oid: 'f'.repeat(64) }];
    }
  });

  publicationPhase = true;
  const result = await engine.inspectGitBugRemote({
    repositoryUrl: 'https://github.com/octocat/Hello-World.git',
    branch: 'main'
  });

  assert.equal(result.succeeded, true);
  assert.equal(result.operation, 'gitBug.publication.inspectRemote');
  assert.deepEqual(result.value.refs, [
    { refName: `refs/bugs/${issueId}`, namespace: 'GitBug', objectId: 'd'.repeat(64) },
    { refName: `refs/identities/${identityId}`, namespace: 'GitBug', objectId: 'f'.repeat(64) }
  ]);
  assert.deepEqual(result.value.credential, { available: false, used: false });
  assert.equal(calls.at(-2).onAuth, undefined);
  assert.equal(calls.at(-1).onAuth, undefined);
});

test('publishGitBug revalidates remote refs and pushes only exact reviewed non-force refs', async () => {
  const request = publicationRequest();
  const remoteCalls = [];
  const pushes = [];
  const token = 'test-token-that-must-not-escape';
  const engine = await openRepository({
    async listServerRefs(options) {
      remoteCalls.push(options);
      return options.prefix === 'refs/bugs/'
        ? [{ ref: request.updates[0].refName, oid: request.remotePreconditions[0].expectedObjectId }]
        : [{ ref: request.updates[1].refName, oid: request.remotePreconditions[1].expectedObjectId }];
    },
    async push(options) {
      pushes.push({ ...options, auth: options.onAuth() });
    }
  });

  const result = await engine.publishGitBug(request, token);

  assert.equal(result.succeeded, true);
  assert.deepEqual(result.value.appliedUpdates, request.updates);
  assert.deepEqual(result.value.credential, { available: true, used: true });
  assert.equal(result.value.boundedUpdateCount, 2);
  assert.equal(pushes.length, 2);
  assert.deepEqual(pushes.map(push => [push.ref, push.remoteRef, push.force]), request.updates.map(update => [update.refName, update.refName, false]));
  assert.equal(pushes[0].auth.password, token);
  assert.equal(remoteCalls.at(-2).onAuth instanceof Function, true);
  assert.equal(remoteCalls.at(-1).onAuth instanceof Function, true);
  assert.doesNotMatch(JSON.stringify(result), new RegExp(token));
});

test('publishGitBug rejects tampered plans and stale refs without transport mutation', async () => {
  let pushCalls = 0;
  const engine = await openRepository({
    async listServerRefs(options) {
      return options.prefix === 'refs/bugs/'
        ? [{ ref: `refs/bugs/${'b'.repeat(64)}`, oid: 'c'.repeat(64) }]
        : [{ ref: `refs/identities/${'e'.repeat(64)}`, oid: 'f'.repeat(64) }];
    },
    async push() { pushCalls += 1; }
  });

  const unrelated = publicationRequest({
    updates: [{
      ...publicationRequest().updates[0],
      refName: 'refs/heads/main'
    }]
  });
  const invalid = await engine.publishGitBug(unrelated, 'token');
  assert.equal(invalid.succeeded, false);
  assert.equal(invalid.failureKind, 'gitBugPublicationInvalidRequest');
  assert.equal(pushCalls, 0);

  const noIdentity = publicationRequest({
    updates: [publicationRequest().updates[0]],
    remotePreconditions: [publicationRequest().remotePreconditions[0]]
  });
  const missingIdentity = await engine.publishGitBug(noIdentity, 'token');
  assert.equal(missingIdentity.succeeded, false);
  assert.equal(missingIdentity.failureKind, 'gitBugPublicationInvalidRequest');
  assert.equal(pushCalls, 0);

  const baseRequest = publicationRequest();
  const staleRequest = publicationRequest({
    updates: [{
      ...baseRequest.updates[0],
      expectedOldObjectId: '1'.repeat(64)
    }, baseRequest.updates[1]],
    remotePreconditions: [{
      refName: baseRequest.updates[0].refName,
      expectedObjectId: '1'.repeat(64)
    }, baseRequest.remotePreconditions[1]]
  });
  const stale = await engine.publishGitBug(staleRequest, 'token');
  assert.equal(stale.succeeded, false);
  assert.equal(stale.failureKind, 'gitBugPublicationStaleRemote');
  assert.equal(stale.diagnostic, null);
  assert.equal(pushCalls, 0);
});

test('publishGitBug maps auth, network, cancellation, and rejection failures without raw diagnostics', async () => {
  const request = publicationRequest();
  const cases = [
    [Object.assign(new Error('private token leaked'), { status: 401 }), 'credentialRejected'],
    [Object.assign(new Error('private token leaked'), { code: 'ENETUNREACH' }), 'networkUnavailable'],
    [Object.assign(new Error('private token leaked'), { name: 'AbortError' }), 'gitBugPublicationCancelled'],
    [new Error('private token leaked'), 'gitBugPublicationRemoteRejected']
  ];

  for (const [error, expectedKind] of cases) {
    const engine = await openRepository({
      async listServerRefs(options) {
        if (options.prefix === 'refs/bugs/') {
          return [{ ref: request.updates[0].refName, oid: request.remotePreconditions[0].expectedObjectId }];
        }
        return [{ ref: request.updates[1].refName, oid: request.remotePreconditions[1].expectedObjectId }];
      },
      async push() { throw error; }
    });
    const result = await engine.publishGitBug(request, 'secret-token');
    assert.equal(result.succeeded, false);
    assert.equal(result.failureKind, expectedKind);
    assert.equal(result.diagnostic, null);
    assert.doesNotMatch(JSON.stringify(result), /secret-token|private token leaked/);
  }
});
