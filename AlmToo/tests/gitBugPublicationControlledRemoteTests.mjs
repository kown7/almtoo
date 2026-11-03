import assert from 'node:assert/strict';
import fs from 'node:fs';
import { test } from 'node:test';

import git from 'isomorphic-git';

import { startControlledGitBugRemote } from '../e2e/controlledGitBugRemote.mjs';

const username = 'controlled-contract-user';
const password = 'controlled-contract-password';

function serviceDiscoveryUrl(remote, service) {
  const url = new URL(remote.url);
  url.pathname = `${url.pathname}/info/refs`;
  url.searchParams.set('service', service);
  return url;
}

function receivePackUrl(remote) {
  return serviceDiscoveryUrl(remote, 'git-receive-pack');
}

function uploadPackUrl(remote) {
  return serviceDiscoveryUrl(remote, 'git-upload-pack');
}

async function requestReceivePack(remote, headers = {}) {
  return fetch(receivePackUrl(remote), { headers });
}

async function seedUnrelatedRef(repository) {
  const refs = await git.listRefs({ fs, gitdir: repository, filepath: 'refs' });
  const bugRef = refs.find(ref => ref.startsWith('bugs/'));
  assert.ok(bugRef, 'the seeded fixture must contain a Git-Bug issue ref');
  const oid = await git.resolveRef({ fs, gitdir: repository, ref: `refs/${bugRef}` });
  await git.writeRef({
    fs,
    gitdir: repository,
    ref: 'refs/heads/unrelated-contract-ref',
    value: oid,
    force: true
  });
}

test('controlled remote exposes deterministic Git-Bug snapshots and unrelated-ref seeding', async () => {
  const remote = await startControlledGitBugRemote({ username, password, seedRepository: seedUnrelatedRef });
  try {
    const ready = await remote.waitUntilReady();
    const before = await remote.snapshot();
    const repeatBefore = await remote.snapshot();

    assert.equal(ready.url, remote.url);
    assert.deepEqual(ready.refs, before.refs);
    assert.ok(before.bugs.length > 0, 'snapshot must expose at least one Git-Bug issue ref');
    assert.ok(before.identities.length > 0, 'snapshot must expose at least one identity ref');
    assert.ok(before.refs.includes('heads/unrelated-contract-ref'));
    assert.deepEqual(before.missingIdentityRefs, []);
    assert.deepEqual(before.requiredIdentityRefs, before.identities);
    assert.equal(before.snapshotDigest, repeatBefore.snapshotDigest);
    assert.equal(before.requestCount, 0);

    const response = await fetch(uploadPackUrl(remote));
    assert.equal(response.status, 200);
    await response.arrayBuffer();

    const after = await remote.snapshot();
    assert.deepEqual(after.refs, before.refs);
    assert.deepEqual(after.refOids, before.refOids);
    assert.deepEqual(after.bugs, before.bugs);
    assert.deepEqual(after.identities, before.identities);
    assert.deepEqual(after.requiredIdentityRefs, before.requiredIdentityRefs);
    assert.deepEqual(after.missingIdentityRefs, []);
    assert.equal(after.snapshotDigest, before.snapshotDigest);
    assert.equal(after.requestCount, before.requestCount + 1);
  } finally {
    await remote.close();
  }
});

test('controlled remote supports documented failure modes and rejects unknown modes', async () => {
  const remote = await startControlledGitBugRemote({ username, password });
  try {
    remote.setFailureMode('auth');
    const authResponse = await requestReceivePack(remote);
    assert.equal(authResponse.status, 401);
    assert.ok((await authResponse.text()).includes('authentication failed'));

    remote.setFailureMode('network');
    await assert.rejects(
      () => requestReceivePack(remote),
      error => error instanceof TypeError || /fetch failed/u.test(error?.message ?? '')
    );

    remote.setFailureMode('non-fast-forward');
    const rejectionResponse = await requestReceivePack(remote, {
      authorization: `Basic ${Buffer.from(`${username}:${password}`).toString('base64')}`
    });
    assert.equal(rejectionResponse.status, 403);
    assert.ok((await rejectionResponse.text()).includes('non-fast-forward'));

    remote.clearFailureMode();
    const authorizedResponse = await requestReceivePack(remote, {
      authorization: `Basic ${Buffer.from(`${username}:${password}`).toString('base64')}`
    });
    assert.equal(authorizedResponse.status, 200);
    await authorizedResponse.arrayBuffer();

    assert.throws(
      () => remote.setFailureMode('unexpected-mode'),
      /Unknown controlled remote failure mode: unexpected-mode/u
    );
  } finally {
    await remote.close();
  }

  await assert.rejects(
    () => startControlledGitBugRemote({ failureMode: 'unexpected-mode' }),
    /Unknown controlled remote failure mode: unexpected-mode/u
  );
});

test('controlled remote snapshots redact credentials and clean up idempotently', async () => {
  const remote = await startControlledGitBugRemote({ username, password });
  const repository = remote.repository;
  try {
    const ready = await remote.waitUntilReady();
    const snapshot = await remote.snapshot();
    const serializedState = JSON.stringify({ ready, snapshot, requestCount: remote.requestCount });

    assert.doesNotMatch(serializedState, new RegExp(username, 'u'));
    assert.doesNotMatch(serializedState, new RegExp(password, 'u'));
    assert.doesNotMatch(serializedState, /Basic\s+[A-Za-z0-9+/=]+/u);
    assert.equal(remote.requestCount, 0);
  } finally {
    await remote.close();
  }

  await remote.close();
  assert.equal(fs.existsSync(repository), false);
  await assert.rejects(
    () => remote.waitUntilReady(),
    /Controlled Git-Bug remote is not listening/u
  );
});
