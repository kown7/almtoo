import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { cp, mkdtemp, readFile, readdir, rm, unlink } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, test } from 'node:test';

import git from 'isomorphic-git';

const moduleUrl = new URL('../wwwroot/js/browserGitEngine.js', import.meta.url);
const sourceFixture = fileURLToPath(new URL('./fixtures/gitBug/v0.11.0', import.meta.url));
const fixtureParent = fileURLToPath(new URL('./fixtures/gitBug/', import.meta.url));
const temporaryFixtures = [];
const readOperations = new Set(['listRefs', 'resolveRef', 'readCommit', 'readTree', 'readBlob', 'currentBranch']);
const forbiddenOperations = new Set([
  'add', 'checkout', 'clone', 'commit', 'deleteRef', 'fetch', 'merge', 'pull', 'push',
  'remove', 'writeBlob', 'writeCommit', 'writeRef', 'writeTree'
]);

async function filesBelow(root, prefix = '') {
  const files = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const absolute = path.join(root, entry.name);
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) files.push(...await filesBelow(absolute, relative));
    else if (entry.isFile()) files.push({ absolute, relative });
  }
  return files.sort((left, right) => left.relative.localeCompare(right.relative));
}

async function snapshot(root) {
  return Object.fromEntries(await Promise.all((await filesBelow(root)).map(async file => [
    file.relative,
    createHash('sha256').update(await readFile(file.absolute)).digest('hex')
  ])));
}

async function copyFixture() {
  const destination = await mkdtemp(path.join(fixtureParent, '.resource-'));
  temporaryFixtures.push(destination);
  await cp(sourceFixture, destination, { recursive: true });
  return destination;
}

async function makeUnsupported(root) {
  const gitdir = path.join(root, 'repository');
  const id = (await git.listRefs({ fs, gitdir, filepath: 'refs/bugs' })).sort()[0];
  const ref = `refs/bugs/${id}`;
  const parent = await git.resolveRef({ fs, gitdir, ref });
  const identity = (await git.listRefs({ fs, gitdir, filepath: 'refs/identities' }))[0];
  const ops = await git.writeBlob({ fs, gitdir, blob: new TextEncoder().encode(JSON.stringify({ author: { id: identity }, ops: [] })) });
  const marker = await git.writeBlob({ fs, gitdir, blob: new Uint8Array() });
  const tree = await git.writeTree({ fs, gitdir, tree: [
    { mode: '100644', path: 'ops', oid: ops, type: 'blob' },
    { mode: '100644', path: 'version-5', oid: marker, type: 'blob' }
  ] });
  const oid = await git.writeCommit({ fs, gitdir, commit: {
    tree, parent: [parent], message: '',
    author: { name: 'fixture', email: 'fixture@example.invalid', timestamp: 1, timezoneOffset: 0 },
    committer: { name: 'fixture', email: 'fixture@example.invalid', timestamp: 1, timezoneOffset: 0 }
  } });
  await git.writeRef({ fs, gitdir, ref, value: oid, force: true });
  return id;
}

function resetGlobals() {
  delete globalThis.document;
  delete globalThis.LightningFS;
  delete globalThis.git;
  delete globalThis.GitHttp;
}

async function openFixture(root) {
  resetGlobals();
  const gitdir = path.join(root, 'repository');
  const calls = [];
  let networkRequests = 0;

  class FixtureFilesystem {
    constructor() {
      this.promises = {
        async mkdir() {},
        async stat(candidate) {
          assert.ok(candidate.endsWith('/.git'), 'Git-Bug reads must not inspect worktree paths.');
          return { isFile: () => false, size: 0 };
        }
      };
    }
  }

  const runtime = {};
  for (const operation of readOperations) {
    runtime[operation] = async (options) => {
      calls.push(operation);
      if (operation === 'currentBranch') return null;
      return git[operation]({ ...options, fs, gitdir, dir: undefined });
    };
  }
  for (const operation of forbiddenOperations) {
    runtime[operation] = async () => assert.fail(`${operation} must not run during a Git-Bug read.`);
  }

  globalThis.document = { querySelector: () => ({ dataset: { loaded: 'true' } }) };
  globalThis.LightningFS = FixtureFilesystem;
  globalThis.git = runtime;
  globalThis.GitHttp = { request() { networkRequests += 1; assert.fail('Git-Bug reads must not use network transport.'); } };

  const engine = await import(`${moduleUrl.href}?case=${Date.now()}-${Math.random()}`);
  const opened = await engine.cloneOrOpen({
    repositoryUrl: 'https://example.invalid/fixture.git',
    workspaceName: 'git-bug-resource'
  });
  assert.equal(opened.succeeded, true);
  calls.length = 0;
  return { engine, calls, get networkRequests() { return networkRequests; } };
}

function assertSafeFailure(result, expectedKind) {
  assert.equal(result.succeeded, false);
  assert.equal(result.failureKind, expectedKind);
  assert.equal(result.value, null);
  assert.equal(result.diagnostic, null);
  assert.deepEqual(Object.keys(result).sort(), ['diagnostic', 'failureKind', 'message', 'operation', 'succeeded', 'value']);
}

async function assertUnchangedRead(root, action) {
  const before = await snapshot(root);
  const runtime = await openFixture(root);
  const result = await action(runtime.engine);
  assert.deepEqual(await snapshot(root), before);
  assert.equal(runtime.networkRequests, 0);
  assert.ok(runtime.calls.length > 0);
  assert.equal(runtime.calls.every(operation => readOperations.has(operation)), true);
  return result;
}

