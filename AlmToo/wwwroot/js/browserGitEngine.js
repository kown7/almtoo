const vendorScripts = [
  './js/vendor/buffer/buffer.min.js',
  './js/vendor/lightning-fs/lightning-fs.min.js',
  './js/vendor/isomorphic-git/index.umd.min.js',
  './js/vendor/isomorphic-git/http-web.umd.js'
];

const workspaceRoot = '/almtoo-workspaces';
const corsProxy = 'https://cors.isomorphic-git.org';
const textDecoder = new TextDecoder('utf-8', { fatal: false });
const textEncoder = new TextEncoder();

let dependenciesPromise;
let filesystem;
let activeRepository;

export async function initialize() {
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
      activeRepository = { repositoryUrl, workspaceName, dir };
      return success(operation, 'Opened the existing browser-local repository.', {
        repositoryUrl,
        workspaceName,
        rootPath: dir,
        wasCloned: false
      });
    }

    await mkdirp(fs, dir);
    await git.clone({
      fs,
      http: getGitHttp(),
      dir,
      url: repositoryUrl,
      corsProxy,
      singleBranch: true,
      depth: 1
    });

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
        isEditableText: isFile && isEditableTextPath(name)
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
  const operation = 'readTextFile';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const relativePath = normalizeRepositoryPath(requireText(path, 'path'));
    const filePath = joinRepositoryPath(dir, relativePath);
    const contentBytes = await fs.promises.readFile(filePath);
    const content = typeof contentBytes === 'string'
      ? contentBytes
      : textDecoder.decode(contentBytes);

    return success(operation, 'The file was read from browser-local storage.', {
      path: relativePath,
      content,
      encoding: 'utf-8',
      sizeBytes: typeof contentBytes === 'string' ? textEncoder.encode(contentBytes).byteLength : contentBytes.byteLength
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
    const filePath = joinRepositoryPath(dir, relativePath);
    const parentPath = filePath.slice(0, filePath.lastIndexOf('/'));

    await mkdirp(fs, parentPath);
    await fs.promises.writeFile(filePath, content ?? '', 'utf8');

    return success(operation, 'The file was written to browser-local storage.');
  } catch (error) {
    return failure(operation, 'The file could not be written to browser-local storage.', error);
  }
}

export async function getStatus() {
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

    return success(operation, 'Repository status was read.', changedFiles);
  } catch (error) {
    return failure(operation, 'Repository status could not be read.', error);
  }
}

export async function commit(request) {
  const operation = 'commit';

  try {
    const { fs, dir } = await getActiveWorkspace(operation);
    const message = requireText(request?.message, 'message');
    const authorName = requireText(request?.authorName, 'authorName');
    const authorEmail = requireText(request?.authorEmail, 'authorEmail');
    const git = getGit();
    const matrix = await git.statusMatrix({ fs, dir });
    const changedFiles = matrix.filter(([, head, workdir, stage]) => mapChangeKind(head, workdir, stage) !== null);

    if (changedFiles.length === 0) {
      throw new Error('There are no browser-local changes to commit.');
    }

    for (const [filepath, head, workdir] of changedFiles) {
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

    return success(operation, 'Created a browser-local commit.', {
      commitId: oid,
      message
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

export async function push(review, personalAccessToken) {
  const operation = 'push';
  let token;

  try {
    token = requireText(personalAccessToken, 'personalAccessToken');
    const reviewed = validatePushReview(review);
    const current = await readPushState(operation);

    if (!samePushState(reviewed, current)) {
      throw new Error('The repository, checked-out branch, or local HEAD changed since review. Inspect the push again.');
    }

    const { fs, dir } = await getActiveWorkspace(operation);
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

    return success(operation, 'Pushed the reviewed commit to the matching origin branch.', {
      repositoryUrl: current.repositoryUrl,
      branch: current.branch,
      destinationRef: current.destinationRef,
      pushedCommitId: current.outgoingCommitId
    });
  } catch (error) {
    return failure(operation, 'The reviewed commit could not be pushed.', error, [token]);
  } finally {
    token = undefined;
  }
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

function canonicalizeGitHubOrigin(origin) {
  let parsed;
  try {
    parsed = new URL(origin.trim());
  } catch {
    throw new Error('The origin remote must be a valid GitHub HTTPS URL.');
  }

  const pathSegments = parsed.pathname.split('/').filter(Boolean);
  if (parsed.protocol !== 'https:'
      || parsed.hostname.toLowerCase() !== 'github.com'
      || parsed.username.length > 0
      || parsed.password.length > 0
      || parsed.search.length > 0
      || parsed.hash.length > 0
      || pathSegments.length !== 2) {
    throw new Error('The origin remote must be a credential-free GitHub HTTPS repository URL.');
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

function requireText(value, name) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${name} is required.`);
  }

  return value.trim();
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
  const normalized = path.replaceAll('\\', '/').trim();
  const withLeadingSlash = normalized.startsWith('/') ? normalized : `/${normalized}`;
  const segments = withLeadingSlash.split('/').filter(Boolean);

  if (segments.some((segment) => segment === '..')) {
    throw new Error('Repository paths cannot contain parent directory segments.');
  }

  return segments.length === 0 ? '/' : `/${segments.join('/')}`;
}

function joinRepositoryPath(root, relativePath) {
  return relativePath === '/' ? root : `${root}${relativePath}`;
}

function isEditableTextPath(name) {
  return /\.(cs|css|csv|gitignore|html|js|json|md|razor|sln|svg|txt|xml|yaml|yml)$/i.test(name)
    || /^[A-Z0-9_-]+(?:\.[A-Z0-9_-]+)?$/i.test(name);
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
