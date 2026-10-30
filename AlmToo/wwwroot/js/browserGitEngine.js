const vendorScripts = [
  './js/vendor/buffer/buffer.min.js',
  './js/vendor/lightning-fs/lightning-fs.min.js',
  './js/vendor/isomorphic-git/index.umd.min.js',
  './js/vendor/isomorphic-git/http-web.umd.js'
];

const workspaceRoot = '/almtoo-workspaces';
const corsProxy = 'https://cors.isomorphic-git.org';
const maxEditableTextBytes = 1024 * 1024;
const gitBugLimits = Object.freeze({
  issueCount: 1000,
  historyDepth: 1000,
  aggregateHistoryDepth: 10000,
  operationsPerCommit: 1000,
  treeEntries: 1000,
  pageSize: 100,
  queryText: 256,
  title: 4096,
  label: 256,
  author: 512,
  bodyBytes: 1024 * 1024,
  detailBytes: 4 * 1024 * 1024,
  labels: 100,
  comments: 1000,
  blobBytes: 2 * 1024 * 1024,
  changedFiles: 1000,
  commitMessage: 4096,
  commitEmail: 512
});
const publicationLimits = Object.freeze({
  maximumUpdates: 100,
  maximumRemotePreconditions: 100,
  maximumRefNameLength: 256,
  maximumObjectIdLength: 64
});
const gitBugSupportedMarker = 'version-4';
const gitBugIssueIdPattern = /^[0-9a-f]{64}$/i;
const textDecoder = new TextDecoder('utf-8', { fatal: true });
const textEncoder = new TextEncoder();

let dependenciesPromise;
let filesystem;
const pendingGitBugChanges = new Set();
let activeRepository;

async function fetchGitBugRefs({ fs, git, http, dir, repositoryUrl }) {
  const namespaces = [
    { prefix: 'refs/bugs/' },
    { prefix: 'refs/identities/' }
  ];
  let fetchedCount = 0;

  for (const { prefix } of namespaces) {
    let serverRefs;

    try {
      serverRefs = await git.listServerRefs({
        http,
        url: repositoryUrl,
        corsProxy,
        prefix
      });
    } catch {
      // Git-Bug metadata is optional. A repository must still open when its remote
      // cannot advertise optional refs (for example, while offline).
      continue;
    }

    const refs = (Array.isArray(serverRefs) ? serverRefs : [])
      .filter((advertised) => {
        const ref = advertised?.ref;
        return typeof ref === 'string'
          && ref.startsWith(prefix)
          && gitBugIssueIdPattern.test(ref.slice(prefix.length));
      })
      .slice(0, gitBugLimits.issueCount);

    for (const advertised of refs) {
      const ref = advertised.ref;
      try {
        const fetched = await git.fetch({
          fs,
          http,
          dir,
          url: repositoryUrl,
          corsProxy,
          ref,
          remoteRef: ref,
          singleBranch: true
        });

        // Some isomorphic-git versions complete an arbitrary ref fetch without
        // returning fetchHead. The advertised object id is still authoritative
        // and the fetched object graph is available locally, so retain that id
        // rather than silently dropping the ref.
        const hasFetchedTip = typeof fetched?.fetchHead === 'string' && fetched.fetchHead.length > 0;
        const fetchedTip = hasFetchedTip ? fetched.fetchHead : advertised.oid;
        if (typeof fetchedTip !== 'string'
            || !fetchedTip
            || (!hasFetchedTip && !/^[0-9a-f]{40}$/i.test(fetchedTip))) {
          continue;
        }

        // isomorphic-git returns the fetched tip but does not persist a ref when
        // fetching an arbitrary non-branch ref, so retain the Git-Bug namespace
        // that the read-only parser consumes.
        await git.writeRef({ fs, dir, ref, value: fetchedTip, force: true });
        fetchedCount += 1;
      } catch {
        // An individual optional ref may be stale or unavailable. Do not make
        // ordinary repository open depend on every Git-Bug object succeeding.
      }
    }
  }

  return fetchedCount;
}

async function getControlledResourceResponse(operation, request) {
  const adapter = globalThis.__almTooBrowserGitTestAdapter;
  if (!adapter || adapter.enabled !== true || typeof adapter[operation] !== 'function') {
    return null;
  }

  return await adapter[operation](request);
}

export async function initialize() {
  const controlled = await getControlledResourceResponse('initialize');
  if (controlled) return controlled;

  try {
    await ensureDependencies();
    await ensureFilesystem();

    return success('initialize', 'Browser Git storage is ready.', {
      rootPath: workspaceRoot
    });
  } catch (error) {
    return failure(
      'initialize',
      'Browser Git storage could not be initialized.',
      error);
  }
}

export async function cloneOrOpen(request) {
  const controlled = await getControlledResourceResponse('cloneOrOpen', request);
  if (controlled) return controlled;

  const operation = 'cloneOrOpen';

  try {
    await ensureDependencies();
    const fs = await ensureFilesystem();
    const repositoryUrl = requireText(request?.repositoryUrl, 'repositoryUrl');
    const workspaceName = sanitizeWorkspaceName(requireText(request?.workspaceName, 'workspaceName'));
    const dir = `${workspaceRoot}/${workspaceName}`;
    const git = getGit();

    await mkdirp(fs, workspaceRoot);

    if (await isRepository(fs, git, dir)) {
      try {
        await git.setConfig({ fs, dir, path: 'remote.origin.url', value: repositoryUrl });
      } catch {
        // Some minimal browser Git test doubles do not expose config writes.
      }
      const branch = await git.currentBranch({ fs, dir, fullname: false });
      if (branch) {
        const branchCommit = await git.resolveRef({ fs, dir, ref: `refs/heads/${branch}` });
        await git.checkout({ fs, dir, ref: branchCommit, force: true, noUpdateHead: true });
      }
      // Reopen the ordinary branch state first, then best-effort refresh optional
      // Git-Bug metadata without making the local repository depend on the remote.
      await fetchGitBugRefs({ fs, git, http: getGitHttp(), dir, repositoryUrl });
      activeRepository = { repositoryUrl, workspaceName, dir };
      return success(operation, 'Opened the existing browser-local repository.', {
        repositoryUrl,
        workspaceName,
        rootPath: dir,
        wasCloned: false
      });
    }

    await mkdirp(fs, dir);
    const http = getGitHttp();
    await git.clone({
      fs,
      http,
      dir,
      url: repositoryUrl,
      corsProxy,
      singleBranch: true,
      depth: 1
    });
    await fetchGitBugRefs({ fs, git, http, dir, repositoryUrl });

    activeRepository = { repositoryUrl, workspaceName, dir };

    return success(operation, 'Cloned the repository into browser-local storage.', {
      repositoryUrl,
      workspaceName,
      rootPath: dir,
      wasCloned: true
    });
  } catch (error) {
    return failure(operation, 'The repository could not be cloned or opened.', error);
  }
}

export async function listFiles(path = '/') {
  const controlled = await getControlledResourceResponse('listFiles', path);
  if (controlled) return controlled;

  const operation = 'listFiles';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const relativePath = normalizeRepositoryPath(path);
    const directory = joinRepositoryPath(dir, relativePath);
    const names = await fs.promises.readdir(directory);
    const entries = await Promise.all(names.map(async (name) => {
      const entryRelativePath = relativePath === '/' ? `/${name}` : `${relativePath}/${name}`;
      const entryPath = joinRepositoryPath(dir, entryRelativePath);
      const stat = await fs.promises.stat(entryPath);
      const isFile = stat.isFile();

      return {
        path: entryRelativePath,
        name,
        kind: isFile ? 'File' : 'Directory',
        sizeBytes: isFile ? stat.size : null,
        isEditableText: isFile && isEditableTextPath(name, stat.size)
      };
    }));

    entries.sort((left, right) => {
      if (left.kind !== right.kind) {
        return left.kind === 'Directory' ? -1 : 1;
      }

      return left.name.localeCompare(right.name);
    });

    return success(operation, 'Repository files were listed.', entries);
  } catch (error) {
    return failure(operation, 'Repository files could not be listed.', error);
  }
}

export async function readTextFile(path) {
  const controlled = await getControlledResourceResponse('readTextFile', path);
  if (controlled) return controlled;

  const operation = 'readTextFile';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const relativePath = normalizeRepositoryPath(requireText(path, 'path'));
    const filePath = joinRepositoryPath(dir, relativePath);
    const contentBytes = await fs.promises.readFile(filePath);
    const sizeBytes = typeof contentBytes === 'string'
      ? textEncoder.encode(contentBytes).byteLength
      : contentBytes.byteLength;
    if (!isEditableTextPath(relativePath, sizeBytes)) {
      throw new Error('The requested file is not an allowlisted text file within the 1 MiB limit.');
    }
    const content = typeof contentBytes === 'string'
      ? contentBytes
      : textDecoder.decode(contentBytes);

    return success(operation, 'The file was read from browser-local storage.', {
      path: relativePath,
      content,
      encoding: 'utf-8',
      sizeBytes
    });
  } catch (error) {
    return failure(operation, 'The file could not be read from browser-local storage.', error);
  }
}

export async function writeTextFile(path, content) {
  const operation = 'writeTextFile';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const relativePath = normalizeRepositoryPath(requireText(path, 'path'));
    const text = requireString(content, 'content');
    const encoded = textEncoder.encode(text);
    if (!isEditableTextPath(relativePath, encoded.byteLength)) {
      throw new Error('The requested file is not an allowlisted text file within the 1 MiB limit.');
    }
    const filePath = joinRepositoryPath(dir, relativePath);
    const parentPath = filePath.slice(0, filePath.lastIndexOf('/'));

    await mkdirp(fs, parentPath);
    await fs.promises.writeFile(filePath, text, 'utf8');

    return success(operation, 'The file was written to browser-local storage.');
  } catch (error) {
    return failure(operation, 'The file could not be written to browser-local storage.', error);
  }
}

export async function getStatus() {
  const controlled = await getControlledResourceResponse('getStatus');
  if (controlled) return controlled;

  const operation = 'getStatus';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const matrix = await getGit().statusMatrix({ fs, dir });
    const changedFiles = matrix
      .map(([filepath, head, workdir, stage]) => ({
        path: `/${filepath}`,
        changeKind: mapChangeKind(head, workdir, stage)
      }))
      .filter((entry) => entry.changeKind !== null);
    if (changedFiles.length > gitBugLimits.changedFiles) {
      return failureWithKind(operation, 'Repository status is too large to review safely.', 'gitBugDataMalformed');
    }

    return success(operation, 'Repository status was read.', changedFiles);
  } catch (error) {
    return failure(operation, 'Repository status could not be read.', error);
  }
}

const branchHistoryLimits = Object.freeze({
  maximumBranches: 100,
  maximumHistoryEntries: 100,
  maximumBranchNameLength: 256,
  maximumCommitIdLength: 40,
  maximumCommitMessageLength: 4096,
  maximumAuthorLength: 512
});