afterEach(async () => {
  resetGlobals();
  await Promise.all(temporaryFixtures.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

test('Resource exports decode fixture-proven list and detail DTOs without network or Git mutation', async () => {
  const before = await snapshot(sourceFixture);
  const runtime = await openFixture(sourceFixture);
  const expectedIds = JSON.parse(await readFile(path.join(sourceFixture, 'expected', 'scenario-ids.json'), 'utf8'));

  const listed = await runtime.engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 100 });
  assert.equal(listed.operation, 'gitBug.listIssues');
  assert.equal(listed.succeeded, true);
  assert.equal(listed.value.totalCount, 3);
  assert.equal(listed.value.nextCursor, null);
  assert.equal(listed.value.issues.length, 3);

  for (const [scenario, humanId] of Object.entries(expectedIds)) {
    const expected = JSON.parse(await readFile(path.join(sourceFixture, 'expected', 'details', `${humanId}.json`), 'utf8'));
    const summary = listed.value.issues.find(issue => issue.id === expected.id);
    assert.ok(summary, `${scenario} must be returned by the Resource export.`);
    assert.deepEqual(summary, {
      id: expected.id,
      title: expected.title,
      state: expected.status,
      labels: expected.labels,
      authorDisplayName: expected.author.name
    });

    const detailed = await runtime.engine.getGitBugIssue(expected.id);
    assert.equal(detailed.operation, 'gitBug.getIssue');
    assert.equal(detailed.succeeded, true);
    assert.deepEqual(detailed.value, {
      summary,
      description: expected.comments[0].message,
      comments: expected.comments.map(comment => ({
        authorDisplayName: expected.author.name,
        body: comment.message
      }))
    });
  }

  const filtered = await runtime.engine.listGitBugIssues({ state: 'closed', searchText: null, cursor: null, pageSize: 1 });
  assert.equal(filtered.succeeded, true);
  assert.equal(filtered.value.totalCount, 1);
  assert.equal(filtered.value.issues[0].state, 'closed');

  const firstPage = await runtime.engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 1 });
  assert.equal(firstPage.value.issues.length, 1);
  assert.equal(firstPage.value.nextCursor, '1');
  const secondPage = await runtime.engine.listGitBugIssues({ state: null, searchText: null, cursor: firstPage.value.nextCursor, pageSize: 1 });
  assert.equal(secondPage.value.issues.length, 1);
  assert.equal(secondPage.value.nextCursor, '2');
  assert.notEqual(firstPage.value.issues[0].id, secondPage.value.issues[0].id);
  assert.deepEqual(await snapshot(sourceFixture), before);
  assert.equal(runtime.networkRequests, 0);
  assert.equal(runtime.calls.every(operation => readOperations.has(operation)), true);
});

test('Resource exports classify missing Git-Bug metadata with fixed safe fields and no writes', async () => {
  const fixture = await copyFixture();
  await rm(path.join(fixture, 'repository', 'refs', 'bugs'), { recursive: true });
  const results = await assertUnchangedRead(fixture, async engine => [
    await engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 50 }),
    await engine.getGitBugIssue('0'.repeat(64))
  ]);
  for (const result of results) assertSafeFailure(result, 'gitBugDataUnavailable');
});

test('Resource exports classify unreadable referenced objects as malformed without partial data or writes', async () => {
  const fixture = await copyFixture();
  const gitdir = path.join(fixture, 'repository');
  const id = (await git.listRefs({ fs, gitdir, filepath: 'refs/bugs' }))[0];
  const oid = await git.resolveRef({ fs, gitdir, ref: `refs/bugs/${id}` });
  await unlink(path.join(gitdir, 'objects', oid.slice(0, 2), oid.slice(2)));

  const results = await assertUnchangedRead(fixture, async engine => [
    await engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 50 }),
    await engine.getGitBugIssue(id)
  ]);
  for (const result of results) assertSafeFailure(result, 'gitBugDataMalformed');
});

test('Resource exports classify an incompatible version without partial data or writes', async () => {
  const fixture = await copyFixture();
  const id = await makeUnsupported(fixture);
  const results = await assertUnchangedRead(fixture, async engine => [
    await engine.listGitBugIssues({ state: null, searchText: null, cursor: null, pageSize: 50 }),
    await engine.getGitBugIssue(id)
  ]);
  for (const result of results) assertSafeFailure(result, 'gitBugFormatUnsupported');
});

test('Resource exports reject malformed and oversized bridge requests before Git object reads', async () => {
  const before = await snapshot(sourceFixture);
  const runtime = await openFixture(sourceFixture);
  const requests = [
    null,
    { state: 'unknown', searchText: null, cursor: null, pageSize: 50 },
    { state: null, searchText: '', cursor: null, pageSize: 50 },
    { state: null, searchText: 'x'.repeat(257), cursor: null, pageSize: 50 },
    { state: null, searchText: null, cursor: 'not-a-cursor', pageSize: 50 },
    { state: null, searchText: null, cursor: null, pageSize: 101 }
  ];
  for (const request of requests) {
    assertSafeFailure(await runtime.engine.listGitBugIssues(request), 'gitBugDataMalformed');
  }
  assertSafeFailure(await runtime.engine.getGitBugIssue('not-an-id'), 'gitBugDataMalformed');
  assert.deepEqual(runtime.calls, []);
  assert.deepEqual(await snapshot(sourceFixture), before);
});
