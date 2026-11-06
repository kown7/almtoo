import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const testsRoot = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(testsRoot, '..');

async function text(relativePath) {
  return readFile(path.join(repositoryRoot, relativePath), 'utf8');
}

function exportedFunction(source, name) {
  const start = source.indexOf(`export async function ${name}`);
  assert.notEqual(start, -1, `${name} must remain an exported Resource operation`);
  const end = source.indexOf('\nexport ', start + 10);
  return source.slice(start, end === -1 ? source.length : end);
}

const requiredOperationalChecks = [
  'diagnosis',
  'preview',
  'explicit confirmation',
  'CLI',
  'idempot',
  'stale',
  'ambiguous',
  'credential',
  'no-mutation'
];

test('M019 recovery Resource keeps diagnosis read-only and application explicitly guarded', async () => {
  const resource = await text('wwwroot/js/browserGitEngine.js');
  const diagnosis = exportedFunction(resource, 'diagnoseGitBugRef');
  const application = exportedFunction(resource, 'applyGitBugRepair');

  assert.match(diagnosis, /git\.resolveRef/);
  assert.match(diagnosis, /readGitBugDiagnosis/);
  assert.doesNotMatch(diagnosis, /writeRef|writeFile|persistFilesystem/);

  for (const safeguard of [
    'request.confirmed',
    'request.sourceRevision',
    'request.expectedOldObjectId',
    'request.expectedNewObjectId',
    'isAllowedRepairTargetRef',
    'isAllowedRepairBackupRef',
    'resolveOptionalRef',
    'alreadyApplied',
    'writeRef',
    'persistFilesystem'
  ]) {
    assert.match(application, new RegExp(safeguard.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')), safeguard);
  }
});

test('M019 malformed fixture matrix distinguishes supported repairs from refusal cases', async () => {
  const manifest = JSON.parse(await text('tests/fixtures/gitBug/malformed-scenarios.json'));
  const scenarios = manifest.scenarios;
  const repairable = Object.entries(scenarios)
    .filter(([, value]) => value.repairability === 'Repairable')
    .map(([key]) => key)
    .sort();

  assert.deepEqual(repairable, ['missing-edit-clock-n', 'missing-nonce']);
  for (const name of [
    'duplicate-operations',
    'multi-operation-edit',
    'backward-clock-skew',
    'malformed-legacy-history',
    'stale-edit-clock-n',
    'invalid-parent-topology',
    'unsupported-marker',
    'ambiguous-history'
  ]) {
    assert.equal(scenarios[name].repairability, 'NotRepairable', name);
  }
  assert.equal(manifest.format, 'git-bug-v0.11.0');
  assert.equal(manifest.marker, 'version-4');
});

test('M019 runbook and architecture docs expose every source-bound UAT concern', async () => {
  const [gitBugDesign, architectureDesign, review] = await Promise.all([
    text('Accessor/BrowserGitAccessor/GIT-BUG-DESIGN.md'),
    text('Accessor/BrowserGitAccessor/DESIGN.md'),
    text('../docs/IDESIGN-REVIEW.md')
  ]);
  const combined = `${gitBugDesign}\n${architectureDesign}\n${review}`.toLocaleLowerCase();

  for (const check of requiredOperationalChecks) {
    assert.match(combined, new RegExp(check.toLocaleLowerCase()), `missing operational concern: ${check}`);
  }
  assert.match(combined, /refs\/almtoo\/recovery/);
  assert.match(combined, /never.*external remote|external remote.*never/i);
  assert.match(combined, /m019 s03 t03 operational recovery evidence review/i);
  assert.match(combined, /no new interface/);

  for (const secret of ['fixture-push-secret', 'controlled-contract-password', 'password=']) {
    assert.doesNotMatch(gitBugDesign, new RegExp(secret, 'i'), `secret-like value leaked into design: ${secret}`);
    assert.doesNotMatch(architectureDesign, new RegExp(secret, 'i'), `secret-like value leaked into design: ${secret}`);
  }
});

test('M019 CLI compatibility proof is mandatory and disposable', async () => {
  const cli = await text('tests/gitBugCliCompatibilityTests.mjs');

  assert.match(cli, /expectedRelease\s*=\s*'v0\.11\.0'/);
  assert.match(cli, /expectedVersionLine/);
  assert.match(cli, /version/);
  for (const command of ['list', 'show', 'pull', 'status']) {
    assert.match(cli, new RegExp(`['\\\"]${command}['\\\"]`), `CLI command ${command} is not covered`);
  }
  assert.match(cli, /assert\.fail|throw new Error/);
  assert.match(cli, /finally|afterEach/);
  assert.match(cli, /rm\(/);
  assert.doesNotMatch(cli, /console\.log\([^)]*(?:token|password|credential)/i);
});

test('M019 no-mutation and controlled-remote proofs are wired to tracked tests', async () => {
  const [discovery, validation, controlledRemote, fixture, resource] = await Promise.all([
    text('tests/gitBugFixtureDiscoveryTests.mjs'),
    text('tests/gitBugFixtureValidationTests.mjs'),
    text('tests/gitBugPublicationControlledRemoteTests.mjs'),
    text('e2e/controlledGitBugRemote.mjs'),
    text('wwwroot/js/browserGitEngine.js')
  ]);

  assert.match(discovery, /sourceFixture/);
  assert.match(discovery, /createHash|hash/);
  assert.match(discovery, /assert\.deepEqual|assert\.equal/);
  assert.match(validation, /validateFixture/);
  assert.match(validation, /mkdtemp/);
  assert.match(validation, /rm\(/);
  assert.match(controlledRemote, /startControlledGitBugRemote/);
  assert.match(controlledRemote, /auth|network|non-fast-forward/i);
  assert.match(fixture, /safeFailureModes/);
  assert.match(`${controlledRemote}\n${fixture}\n${resource}`, /refs\/bugs|refs\/identities/);
  assert.doesNotMatch(fixture, /process\.env\.[A-Z0-9_]*(?:TOKEN|PASSWORD|SECRET)/);
});
