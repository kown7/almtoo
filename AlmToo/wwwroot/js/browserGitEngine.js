const vendorScripts = [
  './js/vendor/lightning-fs/lightning-fs.min.js',
  './js/vendor/isomorphic-git/index.umd.min.js',
  './js/vendor/isomorphic-git/http-web.umd.js'
];

const workspaceRoot = '/almtoo-workspaces';
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

async function ensureDependencies() {
  if (!dependenciesPromise) {
    dependenciesPromise = loadVendorScripts();
  }

  await dependenciesPromise;

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

function failure(operation, message, error) {
  return {
    operation,
    succeeded: false,
    message,
    value: null,
    diagnostic: getDiagnostic(error)
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
