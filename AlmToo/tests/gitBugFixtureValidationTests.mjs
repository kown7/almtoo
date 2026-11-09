import assert from 'node:assert/strict';
import { cp, mkdtemp, readFile, rm, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, test } from 'node:test';

import { validateFixture } from './fixtures/gitBug/validate-fixture.mjs';
import { startControlledGitBugRemote } from '../e2e/controlledGitBugRemote.mjs';

const sourceFixture = fileURLToPath(new URL('./fixtures/gitBug/v0.11.0', import.meta.url));
const fixtureParent = fileURLToPath(new URL('./fixtures/gitBug/', import.meta.url));
const temporaryFixtures = [];

async function copyFixture() {
  const destination = await mkdtemp(path.join(fixtureParent, '.validation-'));
  temporaryFixtures.push(destination);
  await cp(sourceFixture, destination, { recursive: true });
  return destination;
}

async function rejectsWithCategory(action, category) {
  await assert.rejects(action, error => {
    assert.equal(error.category, category);
    assert.equal(error.message, `Git-Bug fixture validation failed: ${category}.`);
    assert.doesNotMatch(error.message, /refs\/|objects\/|café|comment|[a-f0-9]{40}/i);
    return true;
  });
}

afterEach(async () => {
  await Promise.all(temporaryFixtures.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

test('pinned Git-Bug fixture satisfies its scenarios and remains unchanged', async () => {
  const result = await validateFixture(sourceFixture);

  assert.deepEqual(result, {
    release: 'v0.11.0',
    scenarioCount: 3,
    issueRefCount: 3,
    identityRefCount: 1,
    objectCount: 31,
    repositoryState: 'unchanged'
  });
});

test('fixture validation fails safely when required repository data is absent', async () => {
  const fixture = await copyFixture();
  const manifest = JSON.parse(await readFile(path.join(fixture, 'fixture-manifest.json'), 'utf8'));
  const objectPath = Object.keys(manifest.dataFileSha256).find(name => name.startsWith('repository/objects/'));
  await unlink(path.join(fixture, objectPath));

  await rejectsWithCategory(() => validateFixture(fixture), 'fixture-contract-mismatch');
});

test('fixture validation fails safely when the manifest is malformed', async () => {
  const fixture = await copyFixture();
  await writeFile(path.join(fixture, 'fixture-manifest.json'), '{ malformed');

  await rejectsWithCategory(() => validateFixture(fixture), 'malformed-fixture-data');
});

test('controlled remote exposes smart HTTP and redacts credentials from failures', async () => {
  const remote = await startControlledGitBugRemote({ username: 'acceptance-user', password: 'acceptance-secret', failureMode: 'auth' });
  try {
    const advertisement = await fetch(`${remote.url}/info/refs?service=git-upload-pack`);
    assert.equal(advertisement.status, 200);
    assert.match(advertisement.headers.get('content-type'), /git-upload-pack/);

    const failedPush = await fetch(`${remote.url}/git-receive-pack`, { method: 'POST', body: 'test' });
    const message = await failedPush.text();
    assert.equal(failedPush.status, 401);
    assert.doesNotMatch(message, /acceptance-secret/);
    assert.doesNotMatch(remote.url, /acceptance-secret/);
    assert.equal((await remote.snapshot()).repository, 'git-bug-fixture.git');
  } finally {
    await remote.close();
    await remote.close();
  }
});

test('controlled remote rejects non-function repository seed hooks before serving', async () => {
  await assert.rejects(
    () => startControlledGitBugRemote({ seedRepository: 'not-a-callback' }),
    error => {
      assert.equal(error.name, 'TypeError');
      assert.match(error.message, /seedRepository must be a function/);
      return true;
    }
  );
});

test('controlled remote supports explicit failure transitions and cleanup', async () => {
  const remote = await startControlledGitBugRemote();
  try {
    remote.setFailureMode('non-fast-forward');
    const authorization = `Basic ${Buffer.from(`${remote.credentials.username}:${remote.credentials.password}`).toString('base64')}`;
    const response = await fetch(`${remote.url}/git-receive-pack`, {
      method: 'POST',
      headers: { authorization },
      body: 'test'
    });
    assert.equal(response.status, 403);
    remote.clearFailureMode();
    assert.equal((await remote.snapshot()).requestCount, 1);
  } finally {
    await remote.close();
  }
});

test('fixture validation fails safely when expected issue data is changed', async () => {
  const fixture = await copyFixture();
  const issuesPath = path.join(fixture, 'expected', 'issues.json');
  const issues = JSON.parse(await readFile(issuesPath, 'utf8'));
  issues[0].status = 'unknown';
  await writeFile(issuesPath, `${JSON.stringify(issues, null, 2)}\n`);

  await rejectsWithCategory(() => validateFixture(fixture), 'fixture-contract-mismatch');
});

test('fixture validation requires both Git-Bug ref namespaces in the pinned repository state', async () => {
  const fixture = await copyFixture();
  const manifestPath = path.join(fixture, 'fixture-manifest.json');
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  delete manifest.repositoryState.refs['refs/identities/00a93c528f611648ef2a986574aa26e0dbbcee47e1d4c440bf33fc8f03b387a3'];
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

  await rejectsWithCategory(() => validateFixture(fixture), 'fixture-contract-mismatch');
});

test('malformed-ref descriptors cover every approved invariant and remain bounded', async () => {
  const manifest = JSON.parse(await readFile(path.join(fixtureParent, 'malformed-scenarios.json'), 'utf8'));
  const scenarios = Object.entries(manifest.scenarios);
  const expectedCodes = [
    'DuplicateOperations',
    'AmbiguousHistory',
    'MissingNonce',
    'StaleEditClock',
    'UnsupportedMarker',
    'StaleEditClock',
    'MissingEditClock',
    'InvalidParentTopology',
    'UnsupportedMarker',
    'AmbiguousHistory'
  ];

  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.format, 'git-bug-v0.11.0');
  assert.equal(manifest.baseFixture, '../v0.11.0');
  assert.deepEqual(scenarios.map(([, scenario]) => scenario.invariantCode), expectedCodes);
  assert.equal(new Set(scenarios.map(([id]) => id)).size, expectedCodes.length);

  for (const [scenarioId, scenario] of scenarios) {
    assert.match(scenario.baseIssueRef, /^[a-f0-9]{64}$/);
    assert.match(scenario.sourceRevision, /^[a-f0-9]{40}$/);
    assert.match(scenario.latestValidParent ?? scenario.sourceRevision, /^[a-f0-9]{40}$/);
    assert.match(scenario.mutation, /^[a-z0-9-]+$/);
    assert.ok(['NotRepairable', 'Repairable'].includes(scenario.repairability), scenarioId);
    assert.deepEqual(Object.keys(scenario.boundedCounts).sort(), [
      'commitsInspected',
      'operationsInspected',
      'refsInspected'
    ]);
    assert.ok(scenario.boundedCounts.refsInspected >= 1 && scenario.boundedCounts.refsInspected <= 64);
    assert.ok(scenario.boundedCounts.commitsInspected >= 1 && scenario.boundedCounts.commitsInspected <= 128);
    assert.ok(scenario.boundedCounts.operationsInspected >= 0 && scenario.boundedCounts.operationsInspected <= 512);
  }
});
