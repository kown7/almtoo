import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import git from 'isomorphic-git';

const fixtureDirectory = fileURLToPath(new URL('./v0.11.0/', import.meta.url));

class FixtureValidationError extends Error {
  constructor(category) {
    super(`Git-Bug fixture validation failed: ${category}.`);
    this.category = category;
  }
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

async function filesBelow(root) {
  const result = [];

  async function walk(current, prefix = '') {
    for (const entry of await readdir(current, { withFileTypes: true })) {
      const relative = path.posix.join(prefix, entry.name);
      const absolute = path.join(current, entry.name);
      if (entry.isDirectory()) await walk(absolute, relative);
      else if (entry.isFile()) result.push({ absolute, relative });
    }
  }

  await walk(root);
  return result.sort((left, right) => left.relative.localeCompare(right.relative));
}

async function hashFiles(root) {
  return Object.fromEntries(await Promise.all((await filesBelow(root)).map(async file => [
    file.relative,
    sha256(await readFile(file.absolute))
  ])));
}

async function readRefs(repositoryDirectory) {
  const refsDirectory = path.join(repositoryDirectory, 'refs');
  return Object.fromEntries(await Promise.all((await filesBelow(refsDirectory)).map(async file => [
    path.posix.join('refs', file.relative),
    (await readFile(file.absolute, 'utf8')).trim()
  ])));
}

async function captureRepositoryState(root) {
  const repositoryDirectory = path.join(root, 'repository');
  const indexPath = path.join(repositoryDirectory, 'index');
  const indexBytes = await readFile(indexPath);
  const objectFiles = await filesBelow(path.join(repositoryDirectory, 'objects'));
  const objectDigests = await Promise.all(objectFiles.map(async file =>
    `${file.relative}\0${sha256(await readFile(file.absolute))}`));

  return {
    head: (await readFile(path.join(repositoryDirectory, 'HEAD'), 'utf8')).trim(),
    refs: await readRefs(repositoryDirectory),
    index: {
      bytes: (await stat(indexPath)).size,
      sha256: sha256(indexBytes)
    },
    worktree: await hashFiles(path.join(root, 'worktree')),
    gitBugMetadata: {
      issueRefCount: objectDigests.length > 0
        ? (await filesBelow(path.join(repositoryDirectory, 'refs', 'bugs'))).length
        : 0,
      identityRefCount: (await filesBelow(path.join(repositoryDirectory, 'refs', 'identities'))).length,
      reachableObjectCount: objectFiles.length - 3,
      allObjectCount: objectFiles.length,
      aggregateSha256: sha256(objectDigests.join('\n'))
    }
  };
}

async function checkScenarioContract(root, manifest) {
  const expectedDirectory = path.join(root, 'expected');
  const issues = JSON.parse(await readFile(path.join(expectedDirectory, 'issues.json'), 'utf8'));
  const scenarioIds = JSON.parse(await readFile(path.join(expectedDirectory, 'scenario-ids.json'), 'utf8'));
  const details = Object.fromEntries(await Promise.all(Object.entries(scenarioIds).map(async ([name, id]) => [
    name,
    JSON.parse(await readFile(path.join(expectedDirectory, 'details', `${id}.json`), 'utf8'))
  ])));

  assert.equal(issues.length, 3);
  assert.deepEqual(issues.map(issue => issue.status).sort(), ['closed', 'open', 'open']);
  assert.deepEqual(details.unicodeOpen.labels, ['defect', 'priority:high']);
  assert.equal(details.unicodeOpen.comments.length, 2);
  assert.match(details.unicodeOpen.title, /café.*🐛/u);
  assert.match(details.unicodeOpen.comments[0].message, /日本語.*🚀/u);
  assert.match(details.unicodeOpen.comments[1].message, /Ελληνικά/u);
  assert.equal(details.minimalOpen.status, 'open');
  assert.equal(details.minimalOpen.labels, null);
  assert.equal(details.minimalOpen.comments.length, 1);
  assert.equal(details.minimalOpen.author.login, '');
  assert.equal(details.closed.status, 'closed');
  assert.deepEqual(details.closed.labels, ['documentation', 'resolved']);
  assert.equal(details.closed.comments.length, 2);

  for (const [name, detail] of Object.entries(details)) {
    assert.equal(detail.human_id, scenarioIds[name]);
    assert.equal(detail.id, manifest.scenarios[name].id);
    const ref = `refs/bugs/${detail.id}`;
    assert.ok(Object.hasOwn(manifest.repositoryState.refs, ref));
  }
}

async function checkReadableGitObjects(root, manifest) {
  const gitdir = path.join(root, 'repository');
  const refs = Object.keys(manifest.repositoryState.refs)
    .filter(ref => ref.startsWith('refs/bugs/') || ref.startsWith('refs/identities/'));

  for (const ref of refs) {
    const oid = await git.resolveRef({ fs, gitdir, ref });
    const object = await git.readObject({ fs, gitdir, oid });
    assert.equal(object.type, 'commit');
  }
}

export async function validateFixture(root = fixtureDirectory) {
  try {
    const manifest = JSON.parse(await readFile(path.join(root, 'fixture-manifest.json'), 'utf8'));
    assert.equal(manifest.schemaVersion, 1);
    assert.equal(manifest.gitBug.release, 'v0.11.0');
    assert.equal(manifest.gitBug.binarySha256, '431199b5997ec8e6de9fa7e2caab13fe222268b0110d0c85a4354907197e7ce9');

    const stateBefore = await captureRepositoryState(root);
    assert.deepEqual(stateBefore, manifest.repositoryState);

    const dataFiles = {
      ...(await hashFiles(path.join(root, 'repository'))),
      ...Object.fromEntries(Object.entries(await hashFiles(path.join(root, 'worktree')))
        .map(([name, digest]) => [`worktree/${name}`, digest])),
      ...Object.fromEntries(Object.entries(await hashFiles(path.join(root, 'expected')))
        .map(([name, digest]) => [`expected/${name}`, digest]))
    };
    const repositoryFiles = await hashFiles(path.join(root, 'repository'));
    for (const [name, digest] of Object.entries(repositoryFiles)) {
      dataFiles[`repository/${name}`] = digest;
      delete dataFiles[name];
    }
    assert.deepEqual(dataFiles, manifest.dataFileSha256);

    await checkScenarioContract(root, manifest);
    await checkReadableGitObjects(root, manifest);

    const stateAfter = await captureRepositoryState(root);
    assert.deepEqual(stateAfter, stateBefore);

    return {
      release: manifest.gitBug.release,
      scenarioCount: Object.keys(manifest.scenarios).length,
      issueRefCount: stateAfter.gitBugMetadata.issueRefCount,
      identityRefCount: stateAfter.gitBugMetadata.identityRefCount,
      objectCount: stateAfter.gitBugMetadata.allObjectCount,
      repositoryState: 'unchanged'
    };
  } catch (error) {
    if (error instanceof FixtureValidationError) throw error;
    if (error?.code === 'ENOENT') throw new FixtureValidationError('missing-fixture-data');
    if (error instanceof SyntaxError) throw new FixtureValidationError('malformed-fixture-data');
    throw new FixtureValidationError('fixture-contract-mismatch');
  }
}

const isMain = process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;
if (isMain) {
  try {
    const result = await validateFixture();
    console.log(`Git-Bug fixture valid: release=${result.release} scenarios=${result.scenarioCount} issueRefs=${result.issueRefCount} identityRefs=${result.identityRefCount} objects=${result.objectCount} repositoryState=${result.repositoryState}`);
  } catch (error) {
    console.error(error instanceof FixtureValidationError ? error.message : 'Git-Bug fixture validation failed: unknown.');
    process.exitCode = 1;
  }
}