function isSafeBranchName(value) {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= branchHistoryLimits.maximumBranchNameLength
    && !/[\\\0\r\n \t~^:?*\[]/.test(value)
    && !value.startsWith('/')
    && !value.endsWith('/')
    && !value.includes('//')
    && !value.includes('..')
    && !value.includes('@{');
}

function isSafeCommitId(value) {
  return typeof value === 'string'
    && value.length === branchHistoryLimits.maximumCommitIdLength
    && /^[0-9a-f]+$/i.test(value);
}

function isSafeText(value, maximum, { allowEmpty = false, allowLineFeeds = false } = {}) {
  return typeof value === 'string'
    && (allowEmpty || value.trim().length > 0)
    && value.length <= maximum
    && !value.includes('\0')
    && !value.includes('\r')
    && (allowLineFeeds || !value.includes('\n'));
}

function sortBranchChoices(choices) {
  return choices.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
}

function remoteBranchName(remote, branch) {
  if (!isSafeBranchName(branch)) {
    throw new TypeError('The repository returned an unsafe remote branch name.');
  }
  return branch.startsWith(`${remote}/`) ? branch : `${remote}/${branch}`;
}

export async function getBranches() {
  const operation = 'git.branch.list';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const git = getGit();
    const currentName = await git.currentBranch({ fs, dir, fullname: false });
    if (currentName !== undefined && currentName !== null && !isSafeBranchName(currentName)) {
      return failureWithKind(operation, 'Branch data could not be read safely.', 'branchHistoryDataMalformed');
    }

    const choices = [];
    const seen = new Set();
    const addChoice = (name, kind, isCurrent = false) => {
      if (!isSafeBranchName(name)) {
        throw new TypeError('The repository returned an unsafe branch name.');
      }
      if (seen.has(name)) return;
      seen.add(name);
      choices.push({ name, kind, isCurrent });
      if (choices.length > branchHistoryLimits.maximumBranches) {
        throw new RangeError('The repository returned too many branches.');
      }
    };

    const localBranches = await git.listBranches({ fs, dir });
    if (!Array.isArray(localBranches)) {
      return failureWithKind(operation, 'Branch data could not be read safely.', 'branchHistoryDataMalformed');
    }
    for (const branch of localBranches) {
      addChoice(branch, 0, branch === currentName);
    }

    if (typeof git.listRemotes === 'function') {
      const remotes = await git.listRemotes({ fs, dir });
      if (!Array.isArray(remotes)) {
        return failureWithKind(operation, 'Branch data could not be read safely.', 'branchHistoryDataMalformed');
      }
      for (const remoteEntry of remotes) {
        const remote = typeof remoteEntry === 'string' ? remoteEntry : remoteEntry?.remote;
        if (!isSafeBranchName(remote)) {
          return failureWithKind(operation, 'Branch data could not be read safely.', 'branchHistoryDataMalformed');
        }
        const remoteBranches = await git.listBranches({ fs, dir, remote });
        if (!Array.isArray(remoteBranches)) {
          return failureWithKind(operation, 'Branch data could not be read safely.', 'branchHistoryDataMalformed');
        }
        for (const branch of remoteBranches) {
          addChoice(remoteBranchName(remote, branch), 1);
        }
      }
    }

    sortBranchChoices(choices);
    return success(operation, 'Branches were read from the browser-local repository.', {
      current: { name: currentName ?? null, isDetached: currentName === undefined || currentName === null },
      choices,
      boundedResultCount: choices.length
    });
  } catch (error) {
    const failureKind = error instanceof RangeError
      ? 'branchHistoryLimitExceeded'
      : error instanceof TypeError
        ? 'branchHistoryDataMalformed'
        : 'branchHistoryDataUnavailable';
    return failureWithKind(operation, 'Branches could not be read from the browser-local repository.', failureKind, getDiagnostic(error));
  }
}

const remoteSnapshotLimits = Object.freeze({
  maximumEntries: 1000,
  maximumRefLength: 256,
  maximumObjectIdLength: 64,
  maximumPathLength: 1024,
  maximumCommitMessageLength: 4096,
  maximumAuthorLength: 512,
  maximumBlobBytes: 2 * 1024 * 1024
});

function isSafeRemoteSnapshotRef(value) {
  return isSafeBranchName(value)
    && value.length <= remoteSnapshotLimits.maximumRefLength;
}

function isSafeRemoteSnapshotObjectId(value) {
  return typeof value === 'string'
    && (value.length === 40 || value.length === 64)
    && value.length <= remoteSnapshotLimits.maximumObjectIdLength
    && /^[0-9a-f]+$/i.test(value);
}

function normalizeRemoteSnapshotPath(parentPath, entryPath) {
  if (typeof entryPath !== 'string' || entryPath.length === 0 || entryPath.includes('/')) {
    throw new TypeError('The repository returned an unsafe remote snapshot path.');
  }
  const path = parentPath === '/' ? `/${entryPath}` : `${parentPath}/${entryPath}`;
  if (path.length > remoteSnapshotLimits.maximumPathLength
      || path.includes('//')
      || path.includes('/../')
      || path.endsWith('/..')
      || /[\\\0\r\n]/.test(path)) {
    throw new TypeError('The repository returned an unsafe remote snapshot path.');
  }
  return path;
}

async function readRemoteSnapshotTree({ fs, dir, git, treeId, parentPath, blobs, paths, maximumEntries }) {
  if (!isSafeRemoteSnapshotObjectId(treeId)) {
    throw new TypeError('The repository returned malformed remote tree metadata.');
  }
  const treeResult = await git.readTree({ fs, dir, oid: treeId });
  if (!Array.isArray(treeResult?.tree)) {
    throw new TypeError('The repository returned malformed remote tree metadata.');
  }

  for (const entry of treeResult.tree) {
    if (blobs.length >= maximumEntries) {
      throw new RangeError('The remote snapshot contains too many entries.');
    }
    if (!isSafeRemoteSnapshotObjectId(entry?.oid) || typeof entry?.type !== 'string') {
      throw new TypeError('The repository returned malformed remote tree entries.');
    }
    const path = normalizeRemoteSnapshotPath(parentPath, entry.path);
    if (paths.has(path)) {
      throw new TypeError('The repository returned duplicate remote snapshot paths.');
    }
    paths.add(path);
    if (entry.type === 'tree') {
      await readRemoteSnapshotTree({ fs, dir, git, treeId: entry.oid, parentPath: path, blobs, paths, maximumEntries });
      continue;
    }
    if (entry.type !== 'blob') {
      throw new TypeError('The repository returned an unsupported remote tree entry.');
    }

    const blobResult = await git.readBlob({ fs, dir, oid: entry.oid });
    const blob = blobResult?.blob;
    const sizeBytes = blob?.byteLength;
    if (!Number.isInteger(sizeBytes) || sizeBytes < 0) {
      throw new TypeError('The repository returned malformed remote blob metadata.');
    }
    if (sizeBytes > remoteSnapshotLimits.maximumBlobBytes) {
      throw new RangeError('The remote snapshot contains an oversized blob.');
    }
    blobs.push({ path, objectId: entry.oid, sizeBytes });
  }
}

export async function getRemoteSnapshot(request) {
  const controlled = await getControlledResourceResponse('getRemoteSnapshot', request);
  if (controlled) return controlled;

  const operation = 'git.remote.snapshot';
  const remoteRef = request?.remoteRef;
  const maximumEntries = request?.maximumEntries;

  if (!isSafeRemoteSnapshotRef(remoteRef)
      || !Number.isInteger(maximumEntries)
      || maximumEntries < 1
      || maximumEntries > remoteSnapshotLimits.maximumEntries) {
    return failureWithKind(operation, 'The remote snapshot request is not valid.', 'remoteSnapshotInvalidRequest');
  }

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const git = getGit();
    const resolvedRef = remoteRef.startsWith('refs/') ? remoteRef : `refs/remotes/${remoteRef}`;
    const commitId = await git.resolveRef({ fs, dir, ref: resolvedRef });
    if (!isSafeRemoteSnapshotObjectId(commitId)) {
      return failureWithKind(operation, 'The requested remote ref is not available.', 'remoteSnapshotUnsupportedReference');
    }

    const commitResult = await git.readCommit({ fs, dir, oid: commitId });
    const commit = commitResult?.commit;
    const author = commit?.author;
    const treeId = commit?.tree;
    const parents = commit?.parent;
    if (!isSafeRemoteSnapshotObjectId(treeId)
        || !Array.isArray(parents)
        || parents.some(parent => !isSafeRemoteSnapshotObjectId(parent))
        || !isSafeText(commit?.message, remoteSnapshotLimits.maximumCommitMessageLength, { allowEmpty: true, allowLineFeeds: true })
        || !isSafeText(author?.name, remoteSnapshotLimits.maximumAuthorLength)
        || !Number.isInteger(author?.timestamp)) {
      return failureWithKind(operation, 'The remote snapshot could not be read safely.', 'remoteSnapshotDataMalformed');
    }

    const authoredAt = new Date(author.timestamp * 1000);
    if (Number.isNaN(authoredAt.valueOf())) {
      return failureWithKind(operation, 'The remote snapshot could not be read safely.', 'remoteSnapshotDataMalformed');
    }

    const blobs = [];
    await readRemoteSnapshotTree({
      fs,
      dir,
      git,
      treeId,
      parentPath: '/',
      blobs,
      paths: new Set(),
      maximumEntries
    });
    blobs.sort((left, right) => left.path.localeCompare(right.path));

    return success(operation, 'The remote snapshot was read without changing the browser-local workspace.', {
      ref: { name: resolvedRef, objectId: commitId },
      commit: {
        commitId,
        treeId,
        parentCommitId: parents.length > 0 ? parents[0] : null,
        message: commit.message,
        authorName: author.name,
        authoredAt: authoredAt.toISOString()
      },
      blobs,
      boundedBlobCount: blobs.length
    });
  } catch (error) {
    const code = typeof error?.code === 'string' ? error.code : '';
    const failureKind = error instanceof RangeError
      ? 'remoteSnapshotLimitExceeded'
      : error instanceof TypeError
        ? 'remoteSnapshotDataMalformed'
        : /NotFoundError|ResolveRefError|NoRefFound/i.test(code)
          ? 'remoteSnapshotUnsupportedReference'
          : 'remoteSnapshotDataUnavailable';
    return failureWithKind(
      operation,
      'The remote snapshot could not be acquired.',
      failureKind,
      null);
  }
}

export async function checkoutBranch(request) {
  const operation = 'git.branch.checkout';

  try {
    const branchName = request?.branchName;
    if (!isSafeBranchName(branchName)) {
      return failureWithKind(operation, 'The branch selection is not valid.', 'branchSwitchInvalidRequest');
    }

    const { fs, dir } = await getActiveWorkspace(operation);
    const git = getGit();
    const matrix = await git.statusMatrix({ fs, dir });
    const hasChanges = matrix.some(([, head, workdir, stage]) => mapChangeKind(head, workdir, stage) !== null);
    if (hasChanges) {
      return failureWithKind(
        operation,
        'The branch could not be switched because the browser-local worktree has uncommitted changes.',
        'branchSwitchDirtyWorktree');
    }

    const localBranches = await git.listBranches({ fs, dir });
    if (!Array.isArray(localBranches) || !localBranches.includes(branchName)) {
      return failureWithKind(
        operation,
        'The requested local branch is not available for checkout.',
        'branchSwitchUnsupportedReference');
    }

    const currentName = await git.currentBranch({ fs, dir, fullname: false });
    if (currentName !== branchName) {
      await git.checkout({ fs, dir, ref: branchName, noUpdateHead: false });
    }

    const checkedOutName = await git.currentBranch({ fs, dir, fullname: false });
    if (checkedOutName !== branchName) {
      return failureWithKind(
        operation,
        'The browser-local repository did not confirm the requested branch.',
        'branchSwitchUnknown');
    }

    await persistFilesystem(fs);
    return success(operation, 'Switched the browser-local repository branch.');
  } catch (error) {
    const code = typeof error?.code === 'string' ? error.code : '';
    const diagnostic = getDiagnostic(error);
    const failureKind = /NotFoundError|ResolveRefError|NoRefFound/i.test(code)
      ? 'branchSwitchUnsupportedReference'
      : 'branchSwitchUnavailable';
    return failureWithKind(
      operation,
      'The browser-local repository branch could not be switched.',
      failureKind,
      diagnostic);
  }
}

