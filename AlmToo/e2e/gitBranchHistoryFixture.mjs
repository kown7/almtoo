import { execFile as execFileCallback } from 'node:child_process';
import fs from 'node:fs';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';

import git from 'isomorphic-git';

import { fixtureSnapshotDigest, seedGitBugFixture, snapshotGitBugRepository } from './gitBugFixture.mjs';
import { startControlledGitBugRemote } from './controlledGitBugRemote.mjs';

const execFile = promisify(execFileCallback);

export const branchHistoryFixture = Object.freeze({
  mainBranch: 'main',
  featureBranch: 'feature',
  trackedFile: 'branch-history-marker.txt',
  featureContents: 'feature branch marker\n',
  mainContents: 'main branch marker\n',
  historyDepth: 3
});

const gitEnvironment = Object.freeze({
  GIT_AUTHOR_NAME: 'AlmToo deterministic fixture',
  GIT_AUTHOR_EMAIL: 'fixture@almtoo.invalid',
  GIT_COMMITTER_NAME: 'AlmToo deterministic fixture',
  GIT_COMMITTER_EMAIL: 'fixture@almtoo.invalid',
  GIT_TERMINAL_PROMPT: '0'
});

async function runGit(cwd, args) {
  const result = await execFile('git', args, {
    cwd,
    env: { ...process.env, ...gitEnvironment },
    maxBuffer: 1024 * 1024
  });
  return result.stdout.trim();
}

async function commitBranch(worktree, branch, contents, messages) {
  await runGit(worktree, ['checkout', '--quiet', '-B', branch, 'HEAD']);
  for (const [index, message] of messages.entries()) {
    await writeFile(path.join(worktree, branchHistoryFixture.trackedFile), `${contents}${index + 1}\n`);
    await runGit(worktree, ['add', '--', branchHistoryFixture.trackedFile]);
    await runGit(worktree, ['commit', '--quiet', '-m', message]);
  }
  await runGit(worktree, ['push', '--quiet', 'origin', branch]);
}

/**
 * Seeds two intentionally divergent branches into the disposable controlled
 * remote. The source repository remains unchanged and the temporary clone is
 * removed before this function returns.
 */
export async function seedBranchHistoryRepository(repository) {
  if (typeof repository !== 'string' || repository.length === 0) {
    throw new TypeError('A disposable bare repository path is required.');
  }

  const worktree = await mkdtemp(path.join(os.tmpdir(), 'almtoo-git-history-seed-'));
  try {
    await runGit(undefined, ['clone', '--quiet', repository, worktree]);
    await runGit(worktree, ['config', 'user.name', gitEnvironment.GIT_AUTHOR_NAME]);
    await runGit(worktree, ['config', 'user.email', gitEnvironment.GIT_AUTHOR_EMAIL]);

    const baseCommit = await runGit(worktree, ['rev-parse', 'HEAD']);
    await commitBranch(
      worktree,
      branchHistoryFixture.featureBranch,
      branchHistoryFixture.featureContents,
      ['Feature branch checkpoint', 'Feature branch follow-up']
    );
    const featureCommit = await runGit(worktree, ['rev-parse', branchHistoryFixture.featureBranch]);

    await runGit(worktree, ['checkout', '--quiet', branchHistoryFixture.mainBranch]);
    await writeFile(path.join(worktree, branchHistoryFixture.trackedFile), `${branchHistoryFixture.mainContents}1\n`);
    await runGit(worktree, ['add', '--', branchHistoryFixture.trackedFile]);
    await runGit(worktree, ['commit', '--quiet', '-m', 'Main branch checkpoint']);
    await writeFile(path.join(worktree, branchHistoryFixture.trackedFile), `${branchHistoryFixture.mainContents}2\n`);
    await runGit(worktree, ['add', '--', branchHistoryFixture.trackedFile]);
    await runGit(worktree, ['commit', '--quiet', '-m', 'Main branch follow-up']);
    await runGit(worktree, ['push', '--quiet', 'origin', branchHistoryFixture.mainBranch]);
    const mainCommit = await runGit(worktree, ['rev-parse', branchHistoryFixture.mainBranch]);

    return Object.freeze({ baseCommit, featureCommit, mainCommit });
  } finally {
    await rm(worktree, { recursive: true, force: true });
  }
}

async function resolvedRefs(repository) {
  const refs = await git.listRefs({ fs, gitdir: repository, filepath: 'refs' });
  const resolved = {};
  for (const ref of refs.sort()) {
    resolved[ref] = await git.resolveRef({ fs, gitdir: repository, ref: `refs/${ref}` });
  }
  return resolved;
}

