import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { cp, mkdtemp, readFile, readdir, rm, unlink } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, test } from 'node:test';

import git from 'isomorphic-git';

const sourceFixture = fileURLToPath(new URL('./fixtures/gitBug/v0.11.0', import.meta.url));
const fixtureParent = fileURLToPath(new URL('./fixtures/gitBug/', import.meta.url));
const temporaryFixtures = [];
const supportedMarker = 'version-4';
const safeCategories = new Set(['supported', 'metadata-absent', 'malformed', 'unsupported-format']);

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

function malformed() {
  return { category: 'malformed', supportedFormatVersion: null, issues: null };
}

async function readJsonBlob(gitdir, oid) {
  const { blob } = await git.readBlob({ fs, gitdir, oid });
  return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(blob));
}

async function readIdentities(gitdir) {
  const identities = new Map();
  for (const id of await git.listRefs({ fs, gitdir, filepath: 'refs/identities' })) {
    const oid = await git.resolveRef({ fs, gitdir, ref: `refs/identities/${id}` });
    const { commit } = await git.readCommit({ fs, gitdir, oid });
    const { tree } = await git.readTree({ fs, gitdir, oid: commit.tree });
    const version = tree.find(entry => entry.path === 'version' && entry.type === 'blob');
    if (!version) throw new Error('identity-version-absent');
    const value = await readJsonBlob(gitdir, version.oid);
    if (value.version !== 2 || typeof value.name !== 'string') throw new Error('identity-version-invalid');
    identities.set(id, value.name);
  }
  return identities;
}

async function readIssue(gitdir, id, identities) {
  const tip = await git.resolveRef({ fs, gitdir, ref: `refs/bugs/${id}` });
  const commits = [];
  let oid = tip;
  const visited = new Set();
  while (oid) {
    if (visited.has(oid)) throw new Error('cyclic-history');
    visited.add(oid);
    const { commit } = await git.readCommit({ fs, gitdir, oid });
    if (commit.parent.length > 1) throw new Error('non-linear-history');
    commits.unshift(commit);
    oid = commit.parent[0];
  }

  let title;
  let description;
  let authorDisplayName = null;
  let state = 'open';
  let labels = null;
  const comments = [];

  for (const [index, commit] of commits.entries()) {
    const { tree } = await git.readTree({ fs, gitdir, oid: commit.tree });
    const markers = tree.filter(entry => /^version-\d+$/.test(entry.path));
    if (markers.length !== 1) throw new Error('format-marker-invalid');
    if (markers[0].path !== supportedMarker) {
      return { unsupported: true };
    }
    const opsEntry = tree.find(entry => entry.path === 'ops' && entry.type === 'blob');
    if (!opsEntry) throw new Error('ops-absent');
    const payload = await readJsonBlob(gitdir, opsEntry.oid);
    if (!payload?.author || !Array.isArray(payload.ops)) throw new Error('ops-envelope-invalid');
    const actorId = payload.author.id;
    if (typeof actorId !== 'string' || !identities.has(actorId)) throw new Error('author-invalid');

    for (const operation of payload.ops) {
      if (!operation || !Number.isInteger(operation.type) || !Number.isFinite(operation.timestamp)) {
        throw new Error('operation-invalid');
      }
      if (operation.type === 1 && index === 0 && typeof operation.title === 'string' && typeof operation.message === 'string') {
        title = operation.title;
        description = operation.message;
        authorDisplayName = identities.get(actorId);
        comments.push({ authorDisplayName, body: operation.message, createdAt: operation.timestamp });
      } else if (operation.type === 3 && typeof operation.message === 'string') {
        comments.push({ authorDisplayName: identities.get(actorId), body: operation.message, createdAt: operation.timestamp });
      } else if (operation.type === 4 && operation.status === 2) {
        state = 'closed';
      } else if (operation.type === 5 && Array.isArray(operation.added) && (operation.removed === null || Array.isArray(operation.removed))) {
        labels ??= [];
        labels = labels.filter(label => !(operation.removed ?? []).includes(label));
        for (const label of operation.added) if (typeof label === 'string' && !labels.includes(label)) labels.push(label);
      } else {
        throw new Error('operation-unsupported');
      }
    }
  }

  if (typeof title !== 'string' || typeof description !== 'string') throw new Error('creation-absent');
  return { id, title, state, labels, authorDisplayName, description, comments };
}