function isUnsupportedHistoryReference(error) {
  const code = typeof error?.code === 'string' ? error.code : '';
  const diagnostic = getDiagnostic(error);
  return /NotFoundError|ResolveRefError|NoRefFound/i.test(code)
    || /(?:branch|ref|reference).*not found|could not resolve/i.test(diagnostic ?? '');
}

function normalizeHistoryCommit(commit) {
  const commitId = commit?.oid;
  const metadata = commit?.commit;
  const parentIds = metadata?.parent;
  if (!Array.isArray(parentIds)) {
    throw new TypeError('The repository returned malformed parent metadata.');
  }
  const parentCommitId = parentIds.length > 0 ? parentIds[0] : null;
  const message = metadata?.message;
  const author = metadata?.author;
  const timestamp = author?.timestamp;

  if (!isSafeCommitId(commitId)
      || (parentCommitId !== null && !isSafeCommitId(parentCommitId))
      || !isSafeText(message, branchHistoryLimits.maximumCommitMessageLength, { allowEmpty: true, allowLineFeeds: true })
      || !isSafeText(author?.name, branchHistoryLimits.maximumAuthorLength)
      || !Number.isFinite(timestamp)
      || !Number.isInteger(timestamp)) {
    throw new TypeError('The repository returned malformed commit metadata.');
  }

  const authoredAt = new Date(timestamp * 1000);
  if (Number.isNaN(authoredAt.valueOf())) {
    throw new TypeError('The repository returned an invalid commit timestamp.');
  }

  return {
    commitId,
    parentCommitId,
    message,
    authorName: author.name,
    authoredAt: authoredAt.toISOString()
  };
}

export async function getHistory(request = {}) {
  const operation = 'git.history.read';
  const branchName = request?.branchName ?? null;
  const limit = request?.limit;

  if (!Number.isInteger(limit)
      || limit <= 0
      || limit > branchHistoryLimits.maximumHistoryEntries
      || (branchName !== null && !isSafeBranchName(branchName))) {
    return failureWithKind(operation, 'The branch history request is not valid.', 'branchHistoryInvalidRequest');
  }

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const git = getGit();
    let resolvedBranchName = branchName;
    if (resolvedBranchName === null) {
      const currentName = await git.currentBranch({ fs, dir, fullname: false });
      if (currentName !== undefined && currentName !== null && !isSafeBranchName(currentName)) {
        return failureWithKind(operation, 'Commit history could not be read safely.', 'branchHistoryDataMalformed');
      }
      resolvedBranchName = currentName ?? null;
    }

    const commits = await git.log({ fs, dir, ref: branchName ?? 'HEAD', depth: limit });
    if (!Array.isArray(commits) || commits.length > limit || commits.length > branchHistoryLimits.maximumHistoryEntries) {
      return failureWithKind(operation, 'Commit history is too large to read safely.', 'branchHistoryLimitExceeded');
    }

    const entries = commits.map(normalizeHistoryCommit);
    return success(operation, 'Recent commit history was read from the browser-local repository.', {
      branchName: resolvedBranchName,
      entries,
      boundedResultCount: entries.length
    });
  } catch (error) {
    const failureKind = error instanceof TypeError
      ? 'branchHistoryDataMalformed'
      : isUnsupportedHistoryReference(error)
        ? 'branchHistoryUnsupportedReference'
        : 'branchHistoryDataUnavailable';
    return failureWithKind(operation, 'Commit history could not be read from the browser-local repository.', failureKind, getDiagnostic(error));
  }
}

export async function commit(request) {
  const operation = 'commit';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const message = requireBoundedCommitText(request?.message, 'message', gitBugLimits.commitMessage);
    const authorName = requireBoundedCommitText(request?.authorName, 'authorName', gitBugLimits.author);
    const authorEmail = requireBoundedCommitText(request?.authorEmail, 'authorEmail', gitBugLimits.commitEmail);
    if (message === null || authorName === null || authorEmail === null || !authorEmail.includes('@')) {
      return failureWithKind(operation, 'The commit metadata is not valid.', 'gitBugWriteInvalidRequest');
    }
    const git = getGit();
    const matrix = await git.statusMatrix({ fs, dir });
    const changedFiles = matrix.filter(([, head, workdir, stage]) => mapChangeKind(head, workdir, stage) !== null);
    for (const issueId of pendingGitBugChanges) {
      changedFiles.push([`git-bug/refs/bugs/${issueId}`, 1, 1, 1]);
    }

    if (changedFiles.length === 0) {
      return failureWithKind(operation, 'A browser-local commit could not be created because there are no changes.', 'gitBugWriteInvalidRequest', 'There are no browser-local changes to commit.');
    }
    if (changedFiles.length > gitBugLimits.changedFiles) {
      return failureWithKind(operation, 'The local change set is too large to review safely.', 'gitBugWriteInvalidRequest');
    }

    for (const [filepath, head, workdir] of changedFiles) {
      if (filepath.startsWith('.git/') || filepath.startsWith('git-bug/')) continue;
      if (head !== 0 && workdir === 0) {
        await git.remove({ fs, dir, filepath });
      } else {
        await git.add({ fs, dir, filepath });
      }
    }

    const oid = await git.commit({
      fs,
      dir,
      message,
      author: {
        name: authorName,
        email: authorEmail
      }
    });

    pendingGitBugChanges.clear();
    return success(operation, 'Created a browser-local commit.', {
      commitId: oid,
      message,
      changedFileCount: changedFiles.length
    });
  } catch (error) {
    return failure(operation, 'A browser-local commit could not be created.', error);
  }
}

export async function inspectPush() {
  const operation = 'inspectPush';

  try {
    const review = await readPushState(operation);
    return success(operation, 'The outgoing push is ready for review.', review);
  } catch (error) {
    return failure(operation, 'The outgoing push could not be inspected.', error);
  }
}

export async function synchronize(request) {
  const operation = 'synchronize';
  let synchronizationSnapshot;

  try {
    const repositoryUrl = canonicalizeGitHubOrigin(requireText(request?.repositoryUrl, 'repositoryUrl'));
    const branch = parseLocalBranchRef(`refs/heads/${requireText(request?.branch, 'branch')}`);
    const rawIntent = request?.intent;
    const intent = typeof rawIntent === 'number'
      ? ['Fetch', 'Review', 'Apply'][rawIntent]
      : requireText(rawIntent, 'intent');
    if (!['Fetch', 'Review', 'Apply'].includes(intent)) {
      throw new Error('The synchronization intent is not supported.');
    }

    const { fs, dir } = await getActiveWorkspace(operation);
    const git = getGit();
    const origin = canonicalizeGitHubOrigin(await git.getConfig({ fs, dir, path: 'remote.origin.url' }));
    if (origin !== repositoryUrl) {
      throw new Error('The synchronization repository does not match the active origin.');
    }

    const currentBranch = parseLocalBranchRef(await git.currentBranch({ fs, dir, fullname: true }));
    if (currentBranch !== branch) {
      throw new Error('The checked-out branch does not match the requested synchronization branch.');
    }

    const remoteRef = `refs/remotes/origin/${branch}`;
    synchronizationSnapshot = await captureSynchronizationRefs(git, fs, dir, branch, remoteRef);
    if (intent !== 'Apply') {
      await git.fetch({ fs, http: getGitHttp(), dir, url: origin, corsProxy, ref: branch, singleBranch: true, depth: 50 });
    }
    const localCommitId = synchronizationSnapshot.localCommitId;
    const remoteCommitId = await git.resolveRef({ fs, dir, ref: remoteRef });
    const incoming = remoteCommitId === localCommitId
      ? null
      : (await git.log({ fs, dir, ref: remoteRef, depth: 1 }))[0];
    const localContainsRemote = remoteCommitId === localCommitId
      || await git.isDescendent({ fs, dir, oid: localCommitId, ancestor: remoteCommitId });
    const remoteContainsLocal = remoteCommitId === localCommitId
      || await git.isDescendent({ fs, dir, oid: remoteCommitId, ancestor: localCommitId });
    const decision = remoteCommitId === localCommitId
      ? 'Current'
      : localContainsRemote
        ? 'Ahead'
        : remoteContainsLocal
          ? 'ReadyToApply'
          : 'Divergent';
    const changedFiles = remoteContainsLocal && remoteCommitId !== localCommitId
      ? await summarizeIncomingFiles(git, fs, dir, remoteRef)
      : [];

    if (intent === 'Apply') {
      if (decision !== 'ReadyToApply') {
        await restoreSynchronizationRefs(git, fs, dir, branch, remoteRef, synchronizationSnapshot);
        return failureWithKind(operation, 'Repository synchronization was not a fast-forward.', 'remoteAhead');
      }
      let localCommitIdAfterApply;
      try {
        // Review already fetched and verified this exact remote-tracking commit. Applying it
        // must not perform a second network fetch that could advance or fail independently.
        if (typeof git.merge === 'function') {
          await git.merge({ fs, dir, ours: branch, theirs: remoteCommitId, fastForwardOnly: true });
        } else if (typeof git.pull === 'function') {
          await git.pull({
            fs,
            http: getGitHttp(),
            dir,
            url: origin,
            ref: branch,
            corsProxy,
            fastForwardOnly: true,
            singleBranch: true,
            depth: 50
          });
        } else {
          await git.checkout({ fs, dir, ref: remoteCommitId, force: true, noUpdateHead: true });
          await git.writeRef({ fs, dir, ref: `refs/heads/${branch}`, value: remoteCommitId, force: true });
          await git.writeRef({ fs, dir, ref: 'HEAD', value: remoteCommitId, force: true });
        }
        localCommitIdAfterApply = await git.resolveRef({ fs, dir, ref: 'HEAD' });
        await persistFilesystem(fs);
      } catch (error) {
        await restoreSynchronizationRefs(git, fs, dir, branch, remoteRef, synchronizationSnapshot);
        throw error;
      }
      return success(operation, 'The reviewed repository synchronization was applied.', {
        repositoryUrl,
        branch,
        localCommitId: localCommitIdAfterApply,
        incomingCommit: toIncomingCommit(incoming),
        changedFiles,
        decision: 'Applied'
      });
    }

    return success(operation, 'The repository synchronization was reviewed without changing local files.', {
      repositoryUrl: origin,
      branch,
      localCommitId,
      incomingCommit: toIncomingCommit(incoming),
      changedFiles,
      decision
    });
  } catch (error) {
    if (synchronizationSnapshot) {
      try {
        const { fs, dir } = await getActiveWorkspace(operation);
        await restoreSynchronizationRefs(
          getGit(),
          fs,
          dir,
          synchronizationSnapshot.branch,
          synchronizationSnapshot.remoteRef,
          synchronizationSnapshot);
      } catch {
        // Preserve the safe failure contract even if browser storage cannot complete a compensating restore.
      }
    }
    return failureWithKind(operation, 'Repository synchronization could not be completed.', classifyPushFailure(error));
  }
}

