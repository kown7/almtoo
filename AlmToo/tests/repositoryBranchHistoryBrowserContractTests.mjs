import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = path => readFile(new URL(path, root), 'utf8');
const specPath = 'e2e/repositoryBranchHistory.spec.mjs';

 test('branch and history browser acceptance owns the complete observable contract', async () => {
  const spec = await source(specPath);

  for (const requiredImport of [
    'branchHistoryFixture',
    'branchHistorySnapshotDigest',
    'seedBranchHistoryFixture',
    'snapshotBranchHistoryRemote',
    'snapshotBranchHistoryRepository',
    'startBranchHistoryFixture'
  ]) {
    assert.match(spec, new RegExp(`\\b${requiredImport}\\b`), `${requiredImport} is required by the deterministic fixture flow`);
  }

  for (const diagnostic of ['pageErrors', 'consoleErrors', 'requestFailures', 'httpErrors', 'dialogs', 'proxyErrors']) {
    assert.match(spec, new RegExp(`\\b${diagnostic}\\b`), `${diagnostic} must be captured and asserted`);
  }

  for (const contractText of [
    'Current branch',
    'Recent commits',
    'Feature branch follow-up',
    'Feature branch checkpoint',
    'Commit or discard the changed files before switching branches.',
    'branchHistorySnapshotDigest',
    'dirtyBefore',
    'dirtyAfter',
    'remoteBefore',
    'testInfo.attach'
  ]) {
    assert.match(spec, new RegExp(contractText.replace(/[.*+?^${}()|[\\]\\]/g, '\\$&')), `${contractText} must remain part of browser proof`);
  }

  assert.match(spec, /expect\(initial\.head\)\.toBe\(initial\.refs\['heads\/main'\]\)/);
  assert.match(spec, /expect\(cleanFeature\.head\)\.toBe\(cleanFeature\.refs\['heads\/feature'\]\)/);
  assert.match(spec, /expect\(dirtyAfter\.head\)\.toBe\(dirtyAfter\.refs\['heads\/main'\]\)/);
  assert.match(spec, /expect\(branchHistorySnapshotDigest\(dirtyAfter\)\)\.toBe\(branchHistorySnapshotDigest\(dirtyBefore\)\)/);
  assert.match(spec, /expect\(JSON\.stringify\(diagnostics\)\)\.not\.toContain\(fixturePassword\)/);
});

test('branch and history browser proof is isolated from production credentials and infrastructure', async () => {
  const spec = await source(specPath);
  assert.doesNotMatch(spec, /process\.env\.(?:ALMTOO_LIVE|GITHUB|PAT)/i);
  assert.doesNotMatch(spec, /\.env|live\.env/i);
  assert.match(spec, /startBranchHistoryFixture\(\{ username: fixtureUsername, password: fixturePassword \}\)/);
  assert.match(spec, /await remote\.close\(\)/);
  assert.match(spec, /await page\.unroute\(proxyPattern\)/);
});
