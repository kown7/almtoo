import { createHash } from 'node:crypto';
import http from 'node:http';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import git from 'isomorphic-git';

const fixtureRepository = fileURLToPath(new URL('../tests/fixtures/gitBug/v0.11.0/repository', import.meta.url));
const safeFailureModes = new Set(['auth', 'network', 'non-fast-forward']);
const projectName = 'git-bug-fixture.git';

function headerValue(headers, name) {
  return headers[name.toLowerCase()] ?? headers[name] ?? '';
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function collectIdentityIds(value, ids) {
  if (Array.isArray(value)) {
    for (const item of value) collectIdentityIds(item, ids);
    return;
  }
  if (!value || typeof value !== 'object') return;
  for (const [key, child] of Object.entries(value)) {
    if (key === 'id' && typeof child === 'string' && /^[0-9a-f]{64}$/u.test(child)) ids.add(child);
    collectIdentityIds(child, ids);
  }
}

async function reachableObjects(repository, refOids) {
  const pending = [...new Set(Object.values(refOids))];
  const seen = new Set();
  const byType = {};

  while (pending.length > 0) {
    const oid = pending.pop();
    if (!oid || seen.has(oid)) continue;
    seen.add(oid);
    const object = await git.readObject({ fs, gitdir: repository, oid });
    byType[object.type] = (byType[object.type] ?? 0) + 1;
    if (object.type === 'commit') {
      pending.push(object.object.tree, ...(object.object.parent ?? []));
    } else if (object.type === 'tree') {
      pending.push(...object.object.map(entry => entry.oid));
    } else if (object.type === 'tag' && object.object.object) {
      pending.push(object.object.object);
    }
  }

  const objectDigest = sha256(JSON.stringify([...seen].sort()));
  return { count: seen.size, byType, digest: objectDigest };
}

async function requiredIdentityRefs(repository, bugRefOids, identityRefs) {
  const ids = new Set();
  const pending = [...Object.values(bugRefOids)];
  const seen = new Set();
  while (pending.length > 0) {
    const oid = pending.pop();
    if (!oid || seen.has(oid)) continue;
    seen.add(oid);
    const object = await git.readObject({ fs, gitdir: repository, oid });
    if (object.type !== 'commit') continue;
    pending.push(...(object.object.parent ?? []));
    const tree = await git.readObject({ fs, gitdir: repository, oid: object.object.tree });
    for (const entry of tree.object) {
      if (entry.path !== 'ops' || entry.type !== 'blob') continue;
      const blob = await git.readObject({ fs, gitdir: repository, oid: entry.oid });
      try {
        collectIdentityIds(JSON.parse(Buffer.from(blob.object).toString('utf8')), ids);
      } catch {
        // Reachability remains useful even when a malformed ops blob is present.
      }
    }
  }
  return [...ids].map(id => `identities/${id}`).sort();
}

async function repositorySnapshot(repository, requestCount) {
  const refs = (await git.listRefs({ fs, gitdir: repository, filepath: 'refs' }).catch(() => [])).sort();
  const refOids = Object.fromEntries(await Promise.all(refs.map(async ref => [
    ref,
    await git.resolveRef({ fs, gitdir: repository, ref: `refs/${ref}` })
  ])));
  const bugs = refs.filter(ref => ref.startsWith('bugs/'));
  const identities = refs.filter(ref => ref.startsWith('identities/'));
  const required = await requiredIdentityRefs(repository, Object.fromEntries(bugs.map(ref => [ref, refOids[ref]])), identities);
  const reachable = await reachableObjects(repository, refOids);
  const state = {
    refs,
    refOids,
    bugs,
    identities,
    requiredIdentityRefs: required,
    missingIdentityRefs: required.filter(ref => !identities.includes(ref)),
    reachableObjects: reachable
  };
  const digest = sha256(JSON.stringify(state));
  return { ...state, snapshotDigest: digest, repository: path.basename(repository), requestCount };
}

function runHttpBackend({ root, pathInfo, method, query, contentType, body, authorization }) {
  return new Promise((resolve, reject) => {
    const child = spawn('git', ['http-backend'], {
      cwd: root,
      env: {
        ...process.env,
        GIT_PROJECT_ROOT: root,
        GIT_HTTP_EXPORT_ALL: '1',
        PATH_INFO: pathInfo,
        REQUEST_METHOD: method,
        QUERY_STRING: query,
        CONTENT_TYPE: contentType,
        CONTENT_LENGTH: String(body.length),
        REMOTE_USER: authorization ? 'controlled-fixture' : ''
      },
      stdio: ['pipe', 'pipe', 'pipe']
    });
    const output = [];
    const errors = [];
    child.stdout.on('data', chunk => output.push(chunk));
    child.stderr.on('data', chunk => errors.push(chunk));
    child.once('error', reject);
    child.once('close', code => {
      if (code !== 0) {
        reject(new Error(`git http-backend failed with exit code ${code}`));
        return;
      }
      const bytes = Buffer.concat(output);
      const separator = bytes.indexOf('\r\n\r\n');
      if (separator < 0) {
        reject(new Error(`git http-backend returned malformed headers (${errors.length} diagnostic bytes)`));
        return;
      }
      const headers = {};
      for (const line of bytes.subarray(0, separator).toString('utf8').split('\r\n')) {
        const index = line.indexOf(':');
        if (index > 0) headers[line.slice(0, index).toLowerCase()] = line.slice(index + 1).trim();
      }
      resolve({ status: Number(headers.status ?? 200), headers, body: bytes.subarray(separator + 4) });
    });
    child.stdin.end(body);
  });
}

async function seedBareRepository(root) {
  const repository = path.join(root, projectName);
  await cp(fixtureRepository, repository, { recursive: true });
  await writeFile(path.join(repository, 'config'), '[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = true\n\tlogallrefupdates = true\n');
  return repository;
}

function parseFailure(mode) {
  if (mode == null) return null;
  if (!safeFailureModes.has(mode)) throw new Error(`Unknown controlled remote failure mode: ${mode}`);
  return mode;
}

/**
 * Starts a disposable local Git smart-HTTP remote for browser acceptance tests.
 * The helper owns only test infrastructure (Resource boundary); application code
 * receives the opaque URL and never receives credentials or transport details.
 */
export async function startControlledGitBugRemote(options = {}) {
  const username = typeof options.username === 'string' ? options.username : 'fixture-user';
  const password = typeof options.password === 'string' ? options.password : 'fixture-password';
  const expectedAuthorization = `Basic ${Buffer.from(`${username}:${password}`).toString('base64')}`;
  const root = await mkdtemp(path.join(os.tmpdir(), 'almtoo-gitbug-remote-'));
  let repository;
  try {
    repository = await seedBareRepository(root);
    if (options.seedRepository !== undefined) {
      if (typeof options.seedRepository !== 'function') {
        throw new TypeError('seedRepository must be a function when provided.');
      }
      await options.seedRepository(repository);
    }
  } catch (error) {
    await rm(root, { recursive: true, force: true });
    throw error;
  }
  let failureMode = parseFailure(options.failureMode);
  let requestCount = 0;
  let closed = false;

  const server = http.createServer(async (request, response) => {
    requestCount += 1;
    const body = [];
    request.on('data', chunk => body.push(chunk));
    request.on('error', () => response.destroy());
    request.on('end', async () => {
      const bytes = Buffer.concat(body);
      const isReceivePack = request.url?.includes('git-receive-pack');
      if (failureMode === 'network' && isReceivePack) {
        response.destroy();
        return;
      }
      if (failureMode === 'auth' && isReceivePack) {
        response.writeHead(401, { 'content-type': 'text/plain', 'www-authenticate': 'Basic realm="controlled-gitbug"' });
        response.end('controlled remote authentication failed');
        return;
      }
      if (isReceivePack && headerValue(request.headers, 'authorization') !== expectedAuthorization) {
        response.writeHead(401, { 'content-type': 'text/plain', 'www-authenticate': 'Basic realm="controlled-gitbug"' });
        response.end('controlled remote authentication required');
        return;
      }
      if (failureMode === 'non-fast-forward' && isReceivePack) {
        response.writeHead(403, { 'content-type': 'text/plain' });
        response.end('controlled remote rejected non-fast-forward update');
        return;
      }
      try {
        const url = new URL(request.url ?? '/', 'http://controlled-gitbug.invalid');
        const result = await runHttpBackend({
          root,
          pathInfo: url.pathname,
          method: request.method ?? 'GET',
          query: url.searchParams.toString(),
          contentType: headerValue(request.headers, 'content-type'),
          body: bytes,
          authorization: headerValue(request.headers, 'authorization')
        });
        response.writeHead(result.status, Object.fromEntries(Object.entries(result.headers).filter(([key]) => key !== 'status')));
        response.end(result.body);
      } catch {
        response.writeHead(502, { 'content-type': 'text/plain' });
        response.end('controlled remote transport failure');
      }
    });
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  const url = `http://127.0.0.1:${address.port}/${projectName}`;

  return {
    url,
    repository,
    credentials: Object.freeze({ username, password }),
    get requestCount() { return requestCount; },
    async waitUntilReady() {
      if (closed || !server.listening) throw new Error('Controlled Git-Bug remote is not listening.');
      const refs = await git.listRefs({ fs, gitdir: repository, filepath: 'refs' });
      if (refs.length === 0) throw new Error('Controlled Git-Bug remote repository has no seeded refs.');
      return { url, refs: refs.sort() };
    },
    setFailureMode(mode) { failureMode = parseFailure(mode); },
    clearFailureMode() { failureMode = null; },
    async snapshot() {
      return repositorySnapshot(repository, requestCount);
    },
    async close() {
      if (closed) return;
      closed = true;
      await new Promise(resolve => server.close(() => resolve()));
      await rm(root, { recursive: true, force: true });
    }
  };
}

export async function withControlledGitBugRemote(action, options = {}) {
  const remote = await startControlledGitBugRemote(options);
  try {
    return await action(remote);
  } finally {
    await remote.close();
  }
}