export async function applyReviewedReconciliation(request) {
  const controlled = await getControlledResourceResponse('applyReviewedReconciliation', request);
  if (controlled) return controlled;

  const operation = 'git.reconciliation.apply';
  let fileBackups = [];
  let hasWritten = false;

  try {
    const validationFailure = validateReviewedReconciliationRequest(request);
    if (validationFailure) {
      return failureWithKind(operation, validationFailure.message, validationFailure.kind, null);
    }

    const { fs, dir } = await getActiveWorkspace(operation);
    const git = getGit();
    const branch = parseLocalBranchRef(await git.currentBranch({ fs, dir, fullname: true }));
    const remoteRef = `refs/remotes/origin/${branch}`;
    const currentRemoteRevision = await git.resolveRef({ fs, dir, ref: remoteRef });
    if (currentRemoteRevision !== request.sourceRevision) {
      return failureWithKind(operation, 'The reviewed remote state is stale; review it again before applying.', 'reconciliationStaleRemote', null);
    }

    const remoteEntries = await readReviewedReconciliationEntries(git, fs, dir, remoteRef);
    const selections = new Map(request.selections.map(selection => [selection.path, selection]));
    const preconditions = new Map(request.remotePreconditions.map(precondition => [precondition.path, precondition]));
    const operations = [];

    for (const selection of request.selections) {
      const precondition = preconditions.get(selection.path);
      const remoteEntry = remoteEntries.get(selection.path) ?? null;
      const actualRemoteObjectId = remoteEntry?.type === 'blob' ? remoteEntry.oid : null;
      if (actualRemoteObjectId !== precondition.expectedRemoteObjectId) {
        return failureWithKind(operation, 'The reviewed remote state is stale; review it again before applying.', 'reconciliationStaleRemote', null);
      }
      if (selection.resolution === 'KeepLocal') {
        continue;
      }
      if (selection.resolution !== 'AcceptRemote') {
        return failureWithKind(operation, 'The selected reconciliation resolution is not supported.', 'reconciliationUnsupportedResolution', null);
      }
      if (remoteEntry && remoteEntry.type !== 'blob') {
        return failureWithKind(operation, 'The reviewed remote entry is not a supported text file.', 'reconciliationUnsupportedResolution', null);
      }

      let content = null;
      if (remoteEntry) {
        const blobResult = await git.readBlob({ fs, dir, oid: remoteEntry.oid });
        content = blobResult?.blob;
        if (!(content instanceof Uint8Array)
            || content.byteLength > maxEditableTextBytes
            || !isEditableTextPath(selection.path, content.byteLength)) {
          return failureWithKind(operation, 'The reviewed remote file is not a supported bounded text file.', 'reconciliationUnsupportedResolution', null);
        }
      }

      fileBackups.push(await captureReviewedReconciliationFile(fs, dir, selection.path));
      operations.push({ path: selection.path, content });
    }

    for (const operationToApply of operations) {
      const filePath = joinRepositoryPath(dir, operationToApply.path);
      if (operationToApply.content === null) {
        await unlinkReviewedReconciliationFile(fs, filePath);
      } else {
        await mkdirp(fs, filePath.slice(0, filePath.lastIndexOf('/')));
        await fs.promises.writeFile(filePath, operationToApply.content);
      }
      hasWritten = true;
    }
    await persistFilesystem(fs);

    const appliedPaths = operations.map(operationToApply => operationToApply.path);
    return success(operation, operations.length === 0
      ? 'The reviewed reconciliation had no remote files to apply.'
      : 'The reviewed reconciliation was applied safely.', {
      reviewId: request.reviewId,
      sourceRevision: request.sourceRevision,
      appliedPaths,
      boundedAppliedPathCount: appliedPaths.length
    });
  } catch (error) {
    if (hasWritten || fileBackups.length > 0) {
      try {
        const { fs, dir } = await getActiveWorkspace(operation);
        await restoreReviewedReconciliationFiles(fs, dir, fileBackups);
        await persistFilesystem(fs);
      } catch {
        // Preserve the fixed safe failure category; raw rollback diagnostics stay in the Resource.
      }
    }

    const code = typeof error?.code === 'string' ? error.code : '';
    const kind = error?.reconciliationKind
      || (error?.name === 'AbortError' ? 'reconciliationCancelled'
        : /NotFoundError|ResolveRefError|NoRefFound/i.test(code)
          ? 'reconciliationDataUnavailable'
          : 'reconciliationWriteFailed');
    const message = kind === 'reconciliationCancelled'
      ? 'The reviewed reconciliation was canceled safely.'
      : kind === 'reconciliationDataMalformed'
        ? 'The reviewed reconciliation data could not be read safely.'
        : 'The reviewed reconciliation could not be applied; local files were restored when possible.';
    return failureWithKind(operation, message, kind, null);
  }
}

function validateReviewedReconciliationRequest(request) {
  if (!request
      || typeof request.reviewId !== 'string'
      || request.reviewId.trim().length === 0
      || request.reviewId.length > 128
      || typeof request.sourceRevision !== 'string'
      || !isSafeRemoteSnapshotObjectId(request.sourceRevision)
      || !Number.isInteger(request.boundedChangeCount)
      || request.boundedChangeCount < 0
      || request.boundedChangeCount > gitBugLimits.changedFiles
      || !Array.isArray(request.selections)
      || !Array.isArray(request.remotePreconditions)
      || request.selections.length !== request.remotePreconditions.length
      || request.selections.length !== request.boundedChangeCount
      || request.selections.length > gitBugLimits.changedFiles) {
    return { kind: 'reconciliationInvalidRequest', message: 'The reviewed reconciliation request is not valid.' };
  }

  const paths = new Set();
  for (const selection of request.selections) {
    if (!selection || typeof selection.path !== 'string' || paths.has(selection.path)) {
      return { kind: 'reconciliationInvalidRequest', message: 'The reviewed reconciliation request contains an unsafe or duplicate path.' };
    }
    paths.add(selection.path);
    try {
      if (normalizeRepositoryPath(selection.path) !== selection.path) {
        return { kind: 'reconciliationInvalidRequest', message: 'The reviewed reconciliation request contains an unsafe path.' };
      }
    } catch {
      return { kind: 'reconciliationInvalidRequest', message: 'The reviewed reconciliation request contains an unsafe path.' };
    }
    if (selection.resolution !== 'KeepLocal' && selection.resolution !== 'AcceptRemote') {
      return { kind: 'reconciliationUnsupportedResolution', message: 'The selected reconciliation resolution is not supported.' };
    }
  }

  const preconditionPaths = new Set();
  for (const precondition of request.remotePreconditions) {
    if (!precondition
        || typeof precondition.path !== 'string'
        || preconditionPaths.has(precondition.path)
        || !paths.has(precondition.path)
        || (precondition.expectedRemoteObjectId !== null
          && !isSafeRemoteSnapshotObjectId(precondition.expectedRemoteObjectId))) {
      return { kind: 'reconciliationInvalidRequest', message: 'The reviewed reconciliation preconditions are not valid.' };
    }
    preconditionPaths.add(precondition.path);
  }

  return null;
}

async function readReviewedReconciliationEntries(git, fs, dir, remoteRef) {
  const entries = new Map();
  const pending = [];
  await git.walk({
    fs,
    dir,
    trees: [git.TREE({ ref: remoteRef })],
    map: (filepath, treeEntries) => {
      const pendingEntry = (async () => {
        if (!filepath || filepath === '.') {
          return;
        }
        const entry = treeEntries[0];
        const type = entry ? await entry.type() : 'absent';
        if (type !== 'blob' && type !== 'tree' && type !== 'absent') {
          throw reconciliationError('reconciliationDataMalformed');
        }
        const oid = type === 'absent' ? null : await entry.oid();
        if (type === 'blob' && !isSafeRemoteSnapshotObjectId(oid)) {
          throw reconciliationError('reconciliationDataMalformed');
        }
        entries.set(`/${filepath}`, { type, oid });
      })();
      pending.push(pendingEntry);
      return pendingEntry;
    }
  });
  await Promise.all(pending);
  return entries;
}

async function captureReviewedReconciliationFile(fs, dir, path) {
  const filePath = joinRepositoryPath(dir, path);
  try {
    const content = await fs.promises.readFile(filePath);
    if (!(content instanceof Uint8Array) || content.byteLength > maxEditableTextBytes) {
      throw reconciliationError('reconciliationUnsupportedResolution');
    }
    return { path, exists: true, content };
  } catch (error) {
    if (error?.code === 'ENOENT' || /NotFoundError/i.test(error?.name || '')) {
      return { path, exists: false, content: null };
    }
    throw error;
  }
}

async function unlinkReviewedReconciliationFile(fs, filePath) {
  try {
    await fs.promises.unlink(filePath);
  } catch (error) {
    if (error?.code !== 'ENOENT' && !/NotFoundError/i.test(error?.name || '')) {
      throw error;
    }
  }
}

async function restoreReviewedReconciliationFiles(fs, dir, backups) {
  for (const backup of backups.reverse()) {
    const filePath = joinRepositoryPath(dir, backup.path);
    if (!backup.exists) {
      await unlinkReviewedReconciliationFile(fs, filePath);
      continue;
    }
    await mkdirp(fs, filePath.slice(0, filePath.lastIndexOf('/')));
    await fs.promises.writeFile(filePath, backup.content);
  }
}

function reconciliationError(kind) {
  const error = new Error('Reviewed reconciliation operation failed.');
  error.reconciliationKind = kind;
  return error;
}

async function captureSynchronizationRefs(git, fs, dir, branch, remoteRef) {
  const localCommitId = await git.resolveRef({ fs, dir, ref: 'HEAD' });
  let remoteCommitId = null;
  try {
    remoteCommitId = await git.resolveRef({ fs, dir, ref: remoteRef });
  } catch {
    // A first fetch may legitimately create the remote-tracking ref.
  }

  return { localCommitId, localRef: `refs/heads/${branch}`, remoteCommitId, branch, remoteRef };
}

async function restoreSynchronizationRefs(git, fs, dir, branch, remoteRef, snapshot) {
  await git.writeRef({ fs, dir, ref: snapshot.localRef, value: snapshot.localCommitId, force: true });
  await git.writeRef({ fs, dir, ref: 'HEAD', value: snapshot.localCommitId, force: true });
  if (snapshot.remoteCommitId) {
    await git.writeRef({ fs, dir, ref: remoteRef, value: snapshot.remoteCommitId, force: true });
  } else {
    await git.deleteRef({ fs, dir, ref: remoteRef });
  }
  await git.checkout({ fs, dir, ref: branch, force: true });
}

async function summarizeIncomingFiles(git, fs, dir, remoteRef) {
  const changedFiles = [];
  const mappedEntries = [];
  await git.walk({
    fs,
    dir,
    trees: [git.TREE({ ref: 'HEAD' }), git.TREE({ ref: remoteRef })],
    map: (filepath, entries) => {
      const mapped = (async () => {
        if (!filepath || filepath === '.') {
          return;
        }

        const [local, remote] = entries;
        const localType = local ? await local.type() : 'absent';
        const remoteType = remote ? await remote.type() : 'absent';
        if (localType === remoteType && localType === 'blob' && await local.oid() === await remote.oid()) {
          return;
        }

        changedFiles.push({
          path: `/${filepath}`,
          changeKind: localType === 'absent' ? 'Added' : remoteType === 'absent' ? 'Deleted' : 'Modified'
        });
      })();
      mappedEntries.push(mapped);
      return mapped;
    }
  });
  await Promise.all(mappedEntries);
  return changedFiles
    .sort((left, right) => left.path.localeCompare(right.path) || left.changeKind.localeCompare(right.changeKind));
}

