import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

import { readLiveConfiguration } from '../e2e/liveSynchronizationFixture.mjs';

const safeConfiguration = Object.freeze({
  ALMTOO_LIVE_GITHUB_OWNER: 'fixture-owner',
  ALMTOO_LIVE_GITHUB_REPOSITORY: 'fixture-repository',
  ALMTOO_LIVE_GITHUB_BRANCH: 'main',
  ALMTOO_LIVE_GITHUB_PAT: 'fixture-test-token',
  ALMTOO_LIVE_GITHUB_FILE: 'live-fixture.txt'
});

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
