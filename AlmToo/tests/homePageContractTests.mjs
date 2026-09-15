import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const homePageUrl = new URL('../Pages/Home.razor', import.meta.url);
const homeStylesUrl = new URL('../Pages/Home.razor.css', import.meta.url);

async function readHomePage() {
  return readFile(homePageUrl, 'utf8');
}

test('home page opens repositories through the browser Git service contract', async () => {
  const source = await readHomePage();

  assert.match(source, /@inject IBrowserGitService GitService/);
  assert.match(source, /GitService\.CloneOrOpenAsync\(request\)/);
  assert.match(source, /new RepositoryOpenRequest\(\s*trimmedRepositoryUrl,\s*CreateWorkspaceName\(trimmedRepositoryUrl\)\)/);
  assert.match(source, /@onsubmit="OpenRepositoryAsync"/);
  assert.match(source, /@onsubmit:preventDefault="true"/);
});

test('home page exposes loading, success, and quiet failure states', async () => {
  const source = await readHomePage();

  assert.match(source, /Opening repository\.\.\./);
  assert.match(source, /Contacting the Git host and preparing browser storage/);
  assert.match(source, /Repository cloned into browser-local storage/);
  assert.match(source, /Opened the existing browser-local repository/);
  assert.match(source, /This repository could not be opened\. Confirm it is public and try again\./);
  assert.match(source, /catch \(Exception\)/);
  assert.doesNotMatch(source, /@result\.Diagnostic|@.*Diagnostic/);
});

test('home page validates empty input and displays repository state after open', async () => {
  const source = await readHomePage();

  assert.match(source, /string\.IsNullOrWhiteSpace\(trimmedRepositoryUrl\)/);
  assert.match(source, /Enter a public repository URL before opening a workspace/);
  assert.match(source, /Current repository state/);
  assert.match(source, /@currentRepository\.RepositoryUrl/);
  assert.match(source, /@currentRepository\.WorkspaceName/);
  assert.match(source, /@currentRepository\.RootPath/);
});

test('home page styles include visible state treatment for info, success, and warning messages', async () => {
  const styles = await readFile(homeStylesUrl, 'utf8');

  assert.match(styles, /\.status-message\.info/);
  assert.match(styles, /\.status-message\.success/);
  assert.match(styles, /\.status-message\.warning/);
  assert.match(styles, /\.repository-status/);
});
