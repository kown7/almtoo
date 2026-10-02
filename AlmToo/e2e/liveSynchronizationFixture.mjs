const requiredConfiguration = Object.freeze([
  'ALMTOO_LIVE_GITHUB_OWNER',
  'ALMTOO_LIVE_GITHUB_REPOSITORY',
  'ALMTOO_LIVE_GITHUB_BRANCH',
  'ALMTOO_LIVE_GITHUB_PAT',
  'ALMTOO_LIVE_GITHUB_FILE'
]);

const githubApiBase = 'https://api.github.com';

/**
 * Playwright-only live-fixture support. It deliberately owns no application
 * behavior and exposes only opaque run identifiers and SHA-independent facts.
 */
export function readLiveConfiguration(environment = process.env) {
  const missing = requiredConfiguration.filter(key => typeof environment[key] !== 'string' || environment[key].trim().length === 0);
  if (missing.length > 0) {
    throw new Error(`Live GitHub fixture is not configured; set ${missing.join(', ')}.`);
  }

  const owner = environment.ALMTOO_LIVE_GITHUB_OWNER.trim();
  const repository = environment.ALMTOO_LIVE_GITHUB_REPOSITORY.trim();
  const branch = environment.ALMTOO_LIVE_GITHUB_BRANCH.trim();
  const fixtureFile = environment.ALMTOO_LIVE_GITHUB_FILE.trim().replace(/^\/+/, '');
  const token = environment.ALMTOO_LIVE_GITHUB_PAT.trim();
  if (!/^[A-Za-z0-9_.-]+$/.test(owner) || !/^[A-Za-z0-9_.-]+$/.test(repository)
      || branch !== 'main' || !/^[A-Za-z0-9_.-]+$/.test(fixtureFile) || fixtureFile.includes('..')) {
    throw new Error('Live GitHub fixture configuration is invalid.');
  }

  return {
    owner,
    repository,
    branch,
    fixtureFile,
    token,
    repositoryUrl: `https://github.com/${owner}/${repository}.git`
  };
}

export function liveConfigurationError(environment = process.env) {
  try {
    readLiveConfiguration(environment);
    return null;
  } catch (error) {
    return error instanceof Error ? error.message : 'Live GitHub fixture is not configured.';
  }
}

function apiHeaders(token) {
  return {
    accept: 'application/vnd.github+json',
    authorization: `Bearer ${token}`,
    'x-github-api-version': '2022-11-28',
    'content-type': 'application/json'
  };
}

async function api(configuration, path, options = {}) {
  const response = await fetch(`${githubApiBase}${path}`, {
    ...options,
    headers: { ...apiHeaders(configuration.token), ...(options.headers ?? {}) }
  });
  if (!response.ok) {
    throw new Error(`GitHub fixture API request failed (${response.status}).`);
  }
  return response.status === 204 ? null : response.json();
}

async function branchTip(configuration) {
  const ref = await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/ref/heads/${encodeURIComponent(configuration.branch)}`);
  return ref.object.sha;
}

async function commitFor(configuration, sha) {
  return api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/commits/${sha}`);
}

async function createBlob(configuration, content) {
  const blob = await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/blobs`, {
    method: 'POST',
    body: JSON.stringify({ content, encoding: 'utf-8' })
  });
  return blob.sha;
}

async function commitTree(configuration, parentSha, changes, message) {
  const parent = await commitFor(configuration, parentSha);
  const tree = await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/trees`, {
    method: 'POST',
    body: JSON.stringify({
      base_tree: parent.tree.sha,
      tree: changes.map(change => ({
        path: change.path,
        mode: '100644',
        type: 'blob',
        sha: change.sha
      }))
    })
  });
  const commit = await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/commits`, {
    method: 'POST',
    body: JSON.stringify({ message, tree: tree.sha, parents: [parentSha] })
  });
  await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/refs/heads/${encodeURIComponent(configuration.branch)}`, {
    method: 'PATCH',
    body: JSON.stringify({ sha: commit.sha, force: false })
  });
  return commit.sha;
}

export async function prepareLiveFixture(configuration) {
  // Confirm the configured path exists before creating a review that calls it "modified".
  const configuredFile = await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/contents/${configuration.fixtureFile}?ref=${encodeURIComponent(configuration.branch)}`);
  if (configuredFile.type !== 'file') {
    throw new Error('Live GitHub fixture file is not a file.');
  }

  const runId = `live-${Date.now().toString(36)}-${crypto.randomUUID().slice(0, 8)}`;
  const originalSha = await branchTip(configuration);
  const deletedPath = `${configuration.fixtureFile}.${runId}.delete.txt`;
  const addedPath = `${configuration.fixtureFile}.${runId}.add.txt`;
  const baselineBlob = await createBlob(configuration, `fixture baseline ${runId}\n`);
  const baselineSha = await commitTree(configuration, originalSha, [{ path: deletedPath, sha: baselineBlob }], `AlmToo live fixture baseline ${runId}`);
  let advancedSha = null;

  return {
    runId,
    originalSha,
    baselineSha,
    addedPath: `/${addedPath}`,
    modifiedPath: `/${configuration.fixtureFile}`,
    deletedPath: `/${deletedPath}`,
    async advance() {
      if (advancedSha) return advancedSha;
      const modifiedBlob = await createBlob(configuration, `AlmToo live fixture modified ${runId}\n`);
      const addedBlob = await createBlob(configuration, `AlmToo live fixture added ${runId}\n`);
      advancedSha = await commitTree(configuration, baselineSha, [
        { path: configuration.fixtureFile, sha: modifiedBlob },
        { path: addedPath, sha: addedBlob },
        { path: deletedPath, sha: null }
      ], `AlmToo live fixture advance ${runId}`);
      return advancedSha;
    },
    cleanup: () => restoreLiveFixture(configuration, originalSha, [baselineSha, advancedSha].filter(Boolean))
  };
}

export async function restoreLiveFixture(configuration, originalSha, expectedFixtureHeads) {
  const currentSha = await branchTip(configuration);
  if (currentSha === originalSha) return;
  if (!Array.isArray(expectedFixtureHeads) || !expectedFixtureHeads.includes(currentSha)) {
    throw new Error('Live fixture cleanup will not replace an unexpected remote branch head.');
  }
  await api(configuration, `/repos/${configuration.owner}/${configuration.repository}/git/refs/heads/${encodeURIComponent(configuration.branch)}`, {
    method: 'PATCH',
    body: JSON.stringify({ sha: originalSha, force: true })
  });
}