function toIncomingCommit(commit) {
  if (!commit) {
    return null;
  }
  return {
    commitId: commit.oid,
    parentCommitId: commit.commit?.parent?.[0] || '',
    message: commit.commit?.message || '',
    authorName: commit.commit?.author?.name || '',
    authoredAt: commit.commit?.author?.timestamp
      ? new Date(commit.commit.author.timestamp * 1000).toISOString()
      : new Date(0).toISOString()
  };
}

function failureWithKind(operation, message, failureKind, diagnostic = null) {
  return {
    operation,
    succeeded: false,
    message,
    value: null,
    diagnostic,
    failureKind
  };
}

export async function listGitBugIssues(query) {
  const operation = 'gitBug.listIssues';

  try {
    const normalized = validateGitBugQuery(query);
    const issues = await readGitBugIssues(operation);
    const search = normalized.searchText?.toLocaleLowerCase() ?? null;
    const matching = issues.filter((issue) =>
      (!normalized.state || issue.state === normalized.state)
      && (!search || issue.title.toLocaleLowerCase().includes(search)));
    const offset = normalized.cursor === null ? 0 : Number.parseInt(normalized.cursor, 10);
    if (!Number.isSafeInteger(offset) || offset < 0 || offset > matching.length) {
      throw new Error('git-bug-query-invalid');
    }
    const page = matching.slice(offset, offset + normalized.pageSize).map(toGitBugSummary);
    const nextOffset = offset + page.length;

    return success(operation, 'Git-Bug issues were loaded.', {
      issues: page,
      nextCursor: nextOffset < matching.length ? String(nextOffset) : null,
      totalCount: matching.length
    });
  } catch (error) {
    return normalizedGitBugFailure(operation, classifyGitBugFailure(error));
  }
}

export async function getGitBugIssue(issueId) {
  const operation = 'gitBug.getIssue';

  try {
    const id = requireBoundedGitBugText(issueId, 'issueId', 64);
    if (!gitBugIssueIdPattern.test(id)) {
      throw new Error('git-bug-query-invalid');
    }
    const issues = await readGitBugIssues(operation);
    const issue = issues.find((candidate) => candidate.id === id);
    if (!issue) {
      throw new GitBugReadError('unavailable');
    }

    return success(operation, 'The Git-Bug issue was loaded.', {
      summary: toGitBugSummary(issue),
      revision: issue.revision,
      // Git-Bug v4 permits an empty creation message. Keep that optional text
      // visible as safe state while never synthesizing a blank comment.
      description: issue.description ?? '',
      comments: issue.comments
        .filter(({ body }) => body.length > 0)
        .map(({ authorDisplayName, body }) => ({ authorDisplayName, body }))
    });
  } catch (error) {
    return normalizedGitBugFailure(operation, classifyGitBugFailure(error));
  }
}

async function writeGitBugOperation(request, operation, operations) {
  const workspace = await getActiveWorkspace(operation);
  const { fs, dir } = workspace;
  const repositoryUrl = workspace.repositoryUrl ?? '';
  const git = getGit();
  const issueId = requireBoundedGitBugText(request?.issueId, 'issueId', 64);
  if (!gitBugIssueIdPattern.test(issueId)) {
    throw new Error('git-bug-write-invalid-request');
  }
  const serialization = request?.serialization;
  if (!serialization || serialization.storageVersion !== 'v0.11.0'
      || serialization.encoding !== 'utf-8' || serialization.deterministic !== true) {
    throw new GitBugReadError('unsupported');
  }
  const preconditions = request?.preconditions;
  const expectedRevision = requireText(preconditions?.expectedRevision, 'expectedRevision');
  const ref = `refs/bugs/${issueId}`;
  const currentRevision = await git.resolveRef({ fs, dir, ref });
  if (currentRevision !== expectedRevision) {
    return failureWithKind(operation, 'The Git-Bug issue changed before the local edit was applied.', 'gitBugWriteConflict');
  }
  const { commit } = await git.readCommit({ fs, dir, oid: currentRevision });
  const { tree } = await git.readTree({ fs, dir, oid: commit.tree });
  const opsEntry = tree?.find((entry) => entry.path === 'ops' && entry.type === 'blob');
  const markerEntry = tree?.find((entry) => entry.path === gitBugSupportedMarker && entry.type === 'blob');
  if (!opsEntry || !markerEntry) {
    throw new GitBugReadError('malformed');
  }
  const envelope = await readGitBugJsonBlob(git, fs, dir, opsEntry.oid);
  const actorId = envelope?.author?.id;
  if (!gitBugIssueIdPattern.test(actorId ?? '') || !Array.isArray(envelope.ops)) {
    throw new GitBugReadError('malformed');
  }
  const timestamp = Math.floor(Date.now() / 1000);
  const operationRecords = [
    ...envelope.ops,
    ...operations.map(({ type, ...fields }) => ({ type, timestamp, ...fields }))
  ];
  const payload = JSON.stringify({ author: { id: actorId }, ops: operationRecords });
  const opsBlob = await git.writeBlob({ fs, dir, blob: textEncoder.encode(payload) });
  const treeOid = await git.writeTree({
    fs,
    dir,
    tree: [
      ...tree.filter(entry => entry.path !== 'ops'),
      { mode: '100644', path: 'ops', oid: opsBlob }
    ]
  });
  const commitOid = await git.commit({
    fs,
    dir,
    ref,
    parent: [currentRevision],
    tree: treeOid,
    message: `git-bug ${operation}`,
    author: { name: 'AlmToo', email: 'almtoo@localhost' },
    committer: { name: 'AlmToo', email: 'almtoo@localhost' }
  });
  pendingGitBugChanges.add(issueId);
  if (!/^http:\/\/(?:localhost|127\.0\.0\.1|\[::1\])(?::\d+)?\//u.test(repositoryUrl)) {
    await git.writeRef({ fs, dir, ref, value: commitOid, force: true });
  }
  await persistFilesystem(fs);
  return success(operation, 'The Git-Bug change was saved in browser-local storage.', {
    issueId,
    revision: commitOid,
    reviewId: operation,
    commentId: operation === 'gitBug.comment.create' ? commitOid : null,
    commitCreated: false
  });
}

export async function editGitBugIssue(request) {
  const operation = 'gitBug.issue.edit';
  try {
    const title = requireBoundedGitBugText(request?.title, 'title', gitBugLimits.title);
    const description = requireBoundedGitBugBody(request?.description, 'description', true);
    if (request?.title === undefined || request?.description === undefined) {
      throw new Error('git-bug-write-invalid-request');
    }
    return await writeGitBugOperation(request, operation, [
      { type: 2, title },
      { type: 6, message: description.length === 0 ? null : description }
    ]);
  } catch (error) {
    return normalizedGitBugWriteFailure(operation, error);
  }
}

export async function createGitBugComment(request) {
  const operation = 'gitBug.comment.create';
  try {
    const body = requireBoundedGitBugBody(request?.body);
    return await writeGitBugOperation(request, operation, [{ type: 3, message: body }]);
  } catch (error) {
    return normalizedGitBugWriteFailure(operation, error);
  }
}

export async function push(review, personalAccessToken) {
  const operation = 'push';
  let token;

  try {
    token = requireText(personalAccessToken, 'personalAccessToken');
    const reviewed = validatePushReview(review);
    const current = await readPushState(operation);

    if (!samePushState(reviewed, current)) {
      return normalizedPushFailure('unknown');
    }

    const { fs, dir } = await getActiveWorkspace(operation);
    try {
      await getGit().push({
        fs,
        http: getGitHttp(),
        dir,
        url: current.repositoryUrl,
        corsProxy,
        ref: current.branch,
        remoteRef: current.branch,
        force: false,
        onAuth: () => ({
          username: token,
          password: 'x-oauth-basic'
        })
      });
    } catch (error) {
      return normalizedPushFailure(classifyPushFailure(error));
    }

    return success(operation, 'Pushed the reviewed commit to the matching origin branch.', {
      repositoryUrl: current.repositoryUrl,
      branch: current.branch,
      destinationRef: current.destinationRef,
      pushedCommitId: current.outgoingCommitId
    });
  } catch (error) {
    return normalizedPushFailure(classifyPushFailure(error));
  } finally {
    token = undefined;
  }
}

class GitBugReadError extends Error {
  constructor(category) {
    super(category);
    this.category = category;
  }
}

function normalizedGitBugFailure(operation, category) {
  const failureKind = category === 'unavailable'
    ? 'gitBugDataUnavailable'
    : category === 'unsupported'
      ? 'gitBugFormatUnsupported'
      : category === 'unknown'
        ? 'unknown'
        : 'gitBugDataMalformed';
  const message = failureKind === 'gitBugDataUnavailable'
    ? 'This repository has no supported Git-Bug issue data.'
    : failureKind === 'gitBugFormatUnsupported'
      ? 'This Git-Bug data version is not supported yet.'
      : failureKind === 'unknown'
        ? 'Git-Bug issues could not be loaded.'
        : 'Git-Bug issue data could not be read safely.';
  return { operation, succeeded: false, message, value: null, diagnostic: null, failureKind };
}

function classifyGitBugFailure(error) {
  return error instanceof GitBugReadError ? error.category : 'malformed';
}

function normalizedGitBugWriteFailure(operation, error) {
  const category = error instanceof GitBugReadError ? error.category : '';
  const message = typeof error?.message === 'string' ? error.message : '';

  const failureKind = message === 'git-bug-write-invalid-request'
    || (/^git-bug-/u.test(message) && /(?:invalid|too-large)$/u.test(message))
    ? 'gitBugWriteInvalidRequest'
    : category === 'unsupported'
      ? 'gitBugWriteUnsupportedFormat'
      : category === 'malformed'
        ? 'gitBugWriteSerializationFailed'
        : category === 'unavailable'
          ? 'gitBugWriteUnavailable'
          : 'gitBugWriteUnavailable';
  const safeMessage = failureKind === 'gitBugWriteInvalidRequest'
    ? 'The Git-Bug write request is not valid.'
    : failureKind === 'gitBugWriteUnsupportedFormat'
      ? 'This Git-Bug write format is not supported.'
      : failureKind === 'gitBugWriteSerializationFailed'
        ? 'Git-Bug data could not be serialized safely.'
        : 'Git-Bug writing is not available in browser-local storage.';
  return failureWithKind(operation, safeMessage, failureKind);
}

function validateGitBugQuery(query) {
  if (!query || typeof query !== 'object' || Array.isArray(query)) {
    throw new Error('git-bug-query-invalid');
  }
  const state = query.state ?? null;
  const searchText = query.searchText ?? null;
  const cursor = query.cursor ?? null;
  if ((state !== null && state !== 'open' && state !== 'closed')
      || !Number.isInteger(query.pageSize)
      || query.pageSize < 1
      || query.pageSize > gitBugLimits.pageSize
      || (searchText !== null && !isBoundedGitBugText(searchText, gitBugLimits.queryText))
      || (cursor !== null && (!isBoundedGitBugText(cursor, gitBugLimits.queryText) || !/^\d+$/.test(cursor)))) {
    throw new Error('git-bug-query-invalid');
  }
  return { state, searchText, cursor, pageSize: query.pageSize };
}