/** Returns a transport-independent remote snapshot suitable for before/after assertions. */
export async function snapshotBranchHistoryRemote(remote) {
  if (!remote?.repository) throw new TypeError('A branch-history remote is required.');
  return {
    head: await git.resolveRef({ fs, gitdir: remote.repository, ref: 'HEAD' }),
    refs: await resolvedRefs(remote.repository),
    branchNames: [branchHistoryFixture.featureBranch, branchHistoryFixture.mainBranch],
    trackedFile: branchHistoryFixture.trackedFile
  };
}

/**
 * Captures browser-local Git state without including credentials or transport
 * metadata. `files` is retained for full-worktree comparisons; the normalized
 * fields are stable enough for clean and dirty checkout assertions.
 */
export async function snapshotBranchHistoryRepository(page, repositoryWorkspacePath = '/almtoo-workspaces/git-bug-fixture') {
  const files = await snapshotGitBugRepository(page, repositoryWorkspacePath);
  const state = await page.evaluate(async ({ repositoryWorkspacePath }) => {
    const fixtureFs = globalThis.__almTooGitBugFixtureFs;
    const browserGit = globalThis.git;
    if (!fixtureFs || !browserGit) throw new Error('The deterministic browser Git fixture is not ready.');

    const gitdir = `${repositoryWorkspacePath}/.git`;
    const matrix = await browserGit.statusMatrix({ fs: fixtureFs, dir: repositoryWorkspacePath });
    const refNames = await browserGit.listRefs({ fs: fixtureFs, gitdir, filepath: 'refs' });
    const refs = {};
    for (const ref of refNames.sort()) {
      refs[ref] = await browserGit.resolveRef({ fs: fixtureFs, gitdir, ref: `refs/${ref}` });
    }

    return {
      head: await browserGit.resolveRef({ fs: fixtureFs, gitdir, ref: 'HEAD' }),
      currentBranch: await browserGit.currentBranch({ fs: fixtureFs, dir: repositoryWorkspacePath, fullname: false }),
      refs,
      status: matrix.map(([filepath, head, workdir, stage]) => ({ filepath, head, workdir, stage })),
      trackedPaths: matrix.filter(([, head]) => head === 1).map(([filepath]) => filepath).sort()
    };
  }, { repositoryWorkspacePath });

  const trackedFiles = Object.fromEntries(
    state.trackedPaths.map(filepath => [filepath, files[filepath] ?? null])
  );
  return { ...state, trackedFiles, files };
}

export function branchHistorySnapshotDigest(snapshot) {
  return fixtureSnapshotDigest({
    head: snapshot.head,
    currentBranch: snapshot.currentBranch,
    refs: snapshot.refs,
    trackedFiles: snapshot.trackedFiles,
    status: snapshot.status
  });
}

/** Loads the existing deterministic browser filesystem for use with the branch remote. */
export async function seedBranchHistoryFixture(page, remote, options = {}) {
  if (!remote?.url) throw new TypeError('A branch-history remote is required.');
  return seedGitBugFixture(page, 'supported', { ...options, repositoryUrl: remote.url });
}

export async function startBranchHistoryFixture(options = {}) {
  const { seedRepository: callerSeed, ...remoteOptions } = options;
  const commits = {};
  const remote = await startControlledGitBugRemote({
    ...remoteOptions,
    seedRepository: async repository => {
      Object.assign(commits, await seedBranchHistoryRepository(repository));
      if (callerSeed !== undefined) {
        if (typeof callerSeed !== 'function') throw new TypeError('seedRepository must be a function when provided.');
        await callerSeed(repository);
      }
    }
  });

  return {
    url: remote.url,
    repository: remote.repository,
    credentials: remote.credentials,
    get requestCount() { return remote.requestCount; },
    waitUntilReady: remote.waitUntilReady,
    setFailureMode: remote.setFailureMode,
    clearFailureMode: remote.clearFailureMode,
    async snapshot() {
      return { ...await remote.snapshot(), ...(await snapshotBranchHistoryRemote(remote)), commits: { ...commits } };
    },
    async close() { await remote.close(); },
    branchNames: Object.freeze([branchHistoryFixture.featureBranch, branchHistoryFixture.mainBranch]),
    trackedFile: branchHistoryFixture.trackedFile,
    commits: Object.freeze(commits)
  };
}

export async function withBranchHistoryFixture(action, options = {}) {
  const remote = await startBranchHistoryFixture(options);
  try {
    return await action(remote);
  } finally {
    await remote.close();
  }
}
