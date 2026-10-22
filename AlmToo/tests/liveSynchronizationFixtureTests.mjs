import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

import { readLiveConfiguration } from '../e2e/liveSynchronizationFixture.mjs';
import { startControlledGitBugRemote } from '../e2e/controlledGitBugRemote.mjs';
import {
  branchHistoryFixture,
  startBranchHistoryFixture
} from '../e2e/gitBranchHistoryFixture.mjs';

const safeConfiguration = Object.freeze({
  ALMTOO_LIVE_GITHUB_OWNER: 'fixture-owner',
  ALMTOO_LIVE_GITHUB_REPOSITORY: 'fixture-repository',
  ALMTOO_LIVE_GITHUB_BRANCH: 'main',
  ALMTOO_LIVE_GITHUB_PAT: 'fixture-test-token',
  ALMTOO_LIVE_GITHUB_FILE: 'live-fixture.txt'
});

async function receivePackProbe(remote, authorization) {
  return fetch(`${remote.url}/info/refs?service=git-receive-pack`, {
    headers: { authorization }
  });
}

test('live fixture accepts only complete, safe CI environment configuration', () => {
  const configuration = readLiveConfiguration(safeConfiguration);

  assert.deepEqual(configuration, {
    owner: 'fixture-owner',
    repository: 'fixture-repository',
    branch: 'main',
    token: 'fixture-test-token',
    fixtureFile: 'live-fixture.txt',
    repositoryUrl: 'https://github.com/fixture-owner/fixture-repository.git'
  });
});

test('live fixture fails closed for absent configuration without exposing a credential', () => {
  assert.throws(
    () => readLiveConfiguration({}),
    error => {
      assert.match(error.message, /Live GitHub fixture is not configured/);
      assert.doesNotMatch(error.message, /fixture-test-token/);
      return true;
    }
  );
});

test('live fixture rejects malformed owner, repository, branch, and fixture paths safely', () => {
  for (const [key, value] of Object.entries({
    ALMTOO_LIVE_GITHUB_OWNER: 'owner/name',
    ALMTOO_LIVE_GITHUB_REPOSITORY: 'repository name',
    ALMTOO_LIVE_GITHUB_BRANCH: 'trunk',
    ALMTOO_LIVE_GITHUB_FILE: '../outside-fixture.txt'
  })) {
    assert.throws(
      () => readLiveConfiguration({ ...safeConfiguration, [key]: value }),
      error => {
        assert.match(error.message, /Live GitHub fixture configuration is invalid/);
        assert.doesNotMatch(error.message, /fixture-test-token/);
        assert.doesNotMatch(error.message, new RegExp(value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
        return true;
      },
      key
    );
  }
});

test('the committed live-fixture environment template is value-free', async () => {
  const template = await readFile(fileURLToPath(new URL('../.env.example', import.meta.url)), 'utf8');
  const configuredLines = template.split(/\r?\n/)
    .filter(line => line.startsWith('ALMTOO_LIVE_GITHUB_'));

  assert.deepEqual(configuredLines, [
    'ALMTOO_LIVE_GITHUB_OWNER=',
    'ALMTOO_LIVE_GITHUB_REPOSITORY=',
    'ALMTOO_LIVE_GITHUB_BRANCH=',
    'ALMTOO_LIVE_GITHUB_PAT=',
    'ALMTOO_LIVE_GITHUB_FILE='
  ]);
});

test('controlled Git-Bug snapshots record refs, identities, reachability, and safe digests', async () => {
  const remote = await startControlledGitBugRemote({ username: 'snapshot-user', password: 'snapshot-secret' });
  try {
    const snapshot = await remote.snapshot();
    assert.ok(snapshot.bugs.length > 0);
    assert.ok(snapshot.identities.length > 0);
    assert.deepEqual(snapshot.requiredIdentityRefs, snapshot.identities);
    assert.deepEqual(snapshot.missingIdentityRefs, []);
    assert.ok(snapshot.reachableObjects.count > 0);
    assert.equal(snapshot.reachableObjects.digest.length, 64);
    assert.equal(snapshot.snapshotDigest.length, 64);
    assert.doesNotMatch(JSON.stringify(snapshot), /snapshot-secret/);
    assert.doesNotMatch(JSON.stringify(snapshot), /snapshot-user/);
  } finally {
    await remote.close();
  }
});

test('controlled Git-Bug remote rejects credentials without changing the snapshot digest', async () => {
  const remote = await startControlledGitBugRemote({ username: 'auth-user', password: 'auth-secret' });
  try {
    const before = await remote.snapshot();
    const response = await receivePackProbe(remote, `Basic ${Buffer.from('auth-user:wrong-secret').toString('base64')}`);
    const after = await remote.snapshot();
    assert.equal(response.status, 401);
    assert.equal(after.snapshotDigest, before.snapshotDigest);
    assert.ok(after.requestCount > before.requestCount);
    assert.doesNotMatch(JSON.stringify(after), /auth-secret|wrong-secret/);
  } finally {
    await remote.close();
  }
});

test('controlled Git-Bug remote models remote-ahead rejection without mutating refs or objects', async () => {
  const remote = await startControlledGitBugRemote({
    username: 'ahead-user',
    password: 'ahead-secret',
    failureMode: 'non-fast-forward'
  });
  try {
    const before = await remote.snapshot();
    const response = await receivePackProbe(remote, `Basic ${Buffer.from('ahead-user:ahead-secret').toString('base64')}`);
    const after = await remote.snapshot();
    assert.equal(response.status, 403);
    assert.equal(after.snapshotDigest, before.snapshotDigest);
    assert.deepEqual(after.refOids, before.refOids);
    assert.equal(after.reachableObjects.digest, before.reachableObjects.digest);
    assert.doesNotMatch(JSON.stringify(after), /ahead-secret/);
  } finally {
    await remote.close();
  }
});

test('branch-history fixture seeds bounded divergent branches with isolated tracked state', async () => {
  const fixture = await startBranchHistoryFixture();
  try {
    const snapshot = await fixture.snapshot();
    assert.deepEqual(fixture.branchNames, [branchHistoryFixture.featureBranch, branchHistoryFixture.mainBranch]);
    assert.equal(snapshot.branchNames.includes(branchHistoryFixture.featureBranch), true);
    assert.equal(snapshot.branchNames.includes(branchHistoryFixture.mainBranch), true);
    assert.equal(snapshot.refs[`heads/${branchHistoryFixture.featureBranch}`], snapshot.commits.featureCommit);
    assert.equal(snapshot.refs[`heads/${branchHistoryFixture.mainBranch}`], snapshot.commits.mainCommit);
    assert.notEqual(snapshot.commits.featureCommit, snapshot.commits.mainCommit);
    assert.notEqual(snapshot.commits.featureCommit, snapshot.commits.baseCommit);
    assert.notEqual(snapshot.commits.mainCommit, snapshot.commits.baseCommit);
    assert.equal(snapshot.requestCount, 0);
    assert.equal(fixture.credentials.password, 'fixture-password');
    assert.equal(fixture.trackedFile, branchHistoryFixture.trackedFile);
  } finally {
    await fixture.close();
  }
});
