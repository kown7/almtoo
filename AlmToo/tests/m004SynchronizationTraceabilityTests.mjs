import assert from 'node:assert/strict';
import { access, readFile } from 'node:fs/promises';
import { test } from 'node:test';

const workspaceRoot = new URL('../../', import.meta.url);
const traceabilityMap = new URL('../../docs/M004-SYNCHRONIZATION-TRACEABILITY.md', import.meta.url);

const promiseCategories = [
  'Review before mutation.',
  'Clean fast-forward with browser-local refresh.',
  'No-loss dirty-workspace blocking.',
  'Stable safe outcomes.',
  'Tab-scoped input-only PAT handling.'
];

const proofReferences = [
  'AlmToo/tests/browserGitEngineTests.mjs',
  'AlmToo/tests/homePageContractTests.mjs',
  'AlmToo/tests/browserGitArchitectureGateTests.mjs',
  'AlmToo/e2e/liveSynchronization.spec.mjs',
  'AlmToo/AlmToo.csproj',
  'docs/IDESIGN-REVIEW.md'
];

const evidenceCommands = [
  'node --test AlmToo/tests/browserGitEngineTests.mjs',
  'node --test AlmToo/tests/homePageContractTests.mjs',
  'node --test AlmToo/tests/browserGitArchitectureGateTests.mjs',
  'dotnet test AlmToo/AlmToo.csproj --no-restore',
  'dotnet build AlmToo/AlmToo.csproj --no-restore',
  'npm --prefix AlmToo run test:browser:live'
];

function workspacePath(path) {
  return new URL(path, workspaceRoot);
}

test('M004 synchronization traceability map retains every promised safe behavior and proof command', async () => {
  const document = await readFile(traceabilityMap, 'utf8');

  for (const category of promiseCategories) {
    assert.ok(document.includes(category), `missing M004 promise category: ${category}`);
  }
  for (const reference of proofReferences) {
    assert.ok(document.includes(reference), `missing M004 proof reference: ${reference}`);
  }
  for (const command of evidenceCommands) {
    assert.ok(document.includes(command), `missing M004 evidence command: ${command}`);
  }

  assert.match(document, /controlled-remote browser proof, not a credential-free unit check/i,
    'live browser evidence must retain its controlled-remote, non-unit-test boundary');
});

test('M004 synchronization traceability evidence references resolve inside the repository', async () => {
  for (const reference of proofReferences) {
    await assert.doesNotReject(access(workspacePath(reference)), `missing M004 evidence path: ${reference}`);
  }
});