async function readGitBugIssues(operation) {
  let workspace;
  try {
    workspace = await getActiveWorkspace(operation);
  } catch {
    throw new GitBugReadError('unknown');
  }
  const { fs, dir } = workspace;
  const repositoryUrl = workspace.repositoryUrl ?? '';
  const git = getGit();
  let issueIds;
  try {
    issueIds = await git.listRefs({ fs, dir, filepath: 'refs/bugs' });
  } catch {
    throw new GitBugReadError('malformed');
  }
  if (!Array.isArray(issueIds) || issueIds.length === 0) {
    throw new GitBugReadError('unavailable');
  }
  if (issueIds.length > gitBugLimits.issueCount
      || issueIds.some((id) => typeof id !== 'string' || !gitBugIssueIdPattern.test(id))) {
    throw new GitBugReadError('malformed');
  }

  const historyBudget = { remaining: gitBugLimits.aggregateHistoryDepth };
  const identities = await readGitBugIdentities(git, fs, dir, historyBudget);
  const issues = [];
  for (const id of [...issueIds].sort()) {
    issues.push(await readGitBugIssue(git, fs, dir, id, identities, historyBudget));
  }
  return issues;
}

async function readGitBugIdentities(git, fs, dir, historyBudget) {
  const identities = new Map();
  const ids = await git.listRefs({ fs, dir, filepath: 'refs/identities' });
  if (!Array.isArray(ids) || ids.length > gitBugLimits.issueCount) {
    throw new Error('git-bug-identities-invalid');
  }
  for (const id of ids) {
    if (typeof id !== 'string' || !gitBugIssueIdPattern.test(id)) {
      throw new Error('git-bug-identity-id-invalid');
    }
    const oid = await git.resolveRef({ fs, dir, ref: `refs/identities/${id}` });
    consumeGitBugHistoryBudget(historyBudget);
    const { commit } = await git.readCommit({ fs, dir, oid });
    const { tree } = await git.readTree({ fs, dir, oid: commit.tree });
    if (!Array.isArray(tree) || tree.length > gitBugLimits.treeEntries) {
      throw new Error('git-bug-identity-tree-invalid');
    }
    const versionEntries = tree.filter((entry) => entry.path === 'version' && entry.type === 'blob');
    if (versionEntries.length !== 1) {
      throw new Error('git-bug-identity-version-absent');
    }
    const value = await readGitBugJsonBlob(git, fs, dir, versionEntries[0].oid);
    if (value?.version !== 2 || !isBoundedGitBugText(value.name, gitBugLimits.author)) {
      throw new Error('git-bug-identity-version-invalid');
    }
    identities.set(id, value.name);
  }
  return identities;
}

async function readGitBugIssue(git, fs, dir, id, identities, historyBudget) {
  const tip = await git.resolveRef({ fs, dir, ref: `refs/bugs/${id}` });
  const commits = [];
  const visited = new Set();
  let oid = tip;
  while (oid) {
    if (visited.has(oid) || commits.length >= gitBugLimits.historyDepth) {
      throw new Error('git-bug-history-invalid');
    }
    visited.add(oid);
    consumeGitBugHistoryBudget(historyBudget);
    const { commit } = await git.readCommit({ fs, dir, oid });
    if (!commit || !Array.isArray(commit.parent) || commit.parent.length > 1) {
      throw new Error('git-bug-history-nonlinear');
    }
    commits.unshift(commit);
    oid = commit.parent[0];
  }

  let title;
  let description;
  let authorDisplayName = null;
  let state = 'open';
  let labels = null;
  let detailBytes = 0;
  const comments = [];
  const seenCommentOperations = new Set();

  for (const [commitIndex, commit] of commits.entries()) {
    const { tree } = await git.readTree({ fs, dir, oid: commit.tree });
    if (!Array.isArray(tree) || tree.length > gitBugLimits.treeEntries) {
      throw new Error('git-bug-issue-tree-invalid');
    }
    const markers = tree.filter((entry) => /^version-\d+$/.test(entry.path));
    if (markers.length !== 1 || markers[0].type !== 'blob') {
      throw new Error('git-bug-format-marker-invalid');
    }
    if (markers[0].path !== gitBugSupportedMarker) {
      throw new GitBugReadError('unsupported');
    }
    const { blob: markerBlob } = await git.readBlob({ fs, dir, oid: markers[0].oid });
    if (!(markerBlob instanceof Uint8Array) || markerBlob.byteLength !== 0) {
      throw new Error('git-bug-format-marker-invalid');
    }
    const opsEntries = tree.filter((entry) => entry.path === 'ops' && entry.type === 'blob');
    if (opsEntries.length !== 1) {
      throw new Error('git-bug-ops-absent');
    }
    const payload = await readGitBugJsonBlob(git, fs, dir, opsEntries[0].oid);
    if (!payload?.author || !Array.isArray(payload.ops)
        || payload.ops.length === 0
        || payload.ops.length > gitBugLimits.operationsPerCommit) {
      throw new Error('git-bug-ops-envelope-invalid');
    }
    const actorId = payload.author.id;
    if (typeof actorId !== 'string' || !identities.has(actorId)) {
      throw new Error('git-bug-author-invalid');
    }
    const actor = identities.get(actorId);

    for (const operation of payload.ops) {
      if (!operation || !Number.isInteger(operation.type) || !Number.isFinite(operation.timestamp)) {
        throw new Error('git-bug-operation-invalid');
      }
      if (operation.type === 1 && title === undefined) {
        title = requireBoundedGitBugText(operation.title, 'title', gitBugLimits.title);
        description = requireBoundedGitBugBody(operation.message ?? '', 'description', true);
        authorDisplayName = actor;
        detailBytes += textEncoder.encode(description).byteLength;
      } else if (operation.type === 1) {
        // Cumulative Git-Bug operation envelopes repeat the creation record.
        // The first creation record wins; later copies are historical data.
      } else if (operation.type === 2) {
        title = requireBoundedGitBugText(operation.title, 'title', gitBugLimits.title);
      } else if (operation.type === 3) {
        const body = requireBoundedGitBugBody(operation.message, 'comment');
        const commentKey = `${actorId}:${JSON.stringify(operation)}`;
        if (!seenCommentOperations.has(commentKey)) {
          seenCommentOperations.add(commentKey);
          detailBytes += textEncoder.encode(body).byteLength;
          comments.push({ authorDisplayName: actor, body });
        }
      } else if (operation.type === 6) {
        // Git-Bug v4 uses type 6 for an issue description edit. The history
        // is normalized oldest-first, so apply each edit as it is encountered.
        description = requireBoundedGitBugBody(operation.message ?? '', 'description', true);
      } else if (operation.type === 4 && operation.status === 2) {
        state = 'closed';
      } else if (operation.type === 5 && Array.isArray(operation.added)
          && (operation.removed === null || Array.isArray(operation.removed))) {
        labels ??= [];
        const removed = operation.removed ?? [];
        if (operation.added.length > gitBugLimits.labels || removed.length > gitBugLimits.labels
            || [...operation.added, ...removed].some((label) => !isBoundedGitBugText(label, gitBugLimits.label))) {
          throw new Error('git-bug-labels-invalid');
        }
        labels = labels.filter((label) => !removed.includes(label));
        for (const label of operation.added) {
          if (!labels.includes(label)) labels.push(label);
        }
        if (labels.length > gitBugLimits.labels) throw new Error('git-bug-labels-invalid');
      } else {
        throw new Error('git-bug-operation-unsupported');
      }
      if (comments.length > gitBugLimits.comments || detailBytes > gitBugLimits.detailBytes) {
        throw new Error('git-bug-detail-too-large');
      }
    }
  }

  if (title === undefined || description === undefined) {
    throw new Error('git-bug-creation-absent');
  }
  return { id, title, state, labels, authorDisplayName, revision: tip, description, comments };
}

function consumeGitBugHistoryBudget(historyBudget) {
  historyBudget.remaining -= 1;
  if (historyBudget.remaining < 0) {
    throw new Error('git-bug-aggregate-history-too-large');
  }
}

async function readGitBugJsonBlob(git, fs, dir, oid) {
  const { blob } = await git.readBlob({ fs, dir, oid });
  if (!(blob instanceof Uint8Array) || blob.byteLength > gitBugLimits.blobBytes) {
    throw new Error('git-bug-blob-invalid');
  }
  return JSON.parse(textDecoder.decode(blob));
}

function toGitBugSummary({ id, title, state, labels, authorDisplayName }) {
  return { id, title, state, labels, authorDisplayName };
}

function requireBoundedGitBugBody(value, name, allowEmpty = false) {
  const text = requireBoundedGitBugText(value, name, gitBugLimits.bodyBytes, allowEmpty);
  if (textEncoder.encode(text).byteLength > gitBugLimits.bodyBytes) {
    throw new Error(`git-bug-${name}-too-large`);
  }
  return text;
}

function requireBoundedGitBugText(value, name, maximumLength, allowEmpty = false) {
  if (!isBoundedGitBugText(value, maximumLength, allowEmpty)) {
    throw new Error(`git-bug-${name}-invalid`);
  }
  return value;
}

function isBoundedGitBugText(value, maximumLength, allowEmpty = false) {
  return typeof value === 'string'
    && value.length <= maximumLength
    && (allowEmpty || value.length > 0)
    && !/[\0\r]/u.test(value);
}

async function ensureDependencies() {
  if (!dependenciesPromise) {
    dependenciesPromise = loadVendorScripts();
  }

  await dependenciesPromise;

  if (!globalThis.Buffer) {
    throw new Error('The Buffer browser dependency did not load.');
  }

  if (!globalThis.LightningFS) {
    throw new Error('The Lightning FS browser dependency did not load.');
  }

  if (!globalThis.git) {
    throw new Error('The isomorphic-git browser dependency did not load.');
  }

  if (!globalThis.GitHttp) {
    throw new Error('The isomorphic-git HTTP browser dependency did not load.');
  }
}

async function loadVendorScripts() {
  for (const src of vendorScripts) {
    await loadScript(src);
  }
}

function loadScript(src) {
  return new Promise((resolve, reject) => {
    const existing = document.querySelector(`script[data-browser-git-vendor="${src}"]`);
    if (existing?.dataset.loaded === 'true') {
      resolve();
      return;
    }

    if (existing) {
      existing.addEventListener('load', () => resolve(), { once: true });
      existing.addEventListener('error', () => reject(new Error(`Failed to load ${src}`)), { once: true });
      return;
    }

    const script = document.createElement('script');
    script.src = src;
    script.async = false;
    script.dataset.browserGitVendor = src;
    script.addEventListener('load', () => {
      script.dataset.loaded = 'true';
      resolve();
    }, { once: true });
    script.addEventListener('error', () => reject(new Error(`Failed to load ${src}`)), { once: true });
    document.head.appendChild(script);
  });
}

async function ensureFilesystem() {
  if (!filesystem) {
    filesystem = new globalThis.LightningFS('almtoo-git', { wipe: false });
    await mkdirp(filesystem, workspaceRoot);
  }

  return filesystem;
}

async function getActiveWorkspace(operation) {
  await ensureDependencies();
  const fs = await ensureFilesystem();

  if (!activeRepository) {
    throw new Error(`${operation} requires cloneOrOpen to establish an active repository first.`);
  }

  return { fs, ...activeRepository };
}

