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
  assert.match(source, /if \(commitSucceeded\)\s*\{\s*InvalidatePushReview\(\);\s*await RefreshChangedFilesAsync\(\)/);
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

test('push review visibly presents the exact GitHub destination and outgoing commit', async () => {
  const source = await readHomePage();

  assert.match(source, /GitService\.InspectPushAsync\(\)/);
  assert.match(source, /GitHub origin/);
  assert.match(source, /data-push-review="origin">@pushReview\.RepositoryUrl/);
  assert.match(source, /data-push-review="branch">@pushReview\.Branch/);
  assert.match(source, /data-push-review="sha">@pushReview\.OutgoingCommitId/);
  assert.match(source, /new PushRequest\(\s*pushReview\.RepositoryUrl,\s*pushReview\.Branch,\s*pushReview\.OutgoingCommitId,\s*pushReview\.DestinationRef\)/);
  assert.match(source, /InvalidatePushReview\(\);\s*await RefreshChangedFilesAsync\(\)/);
  assert.match(source, /private void InvalidatePushReview\(\)[\s\S]*pushReview = null;[\s\S]*pushConfirmed = false;/);
});

test('push form masks credential entry and requires stored presence plus explicit confirmation', async () => {
  const source = await readHomePage();

  assert.match(source, /id="github-pat"[\s\S]*type="password"[\s\S]*value="@credentialInput"/);
  assert.match(source, /autocomplete="off"/);
  assert.match(source, /Use token in this tab/);
  assert.match(source, /id="confirm-push"[\s\S]*type="checkbox"[\s\S]*@bind="pushConfirmed"/);
  assert.match(source, /disabled="@\(!CanPush\)"/);
  assert.match(source, /private bool CanPush => pushReview is not null\s*&& pushConfirmed\s*&& hasStoredCredential[\s\S]*&& !isPushing;/);
  assert.match(source, /if \(isPushing \|\| pushReview is null \|\| !pushConfirmed \|\| !hasStoredCredential\)\s*\{\s*return;/);
  assert.match(source, /InvokeAsync<string\?>\("getCredentialForPush"\)/);
  assert.match(source, /GitService\.PushAsync\(reviewedPush, tokenForAttempt\)/);
  assert.match(source, /finally\s*\{\s*tokenForAttempt = null;\s*pushConfirmed = false;\s*isPushing = false;/);
});

test('home page restores presence only and supports explicit or rejection-driven forgetting', async () => {
  const source = await readHomePage();
  const initialization = source.match(/protected override async Task OnAfterRenderAsync[\s\S]*?private async Task<IJSObjectReference>/)?.[0] ?? '';
  const pushMethod = source.match(/private async Task PushReviewedCommitAsync\(\)[\s\S]*?\n    private void InvalidatePushReview/)?.[0] ?? '';

  assert.match(source, /CredentialModulePath = "\.\/js\/pushCredentialSession\.js"/);
  assert.match(initialization, /InvokeAsync<bool>\("hasCredential"\)/);
  assert.doesNotMatch(initialization, /getCredentialForPush/);
  assert.match(source, /credentialInput = string\.Empty;[\s\S]*InvokeAsync<bool>\("storeCredential", credentialForStorage\)/);
  assert.match(source, /@onclick="ForgetCredentialAsync"/);
  assert.match(source, /InvokeAsync<bool>\("forgetCredential"\)/);
  assert.match(source, /data-credential-state="present"/);
  assert.match(source, /credentialReplacementRequired \? "replacement-required"/);
  assert.match(pushMethod, /result\.FailureKind == GitOperationFailureKind\.CredentialRejected/);
  assert.match(pushMethod, /ClearCredentialAsync\(replacementRequired: true\)/);
  assert.doesNotMatch(pushMethod, /else[\s\S]*ClearCredentialAsync\(replacementRequired: false\)/);
  assert.doesNotMatch(source, /sessionStorage/);
});

test('push outcomes remain credential-safe, preserve local work, and refresh status only on success', async () => {
  const source = await readHomePage();
  const pushMethod = source.match(/private async Task PushReviewedCommitAsync\(\)[\s\S]*?\n    private void InvalidatePushReview/)?.[0] ?? '';

  assert.match(pushMethod, /pushSucceeded = result\.Succeeded && result\.Value is not null/);
  assert.match(pushMethod, /Pushed commit \{result\.Value!\.PushedCommitId\}/);
  assert.match(pushMethod, /if \(pushSucceeded\)[\s\S]*await RefreshChangedFilesAsync\(\)/);
  assert.match(pushMethod, /The push could not be completed\. The local commit remains available; review the destination and try again\./);
  assert.doesNotMatch(pushMethod, /createdCommit\s*=/);
  assert.doesNotMatch(source, /pushStatusMessage\s*=\s*\$"[^"]*(?:personalAccessToken|tokenForAttempt)/);
  assert.doesNotMatch(source, /@tokenForAttempt/);
  assert.doesNotMatch(source, /credentialStatusMessage\s*=\s*\$"/);
  assert.doesNotMatch(source, /result\.Diagnostic/);
});

test('push review has dedicated responsive and readable styles', async () => {
  const styles = await readFile(homeStylesUrl, 'utf8');

  assert.match(styles, /\.push-panel/);
  assert.match(styles, /\.push-review__sha/);
  assert.match(styles, /overflow-wrap: anywhere/);
  assert.match(styles, /\.push-confirmation/);
  assert.match(styles, /\.secondary-action:disabled/);
});
