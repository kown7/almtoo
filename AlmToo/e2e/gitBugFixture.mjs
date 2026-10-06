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

export async function seedGitBugFixture(page, variant = 'supported') {
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
    globalThis.LightningFS = class GitBugFixtureFilesystem {
      constructor() { return fixtureFs; }
    };
  }, { entries, vendorScripts, workspacePath });

  return { repositoryUrl, workspacePath };
}

export async function snapshotGitBugRepository(page) {
  return page.evaluate(async workspacePath => {
    const filesystem = globalThis.__almTooGitBugFixtureFs;
    if (!filesystem) throw new Error('Git-Bug fixture filesystem is not installed.');
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
    return Object.fromEntries(files);
  }, workspacePath);
}

export function fixtureSnapshotDigest(snapshot) {
  return createHash('sha256').update(JSON.stringify(snapshot)).digest('hex');
}