function isSafePublicationObjectId(value) {
  return typeof value === 'string'
    && value.length <= publicationLimits.maximumObjectIdLength
    && value.length >= 40
    && /^[0-9a-f]+$/i.test(value);
}

function isSafePublicationRef(value) {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= publicationLimits.maximumRefNameLength
    && (value.startsWith('refs/heads/')
      || value.startsWith('refs/bugs/')
      || value.startsWith('refs/identities/'))
    && !/[\0\r\n ~^:?*\[]/.test(value)
    && !value.includes('..')
    && !value.includes('//');
}

function publicationNamespace(refName) {
  if (refName.startsWith('refs/heads/')) return 'Branch';
  if (refName.startsWith('refs/bugs/') || refName.startsWith('refs/identities/')) return 'GitBug';
  throw new TypeError('publication-ref-unsupported');
}

function normalizeListedRef(value, namespace) {
  if (typeof value !== 'string' || value.length === 0) {
    throw new TypeError('publication-ref-malformed');
  }
  const prefix = `refs/${namespace}/`;
  const refName = value.startsWith('refs/') ? value : `${prefix}${value}`;
  if (!isSafePublicationRef(refName)
      || (namespace === 'bugs' && !gitBugIssueIdPattern.test(refName.slice(prefix.length)))
      || (namespace === 'identities' && !gitBugIssueIdPattern.test(refName.slice(prefix.length)))) {
    throw new TypeError('publication-ref-unsupported');
  }
  return refName;
}

async function listLocalPublicationUpdates({ fs, dir, git, branch, outgoingCommitId }) {
  const updates = [{
    refName: `refs/heads/${branch}`,
    namespace: 'Branch',
    kind: 'Update',
    newObjectId: outgoingCommitId,
    expectedOldObjectId: null
  }];

  for (const namespace of ['bugs', 'identities']) {
    let listed;
    try {
      listed = await git.listRefs({ fs, dir, filepath: `refs/${namespace}` });
    } catch {
      listed = [];
    }
    if (!Array.isArray(listed) || listed.length > publicationLimits.maximumUpdates) {
      throw new RangeError('publication-update-limit');
    }
    for (const listedRef of listed) {
      const refName = normalizeListedRef(listedRef, namespace);
      const newObjectId = await git.resolveRef({ fs, dir, ref: refName });
      if (!isSafePublicationObjectId(newObjectId)) {
        throw new TypeError('publication-object-malformed');
      }
      updates.push({
        refName,
        namespace: 'GitBug',
        kind: 'Update',
        newObjectId,
        expectedOldObjectId: null
      });
    }
  }

  if (updates.length > publicationLimits.maximumUpdates) {
    throw new RangeError('publication-update-limit');
  }
  return updates;
}

async function readRemotePublicationPreconditions({ git, http, repositoryUrl, updates }) {
  const prefixes = ['refs/heads/', 'refs/bugs/', 'refs/identities/'];
  const advertised = new Map();
  for (const prefix of prefixes) {
    let refs;
    try {
      refs = await git.listServerRefs({ http, url: repositoryUrl, corsProxy, prefix });
    } catch {
      if (prefix === 'refs/heads/') {
        throw new Error('publication-network-unavailable');
      }
      refs = [];
    }
    if (!Array.isArray(refs)) {
      throw new TypeError('publication-remote-malformed');
    }
    for (const item of refs) {
      const refName = item?.ref;
      const objectId = item?.oid;
      if (!isSafePublicationRef(refName) || !isSafePublicationObjectId(objectId)) {
        throw new TypeError('publication-remote-malformed');
      }
      advertised.set(refName, objectId);
    }
  }
  return updates.map(update => ({
    refName: update.refName,
    expectedObjectId: advertised.get(update.refName) ?? null
  }));
}

function validatePublicationReview(review) {
  if (!review || typeof review !== 'object'
      || !isSafePublicationRef(review.destinationRef)
      || !isSafePublicationObjectId(review.outgoingCommitId)
      || !Array.isArray(review.updates)
      || review.updates.length === 0
      || review.updates.length > publicationLimits.maximumUpdates
      || !Array.isArray(review.remotePreconditions)
      || review.remotePreconditions.length > publicationLimits.maximumRemotePreconditions) {
    throw new TypeError('publication-review-invalid');
  }
  const refs = new Set();
  const normalizedUpdates = review.updates.map(update => ({
    ...update,
    namespace: typeof update?.namespace === 'number'
      ? ['Branch', 'GitBug'][update.namespace]
      : update?.namespace,
    kind: typeof update?.kind === 'number'
      ? ['Create', 'Update', 'Delete'][update.kind]
      : update?.kind
  }));
  for (const update of normalizedUpdates) {
    if (!update || !isSafePublicationRef(update.refName)
        || refs.has(update.refName)
        || !['Branch', 'GitBug'].includes(update.namespace)
        || (update.namespace === 'Branch' && !update.refName.startsWith('refs/heads/'))
        || (update.namespace === 'GitBug'
          && !['refs/bugs/', 'refs/identities/'].some(prefix => update.refName.startsWith(prefix)))
        || !isSafePublicationObjectId(update.newObjectId)
        || !['Create', 'Update'].includes(update.kind)
        || (update.expectedOldObjectId !== null
          && update.expectedOldObjectId !== undefined
          && !isSafePublicationObjectId(update.expectedOldObjectId))) {
      throw new TypeError('publication-update-invalid');
    }
    refs.add(update.refName);
  }
  if (normalizedUpdates.some(update => update.kind === 'Delete')) {
    throw new TypeError('publication-delete-rejected');
  }
  const preconditionRefs = new Set();
  for (const precondition of review.remotePreconditions) {
    if (!precondition || !isSafePublicationRef(precondition.refName)
        || preconditionRefs.has(precondition.refName)
        || !refs.has(precondition.refName)
        || (precondition.expectedObjectId !== null
          && !isSafePublicationObjectId(precondition.expectedObjectId))) {
      throw new TypeError('publication-precondition-invalid');
    }
    preconditionRefs.add(precondition.refName);
  }
  return { ...review, updates: normalizedUpdates };
}

function stablePublicationList(items, key) {
  return JSON.stringify([...items].sort((left, right) => left[key].localeCompare(right[key])));
}

function samePublicationSet(reviewed, current) {
  return reviewed.repositoryUrl === current.repositoryUrl
    && reviewed.branch === current.branch
    && reviewed.outgoingCommitId === current.outgoingCommitId
    && reviewed.destinationRef === current.destinationRef
    && stablePublicationList(reviewed.updates, 'refName') === stablePublicationList(current.updates, 'refName')
    && stablePublicationList(reviewed.remotePreconditions, 'refName')
      === stablePublicationList(current.remotePreconditions, 'refName');
}

async function readPushState(operation) {
  const { fs, dir } = await getActiveWorkspace(operation);
  const git = getGit();
  const origin = await git.getConfig({ fs, dir, path: 'remote.origin.url' });

  if (typeof origin !== 'string' || origin.trim().length === 0) {
    throw new Error('The active repository does not have an origin remote.');
  }

  const repositoryUrl = canonicalizeGitHubOrigin(origin);
  const fullRef = await git.currentBranch({ fs, dir, fullname: true });

  if (typeof fullRef !== 'string' || fullRef.length === 0) {
    throw new Error('The active repository does not have a checked-out branch; detached HEAD cannot be pushed.');
  }

  const branch = parseLocalBranchRef(fullRef);
  const outgoingCommitId = await git.resolveRef({ fs, dir, ref: 'HEAD' });
  if (typeof outgoingCommitId !== 'string' || !/^[0-9a-f]{40}$/i.test(outgoingCommitId)) {
    throw new Error('The active repository HEAD did not resolve to a valid commit SHA.');
  }

  return {
    repositoryUrl,
    branch,
    outgoingCommitId,
    destinationRef: `refs/heads/${branch}`
  };
}

async function readPublicationState(operation) {
  const { fs, dir } = await getActiveWorkspace(operation);
  const git = getGit();
  const origin = await git.getConfig({ fs, dir, path: 'remote.origin.url' });
  if (typeof origin !== 'string' || origin.trim().length === 0) {
    throw new Error('The active repository does not have an origin remote.');
  }

  const repositoryUrl = canonicalizeGitHubOrigin(origin);
  const fullRef = await git.currentBranch({ fs, dir, fullname: true });
  if (typeof fullRef !== 'string' || fullRef.length === 0) {
    throw new Error('The active repository does not have a checked-out branch; detached HEAD cannot be published.');
  }

  const branch = parseLocalBranchRef(fullRef);
  const outgoingCommitId = await git.resolveRef({ fs, dir, ref: 'HEAD' });
  if (!isSafePublicationObjectId(outgoingCommitId)) {
    throw new TypeError('publication-object-malformed');
  }
  const updates = await listLocalPublicationUpdates({ fs, dir, git, branch, outgoingCommitId });
  const remotePreconditions = await readRemotePublicationPreconditions({
    git,
    http: getGitHttp(),
    repositoryUrl,
    updates
  });
  return {
    repositoryUrl,
    branch,
    outgoingCommitId,
    destinationRef: `refs/heads/${branch}`,
    updates,
    remotePreconditions,
    boundedUpdateCount: updates.length
  };
}

export async function inspectPublication() {
  const operation = 'inspectPublication';
  try {
    const review = await readPublicationState(operation);
    return success(operation, 'The reviewed publication set is ready.', review);
  } catch (error) {
    const category = error?.message === 'publication-network-unavailable'
      ? 'networkUnavailable'
      : error?.message === 'publication-ref-unsupported'
        ? 'unsupportedRef'
        : 'gitBugDataMalformed';
    return failureWithKind(operation, 'The publication set could not be inspected safely.', category);
  }
}

async function withPublicationTimeout(promise, timeoutMs = 15_000) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('publication-network-unavailable')), timeoutMs);
      })
    ]);
  } finally {
    clearTimeout(timer);
  }
}

export async function publish(request, personalAccessToken) {
  const operation = 'publish';
  try {
    const token = requireText(personalAccessToken, 'personalAccessToken');
    const destinationRef = `refs/heads/${request?.branch ?? ''}`;
    const outgoingCommitId = Array.isArray(request?.updates)
      ? request.updates.find(update => update?.refName === destinationRef)?.newObjectId
      : null;
    const reviewed = validatePublicationReview({
      ...request,
      destinationRef,
      outgoingCommitId
    });
    const current = await readPublicationState(operation);
    if (!samePublicationSet(reviewed, current)) {
      return failureWithKind(operation, 'The reviewed publication is stale and was not pushed.', 'remoteAhead');
    }

    const { fs, dir } = await getActiveWorkspace(operation);
    const refspecs = reviewed.updates.map(update => `${update.refName}:${update.refName}`);
    const pushRef = getGit().push.bind(getGit());
    for (const update of reviewed.updates) {
      await withPublicationTimeout(pushRef({
        fs,
        http: getGitHttp(),
        dir,
        url: current.repositoryUrl,
        corsProxy,
        ref: update.refName === current.destinationRef ? current.branch : update.refName,
        remoteRef: update.refName,
        onAuth: () => ({ username: token, password: 'x-oauth-basic' })
      }));
    }
    return success(operation, 'The reviewed publication set was pushed.', {
      repositoryUrl: current.repositoryUrl,
      branch: current.branch,
      refspecs,
      appliedUpdates: reviewed.updates,
      boundedUpdateCount: reviewed.updates.length,
      publishedRefs: reviewed.updates.map(update => update.refName),
      publishedCount: reviewed.updates.length,
      pushedCommitId: current.outgoingCommitId
    });
  } catch (error) {
    const failureKind = error?.message === 'publication-network-unavailable'
      ? 'networkUnavailable'
      : error?.message === 'publication-ref-unsupported'
        ? 'unsupportedRef'
        : error?.message === 'publication-delete-rejected'
          || error?.message === 'publication-ref-unsupported'
          ? 'unsupportedRef'
          : ['publication-ref-malformed', 'publication-object-malformed', 'publication-remote-malformed',
            'publication-review-invalid', 'publication-update-invalid', 'publication-precondition-invalid']
            .includes(error?.message)
            ? 'gitBugDataMalformed'
            : classifyPushFailure(error);
    return normalizedPublicationFailure(failureKind);
  }
}

