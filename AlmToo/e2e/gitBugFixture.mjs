import { createHash } from 'node:crypto';
import fs from 'node:fs';
import { cp, mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import git from 'isomorphic-git';

const sourceFixture = fileURLToPath(new URL('../tests/fixtures/gitBug/v0.11.0', import.meta.url));
const repositoryUrl = 'https://github.com/almtoo/git-bug-fixture.git';
const workspacePath = '/almtoo-workspaces/git-bug-fixture';
const vendorScripts = Object.freeze([
  './js/vendor/buffer/buffer.min.js',
  './js/vendor/lightning-fs/lightning-fs.min.js',
  './js/vendor/isomorphic-git/index.umd.min.js',
  './js/vendor/isomorphic-git/http-web.umd.js'
]);

async function filesBelow(root, prefix = '') {
  const files = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const absolute = path.join(root, entry.name);
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) files.push(...await filesBelow(absolute, relative));
    else if (entry.isFile()) files.push({ absolute, relative });
  }
  return files.sort((left, right) => left.relative.localeCompare(right.relative));
}

async function makeUnsupported(root) {
  const gitdir = path.join(root, 'repository');
  const id = (await git.listRefs({ fs, gitdir, filepath: 'refs/bugs' })).sort()[0];
  const ref = `refs/bugs/${id}`;
  const parent = await git.resolveRef({ fs, gitdir, ref });
  const identity = (await git.listRefs({ fs, gitdir, filepath: 'refs/identities' }))[0];
  const ops = await git.writeBlob({ fs, gitdir, blob: new TextEncoder().encode(JSON.stringify({ author: { id: identity }, ops: [] })) });
  const marker = await git.writeBlob({ fs, gitdir, blob: new Uint8Array() });
  const tree = await git.writeTree({ fs, gitdir, tree: [
    { mode: '100644', path: 'ops', oid: ops, type: 'blob' },
    { mode: '100644', path: 'version-5', oid: marker, type: 'blob' }
  ] });
  const oid = await git.writeCommit({ fs, gitdir, commit: {
    tree,
    parent: [parent],
    message: '',
    author: { name: 'fixture', email: 'fixture@example.invalid', timestamp: 1, timezoneOffset: 0 },
    committer: { name: 'fixture', email: 'fixture@example.invalid', timestamp: 1, timezoneOffset: 0 }
  } });
  await git.writeRef({ fs, gitdir, ref, value: oid, force: true });
}

async function fixtureEntries(variant) {
  if (!['supported', 'unavailable', 'unsupported'].includes(variant)) {
    throw new Error(`Unknown Git-Bug browser fixture variant: ${variant}`);
  }

  let fixtureRoot = sourceFixture;
  let temporaryRoot = null;
  try {
    if (variant === 'unsupported') {
      temporaryRoot = await mkdtemp(path.join(os.tmpdir(), 'almtoo-gitbug-e2e-'));
      fixtureRoot = path.join(temporaryRoot, 'fixture');
      await cp(sourceFixture, fixtureRoot, { recursive: true });
      await makeUnsupported(fixtureRoot);
    }

    const repositoryFiles = await filesBelow(path.join(fixtureRoot, 'repository'));
    const worktreeFiles = await filesBelow(path.join(fixtureRoot, 'worktree'));
    const selectedRepositoryFiles = variant === 'unavailable'
      ? repositoryFiles.filter(file => !file.relative.startsWith('refs/bugs/'))
      : repositoryFiles;
    const entries = [
      ...selectedRepositoryFiles.map(file => ({ ...file, relative: `.git/${file.relative}` })),
      ...worktreeFiles
    ];

    return await Promise.all(entries.map(async file => ({
      path: file.relative,
      content: (await readFile(file.absolute)).toString('base64')
    })));
  } finally {
    if (temporaryRoot) await rm(temporaryRoot, { recursive: true, force: true });
  }
}

export async function seedGitBugFixture(page, variant = 'supported', options = {}) {
  const entries = await fixtureEntries(variant);
  await page.evaluate(async ({ entries, vendorScripts, workspacePath }) => {
    const loadVendor = source => new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = source;
      script.async = false;
      script.dataset.browserGitVendor = source;
      script.addEventListener('load', () => {
        script.dataset.loaded = 'true';
        resolve();
      }, { once: true });
      script.addEventListener('error', () => reject(new Error('Git-Bug fixture dependency failed to load.')), { once: true });
      document.head.appendChild(script);
    });
    for (const source of vendorScripts) await loadVendor(source);

    const fixtureFs = new globalThis.LightningFS('almtoo-git', { wipe: true });
    const mkdirp = async target => {
      let current = '';
      for (const segment of target.split('/').filter(Boolean)) {
        current += `/${segment}`;
        try {
          await fixtureFs.promises.mkdir(current);
        } catch (error) {
          if (error?.code !== 'EEXIST' && error?.message !== 'EEXIST') throw error;
        }
      }
    };
    const decode = value => Uint8Array.from(atob(value), character => character.charCodeAt(0));
    await mkdirp(workspacePath);
    for (const entry of entries) {
      const target = `${workspacePath}/${entry.path}`;
      await mkdirp(target.slice(0, target.lastIndexOf('/')));
      await fixtureFs.promises.writeFile(target, decode(entry.content));
    }
    if (typeof fixtureFs.promises.flush === 'function') await fixtureFs.promises.flush();

    globalThis.__almTooGitBugFixtureFs = fixtureFs;
    globalThis.__almTooGitBugFixtureReady = Object.freeze({ workspacePath, entryCount: entries.length });
    globalThis.LightningFS = class GitBugFixtureFilesystem {
      constructor() { return fixtureFs; }
    };
  }, { entries, vendorScripts, workspacePath });

  return { repositoryUrl: options.repositoryUrl ?? repositoryUrl, workspacePath, entryCount: entries.length };
}

