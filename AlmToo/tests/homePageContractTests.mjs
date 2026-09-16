import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const homePageUrl = new URL('../Pages/Home.razor', import.meta.url);
const homeStylesUrl = new URL('../Pages/Home.razor.css', import.meta.url);
const fileBrowserUrl = new URL('../Components/RepositoryFileBrowser.razor', import.meta.url);
const fileBrowserStylesUrl = new URL('../Components/RepositoryFileBrowser.razor.css', import.meta.url);
const commitFormUrl = new URL('../Components/RepositoryCommitForm.razor', import.meta.url);
const commitFormStylesUrl = new URL('../Components/RepositoryCommitForm.razor.css', import.meta.url);

async function readHomePage() {
  return readFile(homePageUrl, 'utf8');
}

async function readFileBrowser() {
  return readFile(fileBrowserUrl, 'utf8');
}

async function readCommitForm() {
  return readFile(commitFormUrl, 'utf8');
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

test('home page loads selected text files into a textarea editor', async () => {
  const source = await readHomePage();

  assert.match(source, /OnFileSelected="SelectFileAsync"/);
  assert.match(source, /selectedEntry\.IsEditableText/);
  assert.match(source, /GitService\.ReadTextFileAsync\(path\)/);
  assert.match(source, /selectedFileContent = result\.Value\.Content/);
  assert.match(source, /editableFileContent = result\.Value\.Content/);
  assert.match(source, /<textarea id="selected-file-content"/);
  assert.match(source, /@bind="editableFileContent"/);
  assert.match(source, /@bind:event="oninput"/);
  assert.match(source, /Plain text editor/);
  assert.match(source, /Selected file/);
  assert.match(source, /Save status/);
  assert.match(source, /Choose a text file from the browser to load the editor\./);
});

test('home page saves edited plain text through the browser Git service contract', async () => {
  const source = await readHomePage();

  assert.match(source, /@onclick="SaveSelectedFileAsync"/);
  assert.match(source, /HasUnsavedChanges/);
  assert.match(source, /GitService\.WriteTextFileAsync\(selectedFilePath, contentToSave\)/);
  assert.match(source, /selectedFileContent = contentToSave/);
  assert.match(source, /saveStatusMessage = result\.Message/);
  assert.match(source, /Choose a text file before saving changes\./);
  assert.match(source, /The selected file could not be saved\. Reopen the repository and try again\./);
  assert.match(source, /Saving\.\.\./);
  assert.match(source, /Unsaved edits/);
  assert.match(source, /No unsaved edits/);
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
  assert.match(homeStyles, /\.file-editor__textarea/);
  assert.match(homeStyles, /\.file-editor__dirty-state/);
  assert.match(homeStyles, /\.status-message\.error/);
  assert.match(fileBrowserStyles, /\.file-browser__entry--directory/);
  assert.match(fileBrowserStyles, /\.file-browser__entry--unsupported/);
  assert.match(fileBrowserStyles, /\.file-browser__entry\.is-selected/);
});

test('local commit form validates message and starter author identity', async () => {
  const source = await readCommitForm();

  assert.match(source, /@onsubmit="CreateCommitAsync"/);
  assert.match(source, /CommitMessage\.Trim\(\)/);
  assert.match(source, /AuthorName\.Trim\(\)/);
  assert.match(source, /AuthorEmail\.Trim\(\)/);
  assert.match(source, /Enter a commit message\./);
  assert.match(source, /Enter an author name\./);
  assert.match(source, /Enter a valid author email\./);
  assert.match(source, /new CommitRequest\(message, authorName, authorEmail\)/);
  assert.match(source, /AlmToo User/);
  assert.match(source, /user@almtoo\.local/);
  assert.match(source, /aria-live="polite"/);
});

test('home page invokes local commit service and refreshes status only after success', async () => {
  const source = await readHomePage();

  assert.match(source, /<RepositoryCommitForm/);
  assert.match(source, /OnCommitRequested="CreateCommitAsync"/);
  assert.match(source, /GitService\.CommitAsync\(request\)/);
  assert.match(source, /commitSucceeded = result\.Succeeded && result\.Value is not null/);
  assert.match(source, /createdCommit = commitSucceeded \? result\.Value : null/);
  assert.match(source, /if \(commitSucceeded\)\s*\{\s*await RefreshChangedFilesAsync\(\)/);
  assert.match(source, /The local commit could not be created\. Review the changed files and try again\./);
  assert.doesNotMatch(source, /result\.Diagnostic/);
});

test('local commit form exposes confirmation and responsive styling', async () => {
  const source = await readCommitForm();
  const styles = await readFile(commitFormStylesUrl, 'utf8');

  assert.match(source, /CreatedCommit\.CommitId/);
  assert.match(source, /CreatedCommit\.Message/);
  assert.match(source, /Pushing is not included/);
  assert.match(styles, /\.commit-form__identity/);
  assert.match(styles, /\.commit-form__confirmation/);
});
