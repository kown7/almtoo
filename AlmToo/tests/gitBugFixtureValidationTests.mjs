import assert from 'node:assert/strict';
import { cp, mkdtemp, readFile, rm, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, test } from 'node:test';

import { validateFixture } from './fixtures/gitBug/validate-fixture.mjs';

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

test('fixture validation fails safely when expected issue data is changed', async () => {
  const fixture = await copyFixture();
  const issuesPath = path.join(fixture, 'expected', 'issues.json');
  const issues = JSON.parse(await readFile(issuesPath, 'utf8'));
  issues[0].status = 'unknown';
  await writeFile(issuesPath, `${JSON.stringify(issues, null, 2)}\n`);

  await rejectsWithCategory(() => validateFixture(fixture), 'fixture-contract-mismatch');
});
