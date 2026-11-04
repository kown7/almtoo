import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('landing and workspace Clients depend on the concrete Manager only', async () => {
  const [home, workspace, navigation] = await Promise.all([
    source('Pages/Home.razor'),
    source('Pages/Workspace.razor'),
    source('Layout/NavMenu.razor')
  ]);

  assert.match(home, new RegExp('@page "/"'));
  assert.match(home, /@inject WorkspaceManager Workspace/);
  assert.doesNotMatch(home, /IBrowserGitAccessor|BrowserGitAccessor|IGitBugAccessor|IJSRuntime|browserGitEngine/);

  assert.match(workspace, /@page "\/workspace"/);
  assert.match(workspace, /@inject WorkspaceManager Manager/);
  assert.doesNotMatch(workspace, /IBrowserGitAccessor|BrowserGitAccessor|IGitBugAccessor|IJSRuntime|browserGitEngine/);

  assert.match(navigation, /href="workspace"/);
});

test('Clients expose proxy identifiers and approvals without accepting endpoint configuration', async () => {
  const [home, workspace] = await Promise.all([
    source('Pages/Home.razor'),
    source('Pages/Workspace.razor')
  ]);
  const clientSurface = `${home}\n${workspace}`;

  assert.match(clientSurface, /ApprovedProxyOptions/);
  assert.match(clientSurface, /SelectProxyAsync/);
  assert.match(clientSurface, /ApproveProxyAsync/);
  assert.doesNotMatch(clientSurface, /proxyUrl|proxy-url|endpointUrl|endpoint-url|corsProxy/i);
  assert.doesNotMatch(clientSurface, /<input[^>]+(?:proxy|endpoint)/i);
});