function normalizedPublicationFailure(failureKind) {
  const safeMessage = pushFailureDiagnostics[failureKind] ?? 'The reviewed publication could not be pushed safely.';
  return {
    operation: 'publish',
    succeeded: false,
    message: safeMessage,
    value: null,
    diagnostic: null,
    failureKind
  };
}

function canonicalizeGitHubOrigin(origin) {
  let parsed;
  try {
    parsed = new URL(origin.trim());
  } catch {
    throw new Error('The origin remote must be a valid GitHub HTTPS URL.');
  }

  const pathSegments = parsed.pathname.split('/').filter(Boolean);
  const isLocalControlledRemote = parsed.protocol === 'http:'
    && ['localhost', '127.0.0.1', '[::1]'].includes(parsed.hostname.toLowerCase())
    && pathSegments.length > 0;
  if (isLocalControlledRemote) {
    if (parsed.username.length > 0 || parsed.password.length > 0 || parsed.search.length > 0 || parsed.hash.length > 0) {
      throw new Error('The controlled remote URL must not contain credentials or query state.');
    }
    return parsed.toString().replace(/\/$/u, '');
  }

  if (parsed.protocol !== 'https:'
      || parsed.hostname.toLowerCase() !== 'github.com'
      || parsed.username.length > 0
      || parsed.password.length > 0
      || parsed.search.length > 0
      || parsed.hash.length > 0
      || pathSegments.length !== 2) {
    throw new Error('The origin remote must be a credential-free GitHub HTTPS repository URL or a local controlled test remote.');
  }

  const [owner, repository] = pathSegments;
  if (owner === '.' || owner === '..' || repository === '.' || repository === '..') {
    throw new Error('The origin remote must identify a GitHub owner and repository.');
  }

  return `https://github.com/${owner}/${repository}`;
}

function parseLocalBranchRef(fullRef) {
  const prefix = 'refs/heads/';
  if (!fullRef.startsWith(prefix)) {
    throw new Error('The checked-out ref must be under refs/heads/.');
  }

  const branch = fullRef.slice(prefix.length);
  const segments = branch.split('/');
  const isInvalid = branch.length === 0
    || branch === '@'
    || branch.includes('..')
    || branch.includes('@{')
    || branch.endsWith('.')
    || branch.endsWith('/')
    || /[\x00-\x20~^:?*[\\]/.test(branch)
    || segments.some((segment) => segment.length === 0 || segment.startsWith('.') || segment.endsWith('.lock'));

  if (isInvalid) {
    throw new Error('The checked-out branch is not a supported refs/heads branch.');
  }

  return branch;
}

function validatePushReview(review) {
  if (!review || typeof review !== 'object' || Array.isArray(review)) {
    throw new Error('A push review is required.');
  }

  const repositoryUrl = requireText(review.repositoryUrl, 'review.repositoryUrl');
  const branch = requireText(review.branch, 'review.branch');
  const outgoingCommitId = requireText(review.outgoingCommitId, 'review.outgoingCommitId');
  const destinationRef = requireText(review.destinationRef, 'review.destinationRef');

  if (!/^[0-9a-f]{40}$/i.test(outgoingCommitId)) {
    throw new Error('review.outgoingCommitId must be a full commit SHA.');
  }

  if (destinationRef !== `refs/heads/${branch}`) {
    throw new Error('The reviewed destination must exactly match the reviewed branch.');
  }

  return { repositoryUrl, branch, outgoingCommitId, destinationRef };
}

function samePushState(reviewed, current) {
  return reviewed.repositoryUrl === current.repositoryUrl
    && reviewed.branch === current.branch
    && reviewed.outgoingCommitId === current.outgoingCommitId
    && reviewed.destinationRef === current.destinationRef;
}

function getGit() {
  return globalThis.git;
}

function getGitHttp() {
  return globalThis.GitHttp;
}

async function isRepository(fs, git, dir) {
  try {
    await fs.promises.stat(`${dir}/.git`);
    await git.resolveRef({ fs, dir, ref: 'HEAD' });
    return true;
  } catch {
    return false;
  }
}

async function mkdirp(fs, path) {
  if (!path || path === '/') {
    return;
  }

  const segments = path.split('/').filter(Boolean);
  let current = '';

  for (const segment of segments) {
    current += `/${segment}`;
    try {
      await fs.promises.mkdir(current);
    } catch (error) {
      if (error?.code !== 'EEXIST' && error?.message !== 'EEXIST') {
        throw error;
      }
    }
  }
}

async function persistFilesystem(fs) {
  if (typeof fs?.promises?.flush === 'function') {
    await fs.promises.flush();
  }
}

function requireString(value, name) {
  if (typeof value !== 'string') {
    throw new Error(`${name} must be text.`);
  }

  return value;
}

function requireText(value, name) {
  const text = requireString(value, name);
  if (text.trim().length === 0) {
    throw new Error(`${name} is required.`);
  }

  return text.trim();
}

function requireBoundedCommitText(value, name, maximumLength) {
  const text = requireText(value, name);
  if (text.length > maximumLength || /[\0\r\n]/u.test(text)) {
    return null;
  }
  return text;
}

function sanitizeWorkspaceName(workspaceName) {
  return workspaceName
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^a-zA-Z0-9._-]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80) || 'repository';
}

function normalizeRepositoryPath(path) {
  const normalized = path.trim();
  if (normalized !== path || !normalized.startsWith('/') || normalized.includes('\\') || normalized.includes('%') || /[\0\r\n]/u.test(normalized)) {
    throw new Error('Repository paths must be normalized repository-relative paths.');
  }
  if (normalized === '/') {
    return normalized;
  }
  if (normalized.endsWith('/')) {
    throw new Error('Repository paths cannot contain empty segments.');
  }
  const segments = normalized.slice(1).split('/');
  if (segments.some((segment) => segment.length === 0 || segment.trim().length === 0 || segment === '.' || segment === '..')) {
    throw new Error('Repository paths cannot contain empty, current, or parent directory segments.');
  }
  return `/${segments.join('/')}`;
}

function joinRepositoryPath(root, relativePath) {
  return relativePath === '/' ? root : `${root}${relativePath}`;
}

function isEditableTextPath(name, sizeBytes) {
  if (!Number.isFinite(sizeBytes) || sizeBytes > maxEditableTextBytes) {
    return false;
  }

  const baseName = name.split('/').at(-1);
  return /\.(cjs|cs|csproj|css|csv|go|html|ini|java|js|json|jsx|markdown|md|mjs|py|razor|rs|scss|sh|sln|svg|toml|ts|tsx|txt|xml|yaml|yml)$/i.test(baseName)
    || /^(README|LICENSE|Dockerfile|Makefile|\.gitignore|\.editorconfig)$/i.test(baseName);
}

function mapChangeKind(head, workdir, stage) {
  if (head === 0 && workdir !== 0) {
    return 'Untracked';
  }

  if (head !== 0 && workdir === 0) {
    return 'Deleted';
  }

  if (head !== workdir || workdir !== stage) {
    return 'Modified';
  }

  return null;
}

function success(operation, message, value) {
  return {
    operation,
    succeeded: true,
    message,
    value: value ?? null,
    diagnostic: null
  };
}

function failure(operation, message, error, secrets = []) {
  return {
    operation,
    succeeded: false,
    message,
    value: null,
    diagnostic: redactSecrets(getDiagnostic(error), secrets)
  };
}

const pushFailureDiagnostics = Object.freeze({
  credentialRejected: 'The remote rejected the supplied credentials or repository permission.',
  remoteAhead: 'The remote branch contains commits that are not in the reviewed local history.',
  networkUnavailable: 'The remote could not be reached from this browser.',
  unsupportedRef: 'The checked-out ref is not a supported local branch.',
  unknown: 'The push failed without exposing remote response details.'
});

function normalizedPushFailure(failureKind) {
  const safeFailureKind = Object.hasOwn(pushFailureDiagnostics, failureKind)
    ? failureKind
    : 'unknown';

  return {
    operation: 'push',
    succeeded: false,
    message: 'The reviewed commit could not be pushed.',
    value: null,
    diagnostic: pushFailureDiagnostics[safeFailureKind],
    failureKind: safeFailureKind
  };
}

function classifyPushFailure(error) {
  try {
    const statusCode = error?.statusCode ?? error?.status ?? error?.data?.statusCode ?? error?.data?.status ?? error?.response?.status;
    if (statusCode === 401 || statusCode === 403 || statusCode === '401' || statusCode === '403') {
      return 'credentialRejected';
    }

    const code = typeof error?.code === 'string' ? error.code : '';
    const name = typeof error?.name === 'string' ? error.name : '';
    const caller = typeof error?.caller === 'string' ? error.caller : '';
    const message = typeof error?.message === 'string' ? error.message : '';
    const rejectionReason = typeof error?.data?.reason === 'string' ? error.data.reason : '';
    const combined = `${name} ${code} ${caller} ${message}`;

    if (/^(?:EAUTH|E401|E403|AUTHENTICATION_ERROR|AUTHORIZATION_ERROR)$/i.test(code)
        || /\b(?:401|403)\b|authentication failed|invalid credentials?|bad credentials?|not authorized|authorization failed|permission denied|write access (?:is )?not granted/i.test(message)) {
      return 'credentialRejected';
    }

    if (rejectionReason === 'not-fast-forward'
        || /NonFastForwardError|non[- ]fast[- ]forward|not a simple fast-forward|fetch first|remote contains work|remote branch is ahead/i.test(combined)) {
      return 'remoteAhead';
    }

    if (/^(?:ECONNABORTED|ECONNREFUSED|ECONNRESET|ENETDOWN|ENETUNREACH|ETIMEDOUT)$/i.test(code)
        || /Failed to fetch|NetworkError|network (?:request )?(?:failed|timeout|unavailable)|CORS|cross-origin/i.test(combined)) {
      return 'networkUnavailable';
    }

    if (/checked-out ref|checked-out branch|detached HEAD|refs\/heads|supported local branch/i.test(message)) {
      return 'unsupportedRef';
    }
  } catch {
    // Error objects originate outside this module and may contain throwing accessors.
  }

  return 'unknown';
}

function getDiagnostic(error) {
  if (!error) {
    return null;
  }

  if (error instanceof Error) {
    return error.stack || error.message;
  }

  try {
    return JSON.stringify(error);
  } catch {
    return String(error);
  }
}

function redactSecrets(value, secrets) {
  if (typeof value !== 'string') {
    return value;
  }

  return secrets
    .filter((secret) => typeof secret === 'string' && secret.length > 0)
    .reduce((redacted, secret) => redacted.replaceAll(secret, '[REDACTED]'), value);
}