export async function waitForGitBugFixture(page, { timeout = 30_000 } = {}) {
  await page.waitForFunction(() => {
    const readiness = globalThis.__almTooGitBugFixtureReady;
    return Boolean(readiness?.workspacePath && readiness.entryCount > 0);
  }, undefined, { timeout });
}

export async function snapshotGitBugRepository(page, repositoryWorkspacePath = workspacePath) {
  return page.evaluate(async workspacePath => {
    const filesystem = globalThis.__almTooGitBugFixtureFs
      ?? new globalThis.LightningFS('almtoo-git', { wipe: false });
    const browserGit = globalThis.git;
    if (!filesystem || !browserGit) throw new Error('Git-Bug browser filesystem is not available.');
    const files = [];
    const visit = async (directory, relative = '') => {
      const names = [...await filesystem.promises.readdir(directory)].sort();
      for (const name of names) {
        const absolute = `${directory}/${name}`;
        const child = relative ? `${relative}/${name}` : name;
        const stat = await filesystem.promises.stat(absolute);
        if (stat.isFile()) {
          const bytes = await filesystem.promises.readFile(absolute);
          const content = typeof bytes === 'string' ? new TextEncoder().encode(bytes) : bytes;
          const digest = await crypto.subtle.digest('SHA-256', content);
          files.push([child, Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')]);
        } else {
          await visit(absolute, child);
        }
      }
    };
    await visit(workspacePath);

    const gitdir = `${workspacePath}/.git`;
    const refs = (await browserGit.listRefs({ fs: filesystem, gitdir, filepath: 'refs' })).sort();
    const refOids = {};
    for (const ref of refs) refOids[ref] = await browserGit.resolveRef({ fs: filesystem, gitdir, ref: `refs/${ref}` });
    const bugs = refs.filter(ref => ref.startsWith('bugs/'));
    const identities = refs.filter(ref => ref.startsWith('identities/'));
    const collectIdentityIds = (value, ids) => {
      if (Array.isArray(value)) {
        for (const item of value) collectIdentityIds(item, ids);
        return;
      }
      if (!value || typeof value !== 'object') return;
      for (const [key, child] of Object.entries(value)) {
        if (key === 'id' && typeof child === 'string' && /^[0-9a-f]{64}$/u.test(child)) ids.add(child);
        collectIdentityIds(child, ids);
      }
    };
    const requiredIds = new Set();
    const pendingBugCommits = bugs.map(ref => refOids[ref]);
    const seenBugCommits = new Set();
    while (pendingBugCommits.length > 0) {
      const oid = pendingBugCommits.pop();
      if (!oid || seenBugCommits.has(oid)) continue;
      seenBugCommits.add(oid);
      const commit = await browserGit.readObject({ fs: filesystem, gitdir, oid });
      if (commit.type !== 'commit') continue;
      pendingBugCommits.push(...(commit.object.parent ?? []));
      const tree = await browserGit.readObject({ fs: filesystem, gitdir, oid: commit.object.tree });
      for (const entry of tree.object) {
        if (entry.path !== 'ops' || entry.type !== 'blob') continue;
        const blob = await browserGit.readObject({ fs: filesystem, gitdir, oid: entry.oid });
        try {
          collectIdentityIds(JSON.parse(new TextDecoder().decode(blob.object)), requiredIds);
        } catch {
          // Keep the repository snapshot useful when an ops blob is malformed.
        }
      }
    }
    const requiredIdentityRefs = [...requiredIds].map(id => `identities/${id}`).sort();

    const pendingObjects = [...Object.values(refOids)];
    const reachableOids = new Set();
    const reachableByType = {};
    while (pendingObjects.length > 0) {
      const oid = pendingObjects.pop();
      if (!oid || reachableOids.has(oid)) continue;
      reachableOids.add(oid);
      const object = await browserGit.readObject({ fs: filesystem, gitdir, oid });
      reachableByType[object.type] = (reachableByType[object.type] ?? 0) + 1;
      if (object.type === 'commit') {
        pendingObjects.push(object.object.tree, ...(object.object.parent ?? []));
      } else if (object.type === 'tree') {
        pendingObjects.push(...object.object.map(entry => entry.oid));
      } else if (object.type === 'tag' && object.object.object) {
        pendingObjects.push(object.object.object);
      }
    }
    const digestText = async value => {
      const encoded = new TextEncoder().encode(JSON.stringify(value));
      const digest = await crypto.subtle.digest('SHA-256', encoded);
      return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
    };
    const fileMap = Object.fromEntries(files);
    const reachableObjects = {
      count: reachableOids.size,
      byType: reachableByType,
      digest: await digestText([...reachableOids].sort())
    };
    const state = {
      files: fileMap,
      refs,
      refOids,
      bugs,
      identities,
      requiredIdentityRefs,
      missingIdentityRefs: requiredIdentityRefs.filter(ref => !identities.includes(ref)),
      reachableObjects
    };
    return { ...fileMap, ...state, snapshotDigest: await digestText(state) };
  }, repositoryWorkspacePath);
}

export function fixtureSnapshotDigest(snapshot) {
  return createHash('sha256').update(JSON.stringify(snapshot)).digest('hex');
}
