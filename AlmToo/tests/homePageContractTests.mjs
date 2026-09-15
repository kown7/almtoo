import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const homePageUrl = new URL('../Pages/Home.razor', import.meta.url);
const homeStylesUrl = new URL('../Pages/Home.razor.css', import.meta.url);
const fileBrowserUrl = new URL('../Components/RepositoryFileBrowser.razor', import.meta.url);
const fileBrowserStylesUrl = new URL('../Components/RepositoryFileBrowser.razor.css', import.meta.url);

async function readHomePage() {
  return readFile(homePageUrl, 'utf8');
}

async function readFileBrowser() {
  return readFile(fileBrowserUrl, 'utf8');
}

test('home page opens repositories through the browser Git service contract', async () => {
  const source = await readHomePage();

  assert.match(source, /@inject IBrowserGitService GitService/);
  assert.match(source, /GitService\.CloneOrOpenAsync\(request\)/);
  assert.match(source, /new RepositoryOpenRequest\(\s*trimmedRepositoryUrl,\s*CreateWorkspaceName\(trimmedRepositoryUrl\)\)/);
  assert.match(source, /@onsubmit="OpenRepositoryAsync"/);
  assert.match(source, /Enter a public repository URL before opening a workspace\./);
  assert.match(source, /The repository could not be opened\. Check that the URL is public and supported\./);
  assert.doesNotMatch(source, /Diagnostic/);
});

test('home page lists repository files after a workspace opens', async () => {
  const source = await readHomePage();

  assert.match(source, /@using AlmToo\.Components/);
  assert.match(source, /<RepositoryFileBrowser/);
  assert.match(source, /Entries="repositoryEntries"/);
  assert.match(source, /CurrentPath="currentBrowserPath"/);
  assert.match(source, /OnDirectorySelected="LoadRepositoryPathAsync"/);
  assert.match(source, /await LoadRepositoryPathAsync\("\/"\)/);
  assert.match(source, /GitService\.ListFilesAsync\(normalizedPath\)/);
  assert.match(source, /NormalizeRepositoryPath\(path\)/);
  assert.match(source, /Repository files could not be loaded\. Reopen the public repository and try again\./);
});

test('home page loads selected text files and exposes current state', async () => {
  const source = await readHomePage();

  assert.match(source, /OnFileSelected="SelectFileAsync"/);
  assert.match(source, /selectedEntry\.IsEditableText/);
  assert.match(source, /GitService\.ReadTextFileAsync\(path\)/);
  assert.match(source, /selectedFileContent = result\.Value\.Content/);
  assert.match(source, /class="file-preview__content"/);
  assert.match(source, /Selected file/);
  assert.match(source, /Save status/);
  assert.match(source, /Not edited in this step/);
});

test('home page shows recoverable unsupported file states', async () => {
  const source = await readHomePage();

  assert.match(source, /The selected file is no longer visible in the current repository listing\./);
  assert.match(source, /This file is binary, oversized, or otherwise unsupported for text preview\./);
  assert.match(source, /The selected file could not be loaded as text\. Choose another file or reopen the repository\./);
  assert.match(source, /fileBrowserSucceeded = false/);
});

test('repository file browser distinguishes directories, text files, and unsupported files', async () => {
  const source = await readFileBrowser();

  assert.match(source, /@using AlmToo\.Services\.Git/);
  assert.match(source, /entry\.Kind == RepositoryFileKind\.Directory/);
  assert.match(source, /OnDirectorySelected\.InvokeAsync\(entry\.Path\)/);
  assert.match(source, /entry\.IsEditableText/);
  assert.match(source, /OnFileSelected\.InvokeAsync\(entry\.Path\)/);
  assert.match(source, /Binary or oversized file not available for text editing/);
  assert.match(source, /aria-disabled="true"/);
  assert.match(source, /FormatSize\(entry\.SizeBytes\.Value\)/);
});

test('repository browser styles expose stateful surfaces', async () => {
  const homeStyles = await readFile(homeStylesUrl, 'utf8');
  const fileBrowserStyles = await readFile(fileBrowserStylesUrl, 'utf8');

  assert.match(homeStyles, /\.repository-status/);
  assert.match(homeStyles, /\.file-preview__content/);
  assert.match(homeStyles, /\.status-message\.error/);
  assert.match(fileBrowserStyles, /\.file-browser__entry--directory/);
  assert.match(fileBrowserStyles, /\.file-browser__entry--unsupported/);
  assert.match(fileBrowserStyles, /\.file-browser__entry\.is-selected/);
});