async function discoverGitBugFixture(root) {
  const gitdir = path.join(root, 'repository');
  try {
    const issueIds = await git.listRefs({ fs, gitdir, filepath: 'refs/bugs' });
    if (issueIds.length === 0) {
      return { category: 'metadata-absent', supportedFormatVersion: null, issues: null };
    }
    const identities = await readIdentities(gitdir);
    const issues = [];
    for (const id of issueIds.sort()) {
      const issue = await readIssue(gitdir, id, identities);
      if (issue.unsupported) {
        return { category: 'unsupported-format', supportedFormatVersion: null, issues: null };
      }
      issues.push(issue);
    }
    return { category: 'supported', supportedFormatVersion: 4, issues };
  } catch {
    return malformed();
  }
}

async function copyFixture() {
  const destination = await mkdtemp(path.join(fixtureParent, '.discovery-'));
  temporaryFixtures.push(destination);
  await cp(sourceFixture, destination, { recursive: true });
  return destination;
}

async function makeUnsupported(root) {
  const gitdir = path.join(root, 'repository');
  const id = (await git.listRefs({ fs, gitdir, filepath: 'refs/bugs' })).sort()[0];
  const ref = `refs/bugs/${id}`;
  const parent = await git.resolveRef({ fs, gitdir, ref });
  const ops = await git.writeBlob({ fs, gitdir, blob: new TextEncoder().encode(JSON.stringify({ author: { id: (await git.listRefs({ fs, gitdir, filepath: 'refs/identities' }))[0] }, ops: [] })) });
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
}

afterEach(async () => {
  await Promise.all(temporaryFixtures.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

test('browser-local Git stack discovers v0.11.0 metadata and fixture-proven read fields without writes', async () => {
  const before = await snapshot(sourceFixture);
  const result = await discoverGitBugFixture(sourceFixture);
  const after = await snapshot(sourceFixture);
  const expectedIds = JSON.parse(await readFile(path.join(sourceFixture, 'expected', 'scenario-ids.json'), 'utf8'));

  assert.equal(result.category, 'supported');
  assert.equal(result.supportedFormatVersion, 4);
  assert.equal(result.issues.length, 3);
  for (const [scenario, humanId] of Object.entries(expectedIds)) {
    const expected = JSON.parse(await readFile(path.join(sourceFixture, 'expected', 'details', `${humanId}.json`), 'utf8'));
    const actual = result.issues.find(issue => issue.id === expected.id);
    assert.ok(actual, `${scenario} must be discovered`);
    assert.deepEqual({
      title: actual.title,
      state: actual.state,
      labels: actual.labels,
      authorDisplayName: actual.authorDisplayName,
      description: actual.description,
      comments: actual.comments.map(comment => comment.body)
    }, {
      title: expected.title,
      state: expected.status,
      labels: expected.labels,
      authorDisplayName: expected.author.name,
      description: expected.comments[0].message,
      comments: expected.comments.map(comment => comment.message)
    });
  }
  assert.deepEqual(after, before);
});

test('discovery classifies metadata-absent repositories without writes', async () => {
  const fixture = await copyFixture();
  await rm(path.join(fixture, 'repository', 'refs', 'bugs'), { recursive: true });
  const before = await snapshot(fixture);
  const result = await discoverGitBugFixture(fixture);
  assert.equal(result.category, 'metadata-absent');
  assert.equal(safeCategories.has(result.category), true);
  assert.deepEqual(await snapshot(fixture), before);
});

test('discovery classifies malformed object storage without leaking partial issue data or writing', async () => {
  const fixture = await copyFixture();
  const gitdir = path.join(fixture, 'repository');
  const id = (await git.listRefs({ fs, gitdir, filepath: 'refs/bugs' }))[0];
  const oid = await git.resolveRef({ fs, gitdir, ref: `refs/bugs/${id}` });
  await unlink(path.join(gitdir, 'objects', oid.slice(0, 2), oid.slice(2)));
  const before = await snapshot(fixture);
  const result = await discoverGitBugFixture(fixture);
  assert.deepEqual(result, { category: 'malformed', supportedFormatVersion: null, issues: null });
  assert.deepEqual(await snapshot(fixture), before);
});

test('discovery rejects an intentionally incompatible version marker without partial data or writes', async () => {
  const fixture = await copyFixture();
  await makeUnsupported(fixture);
  const before = await snapshot(fixture);
  const result = await discoverGitBugFixture(fixture);
  assert.deepEqual(result, { category: 'unsupported-format', supportedFormatVersion: null, issues: null });
  assert.deepEqual(await snapshot(fixture), before);
});
