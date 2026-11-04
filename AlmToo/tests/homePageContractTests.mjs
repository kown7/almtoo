import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('composition registers one scoped browser Git Accessor and concrete workspace Manager', async () => {
  const program = await source('Program.cs');

  assert.match(program, /AddScoped<BrowserGitAccessor>\(\)/);
  assert.match(program, /AddScoped<IBrowserGitAccessor>\(services => services\.GetRequiredService<BrowserGitAccessor>\(\)\)/);
  assert.match(program, /AddScoped<WorkspaceManager>\(\)/);
  assert.doesNotMatch(program, /IRepositoryWorkspaceManager|IVersionControlAccessor|Resources/);
});

test('Home is limited to repository selection, local recovery, proxy approval, and navigation', async () => {
  const home = await source('Pages/Home.razor');

  assert.ok(home.includes('@page "/"'));
  assert.match(home, /@inject WorkspaceManager Workspace/);
  assert.match(home, /@inject NavigationManager Navigation/);
  assert.match(home, /OpenRepositoryAsync/);
  assert.match(home, /OpenExistingRepositoryAsync/);
  assert.match(home, /InitializeWorkspaceAsync/);
  assert.match(home, /State\.AvailableRepositories/);
  assert.match(home, /State\.ApprovedProxyOptions/);
  assert.match(home, /SelectProxyAsync/);
  assert.match(home, /ApproveProxyAsync/);
  assert.match(home, /NavigateToWorkspaceAsync/);
  assert.doesNotMatch(home, /RepositoryFileBrowser|RepositoryBranchSelector|RepositoryCommitHistory|RepositoryChangedFiles|RepositoryCommitForm/);
  assert.doesNotMatch(home, /LoadDirectoryAsync|SelectFileAsync|ApplySynchronizationAsync|PushReviewedCommitAsync/);
});

test('Home exposes stable proxy identifiers without accepting arbitrary endpoint input', async () => {
  const home = await source('Pages/Home.razor');

  assert.match(home, /@onchange="SelectProxyAsync"/);
  assert.match(home, /@onclick="ApproveProxyAsync"/);
  assert.match(home, /State\.SelectedProxy\?\.ProxyId/);
  assert.match(home, /Approved connection/);
  assert.doesNotMatch(home, /proxyUrl|proxy-url|endpointUrl|endpoint-url|<input[^>]+proxy/i);
});

test('Home keeps route blocking and approval feedback in Manager-owned state', async () => {
  const home = await source('Pages/Home.razor');

  assert.match(home, /State\.RouteBlockReason/);
  assert.match(home, /WorkspaceRouteBlockReason\.ProxySelectionRequired/);
  assert.match(home, /WorkspaceRouteBlockReason\.ProxyApprovalRequired/);
  assert.match(home, /data-proxy-approval="approved"/);
  assert.match(home, /disabled="@\(State\.IsBusy \|\| State\.RouteBlockReason/);
});

test('Home recovers local workspace state through the Manager on first render', async () => {
  const home = await source('Pages/Home.razor');
  const initIndex = home.indexOf('await Workspace.InitializeWorkspaceAsync()');
  const managerIndex = home.indexOf('@inject WorkspaceManager Workspace');

  assert.notEqual(initIndex, -1);
  assert.ok(managerIndex < initIndex);
});
