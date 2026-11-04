import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('Home is a landing Client and does not render repository workspace components', async () => {
  const home = await source('Pages/Home.razor');

  assert.ok(home.includes('@page "/"'));
  assert.match(home, /@inject WorkspaceManager Workspace/);
  assert.match(home, /@inject NavigationManager Navigation/);
  assert.match(home, /Open a public repository/);
  assert.match(home, /Approved browser transport/);
  assert.match(home, /@onclick="OpenWorkspaceAsync"/);
  assert.doesNotMatch(home, /RepositoryFileBrowser|RepositoryBranchSelector|RepositoryCommitHistory|RepositoryChangedFiles|RepositoryCommitForm/);
  assert.doesNotMatch(home, /LoadDirectoryAsync|SelectFileAsync|ApplySynchronizationAsync|PushReviewedCommitAsync/);
});

test('Workspace owns the complete repository editing surface and remains Manager-only', async () => {
  const workspace = await source('Pages/Workspace.razor');

  assert.match(workspace, /@page "\/workspace"/);
  assert.match(workspace, /@inject WorkspaceManager Manager/);
  assert.match(workspace, /@inject NavigationManager Navigation/);
  assert.match(workspace, /RepositoryFileBrowser/);
  assert.match(workspace, /RepositoryBranchSelector/);
  assert.match(workspace, /RepositoryCommitHistory/);
  assert.match(workspace, /RepositoryChangedFiles/);
  assert.match(workspace, /RepositoryCommitForm/);
  assert.match(workspace, /PushReviewedCommitAsync/);
  assert.match(workspace, /ApplySynchronizationAsync/);
  assert.match(workspace, /Navigation\.NavigateTo\("\/", replace: true\)/);
  assert.doesNotMatch(workspace, /IBrowserGitAccessor|IGitBugAccessor/);
});

test('navigation exposes the dedicated workspace route', async () => {
  const navigation = await source('Layout/NavMenu.razor');

  assert.match(navigation, /href="workspace"/);
  assert.match(navigation, /> Workspace/);
});

test('workspace reload recovery initializes through the Manager before route validation', async () => {
  const workspace = await source('Pages/Workspace.razor');

  const initializeIndex = workspace.indexOf('await Manager.InitializeWorkspaceAsync()');
  const routeIndex = workspace.indexOf('if (State.Route != WorkspaceRoute.Workspace)');

  assert.notEqual(initializeIndex, -1);
  assert.notEqual(routeIndex, -1);
  assert.ok(initializeIndex < routeIndex);
});

test('Home exposes only approved proxy identifiers and explicit approval actions', async () => {
  const home = await source('Pages/Home.razor');

  assert.match(home, /State\.ApprovedProxyOptions/);
  assert.match(home, /@onchange="SelectProxyAsync"/);
  assert.match(home, /@onclick="ApproveProxyAsync"/);
  assert.match(home, /State\.SelectedProxy\?\.ProxyId/);
  assert.doesNotMatch(home, /proxyUrl|proxy-url|endpointUrl|endpoint-url|<input[^>]+proxy/i);
});
