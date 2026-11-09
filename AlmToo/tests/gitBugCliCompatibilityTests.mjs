import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, test } from 'node:test';

const sourceFixture = fileURLToPath(new URL('./fixtures/gitBug/v0.11.0', import.meta.url));
const sourceRepository = path.join(sourceFixture, 'repository');
const expectedRelease = 'v0.11.0';
const expectedVersionLine = /^git-bug v0\.11\.0 d1819bd1dbd6 go1\.27\.1 /;
const temporaryRepositories = [];

function candidateBins() {
  const configured = process.env.GIT_BUG_BIN;
  return [
    configured && (path.isAbsolute(configured) ? configured : path.resolve(process.cwd(), configured)),
    path.resolve(process.cwd(), '.tmp/git-bug-v0.11.0/git-bug'),
    path.resolve(process.cwd(), '../.tmp/git-bug-v0.11.0/git-bug'),
    'git-bug',
  ].filter(Boolean);
}

function runCli(binary, args, cwd) {
  try {
    const stdout = execFileSync(binary, args, {
      cwd,
      encoding: 'utf8',
      env: { ...process.env, GIT_CONFIG_NOSYSTEM: '1' },
      stdio: ['ignore', 'pipe', 'pipe'],
      timeout: 30_000,
    });
    return { status: 0, stdout, stderr: '' };
  } catch (error) {
    const stdout = typeof error.stdout === 'string' ? error.stdout : '';
    const stderr = typeof error.stderr === 'string' ? error.stderr : '';
    return {
      status: typeof error.status === 'number' ? error.status : 1,
      stdout,
      stderr,
    };
  }
}

function assertNoSensitiveDiagnostics(output, label) {
  assert.doesNotMatch(output, /authorization\s*:/i, `${label} emitted an authorization header`);
  assert.doesNotMatch(output, /\b(?:bearer|basic)\s+[a-z0-9._~+/=-]+/i, `${label} emitted credential material`);
  assert.doesNotMatch(output, /(?:token|password|secret|passwd)\s*[=:]\s*\S+/i, `${label} emitted a credential-like field`);
  assert.doesNotMatch(output, /https?:\/\/[^\s/]+:[^\s/@]+@/i, `${label} emitted credentials in a URL`);
}

function resolveCli() {
  const diagnostics = [];
  for (const candidate of candidateBins()) {
    const result = runCli(candidate, ['version'], process.cwd());
    const combined = `${result.stdout}\n${result.stderr}`;
    if (result.status === 0 && expectedVersionLine.test(result.stdout.trim())) {
      assertNoSensitiveDiagnostics(combined, 'Git-Bug version check');
      return { binary: candidate, version: result.stdout.trim() };
    }
    diagnostics.push(`${path.basename(candidate)}: exit ${result.status}`);
  }

  throw new Error(
    `Git-Bug CLI ${expectedRelease} is required for compatibility verification; ${diagnostics.join(', ') || 'no candidate configured'}.`,
  );
}

async function createIsolatedRepository() {
  const root = await mkdtemp(path.join(os.tmpdir(), 'almtoo-git-bug-cli-'));
  temporaryRepositories.push(root);
  const fixture = path.join(root, 'fixture');
  await cp(sourceFixture, fixture, { recursive: true });

  const configPath = path.join(fixture, 'repository', 'config');
  const config = await readFile(configPath, 'utf8');
  await writeFile(
    configPath,
    `${config}\n[remote "origin"]\n\turl = ${sourceRepository}\n\tfetch = +refs/*:refs/remotes/origin/*\n`,
    'utf8',
  );
  return path.join(fixture, 'repository');
}

afterEach(async () => {
  await Promise.all(temporaryRepositories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

test('installed Git-Bug CLI is the pinned v0.11.0 release', () => {
  const { version } = resolveCli();
  assert.match(version, expectedVersionLine);
});

test('v0.11.0 CLI reads isolated repaired fixture copies through list, show, pull, and status', async () => {
  const { binary } = resolveCli();
  const repository = await createIsolatedRepository();
  const commands = [
    { name: 'list', args: ['bug'], expected: /a85873c\s+open\s+Unicode café regression/ },
    { name: 'show', args: ['bug', 'show', 'a85873c'], expected: /Unicode café regression/ },
    { name: 'pull', args: ['pull'], expected: /Merging data/ },
    { name: 'status', args: ['bug', 'status', 'a85873c'], expected: /^open\s*$/m },
  ];

  for (const command of commands) {
    const result = runCli(binary, command.args, repository);
    const output = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 0, `${command.name} exited with ${result.status}`);
    assert.match(result.stdout, command.expected, `${command.name} returned unexpected output`);
    assertNoSensitiveDiagnostics(output, command.name);
  }
});
